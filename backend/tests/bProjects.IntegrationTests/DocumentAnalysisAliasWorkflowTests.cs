using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// Risposte del modello finto per il contratto v3: leggono SOLO il messaggio realmente inviato (alias `[E<n>]`, mai id), come farebbe un modello reale,
/// e producono il DTO `document-analysis-alias-1` con un requisito per evidenza.
/// </summary>
internal static class AliasModelReplies
{
    private static readonly Regex Entry = new(@"<<<VOCE \[(E[1-9][0-9]*)\] origine=(\w+)[^>\n]*>>>\n(.*?)\n<<<", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Relation = new(@"^\[(R[1-9][0-9]*)\] ", RegexOptions.Multiline | RegexOptions.Compiled);

    public static AiAnalysisOutput Dto(string prompt, Func<AiAnalysisOutput, AiAnalysisOutput>? change = null)
    {
        var entries = Entry.Matches(prompt).Select(m => (Alias: m.Groups[1].Value, Origin: m.Groups[2].Value, Text: m.Groups[3].Value)).ToList();
        var aliases = entries.Select(e => e.Alias).ToList();
        var items = entries.Select((e, i) => AliasFixtures.Item(
            $"i{i + 1}", e.Text, e.Origin == "Message" ? DocumentAnalysisClassification.Declared : DocumentAnalysisClassification.Documented, e.Alias)).ToList();
        var dto = new AiAnalysisOutput
        {
            Contract = "alias-1",
            Visited = new() { A = aliases, B = aliases },
            NoFindings = new() { A = "Nessuna relazione oltre quelle note.", B = "Nessuna relazione oltre quelle note.", Cross = null },
            Items = new()
            {
                Goals = [], ScopeOverview = [], ScopeIncluded = [], ScopeExcluded = [], Requirements = items, Flows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [],
            },
            Conflicts = [], Findings = [],
            KnownRelationChecks = [.. Relation.Matches(prompt).Select(m => new AiKnownRelationCheck { Relation = m.Groups[1].Value, Verdict = AiKnownRelationVerdict.Confirmed, Rationale = "Relazione nota confermata." })],
            Patches = [],
        };
        return change is null ? dto : change(dto);
    }

    public static string Reply(string prompt, Func<AiAnalysisOutput, AiAnalysisOutput>? change = null) => AnalysisAliasContracts.Encode(Dto(prompt, change));
}

/// <summary>
/// Risposte del modello finto per il contratto v5 (alias-2): come <see cref="AliasModelReplies"/> leggono solo il messaggio inviato; per ogni evidenza `tipo=Gap` emettono una
/// domanda aperta e una valutazione Open/NoAnswer (il caso più prudente), per le altre un requisito. Le prove N4 sostituiscono il DTO con quello atteso.
/// </summary>
internal static class AliasModelRepliesV5
{
    private static readonly Regex Entry = new(@"<<<VOCE \[(E[1-9][0-9]*)\] origine=(\w+) tipo=(\w+)[^>\n]*>>>\n(.*?)\n<<<", RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<(string Alias, string Origin, string Kind, string Text)> Entries(string prompt) =>
        [.. Entry.Matches(prompt).Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value))];

    public static AiAnalysisOutputV2 Dto(string prompt, Func<AiAnalysisOutputV2, AiAnalysisOutputV2>? change = null)
    {
        var entries = Entries(prompt);
        var aliases = entries.Select(e => e.Alias).ToList();
        var requirements = new List<AiItem>();
        var questions = new List<AiItem>();
        var records = new List<AiGapAssessment>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var classification = e.Origin == "Message" ? DocumentAnalysisClassification.Declared : DocumentAnalysisClassification.Documented;
            if (e.Kind == "Gap")
            {
                var key = $"i{i + 1}";
                questions.Add(AliasFixtures.Item(key, e.Text, classification, e.Alias));
                records.Add(new() { Gap = e.Alias, Outcome = GapOutcome.Open, Reason = GapReason.NoAnswer, Rationale = "Nessun riscontro nelle fonti.", SupportingEvidence = [], OpenItem = key, OpenConflict = null });
            }
            else
            {
                requirements.Add(AliasFixtures.Item($"i{i + 1}", e.Text, classification, e.Alias));
            }
        }

        var dto = new AiAnalysisOutputV2
        {
            Contract = "alias-2",
            Visited = new() { A = aliases, B = aliases },
            NoFindings = new() { A = "Nessuna relazione oltre quelle note.", B = "Nessuna relazione oltre quelle note.", Cross = null },
            Items = new()
            {
                Goals = [], ScopeOverview = [], ScopeIncluded = [], ScopeExcluded = [], Requirements = requirements, Flows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = questions,
            },
            Conflicts = [], Findings = [], KnownRelationChecks = [], Patches = [], GapAssessments = records,
        };
        return change is null ? dto : change(dto);
    }

    public static string Reply(string prompt, Func<AiAnalysisOutputV2, AiAnalysisOutputV2>? change = null) => AnalysisAliasContracts.Encode(Dto(prompt, change));
}

/// <summary>
/// T125 parte B (MAF-PG) — analisi documentale v3 con alias: percorso API + dispatcher/workflow + PostgreSQL + MAF reali con IChatClient finto a livello HTTP
/// (nessun DGX). Il modello riceve solo alias e produce solo il DTO semantico; il server compone il canonico, lo valida con i validatori esistenti,
/// persiste il WorkOutput {aiOutputHash, catalogHash, canonicalHash} e ricompone a ogni lettura/commit (contracts/ai-workflow.md §7).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed partial class DocumentAnalysisAliasWorkflowTests : ArchiveTestBase
{
    private const string Statement1 = "Il cliente A puo emettere al massimo 5 ordini al giorno.";
    private const string Statement2 = "Il cliente A puo emettere al massimo 8 ordini al giorno.";
    private static readonly Regex AnyUuid = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", RegexOptions.Compiled);
    private static readonly Regex AnyHash = new("[0-9a-f]{64}", RegexOptions.Compiled);

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    // ================================================================ freeze alla richiesta

    [Fact]
    public async Task A_new_analyze_request_freezes_the_v4_definition_with_the_v3_alias_prompt_schema_catalog_and_canonical_id_versions()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);

        var run = await StartRunAsync(client, project);

        Assert.Equal("project-analysis-documents-v4", await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));   // T131: le nuove richieste sono v4
        var prompts = JsonDocument.Parse(await Scalar<string>("SELECT \"PromptVersions\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))).RootElement;
        Assert.Equal("document-analysis-v3", prompts.GetProperty("analysis").GetString());
        Assert.Equal("1.1.0", prompts.GetProperty("analysisSchema").GetString());
        Assert.Equal("document-analysis-alias-1", prompts.GetProperty("analysisAiSchema").GetString());
        Assert.Equal("alias-catalog-1", prompts.GetProperty("aliasCatalog").GetString());
        Assert.Equal("canon-ids-1", prompts.GetProperty("canonIds").GetString());
        Assert.Equal("analysis-single-v2", prompts.GetProperty("algorithm").GetString());
        Assert.Equal("document-analysis-validation-v2", prompts.GetProperty("validation").GetString());
    }

    // ================================================================ analisi completa

    [Fact]
    public async Task A_complete_alias_run_exposes_no_identifier_to_the_model_and_commits_the_canonical_revision_with_the_three_hashes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", Statement1);
        await SeedPmEvidenceAsync(client, project, "Il cliente A deve poter emettere almeno 10 ordini al giorno.");
        var run = await StartRunAsync(client, project);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };

        var result = await RunAsync(run, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(run));
        var request = Assert.Single(model.Requests);
        var everything = request.System + "\n" + request.AllText;
        Assert.Contains("[E1]", request.AllText);
        Assert.Contains(Statement1, request.AllText);
        Assert.Contains("Il cliente A deve poter emettere almeno 10 ordini al giorno.", request.AllText);
        Assert.Equal(DocumentAnalysisWorkflow.InstructionsV3.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));
        // N1 reale: `certezza=Declared` nell'intestazione di ogni voce e stato copiato come classificazione: il messaggio non la espone piu.
        Assert.DoesNotContain("certezza", request.AllText, StringComparison.OrdinalIgnoreCase);
        // N3 quarta esecuzione: senza relazioni note il messaggio dichiara l'assenza (niente alias R da copiare).
        Assert.Contains("Relazioni note: nessuna → knownRelationChecks deve essere [].", request.AllText);
        Assert.DoesNotMatch(@"\[R\d+\]", request.AllText);
        Assert.Matches(@"<<<VOCE \[E1\] origine=\w+ tipo=\w+ polarita=\w+>>>", request.AllText);
        Assert.False(AnyUuid.IsMatch(everything), "UUID nel prompt: " + AnyUuid.Match(everything).Value);
        Assert.False(AnyHash.IsMatch(everything), "hash nel prompt");
        Assert.DoesNotContain(input.BatchKey, everything);
        Assert.DoesNotContain(input.InputHash, everything);
        Assert.DoesNotContain("analysisId", everything);
        Assert.DoesNotContain("sourceCutoffSequence", everything);
        Assert.DoesNotContain("batchId", everything);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"Origin\" = 'Ai' AND \"SchemaVersion\" = '1.1.0' AND \"Version\" = 1", ("r", run)));
        Assert.Equal(run, await Scalar<Guid>("SELECT \"CurrentAnalysisId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", project)));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(2, content.Requirements.Count);
        Assert.Equal(2, content.Sources.Count);
        Assert.Contains(content.Sources, s => s.Id == doc.SourceIds[0] && s.Quote == Statement1);
        for (var i = 0; i < content.Requirements.Count; i++)
        {
            Assert.Equal(AnalysisAliasComposer.CanonicalId("item", run, input.Snapshot.SourceHash, $"i{i + 1}"), content.Requirements[i].Id);
        }

        var payload = JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run))).RootElement;
        var catalog = AnalysisAliasCatalog.Build(input);
        Assert.Equal(catalog.CatalogHash, payload.GetProperty("catalogHash").GetString());
        Assert.Matches("^[0-9a-f]{64}$", payload.GetProperty("aiOutputHash").GetString()!);
        var composed = AnalysisAliasComposer.Compose(AliasModelReplies.Reply(request.AllText), catalog, input);
        Assert.Equal(composed.CanonicalHash, payload.GetProperty("canonicalHash").GetString());
        Assert.Equal(composed.AiOutputHash, payload.GetProperty("aiOutputHash").GetString());
        Assert.Equal("document-analysis-alias-1", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("document-analysis-validation-v2", await Scalar<string>("SELECT \"ValidationVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("project-analysis-documents-v4", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task Running_a_completed_alias_run_again_returns_the_committed_result_without_a_second_call_or_revision()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };
        var first = await RunAsync(run, model);

        var second = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Completed, first.Status);
        Assert.Equal(first.ResultRevisionId, second.ResultRevisionId);
        Assert.Single(model.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    // ================================================================ retry con nuovo root

    [Fact]
    public async Task A_retry_with_a_new_root_reuses_the_same_dto_and_keeps_sources_evidence_and_run_independent_hashes_changing_only_run_derived_ids()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", Statement1, Statement2);
        var run1 = await StartRunAsync(client, project);
        var input1 = await PrepareAsync(run1);
        var model1 = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(run1, model1)).Status);

        var run2 = await StartRunAsync(client, project);
        var input2 = await PrepareAsync(run2);
        var model2 = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };
        var second = await RunAsync(run2, model2);

        Assert.True(second.Status == ProcessingRunStatus.Completed, "Failed: " + second.ErrorCode + " " + await ErrorMessage(run2));
        Assert.NotEqual(run1, run2);
        // Stesso messaggio (nessun dato di run) e quindi stesso DTO; stessa identita di fonti ed evidenze (ledger), stesso hash di catalogo senza run.
        Assert.Equal(model1.Requests[0].AllText, model2.Requests[0].AllText);
        var (catalog1, catalog2) = (AnalysisAliasCatalog.Build(input1), AnalysisAliasCatalog.Build(input2));
        Assert.Equal(catalog1.RunIndependentHash, catalog2.RunIndependentHash);
        Assert.NotEqual(catalog1.CatalogHash, catalog2.CatalogHash);
        Assert.Equal(input1.Snapshot.SourceHash, input2.Snapshot.SourceHash);
        Assert.Equal(await Scalar<string>("SELECT \"ConfigHash\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run1)), await Scalar<string>("SELECT \"ConfigHash\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run2)));
        // Il manifesto congela l'hash di catalogo SENZA id del run: stesso contenuto fra root e retry; il catalogHash con l'id del run sta nel WorkOutput di ciascun root.
        Assert.Equal(catalog1.RunIndependentHash, await Scalar<string>("SELECT \"PlanJson\"->>'catalog' FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run1)));
        Assert.Equal(catalog2.RunIndependentHash, await Scalar<string>("SELECT \"PlanJson\"->>'catalog' FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run2)));
        Assert.Equal(catalog1.CatalogHash, await Scalar<string>("SELECT \"PayloadJson\"->>'catalogHash' FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run1)));
        Assert.Equal(catalog2.CatalogHash, await Scalar<string>("SELECT \"PayloadJson\"->>'catalogHash' FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run2)));
        Assert.Equal(await Scalar<string>("SELECT \"PayloadJson\"->>'aiOutputHash' FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run1)), await Scalar<string>("SELECT \"PayloadJson\"->>'aiOutputHash' FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run2)));

        var content1 = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run1)));
        var content2 = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run2)));
        Assert.Equal(content1.Sources.Select(s => (s.Id, s.Quote)), content2.Sources.Select(s => (s.Id, s.Quote)));
        Assert.Equal(doc.SourceIds, content2.Sources.Select(s => s.Id));
        Assert.Equal(content1.Requirements.Select(i => (i.Text, i.EvidenceRefs)), content2.Requirements.Select(i => (i.Text, i.EvidenceRefs)));
        Assert.Empty(content1.Requirements.Select(i => i.Id).Intersect(content2.Requirements.Select(i => i.Id)));    // gli id derivati dal root cambiano, deterministicamente
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(AnalysisAliasComposer.CanonicalId("item", run2, input2.Snapshot.SourceHash, $"i{i + 1}"), content2.Requirements[i].Id);
        }

        // Stesso root, stesso DTO: id e canonicalHash identici a ogni ricomposizione.
        var json = AliasModelReplies.Reply(model2.Requests[0].AllText);
        Assert.Equal(AnalysisAliasComposer.Compose(json, catalog2, input2).CanonicalHash, AnalysisAliasComposer.Compose(json, AnalysisAliasCatalog.Build(input2), input2).CanonicalHash);
    }

    // ================================================================ snapshot congelato

    [Fact]
    public async Task A_document_added_after_the_post_does_not_enter_the_frozen_snapshot_of_the_run()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        await NewDocument(client, project, "altre", Statement2);       // pronto dopo la richiesta: non entra
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        var request = Assert.Single(model.Requests);
        Assert.Contains(Statement1, request.AllText);
        Assert.DoesNotContain(Statement2, request.AllText);
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Single(content.Requirements);
        Assert.Single(content.Sources);
    }

    // ================================================================ output del modello non validi

    [Theory]
    [InlineData("unknown_alias")]
    [InlineData("source_alias_in_evidence")]
    [InlineData("non_canonical_alias")]
    [InlineData("uuid_instead_of_alias")]
    [InlineData("alias_outside_snapshot")]
    [InlineData("duplicate_local_key")]
    [InlineData("extra_property")]
    [InlineData("truncated_json")]
    [InlineData("declared_without_pm_source")]
    [InlineData("conflict_with_one_alternative")]
    [InlineData("v2_wrapper_instead_of_dto")]
    public async Task An_invalid_alias_output_fails_the_run_permanently_with_no_revision_no_reusable_output_and_no_second_call(string kind)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        string Requirements(AiAnalysisOutput d, params AiItem[] items) => AnalysisAliasContracts.Encode(d with { Items = d.Items with { Requirements = items } });
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r =>
            {
                var dto = AliasModelReplies.Dto(r.AllText);
                return kind switch
                {
                    "unknown_alias" => Requirements(dto, AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E9")),
                    "alias_outside_snapshot" => Requirements(dto, AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E1", "E2")),
                    "source_alias_in_evidence" => Requirements(dto, AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "S1")),
                    "non_canonical_alias" => AnalysisAliasContracts.Encode(dto).Replace("\"E1\"", "\"e1\"", StringComparison.Ordinal),
                    "uuid_instead_of_alias" => AnalysisAliasContracts.Encode(dto).Replace("\"E1\"", "\"a0000000-0000-4000-8000-000000000001\"", StringComparison.Ordinal),
                    "duplicate_local_key" => Requirements(dto, AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E1"), AliasFixtures.Item("i1", "altro", DocumentAnalysisClassification.Documented, "E1")),
                    "extra_property" => Mutate(AnalysisAliasContracts.Encode(dto), n => n["sources"] = new JsonArray()),
                    "truncated_json" => AnalysisAliasContracts.Encode(dto)[..40],
                    "declared_without_pm_source" => Requirements(dto, AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Declared, "E1")),
                    "conflict_with_one_alternative" => AnalysisAliasContracts.Encode(dto with
                    {
                        Items = dto.Items with { Requirements = [] },
                        Conflicts = [new AiConflict { Key = "c1", Description = "x", Alternatives = [AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E1")] }],
                    }),
                    _ => """{"results":[],"analysis":{},"evidenceMap":[]}""",
                };
            },
        };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Equal(kind == "duplicate_local_key" ? 2 : 1, model.Requests.Count);    // T130: le chiavi duplicate hanno l'unico retry guidato (AI §10), il secondo output identico fallisce
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"Status\" = 'Committed'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"CurrentAnalysisId\" IS NOT NULL", ("p", project)));
    }

    // ================================================================ copertura incompleta

    [Fact]
    public async Task An_evidence_cited_by_no_item_is_coverage_incomplete_with_a_reusable_output_and_no_revision()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        var run = await StartRunAsync(client, project);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => AliasModelReplies.Reply(r.AllText, d => d with { Items = d.Items with { Requirements = [d.Items.Requirements[0]] } }),
        };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Equal(2, model.Requests.Count);      // T130: unico retry guidato (AI §10); il secondo output, ancora incompleto, resta riutilizzabile e non genera revisione
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\""));
    }

    [Fact]
    public async Task A_missing_visit_is_coverage_incomplete_and_the_model_declaration_of_completeness_is_never_filled_in_by_the_server()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        var run = await StartRunAsync(client, project);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => AliasModelReplies.Reply(r.AllText, d => d with { Visited = new() { A = ["E1"], B = ["E1", "E2"] } }),
        };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_known_relation_without_a_verdict_is_coverage_incomplete_and_with_a_verdict_the_run_completes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", Statement1, Statement2);
        await AddKnownRelationAsync(doc, EvidenceRelationType.Contradicts);
        var run = await StartRunAsync(client, project);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r =>
            {
                Assert.Contains("[R1] E1 Contradicts E2", r.AllText);
                return AliasModelReplies.Reply(r.AllText, d => d with { KnownRelationChecks = [] });
            },
        };

        var blocked = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, blocked.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, blocked.ErrorCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));

        var project2 = await NewProject(client);
        var doc2 = await NewDocument(client, project2, "regole2", Statement1, Statement2);
        await AddKnownRelationAsync(doc2, EvidenceRelationType.Contradicts);
        var run2 = await StartRunAsync(client, project2);
        var ok = await RunAsync(run2, new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) });

        Assert.True(ok.Status == ProcessingRunStatus.Completed, "Failed: " + ok.ErrorCode + " " + await ErrorMessage(run2));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run2)));
    }

    // ================================================================ replay / recovery con ricomposizione

    [Fact]
    public async Task A_reusable_output_is_recomposed_on_recovery_without_a_new_inference_and_stays_coverage_incomplete()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        var run = await StartRunAsync(client, project);
        var first = await RunAsync(run, IncompleteModel());
        Assert.Equal(DocumentWorkException.CoverageIncomplete, first.ErrorCode);

        await ReopenAsync(run);
        var silent = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };
        var again = await RunAsync(run, silent);

        Assert.Equal(ProcessingRunStatus.Failed, again.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, again.ErrorCode);
        Assert.Empty(silent.Requests);                                                    // output Committed riusato: nessuna reinferenza
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Theory]
    [InlineData("canonicalHash", DocumentWorkException.IntegrityConflict)]
    [InlineData("catalogHash", DocumentWorkException.CheckpointIncompatible)]
    [InlineData("aiOutputHash", DocumentWorkException.WorkOutputInvalid)]
    public async Task A_committed_output_whose_recomposition_diverges_is_a_permanent_error_without_inference_or_revision(string tampered, string expectedCode)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        var run = await StartRunAsync(client, project);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, (await RunAsync(run, IncompleteModel())).ErrorCode);
        await TamperPayloadAsync(run, payload => payload[tampered] = new string('f', 64));

        await ReopenAsync(run);
        var silent = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };
        var result = await RunAsync(run, silent);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Empty(silent.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"CurrentAnalysisId\" IS NOT NULL", ("p", project)));
    }

    [Fact]
    public async Task A_catalog_payload_altered_in_provenance_is_checkpoint_incompatible_and_never_reinferred()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        var run = await StartRunAsync(client, project);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, (await RunAsync(run, IncompleteModel())).ErrorCode);
        await TamperPayloadAsync(run, payload => payload["catalog"]!["sources"]![0]!["quote"] = "citazione alterata");

        await ReopenAsync(run);
        var silent = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };
        var result = await RunAsync(run, silent);

        Assert.Equal(DocumentWorkException.CheckpointIncompatible, result.ErrorCode);
        Assert.Empty(silent.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_source_shared_by_two_evidences_with_different_positions_fails_source_snapshot_invalid_before_any_call()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        var run = await StartRunAsync(client, project);
        // Incoerenza che solo il catalogo vede: lo stesso SourceId con quote e range diversi in due evidenze (ogni evidenza da sola e valida).
        await Execute(
            """
            ALTER TABLE "Evidences" DISABLE TRIGGER USER;
            UPDATE "Evidences" SET "Content" = jsonb_set("Content", '{sources,0,sourceId}', (SELECT "Content"->'sources'->0->'sourceId' FROM "Evidences" WHERE "Key" = 'e1'))
            WHERE "Key" = 'e2';
            ALTER TABLE "Evidences" ENABLE TRIGGER USER;
            """);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.SourceSnapshotInvalid, result.ErrorCode);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_persisted_source_with_an_invalid_range_or_part_is_rejected_by_the_shared_ledger_decoder_as_integrity_conflict_before_any_call()
    {
        // Assunzione di §7.9 verificata: il rifiuto di range/quote/part non nasce dal catalogo ma da DocumentAnalysisSources.LoadAsync (comune a v2 e v3).
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        await Execute(
            "ALTER TABLE \"Evidences\" DISABLE TRIGGER USER; UPDATE \"Evidences\" SET \"Content\" = jsonb_set(\"Content\", '{sources,0,part}', '-1'); ALTER TABLE \"Evidences\" ENABLE TRIGGER USER;");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.IntegrityConflict, result.ErrorCode);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    // ================================================================ compatibilita

    [Fact]
    public async Task A_run_already_frozen_with_v2_still_completes_with_the_v2_prompt_and_decoder_and_never_uses_the_alias_contract()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        await PinToV2Async(run);
        var input = await PrepareAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => ReconciliationCodec.Encode(V2Output(input)) };

        var result = await RunAsync(run, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(run));
        var request = Assert.Single(model.Requests);
        Assert.Equal(DocumentAnalysisWorkflow.Instructions.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));
        Assert.Contains($"fonte:{input.Evidence[0].Sources[0].SourceId}", request.AllText);
        Assert.Contains(input.BatchKey, request.AllText);
        Assert.Equal("document-analysis-1", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("project-analysis-documents-v2", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_formats_never_cross_a_v2_run_rejects_the_alias_dto_and_a_v3_run_rejects_the_v2_wrapper()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var v2 = await StartRunAsync(client, project);
        await PinToV2Async(v2);
        var v2Model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => "{\"contract\":\"alias-1\"}" };

        var v2Result = await RunAsync(v2, v2Model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (v2Result.Status, v2Result.ErrorCode));

        var project2 = await NewProject(client);
        await NewDocument(client, project2, "regole2", Statement1);
        var v3 = await StartRunAsync(client, project2);
        var input = await PrepareAsync(v3);
        var v3Model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => ReconciliationCodec.Encode(V2Output(input)) };

        var v3Result = await RunAsync(v3, v3Model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (v3Result.Status, v3Result.ErrorCode));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Theory]
    [InlineData("project-analysis-documents-v9", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1"}""")]     // definizione ignota
    [InlineData("project-analysis-documents-v3", """{"analysis":"document-analysis-v2","analysisSchema":"1.1.0"}""")]                                                      // v3 con prompt v2: nessuna reinterpretazione
    [InlineData("project-analysis-documents-v2", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1"}""")]     // v2 con prompt v3
    [InlineData("project-analysis-documents-v3", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-9"}""")]     // schema AI ignoto
    [InlineData("project-analysis-documents-v3", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","aliasCatalog":"alias-catalog-9"}""")]
    [InlineData("project-analysis-documents-v3", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","canonIds":"canon-ids-9"}""")]
    [InlineData("project-analysis-v3", """{"analysis":"document-analysis-v3"}""")]                                                                                          // 001 con prompt v3
    public async Task An_unknown_or_incompatible_definition_is_workflow_incompatible_and_never_downgrades_or_reaches_the_model(string definition, string prompts)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r", ("d", definition), ("p", prompts), ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, "workflow_incompatible"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkManifests\""));
    }

    // ================================================================ v5: valutazione dei Gap (T136)

    private const string V5Prompts = """{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3"}""";

    /// <summary>La v5 NON è instradata per le nuove richieste (T141): il test la ottiene riscrivendo la definizione congelata, come per v2.</summary>
    private async Task PinToV5Async(Guid run) => await Execute(
        "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-documents-v5', \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r", ("p", V5Prompts), ("r", run));

    // N4 semplificato: E1 messaggio PM «45», E2 la domanda sulla scheda (Gap), E3 la scheda «30» (documentata).
    private const string N4Pm = "Servono 45 pezzi al minuto.";
    private const string N4Question = "Cosa dice la scheda tecnica sul numero di pezzi al minuto?";
    private const string N4Sheet = "La scheda tecnica indica 30 pezzi al minuto.";

    private async Task<Guid> N4RunAsync(HttpClient client)
    {
        var project = await NewProject(client);
        await SeedPmEvidenceAsync(client, project, N4Pm);
        await NewDocumentOfKinds(client, project, "scheda", [(N4Question, EvidenceKind.Gap), (N4Sheet, EvidenceKind.Requirement)]);
        var run = await StartRunAsync(client, project);
        await PinToV5Async(run);
        return run;
    }

    /// <summary>Il DTO corretto dell'N4: E2 AnsweredInSnapshot/Answered con riscontro E3; conflitto Open E1 (Declared) contro E3 (Documented); nessuna domanda sulla scheda.</summary>
    private static AiAnalysisOutputV2 N4Dto(IReadOnlyList<(string Alias, string Origin, string Kind, string Text)> entries)
    {
        string Alias(string text) => entries.Single(x => x.Text == text).Alias;
        var (e1, e2, e3) = (Alias(N4Pm), Alias(N4Question), Alias(N4Sheet));
        var all = entries.Select(x => x.Alias).ToList();
        return new AiAnalysisOutputV2
        {
            Contract = "alias-2",
            Visited = new() { A = all, B = all },
            NoFindings = new() { A = null, B = "Nessuna relazione in vista B.", Cross = null },
            Items = new()
            {
                Goals = [], ScopeOverview = [], ScopeIncluded = [], ScopeExcluded = [], Requirements = [], Flows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [],
            },
            Conflicts =
            [
                new()
                {
                    Key = "c1", Description = "Pezzi al minuto discordanti.", Alternatives =
                    [
                        AliasFixtures.Item("i1", N4Pm, DocumentAnalysisClassification.Declared, e1), AliasFixtures.Item("i2", N4Sheet, DocumentAnalysisClassification.Documented, e3),
                    ],
                },
            ],
            Findings = [new() { Key = "f1", View = ReconcileView.A, Left = e1, Right = e3, Type = EvidenceRelationType.Contradicts, Basis = EvidenceRelationBasis.Inferred, Rationale = "45 contro 30.", Outcome = AiFindingOutcome.Open, Reason = "Serve la scelta del PM." }],
            KnownRelationChecks = [], Patches = [],
            GapAssessments = [new() { Gap = e2, Outcome = GapOutcome.AnsweredInSnapshot, Reason = GapReason.Answered, Rationale = "La scheda riporta la cadenza.", SupportingEvidence = [e3], OpenItem = null, OpenConflict = null }],
        };
    }

    private static DocumentChatWorkflowTests.FakeChatModel N4Model(Func<AiAnalysisOutputV2, AiAnalysisOutputV2>? change = null) => new()
    {
        Responder = r => AnalysisAliasContracts.Encode(change is null ? N4Dto(AliasModelRepliesV5.Entries(r.AllText)) : change(N4Dto(AliasModelRepliesV5.Entries(r.AllText)))),
    };

    private static JsonObject Payload(string json) => JsonNode.Parse(json)!.AsObject();

    private async Task<JsonObject> WorkPayloadAsync(Guid run) => Payload(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));

    [Fact]
    public async Task A_v5_run_completes_the_corrected_N4_with_one_call_the_v4_prompt_alias_2_and_the_assessment_in_the_work_output()
    {
        using var client = await App.LoginAsync();
        var run = await N4RunAsync(client);
        var input = await PrepareAsync(run);
        var model = N4Model();

        var result = await RunAsync(run, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(run));
        var request = Assert.Single(model.Requests);
        Assert.Equal(DocumentAnalysisWorkflow.InstructionsV4.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));
        Assert.Contains("`alias-2`", request.AllText);
        Assert.DoesNotContain("`alias-1`", request.AllText);
        Assert.Matches(@"<<<VOCE \[E2\] origine=Document tipo=Gap polarita=\w+>>>", request.AllText);          // il tipo Gap è visibile al modello
        Assert.DoesNotMatch(AnyUuid, request.System + request.AllText);
        Assert.DoesNotMatch(AnyHash, request.System + request.AllText);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal("document-analysis-alias-2", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("document-analysis-validation-v3", await Scalar<string>("SELECT \"ValidationVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("project-analysis-documents-v5", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("analysis-single-v3", await Scalar<string>("SELECT \"AlgorithmVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));

        var payload = await WorkPayloadAsync(run);
        Assert.Equal("document-analysis-alias-2", payload["schemaVersion"]!.GetValue<string>());
        Assert.Equal("Complete", payload["outcome"]!.GetValue<string>());
        Assert.Equal("gap-assessment-1", payload["gapAssessmentsVersion"]!.GetValue<string>());
        var record = Assert.Single(payload["gapAssessments"]!.AsArray())!.AsObject();
        Assert.Equal(("E2", "AnsweredInSnapshot", "Answered"), (record["gap"]!.GetValue<string>(), record["outcome"]!.GetValue<string>(), record["reason"]!.GetValue<string>()));
        Assert.Equal(["E3"], record["supportingEvidence"]!.AsArray().Select(n => n!.GetValue<string>()));
        var dto = AnalysisAliasContracts.DecodeV2(payload["ai"]!.ToJsonString());
        Assert.Equal(GapAssessmentRules.Hash(dto.GapAssessments), payload["gapAssessmentsHash"]!.GetValue<string>());
        Assert.Equal(AnalysisAliasContracts.Hash(dto), payload["aiOutputHash"]!.GetValue<string>());
        Assert.Equal(AnalysisAliasCatalog.Build(input, AnalysisAliasContracts.SchemaVersionV2).CatalogHash, payload["catalogHash"]!.GetValue<string>());

        // La revisione è l'AnalysisContent 1.1.0 composto: conflitto aperto E1/E3, nessuna domanda sulla scheda, Gap non mappato e nessun supporto automatico.
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(AnalysisConflictStatus.Open, Assert.Single(content.Conflicts).Status);
        Assert.Empty(content.OpenQuestions);
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));      // E1 ed E3, mai il Gap E2
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"SchemaVersion\" = '1.1.0'", ("r", run)));
    }

    [Fact]
    public async Task A_v5_run_with_a_gap_that_has_no_record_is_coverage_incomplete_with_a_reusable_output_gap_not_assessed_and_no_revision()
    {
        using var client = await App.LoginAsync();
        var run = await N4RunAsync(client);
        var model = N4Model(dto => dto with { GapAssessments = [] });

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.CoverageIncomplete), (result.Status, result.ErrorCode));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        var payload = await WorkPayloadAsync(run);
        Assert.Equal("ValidIncomplete", payload["outcome"]!.GetValue<string>());
        Assert.Contains(payload["gaps"]!.AsArray().Select(g => g!.GetValue<string>()), g => g.StartsWith("gap_not_assessed:", StringComparison.Ordinal));
        Assert.Empty(payload["gapAssessments"]!.AsArray());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(2, model.Requests.Count);                                                                                 // T137: il solo Gap senza record ha il retry informato; il modello finto ripete l'output incompleto e vale il motivo originale
    }

    [Theory]
    [InlineData("unknown-alias")]
    [InlineData("non-gap-record")]
    [InlineData("open-item-missing")]
    public async Task A_v5_run_with_an_invalid_assessment_fails_permanently_with_no_work_output_no_revision_and_no_second_call(string kind)
    {
        using var client = await App.LoginAsync();
        var run = await N4RunAsync(client);
        var model = N4Model(dto =>
        {
            var record = dto.GapAssessments[0];
            var bad = kind switch
            {
                "unknown-alias" => record with { SupportingEvidence = ["E9"] },
                "non-gap-record" => record with { Gap = record.SupportingEvidence[0] },
                _ => record with { Outcome = GapOutcome.Open, Reason = GapReason.NoAnswer, SupportingEvidence = [], OpenItem = "i7" },
            };
            return dto with { GapAssessments = [bad] };
        });

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_v5_run_whose_model_returns_the_alias_1_contract_fails_work_output_invalid_and_the_formats_never_cross()
    {
        using var client = await App.LoginAsync();
        var run = await N4RunAsync(client);
        var legacy = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };

        var result = await RunAsync(run, legacy);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_v4_run_keeps_the_v3_prompt_and_alias_1_and_rejects_an_alias_2_reply()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);                                              // v4 congelata: single-call alias-1
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelRepliesV5.Reply(r.AllText) };

        var result = await RunAsync(run, model);

        var request = Assert.Single(model.Requests);
        Assert.Equal(DocumentAnalysisWorkflow.InstructionsV3.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));
        Assert.Contains("`alias-1`", request.AllText);
        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
    }

    [Fact]
    public async Task A_new_analyze_request_still_freezes_the_v4_definition_and_never_the_v5_one()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);

        var run = await StartRunAsync(client, project);

        Assert.Equal("project-analysis-documents-v4", await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        var prompts = JsonDocument.Parse(await Scalar<string>("SELECT \"PromptVersions\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))).RootElement;
        Assert.Equal("document-analysis-v3", prompts.GetProperty("analysis").GetString());
        Assert.Equal("document-analysis-alias-1", prompts.GetProperty("analysisAiSchema").GetString());
        Assert.Equal("analysis-single-v2", prompts.GetProperty("algorithm").GetString());
    }

    [Theory]
    [InlineData("tampered-gap-record")]
    [InlineData("tampered-gap-hash")]
    [InlineData("missing-gap-hash")]
    [InlineData("tampered-canonical")]
    [InlineData("v3-payload-in-v5-run")]
    public async Task A_committed_v5_output_whose_assessments_diverge_is_a_permanent_error_without_inference_or_revision(string tamper)
    {
        using var client = await App.LoginAsync();
        var run = await N4RunAsync(client);
        // Valido ma incompleto (nessun item per le evidenze non-Gap): WorkOutput Committed riutilizzabile.
        var incomplete = N4Model(dto => dto with { Findings = [], Conflicts = [], NoFindings = new() { A = "Nessuna.", B = "Nessuna.", Cross = "Nessun elemento." } });
        Assert.Equal(DocumentWorkException.CoverageIncomplete, (await RunAsync(run, incomplete)).ErrorCode);
        var expected = tamper switch
        {
            "tampered-gap-record" => DocumentWorkException.IntegrityConflict,
            "tampered-gap-hash" => DocumentWorkException.WorkOutputInvalid,
            "missing-gap-hash" => DocumentWorkException.WorkOutputInvalid,
            "tampered-canonical" => DocumentWorkException.IntegrityConflict,
            _ => DocumentWorkException.CheckpointIncompatible,
        };
        await TamperPayloadAsync(run, payload =>
        {
            switch (tamper)
            {
                case "tampered-gap-record":
                    payload["ai"]!["gapAssessments"]![0]!["rationale"] = "Riscritta dopo il commit.";
                    break;
                case "tampered-gap-hash":
                    payload["gapAssessmentsHash"] = new string('f', 64);
                    break;
                case "missing-gap-hash":
                    payload.Remove("gapAssessmentsHash");
                    break;
                case "tampered-canonical":
                    payload["canonicalHash"] = new string('f', 64);
                    break;
                default:
                    payload["schemaVersion"] = "document-analysis-alias-1";
                    break;
            }
        });

        await ReopenAsync(run);
        var silent = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };
        var result = await RunAsync(run, silent);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(expected, result.ErrorCode);
        Assert.Empty(silent.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_committed_incomplete_v5_output_is_recomposed_on_recovery_without_a_new_inference_and_stays_coverage_incomplete()
    {
        using var client = await App.LoginAsync();
        var run = await N4RunAsync(client);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, (await RunAsync(run, N4Model(dto => dto with { GapAssessments = [] }))).ErrorCode);

        await ReopenAsync(run);
        var silent = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };
        var again = await RunAsync(run, silent);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.CoverageIncomplete), (again.Status, again.ErrorCode));
        Assert.Empty(silent.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_v5_run_with_an_open_no_answer_gap_completes_the_open_assessment_is_not_a_technical_incompleteness()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocumentOfKinds(client, project, "rilievi", [("Chi approva la modifica?", EvidenceKind.Gap), ("Il turno dura 8 ore.", EvidenceKind.Requirement)]);
        var run = await StartRunAsync(client, project);
        await PinToV5Async(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelRepliesV5.Reply(r.AllText) };

        var result = await RunAsync(run, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(run));
        var payload = await WorkPayloadAsync(run);
        var record = Assert.Single(payload["gapAssessments"]!.AsArray())!.AsObject();
        Assert.Equal(("Open", "NoAnswer"), (record["outcome"]!.GetValue<string>(), record["reason"]!.GetValue<string>()));
        Assert.Empty(payload["gaps"]!.AsArray());
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal("Chi approva la modifica?", Assert.Single(content.OpenQuestions).Text);
    }

    [Theory]
    [InlineData("project-analysis-documents-v5", """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v2","validation":"document-analysis-validation-v2"}""")]     // v5 con versioni v3
    [InlineData("project-analysis-documents-v5", """{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v2","validation":"document-analysis-validation-v3"}""")]     // algoritmo v2 con schema v5
    [InlineData("project-analysis-documents-v5", """{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v2"}""")]     // validazione v2
    [InlineData("project-analysis-documents-v3", """{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3"}""")]     // v3 con versioni v5
    [InlineData("project-analysis-documents-v4", """{"analysis":"document-analysis-v4","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-2","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v3","validation":"document-analysis-validation-v3"}""")]     // v4 con versioni v5
    public async Task The_v5_versions_never_mix_with_the_v3_and_v4_definitions_and_a_mismatch_is_workflow_incompatible(string definition, string prompts)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1);
        var run = await StartRunAsync(client, project);
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r", ("d", definition), ("p", prompts), ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, "workflow_incompatible"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkManifests\""));
    }

    // ================================================================ supporto: output e dati

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    private static DocumentChatWorkflowTests.FakeChatModel IncompleteModel() => new()
    {
        Responder = r => AliasModelReplies.Reply(r.AllText, d => d with { Items = d.Items with { Requirements = [d.Items.Requirements[0]] } }),
    };

    private async Task<string> ErrorMessage(Guid run) => await Scalar<string>("SELECT coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

    /// <summary>Output v2 (storico): serve a provare che i due formati non si incrociano e che un run v2 congelato completa con il proprio decoder.</summary>
    private static SingleCallOutput V2Output(DocumentAnalysisInput input)
    {
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
        ReconcileResult Result(ReconcileView view) => new()
        {
            BatchId = input.BatchKey, InputHash = input.InputHash, View = view, Level = 0,
            VisitedEntryIds = [.. input.Snapshot.Evidence.Select(e => ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)).Order(StringComparer.Ordinal)],
            VisitedNodeIds = [], Candidates = [], Dispositions = [], ItemProposals = [], Patches = [], Descriptor = null, NoFindingsReason = "nessun collegamento oltre quelli già noti",
        };
        return new()
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
    }

    private async Task PinToV2Async(Guid run) => await Execute(
        "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-documents-v2', \"PromptVersions\" = '{\"analysis\":\"document-analysis-v2\",\"analysisSchema\":\"1.1.0\"}'::jsonb WHERE \"Id\" = @r",
        ("r", run));

    /// <summary>Riapre un run terminale (simula la ripresa di un nuovo processo con lo stesso database): gli output Committed restano.</summary>
    private async Task ReopenAsync(Guid run) => await Execute(
        "UPDATE \"ProcessingRuns\" SET \"Status\" = 'Queued', \"ErrorCode\" = NULL, \"ErrorMessage\" = NULL, \"CompletedAt\" = NULL, \"LeaseOwner\" = NULL, \"LeaseUntil\" = NULL WHERE \"Id\" = @r",
        ("r", run));

    /// <summary>Altera il payload di un WorkOutput mantenendone coerente l'OutputHash, così il lookup lo accetta e solo la ricomposizione può accorgersene.</summary>
    private async Task TamperPayloadAsync(Guid run, Action<JsonObject> change)
    {
        var payload = JsonNode.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)))!.AsObject();
        change(payload);
        var text = payload.ToJsonString();
        await Execute(
            "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; UPDATE \"WorkOutputs\" SET \"PayloadJson\" = @p::jsonb, \"OutputHash\" = @h WHERE \"OwnerId\" = @r; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;",
            ("p", text), ("h", CanonicalJson.Sha256Hex(text)), ("r", run));
    }

    private sealed record Doc(Guid Document, Guid Version, Guid EvidenceVersion, List<Guid> EvidenceIds, List<Guid> SourceIds);

    /// <summary>Documento pronto con una evidenza (e una fonte documentale) per enunciato, nella stessa versione di evidenza.</summary>
    private async Task<Doc> NewDocument(HttpClient client, Guid project, string name, params string[] statements) =>
        await NewDocumentOfKinds(client, project, name, [.. statements.Select(s => (s, EvidenceKind.Requirement))]);

    /// <summary>Come <see cref="NewDocument"/> ma con il tipo di evidenza per enunciato (T136: i Gap sono evidenze di tipo Gap).</summary>
    private async Task<Doc> NewDocumentOfKinds(HttpClient client, Guid project, string name, (string Statement, EvidenceKind Kind)[] items)
    {
        var statements = items.Select(i => i.Statement).ToArray();
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var (document, version) = (created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == version, Ct);
        var now = DateTimeOffset.UtcNow;
        var (job, evidenceVersion) = (Guid.NewGuid(), Guid.NewGuid());
        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = evidenceVersion, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = statements.Length, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var (evidenceIds, sourceIds) = (new List<Guid>(), new List<Guid>());
        for (var i = 0; i < statements.Length; i++)
        {
            var (evidenceId, sourceId, statement) = (Guid.NewGuid(), Guid.NewGuid(), statements[i]);
            evidenceIds.Add(evidenceId);
            sourceIds.Add(sourceId);
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
                        SourceId = sourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                        MessageId = null, Sequence = null, DocumentId = document, DocumentVersionId = version, PreparationId = preparation.Id,
                        ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = statement.Length, Quote = statement,
                    },
                ],
            };
            db.Evidences.Add(new EvidenceItem
            {
                Id = evidenceId, VersionId = evidenceVersion, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = i + 1,
                Key = $"e{i + 1}", Kind = items[i].Kind, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement,
                Content = DocumentEvidenceCodec.EncodeContent(content),
            });
        }

        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = version, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = evidenceVersion, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = evidenceVersion;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, version), Ct);
        return new Doc(document, version, evidenceVersion, evidenceIds, sourceIds);
    }

    /// <summary>Relazione nota E1 → E2 (le due evidenze della stessa versione), come prodotta dall'estrazione.</summary>
    private async Task AddKnownRelationAsync(Doc doc, EvidenceRelationType type)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = await db.ProjectDocuments.Where(d => d.Id == doc.Document).Select(d => d.ProjectId).SingleAsync(Ct);
        db.EvidenceRelations.Add(new EvidenceRelation
        {
            Id = Guid.NewGuid(), ProjectId = project, VersionId = doc.EvidenceVersion, FromEvidenceId = doc.EvidenceIds[0], ToEvidenceId = doc.EvidenceIds[1],
            Type = type, Basis = EvidenceRelationBasis.Declared, Rationale = null,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SeedPmEvidenceAsync(HttpClient client, Guid project, string text)
    {
        var post = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
    }

    private async Task<Guid> StartRunAsync(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Presa in carico come il dispatcher: Running con owner, fence e lease validi (idempotente).</summary>
    private async Task<AnalysisRunClaim> ClaimAsync(Guid run)
    {
        await Execute(
            """
            UPDATE "ProcessingRuns" SET "Status" = 'Running', "LeaseOwner" = 'test-owner', "FencingToken" = 1, "AttemptCount" = 1, "StartedAt" = now(),
                   "LeaseUntil" = now() + interval '10 minutes'
            WHERE "Id" = @r AND "Status" = 'Queued'
            """,
            ("r", run));
        return new AnalysisRunClaim(run, "test-owner", 1, 1);
    }

    /// <summary>Congela la selezione (idempotente) e carica lo snapshot da cui il test ricostruisce catalogo e id attesi.</summary>
    private async Task<DocumentAnalysisInput> PrepareAsync(Guid run)
    {
        var claim = await ClaimAsync(run);
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var frozen = await new EvidenceLedgerStore(db).FreezeDocumentSelectionAsync(claim, Ct);
        Assert.True(frozen.Outcome is SelectionOutcome.Frozen or SelectionOutcome.Existing, frozen.Outcome.ToString());
        return await DocumentAnalysisSources.LoadAsync(db, run, Ct);
    }

    private async Task<AnalysisRunResult> RunAsync(Guid run, DocumentChatWorkflowTests.FakeChatModel model, DocumentOptions? documents = null) =>
        await RunClaimAsync(await ClaimAsync(run), model, documents);

    private async Task<AnalysisRunResult> RunClaimAsync(AnalysisRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model, DocumentOptions? documents = null)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentAnalysisWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(documents ?? new DocumentOptions()));
        return await workflow.ExecuteAsync(claim, Ct);
    }
}
