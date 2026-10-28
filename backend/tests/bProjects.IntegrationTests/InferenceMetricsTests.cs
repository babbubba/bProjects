using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// Metriche di inferenza reali e monitor del contesto con PostgreSQL reale e un server llama.cpp FINTO (<see cref="CannedLlamaServer"/>,
/// risposte canned con la forma osservata sul runtime reale): cattura per chiamata dietro il vero client, usage dell'ultimo chunk in
/// streaming, composizione con il tokenizer, compattazione, persistenza con lease/fencing, retry, API e isolamento per progetto.
/// Non sostituisce la prova MafReal sul DGX (<see cref="InferenceMetricsMafTests"/>).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class InferenceMetricsTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private string Connection
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _database.ConnectionString;
        }
    }

    // ---------------------------------------------------------------- chat in streaming

    [Fact]
    public async Task Streaming_chat_records_the_usage_of_the_final_chunk_and_exposes_it_on_the_assistant_message()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        var turn = await MetricsHost.ChatAsync(pm, project, "Voglio un portale clienti per le pratiche.");
        Assert.Equal("Completed", turn.GetProperty("status").GetString());

        // L'SDK chiede da solo l'usage in streaming: nessun adattamento del client è necessario.
        var request = Assert.Single(server.Requests);
        Assert.True(request.Stream);
        Assert.True(request.IncludeUsage);

        var row = Assert.Single(await host.CallsAsync(turnId: turn.GetProperty("id").GetGuid()));
        Assert.True(row.IsFinal);
        Assert.Equal(AiPhase.Chat, row.Phase);
        Assert.Equal(InferenceCallState.Completed, row.State);
        Assert.Equal(CannedLlamaServer.ModelId, row.ModelId);
        Assert.Equal(32768, row.ContextWindowTokens);
        Assert.Equal(host.Options.ForPhase(AiPhase.Chat).MaxOutputTokens, row.MaxOutputTokensRequested);
        Assert.Null(row.ServerDefaultNPredict); // -1 = nessun limite: non è un numero
        Assert.Equal(1, row.Attempt);
        Assert.Equal("stop", row.FinishReason);
        Assert.Equal(7, row.CachedTokens);
        Assert.Equal(row.PromptTokens + row.CompletionTokens, row.TotalTokens);

        var metrics = (await MetricsHost.AssistantAsync(pm, project, row.ChatTurnId!.Value)).GetProperty("metrics");
        Assert.Equal("completed", metrics.GetProperty("state").GetString());
        Assert.Equal("chat", metrics.GetProperty("phase").GetString());
        Assert.Equal(CannedLlamaServer.ModelId, metrics.GetProperty("modelId").GetString());
        MetricsHost.AssertToken(metrics.GetProperty("contextWindowTokens"), 32768, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("promptTokens"), row.PromptTokens, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("completionTokens"), row.CompletionTokens, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("totalTokens"), row.TotalTokens, "measured");
        MetricsHost.AssertToken(metrics.GetProperty("cachedTokens"), 7, "measured");
        Assert.Equal(row.MaxOutputTokensRequested, metrics.GetProperty("maxOutputTokens").GetProperty("value").GetInt32());
        Assert.False(metrics.GetProperty("maxOutputTokens").GetProperty("confirmed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("serverDefaultNPredict").ValueKind);
        Assert.False(metrics.GetProperty("reasoning").GetProperty("requested").GetBoolean());
        Assert.Equal("off", metrics.GetProperty("reasoning").GetProperty("effective").GetString());
        Assert.Equal("stop", metrics.GetProperty("finishReason").GetString());
        Assert.Equal(1, metrics.GetProperty("calls").GetInt32());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("compaction").ValueKind);

        // I messaggi PM non hanno metriche.
        var page = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/messages", Ct);
        var user = page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal(JsonValueKind.Null, user.GetProperty("metrics").ValueKind);
    }

    [Fact]
    public async Task The_composition_is_counted_with_the_tokenizer_and_the_template_overhead_stays_unattributed()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        const string first = "Il portale si chiama Aurora e serve agli agenti.";
        const string second = "Le notifiche SMS sono escluse dal primo rilascio.";

        await MetricsHost.ChatAsync(pm, project, first);
        var turn = await MetricsHost.ChatAsync(pm, project, second);
        var usage = await MetricsHost.ContextUsageAsync(pm, project);

        var composition = usage.GetProperty("composition");
        var items = composition.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!);
        Assert.Equal(["assistant_messages", "system_instructions", "user_messages"], items.Keys.Order());
        MetricsHost.AssertToken(items["system_instructions"].GetProperty("tokens"), CannedLlamaServer.Tokens(ConversationalAgent.Instructions), "measured");
        Assert.Equal(JsonValueKind.Null, items["system_instructions"].GetProperty("messageCount").ValueKind);
        MetricsHost.AssertToken(items["user_messages"].GetProperty("tokens"), CannedLlamaServer.Tokens(first) + CannedLlamaServer.Tokens(second), "measured");
        Assert.Equal(2, items["user_messages"].GetProperty("messageCount").GetInt32());
        MetricsHost.AssertToken(items["assistant_messages"].GetProperty("tokens"), CannedLlamaServer.Tokens(server.ChatReply), "measured");
        Assert.Equal("Messaggi del PM", items["user_messages"].GetProperty("label").GetString());

        // Prompt del runtime = testi attribuiti + template (4 token per messaggio inviato, istruzioni comprese, + 3 di generazione).
        var prompt = composition.GetProperty("promptTokens").GetProperty("value").GetInt32();
        var attributed = composition.GetProperty("attributedTokens").GetInt32();
        Assert.Equal(items.Values.Sum(i => i.GetProperty("tokens").GetProperty("value").GetInt32()), attributed);
        MetricsHost.AssertToken(composition.GetProperty("unattributedTokens"), (4 * CannedLlamaServer.OverheadPerMessage) + CannedLlamaServer.GenerationPromptOverhead, "calculated");
        Assert.Equal(prompt - attributed, composition.GetProperty("unattributedTokens").GetProperty("value").GetInt32());

        var last = usage.GetProperty("last");
        Assert.Equal("chat", last.GetProperty("kind").GetString());
        Assert.Equal(turn.GetProperty("resultMessageId").GetGuid().ToString(), last.GetProperty("refId").GetString());
        Assert.Equal(Math.Round(prompt * 100.0 / 32768, 1), last.GetProperty("usedPercent").GetDouble());
        var reserved = host.Options.ForPhase(AiPhase.Chat).MaxOutputTokens;
        Assert.Equal(reserved, last.GetProperty("outputReservedTokens").GetInt32());
        Assert.Equal(host.Options.SafetyMarginTokens, last.GetProperty("safetyMarginTokens").GetInt32());
        MetricsHost.AssertToken(last.GetProperty("remainingTokens"), 32768 - prompt - reserved - host.Options.SafetyMarginTokens, "calculated");
        Assert.Equal(JsonValueKind.Null, last.GetProperty("staleReason").ValueKind);

        // Storico originale letto dall'ultima inferenza: le tre note/risposte precedenti più il trigger, contate con il tokenizer.
        var history = usage.GetProperty("history");
        Assert.Equal(3, history.GetProperty("messageCount").GetInt32());
        MetricsHost.AssertToken(history.GetProperty("originalTokens"),
            CannedLlamaServer.Tokens(first) + CannedLlamaServer.Tokens(second) + CannedLlamaServer.Tokens(server.ChatReply), "calculated");
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("compaction").ValueKind);

        var runtime = usage.GetProperty("runtime");
        Assert.Equal("online", runtime.GetProperty("status").GetString());
        MetricsHost.AssertToken(runtime.GetProperty("contextWindowTokens"), 32768, "measured");
        Assert.False(runtime.GetProperty("windowMismatch").GetBoolean());
        var phases = usage.GetProperty("limits").GetProperty("phases");
        foreach (var phase in Enum.GetValues<AiPhase>())
        {
            var settings = host.Options.ForPhase(phase);
            var limits = phases.GetProperty(InferenceMetrics.PhaseName(phase));
            Assert.Equal(settings.MaxOutputTokens, limits.GetProperty("maxOutputTokens").GetInt32());
            Assert.Equal(settings.InputBudgetTokens, limits.GetProperty("inputBudgetTokens").GetInt32());
            Assert.Equal(settings.EnableThinking, limits.GetProperty("enableThinking").GetBoolean());
        }
    }

    [Fact]
    public async Task Without_usage_from_the_runtime_tokens_are_unavailable_and_never_estimated()
    {
        var server = new CannedLlamaServer { OmitUsage = true };
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        var turn = await MetricsHost.ChatAsync(pm, project, "Una nota senza usage nella risposta.");
        var metrics = (await MetricsHost.AssistantAsync(pm, project, turn.GetProperty("id").GetGuid())).GetProperty("metrics");

        foreach (var name in new[] { "promptTokens", "completionTokens", "totalTokens", "cachedTokens" })
        {
            MetricsHost.AssertToken(metrics.GetProperty(name), null, "unavailable");
        }

        Assert.Equal("completed", metrics.GetProperty("state").GetString());
        var usage = await MetricsHost.ContextUsageAsync(pm, project);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("last").GetProperty("usedPercent").ValueKind);
        MetricsHost.AssertToken(usage.GetProperty("last").GetProperty("remainingTokens"), null, "unavailable");
        MetricsHost.AssertToken(usage.GetProperty("composition").GetProperty("unattributedTokens"), null, "unavailable");
        Assert.True(usage.GetProperty("composition").GetProperty("attributedTokens").GetInt32() > 0); // il tokenizer c'è, l'usage no
    }

    [Fact]
    public async Task Reasoning_is_detected_from_the_response_and_its_text_never_reaches_tables_or_messages()
    {
        const string secret = "PENSIERO-RISERVATO-0042";
        const string marker = "NOTA-PM-UNICA-8812";
        var server = new CannedLlamaServer { Reasoning = secret };
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        var turn = await MetricsHost.ChatAsync(pm, project, $"Il portale {marker} serve ai clienti.");
        var assistant = await MetricsHost.AssistantAsync(pm, project, turn.GetProperty("id").GetGuid());
        Assert.Equal("on", assistant.GetProperty("metrics").GetProperty("reasoning").GetProperty("effective").GetString());
        Assert.DoesNotContain(secret, assistant.GetRawText());

        // Nessun testo (nota PM, istruzioni, risposta, ragionamento) in nessuna colonna della telemetria.
        var dump = await DumpCallsAsync(Connection);
        Assert.NotEmpty(dump);
        foreach (var forbidden in new[] { secret, marker, server.ChatReply, ConversationalAgent.Instructions[..40] })
        {
            Assert.DoesNotContain(forbidden, dump);
        }
    }

    // ---------------------------------------------------------------- analisi e documento

    [Fact]
    public async Task Analysis_and_document_runs_expose_metrics_on_run_analysis_and_draft_views()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        await MetricsHost.ChatAsync(pm, project, "Voglio un portale clienti chiamato Aurora.");

        var (analysis, document) = await MetricsHost.AnalyzeAndGenerateAsync(pm, project);
        Assert.True(document.GetProperty("status").GetString() == "Completed", CompactionHost.ErrorOf(document));
        var analysisId = analysis.GetProperty("id").GetGuid();
        var documentId = document.GetProperty("id").GetGuid();

        // Output strutturato: una chiamata finale di fase analysis; documento: una di fase document.
        var analysisRow = Assert.Single(await host.CallsAsync(runId: analysisId));
        Assert.True(analysisRow.IsFinal);
        Assert.Equal(AiPhase.Analysis, analysisRow.Phase);
        Assert.Contains("\"structuredOutput\": true", await Scalar<string>(Connection, """SELECT "Config"::text FROM "InferenceCalls" WHERE "Id" = @i""", ("i", analysisRow.Id)));
        var documentRow = Assert.Single(await host.CallsAsync(runId: documentId));
        Assert.Equal(AiPhase.Document, documentRow.Phase);

        var run = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/processing-runs/{analysisId}", Ct);
        Assert.Equal("analysis", run.GetProperty("metrics").GetProperty("phase").GetString());
        MetricsHost.AssertToken(run.GetProperty("metrics").GetProperty("promptTokens"), analysisRow.PromptTokens, "measured");
        var view = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/analyses/{analysisId}", Ct);
        Assert.Equal(run.GetProperty("metrics").GetRawText(), view.GetProperty("metrics").GetRawText());

        var documentRun = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/processing-runs/{documentId}", Ct);
        Assert.Equal("document", documentRun.GetProperty("metrics").GetProperty("phase").GetString());
        var draft = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/draft", Ct);
        Assert.Equal(documentRun.GetProperty("metrics").GetRawText(), draft.GetProperty("metrics").GetRawText());

        // Il documento riceve l'intera analisi confermata: è l'unica categoria oltre alle istruzioni.
        var usage = await MetricsHost.ContextUsageAsync(pm, project);
        Assert.Equal("document", usage.GetProperty("last").GetProperty("kind").GetString());
        Assert.Equal(documentId.ToString(), usage.GetProperty("last").GetProperty("refId").GetString());
        var keys = usage.GetProperty("composition").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).Order();
        Assert.Equal(["analysis_structured", "system_instructions"], keys);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("last").GetProperty("staleReason").ValueKind);
    }

    [Fact]
    public async Task A_run_rejected_by_the_validator_keeps_its_call_but_exposes_no_metrics()
    {
        var server = new CannedLlamaServer { AnalysisOverride = "{\"non\": \"un'analisi\"}" };
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        await MetricsHost.ChatAsync(pm, project, "Voglio un portale clienti.");

        var run = await CompactionHost.WaitRunAsync(pm, project, await CompactionHost.AnalyzeAsync(pm, project));

        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("metrics").ValueKind);
        var row = Assert.Single(await host.CallsAsync(runId: run.GetProperty("id").GetGuid()));
        Assert.False(row.IsFinal);
        Assert.Equal(InferenceCallState.Completed, row.State); // la chiamata è riuscita, il risultato no
        Assert.NotNull(row.PromptTokens);
    }

    // ---------------------------------------------------------------- compattazione

    [Fact]
    public async Task Summary_calls_are_aggregated_in_the_turn_and_the_compaction_event_is_recorded()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions(o => o.CompactionTriggerTokens = 3000));
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        await MetricsHost.ChatAsync(pm, project, CompactionDialogs.Message(0, 8000));
        var turn = await MetricsHost.ChatAsync(pm, project, CompactionDialogs.Message(1, 8000));
        Assert.Equal("Completed", turn.GetProperty("status").GetString());

        var rows = await host.CallsAsync(turnId: turn.GetProperty("id").GetGuid());
        var summary = Assert.Single(rows, r => r.Phase == AiPhase.Compaction);
        var final = Assert.Single(rows, r => r.IsFinal);
        Assert.Equal(AiPhase.Chat, final.Phase);
        Assert.False(summary.IsFinal);
        Assert.True(summary.MaxOutputTokensRequested <= host.Options.ForPhase(AiPhase.Compaction).MaxOutputTokens);

        var metrics = (await MetricsHost.AssistantAsync(pm, project, final.ChatTurnId!.Value)).GetProperty("metrics");
        Assert.Equal(2, metrics.GetProperty("calls").GetInt32());
        var compaction = metrics.GetProperty("compaction");
        Assert.Equal(1, compaction.GetProperty("summaryCalls").GetInt32());
        MetricsHost.AssertToken(compaction.GetProperty("summaryPromptTokens"), summary.PromptTokens, "measured");
        MetricsHost.AssertToken(compaction.GetProperty("summaryCompletionTokens"), summary.CompletionTokens, "measured");

        var usage = await MetricsHost.ContextUsageAsync(pm, project);
        var items = usage.GetProperty("composition").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!);
        Assert.Equal(1, items["derived_summary"].GetProperty("messageCount").GetInt32());
        MetricsHost.AssertToken(items["derived_summary"].GetProperty("tokens"),
            CannedLlamaServer.Tokens("[Summary]\nSintesi fedele: il portale si chiama Aurora."), "measured");

        var evt = usage.GetProperty("compaction");
        var before = evt.GetProperty("tokensBefore").GetProperty("value").GetInt32();
        var after = evt.GetProperty("tokensAfter").GetProperty("value").GetInt32();
        Assert.True(after < before, $"dopo {after} non minore di prima {before}");
        MetricsHost.AssertToken(evt.GetProperty("tokensSaved"), before - after, "calculated");
        Assert.True(evt.GetProperty("messagesSummarized").GetInt32() >= 1);
        // tokensBefore è lo storico originale entrato nella pipeline (stesso valore di history.originalTokens).
        Assert.Equal(before, usage.GetProperty("history").GetProperty("originalTokens").GetProperty("value").GetInt32());
        // tokensAfter sono i messaggi della chiamata finale: tutte le categorie tranne le istruzioni.
        Assert.Equal(items.Where(i => i.Key != "system_instructions").Sum(i => i.Value.GetProperty("tokens").GetProperty("value").GetInt32()), after);
    }

    // ---------------------------------------------------------------- retry, fencing e riconciliazione

    [Fact]
    public async Task A_retried_turn_keeps_distinct_attempt_rows_and_the_message_exposes_only_the_winning_attempt()
    {
        var server = new CannedLlamaServer();
        server.Faults.Enqueue(() => CannedLlamaServer.Status(HttpStatusCode.ServiceUnavailable));
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        var turn = await MetricsHost.ChatAsync(pm, project, "Una nota che richiede un nuovo tentativo.");

        Assert.Equal("Completed", turn.GetProperty("status").GetString());
        Assert.Equal(2, turn.GetProperty("attemptCount").GetInt32());
        var rows = await host.CallsAsync(turnId: turn.GetProperty("id").GetGuid());
        Assert.Equal(2, rows.Count);
        var failed = rows.Single(r => r.Attempt == 1);
        Assert.Equal(InferenceCallState.Failed, failed.State);
        Assert.False(failed.IsFinal);
        Assert.Null(failed.PromptTokens);   // nessun token attribuito a una richiesta fallita
        Assert.Null(failed.CompletionTokens);
        var winner = rows.Single(r => r.Attempt == 2);
        Assert.True(winner.IsFinal);
        Assert.NotEqual(failed.FencingToken, winner.FencingToken);

        var metrics = (await MetricsHost.AssistantAsync(pm, project, winner.ChatTurnId!.Value)).GetProperty("metrics");
        Assert.Equal(1, metrics.GetProperty("calls").GetInt32()); // i retry non sono chiamate del risultato logico
        Assert.Equal(winner.StartedAt, metrics.GetProperty("startedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Fencing_rejects_the_rows_of_a_stale_worker_and_a_reconciled_commit_does_not_duplicate_them()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions(), chat: false, formal: false);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var turnId = await CompactionHost.PostAsync(pm, project, "Nota eseguita a mano per provare il fencing.");
        var agent = host.App.Services.GetRequiredService<ConversationalAgent>();

        var stale = (await ClaimAsync(host, "worker-a"))!;
        using (InferenceScope.Begin())
        {
            // Il vecchio worker produce la risposta, perde il lease a favore di un nuovo owner e committa: rifiutato, nessuna riga.
            var staleReply = await RunAsync(host, agent, stale);
            await Exec(Connection, """UPDATE "ChatTurns" SET "LeaseUntil" = clock_timestamp() - interval '1 second' WHERE "Id" = @t""", ("t", turnId));
            Assert.Equal(turnId, (await ClaimAsync(host, "worker-b"))!.TurnId);
            Assert.Equal(ChatTurnCommitOutcome.Rejected, (await CommitAsync(host, stale, staleReply)).Outcome);
        }

        Assert.Empty(await host.CallsAsync(turnId: turnId));
        var fresh = await ClaimStateAsync(host, turnId, "worker-b");

        using (InferenceScope.Begin())
        {
            Assert.Equal(ChatTurnCommitOutcome.Committed, (await CommitAsync(host, fresh, await RunAsync(host, agent, fresh))).Outcome);
        }

        var row = Assert.Single(await host.CallsAsync(turnId: turnId));
        Assert.Equal(fresh.FencingToken, row.FencingToken);
        Assert.Equal(fresh.AttemptCount, row.Attempt);
        Assert.Equal(2, row.Attempt);

        using (InferenceScope.Begin())
        {
            // Riconciliazione dello stesso commit (es. esito incerto): AlreadyCompleted, nessuna riga duplicata.
            Assert.Equal(ChatTurnCommitOutcome.AlreadyCompleted, (await CommitAsync(host, fresh, await RunAsync(host, agent, fresh))).Outcome);
        }

        Assert.Single(await host.CallsAsync(turnId: turnId));
    }

    // ---------------------------------------------------------------- persistenza, riapertura, configurazione

    [Fact]
    public async Task Persisted_metrics_survive_a_restart_and_do_not_change_when_model_or_runtime_configuration_changes()
    {
        var server = new CannedLlamaServer();
        Guid project;
        Guid turnId;
        string before;
        await using (var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions()))
        {
            var (pm, p) = await host.NewProjectAsync();
            using var _ = pm;
            project = p;
            turnId = (await MetricsHost.ChatAsync(pm, project, "Nota prima del riavvio.")).GetProperty("id").GetGuid();
            before = (await MetricsHost.AssistantAsync(pm, project, turnId)).GetProperty("metrics").GetRawText();
        }

        // Nuovo processo: runtime riavviato con un ctx-size minore della configurazione e tetto di output diverso.
        server.Ctx = 16384;
        await using var restarted = await MetricsHost.StartAsync(
            Connection, server, MetricsHost.CannedOptions(o => o.MaxOutputTokens = 2048), chat: false, formal: false, prepare: false);
        using var client = await restarted.App.LoginAsync();

        var after = (await MetricsHost.AssistantAsync(client, project, turnId)).GetProperty("metrics");
        Assert.Equal(before, after.GetRawText());
        MetricsHost.AssertToken(after.GetProperty("contextWindowTokens"), 32768, "measured");

        var usage = await MetricsHost.ContextUsageAsync(client, project);
        var runtime = usage.GetProperty("runtime");
        MetricsHost.AssertToken(runtime.GetProperty("contextWindowTokens"), 16384, "measured");
        Assert.Equal(32768, runtime.GetProperty("configuredContextWindowTokens").GetInt32());
        Assert.True(runtime.GetProperty("windowMismatch").GetBoolean());
        Assert.Equal(2048, usage.GetProperty("limits").GetProperty("phases").GetProperty("chat").GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(before, usage.GetProperty("last").GetProperty("metrics").GetRawText()); // storico immutabile
    }

    [Fact]
    public async Task Context_usage_answers_200_with_persisted_metrics_when_the_model_is_offline()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions(o => o.RuntimeCheckSeconds = 1));
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        await MetricsHost.ChatAsync(pm, project, "Nota prima che il DGX vada offline.");

        server.Offline = true;
        host.App.Services.GetRequiredService<ModelRuntimeMonitor>().Invalidate();
        var usage = await MetricsHost.ContextUsageAsync(pm, project);

        Assert.Equal("offline", usage.GetProperty("runtime").GetProperty("status").GetString());
        MetricsHost.AssertToken(usage.GetProperty("runtime").GetProperty("contextWindowTokens"), null, "unavailable");
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("runtime").GetProperty("serverDefaultNPredict").ValueKind);
        Assert.Equal("chat", usage.GetProperty("last").GetProperty("kind").GetString());
        MetricsHost.AssertToken(usage.GetProperty("last").GetProperty("metrics").GetProperty("contextWindowTokens"), 32768, "measured");
    }

    [Fact]
    public async Task Context_usage_reports_newer_PM_notes_after_the_last_completed_inference_as_stale()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var first = await MetricsHost.ChatAsync(pm, project, "Prima nota con risposta.");
        server.Faults.Enqueue(() => CannedLlamaServer.Status(HttpStatusCode.BadRequest)); // errore permanente: nessuna risposta
        var failed = await MetricsHost.ChatAsync(pm, project, "Seconda nota senza risposta.");
        Assert.Equal("Failed", failed.GetProperty("status").GetString());

        var usage = await MetricsHost.ContextUsageAsync(pm, project);

        Assert.Equal(first.GetProperty("resultMessageId").GetGuid().ToString(), usage.GetProperty("last").GetProperty("refId").GetString());
        Assert.Equal("newer_messages", usage.GetProperty("last").GetProperty("staleReason").GetString());
        var rows = await host.CallsAsync(turnId: failed.GetProperty("id").GetGuid());
        Assert.Equal(InferenceCallState.Failed, Assert.Single(rows).State); // il turno fallito ha la sua riga, senza metriche esposte
    }

    [Fact]
    public async Task Context_usage_is_empty_for_a_new_project_404_for_an_unknown_one_and_never_leaks_between_projects()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var turn = await MetricsHost.ChatAsync(pm, project, "Nota del progetto A.");
        var (other, otherProject) = await host.NewProjectAsync();
        using var __ = other;

        var empty = await MetricsHost.ContextUsageAsync(other, otherProject);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("last").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("composition").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("compaction").ValueKind);
        Assert.Equal(0, empty.GetProperty("history").GetProperty("messageCount").GetInt32());
        MetricsHost.AssertToken(empty.GetProperty("history").GetProperty("originalTokens"), null, "unavailable");

        Assert.Equal(HttpStatusCode.NotFound, (await pm.GetAsync($"/api/projects/{Guid.NewGuid()}/context-usage", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.App.Anonymous().GetAsync($"/api/projects/{project}/context-usage", Ct)).StatusCode);

        // Le metriche si leggono solo con il progetto proprietario.
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var turnId = turn.GetProperty("id").GetGuid();
        Assert.Empty(await InferenceMetrics.ForTurnsAsync(db, otherProject, [turnId], Ct));
        Assert.Single(await InferenceMetrics.ForTurnsAsync(db, project, [turnId], Ct));
    }

    [Fact]
    public async Task Messages_and_runs_without_telemetry_return_null_metrics()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var turn = await MetricsHost.ChatAsync(pm, project, "Nota di un record storico.");
        var (analysis, _) = await MetricsHost.AnalyzeAndGenerateAsync(pm, project);

        await Exec(Connection, """DELETE FROM "InferenceCalls" """); // come i record precedenti alla tabella

        var assistant = await MetricsHost.AssistantAsync(pm, project, turn.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("metrics").ValueKind);
        var run = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/processing-runs/{analysis.GetProperty("id").GetGuid()}", Ct);
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("metrics").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/draft", Ct)).GetProperty("metrics").ValueKind);
        var usage = await MetricsHost.ContextUsageAsync(pm, project);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("last").ValueKind);
    }

    // ---------------------------------------------------------------- eventi

    [Fact]
    public async Task The_terminal_events_are_published_after_the_metrics_are_committed()
    {
        var server = new CannedLlamaServer();
        await using var host = await MetricsHost.StartAsync(Connection, server, MetricsHost.CannedOptions());
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        using var subscription = host.App.Services.GetRequiredService<ProjectEventHub>().Subscribe(project);

        var turnId = await CompactionHost.PostAsync(pm, project, "Nota per l'ordine degli eventi.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var evt = await subscription.Reader.ReadAsync(timeout.Token);
            if (evt.Name == ProjectEventHub.ChatState && JsonDocument.Parse(evt.Data).RootElement.GetProperty("status").GetString() == "Completed")
            {
                break;
            }
        }

        // Alla ricezione di chat.state Completed le righe sono già committate: il GET successivo vede le metriche definitive.
        Assert.Single(await host.CallsAsync(turnId: turnId), r => r.IsFinal);

        await CompactionHost.AnalyzeAsync(pm, project);
        var states = await ProcessingStateEvents.UntilTerminalAsync(subscription, Ct);
        var terminal = states[^1];
        Assert.Equal("Completed", terminal.GetProperty("status").GetString());
        Assert.Equal("analysis", terminal.GetProperty("metrics").GetProperty("phase").GetString()); // il payload stesso porta le metriche
    }

    // ---------------------------------------------------------------- supporto

    private static async Task<ChatTurnClaim?> ClaimAsync(MetricsHost host, string owner)
    {
        using var scope = host.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextAsync(owner, Ct);
    }

    /// <summary>Claim corrente del turno per l'owner indicato, riletto dal database (fencing e tentativo assegnati dal reclaim).</summary>
    private static async Task<ChatTurnClaim> ClaimStateAsync(MetricsHost host, Guid turnId, string owner)
    {
        using var scope = host.App.Services.CreateScope();
        var turn = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId, Ct);
        Assert.Equal(owner, turn.LeaseOwner);
        return new ChatTurnClaim(turn.Id, turn.ConversationId, turn.TriggerMessageId, turn.SourceCutoffSequence, owner,
            turn.FencingToken, turn.AttemptCount, turn.LeaseUntil!.Value, turn.DeadlineAt);
    }

    /// <summary>ConversationalAgent nello scope di telemetria corrente, come farebbe il dispatcher per il tentativo.</summary>
    private static async Task<ConversationalReply> RunAsync(MetricsHost host, ConversationalAgent agent, ChatTurnClaim claim)
    {
        using var services = host.App.Services.CreateScope();
        return await agent.RunAsync(services.ServiceProvider.GetRequiredService<AppDbContext>(), claim, null, Ct);
    }

    private static async Task<ChatTurnCommitResult> CommitAsync(MetricsHost host, ChatTurnClaim claim, ConversationalReply reply)
    {
        using var services = host.App.Services.CreateScope();
        return await services.ServiceProvider.GetRequiredService<IChatTurnCommitter>().CommitAsync(claim, reply, Ct);
    }

    /// <summary>Tutte le colonne di InferenceCalls come testo (JSON per riga): per verificare che non contengano contenuti.</summary>
    private static async Task<string> DumpCallsAsync(string connection)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT coalesce(string_agg(row_to_json(i)::text, E'\n'), '') FROM "InferenceCalls" i""", c);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<T> Scalar<T>(string connection, string sql, params (string Name, object Value)[] parameters) =>
        await CompactionHost.ScalarAsync<T>(connection, sql, parameters);

    private static async Task Exec(string connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}
