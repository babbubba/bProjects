using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T123 (MAF-PG) — raccordo DI/dispatch dei tre workflow documentali. Applicazione reale (API, DI di produzione `AddAiWorkflows`), dispatcher reale
/// costruito dalla DI, PostgreSQL e MAF 1.26.0 reali; solo il modello e finto, a livello HTTP (nessun DGX). Si prova che chat, Analizza
/// document-only e Genera dopo conferma raggiungono il grafo 2 e completano, che il ramo senza documenti e i checkpoint 001 restano sul grafo
/// originale, che la definizione congelata alla richiesta (non la selezione corrente) decide, e che una definizione ignota o incompatibile e un
/// errore esplicito. Il tipo di grafo si legge dal prompt realmente inviato e dalla definizione scritta nei checkpoint.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentWorkflowRoutingTests : ArchiveTestBase
{
    private const string Statement = "Il cliente A puo emettere al massimo 5 ordini al giorno.";

    private readonly RoutingModel _model;
    private Func<Req, string>? _chatOverride;   // risposta del modello per il grafo chat v3 (T128): default = cita il primo alias

    public DocumentWorkflowRoutingTests() => _model = new RoutingModel(Reply);

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        // Il dispatcher e costruito dal test dalla DI (un solo owner per volta, nessuna corsa con il ciclo in background); i workflow sono
        // quelli registrati da produzione. L'estrattore deterministico del registro lo aggiunge l'harness.
        foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AiDispatchService)).ToList())
        {
            services.Remove(hosted);
        }

        services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, _model));
        services.AddAiWorkflows();
    }

    // ================================================================ grafo 2: chat, Analizza document-only, Genera dopo conferma

    [Fact]
    public async Task A_chat_turn_frozen_with_a_ready_document_runs_the_document_chat_graph_and_completes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewReadyDocument(client, project, "regole", Statement);
        var turn = await PostChat(client, project, "Quanti ordini al giorno puo emettere il cliente A?");

        await DrainAsync();

        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal([Graph.DocChatV3], _model.Graphs);                                                // un solo invio, del grafo documentale con alias (T128)
        var body = await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t", ("t", turn));
        Assert.Contains($"[fonte:{doc.SourceId}]", body);                                              // il modello cita F1: la risposta pubblica e' solo canonica
        Assert.DoesNotMatch(@"\[fonte:F\d", body);
        Assert.DoesNotMatch("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", string.Concat(_model.Texts));   // il modello non ha mai visto un UUID di fonte
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn)));
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal([DocumentChatWorkflow.DefinitionVersionV3], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ChatTurnId\" = @o", ("o", turn)));
        await AssertClassifiedBeforeFirstClaim("ChatTurns", "ChatTurn", turn, expectedPayloadMeasured: true);
    }

    [Fact]
    public async Task A_chat_turn_requested_before_the_alias_contract_and_frozen_with_v2_still_runs_the_v2_graph_with_its_own_prompt_and_decoder()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewReadyDocument(client, project, "regole", Statement);
        var turn = await PostChat(client, project, "Quanti ordini al giorno puo emettere il cliente A?");
        await Execute("UPDATE \"ChatTurns\" SET \"PromptVersion\" = NULL, \"AgentConfig\" = NULL WHERE \"Id\" = @t", ("t", turn));

        await DrainAsync();

        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal([Graph.DocChat], _model.Graphs);                                                  // prompt e messaggio con UUID della v2, mai l'alias
        Assert.Contains($"[fonte:{doc.SourceId}]", await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t", ("t", turn)));
        Assert.Equal([DocumentChatWorkflow.DefinitionVersion], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ChatTurnId\" = @o", ("o", turn)));
        await AssertClassifiedBeforeFirstClaim("ChatTurns", "ChatTurn", turn, expectedPayloadMeasured: true);
    }

    [Fact]
    public async Task A_v3_chat_turn_whose_model_writes_a_raw_uuid_or_an_unknown_alias_fails_permanently_with_work_output_invalid_and_no_answer()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewReadyDocument(client, project, "regole", Statement);
        _chatOverride = _ => JsonSerializer.Serialize(new { mode = "documented", answer = $"Massimo 5 [fonte:{doc.SourceId}] e [fonte:F7]." });
        var turn = await PostChat(client, project, "Quanti ordini al giorno puo emettere il cliente A?");

        await DrainAsync();

        Assert.Equal(("Failed", "work_output_invalid"), (
            await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)), await Scalar<string>("SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn))));
        Assert.Equal([Graph.DocChatV3], _model.Graphs);                                                // un solo invio: nessun retry ne' riparazione
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t", ("t", turn)));
    }

    [Theory]
    [InlineData("document-chat-v9", null)]
    [InlineData("document-chat-v3", """{"chatDefinition":"project-chat-documents-v3","chat":"document-chat-v3"}""")]   // v3 senza schema, catalogo, algoritmo e validazione congelati
    public async Task An_unknown_or_incompatible_chat_definition_fails_explicitly_and_never_reaches_the_model(string prompt, string? config)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var turn = await PostChat(client, project, "Quanti ordini al giorno puo emettere il cliente A?");
        await Execute("UPDATE \"ChatTurns\" SET \"PromptVersion\" = @p, \"AgentConfig\" = @c::jsonb WHERE \"Id\" = @t", ("p", prompt), ("c", (object?)config ?? DBNull.Value), ("t", turn));

        await DrainAsync();

        Assert.Equal(("Failed", "workflow_incompatible"), (
            await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)), await Scalar<string>("SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn))));
        Assert.Empty(_model.Graphs);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task Analyze_with_only_documents_runs_the_document_analysis_graph_and_completes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);

        var run = await StartAnalysis(client, project);
        await DrainAsync();

        var row = await RunRow(run);
        Assert.Equal(("Completed", null), (row.Status, row.ErrorCode));
        Assert.Equal([Graph.DocAnalysisV3], _model.Graphs);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"SchemaVersion\" = '1.1.0'", ("r", run)));
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal([DocumentAnalysisWorkflow.DefinitionVersionV3], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @o", ("o", run)));

        // T124 (invariato per v3): il payload dell'analisi non e determinabile prima del claim (selezione evidenze congelata sotto lease): T unavailable => Extended workload_unknown.
        await AssertClassifiedBeforeFirstClaim("ProcessingRuns", "ProcessingRun", run, expectedPayloadMeasured: false);
        Assert.Equal(("Extended", "workload_unknown"), (await Scalar<string>("SELECT \"InitialRegime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), await Scalar<string>("SELECT \"ClassificationReason\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))));
    }

    [Fact]
    public async Task A_run_frozen_with_v2_before_the_alias_contract_still_runs_the_v2_graph_with_its_own_prompt_and_completes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);

        var run = await StartAnalysis(client, project);
        await PinToV2Async(run);
        await DrainAsync();

        var row = await RunRow(run);
        Assert.Equal(("Completed", null), (row.Status, row.ErrorCode));
        Assert.Equal([Graph.DocAnalysis], _model.Graphs);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"SchemaVersion\" = '1.1.0'", ("r", run)));
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal([DocumentAnalysisWorkflow.DefinitionVersion], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @o", ("o", run)));

        // T124: il payload dell'analisi non e determinabile prima del claim (selezione evidenze congelata sotto lease): T unavailable => Extended workload_unknown.
        await AssertClassifiedBeforeFirstClaim("ProcessingRuns", "ProcessingRun", run, expectedPayloadMeasured: false);
        Assert.Equal(("Extended", "workload_unknown"), (await Scalar<string>("SELECT \"InitialRegime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), await Scalar<string>("SELECT \"ClassificationReason\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))));
    }

    [Fact]
    public async Task Generate_after_confirmation_runs_the_document_draft_graph_and_completes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var analysis = await StartAnalysis(client, project);
        await DrainAsync();
        var revision = await ConfirmAsync(client, project, analysis);

        var generation = await StartGeneration(client, project, analysis, revision);
        await DrainAsync();

        var row = await RunRow(generation);
        Assert.Equal(("Completed", null), (row.Status, row.ErrorCode));
        Assert.Equal([Graph.DocAnalysisV3, Graph.DocDraftV3], _model.Graphs);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r AND \"AnalysisRevisionId\" = @v", ("r", generation), ("v", revision)));
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", generation)));
        Assert.Equal([DocumentSourceDraftWorkflow.DefinitionVersionV3], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @o", ("o", generation)));
        await AssertClassifiedBeforeFirstClaim("ProcessingRuns", "ProcessingRun", generation, expectedPayloadMeasured: true);
    }

    [Fact]
    public async Task A_generation_run_requested_before_the_alias_draft_and_frozen_with_v2_still_completes_with_the_v2_prompt_and_decoder()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var analysis = await StartAnalysis(client, project);
        await DrainAsync();
        var revision = await ConfirmAsync(client, project, analysis);
        var generation = await StartGeneration(client, project, analysis, revision);
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-document-documents-v2', \"PromptVersions\" = '{\"document\":\"document-documents-v2\",\"analysisSchema\":\"1.1.0\"}'::jsonb WHERE \"Id\" = @r",
            ("r", generation));

        await DrainAsync();

        var row = await RunRow(generation);
        Assert.Equal(("Completed", null), (row.Status, row.ErrorCode));
        Assert.Equal([Graph.DocAnalysisV3, Graph.DocDraft], _model.Graphs);   // il run v2 usa il prompt e il messaggio con UUID della v2, mai l'alias
        Assert.Equal([DocumentSourceDraftWorkflow.DefinitionVersion], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @o", ("o", generation)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", generation)));
    }

    [Fact]
    public async Task The_regime_probe_payload_of_a_generation_root_follows_its_frozen_definition()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var analysis = await StartAnalysis(client, project);
        await DrainAsync();
        var revision = await ConfirmAsync(client, project, analysis);
        var generation = await StartGeneration(client, project, analysis, revision);
        var draft = App.Services.GetRequiredService<DocumentSourceDraftWorkflow>();

        var v3 = await draft.BuildRegimePayloadAsync(generation, Ct);
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-document-documents-v2' WHERE \"Id\" = @r", ("r", generation));
        var v2 = await draft.BuildRegimePayloadAsync(generation, Ct);

        Assert.Equal(DraftAliasContracts.JsonSchema().GetRawText(), v3.SchemaJson);
        Assert.Equal(DocumentSourceDraftWorkflow.InstructionsV3, v3.Payload!.Instructions);
        Assert.Equal(DocumentSourceDraftWorkflow.Instructions, v2.Payload!.Instructions);
        Assert.NotEqual(v2.SchemaJson, v3.SchemaJson);
    }

    // ================================================================ T064: attribuzione document_sources nella composizione della chiamata realmente inviata

    private static int WordsOf(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private async Task<Dictionary<string, (int Tokens, int Messages)>> CompositionOfAsync(string ownerColumn, Guid owner)
    {
        var json = await Scalar<string>($"SELECT \"Composition\"::text FROM \"InferenceCalls\" WHERE \"{ownerColumn}\" = @o AND \"IsFinal\" = true", ("o", owner));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("key").GetString()!, i => (i.GetProperty("tokens").GetInt32(), i.TryGetProperty("messageCount", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0));
    }

    [Fact]
    public async Task The_document_chat_composition_attributes_the_evidence_block_to_document_sources_and_the_pm_question_to_user_messages()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var turn = await PostChat(client, project, "Quanti ordini al giorno puo emettere il cliente A?");
        await DrainAsync();

        var sent = Assert.Single(_model.Texts);                                         // un solo messaggio utente: evidenze + domanda
        var split = sent.IndexOf("\nMessaggio del project manager:\n", StringComparison.Ordinal);
        Assert.True(split > 0);
        var composition = await CompositionOfAsync("ChatTurnId", turn);

        // Né il testo del PM né l'istruzione di risposta sono fonti documentali; né le evidenze sono testo del PM: ogni parte nella sua categoria.
        Assert.Equal(WordsOf(sent[..split]), composition[ContextComposition.DocumentSources].Tokens);
        Assert.Equal(WordsOf(sent[split..]), composition[ContextComposition.UserMessages].Tokens);
        Assert.Equal(1, composition[ContextComposition.DocumentSources].Messages);
    }

    [Fact]
    public async Task The_document_analysis_composition_attributes_only_the_evidence_entries_to_document_sources_not_the_server_data_or_the_dialog()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var run = await StartAnalysis(client, project);
        await DrainAsync();

        var sent = Assert.Single(_model.Texts);
        var entries = Regex.Matches(sent, "<<<VOCE .*?<<<FINE VOCE>>>\n", RegexOptions.Singleline).Select(m => m.Value).ToList();
        Assert.NotEmpty(entries);
        Assert.Contains("origine=Document", entries[0]);
        var composition = await CompositionOfAsync("ProcessingRunId", run);

        Assert.Equal(entries.Sum(WordsOf), composition[ContextComposition.DocumentSources].Tokens);
        Assert.Equal(WordsOf(sent) - entries.Sum(WordsOf), composition[ContextComposition.UserMessages].Tokens);
    }

    [Fact]
    public async Task The_document_draft_composition_is_not_attributed_to_document_sources_because_pm_edits_and_sources_share_the_revision_json()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var analysis = await StartAnalysis(client, project);
        await DrainAsync();
        var revision = await ConfirmAsync(client, project, analysis);
        var generation = await StartGeneration(client, project, analysis, revision);
        await DrainAsync();

        var composition = await CompositionOfAsync("ProcessingRunId", generation);

        Assert.DoesNotContain(ContextComposition.DocumentSources, composition.Keys);   // attribuzione non dimostrabile: nessuna cifra inventata
        Assert.True(composition[ContextComposition.AnalysisStructured].Tokens > 0);
    }

    /// <summary>T124: il root documentale e stato classificato (record unico, payload misurato se determinabile) PRIMA del primo claim, con la deadline dal t0 originale.</summary>
    private async Task AssertClassifiedBeforeFirstClaim(string table, string ownerKind, Guid root, bool expectedPayloadMeasured)
    {
        Assert.Equal("Classified", await Scalar<string>($"SELECT \"ClassificationStatus\" FROM \"{table}\" WHERE \"Id\" = @r", ("r", root)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ClassificationRecords\" WHERE \"OwnerKind\" = @k AND \"OwnerId\" = @r", ("k", ownerKind), ("r", root)));
        Assert.Equal(expectedPayloadMeasured ? "measured" : "unavailable", await Scalar<string>("SELECT \"TokenQuality\" FROM \"ClassificationRecords\" WHERE \"OwnerId\" = @r", ("r", root)));
        Assert.True(await Scalar<bool>($"SELECT c.\"ObservedAt\" <= x.\"StartedAt\" FROM \"ClassificationRecords\" c JOIN \"{table}\" x ON x.\"Id\" = c.\"OwnerId\" WHERE c.\"OwnerId\" = @r", ("r", root)));
        Assert.True(await Scalar<bool>($"SELECT x.\"HardDeadlineAt\" = x.\"RequestedAt\" + interval '3600 seconds' AND x.\"EffectiveDeadlineAt\" = CASE x.\"Regime\" WHEN 'Ordinary' THEN x.\"RequestedAt\" + interval '180 seconds' ELSE x.\"HardDeadlineAt\" END FROM \"{table}\" x WHERE x.\"Id\" = @r", ("r", root)));
    }

    // ================================================================ grafi 001: ramo senza documenti, definizione congelata, checkpoint

    [Fact]
    public async Task Without_documents_chat_analysis_and_generation_keep_the_001_graphs()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var turn = await PostChat(client, project, "Vorrei un portale clienti.");
        await DrainAsync();
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));

        var (analysis, revision) = await LegacyConfirmedAnalysis(client, project);
        var generation = await StartGeneration(client, project, analysis, revision);
        await DrainAsync();

        Assert.Equal([Graph.Chat001, Graph.Analysis001, Graph.Draft001], _model.Graphs);
        Assert.Equal(("Completed", null), (await RunRow(analysis)).Pair());
        Assert.Equal(("Completed", null), (await RunRow(generation)).Pair());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\""));                           // nessun grafo 2
        Assert.Equal(["project-analysis-v3", "project-document-v1"], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" ORDER BY 1"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkflowCheckpoints\" WHERE \"ChatTurnId\" IS NOT NULL"));
    }

    [Fact]
    public async Task Work_requested_without_ready_sources_stays_on_001_even_if_a_source_is_included_before_execution()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewReadyDocument(client, project, "regole", Statement);
        await SetIncludedAsync(client, project, doc.Document, included: false);                         // alla richiesta nessuna fonte pronta e inclusa
        var turn = await PostChat(client, project, "Cosa dice il documento?");
        await PostChat(client, project, "Vorrei un portale clienti.", expectTurn: false);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var run = await StartAnalysis(client, project);
        await SetIncludedAsync(client, project, doc.Document, included: true);                          // la selezione corrente ora ha una fonte pronta

        await DrainAsync();

        Assert.Equal(("Completed", null), (await RunRow(run)).Pair());
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal([Graph.Chat001, Graph.Analysis001], _model.Graphs);
        Assert.DoesNotContain(Statement, string.Concat(_model.Texts));                                  // la selezione corrente non allarga la richiesta
        Assert.Equal("project-analysis-v3", (await RunRow(run)).Definition);
    }

    [Fact]
    public async Task A_chat_turn_frozen_with_only_pending_documents_stays_on_001_and_declares_the_unavailable_source()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var pending = await NewPendingDocument(client, project, "attesa");
        var turn = await PostChat(client, project, "Cosa dice il documento?");
        await MakeReady(pending, Statement);                                                            // pronto DOPO la richiesta: lo snapshot non cambia

        await DrainAsync();

        Assert.Equal([Graph.Chat001], _model.Graphs);
        Assert.Contains("Nota sulle fonti", await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t", ("t", turn)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\""));
    }

    [Fact]
    public async Task A_document_run_keeps_the_document_graph_when_the_current_selection_no_longer_has_ready_sources()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewReadyDocument(client, project, "regole", Statement);
        var run = await StartAnalysis(client, project);
        await SetIncludedAsync(client, project, doc.Document, included: false);                         // la selezione corrente non ha piu fonti

        await DrainAsync();

        Assert.Equal(("Completed", null), (await RunRow(run)).Pair());
        Assert.Equal([Graph.DocAnalysisV3], _model.Graphs);
        Assert.Contains(Statement, string.Concat(_model.Texts));                                        // fonti dello snapshot congelato
    }

    [Fact]
    public async Task A_001_checkpoint_resumes_the_original_graph_after_a_transient_failure()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await PostChat(client, project, "Vorrei un portale clienti.", expectTurn: false);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var run = await StartAnalysis(client, project);
        _model.FailNext(Graph.Analysis001);

        await DrainAsync();
        Assert.Equal("Queued", (await RunRow(run)).Status);                                             // errore transitorio: nuovo tentativo con backoff
        await Execute("UPDATE \"ProcessingRuns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run));
        await DrainAsync();

        Assert.Equal(("Completed", null), (await RunRow(run)).Pair());
        Assert.Equal([Graph.Analysis001, Graph.Analysis001], _model.Graphs);                            // invio fallito + invio riuscito, mai un grafo 2
        Assert.Equal(2, (await RunRow(run)).Attempts);
        Assert.Equal(["project-analysis-v3"], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @o", ("o", run)));
    }

    [Fact]
    public async Task A_document_run_resumes_the_document_graph_after_a_transient_failure_and_an_application_restart()
    {
        using var first = await App.LoginAsync();
        var project = await NewProject(first);
        await NewReadyDocument(first, project, "regole", Statement);
        var run = await StartAnalysis(first, project);
        _model.FailNext(Graph.DocAnalysisV3);

        await DrainAsync();
        Assert.Equal("Queued", (await RunRow(run)).Status);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

        await RestartAppAsync();                                                                        // nuovo processo, stesso database
        await Execute("UPDATE \"ProcessingRuns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run));
        await DrainAsync();

        var row = await RunRow(run);
        Assert.Equal(("Completed", null), row.Pair());
        Assert.Equal(2, row.Attempts);
        Assert.Equal([Graph.DocAnalysisV3, Graph.DocAnalysisV3], _model.Graphs);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal([DocumentAnalysisWorkflow.DefinitionVersionV3], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @o", ("o", run)));

        // Replay: un run terminale non si rielabora e non invia nulla.
        Assert.False(await Dispatcher().RunOnceAsync(Ct));
        Assert.Equal(2, _model.Graphs.Count);
    }

    [Fact]
    public async Task A_v2_document_run_resumes_the_v2_graph_after_a_transient_failure_and_an_application_restart()
    {
        using var first = await App.LoginAsync();
        var project = await NewProject(first);
        await NewReadyDocument(first, project, "regole", Statement);
        var run = await StartAnalysis(first, project);
        await PinToV2Async(run);
        _model.FailNext(Graph.DocAnalysis);

        await DrainAsync();
        Assert.Equal("Queued", (await RunRow(run)).Status);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

        await RestartAppAsync();                                                                        // nuovo processo, stesso database
        await Execute("UPDATE \"ProcessingRuns\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", run));
        await DrainAsync();

        var row = await RunRow(run);
        Assert.Equal(("Completed", null), row.Pair());
        Assert.Equal(2, row.Attempts);
        Assert.Equal([Graph.DocAnalysis, Graph.DocAnalysis], _model.Graphs);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal([DocumentAnalysisWorkflow.DefinitionVersion], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ProcessingRunId\" = @o", ("o", run)));

        // Replay: un run terminale non si rielabora e non invia nulla.
        Assert.False(await Dispatcher().RunOnceAsync(Ct));
        Assert.Equal(2, _model.Graphs.Count);
    }

    // ================================================================ T124/T125: il preflight misura lo schema del contratto congelato sul root

    [Fact]
    public void The_regime_preflight_measures_the_alias_dto_schema_for_v3_roots_and_the_v2_wrapper_for_v2_and_unknown_roots()
    {
        var analysis = App.Services.GetRequiredService<DocumentAnalysisWorkflow>();

        var v3 = analysis.BuildRegimePayload(DocumentAnalysisWorkflow.DefinitionVersionV3);
        var v2 = analysis.BuildRegimePayload(DocumentAnalysisWorkflow.DefinitionVersion);

        Assert.Equal(AnalysisAliasContracts.JsonSchema().GetRawText(), v3.SchemaJson);
        Assert.Contains("\"alias-1\"", v3.SchemaJson);
        Assert.DoesNotContain("evidenceMap", v3.SchemaJson);                                   // l'involucro v2 non e piu quello misurato per v3
        Assert.Equal(analysis.BuildRegimePayload().SchemaJson, v2.SchemaJson);
        Assert.Contains("evidenceMap", v2.SchemaJson);
        Assert.Equal(v2.SchemaJson, analysis.BuildRegimePayload("project-analysis-documents-v9").SchemaJson);   // ignota: nessun upgrade implicito (l'esito e del claim)
        Assert.Equal((AiPhase.Analysis, (DocumentPayload?)null), (v3.Phase, v3.Payload));
    }

    // ================================================================ definizione ignota o incompatibile: errore esplicito

    [Theory]
    [InlineData("project-analysis-documents-v9", """{"analysis":"document-analysis-v2","analysisSchema":"1.1.0"}""")]      // definizione ignota
    [InlineData("project-document-documents-v2", """{"analysis":"document-analysis-v2","analysisSchema":"1.1.0"}""")]      // definizione di un'altra famiglia
    [InlineData("project-analysis-documents-v2", """{"analysis":"document-analysis-v1","analysisSchema":"1.1.0"}""")]      // prompt congelato diverso
    [InlineData("project-analysis-v3", """{"analysis":"document-analysis-v2"}""")]                                         // 001 con prompt documentale: nessuna reinterpretazione
    [InlineData("project-analysis-documents-v3", """{"analysis":"document-analysis-v2","analysisSchema":"1.1.0"}""")]  // v3 con prompt v2: mai un downgrade
    [InlineData("project-analysis-documents-v3", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0"}""")]  // v3 senza schema AI, catalogo e id canonici congelati
    [InlineData("project-analysis-documents-v2", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1"}""")]   // v2 con prompt v3: mai un upgrade
    [InlineData("project-analysis-documents-v4", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1"}""")]   // definizione futura ignota
    public async Task An_unknown_or_incompatible_analysis_definition_fails_explicitly_and_never_reaches_the_model(string definition, string prompts)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewReadyDocument(client, project, "regole", Statement);
        var run = await StartAnalysis(client, project);
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r", ("d", definition), ("p", prompts), ("r", run));

        await DrainAsync();

        var row = await RunRow(run);
        Assert.Equal(("Failed", "workflow_incompatible"), (row.Status, row.ErrorCode));
        Assert.Empty(_model.Graphs);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Theory]
    [InlineData("project-document-documents-v9", """{"document":"document-documents-v2","analysisSchema":"1.1.0"}""")]
    [InlineData("project-document-documents-v3", """{"document":"document-documents-v2","analysisSchema":"1.1.0"}""")]   // v3 con prompt v2: mai un downgrade
    [InlineData("project-document-documents-v3", """{"document":"document-draft-v3","analysisSchema":"1.1.0"}""")]       // v3 senza schema, catalogo, algoritmo e validazione congelati
    [InlineData("project-document-documents-v2", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-1","draftCatalog":"draft-alias-catalog-1","algorithm":"draft-single-v2","validation":"document-draft-validation-v2"}""")]   // v2 con prompt v3: mai un upgrade
    [InlineData("project-document-documents-v4", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-1","draftCatalog":"draft-alias-catalog-1","algorithm":"draft-single-v2","validation":"document-draft-validation-v2"}""")]   // definizione futura ignota
    [InlineData("project-document-documents-v2", """{"document":"document-documents-v1","analysisSchema":"1.1.0"}""")]
    [InlineData("project-analysis-documents-v2", """{"document":"document-documents-v2"}""")]
    public async Task An_unknown_or_incompatible_generation_definition_fails_explicitly_and_never_reaches_the_model(string definition, string prompts)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var (analysis, revision) = await LegacyConfirmedAnalysis(client, project);
        var generation = await StartGeneration(client, project, analysis, revision);
        _model.Reset();
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r", ("d", definition), ("p", prompts), ("r", generation));

        await DrainAsync();

        var row = await RunRow(generation);
        Assert.Equal(("Failed", "workflow_incompatible"), (row.Status, row.ErrorCode));
        Assert.Empty(_model.Graphs);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", generation)));
    }

    // ================================================================ supporto: dispatcher e dati

    /// <summary>Simula un run richiesto prima dell'introduzione del contratto con alias: definizione e prompt v2 congelati sul root.</summary>
    private async Task PinToV2Async(Guid run) => await Execute(
        "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-documents-v2', \"PromptVersions\" = '{\"analysis\":\"document-analysis-v2\",\"analysisSchema\":\"1.1.0\"}'::jsonb WHERE \"Id\" = @r",
        ("r", run));

    private AiDispatchService Dispatcher() => ActivatorUtilities.CreateInstance<AiDispatchService>(App.Services);

    /// <summary>Esegue i lavori eleggibili (estrazioni deterministiche incluse) finche il dispatcher non ha piu nulla da prendere in carico.</summary>
    private async Task DrainAsync()
    {
        var dispatcher = Dispatcher();
        for (var i = 0; i < 20 && await dispatcher.RunOnceAsync(Ct); i++)
        {
        }
    }

    private sealed record Doc(Guid Document, Guid Version, Guid SourceId);

    private sealed record RunState(string Status, string? ErrorCode, string Definition, int Attempts)
    {
        public (string, string?) Pair() => (Status, ErrorCode);
    }

    private async Task<RunState> RunRow(Guid run)
    {
        await using var connection = await OpenConnection();
        await using var command = new NpgsqlCommand("SELECT \"Status\", \"ErrorCode\", \"WorkflowDefinitionVersion\", \"AttemptCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", connection);
        command.Parameters.AddWithValue("r", run);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
    }

    private async Task<List<string>> Strings(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = await OpenConnection();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private async Task<Guid> PostChat(HttpClient client, Guid project, string text, bool expectTurn = true)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var turn = (await Json(response)).GetProperty("chatTurnId").GetGuid();
        if (!expectTurn)
        {
            // Messaggio solo come input dell'analisi: il suo turno non interessa la prova e non deve consumare invii del modello.
            await Execute("UPDATE \"ChatTurns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'test_skipped', \"CompletedAt\" = now() WHERE \"Id\" = @t", ("t", turn));
        }

        return turn;
    }

    private async Task<Guid> StartAnalysis(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ConfirmAsync(HttpClient client, Guid project, Guid analysis)
    {
        var preview = await client.GetAsync($"/api/projects/{project}/analyses/{analysis}", Ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var revision = (await Json(preview)).GetProperty("currentRevisionId").GetGuid();
        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/confirm");
        confirm.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(confirm, Ct)).StatusCode);
        return revision;
    }

    private async Task<Guid> StartGeneration(HttpClient client, Guid project, Guid analysis, Guid revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Analisi 001 (nessun documento) completata dal dispatcher e confermata dal PM: base della generazione 001.</summary>
    private async Task<(Guid Analysis, Guid Revision)> LegacyConfirmedAnalysis(HttpClient client, Guid project)
    {
        await PostChat(client, project, "Vogliamo un portale clienti.", expectTurn: false);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var analysis = await StartAnalysis(client, project);
        await DrainAsync();
        Assert.Equal(("Completed", null), (await RunRow(analysis)).Pair());
        return (analysis, await ConfirmAsync(client, project, analysis));
    }

    private async Task SetIncludedAsync(HttpClient client, Guid project, Guid document, bool included)
    {
        var revision = (await Json(await client.GetAsync($"/api/projects/{project}/source-selection", Ct))).GetProperty("revision").GetInt64();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection")
        {
            Content = JsonContent.Create(new { items = new[] { new { documentId = document, included, mode = "LatestReady", versionId = (Guid?)null } } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, Ct)).StatusCode);
    }

    private async Task<Doc> NewPendingDocument(HttpClient client, Guid project, string name)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        return new Doc(created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid(), Guid.NewGuid());
    }

    private async Task<Doc> NewReadyDocument(HttpClient client, Guid project, string name, string statement) =>
        await MakeReady(await NewPendingDocument(client, project, name), statement);

    /// <summary>Rende pronta una versione con una evidenza e una fonte (stessa scrittura dei test dei workflow documentali).</summary>
    private async Task<Doc> MakeReady(Doc doc, string statement)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == doc.Version, Ct);
        var now = DateTimeOffset.UtcNow;
        var (job, version) = (Guid.NewGuid(), Guid.NewGuid());
        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 1, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var content = new DocumentEvidenceContent
        {
            SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
            KindRationale = null,
            Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
            Qualifiers = [],
            Sources =
            [
                new PersistedSource
                {
                    SourceId = doc.SourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                    MessageId = null, Sequence = null, DocumentId = doc.Document, DocumentVersionId = doc.Version, PreparationId = preparation.Id,
                    ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = statement.Length, Quote = statement,
                },
            ],
        };
        db.Evidences.Add(new EvidenceItem
        {
            Id = Guid.NewGuid(), VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = 1,
            Key = "e1", Kind = EvidenceKind.Requirement, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement,
            Content = DocumentEvidenceCodec.EncodeContent(content),
        });
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = doc.Version, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = version, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = version;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == doc.Document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, doc.Version), Ct);
        return doc;
    }

    // ================================================================ supporto: modello finto (HTTP, llama.cpp simulato)

    internal enum Graph { DocChat, DocChatV3, Chat001, DocAnalysis, DocAnalysisV3, Analysis001, DocDraft, DocDraftV3, Draft001, Other }

    private sealed record Req(Graph Graph, string System, string Text);

    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static Graph Classify(string system)
    {
        var s = Norm(system);
        return s.Contains(Norm(DocumentChatWorkflow.Instructions)) ? Graph.DocChat
            : s.Contains(Norm(DocumentChatWorkflow.InstructionsV3)) ? Graph.DocChatV3
            : s.Contains(Norm(DocumentAnalysisWorkflow.Instructions)) ? Graph.DocAnalysis
            : s.Contains(Norm(DocumentAnalysisWorkflow.InstructionsV3)) ? Graph.DocAnalysisV3
            : s.Contains(Norm(DocumentSourceDraftWorkflow.Instructions)) ? Graph.DocDraft
            : s.Contains(Norm(DocumentSourceDraftWorkflow.InstructionsV3)) ? Graph.DocDraftV3
            : s.Contains(Norm(ConversationalAgent.ConsolidationInstructions)) ? Graph.Analysis001
            : s.Contains(Norm(DocumentAgent.Instructions)) ? Graph.Draft001
            : s.Contains(Norm(ConversationalAgent.Instructions)) ? Graph.Chat001
            : Graph.Other;
    }

    /// <summary>Risposta valida per il tipo di grafo che ha inviato la richiesta (deterministica, dai soli dati della richiesta e del database).</summary>
    private string Reply(Req request) => request.Graph switch
    {
        Graph.DocChat => JsonSerializer.Serialize(new { mode = "documented", answer = $"Massimo 5 ordini al giorno [fonte:{Regex.Match(request.Text, "fonte:([0-9a-f-]{36})").Groups[1].Value}]." }),
        Graph.DocChatV3 => _chatOverride?.Invoke(request)
            ?? JsonSerializer.Serialize(new { mode = "documented", answer = $"Massimo 5 ordini al giorno [fonte:{Regex.Match(request.Text, "fonte:(F[0-9]+) ").Groups[1].Value}]." }),   // alias, mai UUID
        Graph.Chat001 => "Quali utenti userebbero il portale?",
        Graph.DocAnalysis => DocumentAnalysisReply(request.Text),
        Graph.DocAnalysisV3 => AliasModelReplies.Reply(request.Text),   // il modello vede solo alias e produce il DTO semantico
        Graph.Analysis001 => LegacyAnalysisReply(request.Text),
        Graph.DocDraft => DocumentDraftReply(request.Text),
        Graph.DocDraftV3 => DraftAliasModelReplies.Reply(request.Text),   // alias nel messaggio, DTO con alias nella risposta
        Graph.Draft001 => $"# Documento\n\n- Obiettivo [fonte:{Regex.Match(request.Text, "\"sources\"\\s*:\\s*\\[\\s*\\{\\s*\"id\"\\s*:\\s*\"([0-9a-f-]{36})\"").Groups[1].Value}]",
        _ => throw new InvalidOperationException("Richiesta al modello di un grafo non riconosciuto."),
    };

    private static string LegacyAnalysisReply(string request)
    {
        string Match(string pattern) => Regex.Match(request, pattern).Groups[1].Value;
        var message = Regex.Match(request, @"\[messaggio id=([0-9a-f-]{36}) sequenza=(\d+)\]");
        var source = Guid.NewGuid().ToString();
        return new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["analysisId"] = Match(@"analysisId: ([0-9a-f-]{36})"),
            ["projectId"] = Match(@"projectId: ([0-9a-f-]{36})"),
            ["sourceCutoffSequence"] = long.Parse(Match(@"sourceCutoffSequence: (\d+)")),
            ["validationState"] = "Unvalidated",
            ["sources"] = new JsonArray(new JsonObject
            {
                ["id"] = source, ["kind"] = "Message", ["messageId"] = message.Groups[1].Value, ["messageRole"] = "user", ["revisionId"] = null,
            }),
            ["goals"] = new JsonArray(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["text"] = "Realizzare un portale clienti", ["classification"] = "Declared", ["evidenceRefs"] = new JsonArray(JsonValue.Create(source)),
            }),
            ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
            ["requirements"] = new JsonArray(),
            ["operationalFlows"] = new JsonArray(),
            ["constraints"] = new JsonArray(),
            ["dependencies"] = new JsonArray(),
            ["assumptions"] = new JsonArray(),
            ["openQuestions"] = new JsonArray(),
            ["conflicts"] = new JsonArray(),
        }.ToJsonString();
    }

    private string DocumentAnalysisReply(string request)
    {
        var run = Guid.Parse(Regex.Match(request, "analysisId: ([0-9a-f-]{36})").Groups[1].Value);
        using var scope = App.Services.CreateScope();
        var input = DocumentAnalysisSources.LoadAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), run, Ct).GetAwaiter().GetResult();
        ReconcileResult Result(ReconcileView view) => new()
        {
            BatchId = input.BatchKey, InputHash = input.InputHash, View = view, Level = 0,
            VisitedEntryIds = [.. input.Snapshot.Evidence.Select(e => ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)).Order(StringComparer.Ordinal)],
            VisitedNodeIds = [], Candidates = [], Dispositions = [], ItemProposals = [], Patches = [], Descriptor = null,
            NoFindingsReason = "nessun collegamento oltre quelli già noti",
        };

        static Guid ItemId(Guid evidence) => new(System.Security.Cryptography.SHA256.HashData(evidence.ToByteArray())[..16]);
        static DocumentAnalysisSource Source(PersistedSource s) => s.Kind == EvidenceSourceKind.Message
            ? new()
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.Message, MessageId = s.MessageId, MessageRole = AnalysisMessageRole.User, RevisionId = null, DocumentId = null,
                DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
            }
            : new()
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = s.DocumentId,
                DocumentVersionId = s.DocumentVersionId, PreparationId = s.PreparationId, ArtifactId = s.ArtifactId, BlockId = s.BlockId, Start = s.Start, End = s.End, Quote = s.Quote,
            };

        var output = new SingleCallOutput
        {
            Results = [Result(ReconcileView.A), Result(ReconcileView.B), Result(ReconcileView.Cross)],
            Analysis = new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = input.AnalysisId, ProjectId = input.ProjectId, SourceCutoffSequence = input.Cutoff,
                ValidationState = AnalysisContentState.Unvalidated, Sources = [.. input.Evidence.SelectMany(e => e.Sources.Select(Source))], Goals = [],
                ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
                Requirements =
                [
                    .. input.Evidence.Select(e => new DocumentAnalysisItem
                    {
                        Id = ItemId(e.EvidenceId), Text = e.Statement,
                        Classification = e.Origin == EvidenceOriginKind.Message ? DocumentAnalysisClassification.Declared : DocumentAnalysisClassification.Documented,
                        EvidenceRefs = [.. e.Sources.Select(s => s.SourceId)],
                    }),
                ],
                OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
            },
            EvidenceMap = [.. input.Evidence.Select(e => new EvidenceMapEntry { ItemId = ItemId(e.EvidenceId), EvidenceIds = [e.EvidenceId] })],
        };
        return ReconciliationCodec.Encode(output);
    }

    /// <summary>Un unico blocco con tutti gli item della revisione confermata e le loro fonti, letti dal JSON che il server ha inviato nel messaggio.</summary>
    private static string DocumentDraftReply(string request)
    {
        var start = request.IndexOf("completo:\n", StringComparison.Ordinal) + "completo:\n".Length;
        var end = request.LastIndexOf("\nRispondi ora", StringComparison.Ordinal);
        using var content = JsonDocument.Parse(request[start..end]);
        var root = content.RootElement;
        var items = new List<JsonElement>();
        foreach (var name in new[] { "goals", "requirements", "operationalFlows", "constraints", "dependencies", "assumptions", "openQuestions" })
        {
            items.AddRange(root.GetProperty(name).EnumerateArray());
        }

        foreach (var section in new[] { "overview", "included", "excluded" })
        {
            items.AddRange(root.GetProperty("contextAndScope").GetProperty(section).EnumerateArray());
        }

        foreach (var conflict in root.GetProperty("conflicts").EnumerateArray())
        {
            items.AddRange(conflict.GetProperty("alternatives").EnumerateArray());
        }

        var ids = items.Select(i => i.GetProperty("id").GetString()!).ToList();
        var sources = items.SelectMany(i => i.GetProperty("evidenceRefs").EnumerateArray().Select(r => r.GetString()!)).Distinct().ToList();
        var markdown = "- Requisiti confermati " + string.Join(' ', sources.Select(s => $"[fonte:{s}]"));
        return new JsonObject
        {
            ["sectionKey"] = "documento",
            ["blocks"] = new JsonArray(new JsonObject
            {
                ["itemIds"] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]),
                ["markdown"] = markdown,
                ["sourceIds"] = new JsonArray([.. sources.Select(i => (JsonNode)JsonValue.Create(i)!)]),
            }),
        }.ToJsonString();
    }

    private sealed class RoutingModel(Func<Req, string> reply) : HttpMessageHandler
    {
        private readonly List<Req> _requests = [];
        private readonly List<Graph> _failNext = [];

        public IReadOnlyList<Graph> Graphs
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests.Select(r => r.Graph)];
                }
            }
        }

        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests.Select(r => r.Text)];
                }
            }
        }

        /// <summary>La prossima richiesta di quel grafo riceve 503 (errore transitorio del modello interno).</summary>
        public void FailNext(Graph graph)
        {
            lock (_requests)
            {
                _failNext.Add(graph);
            }
        }

        public void Reset()
        {
            lock (_requests)
            {
                _requests.Clear();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models":
                    return Json("""{"object":"list","data":[{"id":"test-model","object":"model","status":{"value":"loaded","args":["llama-server"]}}]}""");
                case "/props":
                    return Json("{\"default_generation_settings\":{\"n_ctx\":32768,\"params\":{\"n_predict\":-1}},\"total_slots\":1}");
                case "/tokenize":
                {
                    using var doc = JsonDocument.Parse(body);
                    var count = Words(doc.RootElement.GetProperty("content").GetString()!);
                    return Json("{\"tokens\":[" + string.Join(',', Enumerable.Range(0, count)) + "]}");
                }

                case "/apply-template":
                {
                    using var doc = JsonDocument.Parse(body);
                    var prompt = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => $"[{m.GetProperty("role").GetString()}] {TextOf(m.GetProperty("content"))}"));
                    return Json(JsonSerializer.Serialize(new { prompt }));
                }

                case "/v1/chat/completions":
                {
                    using var doc = JsonDocument.Parse(body);
                    var messages = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (Role: m.GetProperty("role").GetString()!, Text: TextOf(m.GetProperty("content")))).ToList();
                    var system = string.Join("\n", messages.Where(m => m.Role is "system" or "developer").Select(m => m.Text));
                    var record = new Req(Classify(system), system, string.Join("\n", messages.Where(m => m.Role is not ("system" or "developer")).Select(m => m.Text)));
                    lock (_requests)
                    {
                        _requests.Add(record);
                        if (_failNext.Remove(record.Graph))
                        {
                            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"giu\"}}", Encoding.UTF8, "application/json") };
                        }
                    }

                    var content = reply(record);
                    var stream = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
                    return stream ? Sse(content) : Json(JsonSerializer.Serialize(new
                    {
                        id = "x", @object = "chat.completion", created = 1, model = "test-model",
                        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
                        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
                    }));
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Sse(string content)
        {
            string Chunk(object delta, string? finish, object? usage = null) =>
                "data: " + JsonSerializer.Serialize(new { id = "x", @object = "chat.completion.chunk", created = 1, model = "test-model", choices = new[] { new { index = 0, delta, finish_reason = finish } }, usage }) + "\n\n";
            var text = Chunk(new { role = "assistant", content }, null) + Chunk(new { }, "stop", new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }) + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
        }

        private static string TextOf(JsonElement content) => content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));

        private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
