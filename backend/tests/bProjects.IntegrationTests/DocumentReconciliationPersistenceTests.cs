using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using CorpusFact = bProjects.IntegrationTests.DocumentGlobalReconciliationTests.Fact;
using static bProjects.IntegrationTests.DocumentGlobalReconciliationTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T080 (PG + modello FINTO) — persistenza, replay e concorrenza della riconciliazione globale sul corpus T002: tabelle e vincoli di cataloghi/candidati/patch,
/// crash fra i livelli del grafo (foglie, riduzioni, Cross, espansioni, confronti) con ripresa identica, due worker sullo stesso run, output Committed alterato,
/// tetto di chiamate, patch Stale e frontiera non risolta. Invarianti: stessi membership/ID/hash/disposition, nessuna nuova inferenza dopo un output
/// committato, un solo terminale e nessuna revisione completa se restano residui.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentReconciliationPersistenceTests : ReconcileRig
{
    private const string Committed = "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'";

    private static readonly string[] Tables =
    [
        "RunCatalogNodes", "RunCatalogPages", "RunCatalogMembers", "RunCatalogSets", "RunCatalogSetNodes", "RunCandidates", "RunCandidateOrigins", "RunCandidateDispositions",
        "RunPatches", "RunPatchDispositions",
    ];

    private async Task<(Guid Run, AnalysisRunClaim Claim)> ArrangeAsync(List<CorpusFact> corpus)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 0; n < corpus.Count; n++)
        {
            await NewDocument(client, project, $"doc{n}", Statement(corpus[n], n), corpus[n].Subject, corpus[n].Scope);
        }

        var run = await StartRunAsync(client, project);
        return (run, await ClaimAsync(run));
    }

    private static DocumentChatWorkflowTests.FakeChatModel Model(Gate gate, Func<DocumentChatWorkflowTests.ChatRequest, string?>? around = null) =>
        new() { Responder = r => around?.Invoke(r) ?? gate.Respond(r) };

    /// <summary>Firma di ciò che la ripresa deve riprodurre identico: membership, nodi, pagine, candidati, disposition, patch e revisione.</summary>
    private async Task<string> SignatureAsync(Guid run)
    {
        var parts = new List<string>();
        foreach (var query in new[]
        {
            "SELECT coalesce(string_agg(\"NodeId\" || '|' || \"EvidenceId\" || '|' || \"View\" || '|' || \"ContentHash\", ',' ORDER BY \"NodeId\", \"EvidenceId\", \"View\"), '') FROM \"RunCatalogMembers\" WHERE \"RunId\" = @r",
            "SELECT coalesce(string_agg(\"NodeId\", ',' ORDER BY \"NodeId\"), '') FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r",
            "SELECT coalesce(string_agg(\"CandidateId\" || '|' || \"InputHash\", ',' ORDER BY \"CandidateId\"), '') FROM \"RunCandidates\" WHERE \"RunId\" = @r",
            "SELECT coalesce(string_agg(\"CandidateId\" || '|' || \"State\" || '|' || \"ComparisonOutputHashesJson\"::text, ',' ORDER BY \"CandidateId\"), '') FROM \"RunCandidateDispositions\" WHERE \"RunId\" = @r",
            "SELECT coalesce(string_agg(\"PatchId\" || '|' || \"Kind\", ',' ORDER BY \"PatchId\"), '') FROM \"RunPatchDispositions\" WHERE \"RunId\" = @r",
            "SELECT coalesce(string_agg(\"ContentJson\"::text, ',' ORDER BY \"Id\"), '') FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r",
        })
        {
            parts.Add(await Scalar<string>(query, ("r", run)));
        }

        return string.Join('\n', parts);
    }

    // ================================================================ migrazione

    [Fact]
    public async Task The_catalog_candidate_and_patch_tables_exist_with_hash_constraints_and_a_completed_run_fills_them()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);

        var result = await RunAsync(claim, Model(gate));

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        foreach (var table in Tables)
        {
            Assert.Equal(1, await Count("SELECT count(*) FROM information_schema.tables WHERE table_name = @t AND table_schema = current_schema()", ("t", table)));
        }

        Assert.True(await Count("SELECT count(*) FROM pg_constraint WHERE conname = 'CK_RunPatches_Hashes'") == 1);
        Assert.True(await Count("SELECT count(*) FROM pg_constraint WHERE conname = 'CK_RunCandidateOrigins_Hash'") == 1);
        foreach (var table in new[] { "RunCatalogNodes", "RunCatalogPages", "RunCatalogMembers", "RunCatalogSets", "RunCandidates", "RunCandidateOrigins", "RunCandidateDispositions" })
        {
            Assert.True(await Count($"SELECT count(*) FROM \"{table}\" WHERE \"RunId\" = @r", ("r", run)) > 0, table);
        }

        // Un hash non esadecimale è rifiutato dal database, non solo dal codice.
        await Assert.ThrowsAnyAsync<Exception>(() => Execute(
            """
            INSERT INTO "RunPatches" ("RunId","PatchId","ProjectId","SourceHash","BaseHash","TargetKind","TargetId","ExpectedHash","Operation","PayloadJson")
            SELECT "Id", 'zz', "ProjectId", 'zz', 'zz', 'Item', 'x', 'zz', 'AddSupport', '{}'::jsonb FROM "ProcessingRuns" WHERE "Id" = @r
            """,
            ("r", run)));
    }

    // ================================================================ crash e ripresa

    [Theory]
    [InlineData("leaf")]
    [InlineData("reduce")]
    [InlineData("cross")]
    [InlineData("expand")]
    [InlineData("compare")]
    public async Task A_crash_at_every_level_resumes_to_the_same_membership_ids_hashes_and_dispositions_without_resending_committed_batches(string stage)
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var seen = 0;
        var failing = Model(gate, r => Parse(r.AllText).Stage == stage && Interlocked.Increment(ref seen) == 1 ? throw new InvalidOperationException("rete caduta") : null);

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        var committedBefore = await Count(Committed, ("r", run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var committedIds = (await ScalarList("SELECT \"BatchId\" FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", run)).ToHashSet();
        Assert.Equal(committedBefore, committedIds.Count);

        var healthy = Model(gate);
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.DoesNotContain(healthy.Requests.Select(r => Parse(r.AllText).BatchId), committedIds.Contains);   // nessuna nuova inferenza su un output committato
        Assert.Equal(committedBefore + healthy.Requests.Count, await Count(Committed, ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var afterResume = await SignatureAsync(run);

        var again = Model(gate);
        var replay = await RunAsync(claim, again);

        Assert.Empty(again.Requests);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Equal(afterResume, await SignatureAsync(run));   // stessi membership/ID/hash/disposition dopo il replay
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Empty(MissingGoldConflicts(DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run))), Gold(AllGold)));
    }

    [Fact]
    public async Task An_interrupted_and_resumed_run_has_the_same_shape_as_an_uninterrupted_one_over_the_same_corpus()
    {
        var corpus = Corpus();
        var (clean, cleanClaim) = await ArrangeAsync(corpus);
        var cleanGate = new Gate(corpus);
        var cleanModel = Model(cleanGate);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(cleanClaim, cleanModel)).Status);

        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var seen = 0;
        var failing = Model(gate, r => Interlocked.Increment(ref seen) == 5 ? throw new InvalidOperationException("rete caduta") : null);
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        var healthy = Model(gate);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, healthy)).Status);

        // Gli id dipendono dai documenti (evidenze diverse), quindi si confrontano le cardinalità strutturali: nulla è perso né duplicato dalla ripresa.
        foreach (var table in new[] { "RunCatalogNodes", "RunCatalogPages", "RunCatalogMembers", "RunCatalogSets", "RunCandidates", "RunCandidateDispositions" })
        {
            Assert.Equal(await Count($"SELECT count(*) FROM \"{table}\" WHERE \"RunId\" = @r", ("r", clean)), await Count($"SELECT count(*) FROM \"{table}\" WHERE \"RunId\" = @r", ("r", run)));
        }

        Assert.Equal(cleanModel.Requests.Count, 4 + healthy.Requests.Count);
        Assert.Equal(await Count(Committed, ("r", clean)), await Count(Committed, ("r", run)));
    }

    // ================================================================ due worker

    [Fact]
    public async Task Two_workers_on_the_same_run_end_with_one_terminal_one_revision_and_unique_committed_batches()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var (first, second) = (Model(gate), Model(gate));

        var outcomes = await Task.WhenAll(Attempt(claim, first), Attempt(claim, second));

        Assert.Contains(outcomes, o => o?.Status == ProcessingRunStatus.Completed);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        var batches = await ScalarList("SELECT \"BatchId\" FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", run);
        Assert.Equal(batches.Count, batches.Distinct().Count());
        var sent = first.Requests.Concat(second.Requests).Select(r => Parse(r.AllText).BatchId).ToHashSet();
        Assert.Superset(batches.ToHashSet(), sent);   // ogni batch committato è stato inviato da almeno un worker, nessuno è inventato
        var signature = await SignatureAsync(run);

        var again = Model(gate);
        await RunAsync(claim, again);
        Assert.Empty(again.Requests);
        Assert.Equal(signature, await SignatureAsync(run));
    }

    private async Task<AnalysisRunResult?> Attempt(AnalysisRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model)
    {
        try
        {
            return await Task.Run(() => RunAsync(claim, model));
        }
        catch (Exception)
        {
            return null;   // il perdente della corsa non produce un terminale suo
        }
    }

    // ================================================================ hash discordanti

    [Fact]
    public async Task A_committed_output_cannot_be_altered_or_removed_in_the_database()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var seen = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, Model(gate, r => Interlocked.Increment(ref seen) == 4 ? throw new InvalidOperationException("rete caduta") : null)));
        Assert.Equal(3, await Count(Committed, ("r", run)));

        await Assert.ThrowsAnyAsync<Exception>(() => Execute("UPDATE \"WorkOutputs\" SET \"OutputHash\" = repeat('0', 64) WHERE \"OwnerId\" = @r", ("r", run)));
        await Assert.ThrowsAnyAsync<Exception>(() => Execute("DELETE FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(3, await Count(Committed, ("r", run)));
    }

    [Fact]
    public async Task A_worker_whose_output_differs_from_the_one_already_committed_for_the_same_batch_never_overwrites_it_nor_adds_a_second_terminal()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var winner = Model(gate);
        AnalysisRunResult? winnerResult = null;
        var late = false;
        // Il primo worker è già in volo sul primo batch quando il secondo completa tutto il run; poi risponde con un descriptor diverso (hash discordante).
        var slow = Model(gate, r =>
        {
            if (late)
            {
                return null;
            }

            late = true;
            winnerResult = Task.Run(() => RunAsync(claim, winner)).GetAwaiter().GetResult();
            return Respond(r, (p, result) => result with { Descriptor = result.Descriptor is null ? null : "descriptor diverso" });
        });

        AnalysisRunResult? loser = null;
        Exception? failure = null;
        try
        {
            loser = await RunAsync(claim, slow);
        }
        catch (Exception e)
        {
            failure = e;
        }

        Assert.NotNull(winnerResult);
        Assert.Equal(ProcessingRunStatus.Completed, winnerResult!.Status);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.True(loser is null || loser.Status != ProcessingRunStatus.Completed || loser.ResultRevisionId == winnerResult.ResultRevisionId, failure?.Message);
        var batches = await ScalarList("SELECT \"BatchId\" FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", run);
        Assert.Equal(batches.Count, batches.Distinct().Count());
        Assert.Equal(winner.Requests.Count, batches.Count);   // il perdente non ha aggiunto output
    }

    // ================================================================ tetto di chiamate

    [Fact]
    public async Task The_call_cap_stops_before_the_next_send_with_no_revision_and_raising_it_resumes_without_resending_committed_batches()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var capped = Model(gate);

        var stopped = await RunAsync(claim, capped, new DocumentOptions { MaxAiCallsPerRun = 5 });

        Assert.Equal(ProcessingRunStatus.Failed, stopped.Status);
        Assert.Equal(DocumentWorkException.CallLimitExceeded, stopped.ErrorCode);
        Assert.Equal(5, capped.Requests.Count);   // nessun invio oltre il tetto
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(5, await Count(Committed, ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)));
    }

    // ================================================================ patch Stale e frontiera

    [Fact]
    public async Task A_stale_patch_is_kept_but_blocks_completion_and_no_revision_is_written()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var stale = false;
        var gate = new Gate(corpus, tweak: (p, result) =>
        {
            if (stale || p.Stage != "leaf" || p.View != ReconcileView.A || p.Voci.Length == 0)
            {
                return result;
            }

            stale = true;
            var voce = p.Voci[0];
            var patch = new Patch
            {
                PatchId = "pending", BaseHash = new string('b', 64), Operation = PatchOperation.AddSupport, EvidenceIds = [voce.Evidence], SourceIds = [voce.Source], OriginBatchIds = [p.BatchId],
                Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = $"k-{voce.Evidence:N}", ExpectedHash = new string('c', 64) },
                Value = new AddSupportValue { EvidenceIds = [voce.Evidence], SourceIds = [voce.Source] },
            };
            return result with { Patches = [patch with { PatchId = ReconciliationIds.PatchId(patch) }] };
        });
        var model = Model(gate);

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Equal(model.Requests.Count, await Count(Committed, ("r", run)));   // gli output validi restano committati
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunPatches\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_candidate_left_unresolved_by_the_expansion_leaves_an_explicit_frontier_and_the_run_never_completes()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus, tweak: (p, result) => p.Stage == "expand"
            ? result with
            {
                Dispositions = [new CandidateDisposition { CandidateId = p.FocusId!, State = CandidateState.NotProcessed, ChildCandidateIds = [], ComparisonOutputHashes = [], Reason = "candidate_unresolved" }],
            }
            : result);
        var model = Model(gate);

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Contains(result.ErrorCode, new[] { DocumentWorkException.CoverageIncomplete, DocumentWorkException.ReconciliationIrreducible, DocumentWorkException.ReconciliationRoundLimit });
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCandidateDispositions\" WHERE \"RunId\" = @r AND \"State\" = 'Open'", ("r", run)));
        Assert.Equal(model.Requests.Count, await Count(Committed, ("r", run)));
    }

    private async Task<List<string>> ScalarList(string sql, Guid run)
    {
        var count = await Count(sql.Replace("SELECT \"BatchId\"", "SELECT count(*)"), ("r", run));
        var list = new List<string>();
        for (var i = 0; i < count; i++)
        {
            list.Add(await Scalar<string>(sql + $" ORDER BY \"BatchId\" OFFSET {i} LIMIT 1", ("r", run)));
        }

        return list;
    }
}
