using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// T047 - compattazione nativa MAF (CompactionProvider + SummarizationCompactionStrategy) su chat e analisi con le applicazioni
/// complete su PostgreSQL: i messaggi arrivano al modello dietro il vero <see cref="InternalModelClient"/>, un registratore HTTP
/// vede cosa parte davvero (richieste di sintesi, contesto finale, <c>usage.prompt_tokens</c> misurato dal server) e il database
/// dice cosa e' rimasto (originali, revisioni). Le prove sono scritte una volta sola e girate due volte: contro il DGX Spark
/// (<see cref="CompactionMafTests"/>, MafReal) e contro un server finto deterministico (<c>CompactionFlowTests</c>, Integration).
/// Un test senza servizi risulta Skipped, mai superato. Registra token, durate e conteggi, non i contenuti integrali.
/// </summary>
public abstract class CompactionScenarios : IAsyncLifetime
{
    // Valori di produzione (infra/.env.example): finestra 32768, output 4096, margine 1024 -> budget di input 27648.
    private const int Window = 32768;
    private const int Output = 4096;
    private const int Margin = 1024;

    private readonly PostgresDatabaseFixture _database = new();

    /// <summary>Database isolato del test (creato all'inizializzazione).</summary>
    protected string Connection => _database.ConnectionString;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>true = modello reale su DGX (aggiunge i controlli sul contenuto delle risposte del modello).</summary>
    protected abstract bool Real { get; }

    /// <summary>Endpoint, chiave e modello da usare; Skipped se i servizi richiesti non sono disponibili.</summary>
    protected abstract AiOptions Services();

    /// <summary>Finestra massima del server finto (n_ctx); ignorata con il modello reale, dove la legge GET /props.</summary>
    protected abstract int FakeContextLimit { get; }

    protected abstract HttpMessageHandler NewServer();

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private static AiOptions Tuned(AiOptions services, int window, int output, int margin, int trigger) => new()
    {
        Endpoint = services.Endpoint,
        ApiKey = services.ApiKey,
        ModelId = services.ModelId,
        ContextWindowTokens = window,
        MaxOutputTokens = output,
        SafetyMarginTokens = margin,
        CompactionTriggerTokens = trigger,
    };

    private async Task<int> InstructionTokensAsync(AiOptions options, string instructions)
    {
        using var client = new InternalModelClient(options, NewServer());
        return await client.CountTokensAsync(instructions, Ct);
    }

    /// <summary>Soglia ridotta: la compattazione scatta quando i soli messaggi superano ~1500 token, ben sotto il budget.</summary>
    private async Task<AiOptions> ReducedAsync(AiOptions services, string instructions) =>
        Tuned(services, Window, Output, Margin, await InstructionTokensAsync(Tuned(services, Window, Output, Margin, 0), instructions) + 1500);

    private static AiOptions Production(AiOptions services) => Tuned(services, Window, Output, Margin, 0);

    // ------------------------------------------------------------------------------------------------ chat

    [Fact]
    public Task Chat_over_a_reduced_threshold_compacts_natively_within_the_window_and_keeps_originals_negations_and_exclusions() =>
        RunChatAsync(messages: 8, chars: 1600, observedCase: false);

    [Fact]
    public Task Chat_with_the_ten_large_messages_of_the_first_manual_test_compacts_instead_of_overflowing() =>
        RunChatAsync(messages: 10, chars: 18_500, observedCase: true);

    private async Task RunChatAsync(int messages, int chars, bool observedCase)
    {
        TestEnvironment.RequirePostgres();
        var connection = Connection;
        var services = Services();
        var options = observedCase ? Production(services) : await ReducedAsync(services, ConversationalAgent.Instructions);
        var history = Enumerable.Range(0, messages - 1).Select(i => CompactionDialogs.Message(i, chars)).ToList();
        var finalText = CompactionDialogs.Message(messages - 1, chars, question: true);

        // Fase 1 (preparazione, modello finto, nessuna compattazione): lo storico di originali PM e risposte e' persistito dal flusso vero.
        Guid project;
        await using (var phase1 = await CompactionHost.StartAsync(
            connection,
            Tuned(services, 4_000_000, Output, Margin, 0),
            new FakeModelServer { ContextLimit = int.MaxValue, ChatReply = "Preso nota." },
            chat: true, analysis: false, prepare: true))
        {
            Guid pmProject;
            HttpClient pm;
            (pm, pmProject) = await phase1.NewProjectAsync();
            project = pmProject;
            using (pm)
            {
                foreach (var text in history)
                {
                    var done = await CompactionHost.WaitTurnAsync(pm, project, await CompactionHost.PostAsync(pm, project, text));
                    Assert.True(done.GetProperty("status").GetString() == "Completed", CompactionHost.ErrorOf(done));
                }
            }
        }

        var before = await CompactionHost.OriginalsAsync(connection);

        // Fase 2: ultimo turno con la configurazione sotto prova (soglia ridotta o valori di produzione) e il modello sotto prova.
        await using var host = await CompactionHost.StartAsync(connection, options, NewServer(), chat: true, analysis: false, prepare: false, requestUsage: Real);
        using var second = await host.LoginAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var turn = await CompactionHost.PostAsync(second, project, finalText);
        var terminal = await CompactionHost.WaitTurnAsync(second, project, turn);
        clock.Stop();
        Assert.True(terminal.GetProperty("status").GetString() == "Completed", $"turno non completato: {CompactionHost.ErrorOf(terminal)}");

        // Attivazione nativa: la strategia di sintesi ha chiamato il modello con il proprio prompt; un solo invio finale, senza retry.
        var summaries = host.Recorder.Summaries;
        Assert.True(summaries.Count >= 1, "la compattazione nativa non si e' attivata");
        Assert.All(summaries, s => Assert.Equal(200, s.Status));
        var main = Assert.Single(host.Recorder.Main);
        var summary = Assert.Single(main.Messages, m => m.Text.StartsWith("[Summary]", StringComparison.Ordinal));
        Assert.Equal("assistant", summary.Role);
        Assert.Contains(main.Messages, m => m.Role == "user" && m.Text == finalText);               // il messaggio a cui rispondere e' intatto
        Assert.DoesNotContain(main.Messages, m => m.Text == history[0]);                           // il primo originale e' sostituito dalla sintesi nel solo contesto

        // La compattazione era necessaria (contesto originale oltre soglia) e il contesto finale rientra: stesso tokenizer e misura del server.
        var instructionTokens = await host.Model.CountTokensAsync(ConversationalAgent.Instructions, Ct);
        var originalTokens = instructionTokens + await CountAsync(host, history.Append(finalText));
        var finalTokens = await host.CountSentAsync(main);
        var trigger = options.EffectiveCompactionTriggerTokens;
        Assert.True(originalTokens > trigger, $"contesto originale {originalTokens} non oltre la soglia {trigger}: la prova non esercita la compattazione");
        Assert.True(finalTokens <= options.InputBudgetTokens, $"contesto finale {finalTokens} oltre il budget {options.InputBudgetTokens}");
        if (main.PromptTokens is { } measured)
        {
            Assert.True(measured <= options.ContextWindowTokens - options.MaxOutputTokens, $"prompt misurato dal server {measured} oltre finestra-output");
        }

        // Originali invariati e aggiunti solo l'ultimo messaggio PM e la risposta; sintesi derivata, mai persistita come originale.
        var after = await CompactionHost.OriginalsAsync(connection);
        Assert.Equal(before, after.Take(before.Count).ToList());
        Assert.Equal(before.Count + 2, after.Count);
        Assert.Equal(finalText, after[before.Count].Body);
        Assert.DoesNotContain(after, m => m.Body.StartsWith("[Summary]", StringComparison.Ordinal));

        // Negazioni, esclusioni e nome sopravvivono alla sintesi (il prompt le richiede) e la risposta le usa.
        foreach (var fact in new[] { "SMS", "fattur", "Aurora" })
        {
            Assert.Contains(fact, summary.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(fact, after[^1].Body, StringComparison.OrdinalIgnoreCase);
        }

        Report(
            $"T047 chat {(observedCase ? "caso osservato" : "soglia ridotta")}: finestra={options.ContextWindowTokens} budget={options.InputBudgetTokens} soglia={trigger} "
            + $"originale={originalTokens} finale={finalTokens} prompt_server={main.PromptTokens?.ToString() ?? "n/d"} sintesi={summaries.Count} "
            + $"messaggi_inviati={main.Messages.Count} durata={clock.Elapsed.TotalSeconds:F1}s");
    }

    // ------------------------------------------------------------------------------------------------ analisi

    [Fact]
    public Task Analysis_over_a_reduced_threshold_compacts_natively_within_the_window_and_keeps_originals_sources_and_exclusions() =>
        RunAnalysisAsync(messages: 8, chars: 1600, observedCase: false);

    [Fact]
    public Task Analysis_with_the_ten_large_messages_of_the_first_manual_test_compacts_instead_of_overflowing() =>
        RunAnalysisAsync(messages: 10, chars: 18_500, observedCase: true);

    private async Task RunAnalysisAsync(int messages, int chars, bool observedCase)
    {
        TestEnvironment.RequirePostgres();
        var connection = Connection;
        var services = Services();
        var options = observedCase ? Production(services) : await ReducedAsync(services, ConversationalAgent.ConsolidationInstructions);
        var texts = Enumerable.Range(0, messages).Select(i => CompactionDialogs.Message(i, chars)).ToList();

        await using var host = await CompactionHost.StartAsync(connection, options, NewServer(), chat: false, analysis: true, prepare: true, requestUsage: Real);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        foreach (var text in texts)
        {
            await CompactionHost.PostAsync(pm, project, text);    // senza esecutore chat i turni restano in coda: solo l'analisi usa il modello
        }

        var before = await CompactionHost.OriginalsAsync(connection);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = await CompactionHost.AnalyzeAsync(pm, project);
        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);
        clock.Stop();
        Assert.True(terminal.GetProperty("status").GetString() == "Completed", $"analisi non completata: {CompactionHost.ErrorOf(terminal)}");

        var summaries = host.Recorder.Summaries;
        Assert.True(summaries.Count >= 1, "la compattazione nativa non si e' attivata");
        Assert.All(summaries, s => Assert.Equal(200, s.Status));
        var main = Assert.Single(host.Recorder.Main);
        Assert.True(main.Structured, "la chiamata finale deve essere quella di consolidamento con output strutturato");
        var summary = Assert.Single(main.Messages, m => m.Text.StartsWith("[Summary]", StringComparison.Ordinal));
        Assert.Equal("assistant", summary.Role);
        Assert.Contains(main.Messages, m => m.Role == "user" && m.Text.Contains("Produci ora l'unico oggetto JSON"));   // l'istruzione finale e' intatta

        // Gli identificativi di fonte del primo messaggio (sintetizzato) restano nella sintesi: l'analisi puo' ancora citarlo.
        var first = before[0];
        Assert.Contains(first.Id.ToString(), summary.Text);

        var instructionTokens = await host.Model.CountTokensAsync(ConversationalAgent.ConsolidationInstructions, Ct);
        var originalTokens = instructionTokens + await CountAsync(host, texts);
        var finalTokens = await host.CountSentAsync(main);
        var trigger = options.EffectiveCompactionTriggerTokens;
        Assert.True(originalTokens > trigger, $"contesto originale {originalTokens} non oltre la soglia {trigger}: la prova non esercita la compattazione");
        Assert.True(finalTokens <= options.InputBudgetTokens, $"contesto finale {finalTokens} oltre il budget {options.InputBudgetTokens}");
        if (main.PromptTokens is { } measured)
        {
            Assert.True(measured <= options.ContextWindowTokens - options.MaxOutputTokens, $"prompt misurato dal server {measured} oltre finestra-output");
        }

        // Originali invariati; la revisione AI e' valida, Unvalidated e le sue fonti sono messaggi realmente ammessi.
        Assert.Equal(before, await CompactionHost.OriginalsAsync(connection));
        var revisionId = terminal.GetProperty("resultRevisionId").GetGuid();
        var content = AnalysisContentJson.Deserialize(await CompactionHost.ScalarAsync<string>(
            connection, """SELECT "ContentJson" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", revisionId)));
        Assert.Equal(AnalysisContentState.Unvalidated, content.ValidationState);
        var admitted = before.Select(m => m.Id).ToHashSet();
        Assert.NotEmpty(content.Sources);
        Assert.All(content.Sources, s => Assert.Contains(s.MessageId!.Value, admitted));

        if (Real)
        {
            // Solo il modello reale puo' dimostrare che negazioni ed esclusioni attraversano la sintesi fino all'analisi.
            foreach (var fact in new[] { "SMS", "fattur", "Aurora" })
            {
                Assert.Contains(fact, summary.Text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains(content.ContextAndScope.Excluded, i => i.Text.Contains("SMS", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(content.ContextAndScope.Excluded, i => i.Text.Contains("fattur", StringComparison.OrdinalIgnoreCase));
            var sourceOfFirst = content.Sources.Where(s => s.MessageId == first.Id).Select(s => s.Id).ToHashSet();
            Assert.Contains(content.ContextAndScope.Excluded, i => i.EvidenceRefs.Any(sourceOfFirst.Contains));   // fonte del messaggio sintetizzato
        }

        Report(
            $"T047 analisi {(observedCase ? "caso osservato" : "soglia ridotta")}: finestra={options.ContextWindowTokens} budget={options.InputBudgetTokens} soglia={trigger} "
            + $"originale={originalTokens} finale={finalTokens} prompt_server={main.PromptTokens?.ToString() ?? "n/d"} sintesi={summaries.Count} "
            + $"messaggi_inviati={main.Messages.Count} fonti={content.Sources.Count} durata={clock.Elapsed.TotalSeconds:F1}s");
    }

    // ------------------------------------------------------------------------------------------------ limiti strutturali

    [Fact]
    public async Task A_single_PM_message_over_the_input_budget_is_a_structural_limit_chat_fails_with_context_overflow_and_measured_tokens()
    {
        TestEnvironment.RequirePostgres();
        var connection = Connection;
        var options = Tuned(Services(), 8192, 1024, 512, 0);      // budget 6656: un messaggio PM (max 65.536 caratteri) puo' superarlo
        var text = CompactionDialogs.Message(0, 30_000);

        await using var host = await CompactionHost.StartAsync(connection, options, NewServer(), chat: true, analysis: false, prepare: true);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        var turn = await CompactionHost.PostAsync(pm, project, text);
        var terminal = await CompactionHost.WaitTurnAsync(pm, project, turn);

        Assert.Equal("Failed", terminal.GetProperty("status").GetString());
        var error = terminal.GetProperty("error");
        Assert.Equal("context_overflow", error.GetProperty("code").GetString());
        Assert.Equal(1, terminal.GetProperty("attemptCount").GetInt32());                     // permanente: nessun retry
        Assert.Empty(host.Recorder.Calls);                                                    // nessuna sintesi e nessuna chiamata al modello

        // Valori misurati, non stimati: istruzioni + messaggio contati con il tokenizer del modello, mai percentuali.
        var message = error.GetProperty("message").GetString()!;
        var numbers = Regex.Match(message, @"Contesto di (\d+) token oltre il budget di input di (\d+) token");
        Assert.True(numbers.Success, "il messaggio deve riportare token e budget misurati");
        var expected = await host.Model.CountTokensAsync(ConversationalAgent.Instructions, Ct) + await host.Model.CountTokensAsync(text, Ct);
        Assert.Equal(expected, int.Parse(numbers.Groups[1].Value));
        Assert.Equal(options.InputBudgetTokens, int.Parse(numbers.Groups[2].Value));
        Assert.DoesNotContain("%", message);

        // L'originale resta persistito integro e nessuna risposta e' stata prodotta.
        var originals = await CompactionHost.OriginalsAsync(connection);
        Assert.Equal([text], originals.Select(m => m.Body).ToList());

        Report($"T047 limite strutturale chat: token={numbers.Groups[1].Value} budget={numbers.Groups[2].Value} (finestra {options.ContextWindowTokens})");
    }

    [Fact]
    public async Task A_dialog_larger_than_the_model_window_cannot_be_summarized_in_one_call_analysis_fails_with_compaction_failed()
    {
        TestEnvironment.RequirePostgres();
        var connection = Connection;
        var options = Production(Services());
        await using var host = await CompactionHost.StartAsync(connection, options, NewServer(), chat: false, analysis: true, prepare: true);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        // Il riassunto nativo e' una sola chiamata: i gruppi da riassumere superano n_ctx del runtime (circa 2,6 volte la finestra).
        var runtimeCtx = await RuntimeContextAsync(host);
        var count = (int)Math.Min(40, Math.Ceiling(runtimeCtx * 2.6 * 3 / 60_000));
        for (var i = 0; i < count; i++)
        {
            await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(i, 60_000));
        }

        var before = await CompactionHost.OriginalsAsync(connection);
        var run = await CompactionHost.AnalyzeAsync(pm, project);
        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);

        Assert.Equal("Failed", terminal.GetProperty("status").GetString());
        Assert.Equal("compaction_failed", terminal.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, terminal.GetProperty("attemptCount").GetInt32());                      // permanente: nessun retry
        var summary = Assert.Single(host.Recorder.Summaries);                                   // tentata una volta, rifiutata dal runtime
        Assert.Equal(400, summary.Status);
        Assert.Empty(host.Recorder.Main);                                                       // nessuna analisi su contesto non riassunto o troncato
        Assert.Equal(0, await CompactionHost.ScalarAsync<long>(connection, """SELECT COUNT(*) FROM "AnalysisRevisions" """));
        Assert.Equal(before, await CompactionHost.OriginalsAsync(connection));

        Report(
            $"T047 limite strutturale sintesi: messaggi={count} n_ctx={runtimeCtx} sintesi_http={summary.Status} errore={terminal.GetProperty("error").GetProperty("code").GetString()}");
    }

    // ------------------------------------------------------------------------------------------------ Document

    [Fact]
    public async Task Document_snapshot_within_budget_is_sent_whole_without_summarizer_and_over_budget_fails_before_any_call()
    {
        var options = Production(Services());
        var recorder = new ModelRecorder(NewServer(), Real);
        using var model = new InternalModelClient(options, recorder);
        var agent = new DocumentAgent(AgentFactory.Create(options, model), model.CreateChatOptions(temperature: 0.2f));
        var projectId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();

        // Entro il budget: l'intero JSON confermato arriva al modello in un solo messaggio, senza history ne' sintesi.
        var small = BuildInput(projectId, analysisId, itemCount: 3);
        var reply = await agent.CreateAgent().RunAsync(DocumentAgent.BuildMessages(small), cancellationToken: Ct);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        Assert.Empty(recorder.Summaries);
        var call = Assert.Single(recorder.Main);
        var sent = call.Messages.Single(m => m.Role == "user").Text;
        var embedded = System.Text.Json.Nodes.JsonNode.Parse(sent[sent.IndexOf('{')..(sent.LastIndexOf('}') + 1)]);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(AnalysisContentJson.Serialize(small.Content)), embedded));   // nessuna riduzione dello snapshot

        // Oltre il budget (snapshot confermato di circa il doppio): errore esplicito prima di ogni chiamata, nessun provider di riduzione.
        var large = BuildInput(projectId, analysisId, itemCount: 400);
        var messages = DocumentAgent.BuildMessages(large);
        var overflow = await Assert.ThrowsAsync<ContextOverflowException>(() => agent.PreflightAsync(messages, Ct).AsTask());
        Assert.True(overflow.Tokens > overflow.BudgetTokens);
        Assert.Equal(options.InputBudgetTokens, overflow.BudgetTokens);
        await Assert.ThrowsAsync<ContextOverflowException>(() => agent.CreateAgent().RunAsync(messages, cancellationToken: Ct));
        Assert.Single(recorder.Calls);                       // sempre e solo la chiamata entro budget: nessuna sintesi, nessun invio troncato
        Assert.Contains("\"text\"", messages[0].Text);       // lo snapshot non e' stato modificato dal tentativo

        Report($"T047 Document: oltre budget token={overflow.Tokens} budget={overflow.BudgetTokens}; entro budget un solo invio");
    }

    private static DocumentInput BuildInput(Guid projectId, Guid analysisId, int itemCount)
    {
        var source = Guid.NewGuid();
        AnalysisItem Item(int i) => new()
        {
            Id = Guid.NewGuid(),
            Text = $"Requisito {i}: " + CompactionDialogs.Message(i, 700),
            Classification = AnalysisClassification.Declared,
            EvidenceRefs = [source],
        };

        var content = new AnalysisContent
        {
            SchemaVersion = AnalysisContent.CurrentSchemaVersion,
            AnalysisId = analysisId,
            ProjectId = projectId,
            SourceCutoffSequence = 1,
            ValidationState = AnalysisContentState.PmConfirmed,
            Sources = [new AnalysisSource { Id = source, Kind = AnalysisSourceKind.Message, MessageId = Guid.NewGuid(), MessageRole = AnalysisMessageRole.User, RevisionId = null }],
            Goals = [Item(0)],
            ContextAndScope = new AnalysisContextAndScope { Overview = [], Included = [], Excluded = [Item(1)] },
            Requirements = Enumerable.Range(2, itemCount).Select(Item).ToList(),
            OperationalFlows = [],
            Constraints = [],
            Dependencies = [],
            Assumptions = [],
            OpenQuestions = [],
            Conflicts = [],
        };
        return new DocumentInput(projectId, analysisId, Guid.NewGuid(), 1, content);
    }

    // ------------------------------------------------------------------------------------------------ supporto

    /// <summary>Solo numeri e durate (mai contenuti): nel log diagnostico e nell'output del test.</summary>
    private static void Report(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }

    private static async Task<int> CountAsync(CompactionHost host, IEnumerable<string> texts)
    {
        var total = 0;
        foreach (var text in texts)
        {
            total += await host.Model.CountTokensAsync(text, Ct);
        }

        return total;
    }

    /// <summary>Finestra allocata dal runtime (GET /props) sul server reale; sul finto il limite configurato.</summary>
    private async Task<int> RuntimeContextAsync(CompactionHost host)
    {
        if (!Real)
        {
            return FakeContextLimit;
        }

        try
        {
            return await host.Model.GetRuntimeContextTokensAsync(Ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Window;
        }
    }
}

/// <summary>T047 contro il DGX Spark reale. Servono BPROJECTS_TEST_POSTGRES e BPROJECTS_TEST_DGX_ENDPOINT/API_KEY/MODEL.</summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class CompactionMafTests : CompactionScenarios
{
    protected override bool Real => true;

    protected override int FakeContextLimit => 0;

    protected override AiOptions Services() => TestEnvironment.RequireDgxOptions();

    protected override HttpMessageHandler NewServer() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
}
