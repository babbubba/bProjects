using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T118 (PG) — guard dell'autorità temporale corrente dei root documentali e dei figli: sotto il clock del database un'operazione è
/// ammessa solo se root/figlio sono attivi, owner/fence/lease sono validi e now &lt; min(DeadlineAt locale, EffectiveDeadlineAt del padre).
/// Un vecchio claim non è autorità (né per chiudere né per prolungare): si rilegge sempre la riga corrente; rinnovi e claim non
/// azzerano alcun tetto. Il ramo legacy (EffectiveDeadlineAt nullo) resta invariato. Nessuna CAS di promozione, nessuno scheduler.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentDeadlineGuardTests
{
    private const string Lease = "worker-a";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ExecutionLeaseStore Leases(AppDbContext context) => new(context, Options.Create(new ExecutionOptions()));

    private static ChatTurnClaim TurnClaim(SeededRoot root, long fence = 3, string owner = Lease) =>
        new(root.Id, root.Conversation, Guid.Empty, 1, owner, fence, 1, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(3));

    private static async Task ExpireAsync(ParsingDatabase database, SeededRoot root, string sql = "clock_timestamp() - interval '1 millisecond'") =>
        await database.ExecuteAsync($"UPDATE \"{root.Table}\" x SET \"DeadlineAt\" = v.t, \"EffectiveDeadlineAt\" = v.t FROM (SELECT {sql} AS t) v WHERE x.\"Id\" = '{root.Id}'");

    // ------------------------------------------------------------------ root: autorità dal DB

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Root_authority_is_read_from_the_db_clock_and_follows_the_current_effective_deadline(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { ElapsedSeconds = 100 });
        await using var context = database.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        var live = await DocumentDeadlineGuard.EvaluateAsync(context, root.OwnerKind(), root.Id, Lease, 3, lockRow: true, Ct);
        Assert.True(live.Allowed);
        Assert.Equal(DeadlineDenial.None, live.Denial);
        Assert.InRange(live.Remaining.TotalSeconds, 70, 80);   // t0+180 s con 100 s trascorsi, dal clock del database
        Assert.Equal(live.RootEffectiveDeadlineAt, live.Until);
        await transaction.RollbackAsync(Ct);

        // La proiezione cambia (promozione): l'autorità non è «incollata» al valore visto prima.
        await database.ExecuteAsync($"UPDATE \"{root.Table}\" SET \"DeadlineAt\" = \"HardDeadlineAt\", \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" WHERE \"Id\" = '{root.Id}'");
        var promoted = await DocumentDeadlineGuard.EvaluateAsync(context, root.OwnerKind(), root.Id, Lease, 3, lockRow: false, Ct);
        Assert.InRange(promoted.Remaining.TotalSeconds, 3_400, 3_500);

        await ExpireAsync(database, root);
        var expired = await DocumentDeadlineGuard.EvaluateAsync(context, root.OwnerKind(), root.Id, Lease, 3, lockRow: false, Ct);
        Assert.False(expired.Allowed);
        Assert.Equal(DeadlineDenial.DeadlineExceeded, expired.Denial);
        Assert.Equal(TimeSpan.Zero, expired.Remaining);
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Root_authority_requires_running_owner_fence_and_a_valid_lease(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var running = await DocumentRegimeRoots.SeedAsync(database, kind);
        var staleLease = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { LeaseUntilSql = "clock_timestamp() - interval '1 second'" });
        var queued = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0 });
        var failed = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { Status = "Failed", LeaseOwner = null });
        await using var context = database.NewContext();

        async Task<DeadlineDenial> Denial(SeededRoot root, string owner = Lease, long fence = 3) =>
            (await DocumentDeadlineGuard.EvaluateAsync(context, root.OwnerKind(), root.Id, owner, fence, lockRow: false, Ct)).Denial;

        Assert.Equal(DeadlineDenial.None, await Denial(running));
        Assert.Equal(DeadlineDenial.Fenced, await Denial(running, fence: 2));           // fence vecchio
        Assert.Equal(DeadlineDenial.Fenced, await Denial(running, owner: "worker-b"));  // altro owner
        Assert.Equal(DeadlineDenial.LeaseExpired, await Denial(staleLease));
        Assert.Equal(DeadlineDenial.NotRunning, await Denial(queued, fence: 0));
        Assert.Equal(DeadlineDenial.Terminal, await Denial(failed));
        Assert.Equal(DeadlineDenial.Missing, (await DocumentDeadlineGuard.EvaluateAsync(context, kind == RootKind.Turn ? WorkOwnerKind.ChatTurn : WorkOwnerKind.ProcessingRun, Guid.NewGuid(), Lease, 3, false, Ct)).Denial);
    }

    // ------------------------------------------------------------------ figlio: min(locale, padre)

    [Fact]
    public async Task Child_authority_is_the_minimum_of_its_own_deadline_and_the_current_parent_effective_deadline()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        await database.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "Status" = 'Running', "LeaseOwner" = '{Lease}', "LeaseUntil" = clock_timestamp() + interval '1 minute', "FencingToken" = 5, "AttemptCount" = 1 WHERE "Id" = '{seed.Job}';
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{seed.Preparation}';
            """);
        await using var context = database.NewContext();
        Task<DeadlineAuthority> Evaluate(long fence = 5) =>
            DocumentDeadlineGuard.EvaluateAsync(context, WorkOwnerKind.DocumentJob, seed.Job, Lease, fence, lockRow: false, Ct);

        // Il limite del padre (t0+1 h) è il più stretto: il figlio ha lo stesso cap.
        var withParent = await Evaluate();
        Assert.True(withParent.Allowed);
        Assert.InRange(withParent.Remaining.TotalSeconds, 3_500, 3_601);

        // Il limite locale del figlio è più stretto di quello del padre: prevale e il padre non lo allarga.
        await database.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"DeadlineAt\" = clock_timestamp() + interval '30 seconds' WHERE \"Id\" = '{seed.Job}'");
        var tighter = await Evaluate();
        Assert.InRange(tighter.Remaining.TotalSeconds, 25, 31);

        // Il padre si stringe: prevale il suo limite corrente, anche se il figlio ne ha uno più largo.
        await database.ExecuteAsync($"""
            UPDATE "DocumentJobs" SET "DeadlineAt" = "DeadlineAt" + interval '1 hour' WHERE "Id" = '{seed.Job}';
            UPDATE "DocumentPreparations" SET "EffectiveDeadlineAt" = clock_timestamp() + interval '20 seconds' WHERE "Id" = '{seed.Preparation}';
            """);
        var parentTighter = await Evaluate();
        Assert.InRange(parentTighter.Remaining.TotalSeconds, 15, 21);

        // Padre scaduto: nessuna autorità al figlio, anche con lease e fence validi e DeadlineAt locale lontana.
        await database.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = clock_timestamp() - interval '1 millisecond' WHERE \"Id\" = '{seed.Preparation}'");
        Assert.Equal(DeadlineDenial.ParentDeadlineExceeded, (await Evaluate()).Denial);

        // Padre non più attivo (terminale) o figlio scaduto: negato; fence vecchio: negato.
        await database.ExecuteAsync($"""
            UPDATE "DocumentPreparations" SET "EffectiveDeadlineAt" = "HardDeadlineAt", "Status" = 'Failed', "ErrorCode" = 'x' WHERE "Id" = '{seed.Preparation}'
            """);
        Assert.Equal(DeadlineDenial.ParentNotActive, (await Evaluate()).Denial);
        await database.ExecuteAsync($"""
            UPDATE "DocumentPreparations" SET "Status" = 'Extracting', "ErrorCode" = NULL WHERE "Id" = '{seed.Preparation}';
            UPDATE "DocumentJobs" SET "DeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{seed.Job}';
            """);
        Assert.Equal(DeadlineDenial.DeadlineExceeded, (await Evaluate()).Denial);
        await database.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"DeadlineAt\" = \"DeadlineAt\" + interval '1 hour' WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(DeadlineDenial.Fenced, (await Evaluate(fence: 4)).Denial);
        Assert.True((await Evaluate()).Allowed);
    }

    // ------------------------------------------------------------------ store del lease: claim/renew/release/complete

    [Fact]
    public async Task An_old_claim_is_no_authority_renew_follows_the_database_in_both_directions_and_never_resets_the_cap()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        // Il root scade fra 0,4 s: l'autorità del vecchio claim (deadline di 3 minuti nel suo record) non conta.
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { EffectiveSql = "clock_timestamp() + interval '400 milliseconds'" });
        var claim = TurnClaim(root);
        await using var context = database.NewContext();
        var leases = Leases(context);
        var before = await database.ScalarAsync<string>($"SELECT concat_ws('|', \"DeadlineAt\", \"EffectiveDeadlineAt\", \"HardDeadlineAt\", \"PromotionCount\", \"RequestedAt\") FROM \"ChatTurns\" WHERE \"Id\" = '{root.Id}'");

        Assert.True(await leases.RenewAsync(claim, Ct));
        Assert.Equal(before, await database.ScalarAsync<string>($"SELECT concat_ws('|', \"DeadlineAt\", \"EffectiveDeadlineAt\", \"HardDeadlineAt\", \"PromotionCount\", \"RequestedAt\") FROM \"ChatTurns\" WHERE \"Id\" = '{root.Id}'"));   // il rinnovo non sposta nulla

        await Task.Delay(600, Ct);
        Assert.False(await leases.RenewAsync(claim, Ct));                    // scaduto: il vecchio claim non rinnova
        Assert.False(await leases.CompleteAsync(claim, Guid.NewGuid(), Ct)); // né committa
        var release = await leases.ReleaseAfterFailureAsync(claim, new ModelFailure(true, "model_unreachable", null), "x", "x", Ct);
        Assert.Equal((TurnTransition.Failed, "deadline_exceeded"), (release.Outcome, release.ErrorCode));   // nessun retry oltre la deadline corrente

        // Promozione prima della scadenza: il claim vecchio (che «conosce» solo 180 s) prosegue per la nuova deadline.
        var promotedRoot = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { EffectiveSql = "clock_timestamp() + interval '400 milliseconds'" });
        var oldClaim = TurnClaim(promotedRoot);
        await database.ExecuteAsync($"UPDATE \"ChatTurns\" SET \"DeadlineAt\" = \"HardDeadlineAt\", \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" WHERE \"Id\" = '{promotedRoot.Id}'");
        await Task.Delay(600, Ct);
        Assert.True(await leases.RenewAsync(oldClaim, Ct));
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Store_operations_take_the_effective_deadline_as_the_authority_when_the_projection_diverges(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        // Difesa in profondità: anche se la proiezione DeadlineAt divergesse da EffectiveDeadlineAt (CHECK rimosso), vale il minore.
        await database.ExecuteAsync($"ALTER TABLE \"{(kind == RootKind.Turn ? "ChatTurns" : "ProcessingRuns")}\" DROP CONSTRAINT \"CK_{(kind == RootKind.Turn ? "ChatTurns" : "ProcessingRuns")}_DeadlineIsEffective\"");
        var running = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec());
        var queued = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { Status = "Queued", LeaseOwner = null, AttemptCount = 0, Fence = 0 });
        await database.ExecuteAsync($"""
            UPDATE "{running.Table}" SET "EffectiveDeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{running.Id}';
            UPDATE "{queued.Table}" SET "EffectiveDeadlineAt" = clock_timestamp() - interval '1 millisecond' WHERE "Id" = '{queued.Id}';
            """);
        await using var context = database.NewContext();
        var leases = Leases(context);

        Assert.False(kind == RootKind.Turn
            ? await leases.RenewAsync(TurnClaim(running), Ct)
            : await leases.RenewAsync(new AnalysisRunClaim(running.Id, Lease, 3, 1), Ct));
        Assert.Equal(RootCommitState.Stale, await DocumentDeadlineGuard.CommitStateAsync(context, kind == RootKind.Turn, running.Id, Lease, 3, Ct));

        // Il claim non prende in carico un root la cui deadline effettiva è passata; lo sweep del claim lo chiude.
        await database.ExecuteAsync($"UPDATE \"{running.Table}\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now(), \"LeaseOwner\" = NULL, \"LeaseUntil\" = NULL WHERE \"Id\" = '{running.Id}'");
        var work = await leases.ClaimNextWorkAsync("w-new", turns: kind == RootKind.Turn, runs: kind == RootKind.Run, Ct);
        Assert.Null(work);
        Assert.Equal("Failed", await database.ScalarAsync<string>($"SELECT \"Status\" FROM \"{queued.Table}\" WHERE \"Id\" = '{queued.Id}'"));
        Assert.Equal("deadline_exceeded", await database.ScalarAsync<string>($"SELECT \"ErrorCode\" FROM \"{queued.Table}\" WHERE \"Id\" = '{queued.Id}'"));
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Commit_state_is_valid_only_with_owner_fence_lease_and_both_deadlines_open(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind);
        await using var context = database.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        Task<RootCommitState> State(long fence = 3, string owner = Lease) =>
            DocumentDeadlineGuard.CommitStateAsync(context, kind == RootKind.Turn, root.Id, owner, fence, Ct);

        Assert.Equal(RootCommitState.Valid, await State());
        Assert.Equal(RootCommitState.Stale, await State(fence: 2));
        Assert.Equal(RootCommitState.Stale, await State(owner: "worker-b"));
        await transaction.RollbackAsync(Ct);

        await ExpireAsync(database, root);
        Assert.Equal(RootCommitState.Stale, await State());
        await database.ExecuteAsync($"UPDATE \"{root.Table}\" SET \"DeadlineAt\" = \"HardDeadlineAt\", \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" WHERE \"Id\" = '{root.Id}'");
        Assert.Equal(RootCommitState.Valid, await State());   // la promozione riapre l'autorità (stesso fence, stesso lease)
    }

    // ------------------------------------------------------------------ checkpoint nativi

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Checkpoint_writes_of_a_documentary_root_stop_at_the_current_deadline_and_resume_after_promotion(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind);
        var json = JsonDocument.Parse("{}").RootElement;
        var writer = kind == RootKind.Turn
            ? CheckpointWriter.ForChatTurn(root.Id, Lease, 3, "chat-docs-1")
            : new CheckpointWriter(root.Id, Lease, 3, WorkOwnerKind.ProcessingRun, "project-analysis-documents-v2", "1.26.0");
        await using var context = database.NewContext();
        var store = new PostgresCheckpointStore(context, writer);

        await store.CreateCheckpointAsync("s1", json);
        await ExpireAsync(database, root);
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await store.CreateCheckpointAsync("s1", json));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM \"WorkflowCheckpoints\""));
        Assert.Single(await store.RetrieveIndexAsync("s1"));   // la lettura per il resume non richiede autorità

        await database.ExecuteAsync($"UPDATE \"{root.Table}\" SET \"DeadlineAt\" = \"HardDeadlineAt\", \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" WHERE \"Id\" = '{root.Id}'");
        await store.CreateCheckpointAsync("s1", json);
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM \"WorkflowCheckpoints\""));
    }

    [Fact]
    public async Task Legacy_roots_keep_the_001_checkpoint_behaviour_without_a_deadline_guard()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var legacy = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Legacy = true, LegacyDeadlineSql = "now() - interval '1 minute'" });
        await using var context = database.NewContext();
        var store = new PostgresCheckpointStore(context, new CheckpointWriter(legacy.Id, Lease, 3, WorkOwnerKind.ProcessingRun, "project-analysis-v3", "1.26.0"));

        await store.CreateCheckpointAsync("s1", JsonDocument.Parse("{}").RootElement);   // invariato: il ramo 001 non guarda la deadline

        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM \"WorkflowCheckpoints\""));
        // e la guard del lease legacy continua a ragionare sulla sola DeadlineAt
        var authority = await DocumentDeadlineGuard.EvaluateAsync(context, WorkOwnerKind.ProcessingRun, legacy.Id, Lease, 3, lockRow: false, Ct);
        Assert.Equal(DeadlineDenial.DeadlineExceeded, authority.Denial);
    }

    // ------------------------------------------------------------------ riserva del contatore chiamate del root (seam T044 → T118/T047)

    private static CheckpointWriter WriterOf(SeededRoot root, string lease = Lease, long fence = 3) => root.Kind == RootKind.Turn
        ? CheckpointWriter.ForChatTurn(root.Id, lease, fence, "chat-docs-1")
        : new CheckpointWriter(root.Id, lease, fence, WorkOwnerKind.ProcessingRun, "project-analysis-documents-v2", "1.26.0");

    private static readonly BatchRef AnyBatch = new(0, "b1", new string('1', 64));

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Call_reservation_counts_under_the_guard_and_never_resets_the_counter_across_promotion(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { ElapsedSeconds = 100, CallCount = 4 });
        await using var context = database.NewContext();
        var reservation = new RootCallCounterReservation(context, WriterOf(root));
        Task<int> Calls() => database.ScalarAsync<int>($"SELECT \"CallCount\" FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'");

        await reservation.ReserveAsync(AnyBatch, Ct);
        Assert.Equal(5, await Calls());

        // Autorità persa: fence vecchio, lease scaduto, scadenza corrente raggiunta → nessuna riserva, nessun incremento, nessuna sessione.
        await Assert.ThrowsAsync<CheckpointFencedException>(async () =>
            await new RootCallCounterReservation(context, WriterOf(root, fence: 2)).ReserveAsync(AnyBatch, Ct));
        await ExpireAsync(database, root);
        await Assert.ThrowsAsync<CheckpointFencedException>(async () => await reservation.ReserveAsync(AnyBatch, Ct));
        Assert.Equal(5, await Calls());

        // Dopo la promozione (DeadlineAt=EffectiveDeadlineAt=Hard) si riserva di nuovo: il contatore prosegue, non riparte.
        await database.ExecuteAsync($"UPDATE \"{root.Table}\" SET \"DeadlineAt\" = \"HardDeadlineAt\", \"EffectiveDeadlineAt\" = \"HardDeadlineAt\", \"Regime\" = 'Extended', \"PromotionCount\" = 1, \"PromotedAt\" = clock_timestamp(), \"PromotedReason\" = 'additional_work' WHERE \"Id\" = '{root.Id}'");
        await reservation.ReserveAsync(AnyBatch, Ct);
        Assert.Equal(6, await Calls());
    }

    [Theory]
    [InlineData(RootKind.Turn)]
    [InlineData(RootKind.Run)]
    public async Task Call_reservation_refuses_beyond_the_limit_but_allows_exactly_the_maximum(RootKind kind)
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, kind, new RootSpec { ElapsedSeconds = 10, CallCount = 1 });
        await using var context = database.NewContext();
        var reservation = new RootCallCounterReservation(context, WriterOf(root), maxCalls: 2);
        Task<int> Calls() => database.ScalarAsync<int>($"SELECT \"CallCount\" FROM \"{root.Table}\" WHERE \"Id\" = '{root.Id}'");

        await reservation.ReserveAsync(AnyBatch, Ct);   // 2 = massimo ammesso: non è un superamento
        Assert.Equal(2, await Calls());

        var refused = await Assert.ThrowsAsync<DocumentWorkException>(async () => await reservation.ReserveAsync(AnyBatch, Ct));
        Assert.Equal(DocumentWorkException.CallLimitExceeded, refused.Code);
        Assert.Equal(2, await Calls());   // nessun incremento oltre il massimo
    }

    [Fact]
    public async Task Call_reservation_is_not_defined_for_legacy_roots()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var legacy = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Legacy = true });
        await using var context = database.NewContext();

        await Assert.ThrowsAsync<NotSupportedException>(async () => await new RootCallCounterReservation(context, WriterOf(legacy)).ReserveAsync(AnyBatch, Ct));
        Assert.Equal(0L, await database.ScalarAsync<long>($"SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Id\" = '{legacy.Id}' AND \"CallCount\" IS NOT NULL"));
    }

    // ------------------------------------------------------------------ timeout della chiamata

    [Fact]
    public void Call_timeout_is_the_minimum_of_the_configured_timeout_and_every_positive_residual()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DocumentDeadlineGuard.CallTimeout(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(500)));
        Assert.Equal(TimeSpan.FromSeconds(120), DocumentDeadlineGuard.CallTimeout(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(900)));
        Assert.Equal(TimeSpan.FromSeconds(120), DocumentDeadlineGuard.CallTimeout(TimeSpan.FromSeconds(120)));
        Assert.Null(DocumentDeadlineGuard.CallTimeout(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), TimeSpan.Zero));       // scadenza esatta: nessun invio
        Assert.Null(DocumentDeadlineGuard.CallTimeout(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(-1)));
        Assert.Null(DocumentDeadlineGuard.CallTimeout(TimeSpan.Zero, TimeSpan.FromSeconds(30)));
    }
}
