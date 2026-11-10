using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T049 (MAF-PG) — DocumentEvidenceAgent: un DocumentJob ExtractUnit percorre il grafo nativo fisso (T043) con il port di produzione (T044)
/// su PostgreSQL reale, SDK MAF 1.26.0 reale, preflight/AgentFactory reali e modello FINTO a livello HTTP (nessun DGX). Si prova: partizione
/// esaustiva delle porzioni oltre la finestra complessiva, una sessione nuova per invio (nessuna history), zero evidenze motivato, errori
/// permanenti (output non valido, unità irriducibile), tetto di chiamate del root (512 inclusi i retry), recupero dopo errore transitorio e
/// timeout residuo. Nessun commit aggregato: l'unità non rende Ready la preparazione.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed partial class DocumentEvidenceAgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ scenari

    [Fact]
    public async Task Portions_beyond_the_whole_window_are_extracted_in_fresh_sessions_one_batch_per_call_and_the_unit_completes_without_ready()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync(Enumerable.Range(0, 12).Select(i => Words(60, $"s{i}x")).ToArray());
        var model = new FakeUnitModel();

        await rig.RunAsync(model);

        // 12 porzioni da 60 parole: tre per batch (180 parole x 8 <= output), quattro no -> 4 batch / 4 invii.
        Assert.Equal(4, model.Requests.Count);
        Assert.Equal(4, await rig.CallCountAsync());
        Assert.Equal(4L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
        // Sessioni nuove: ogni richiesta ha UN solo messaggio utente con i soli range del proprio batch, mai quelli degli altri (nessuna history).
        Assert.All(model.Requests, r => Assert.Equal(1, r.UserMessages));
        Assert.Equal(model.Requests.Count, model.Requests.Select(r => string.Join(',', r.Keys)).Distinct().Count());
        Assert.Equal(12, model.Requests.SelectMany(r => r.Keys).Distinct().Count());   // nessun range inviato due volte
        Assert.Equal(12, model.Requests.SelectMany(r => r.Keys).Count());
        // Il documento oltre la finestra: la somma dei prompt supera la finestra, ma ogni invio sta nel suo budget.
        Assert.True(model.Requests.Sum(r => r.PromptTokens) > FakeUnitModel.Window, "la somma dei payload deve superare la finestra");
        Assert.All(model.Requests, r => Assert.InRange(r.PromptTokens, 1, FakeUnitModel.Window));

        Assert.Equal("Completed", await rig.JobAsync("Status"));
        Assert.NotNull(await rig.JobAsync("ResultHash"));
        Assert.Equal(12L, await rig.Db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentPortions" WHERE "PreparationId" = '{rig.Seed.Preparation}' AND "Status" = 'Completed' AND "ResultHash" IS NOT NULL"""));
        // L'unità non è l'aggregatore: nessuna versione Evidence, preparazione ancora Extracting (mai Ready qui).
        Assert.Equal("Extracting", await rig.Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentPreparations" WHERE "Id" = '{rig.Seed.Preparation}'"""));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "EvidenceExtractionVersions" """));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "Evidences" """));
    }

    [Fact]
    public async Task Output_payloads_carry_server_resolved_sources_with_exact_quotes_and_stable_unit_ordinals()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(40, "aa"), Words(40, "bb")]);

        await rig.RunAsync(new FakeUnitModel());

        var payload = await rig.Db.ScalarAsync<string>("""SELECT "PayloadJson"::text FROM "WorkOutputs" LIMIT 1""");
        var output = JsonSerializer.Deserialize<DocumentUnitOutput>(payload, DocumentEvidenceCodec.Options)!;
        Assert.Equal(2, output.Evidences.Count);
        Assert.Equal(0 * DocumentEvidenceAgent.UnitOrdinalStride + 0, output.UnitOrdinal);
        Assert.Equal("0e1", output.Evidences[0].GlobalKey);
        var source = output.Evidences[0].Content.Sources.Single();
        Assert.Equal(EvidenceSourceKind.DocumentPassage, source.Kind);
        Assert.Equal(EvidenceSourceRole.Primary, source.Role);
        Assert.Equal(0, source.Start);
        Assert.Equal("aa0 aa1 aa2", source.Quote);
        Assert.NotNull(source.BlockId);
        Assert.NotNull(source.ArtifactId);
        Assert.Equal(rig.Seed.Preparation, source.PreparationId);
        var maps = await rig.Db.ScalarAsync<string>("""SELECT "MapsJson"::text FROM "WorkOutputs" LIMIT 1""");
        Assert.Contains(source.SourceId.ToString(), maps);
        var covered = await rig.Db.ScalarAsync<string>("""SELECT "CoveredIdsJson"::text FROM "WorkOutputs" LIMIT 1""");
        Assert.Equal(2, JsonSerializer.Deserialize<string[]>(covered)!.Length);
        var manifest = await rig.Db.ScalarAsync<string>("""SELECT "PlanJson"::text FROM "WorkManifests" LIMIT 1""");
        Assert.Contains("\"regime\": \"Extended\"", manifest);   // la preparazione è sempre Extended: nessuna classificazione né promozione
    }

    [Fact]
    public async Task A_unit_with_no_information_is_zero_evidences_with_a_reason_and_the_reason_is_recorded_on_the_portions()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync(["Pagina 3 di 10"]);
        var model = new FakeUnitModel { Responder = _ => """{"schemaVersion":"evidence-1.1.0","evidences":[],"noEvidenceReason":"Solo numerazione di pagina."}""" };

        await rig.RunAsync(model);

        Assert.Equal("Completed", await rig.JobAsync("Status"));
        Assert.Equal("Solo numerazione di pagina.", await rig.Db.ScalarAsync<string>($"""SELECT "NoEvidenceReason" FROM "DocumentPortions" WHERE "PreparationId" = '{rig.Seed.Preparation}'"""));
        Assert.Equal("Completed", await rig.Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentPortions" WHERE "PreparationId" = '{rig.Seed.Preparation}'"""));
    }

    [Fact]
    public async Task Zero_evidences_without_a_reason_or_with_an_invented_quote_is_a_permanent_error_with_no_output_committed()
    {
        TestEnvironment.RequirePostgres();
        foreach (var bad in new[]
        {
            """{"schemaVersion":"evidence-1.1.0","evidences":[],"noEvidenceReason":null}""",
            """{"schemaVersion":"evidence-1.1.0","evidences":[{"key":"e1","sources":[{"kind":"DocumentPassage","rangeKey":"@KEY","quote":"testo che non esiste","occurrence":1,"role":"Primary","citationGroupKey":"g","part":0}],"kind":"Requirement","kindRationale":null,"statement":"x","subject":null,"action":null,"object":null,"scope":null,"purpose":null,"instrument":null,"channel":null,"recipient":null,"qualifiers":[],"polarity":"Affirmed","certainty":"Declared","relations":[]}],"noEvidenceReason":null}""",
            "{\"schemaVersion\":\"evidence-1.1.0\",\"evidences\":[",   // JSON troncato
        })
        {
            await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
            var model = new FakeUnitModel { Responder = r => bad.Replace("@KEY", r.Keys[0]) };

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

            Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
            Assert.False(ModelFailure.Classify(Unwrap(failure)).IsTransient);
            Assert.DoesNotContain("testo che non esiste", Unwrap(failure).Message);
            Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
            Assert.Equal("Running", await rig.JobAsync("Status"));   // lo stato finale lo decide il dispatcher: l'unità non si completa
            Assert.Equal(1, await rig.CallCountAsync());
        }
    }

    [Fact]
    public async Task An_output_truncated_by_the_length_limit_is_not_accepted_even_if_the_json_looks_closed()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "t")]);
        var model = new FakeUnitModel { FinishReason = _ => "length" };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
    }

    [Fact]
    public async Task An_irreducible_unit_fails_before_any_call_with_an_explicit_incomplete_error_and_without_the_document_text()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(400, "z")]);   // una riga senza confini: 400 parole x 8 > output
        var model = new FakeUnitModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        var classified = ModelFailure.Classify(Unwrap(failure));
        Assert.Equal(DocumentWorkException.DocumentIncomplete, classified.Code);
        Assert.False(classified.IsTransient);
        Assert.DoesNotContain("z100", classified.Detail ?? "");
        Assert.Empty(model.Requests);
        Assert.Equal(0, await rig.CallCountAsync());
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
    }

    [Fact]
    public async Task A_runtime_without_a_verified_count_blocks_the_unit_before_any_request()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "r")]);
        var model = new FakeUnitModel { TokenizerAvailable = false };   // /tokenize e /apply-template assenti, nessuna calibrazione di produzione

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal("runtime_unverified", ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await rig.CallCountAsync());
    }

    // ------------------------------------------------------------------ versione del prompt congelata nel job (T082)

    [Fact]
    public async Task A_job_without_the_prompt_version_replays_as_historic_v2_and_a_new_job_freezes_v3_with_different_manifest_and_batch_identities()
    {
        TestEnvironment.RequirePostgres();
        string[] portions = [Words(50, "p")];
        string historicVersion, historicConfig, historicBatch, currentVersion, currentConfig, currentBatch;

        await using (var historic = await UnitRig.CreateAsync(portions))
        {
            await historic.RunAsync(new FakeUnitModel());
            historicVersion = await historic.Db.ScalarAsync<string>("""SELECT "PromptVersion" FROM "DerivedBatches" LIMIT 1""");
            historicConfig = await historic.Db.ScalarAsync<string>("""SELECT "ConfigHash" FROM "WorkManifests" LIMIT 1""");
            historicBatch = await historic.Db.ScalarAsync<string>("""SELECT "Id"::text FROM "DerivedBatches" LIMIT 1""");
        }

        await using (var current = await UnitRig.CreateAsync(portions, configJson: """{"promptVersion":"document-extraction-v3"}"""))
        {
            await current.RunAsync(new FakeUnitModel());
            currentVersion = await current.Db.ScalarAsync<string>("""SELECT "PromptVersion" FROM "DerivedBatches" LIMIT 1""");
            currentConfig = await current.Db.ScalarAsync<string>("""SELECT "ConfigHash" FROM "WorkManifests" LIMIT 1""");
            currentBatch = await current.Db.ScalarAsync<string>("""SELECT "Id"::text FROM "DerivedBatches" LIMIT 1""");
        }

        Assert.Equal("document-extraction-v2", historicVersion);
        Assert.Equal("document-extraction-v3", currentVersion);
        Assert.NotEqual(historicConfig, currentConfig);
        Assert.NotEqual(historicBatch, currentBatch);
    }

    [Fact]
    public async Task A_job_frozen_with_an_unknown_prompt_version_fails_as_incompatible_before_any_model_call()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(50, "p")], configJson: """{"promptVersion":"document-extraction-v9"}""");
        var model = new FakeUnitModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        var work = Assert.IsType<DocumentWorkException>(Unwrap(failure));
        Assert.Equal(DocumentWorkException.CheckpointIncompatible, work.Code);
        Assert.Empty(model.Requests);
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
    }

    // ------------------------------------------------------------------ tetto chiamate e retry

    [Fact]
    public async Task The_call_ceiling_counts_retries_and_is_frozen_on_the_job_a_failed_send_consumes_a_call_and_the_ceiling_stops_the_next_one()
    {
        TestEnvironment.RequirePostgres();
        // Tre batch (una porzione da 100 parole ciascuno: due non entrano nell'output) e tetto congelato nel job a 3 chiamate.
        await using var rig = await UnitRig.CreateAsync([Words(100, "a"), Words(100, "b"), Words(100, "c")], configJson: """{"MaxAiCallsPerPreparation":3}""");
        var model = new FakeUnitModel { Fail = index => index == 1 ? HttpStatusCode.ServiceUnavailable : null };

        // Tentativo 1: batch 0 ok (chiamata 1), batch 1 rifiutato dal server (chiamata 2, consumata).
        var first = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model, fence: 5, attempt: 1));
        Assert.True(ModelFailure.Classify(Unwrap(first)).IsTransient);
        Assert.Equal(2, await rig.CallCountAsync());

        // Tentativo 2 (nuovo lease): il batch 0 è un hit (nessuna chiamata), il batch 1 è reinferito (chiamata 3), il batch 2 supera il tetto.
        await rig.TakeOverAsync(fence: 6);
        var second = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model, fence: 6, attempt: 2, lease: "worker-b"));

        Assert.Equal(DocumentWorkException.CallLimitExceeded, ModelFailure.Classify(Unwrap(second)).Code);
        Assert.False(ModelFailure.Classify(Unwrap(second)).IsTransient);
        Assert.Equal(3, await rig.CallCountAsync());
        Assert.Equal(3, model.Requests.Count);   // [b0, b1 fallito, b1]: il quarto invio non è mai partito
        Assert.Equal(2L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
        Assert.Equal("Running", await rig.JobAsync("Status"));
    }

    [Fact]
    public async Task Five_hundred_and_twelve_sends_are_allowed_and_the_five_hundred_and_thirteenth_is_refused_with_the_default_ceiling()
    {
        TestEnvironment.RequirePostgres();
        // 513 porzioni da 100 parole: ciascuna è un batch a sé (due non entrano nell'output stimato).
        await using var rig = await UnitRig.CreateAsync(Enumerable.Range(0, 513).Select(i => Words(100, $"n{i}x")).ToArray());
        var model = new FakeUnitModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.CallLimitExceeded, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Equal(512, model.Requests.Count);
        Assert.Equal(512, await rig.CallCountAsync());
        Assert.Equal(512L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
        Assert.Equal("Running", await rig.JobAsync("Status"));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentPortions" WHERE "PreparationId" = '{rig.Seed.Preparation}' AND "Status" = 'Completed'"""));
    }

    [Fact]
    public async Task The_frozen_ceiling_of_the_job_wins_over_the_current_server_configuration_and_the_server_value_is_the_fallback()
    {
        Assert.Equal(3, DocumentEvidenceAgent.FrozenMaxCalls("""{"maxAiCallsPerPreparation":3}""", new DocumentOptions { MaxAiCallsPerPreparation = 512 }));
        Assert.Equal(7, DocumentEvidenceAgent.FrozenMaxCalls(null, new DocumentOptions { MaxAiCallsPerPreparation = 7 }));
        Assert.Equal(7, DocumentEvidenceAgent.FrozenMaxCalls("""{"ParseTimeoutSeconds":300}""", new DocumentOptions { MaxAiCallsPerPreparation = 7 }));
        Assert.Equal(7, DocumentEvidenceAgent.FrozenMaxCalls("""{"MaxAiCallsPerPreparation":0}""", new DocumentOptions { MaxAiCallsPerPreparation = 7 }));
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ dispatcher esistente + agente reale (T049 + T050)

    [Fact]
    public async Task The_existing_dispatcher_claims_and_completes_a_real_unit_without_any_browser_and_records_the_attempt_calls()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(40, "e"), Words(40, "f")], queued: true);
        var model = new FakeUnitModel();

        Assert.True(await rig.DispatchOnceAsync(model));

        Assert.Equal("Completed", await rig.JobAsync("Status"));
        Assert.Single(model.Requests);
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>($"""SELECT count(*) FROM "InferenceCalls" WHERE "DocumentJobId" = '{rig.Job}' AND "Phase" = 'Extraction'"""));
        Assert.Equal("Extracting", await rig.Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentPreparations" WHERE "Id" = '{rig.Seed.Preparation}'"""));
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>($"""SELECT "FencingToken" FROM "DocumentJobs" WHERE "Id" = '{rig.Job}'"""));
        Assert.Equal("", await rig.Db.ScalarAsync<string>($"""SELECT COALESCE("LeaseOwner", '') FROM "DocumentJobs" WHERE "Id" = '{rig.Job}'"""));
    }

    [Fact]
    public async Task An_invalid_output_through_the_dispatcher_fails_the_unit_and_the_preparation_with_the_stable_code_and_keeps_the_failed_call()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "g")], queued: true);
        var model = new FakeUnitModel { Responder = _ => """{"schemaVersion":"evidence-1.1.0","evidences":[],"noEvidenceReason":null}""" };

        Assert.True(await rig.DispatchOnceAsync(model));

        Assert.Equal("Failed", await rig.JobAsync("Status"));
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, await rig.JobAsync("ErrorCode"));
        Assert.Equal("Failed", await rig.Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentPreparations" WHERE "Id" = '{rig.Seed.Preparation}'"""));
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>($"""SELECT count(*) FROM "InferenceCalls" WHERE "DocumentJobId" = '{rig.Job}'"""));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
    }

    [Fact]
    public async Task A_transient_model_error_through_the_dispatcher_requeues_the_unit_and_a_retry_reuses_the_committed_batches()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(100, "a"), Words(100, "b")], queued: true);
        var model = new FakeUnitModel { Fail = index => index == 1 ? HttpStatusCode.ServiceUnavailable : null };

        Assert.True(await rig.DispatchOnceAsync(model));

        Assert.Equal("Queued", await rig.JobAsync("Status"));
        Assert.NotNull(await rig.JobAsync("NextAttemptAt"));
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
        Assert.Equal(2, await rig.CallCountAsync());

        await rig.Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "NextAttemptAt" = now() - interval '1 second' WHERE "Id" = '{rig.Job}'""");
        Assert.True(await rig.DispatchOnceAsync(model));

        Assert.Equal("Completed", await rig.JobAsync("Status"));
        Assert.Equal(3, model.Requests.Count);   // [b0, b1 fallito, b1]: il batch 0 non è reinferito
        Assert.Equal(3, await rig.CallCountAsync());
        Assert.Equal(2L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
        Assert.Equal(2, int.Parse(await rig.JobAsync("AttemptCount")));
    }

    // ------------------------------------------------------------------ timeout (T119) e fence

    [Fact]
    public async Task A_call_is_cancelled_at_the_residual_deadline_of_the_parent_not_at_the_phase_timeout()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "d")]);
        await rig.Db.ExecuteAsync($"""
            UPDATE "DocumentPreparations" SET "EffectiveDeadlineAt" = clock_timestamp() + interval '4 seconds' WHERE "Id" = '{rig.Seed.Preparation}';
            UPDATE "DocumentJobs" SET "DeadlineAt" = clock_timestamp() + interval '4 seconds' WHERE "Id" = '{rig.Job}';
            """);
        var model = new FakeUnitModel { Delay = _ => TimeSpan.FromSeconds(60) };
        var started = DateTime.UtcNow;

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "il timeout residuo (~4 s) deve prevalere su quello della fase (150 s)");
        var classified = ModelFailure.Classify(Unwrap(failure));
        Assert.True(classified.IsTransient);
        Assert.Equal("model_timeout", classified.Code);
        Assert.Equal(1, await rig.CallCountAsync());
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
    }

    [Fact]
    public async Task A_worker_that_lost_the_lease_neither_sends_nor_writes_anything()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "l")]);
        await rig.TakeOverAsync(fence: 9);   // un altro owner ha il fence 9
        var model = new FakeUnitModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model, fence: 5));

        Assert.Contains(Chain(failure), e => e is LeaseLostException);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await rig.CallCountAsync());
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" """));
    }

    // ------------------------------------------------------------------ infrastruttura

    internal static string Words(int count, string prefix) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    internal static IEnumerable<Exception> Chain(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException!)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }

            if (current.InnerException is null)
            {
                yield break;
            }
        }
    }

    /// <summary>La causa applicativa (DocumentWorkException/Runtime*/Timeout) sotto l'eventuale involucro dei workflow.</summary>
    internal static Exception Unwrap(Exception e) =>
        Chain(e).FirstOrDefault(x => x is DocumentWorkException or RuntimeUnverifiedException or TaskCanceledException or LeaseLostException) ?? e;

    internal sealed class UnitRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly ExecutionOptions _execution = new();

        private UnitRig(ParsingDatabase db, SeededParse seed, Guid job, string? configJson)
        {
            Db = db;
            Seed = seed;
            Job = job;
            ConfigJson = configJson;
            _services = new ServiceCollection().AddLogging().AddDbContext<AppDbContext>(o => o.UseNpgsql(db.ConnectionString))
                .AddSingleton<IOptions<ExecutionOptions>>(Options.Create(_execution)).AddScoped<ExecutionLeaseStore>().BuildServiceProvider();
        }

        public ParsingDatabase Db { get; }

        public SeededParse Seed { get; }

        public Guid Job { get; }

        private string? ConfigJson { get; }

        public static async Task<UnitRig> CreateAsync(IReadOnlyList<string> portionTexts, string? configJson = null, bool queued = false)
        {
            var db = await ParsingDatabase.CreateAsync();
            var seed = await db.SeedParseAsync();
            var job = Guid.NewGuid();
            await using (var context = db.NewContext())
            {
                var artifact = new ParsedArtifact
                {
                    Id = Guid.NewGuid(), ProjectId = seed.Project, PreparationId = seed.Preparation, JobId = seed.Job, FencingToken = 1, SchemaVersion = "parsed-1.0.0",
                    StorageKey = "k/" + Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64), ByteLength = 10, ParserConfigHash = new string('b', 64),
                    BlockCount = portionTexts.Count, IssueCount = 0,
                };
                context.ParsedArtifacts.Add(artifact);
                await context.SaveChangesAsync(Ct);
                var blocks = portionTexts.Select((text, i) => new DocumentBlock
                {
                    Id = Guid.NewGuid(), ProjectId = seed.Project, ArtifactId = artifact.Id, Ordinal = i, Ref = $"b{i}", Type = BlockType.Paragraph,
                    CanonicalText = text, TextHash = Hash(text), HeadingPathJson = "[]",
                }).ToList();
                context.DocumentBlocks.AddRange(blocks);
                await context.SaveChangesAsync(Ct);
                var hard = DateTimeOffset.UtcNow.AddHours(1);
                var portions = blocks.Select((block, i) => new DocumentPortion
                {
                    Id = Guid.NewGuid(), ProjectId = seed.Project, PreparationId = seed.Preparation, Ordinal = i, JobId = job,
                    RangesJson = JsonSerializer.Serialize(new[] { new { rangeKey = $"r{i}", blockId = block.Id, blockRef = block.Ref, start = 0, end = block.CanonicalText.Length, textHash = Hash(block.CanonicalText) } }),
                    ContextRangesJson = "[]", TextHash = Hash(block.CanonicalText), Status = PortionStatus.Pending,
                }).ToList();
                context.DocumentJobs.Add(new DocumentJob
                {
                    Id = job, ProjectId = seed.Project, PreparationId = seed.Preparation, Kind = DocumentJobKind.ExtractUnit, UnitId = portions[0].Id,
                    Status = queued ? DocumentJobStatus.Queued : DocumentJobStatus.Running, RequestedBy = seed.User, RequestedAt = DateTimeOffset.UtcNow,
                    StartedAt = queued ? null : DateTimeOffset.UtcNow, DeadlineAt = hard, AttemptCount = queued ? 0 : 1, MaxAttempts = 3,
                    LeaseOwner = queued ? null : "worker-a", LeaseUntil = queued ? null : DateTimeOffset.UtcNow.AddMinutes(30), FencingToken = queued ? 0 : 5, ConfigJson = configJson,
                });
                await context.SaveChangesAsync(Ct);
                context.DocumentPortions.AddRange(portions);
                await context.SaveChangesAsync(Ct);
            }

            await db.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{seed.Preparation}'""");
            return new UnitRig(db, seed, job, configJson);
        }

        public async Task RunAsync(FakeUnitModel model, long fence = 5, int attempt = 1, string lease = "worker-a", Func<Microsoft.Extensions.AI.IChatClient, Microsoft.Extensions.AI.IChatClient>? wrap = null)
        {
            var options = new AiOptions
            {
                Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = FakeUnitModel.Window,
                MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
                ExtractionOutputExpansion = PlannerLimits.DefaultOutputExpansion,   // fixture dimensionata sul fattore 8: questi test verificano meccaniche (tetti, retry), non la calibrazione (ExtractionOutputExpansionTests)
            };
            using var client = new InternalModelClient(options, model);
            var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
            var factory = new AgentFactory(options, wrap is null ? client.ChatClient : wrap(client.ChatClient), (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
            var agent = new DocumentEvidenceAgent(_services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions()));
            await agent.ExecuteAsync(new DocumentUnitClaim(Job, Seed.Preparation, Seed.Project, null, lease, fence, attempt, 3, ConfigJson), Ct);
        }

        /// <summary>Esegue UN ciclo del dispatcher esistente (claim, heartbeat, esecuzione, rilascio) con l'agente reale e il modello finto.</summary>
        public async Task<bool> DispatchOnceAsync(FakeUnitModel model, Func<Microsoft.Extensions.AI.IChatClient, Microsoft.Extensions.AI.IChatClient>? wrap = null)
        {
            var options = new AiOptions
            {
                Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = FakeUnitModel.Window,
                MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
                ExtractionOutputExpansion = PlannerLimits.DefaultOutputExpansion,   // fixture dimensionata sul fattore 8: questi test verificano meccaniche (tetti, retry), non la calibrazione (ExtractionOutputExpansionTests)
            };
            using var client = new InternalModelClient(options, model);
            var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
            var factory = new AgentFactory(options, wrap is null ? client.ChatClient : wrap(client.ChatClient), (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            var agent = new DocumentEvidenceAgent(scopes, factory, client, Options.Create(new DocumentOptions()));
            var dispatcher = new AiDispatchService(scopes, Options.Create(_execution), NullLogger<AiDispatchService>.Instance, unitExecutor: agent);
            return await dispatcher.RunOnceAsync(Ct);
        }

        public Task TakeOverAsync(long fence, string lease = "worker-b") =>
            Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "LeaseOwner" = '{lease}', "FencingToken" = {fence}, "LeaseUntil" = now() + interval '30 minutes' WHERE "Id" = '{Job}'""");

        public Task<int> CallCountAsync() => Db.ScalarAsync<int>($"""SELECT "CallCount" FROM "DocumentPreparations" WHERE "Id" = '{Seed.Preparation}'""");

        public Task<string> JobAsync(string column) => Db.ScalarAsync<string>($"""SELECT "{column}"::text FROM "DocumentJobs" WHERE "Id" = '{Job}'""");

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Db.DisposeAsync();
        }

        private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    internal sealed record ModelRequest(int Index, string UserText, int UserMessages, IReadOnlyList<string> Keys, int PromptTokens);

    /// <summary>Modello finto a livello HTTP (llama.cpp simulato): tokenizer a parole, finestra fissa, risposte valide per default.</summary>
    internal sealed class FakeUnitModel : HttpMessageHandler
    {
        public const int Window = 4000;

        private static readonly Regex Range = new(@"<<<INTERVALLO rangeKey=(\S+) ruolo=(Primary|Context)>>>\n(.*?)\n<<<FINE rangeKey=\1>>>", RegexOptions.Singleline | RegexOptions.Compiled);

        public List<ModelRequest> Requests { get; } = [];

        public bool TokenizerAvailable { get; init; } = true;

        public Func<ModelRequest, string>? Responder { get; init; }

        public Func<int, HttpStatusCode?>? Fail { get; init; }

        public Func<int, TimeSpan>? Delay { get; init; }

        public Func<int, string>? FinishReason { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/v1/models":
                    return Json("""{"object":"list","data":[{"id":"Qwen-A","object":"model","status":{"value":"loaded","args":["llama-server"]}}]}""");
                case "/props":
                    return Json("{\"default_generation_settings\":{\"n_ctx\":" + Window + ",\"params\":{\"n_predict\":-1}},\"total_slots\":1}");
                case "/tokenize" when TokenizerAvailable:
                {
                    using var doc = JsonDocument.Parse(body);
                    var count = Words(doc.RootElement.GetProperty("content").GetString()!);
                    return Json("{\"tokens\":[" + string.Join(',', Enumerable.Range(0, count)) + "]}");
                }

                case "/apply-template" when TokenizerAvailable:
                {
                    using var doc = JsonDocument.Parse(body);
                    var prompt = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => $"[{m.GetProperty("role").GetString()}] {m.GetProperty("content").GetString()}"));
                    return Json(JsonSerializer.Serialize(new { prompt }));
                }

                case "/v1/chat/completions":
                    return await CompleteAsync(body, cancellationToken);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private async Task<HttpResponseMessage> CompleteAsync(string body, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(body);
            var messages = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (Role: m.GetProperty("role").GetString()!, Text: TextOf(m.GetProperty("content")))).ToList();
            var user = messages.Where(m => m.Role == "user").ToList();
            var userText = string.Join("\n", user.Select(m => m.Text));
            var all = Range.Matches(userText).Select(m => (Key: m.Groups[1].Value, Primary: m.Groups[2].Value == "Primary", Text: m.Groups[3].Value)).ToList();
            ModelRequest record;
            lock (Requests)
            {
                record = new ModelRequest(Requests.Count, userText, user.Count, [.. all.Select(r => r.Key)], messages.Sum(m => Words(m.Text)));
                Requests.Add(record);
            }

            if (Delay?.Invoke(record.Index) is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (Fail?.Invoke(record.Index) is { } status)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":{\"message\":\"indisponibile\"}}", Encoding.UTF8, "application/json") };
            }

            var content = Responder?.Invoke(record) ?? DefaultAnswer(all);
            return Json(JsonSerializer.Serialize(new
            {
                id = "x", @object = "chat.completion", created = 1, model = "Qwen-A",
                choices = new[] { new { index = 0, finish_reason = FinishReason?.Invoke(record.Index) ?? "stop", message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
            }));
        }

        /// <summary>Una evidenza per ogni range Primary: citazione = prime tre parole del range (sottostringa esatta).</summary>
        private static string DefaultAnswer(List<(string Key, bool Primary, string Text)> ranges)
        {
            var evidences = ranges.Where(r => r.Primary).Select((r, i) => new
            {
                key = $"e{i + 1}",
                sources = new[] { new { kind = "DocumentPassage", rangeKey = r.Key, quote = string.Join(' ', r.Text.Split(' ').Take(3)), occurrence = 1, role = "Primary", citationGroupKey = $"g{i + 1}", part = 0 } },
                kind = "Requirement", kindRationale = (string?)null, statement = $"Requisito {i + 1}", subject = (string?)null, action = (string?)null, @object = (string?)null,
                scope = (string?)null, purpose = (string?)null, instrument = (string?)null, channel = (string?)null, recipient = (string?)null,
                qualifiers = Array.Empty<object>(), polarity = "Affirmed", certainty = "Declared", relations = Array.Empty<object>(),
            }).ToList();
            return JsonSerializer.Serialize(new { schemaVersion = "evidence-1.1.0", evidences, noEvidenceReason = (string?)null });
        }

        private static string TextOf(JsonElement content) => content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));

        private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
