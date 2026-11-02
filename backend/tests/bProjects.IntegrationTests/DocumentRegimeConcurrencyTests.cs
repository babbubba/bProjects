using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T048 (PG) — campagna integrata di concorrenza e deadline B1: guard dell'autorità temporale (T118), CAS atomica di promozione (T047) e sweep/monitor
/// (T119) sugli stessi root, con due worker, fence/lease scaduti, retry e restart. PostgreSQL reale; nessun DGX, nessun MAF.
/// <para>
/// Clock: il clock del database è <b>congelato</b> nel database di test. Uno schema <c>tclock</c> con una funzione <c>clock_timestamp()</c> che
/// legge un istante fissato precede <c>pg_catalog</c> nel <c>search_path</c> delle sole connessioni del test; tutto il codice di produzione (guard,
/// CAS, sweep, claim, store) esegue quindi SQL invariato ma legge un tempo esatto: 119,999 / 120 / 179,999 / 180 / 3600 s dal t0 sono istanti
/// esatti, non approssimazioni di millisecondi del clock reale, e si può avanzare il clock mentre un worker attende il lock di riga per provare
/// che lo legge dopo il lock.
/// </para>
/// Scenari A–G del QS §B1 come regole di controllo: A nominale a 150 s; B oltre 180 s; C solo coda a 120 s; D piano multipasso (Extended
/// iniziale); E promozione contro completamento/scadenza; F retry dopo promozione a 110 s e restart; G copertura incompleta/limiti.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentRegimeConcurrencyTests
{
    private const string Lease = "worker-a";
    private const long Fence = 3;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);
    private static readonly BatchRef AnyBatch = new(0, "b1", new string('1', 64));

    private static readonly double[] OrdinaryInstants = [119.999, 120, 179.999, 180, 3600];
    private static readonly double[] ExtendedInstants = [179.999, 180, 3599.999, 3600];

    public static TheoryData<RootKind, double> OrdinaryMatrix => Matrix(OrdinaryInstants);

    public static TheoryData<RootKind, double, bool> ExtendedMatrix
    {
        get
        {
            var data = new TheoryData<RootKind, double, bool>();
            foreach (var kind in new[] { RootKind.Turn, RootKind.Run })
            {
                foreach (var promoted in new[] { false, true })
                {
                    foreach (var seconds in ExtendedInstants)
                    {
                        data.Add(kind, seconds, promoted);
                    }
                }
            }

            return data;
        }
    }

    private static TheoryData<RootKind, double> Matrix(double[] instants)
    {
        var data = new TheoryData<RootKind, double>();
        foreach (var kind in new[] { RootKind.Turn, RootKind.Run })
        {
            foreach (var seconds in instants)
            {
                data.Add(kind, seconds);
            }
        }

        return data;
    }

    // =====================================================================================================================================
    // 1. Matrice degli istanti: guard, riserva, rinnovo, commit e sweep sullo stesso instante esatto
    // =====================================================================================================================================

    [Fact]
    public void Discovery_of_the_campaign_is_not_empty()
    {
        var tests = typeof(DocumentRegimeConcurrencyTests).GetMethods()
            .Count(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0);
        Assert.True(tests >= 20, $"La campagna T048 deve avere test propri; trovati {tests}.");
        Assert.Equal(10, OrdinaryMatrix.Count());     // 2 tipi di root × 5 istanti (119,999 / 120 / 179,999 / 180 / 3600)
        Assert.Equal(16, ExtendedMatrix.Count());     // 2 tipi × (iniziale Extended | promosso) × 4 istanti
    }

    [Theory]
    [MemberData(nameof(OrdinaryMatrix))]
    public async Task An_ordinary_root_is_operable_up_to_but_excluding_180_s_and_nothing_promotes_it_by_time(RootKind kind, double seconds)
    {
        await using var rig = await Rig.CreateAsync();
        var root = await rig.SeedAsync(kind, new RootSpec { CallCount = 1 });
        await rig.AtAsync(root, seconds);
        var expectedNow = (await rig.T0Async(root)).AddMilliseconds(Math.Round(seconds * 1000));
        var alive = seconds < 180;

        var authority = await rig.AuthorityAsync(root);
        Assert.Equal(expectedNow, authority.Now);   // il clock letto sotto lock è quello congelato, non il clock del processo
        Assert.Equal(alive, authority.Allowed);
        if (alive)
        {
            Assert.Equal(180 - seconds, authority.Remaining.TotalSeconds, 3);
        }
        else
        {
            Assert.Equal((DeadlineDenial.DeadlineExceeded, TimeSpan.Zero), (authority.Denial, authority.Remaining));
        }

        Assert.Equal(alive ? RootCommitState.Valid : RootCommitState.Stale, await rig.CommitStateAsync(root));
        Assert.Equal(alive ? "reserved" : "fenced", await rig.ReserveAsync(root));
        Assert.Equal(alive, await rig.RenewAsync(root));
        Assert.Equal(alive ? 2 : 1, await rig.CallCountAsync(root));

        // Nessuna soglia temporale promuove: né il tempo, né la coda, né il monitor.
        var closed = await rig.SweepAsync();
        Assert.Equal(0L, await rig.TransitionsAsync(root));
        if (alive)
        {
            Assert.False(closed.Any);
            Assert.Equal(("Running", "Ordinary", 0), await rig.StatusRegimeAsync(root));
        }
        else
        {
            Assert.Contains(root.Id, closed.Turns.Concat(closed.Runs));
            Assert.Equal(("Failed", "Ordinary", 0), await rig.StatusRegimeAsync(root));
            Assert.Equal("deadline_exceeded", await rig.QueryAsync<string>($"SELECT \"ErrorCode\" FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'"));
            Assert.Equal(Fence + 1, await rig.QueryAsync<long>($"SELECT \"FencingToken\" FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'"));
            Assert.Equal(RootCommitState.Stale, await rig.CommitStateAsync(root));   // il vecchio possessore non committa più
            Assert.False(await rig.RenewAsync(root));
        }
    }

    [Theory]
    [MemberData(nameof(ExtendedMatrix))]
    public async Task An_extended_or_promoted_root_keeps_t0_plus_3600_and_an_old_claim_at_180_does_not_close_it(RootKind kind, double seconds, bool promoted)
    {
        await using var rig = await Rig.CreateAsync();
        var spec = promoted
            ? new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Ordinary, PromotionCount = 1, CallCount = 1 }
            : new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, CallCount = 1 };
        var root = await rig.SeedAsync(kind, spec);
        await rig.AtAsync(root, seconds);
        var alive = seconds < 3600;

        var authority = await rig.AuthorityAsync(root);
        Assert.Equal(alive, authority.Allowed);
        if (alive)
        {
            Assert.Equal(3600 - seconds, authority.Remaining.TotalSeconds, 3);   // il tetto è t0+3600, mai 3600 s da un cambio
        }

        Assert.Equal(alive ? RootCommitState.Valid : RootCommitState.Stale, await rig.CommitStateAsync(root));
        Assert.Equal(alive ? "reserved" : "fenced", await rig.ReserveAsync(root));
        Assert.Equal(alive, await rig.RenewAsync(root));   // il claim è del vecchio possessore (Ordinary a 180 s): non è autorità

        var closed = await rig.SweepAsync();
        Assert.Equal(!alive, closed.Any);
        Assert.Equal(alive ? "Running" : "Failed", (await rig.StatusRegimeAsync(root)).Status);
        Assert.Equal(0L, await rig.TransitionsAsync(root));   // un Extended non si «promuove» e nessuno sweep scrive una transizione
    }

    // =====================================================================================================================================
    // 2. Promozione agli istanti esatti (CAS T047 con guard T118) e interazione con lo sweep T119
    // =====================================================================================================================================

    [Theory]
    [MemberData(nameof(OrdinaryMatrix))]
    public async Task A_valid_promotion_exists_only_before_the_180_s_deadline_and_keeps_t0_plus_3600(RootKind kind, double seconds)
    {
        await using var rig = await Rig.CreateAsync();
        var s = await rig.NewScenarioAsync(kind);
        await rig.AtAsync(s.Root, seconds);
        var t0 = await rig.T0Async(s.Root);
        var snapshot = await rig.StateAsync(s.Root);
        var counts = await rig.CountsAsync();

        var result = await rig.PromoteAsync(s);

        if (seconds < 180)
        {
            Assert.Equal(PromotionOutcome.Promoted, result.Outcome);
            Assert.Equal(t0.AddSeconds(3600), result.EffectiveDeadlineAt);
            Assert.Equal(t0.AddMilliseconds(Math.Round(seconds * 1000)), result.At);
            Assert.Equal((long)Math.Round(seconds * 1000), await rig.QueryAsync<long>($"SELECT \"ElapsedMs\" FROM \"RegimeTransitions\" WHERE \"RootId\" = '{s.Root.Id}'"));
            Assert.Equal(1L, await rig.TransitionsAsync(s.Root));
            Assert.Equal(("Running", "Extended", 1), await rig.StatusRegimeAsync(s.Root));
            Assert.Equal(1L, await rig.QueryAsync<long>($"SELECT count(*) FROM \"{s.Root.Table}\" WHERE \"Id\" = '{s.Root.Id}' AND \"DeadlineAt\" = \"EffectiveDeadlineAt\" AND \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" AND \"HardDeadlineAt\" = \"RequestedAt\" + interval '3600 seconds'"));

            // t0+3600 preservato: 3600 − seconds residui, mai 3600 s dal cambio; né lo sweep a 180 né a 3599,999 lo chiudono; a 3600 sì.
            Assert.Equal(3600 - seconds, (await rig.AuthorityAsync(s.Root)).Remaining.TotalSeconds, 3);
            foreach (var later in new[] { 180d, 3599.999 })
            {
                await rig.AtAsync(s.Root, later);
                Assert.False((await rig.SweepAsync()).Any);
                Assert.True((await rig.AuthorityAsync(s.Root)).Allowed);
            }

            await rig.AtAsync(s.Root, 3600);
            var closed = await rig.SweepAsync();
            Assert.Contains(s.Root.Id, closed.Turns.Concat(closed.Runs));
            Assert.Equal("Failed", (await rig.StatusRegimeAsync(s.Root)).Status);
            Assert.Equal(1L, await rig.TransitionsAsync(s.Root));   // lo sweep non crea né modifica transizioni
        }
        else
        {
            // A scadenza esatta prevale l'errore: né promozione tardiva né ricevuta scritta.
            Assert.Equal(PromotionOutcome.DeadlineExceeded, result.Outcome);
            Assert.Equal(snapshot, await rig.StateAsync(s.Root));
            Assert.Equal(counts, await rig.CountsAsync());
            await rig.SweepAsync();
            Assert.Equal(PromotionOutcome.Terminal, (await rig.PromoteAsync(s)).Outcome);   // dopo la chiusura nessuna riapertura
            Assert.Equal(0L, await rig.TransitionsAsync(s.Root));
        }
    }

    [Fact]
    public async Task Two_current_and_stale_workers_race_for_the_promotion_and_exactly_one_transition_exists()
    {
        await using var rig = await Rig.CreateAsync();
        var s = await rig.NewScenarioAsync(RootKind.Run, seconds: 110);
        await rig.AtAsync(s.Root, 110);

        var attempts = new List<Func<Task<PromotionResult>>>();
        for (var i = 0; i < 4; i++)
        {
            attempts.Add(() => rig.PromoteAsync(s));                                  // il possessore corrente (più thread)
            attempts.Add(() => rig.PromoteAsync(s, lease: Lease, fence: Fence - 1));  // fence scaduto
            attempts.Add(() => rig.PromoteAsync(s, lease: "worker-b"));               // altro worker, stesso fence
        }

        var results = await Task.WhenAll(attempts.Select(a => Task.Run(a, Ct)));

        Assert.Equal(1, results.Count(r => r.Outcome == PromotionOutcome.Promoted));
        Assert.Equal(3, results.Count(r => r.Outcome == PromotionOutcome.AlreadyPromoted));
        Assert.Equal(8, results.Count(r => r.Outcome == PromotionOutcome.Fenced));
        Assert.Equal(1L, await rig.TransitionsAsync(s.Root));
        Assert.Equal(1L, await rig.QueryAsync<long>("SELECT count(*) FROM \"DerivedBatches\" WHERE \"BatchId\" = 'b2'"));
        Assert.Equal(("Running", "Extended", 1), await rig.StatusRegimeAsync(s.Root));
    }

    [Fact]
    public async Task The_promotion_reads_the_clock_after_it_obtains_the_row_lock()
    {
        await using var rig = await Rig.CreateAsync();

        // A 179,999 s la promozione entra in attesa sul lock del root; mentre attende, il clock raggiunge i 180 s esatti. Deve vincere l'errore.
        var late = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(late.Root, 179.999);
        var lateResult = (PromotionResult)(await WhileLockedAsync<object?>(
            rig, late.Root, blocked: 1, whileBlocked: () => rig.AtAsync(late.Root, 180), commit: false, async () => await rig.PromoteAsync(late)))[0]!;
        Assert.Equal(PromotionOutcome.DeadlineExceeded, lateResult.Outcome);
        Assert.Equal(0L, await rig.TransitionsAsync(late.Root));
        Assert.Equal(("Running", "Ordinary", 0), await rig.StatusRegimeAsync(late.Root));

        // Stessa attesa senza avanzare il clock: la promozione, ancora in tempo, vince.
        var early = await rig.NewScenarioAsync(RootKind.Run);
        await rig.AtAsync(early.Root, 179.999);
        var earlyResult = (PromotionResult)(await WhileLockedAsync<object?>(
            rig, early.Root, blocked: 1, whileBlocked: null, commit: false, async () => await rig.PromoteAsync(early)))[0]!;
        Assert.Equal(PromotionOutcome.Promoted, earlyResult.Outcome);
        Assert.Equal(1L, await rig.TransitionsAsync(early.Root));
    }

    [Fact]
    public async Task A_guard_that_waits_for_the_lock_evaluates_the_deadline_after_the_wait()
    {
        await using var rig = await Rig.CreateAsync();
        var root = await rig.SeedAsync(RootKind.Turn, new RootSpec { CallCount = 1 });
        await rig.AtAsync(root, 179.999);

        // Il worker B (riserva di una chiamata, che prende il lock fenced dell'owner) attende un lock di riga tenuto da altri senza modificarla.
        // Mentre attende, la scadenza esatta passa: la riserva non deve riuscire (nessun commit dopo la scadenza).
        var outcome = (string)(await WhileLockedAsync<object?>(
            rig, root, blocked: 1, whileBlocked: () => rig.AtAsync(root, 180), commit: false, async () => await rig.ReserveAsync(root)))[0]!;

        Assert.Equal("fenced", outcome);
        Assert.Equal(1, await rig.CallCountAsync(root));
    }

    [Fact]
    public async Task Sweep_and_promotion_straddling_the_ordinary_deadline_give_a_single_terminal_or_a_single_transition()
    {
        await using var rig = await Rig.CreateAsync();

        // (a) Promozione valida committata a 179,999: a 180 lo sweep legge la deadline aggiornata e non chiude.
        var promoted = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(promoted.Root, 179.999);
        Assert.False((await rig.SweepAsync()).Any);
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(promoted)).Outcome);
        await rig.AtAsync(promoted.Root, 180);
        Assert.False((await rig.SweepAsync()).Any);
        Assert.True((await rig.AuthorityAsync(promoted.Root)).Allowed);
        Assert.True(await rig.RenewAsync(promoted.Root));   // il vecchio claim prosegue sulla nuova deadline

        // (b) Sweep e promozione in gara a 180 s esatti, entrambi in attesa del lock: un solo terminale, nessuna transizione, nessun doppio fence.
        var expired = await rig.NewScenarioAsync(RootKind.Run);
        await rig.AtAsync(expired.Root, 180);
        var results = await WhileLockedAsync<object?>(
            rig, expired.Root, blocked: 2, whileBlocked: null, commit: false,
            async () => await rig.PromoteAsync(expired), async () => await rig.SweepAsync());
        var promotion = (PromotionResult)results[0]!;
        Assert.Contains(promotion.Outcome, new[] { PromotionOutcome.DeadlineExceeded, PromotionOutcome.Terminal });
        Assert.Equal(("Failed", "Ordinary", 0), await rig.StatusRegimeAsync(expired.Root));
        Assert.Equal(0L, await rig.TransitionsAsync(expired.Root));
        Assert.Equal(Fence + 1, await rig.QueryAsync<long>($"SELECT \"FencingToken\" FROM \"ProcessingRuns\" WHERE \"Id\" = '{expired.Root.Id}'"));
    }

    [Fact]
    public async Task Two_sweeps_race_at_180_and_close_the_root_once_with_a_single_fence_increment()
    {
        await using var rig = await Rig.CreateAsync();
        var turn = await rig.SeedAsync(RootKind.Turn);
        var run = await rig.SeedAsync(RootKind.Run);
        await rig.AlignAsync(turn, run);
        await rig.AtAsync(turn, 180);

        var sweeps = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => rig.SweepAsync(), Ct)));

        Assert.Equal(1, sweeps.Sum(s => s.Turns.Count(id => id == turn.Id)));
        Assert.Equal(1, sweeps.Sum(s => s.Runs.Count(id => id == run.Id)));
        Assert.Equal(Fence + 1, await rig.QueryAsync<long>($"SELECT \"FencingToken\" FROM \"ChatTurns\" WHERE \"Id\" = '{turn.Id}'"));
        Assert.Equal(Fence + 1, await rig.QueryAsync<long>($"SELECT \"FencingToken\" FROM \"ProcessingRuns\" WHERE \"Id\" = '{run.Id}'"));
    }

    // =====================================================================================================================================
    // 3. E — promozione contro completamento; fence e lease scaduti
    // =====================================================================================================================================

    [Fact]
    public async Task E_a_terminal_commit_that_precedes_the_promotion_wins_and_a_valid_promotion_that_precedes_it_extends_the_commit_deadline()
    {
        await using var rig = await Rig.CreateAsync();

        // Commit finale prima: il root è terminale, nessuna transizione.
        var committed = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(committed.Root, 110);
        Assert.True(await rig.CompleteAsync(committed.Root));
        Assert.Equal(PromotionOutcome.Terminal, (await rig.PromoteAsync(committed)).Outcome);
        Assert.Equal(0L, await rig.TransitionsAsync(committed.Root));
        Assert.Equal(("Completed", "Ordinary", 0), await rig.StatusRegimeAsync(committed.Root));

        // Promozione prima: il commit a 200 s legge la deadline aggiornata (a 180 s un Ordinary non promosso non potrebbe più committare).
        var promoted = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(promoted.Root, 110);
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(promoted)).Outcome);
        await rig.AtAsync(promoted.Root, 135);
        Assert.True(await rig.RenewAsync(promoted.Root));
        await rig.AtAsync(promoted.Root, 160);
        Assert.True(await rig.RenewAsync(promoted.Root));
        await rig.AtAsync(promoted.Root, 185);
        Assert.True(await rig.CompleteAsync(promoted.Root));
        Assert.Equal(("Completed", "Extended", 1), await rig.StatusRegimeAsync(promoted.Root));
        Assert.Equal(1L, await rig.TransitionsAsync(promoted.Root));
    }

    [Fact]
    public async Task E_promotion_and_final_commit_in_a_lock_race_leave_one_terminal_and_either_zero_or_one_transition()
    {
        await using var rig = await Rig.CreateAsync();
        var promotionWins = 0;
        for (var i = 0; i < 6; i++)
        {
            var s = await rig.NewScenarioAsync(RootKind.Turn);
            await rig.AtAsync(s.Root, 110);
            await rig.EnsureAnswerAsync(s.Root);

            var results = await WhileLockedAsync<object?>(
                rig, s.Root, blocked: 2, whileBlocked: null, commit: false,
                async () => await rig.PromoteAsync(s), async () => await rig.CompleteAsync(s.Root));
            var promotion = (PromotionResult)results[0]!;

            Assert.True((bool)results[1]!);   // il commit del risultato valido riesce comunque: prima o dopo la promozione
            Assert.Equal("Completed", (await rig.StatusRegimeAsync(s.Root)).Status);
            if (promotion.Outcome == PromotionOutcome.Promoted)
            {
                promotionWins++;
                Assert.Equal((1L, "Extended"), (await rig.TransitionsAsync(s.Root), (await rig.StatusRegimeAsync(s.Root)).Regime));
            }
            else
            {
                Assert.Equal(PromotionOutcome.Terminal, promotion.Outcome);   // il commit terminale precedente vince
                Assert.Equal((0L, "Ordinary"), (await rig.TransitionsAsync(s.Root), (await rig.StatusRegimeAsync(s.Root)).Regime));
            }

            Assert.Equal(1L, await rig.QueryAsync<long>($"SELECT count(*) FROM \"ChatTurns\" WHERE \"Id\" = '{s.Root.Id}' AND \"ResultMessageId\" IS NOT NULL AND \"Status\" = 'Completed'"));
        }

        Assert.InRange(promotionWins, 0, 6);
    }

    [Fact]
    public async Task Two_workers_complete_the_same_turn_and_only_the_current_owner_wins_once()
    {
        await using var rig = await Rig.CreateAsync();
        var root = await rig.SeedAsync(RootKind.Turn);
        await rig.AtAsync(root, 150);
        await rig.EnsureAnswerAsync(root);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => rig.CompleteAsync(root), Ct))
                .Concat(Enumerable.Range(0, 4).Select(_ => Task.Run(() => rig.CompleteAsync(root, fence: Fence - 1), Ct)))
                .Concat([Task.Run(() => rig.CompleteAsync(root, owner: "worker-b"), Ct)]));

        Assert.Equal(1, results[..4].Count(r => r));   // un solo vincitore fra i thread del possessore corrente
        Assert.DoesNotContain(true, results[4..]);       // fence scaduto e altro worker: mai
        Assert.Equal(("Completed", "Ordinary", 0), await rig.StatusRegimeAsync(root));
    }

    [Fact]
    public async Task An_expired_fence_or_lease_writes_nothing_while_the_new_owner_continues_and_can_promote()
    {
        await using var rig = await Rig.CreateAsync();
        var s = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(s.Root, 110);
        await rig.EnsureAnswerAsync(s.Root);

        // Il worker B prende il lease (claim): fence 4. Il worker A (fence 3) è «scaduto».
        await rig.ExecAsync($"UPDATE \"ChatTurns\" SET \"LeaseOwner\" = 'worker-b', \"FencingToken\" = 4, \"AttemptCount\" = 2, \"LeaseUntil\" = (SELECT t FROM tclock.state) + interval '10 hours' WHERE \"Id\" = '{s.Root.Id}'");
        var snapshot = await rig.StateAsync(s.Root);
        var counts = await rig.CountsAsync();
        var json = JsonDocument.Parse("{}").RootElement;

        Assert.Equal(DeadlineDenial.Fenced, (await rig.AuthorityAsync(s.Root)).Denial);
        Assert.Equal(RootCommitState.Stale, await rig.CommitStateAsync(s.Root));
        Assert.Equal("fenced", await rig.ReserveAsync(s.Root));
        Assert.False(await rig.RenewAsync(s.Root));
        Assert.False(await rig.CompleteAsync(s.Root));
        Assert.Equal(PromotionOutcome.Fenced, (await rig.PromoteAsync(s)).Outcome);
        await using (var context = rig.Ctx())
        {
            await Assert.ThrowsAsync<CheckpointFencedException>(async () => await new DocumentWorkStore(context, s.Writer).CommitOutputAsync(rig.OutputOf("b2", 'x'), null, Ct));
            await Assert.ThrowsAsync<CheckpointFencedException>(async () => await new PostgresCheckpointStore(context, s.Writer).CreateCheckpointAsync("s1", json));
        }

        Assert.Equal(snapshot, await rig.StateAsync(s.Root));
        Assert.Equal(counts, await rig.CountsAsync());

        // Il nuovo owner è l'unico autorizzato: riserva, promuove, committa l'output del batch aggiuntivo con il proprio fence.
        Assert.Equal("reserved", await rig.ReserveAsync(s.Root, fence: 4, owner: "worker-b"));
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(s, lease: "worker-b", fence: 4)).Outcome);
        var writer = s.Writer with { LeaseOwner = "worker-b", FencingToken = 4 };
        await using (var context = rig.Ctx())
        {
            var committed = await new DocumentWorkStore(context, writer).CommitOutputAsync(rig.OutputOf("b2", 'x', s.Batches[0].InputHash), null, Ct);
            Assert.Equal(4L, committed.Output.CommittedFence);
        }
    }

    [Fact]
    public async Task An_expired_lease_with_the_same_fence_is_refused_and_the_reclaim_creates_a_new_fence_without_moving_the_deadlines()
    {
        await using var rig = await Rig.CreateAsync();
        var expired = await rig.NewScenarioAsync(RootKind.Run);
        await rig.AtAsync(expired.Root, 100);
        await rig.ExecAsync($"UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = (SELECT t FROM tclock.state) - interval '1 millisecond' WHERE \"Id\" = '{expired.Root.Id}'");
        var budget = await rig.BudgetAsync(expired.Root);
        Assert.Equal(DeadlineDenial.LeaseExpired, (await rig.AuthorityAsync(expired.Root)).Denial);
        Assert.Equal("fenced", await rig.ReserveAsync(expired.Root));
        Assert.Equal(PromotionOutcome.Fenced, (await rig.PromoteAsync(expired)).Outcome);
        Assert.False((await rig.SweepAsync()).Any);   // il lease scaduto non è una scadenza del root
        await using (var context = rig.Ctx())
        {
            var claim = (await rig.Leases(context).ClaimNextWorkAsync("worker-c", turns: false, runs: true, Ct))!.Run!;
            Assert.Equal((Fence + 1, 2), (claim.FencingToken, claim.AttemptCount));
        }

        Assert.Equal(budget, await rig.BudgetAsync(expired.Root));
        Assert.Equal("fenced", await rig.ReserveAsync(expired.Root));   // il vecchio worker A resta escluso
        Assert.Equal(PromotionOutcome.Fenced, (await rig.PromoteAsync(expired)).Outcome);
    }

    [Fact]
    public async Task Two_workers_claim_a_queued_root_and_only_one_gets_it_before_the_deadline_and_none_at_180()
    {
        foreach (var seconds in new[] { 119.999, 120, 179.999, 180 })
        {
            await using var rig = await Rig.CreateAsync();
            var queued = await rig.SeedAsync(RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0 });
            await rig.AtAsync(queued, seconds);
            var before = await rig.BudgetAsync(queued);

            var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
            {
                await using var context = rig.Ctx();
                return await rig.Leases(context).ClaimNextAsync($"worker-{i}", Ct);
            }, Ct)));

            if (seconds < 180)
            {
                var winner = Assert.Single(claims, c => c is not null)!;
                Assert.Equal((1L, 1), (winner.FencingToken, winner.AttemptCount));
                Assert.Equal(before, await rig.BudgetAsync(queued));   // il claim non sposta alcuna scadenza e non promuove
                Assert.Equal(("Running", "Ordinary", 0), await rig.StatusRegimeAsync(queued));
            }
            else
            {
                Assert.All(claims, c => Assert.Null(c));
                Assert.Equal(("Failed", "Ordinary", 0), await rig.StatusRegimeAsync(queued));
            }

            Assert.Equal(0L, await rig.TransitionsAsync(queued));
        }
    }

    // =====================================================================================================================================
    // 4. F — retry dopo promozione a 110 s e restart
    // =====================================================================================================================================

    [Fact]
    public async Task F_retry_and_restart_after_a_promotion_at_110_s_keep_t0_hard_receipt_and_counters_and_never_rebuild_the_budget()
    {
        await using var rig = await Rig.CreateAsync();
        var s = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(s.Root, 110);
        var t0 = await rig.T0Async(s.Root);
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(s)).Outcome);
        Assert.Equal("reserved", await rig.ReserveAsync(s.Root));   // 2 chiamate consumate
        var budget = await rig.BudgetAsync(s.Root);
        var receipt = await rig.QueryAsync<string>($"SELECT concat_ws('|', \"DerivedPlanHash\", \"ReceiptJson\"::text, \"OldDeadlineAt\", \"NewDeadlineAt\") FROM \"RegimeTransitions\" WHERE \"RootId\" = '{s.Root.Id}'");

        // Errore transitorio del primo tentativo: ritenta con backoff 2 s sulla stessa deadline (t0+3600, non +3600 dal retry).
        TurnRelease release;
        await using (var context = rig.Ctx())
        {
            release = await rig.Leases(context).ReleaseAfterFailureAsync(rig.TurnClaim(s.Root), new ModelFailure(true, "model_unreachable", null), "x", "x", Ct);
        }

        Assert.Equal(TurnTransition.Requeued, release.Outcome);
        Assert.Equal(t0.AddMilliseconds(112_000), release.NextAttemptAt);
        await rig.AtAsync(s.Root, 112.001);
        ChatTurnClaim retry;
        await using (var context = rig.Ctx())
        {
            retry = (await rig.Leases(context).ClaimNextAsync("worker-b", Ct))!;
        }

        Assert.Equal((4L, 2), (retry.FencingToken, retry.AttemptCount));
        Assert.Equal(t0.AddSeconds(3600), retry.DeadlineAt);   // il claim legge la deadline attuale: nessun cap incorporato a 180 s, nessun nuovo cap
        Assert.Equal(budget, await rig.BudgetAsync(s.Root));
        Assert.False(await rig.RenewAsync(s.Root));            // il primo possessore (fence 3) non rinnova più
        Assert.Equal(PromotionOutcome.Fenced, (await rig.PromoteAsync(s)).Outcome);

        // Oltre i 180 s originari il retry continua, riserva e non può promuovere una seconda volta.
        foreach (var at in new[] { 135d, 160d, 185d })
        {
            await rig.AtAsync(s.Root, at);
            Assert.True(await rig.RenewAsync(s.Root, fence: 4, owner: "worker-b"));
        }

        Assert.Equal("reserved", await rig.ReserveAsync(s.Root, fence: 4, owner: "worker-b"));
        Assert.Equal(PromotionOutcome.AlreadyPromoted, (await rig.PromoteAsync(s, lease: "worker-b", fence: 4)).Outcome);
        Assert.False((await rig.SweepAsync()).Any);   // il timer a 180 non chiude il root promosso
        Assert.Equal(3, await rig.CallCountAsync(s.Root));

        // Restart del processo a 3000 s: lease scaduto da tempo, nuovo claim (reclaim) con fence nuovo, contatore, t0 e hard invariati.
        await rig.AtAsync(s.Root, 3000);
        Assert.False((await rig.SweepAsync()).Any);
        await using (var context = rig.Ctx())
        {
            var reclaim = (await rig.Leases(context).ClaimNextAsync("worker-c", Ct))!;
            Assert.Equal((5L, 3), (reclaim.FencingToken, reclaim.AttemptCount));
            Assert.Equal(t0.AddSeconds(3600), reclaim.DeadlineAt);
        }

        Assert.Equal(budget, await rig.BudgetAsync(s.Root));
        Assert.Equal(3, await rig.CallCountAsync(s.Root));   // il contatore è cresciuto, mai azzerato dal restart
        Assert.Equal(receipt, await rig.QueryAsync<string>($"SELECT concat_ws('|', \"DerivedPlanHash\", \"ReceiptJson\"::text, \"OldDeadlineAt\", \"NewDeadlineAt\") FROM \"RegimeTransitions\" WHERE \"RootId\" = '{s.Root.Id}'"));

        // Il cap esatto a 3600 s chiude comunque: il restart non lo ha allungato; un commit tardivo è rifiutato.
        await rig.AtAsync(s.Root, 3599.999);
        Assert.False((await rig.SweepAsync()).Any);
        await rig.AtAsync(s.Root, 3600);
        var closed = await rig.SweepAsync();
        Assert.Equal(new[] { s.Root.Id }, closed.Turns.ToArray());
        Assert.Equal("deadline_exceeded", await rig.QueryAsync<string>($"SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = '{s.Root.Id}'"));
        Assert.False(await rig.CompleteAsync(s.Root, fence: 5, owner: "worker-c"));
        Assert.Equal(1L, await rig.TransitionsAsync(s.Root));
        await using (var context = rig.Ctx())
        {
            Assert.Null(await rig.Leases(context).ClaimNextAsync("worker-d", Ct));
        }
    }

    [Fact]
    public async Task F_the_dispatcher_monitor_reconciles_at_restart_from_the_database_and_an_old_timer_at_180_does_not_close_a_promoted_root()
    {
        await using var rig = await Rig.CreateAsync();
        var plain = await rig.SeedAsync(RootKind.Turn);
        var promoted = await rig.NewScenarioAsync(RootKind.Turn);
        var extended = await rig.SeedAsync(RootKind.Turn, new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended });
        await rig.AlignAsync(plain, promoted.Root, extended);
        await rig.AtAsync(promoted.Root, 110);
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(promoted)).Outcome);

        var options = new ExecutionOptions();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(rig.Connection))
            .AddSingleton<IOptions<ExecutionOptions>>(Options.Create(options))
            .AddScoped<ExecutionLeaseStore>()
            .BuildServiceProvider();
        using var dispatcher = new AiDispatchService(
            services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<AiDispatchService>.Instance, executor: new NoopExecutor());

        await rig.AtAsync(plain, 179.999);
        Assert.False((await dispatcher.SweepDeadlinesOnceAsync(Ct)).Any);
        await rig.AtAsync(plain, 180);
        var at180 = await dispatcher.SweepDeadlinesOnceAsync(Ct);
        Assert.Equal(new[] { plain.Id }, at180.Turns.ToArray());   // chiusa solo quella non promossa; quella promossa e l'Extended proseguono
        Assert.Equal("Running", (await rig.StatusRegimeAsync(promoted.Root)).Status);
        Assert.Equal("Running", (await rig.StatusRegimeAsync(extended)).Status);

        await rig.AtAsync(plain, 3599.999);
        Assert.False((await dispatcher.SweepDeadlinesOnceAsync(Ct)).Any);
        await rig.AtAsync(plain, 3600);
        var at3600 = await dispatcher.SweepDeadlinesOnceAsync(Ct);
        Assert.Equal(new[] { promoted.Root.Id, extended.Id }.Order().ToArray(), at3600.Turns.Order().ToArray());
        Assert.False((await dispatcher.SweepDeadlinesOnceAsync(Ct)).Any);   // idempotente
    }

    private sealed class NoopExecutor : IChatTurnExecutor
    {
        public Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct) => Task.CompletedTask;
    }

    // =====================================================================================================================================
    // 5. A–D, G — esiti nominali, coda, piano multipasso, copertura incompleta e limiti
    // =====================================================================================================================================

    [Fact]
    public async Task A_nominal_complete_at_150_s_passes_with_or_without_a_valid_promotion_and_a_promotion_does_not_decide_the_verdict()
    {
        await using var rig = await Rig.CreateAsync();

        var plain = await rig.SeedAsync(RootKind.Turn);
        await rig.AtAsync(plain, 150);
        Assert.True(await rig.CompleteAsync(plain));
        Assert.True(await rig.NominalAsync(plain, quality: true) is { Functional: true, Temporal: true, Pass: true });
        Assert.Equal("Ordinary", (await rig.StatusRegimeAsync(plain)).Regime);   // nessuna promozione per il solo tempo

        var promoted = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(promoted.Root, 110);
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(promoted)).Outcome);
        await rig.AtAsync(promoted.Root, 150);
        Assert.True(await rig.CompleteAsync(promoted.Root));
        Assert.True(await rig.NominalAsync(promoted.Root, quality: true) is { Functional: true, Temporal: true, Pass: true });
        Assert.False((await rig.NominalAsync(promoted.Root, quality: false)).Pass);   // la qualità non conforme resta FAIL anche a 150 s
        Assert.Equal(("Completed", "Extended", 1), await rig.StatusRegimeAsync(promoted.Root));
    }

    [Fact]
    public async Task B_a_nominal_over_180_s_is_a_temporal_fail_even_after_promotion_and_never_a_late_commit_without_one()
    {
        await using var rig = await Rig.CreateAsync();

        // Senza promozione: scade a 180 s, il risultato disponibile a 190 s non si committa; esito Failed, mai Completed tardivo.
        var plain = await rig.SeedAsync(RootKind.Turn);
        await rig.EnsureAnswerAsync(plain);
        await rig.AtAsync(plain, 190);
        Assert.False(await rig.CompleteAsync(plain));
        Assert.Equal(new[] { plain.Id }, (await rig.SweepAsync()).Turns.ToArray());
        Assert.Equal(("Failed", "Ordinary", 0), await rig.StatusRegimeAsync(plain));
        Assert.Null(await rig.QueryAsync<Guid?>($"SELECT \"ResultMessageId\" FROM \"ChatTurns\" WHERE \"Id\" = '{plain.Id}'"));
        Assert.False((await rig.NominalAsync(plain, quality: true)).Pass);

        // Già promosso validamente a 110 s: completa funzionalmente a 190 s ma è FAIL temporale N, senza riclassificazione a E.
        var promoted = await rig.NewScenarioAsync(RootKind.Turn);
        await rig.AtAsync(promoted.Root, 110);
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(promoted)).Outcome);
        await rig.AtAsync(promoted.Root, 135);
        Assert.True(await rig.RenewAsync(promoted.Root));
        await rig.AtAsync(promoted.Root, 160);
        Assert.True(await rig.RenewAsync(promoted.Root));
        await rig.AtAsync(promoted.Root, 185);
        Assert.True(await rig.RenewAsync(promoted.Root));
        await rig.AtAsync(promoted.Root, 190);
        Assert.True(await rig.CompleteAsync(promoted.Root));
        var verdict = await rig.NominalAsync(promoted.Root, quality: true);
        Assert.Equal(new NominalEvaluation(Functional: true, Temporal: false, Pass: false), verdict);
        Assert.Equal("Ordinary", await rig.QueryAsync<string>($"SELECT \"InitialRegime\" FROM \"ChatTurns\" WHERE \"Id\" = '{promoted.Root.Id}'"));
    }

    [Fact]
    public async Task C_a_root_only_queued_at_120_s_is_not_promoted_and_can_still_commit_at_140_s_with_the_original_deadline()
    {
        await using var rig = await Rig.CreateAsync();
        var queued = await rig.SeedAsync(RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0 });
        var t0 = await rig.T0Async(queued);
        await rig.AtAsync(queued, 120);
        var before = await rig.BudgetAsync(queued);

        Assert.False((await rig.SweepAsync()).Any);   // il monitor non promuove e non chiude
        Assert.Equal(before, await rig.BudgetAsync(queued));
        ChatTurnClaim claim;
        await using (var context = rig.Ctx())
        {
            claim = (await rig.Leases(context).ClaimNextAsync("worker-q", Ct))!;
        }

        Assert.Equal(t0.AddSeconds(180), claim.DeadlineAt);   // stessa deadline originaria, nessuna promozione dalla sola coda
        await rig.AtAsync(queued, 130);
        Assert.False((await rig.SweepAsync()).Any);
        await rig.AtAsync(queued, 140);
        await rig.EnsureAnswerAsync(queued);
        await using (var context = rig.Ctx())
        {
            Assert.True(await rig.Leases(context).CompleteAsync(claim, rig.AnswerOf(queued), Ct));
        }

        Assert.Equal(("Completed", "Ordinary", 0), await rig.StatusRegimeAsync(queued));
        Assert.Equal(0L, await rig.TransitionsAsync(queued));
        Assert.True((await rig.NominalAsync(queued, quality: true)).Pass);   // l'esito usa il profilo originario

        // Con lo slot occupato da un altro lavoro, il monitor continua e la coda non promuove: a 180 s chiude solo il root scaduto.
        var occupant = await rig.SeedAsync(RootKind.Turn, new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended });
        var waiting = await rig.SeedAsync(RootKind.Turn, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0 });
        await rig.AtAsync(waiting, 120);
        await using (var context = rig.Ctx())
        {
            Assert.Null(await rig.Leases(context).ClaimNextAsync("worker-q2", Ct));
        }

        Assert.False((await rig.SweepAsync()).Any);
        Assert.Equal(("Queued", "Ordinary", 0), await rig.StatusRegimeAsync(waiting));
        await rig.AtAsync(waiting, 180);
        Assert.Equal(new[] { waiting.Id }, (await rig.SweepAsync()).Turns.ToArray());
        Assert.Equal("Running", (await rig.StatusRegimeAsync(occupant)).Status);
        Assert.Equal(0L, await rig.QueryAsync<long>("SELECT count(*) FROM \"RegimeTransitions\""));
    }

    [Fact]
    public async Task D_a_structurally_extended_root_is_never_promoted_and_has_t0_plus_3600_from_the_start()
    {
        await using var rig = await Rig.CreateAsync();
        var s = await rig.NewScenarioAsync(RootKind.Run, spec: new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, CallCount = 1 });
        await rig.AtAsync(s.Root, 110);
        var snapshot = await rig.StateAsync(s.Root);

        var result = await rig.PromoteAsync(s);

        Assert.Equal((PromotionOutcome.NotEligible, "already_extended"), (result.Outcome, result.Reason));
        Assert.Equal(snapshot, await rig.StateAsync(s.Root));
        Assert.Equal(0L, await rig.TransitionsAsync(s.Root));
        await rig.AtAsync(s.Root, 3599.999);
        Assert.True((await rig.AuthorityAsync(s.Root)).Allowed);
        await rig.AtAsync(s.Root, 3600);
        Assert.False((await rig.AuthorityAsync(s.Root)).Allowed);
        Assert.Contains(s.Root.Id, (await rig.SweepAsync()).Runs);
    }

    [Fact]
    public async Task G_incomplete_coverage_at_the_hard_cap_never_becomes_completed_and_a_late_output_is_refused()
    {
        await using var rig = await Rig.CreateAsync();
        var s = await rig.NewScenarioAsync(RootKind.Turn, spec: new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, CallCount = 1 });
        await rig.EnsureAnswerAsync(s.Root);
        var outputs = await rig.CountsAsync();
        await rig.AtAsync(s.Root, 3600);   // il lavoro per l'obbligazione o2 non è terminato

        Assert.False(await rig.CompleteAsync(s.Root));
        await using (var context = rig.Ctx())
        {
            await Assert.ThrowsAsync<CheckpointFencedException>(async () =>
                await new DocumentWorkStore(context, s.Writer).CommitOutputAsync(rig.OutputOf("b2", 'y', s.Batches[0].InputHash), null, Ct));
        }

        Assert.Equal(new[] { s.Root.Id }, (await rig.SweepAsync()).Turns.ToArray());
        Assert.Equal(("Failed", "Extended", 0), await rig.StatusRegimeAsync(s.Root));
        Assert.Equal("deadline_exceeded", await rig.QueryAsync<string>($"SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = '{s.Root.Id}'"));
        Assert.Null(await rig.QueryAsync<Guid?>($"SELECT \"ResultMessageId\" FROM \"ChatTurns\" WHERE \"Id\" = '{s.Root.Id}'"));
        Assert.Equal(outputs.Outputs, (await rig.CountsAsync()).Outputs);   // l'output già Committed (frontier) resta; nessun output tardivo
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Exact_call_limits_are_allowed_at_the_maximum_and_refuse_only_the_next_call_or_the_work_beyond_it(RootKind kind)
    {
        await using var rig = await Rig.CreateAsync();
        var s = await rig.NewScenarioAsync(kind, seconds: 110, spec: new RootSpec { CallCount = 1 });
        await rig.AtAsync(s.Root, 110);

        // Riserva: 1 → 2 = massimo ammesso; la successiva è rifiutata senza incremento.
        Assert.Equal("reserved", await rig.ReserveAsync(s.Root, maxCalls: 2));
        Assert.Equal(2, await rig.CallCountAsync(s.Root));
        Assert.Equal("limit", await rig.ReserveAsync(s.Root, maxCalls: 2));
        Assert.Equal(2, await rig.CallCountAsync(s.Root));

        // Lavoro aggiuntivo oltre il massimo → limite (Failed per il chiamante), nessuna promozione né scrittura; byte come limite certo del chiamante.
        var snapshot = await rig.StateAsync(s.Root);
        var counts = await rig.CountsAsync();
        Assert.Equal(PromotionOutcome.LimitExceeded, (await rig.PromoteAsync(s, new PromotionSignals(MaxAiCalls: 2))).Outcome);
        Assert.Equal(PromotionOutcome.LimitExceeded, (await rig.PromoteAsync(s, new PromotionSignals(LimitExceeded: "byte_limit"))).Outcome);
        Assert.Equal(snapshot, await rig.StateAsync(s.Root));
        Assert.Equal(counts, await rig.CountsAsync());

        // Esattamente al massimo (2 + 1 batch = 3) il lavoro necessario è ancora ammesso; un risultato completo precede la promozione.
        Assert.Equal(PromotionOutcome.CompletionPrecedes, (await rig.PromoteAsync(s, new PromotionSignals(CompleteResultReady: true, MaxAiCalls: 3))).Outcome);
        Assert.Equal(snapshot, await rig.StateAsync(s.Root));
        Assert.Equal(PromotionOutcome.Promoted, (await rig.PromoteAsync(s, new PromotionSignals(MaxAiCalls: 3))).Outcome);
    }

    [Fact]
    public async Task A_complete_final_result_with_the_counter_exactly_at_the_maximum_commits_and_does_not_fail_for_the_counter_alone()
    {
        await using var rig = await Rig.CreateAsync();
        var root = await rig.SeedAsync(RootKind.Turn, new RootSpec { CallCount = 2 });
        await rig.AtAsync(root, 150);
        await rig.EnsureAnswerAsync(root);

        Assert.Equal("limit", await rig.ReserveAsync(root, maxCalls: 2));   // nessuna chiamata ulteriore è ammessa…
        Assert.True(await rig.CompleteAsync(root));                         // …ma il finale già completo si committa: il contatore non è un motivo di errore
        Assert.Equal(("Completed", "Ordinary", 0), await rig.StatusRegimeAsync(root));
        Assert.Equal(2, await rig.CallCountAsync(root));
    }

    // =====================================================================================================================================
    // 6. Preflight tardivo (policy con il clock del database)
    // =====================================================================================================================================

    [Fact]
    public async Task A_late_preflight_uses_the_database_clock_and_the_original_t0_so_waiting_never_makes_it_extended()
    {
        await using var rig = await Rig.CreateAsync();

        DocumentRegimeInput Input(DateTimeOffset t0, DateTimeOffset now) => new()
        {
            RequestedAt = t0, Now = now, HardDeadlineAt = t0.AddSeconds(3600), OrdinaryDeadlineSeconds = 180, RuntimeFingerprint = "fp-1", WindowTokens = 10_000,
            OutputTokens = 2_000, MarginTokens = 1_000, PayloadTokens = 3_000, TokenQuality = TokenCountQuality.Measured, PlannedCalls = 1, HasDependencies = false,
            ReasoningDisabled = true, ShapeFloorTokens = 100, ShapeSkeletonHash = H('a'), Provenance = new ClassificationProvenance("runtime", "config", "config", "tokenizer"),
        };

        // Pending (provvisorio Extended): nessuna inferenza ammessa a 130 s e a 190 s; non è promuovibile e nessuna ricevuta lo promuove prima della classificazione.
        var pending = await rig.NewScenarioAsync(RootKind.Turn, spec: new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, CallCount = 1 });
        await rig.ExecAsync($"UPDATE \"ChatTurns\" SET \"ClassificationStatus\" = 'Pending', \"InitialRegime\" = NULL, \"OrdinaryDeadlineAt\" = NULL WHERE \"Id\" = '{pending.Root.Id}'");
        var pendingT0 = await rig.T0Async(pending.Root);
        foreach (var seconds in new[] { 130d, 190d })
        {
            await rig.AtAsync(pending.Root, seconds);
            var now = await rig.QueryAsync<DateTimeOffset>("SELECT clock_timestamp()");
            Assert.False(DocumentRegimePolicy.Pending(Input(pendingT0, now)).AiAllowed);
            Assert.Equal(PromotionOutcome.NotEligible, (await rig.PromoteAsync(pending)).Outcome);
        }

        Assert.Equal(0L, await rig.TransitionsAsync(pending.Root));

        // Preflight concluso a 130 s: Ordinary con 50 s residui dal t0 originale; il guard del root (stessa deadline) concorda.
        var ordinary = await rig.SeedAsync(RootKind.Turn);
        var t0 = await rig.T0Async(ordinary);
        await rig.AtAsync(ordinary, 130);
        var at130 = DocumentRegimePolicy.Classify(Input(t0, await rig.QueryAsync<DateTimeOffset>("SELECT clock_timestamp()")));
        Assert.Equal((ClassificationOutcome.AdmitOrdinary, ExecutionRegime.Ordinary, false), (at130.Outcome, at130.Regime, at130.FailDeadlineExceeded));
        var authority = await rig.AuthorityAsync(ordinary);
        Assert.Equal(TimeSpan.FromSeconds(50), authority.Remaining);
        Assert.Equal(at130.EffectiveDeadlineAt, authority.RootEffectiveDeadlineAt);

        // Preflight concluso a 190 s: stessa classificazione Ordinary, fallimento senza invio e mai Extended per la sola attesa; il guard concorda.
        await rig.AtAsync(ordinary, 190);
        var at190 = DocumentRegimePolicy.Classify(Input(t0, await rig.QueryAsync<DateTimeOffset>("SELECT clock_timestamp()")));
        Assert.Equal((ClassificationOutcome.AdmitOrdinary, ExecutionRegime.Ordinary, true, "deadline_exceeded"), (at190.Outcome, at190.Regime, at190.FailDeadlineExceeded, at190.ErrorCode));
        Assert.Equal(DeadlineDenial.DeadlineExceeded, (await rig.AuthorityAsync(ordinary)).Denial);
        Assert.Equal(Fence + 1, await rig.SweepThenFenceAsync(ordinary));
        Assert.Equal(0L, await rig.TransitionsAsync(ordinary));
    }

    // =====================================================================================================================================
    // Infrastruttura
    // =====================================================================================================================================

    /// <summary>
    /// Tiene il lock di riga del root in una transazione separata, avvia i contendenti, attende che <paramref name="blocked"/> backend siano in
    /// attesa di un lock, esegue <paramref name="whileBlocked"/> (es. avanza il clock) e rilascia (commit o rollback, la riga non è modificata).
    /// </summary>
    private static async Task<object?[]> WhileLockedAsync<T>(
        Rig rig, SeededRoot root, int blocked, Func<Task>? whileBlocked, bool commit, params Func<Task<T>>[] contenders)
    {
        await using var holder = new NpgsqlConnection(rig.Db.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand($"SELECT 1 FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}' FOR UPDATE", holder, transaction))
        {
            await lockRow.ExecuteScalarAsync(Ct);
        }

        var tasks = contenders.Select(c => Task.Run(async () => (object?)await c(), Ct)).ToArray();
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (await rig.Db.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'") < blocked)
        {
            Assert.True(DateTime.UtcNow < limit, "I contendenti non sono entrati in attesa sul lock di riga.");
            Assert.DoesNotContain(tasks, t => t.IsFaulted);
            await Task.Delay(30, Ct);
        }

        if (whileBlocked is not null)
        {
            await whileBlocked();
        }

        if (commit)
        {
            await transaction.CommitAsync(Ct);
        }
        else
        {
            await transaction.RollbackAsync(Ct);
        }

        return await Task.WhenAll(tasks);
    }

    private sealed record Scenario(Rig Rig, SeededRoot Root, CheckpointWriter Writer, WorkOutput First, AdditionalWorkReceipt Receipt, DerivedBatchInput[] Batches)
    {
        public PromotionRequest Request(PromotionSignals? signals = null, string lease = Lease, long fence = Fence) =>
            new(Root.OwnerKind(), Root.Id, lease, fence, Receipt, Batches, signals);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly Dictionary<Guid, Guid> _answers = [];

        private Rig(ParsingDatabase db, string connection)
        {
            Db = db;
            Connection = connection;
        }

        public ParsingDatabase Db { get; }

        /// <summary>Connessione con il clock congelato (<c>tclock</c> prima di <c>pg_catalog</c>): la usa tutto il codice sotto prova.</summary>
        public string Connection { get; }

        public static async Task<Rig> CreateAsync()
        {
            var db = await ParsingDatabase.CreateAsync();
            await db.ExecuteAsync("""
                CREATE SCHEMA tclock;
                CREATE TABLE tclock.state (id int PRIMARY KEY, t timestamptz NOT NULL);
                INSERT INTO tclock.state VALUES (1, '2030-01-01 00:00:00+00');
                CREATE FUNCTION tclock.clock_timestamp() RETURNS timestamptz LANGUAGE sql VOLATILE AS 'SELECT t FROM tclock.state WHERE id = 1';
                """);
            var rig = new Rig(db, new NpgsqlConnectionStringBuilder(db.ConnectionString) { SearchPath = "tclock, public, pg_catalog" }.ConnectionString);

            // Verifica del congelamento: se la funzione non precede pg_catalog il test non proverebbe nulla.
            var frozen = await rig.QueryAsync<DateTimeOffset>("SELECT clock_timestamp()");
            if (frozen != new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero))
            {
                await rig.DisposeAsync();
                throw new InvalidOperationException("Il clock del database di test non è congelato.");
            }

            return rig;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Connection).Options);

        public ExecutionLeaseStore Leases(AppDbContext context) => new(context, Options.Create(new ExecutionOptions()));

        // ------------------------------------------------------------------ clock

        public Task SetAsync(DateTimeOffset at) =>
            Db.ExecuteAsync($"UPDATE tclock.state SET t = '{at.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}+00'::timestamptz WHERE id = 1");

        public Task<DateTimeOffset> T0Async(SeededRoot root) =>
            QueryAsync<DateTimeOffset>($"SELECT \"RequestedAt\" FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'");

        /// <summary>Sposta t0 e tutte le scadenze di <paramref name="others"/> sul t0 di <paramref name="anchor"/> (stesso istante per più root).</summary>
        public async Task AlignAsync(SeededRoot anchor, params SeededRoot[] others)
        {
            var t0 = await T0Async(anchor);
            foreach (var other in others)
            {
                var micros = (t0 - await T0Async(other)).Ticks / 10;
                var shift = $"interval '{micros} microseconds'";
                await Db.ExecuteAsync($"UPDATE \"{other.Table}\" SET \"RequestedAt\" = \"RequestedAt\" + {shift}, \"HardDeadlineAt\" = \"HardDeadlineAt\" + {shift}, \"EffectiveDeadlineAt\" = \"EffectiveDeadlineAt\" + {shift}, \"DeadlineAt\" = \"DeadlineAt\" + {shift}, \"OrdinaryDeadlineAt\" = \"OrdinaryDeadlineAt\" + {shift} WHERE \"Id\" = '{other.Id}'");
            }
        }

        /// <summary>Porta il clock congelato a t0 + secondi (millisecondi interi) del root.</summary>
        public async Task AtAsync(SeededRoot root, double seconds) =>
            await SetAsync((await T0Async(root)).AddMilliseconds(Math.Round(seconds * 1000)));

        // ------------------------------------------------------------------ SQL

        public async Task<T> QueryAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return reader.IsDBNull(0) ? default! : reader.GetFieldValue<T>(0);
        }

        public async Task ExecAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync(Ct);
        }

        public Task<string> StateAsync(SeededRoot r) => QueryAsync<string>($"""
            SELECT concat_ws('|', "Status", "Regime", "InitialRegime", "PromotionCount", "DeadlineAt", "EffectiveDeadlineAt", "HardDeadlineAt", "OrdinaryDeadlineAt",
                             "RequestedAt", "FencingToken", "AttemptCount", "CallCount", "ErrorCode", "CompletedAt", "LeaseOwner", "LeaseUntil", "PromotedAt")
            FROM "{r.Table}" WHERE "Id" = '{r.Id}'
            """);

        /// <summary>Budget del root: cap, t0, regime e scadenze. Non devono mai essere azzerati né allungati da retry o restart.</summary>
        public Task<string> BudgetAsync(SeededRoot r) => QueryAsync<string>($"""
            SELECT concat_ws('|', "HardDeadlineAt", "RequestedAt", "OrdinaryDeadlineAt", "InitialRegime", "EffectiveDeadlineAt", "DeadlineAt", "Regime", "PromotionCount")
            FROM "{r.Table}" WHERE "Id" = '{r.Id}'
            """);

        public async Task<(string Status, string Regime, int Promotions)> StatusRegimeAsync(SeededRoot r)
        {
            var parts = (await QueryAsync<string>($"SELECT concat_ws('|', \"Status\", \"Regime\", \"PromotionCount\") FROM \"{r.Table}\" WHERE \"Id\" = '{r.Id}'")).Split('|');
            return (parts[0], parts[1], int.Parse(parts[2]));
        }

        public Task<long> TransitionsAsync(SeededRoot r) => QueryAsync<long>($"SELECT count(*) FROM \"RegimeTransitions\" WHERE \"RootId\" = '{r.Id}'");

        public Task<int> CallCountAsync(SeededRoot r) => QueryAsync<int>($"SELECT \"CallCount\" FROM \"{r.Table}\" WHERE \"Id\" = '{r.Id}'");

        public async Task<(long Batches, long Transitions, long Outputs)> CountsAsync() =>
            (await QueryAsync<long>("SELECT count(*) FROM \"DerivedBatches\""),
             await QueryAsync<long>("SELECT count(*) FROM \"RegimeTransitions\""),
             await QueryAsync<long>("SELECT count(*) FROM \"WorkOutputs\""));

        // ------------------------------------------------------------------ semina

        public Task<SeededRoot> SeedAsync(RootKind kind, RootSpec? spec = null) =>
            DocumentRegimeRoots.SeedAsync(Db, kind, (spec ?? new RootSpec()) with { LeaseUntilSql = "clock_timestamp() + interval '10 hours'" });

        private static CheckpointWriter WriterOf(SeededRoot root, string lease = Lease, long fence = Fence) => root.Kind == RootKind.Turn
            ? CheckpointWriter.ForChatTurn(root.Id, lease, fence, "chat-docs-1")
            : new CheckpointWriter(root.Id, lease, fence, WorkOwnerKind.ProcessingRun, "project-analysis-documents-v2", "1.26.0");

        private static DerivedBatchInput Batch(string id, string input, params string[] parents) =>
            new(id, "extract", null, 0, 0, 1, parents, """{"ranges":[[0,10]]}""", 120, "measured", input, "evidence-1.1.0", "p1");

        public WorkOutputInput OutputOf(string batchId, char marker, string? input = null)
        {
            var payload = $$"""{"evidences":[],"noEvidenceReason":"{{marker}}"}""";
            return new(batchId, "InvokeBatch", input ?? H('2'), CanonicalJson.Sha256Hex(payload), payload, null, null, """["o2"]""", null, null, "evidence-1.1.0", "v1", null);
        }

        /// <summary>
        /// Root con manifesto, un primo batch b1 già Committed (copre l'obbligazione o1) e una ricevuta additional_work valida per b2 (copre o2).
        /// La scena è costruita al clock <paramref name="seconds"/> (default 100 s dal t0); chi serve la sposta poi dove vuole.
        /// </summary>
        public async Task<Scenario> NewScenarioAsync(RootKind kind, double seconds = 100, RootSpec? spec = null)
        {
            var root = await SeedAsync(kind, spec ?? new RootSpec { CallCount = 1 });
            await AtAsync(root, seconds);
            var writer = WriterOf(root);
            await using var context = Ctx();
            var store = new DocumentWorkStore(context, writer);
            await store.PublishManifestAsync(new WorkManifestInput(H('a'), H('b'), "fp-1", writer.DefinitionVersion!, "alg-1", DocumentRegimeRoots.PolicyHash, """{"nodes":["Load","PrepareBatch"]}"""), Ct);
            await store.PublishBatchesAsync([Batch("b1", H('1'))], Ct);
            const string payload = """{"evidences":[],"noEvidenceReason":"nessuna"}""";
            var first = (await store.CommitOutputAsync(
                new WorkOutputInput("b1", "InvokeBatch", H('1'), CanonicalJson.Sha256Hex(payload), payload, null, null, """["o1"]""", null, null, "evidence-1.1.0", "v1", null), null, Ct)).Output;
            DerivedBatchInput[] batches = [Batch("b2", H('2'), first.OutputHash)];
            var receipt = new AdditionalWorkReceipt(AdditionalWorkReceipt.AdditionalWork, H('b'), [first.OutputHash], ["o2"], ["b2"], "");
            receipt = receipt with { DerivedPlanHash = DocumentRegimeTransitions.ComputeDerivedPlanHash(receipt, batches) };
            return new(this, root, writer, first, receipt, batches);
        }

        // ------------------------------------------------------------------ operazioni dei worker (sempre contesto, connessione e transazione propri)

        public ChatTurnClaim TurnClaim(SeededRoot root, long fence = Fence, string owner = Lease, int attempt = 1) =>
            new(root.Id, root.Conversation, Guid.Empty, 1, owner, fence, attempt, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(3));

        public async Task<DeadlineAuthority> AuthorityAsync(SeededRoot root, string owner = Lease, long fence = Fence)
        {
            await using var context = Ctx();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            var authority = await DocumentDeadlineGuard.EvaluateAsync(context, root.OwnerKind(), root.Id, owner, fence, lockRow: true, Ct);
            await transaction.RollbackAsync(Ct);
            return authority;
        }

        public async Task<RootCommitState> CommitStateAsync(SeededRoot root, string owner = Lease, long fence = Fence)
        {
            await using var context = Ctx();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            var state = await DocumentDeadlineGuard.CommitStateAsync(context, root.Kind == RootKind.Turn, root.Id, owner, fence, Ct);
            await transaction.RollbackAsync(Ct);
            return state;
        }

        public async Task<string> ReserveAsync(SeededRoot root, long fence = Fence, string owner = Lease, int? maxCalls = null)
        {
            await using var context = Ctx();
            try
            {
                await new RootCallCounterReservation(context, WriterOf(root, owner, fence), maxCalls).ReserveAsync(AnyBatch, Ct);
                return "reserved";
            }
            catch (CheckpointFencedException)
            {
                return "fenced";
            }
            catch (DocumentWorkException ex) when (ex.Code == DocumentWorkException.CallLimitExceeded)
            {
                return "limit";
            }
        }

        public async Task<bool> RenewAsync(SeededRoot root, long fence = Fence, string owner = Lease)
        {
            await using var context = Ctx();
            return root.Kind == RootKind.Turn
                ? await Leases(context).RenewAsync(TurnClaim(root, fence, owner), Ct)
                : await Leases(context).RenewAsync(new AnalysisRunClaim(root.Id, owner, fence, 1), Ct);
        }

        public async Task<ExpiredWork> SweepAsync()
        {
            await using var context = Ctx();
            return await Leases(context).SweepExpiredDocumentWorkAsync(turns: true, analysisRuns: true, documentRuns: true, Ct);
        }

        public async Task<long> SweepThenFenceAsync(SeededRoot root)
        {
            await SweepAsync();
            return await QueryAsync<long>($"SELECT \"FencingToken\" FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'");
        }

        public async Task<PromotionResult> PromoteAsync(Scenario s, PromotionSignals? signals = null, string lease = Lease, long fence = Fence)
        {
            await using var context = Ctx();
            return await new DocumentRegimeTransitions(context).PromoteAsync(s.Request(signals, lease, fence), Ct);
        }

        public async Task EnsureAnswerAsync(SeededRoot root)
        {
            if (_answers.ContainsKey(root.Id))
            {
                return;
            }

            var answer = Guid.NewGuid();
            await Db.ExecuteAsync($"""
                INSERT INTO "ConversationMessages" ("Id", "ConversationId", "Sequence", "Role", "Body", "CreatedAt", "AgentId", "ReplyToMessageId", "ChatTurnId")
                SELECT '{answer}', t."ConversationId", (SELECT max(m2."Sequence") + 1 FROM "ConversationMessages" m2 WHERE m2."ConversationId" = t."ConversationId"),
                       'Assistant', 'risposta di prova', now(), 'test', t."TriggerMessageId", t."Id"
                FROM "ChatTurns" t WHERE t."Id" = '{root.Id}'
                """);
            _answers[root.Id] = answer;
        }

        public Guid AnswerOf(SeededRoot root) => _answers[root.Id];

        /// <summary>Commit finale di un ChatTurn (risposta già scritta dal chiamante nella stessa transazione, qui solo il passaggio protetto a Completed).</summary>
        public async Task<bool> CompleteAsync(SeededRoot root, long fence = Fence, string owner = Lease)
        {
            await EnsureAnswerAsync(root);
            await using var context = Ctx();
            return await Leases(context).CompleteAsync(TurnClaim(root, fence, owner), AnswerOf(root), Ct);
        }

        public async Task<NominalEvaluation> NominalAsync(SeededRoot root, bool quality)
        {
            var requested = await T0Async(root);
            var completed = await QueryAsync<DateTimeOffset?>($"SELECT CASE WHEN \"Status\" = 'Completed' THEN \"CompletedAt\" END FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'");
            return NominalProfile.Evaluate(completed is not null, quality, requested, completed);
        }
    }
}
