using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;

namespace bProjects.IntegrationTests;

/// <summary>
/// T064 — telemetria documentale con PostgreSQL reale e righe seminate (nessun modello, nessun DGX): aggregati per unità/tentativi senza
/// doppio conteggio dei figli, durata totale autorevole dal database, intervalli osservati e unavailable dopo crash, nessuna certificazione
/// del target N, output di trasporto redatto, eventi dello stream e monitor del contesto con chiamate di un lavoro documentale.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentTelemetryTests
{
    private const string EmptyJson = "{}";
    private const string Base = "'2026-01-01T00:00:00Z'::timestamptz";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Secrets => "http://dgx.interno:8080/v1 Bearer sk-segretissimo prompt: DOCUMENTO RISERVATO";

    /// <summary>Riga di InferenceCalls con l'inizio espresso in millisecondi dopo una base fissa (i test non dipendono dall'orologio).</summary>
    private static string Call(
        Guid project, string ownerColumn, Guid owner, int attempt, long fence, long startMs, long? durationMs, string state = "Completed",
        bool final = true, string phase = "Extraction", string model = "modello-A", int? prompt = 100, int? completion = 20, string finishReason = "stop")
    {
        var total = prompt is { } p && completion is { } c ? (p + c).ToString() : "NULL";
        return $"""
            INSERT INTO "InferenceCalls" ("Id","ProjectId","{ownerColumn}","Attempt","FencingToken","Phase","ModelId","StartedAt","DurationMs","ReasoningRequested","ReasoningEffective",
                "PromptTokens","CompletionTokens","TotalTokens","FinishReason","State","IsFinal","Config")
            VALUES ('{Guid.NewGuid()}','{project}','{owner}',{attempt},{fence},'{phase}','{model}',{Base} + interval '{startMs} milliseconds',{durationMs?.ToString() ?? "NULL"},false,'off',
                {prompt?.ToString() ?? "NULL"},{completion?.ToString() ?? "NULL"},{total},'{finishReason}','{state}',{(final ? "true" : "false")},'{EmptyJson}')
            """;
    }

    private static async Task<(SeededParse Parse, Guid JobA, Guid JobB)> PreparationWithUnitsAsync(ParsingDatabase database)
    {
        var parse = await database.SeedParseAsync();
        await using var db = database.NewContext();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var (id, status) in new[] { (a, DocumentJobStatus.Completed), (b, DocumentJobStatus.Running) })
        {
            db.DocumentJobs.Add(new DocumentJob
            {
                Id = id, ProjectId = parse.Project, PreparationId = parse.Preparation, Kind = DocumentJobKind.ExtractUnit, UnitId = Guid.NewGuid(), Status = status,
                RequestedBy = parse.User, RequestedAt = DateTimeOffset.UtcNow, DeadlineAt = DateTimeOffset.UtcNow.AddHours(1), MaxAttempts = 3,
                ErrorMessage = id == a ? Secrets : null, ResultHash = status == DocumentJobStatus.Completed ? new string('a', 64) : null,
            });
        }

        await db.SaveChangesAsync(Ct);
        return (parse, a, b);
    }

    private static async Task<RootTelemetryView> PreparationViewAsync(ParsingDatabase database, SeededParse parse)
    {
        await using var db = database.NewContext();
        return (await ExecutionTelemetry.ForRootAsync(db, parse.Project, ClassificationOwnerKind.DocumentPreparation, parse.Preparation, Ct))!;
    }

    // ---------------------------------------------------------------- aggregati senza doppio conteggio

    [Fact]
    public async Task Parallel_children_are_counted_once_and_their_overlapping_intervals_are_united()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (parse, jobA, jobB) = await PreparationWithUnitsAsync(database);

        // Unità A: primo tentativo fallito a -5..-2 s, poi tentativo vincente 0..10 s. Unità B (in parallelo): 5..15 s.
        await database.ExecuteAsync(Call(parse.Project, "DocumentJobId", jobA, 1, 1, -5000, 3000, state: "Failed", final: false, finishReason: "error"));
        await database.ExecuteAsync(Call(parse.Project, "DocumentJobId", jobA, 2, 2, 0, 10000));
        await database.ExecuteAsync(Call(parse.Project, "DocumentJobId", jobB, 1, 1, 5000, 10000, model: "modello-B"));

        var view = await PreparationViewAsync(database, parse);

        // Solo le chiamate dei tentativi vincenti: 2, non 3 né 4 (nessuna somma figli + padre).
        Assert.Equal(2, view.Metrics.Calls);
        Assert.Equal(200, view.Metrics.PromptTokens.Value);
        Assert.Equal("calculated", view.Metrics.PromptTokens.Source);
        Assert.Equal(2, view.Metrics.Units.Count);
        Assert.All(view.Metrics.Units, u => Assert.Equal(1, u.Calls));
        Assert.Equal(["modello-A", "modello-B"], view.Metrics.Units.Select(u => u.ModelId).Order());
        Assert.All(view.Metrics.Units, u => Assert.Equal("extraction", u.Phase));

        // Unione temporale: [-5,-2] + [0,15] = 3 s + 15 s, non 3 + 10 + 10.
        Assert.Equal(18000, view.Execution.ProcessingMs);
        Assert.Null(view.Execution.QueueMs); // nessun intervallo di attesa persistito: unavailable, mai zero inventato

        // Il tentativo fallito resta diagnostica distinta e non entra nelle metriche del vincente.
        var failed = Assert.Single(view.Metrics.FailedAttempts);
        Assert.Equal((jobA, 1, "failed", "error"), (failed.JobId, failed.Attempt, failed.State, failed.FinishReason));

        Assert.Equal((2, 1), (view.Execution.PlannedUnits, view.Execution.CompletedUnits));
        Assert.Equal(["modello-A", "modello-B"], view.Metrics.ModelIds.Order());
    }

    [Fact]
    public async Task Missing_details_stay_null_and_never_become_zero_or_invented_active_time()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (parse, jobA, jobB) = await PreparationWithUnitsAsync(database);

        // Chiamata interrotta da un crash: nessuna durata né token. La chiamata del job B ha tutto.
        await database.ExecuteAsync(Call(parse.Project, "DocumentJobId", jobA, 1, 1, 0, null, state: "Interrupted", final: false, prompt: null, completion: null));
        await database.ExecuteAsync(Call(parse.Project, "DocumentJobId", jobB, 1, 1, 1000, 4000, prompt: null, completion: null));

        var view = await PreparationViewAsync(database, parse);

        Assert.Null(view.Execution.ProcessingMs); // un intervallo non osservabile rende il totale indisponibile
        Assert.Null(view.Execution.QueueMs);
        var unit = Assert.Single(view.Metrics.Units);
        Assert.Null(unit.PromptTokens.Value);
        Assert.Equal("unavailable", unit.PromptTokens.Source);
        Assert.Equal("unavailable", view.Metrics.TotalTokens.Source);
        var interrupted = Assert.Single(view.Metrics.FailedAttempts);
        Assert.Null(interrupted.DurationMs);
        Assert.Equal("interrupted", interrupted.State);
        Assert.True(view.Execution.ElapsedMs > 0); // il tempo totale dal t0 non dipende dai dettagli
    }

    // ---------------------------------------------------------------- tempo totale autorevole, N non certificato

    [Fact]
    public async Task Total_elapsed_comes_from_the_database_timestamps_even_when_details_are_unavailable()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Run, new RootSpec { Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Extended, ElapsedSeconds = 400, Status = "Failed", LeaseOwner = null });
        await database.ExecuteAsync(Call(root.Project, "ProcessingRunId", root.Id, 1, 3, 0, null, state: "Interrupted", final: false, phase: "Analysis", prompt: null, completion: null));

        await using var db = database.NewContext();
        var view = (await ExecutionTelemetry.ForRootAsync(db, root.Project, ClassificationOwnerKind.ProcessingRun, root.Id, Ct))!;

        // t0 spostato di 400 s sul clock del database; il commit è avvenuto all'inserimento: la durata è CompletedAt - RequestedAt.
        Assert.InRange(view.Execution.ElapsedMs, 399_000, 404_000);
        Assert.Null(view.Execution.ProcessingMs);
        Assert.Equal("Extended", view.Execution.Regime);

        // Un root non terminale usa il clock del database «ora»: cresce fra due letture.
        var running = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { ElapsedSeconds = 30 });
        var first = (await ExecutionTelemetry.ForRootAsync(db, running.Project, ClassificationOwnerKind.ChatTurn, running.Id, Ct))!;
        await Task.Delay(50, Ct);
        var second = (await ExecutionTelemetry.ForRootAsync(db, running.Project, ClassificationOwnerKind.ChatTurn, running.Id, Ct))!;
        Assert.InRange(first.Execution.ElapsedMs, 30_000, 34_000);
        Assert.True(second.Execution.ElapsedMs > first.Execution.ElapsedMs);
    }

    [Fact]
    public async Task A_late_completed_root_reports_real_duration_and_never_certifies_the_180_second_target()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec
        {
            Regime = ExecutionRegime.Extended, InitialRegime = ExecutionRegime.Ordinary, PromotionCount = 1, ElapsedSeconds = 190,
        });
        await database.ExecuteAsync($"""UPDATE "ChatTurns" SET "Status" = 'Completed', "CompletedAt" = clock_timestamp(), "ResultMessageId" = "TriggerMessageId", "LeaseOwner" = NULL, "LeaseUntil" = NULL WHERE "Id" = '{root.Id}'""");

        await using var db = database.NewContext();
        var view = (await ExecutionTelemetry.ForRootAsync(db, root.Project, ClassificationOwnerKind.ChatTurn, root.Id, Ct))!;

        Assert.InRange(view.Execution.ElapsedMs, 190_000, 194_000);
        Assert.Equal(("Ordinary", "Extended", "additional_work"), (view.Execution.InitialRegime, view.Execution.Regime, view.Execution.PromotionReason));
        Assert.NotNull(view.Execution.PromotedAt);

        // Nessuna proprietà del contratto affermi il rispetto di un target temporale: la durata è l'unica informazione.
        var names = typeof(ExecutionView).GetProperties().Concat(typeof(RootTelemetryView).GetProperties()).Select(p => p.Name)
            .Concat(typeof(RootMetricsView).GetProperties().Select(p => p.Name)).ToList();
        string[] forbidden = ["Target", "Nominal", "Certif", "Within", "Respect", "Complian", "Sla"];
        Assert.DoesNotContain(names, n => forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Promotion_receipt_hash_and_classification_hash_are_exposed_without_the_receipt_body()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var root = await DocumentRegimeRoots.SeedAsync(database, RootKind.Turn, new RootSpec { Regime = ExecutionRegime.Extended, PromotionCount = 1, ElapsedSeconds = 60 });
        var plan = new string('7', 64);
        var receipt = "{\"corpo\":\"" + Secrets.Replace("'", "") + "\"}";
        await database.ExecuteAsync($"""
            INSERT INTO "RegimeTransitions" ("Id","ProjectId","RootKind","RootId","FromRegime","ToRegime","At","Reason","OldDeadlineAt","NewDeadlineAt","ElapsedMs","PolicyHash","Fence","ReceiptJson","DerivedPlanHash")
            VALUES ('{Guid.NewGuid()}','{root.Project}','ChatTurn','{root.Id}','Ordinary','Extended',clock_timestamp(),'additional_work',now(),now() + interval '1 hour',60000,'{DocumentRegimeRoots.PolicyHash}',3,
                '{receipt}','{plan}')
            """);

        await using var db = database.NewContext();
        var view = (await ExecutionTelemetry.ForRootAsync(db, root.Project, ClassificationOwnerKind.ChatTurn, root.Id, Ct))!;

        Assert.Equal(plan, view.Execution.PromotionReceiptHash);
        Assert.Equal("additional_work", view.Execution.PromotionReason);
        var json = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("sk-segretissimo", json);
        Assert.DoesNotContain("corpo", json);
    }

    // ---------------------------------------------------------------- redazione

    [Fact]
    public async Task Transport_output_credentials_prompts_and_documents_never_reach_the_view()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (parse, jobA, _) = await PreparationWithUnitsAsync(database); // il job A porta un ErrorMessage con endpoint, credenziale e testo del documento
        await database.ExecuteAsync(Call(parse.Project, "DocumentJobId", jobA, 1, 1, 0, 1000, state: "Failed", final: false, finishReason: "error"));

        var view = await PreparationViewAsync(database, parse);
        var json = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var forbidden in new[] { "dgx.interno", "sk-segretissimo", "Bearer", "RISERVATO", "prompt:" })
        {
            Assert.DoesNotContain(forbidden, json);
        }

        Assert.DoesNotContain("reasoning", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thinking", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("errorMessage", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_or_foreign_roots_have_no_telemetry()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var (parse, _, _) = await PreparationWithUnitsAsync(database);
        await using var db = database.NewContext();

        Assert.Null(await ExecutionTelemetry.ForRootAsync(db, parse.Project, ClassificationOwnerKind.DocumentPreparation, Guid.NewGuid(), Ct));
        Assert.Null(await ExecutionTelemetry.ForRootAsync(db, Guid.NewGuid(), ClassificationOwnerKind.DocumentPreparation, parse.Preparation, Ct));
    }

    // ---------------------------------------------------------------- unione degli intervalli (pura)

    [Fact]
    public void Interval_union_never_double_counts()
    {
        Assert.Equal(15, ExecutionTelemetry.UnionLength([(0, 10), (5, 15)]));            // sovrapposti
        Assert.Equal(20, ExecutionTelemetry.UnionLength([(10, 20), (0, 10)]));           // adiacenti, fuori ordine
        Assert.Equal(25, ExecutionTelemetry.UnionLength([(0, 10), (20, 30), (40, 45)])); // disgiunti
        Assert.Equal(100, ExecutionTelemetry.UnionLength([(0, 100), (10, 20), (30, 40)])); // contenuti
        Assert.Equal(0, ExecutionTelemetry.UnionLength([]));
    }

    // ---------------------------------------------------------------- eventi dello stream

    [Fact]
    public void Regime_change_event_carries_only_root_time_reason_deadline_and_receipt_reference()
    {
        var hub = new ProjectEventHub();
        var project = Guid.NewGuid();
        using var subscription = hub.Subscribe(project);
        var transition = new RegimeTransition
        {
            Id = Guid.NewGuid(), ProjectId = project, RootKind = WorkOwnerKind.ChatTurn, RootId = Guid.NewGuid(), FromRegime = ExecutionRegime.Ordinary, ToRegime = ExecutionRegime.Extended,
            At = DateTimeOffset.UtcNow, Reason = "additional_work", OldDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(3), NewDeadlineAt = DateTimeOffset.UtcNow.AddHours(1),
            PolicyHash = new string('c', 64), ReceiptJson = $$"""{"corpo":"{{Secrets.Replace("\"", "")}}"}""", DerivedPlanHash = new string('9', 64),
        };

        hub.PublishRegimeChanged(transition);

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal("execution.regime-changed", evt.Name);
        using var json = JsonDocument.Parse(evt.Data);
        Assert.Equal(["at", "effectiveDeadlineAt", "reason", "receiptHash", "rootId"], json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(transition.RootId, json.RootElement.GetProperty("rootId").GetGuid());
        Assert.Equal("additional_work", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(transition.NewDeadlineAt, json.RootElement.GetProperty("effectiveDeadlineAt").GetDateTimeOffset());
        Assert.DoesNotContain("segretissimo", evt.Data);
    }

    [Fact]
    public void Document_state_event_carries_only_identifiers_and_state()
    {
        var hub = new ProjectEventHub();
        var project = Guid.NewGuid();
        using var subscription = hub.Subscribe(project);
        var (job, version, preparation) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        hub.PublishDocumentState(project, job, version, preparation, "Extracting");

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal("document.state", evt.Name);
        using var json = JsonDocument.Parse(evt.Data);
        Assert.Equal(["jobId", "preparationId", "state", "versionId"], json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Extracting", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(preparation, json.RootElement.GetProperty("preparationId").GetGuid());
    }

    // ---------------------------------------------------------------- monitor del contesto con chiamate documentali

    [Fact]
    public async Task Context_usage_accepts_a_document_job_final_call_and_labels_document_sources()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        await using var host = await MetricsHost.StartAsync(database.ConnectionString, new CannedLlamaServer(), MetricsHost.CannedOptions(), chat: false, formal: false);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var user = Guid.Parse((await database.ScalarAsync<string>($"""SELECT "CreatedBy"::text FROM "Projects" WHERE "Id" = '{project}'""")));
        var parse = await database.SeedParseAsync(owner: (user, project));
        var job = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT INTO "DocumentJobs" ("Id","ProjectId","PreparationId","Kind","UnitId","Status","RequestedBy","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","FencingToken")
            VALUES ('{job}','{project}','{parse.Preparation}','ExtractUnit','{Guid.NewGuid()}','Running','{user}',now(),now() + interval '1 hour',1,3,1)
            """);
        const string composition = "{\"items\":[{\"key\":\"document_sources\",\"tokens\":900,\"messageCount\":3}]}";
        const string config = "{\"phaseMaxOutputTokens\":512,\"safetyMarginTokens\":256}";
        await database.ExecuteAsync($"""
            INSERT INTO "InferenceCalls" ("Id","ProjectId","DocumentJobId","Attempt","FencingToken","Phase","ModelId","StartedAt","DurationMs","ContextWindowTokens","ReasoningRequested","ReasoningEffective",
                "PromptTokens","CompletionTokens","TotalTokens","FinishReason","State","IsFinal","Composition","Config")
            VALUES ('{Guid.NewGuid()}','{project}','{job}',1,1,'Document','modello-A',{Base},2000,32768,false,'off',1000,50,1050,'stop','Completed',true,
                '{composition}','{config}')
            """);

        var usage = await MetricsHost.ContextUsageAsync(pm, project);

        var last = usage.GetProperty("last");
        Assert.Equal(job.ToString(), last.GetProperty("refId").GetString());
        Assert.Equal("document", last.GetProperty("kind").GetString());
        var item = usage.GetProperty("composition").GetProperty("items").EnumerateArray().Single();
        Assert.Equal("document_sources", item.GetProperty("key").GetString());
        Assert.Equal("Fonti documentali", item.GetProperty("label").GetString());
        Assert.Equal(900, item.GetProperty("tokens").GetProperty("value").GetInt32());
    }
}
