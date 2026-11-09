using System.Text.Json;
using bProjects.App.AI;
using Npgsql;
using static bProjects.IntegrationTests.DocumentEvidenceAgentTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// Verifiche OFFLINE (modello finto a livello HTTP, PostgreSQL reale, nessun DGX) della diagnostica di estrazione dell'harness (<see cref="ExtractionDiagnosticsRecorder"/>):
/// (1) il wrapper intercetta una risposta finish=length e ne salva il testo; (2) la cattura non altera il comportamento del workflow; (3) un output troncato resta rifiutato
/// (work_output_invalid, nessun commit); (4) nessun dato diagnostico finisce nei dati di lavoro validi; (5) il cleanup del test non elimina i file diagnostici e nessun segreto compare nei file.
/// Riusa UnitRig/FakeUnitModel di <see cref="DocumentEvidenceAgentTests"/>.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ExtractionDiagnosticsCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TruncatedJson = """{"schemaVersion":"evidence-1.1.0","evidences":[{"key":"e1","sources":[{"kind":"DocumentPassage","rangeKey":"r0","quote":"t0 t1 t2"}],"kind":"Requirement"},{"key":"e2","sources":[""";

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "bprojects-diagtest-" + Guid.NewGuid().ToString("N"));

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static Func<string, CancellationToken, Task<int?>> WordMeter() => (text, _) => Task.FromResult<int?>(WordCount(text));

    private static async Task<IReadOnlyList<string>> FinalizeAsync(ExtractionDiagnosticsRecorder recorder, UnitRig rig)
    {
        await using var connection = new NpgsqlConnection(rig.Db.ConnectionString);
        await connection.OpenAsync(Ct);
        return await recorder.FinalizeAsync(connection, WordMeter());
    }

    private static JsonElement Summary(ExtractionDiagnosticsRecorder recorder) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.Directory, "summary.json"))).RootElement;

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------ (1) + (3)

    [Fact]
    public async Task A_truncated_response_is_captured_with_its_full_text_correlated_to_its_batch_and_is_still_rejected_without_any_commit()
    {
        TestEnvironment.RequirePostgres();
        var root = TempRoot();
        try
        {
            var recorder = new ExtractionDiagnosticsRecorder(root, ["test-key"]);
            await using var rig = await UnitRig.CreateAsync([Words(30, "t")]);
            var model = new FakeUnitModel { Responder = _ => TruncatedJson, FinishReason = _ => "length" };

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model, wrap: recorder.Wrap));

            // (3) rifiutato come prima: work_output_invalid, nessun output committato, una sola chiamata consumata.
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
            Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
            Assert.Equal("Running", await rig.JobAsync("Status"));
            Assert.Equal(1, await rig.CallCountAsync());

            // (1) intercettata: finish=length, testo completo su file, SHA-256 del testo catturato.
            var call = Assert.Single(recorder.Calls);
            Assert.Equal("length", call.FinishReason);
            Assert.Equal("Extraction/DocumentUnit", call.Phase);
            Assert.NotNull(call.TextFile);
            Assert.StartsWith(recorder.TextsDirectory, call.TextFile, StringComparison.Ordinal);
            Assert.Equal(TruncatedJson, await File.ReadAllTextAsync(call.TextFile!, Ct));
            Assert.Equal(ExtractionDiagnosticsRecorder.Sha(TruncatedJson), call.OutputSha256);
            Assert.False(call.JsonParsable);
            Assert.Equal(2, call.EvidenceCountApprox);   // e1 ed e2 (JSON non parsabile: conteggio approssimato)
            Assert.NotNull(call.MaxOutputConfigured);

            var lines = await FinalizeAsync(recorder, rig);

            // correlazione esatta con il batch del DB (DocumentJob + ordinale + hash d'ingresso) e con la stima del planner.
            var node = Summary(recorder).GetProperty("calls")[0].GetProperty("correlation");
            Assert.True(node.GetProperty("matched").GetBoolean());
            Assert.Equal(rig.Job, node.GetProperty("jobId").GetGuid());
            Assert.Equal(0, node.GetProperty("batchOrdinal").GetInt32());
            Assert.Equal(await rig.Db.ScalarAsync<string>("""SELECT "BatchId" FROM "DerivedBatches" """), node.GetProperty("batchId").GetString());
            Assert.Equal(await rig.Db.ScalarAsync<string>("""SELECT "InputHash" FROM "DerivedBatches" """), node.GetProperty("inputHash").GetString());
            Assert.False(node.GetProperty("outputCommitted").GetBoolean());
            Assert.Equal(30, node.GetProperty("ownedTokens").GetInt32());
            Assert.Equal((int)Math.Ceiling(30 * 8.0), node.GetProperty("plannerExpectedOutputTokens").GetInt32());
            Assert.Contains(lines, l => l.Contains("finish=length", StringComparison.Ordinal) && l.Contains("atteso=240", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("evidence-1.1.0", StringComparison.Ordinal));   // nessun testo grezzo nel riepilogo di log
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ------------------------------------------------------------------ (2) + (4)

    [Fact]
    public async Task Capturing_does_not_change_the_workflow_and_leaves_no_diagnostic_data_in_the_valid_work_data()
    {
        TestEnvironment.RequirePostgres();
        var root = TempRoot();
        try
        {
            var recorder = new ExtractionDiagnosticsRecorder(root, ["test-key"]);
            var portions = Enumerable.Range(0, 12).Select(i => Words(60, $"s{i}x")).ToArray();

            await using var plain = await UnitRig.CreateAsync(portions);
            var plainModel = new FakeUnitModel();
            await plain.RunAsync(plainModel);

            await using var wrapped = await UnitRig.CreateAsync(portions);
            var wrappedModel = new FakeUnitModel();
            await wrapped.RunAsync(wrappedModel, wrap: recorder.Wrap);

            // (2) stesse chiamate (stessi payload nello stesso ordine) e stessi esiti.
            Assert.Equal(4, wrappedModel.Requests.Count);
            Assert.Equal(plainModel.Requests.Select(r => r.UserText), wrappedModel.Requests.Select(r => r.UserText));
            Assert.Equal(await plain.CallCountAsync(), await wrapped.CallCountAsync());
            Assert.Equal(await plain.JobAsync("Status"), await wrapped.JobAsync("Status"));
            Assert.Equal("Completed", await wrapped.JobAsync("Status"));
            const string Shape = """
                SELECT string_agg(b."Ordinal" || ':' || b."InputHash" || ':' || o."CoveredIdsJson"::text || ':' || o."SchemaVersion" || ':' || o."Status"::text, '|' ORDER BY b."Ordinal")
                FROM "DerivedBatches" b JOIN "WorkOutputs" o ON o."BatchId" = b."BatchId"
                """;
            Assert.Equal(await plain.Db.ScalarAsync<string>(Shape), await wrapped.Db.ScalarAsync<string>(Shape));
            Assert.Equal(await plain.Db.ScalarAsync<string>("""SELECT string_agg("Status"::text, ',' ORDER BY "Ordinal") FROM "DocumentPortions" """), await wrapped.Db.ScalarAsync<string>("""SELECT string_agg("Status"::text, ',' ORDER BY "Ordinal") FROM "DocumentPortions" """));

            // catturate esattamente le quattro chiamate, tutte finish=stop: nessun testo salvato (solo lunghezza e SHA-256).
            Assert.Equal(4, recorder.Calls.Count);
            Assert.All(recorder.Calls, c =>
            {
                Assert.Equal("stop", c.FinishReason);
                Assert.Null(c.TextFile);
                Assert.True(c.JsonParsable);
                Assert.True(c.OutputChars > 0);
                Assert.NotNull(c.OutputSha256);
            });
            Assert.Empty(Directory.EnumerateFiles(recorder.TextsDirectory));

            var lines = await FinalizeAsync(recorder, wrapped);
            var calls = Summary(recorder).GetProperty("calls").EnumerateArray().ToList();
            Assert.Equal([0, 1, 2, 3], calls.Select(c => c.GetProperty("correlation").GetProperty("batchOrdinal").GetInt32()));
            Assert.All(calls, c => Assert.True(c.GetProperty("correlation").GetProperty("outputCommitted").GetBoolean()));
            Assert.Equal(4, lines.Count(l => l.StartsWith("diag call#", StringComparison.Ordinal)));

            // (4) nessun dato diagnostico nei dati di lavoro (output, batch, manifesti, job, chiamate, porzioni, checkpoint).
            foreach (var table in new[] { "WorkOutputs", "DerivedBatches", "WorkManifests", "DocumentJobs", "DocumentPortions", "InferenceCalls", "DocumentPreparations" })
            {
                var dump = await wrapped.Db.ScalarAsync<string>($"""SELECT COALESCE(string_agg(to_jsonb(t)::text, ' '), '') FROM "{table}" t""");
                Assert.DoesNotContain(recorder.RunId, dump, StringComparison.Ordinal);
                Assert.DoesNotContain(root, dump, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ExtractionDiagnostics", dump, StringComparison.Ordinal);
                Assert.All(recorder.Calls, c =>
                {
                    Assert.DoesNotContain(c.PromptSha256, dump, StringComparison.Ordinal);
                    Assert.DoesNotContain(c.OutputSha256!, dump, StringComparison.Ordinal);
                });
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ------------------------------------------------------------------ correlazione con InferenceCalls (dispatcher esistente)

    [Fact]
    public async Task Through_the_dispatcher_a_truncated_call_is_linked_to_its_inference_call_row_and_attempt()
    {
        TestEnvironment.RequirePostgres();
        var root = TempRoot();
        try
        {
            var recorder = new ExtractionDiagnosticsRecorder(root, ["test-key"]);
            await using var rig = await UnitRig.CreateAsync([Words(100, "a"), Words(100, "b")], queued: true);
            var model = new FakeUnitModel { FinishReason = index => index == 1 ? "length" : "stop" };

            Assert.True(await rig.DispatchOnceAsync(model, recorder.Wrap));

            Assert.Equal("Failed", await rig.JobAsync("Status"));
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, await rig.JobAsync("ErrorCode"));
            Assert.Equal(2, recorder.Calls.Count);

            await FinalizeAsync(recorder, rig);
            var calls = Summary(recorder).GetProperty("calls").EnumerateArray().ToList();
            var truncated = calls.Single(c => c.GetProperty("finishReason").GetString() == "length");
            var correlation = truncated.GetProperty("correlation");
            Assert.Equal(1, correlation.GetProperty("batchOrdinal").GetInt32());
            Assert.False(correlation.GetProperty("outputCommitted").GetBoolean());
            var row = correlation.GetProperty("inferenceCall");
            Assert.Equal(await rig.Db.ScalarAsync<string>($"""SELECT "Id"::text FROM "InferenceCalls" WHERE "DocumentJobId" = '{rig.Job}' AND "FinishReason" = 'length'"""), row.GetProperty("id").GetString());
            Assert.Equal(1, row.GetProperty("attempt").GetInt32());
            Assert.Equal("length", row.GetProperty("finishReason").GetString());
            Assert.True(calls.Single(c => c.GetProperty("finishReason").GetString() == "stop").GetProperty("correlation").GetProperty("outputCommitted").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ------------------------------------------------------------------ (5)

    [Fact]
    public async Task Diagnostic_files_survive_the_test_cleanup_and_contain_no_secret_nor_prompt_text()
    {
        TestEnvironment.RequirePostgres();
        var root = TempRoot();
        const string Password = "hunter2-db-password";
        try
        {
            var recorder = new ExtractionDiagnosticsRecorder(root, ["test-key", Password]);
            var leaking = $"{{\"note\":\"Authorization: Bearer abcdefgh12345678 key=test-key Host=x;Password={Password};Database=d\",\"evidences\":[";
            var directory = recorder.Directory;
            await using (var rig = await UnitRig.CreateAsync([Words(30, "docword")]))
            {
                var model = new FakeUnitModel { Responder = _ => leaking, FinishReason = _ => "length" };
                await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model, wrap: recorder.Wrap));
                await FinalizeAsync(recorder, rig);
            }

            // Il database di test e stato eliminato (DisposeAsync del rig): i file diagnostici, fuori dal DB e dalle sandbox di cleanup, restano.
            Assert.True(File.Exists(Path.Combine(directory, "summary.json")));
            Assert.True(File.Exists(Path.Combine(directory, "calls.jsonl")));
            var text = Assert.Single(Directory.EnumerateFiles(recorder.TextsDirectory, "*-finish-length.txt"));
            Assert.Contains("[REDACTED]", await File.ReadAllTextAsync(text, Ct));

            // Nessun segreto in nessun file; il prompt (testo del documento) non e mai scritto; nessuna intestazione di autenticazione.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var content = await File.ReadAllTextAsync(file, Ct);
                Assert.DoesNotContain("test-key", content, StringComparison.Ordinal);
                Assert.DoesNotContain(Password, content, StringComparison.Ordinal);
                Assert.DoesNotContain("abcdefgh12345678", content, StringComparison.Ordinal);
                Assert.DoesNotContain("docword", content, StringComparison.Ordinal);
                Assert.DoesNotContain("Authorization", content.Replace("Authorization: Bearer [REDACTED]", ""), StringComparison.Ordinal);
            }

            // La radice predefinita e fuori da repository, database e sandbox che i test eliminano.
            var defaultRoot = ExtractionDiagnosticsRecorder.DefaultRoot;
            Assert.Equal(Path.Combine(Path.GetTempPath(), "bprojects-i5-real", "diag"), defaultRoot);
            Assert.DoesNotContain(AcceptanceFixtureTests.RepoRoot(), defaultRoot, StringComparison.OrdinalIgnoreCase);
            foreach (var sandbox in new[] { "bprojects-archive-", "bprojects-parse-", "bprojects-store-", "bprojects-work-", "bprojects-corpus-test-" })
            {
                Assert.DoesNotContain(sandbox, defaultRoot, StringComparison.Ordinal);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }
}
