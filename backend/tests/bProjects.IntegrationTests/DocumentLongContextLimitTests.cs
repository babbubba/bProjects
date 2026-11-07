using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using G = bProjects.IntegrationTests.DocumentGlobalReconciliationTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T089 (DET, PG + modello FINTO) — limiti del lavoro oltre contesto su chat, analisi e generazione: quando contesto, uscita, chiamate, livelli, byte o stallo si
/// esauriscono, ogni residuo ha un ID o un motivo esplicito e impedisce Complete (nessuna revisione, risposta o bozza parziale). I limiti sono quelli CONGELATI dalla
/// policy B1 (nessun limite discrezionale nuovo) e il gold è quello di T079: una perdita semantica del gold è FAIL anche con mappa al 100% e run Completed. Nessun test
/// finto equivale al nominale reale: la prova con DGX resta T090. Analisi e chat usano il database reale; la generazione usa il planner editoriale deterministico.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentLongContextLimitTests : ReconcileRig
{
    // ================================================================ analisi: limiti B1 congelati sul corpus gold T079

    [Fact]
    public async Task The_frozen_candidate_cap_leaves_a_named_residual_and_no_revision_even_though_the_gold_conflicts_were_found()
    {
        await SetLimitsAsync(l => l with { MaxCandidatesPerRun = 1 });
        var corpus = G.Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new G.Gate(corpus);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };

        var result = await RunAsync(claim, model);

        await AssertBlockedAsync(run, result, DocumentWorkException.CoverageIncomplete, "oltre 1 candidati (candidato ");
    }

    [Fact]
    public async Task The_frozen_derived_bytes_cap_leaves_a_named_residual_and_no_revision()
    {
        await SetLimitsAsync(l => l with { MaxDerivedBytes = 1 });
        var corpus = G.Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new G.Gate(corpus);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };

        var result = await RunAsync(claim, model);

        await AssertBlockedAsync(run, result, DocumentWorkException.CoverageIncomplete, "byte derivati esauriti davanti al candidato ");
    }

    [Fact]
    public async Task The_root_call_cap_stops_the_run_after_exactly_that_many_calls_with_no_revision()
    {
        var corpus = G.Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new G.Gate(corpus);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };

        var result = await RunAsync(claim, model, new DocumentOptions { MaxAiCallsPerRun = 3 });

        await AssertBlockedAsync(run, result, DocumentWorkException.CallLimitExceeded, "limite di chiamate");
        Assert.Equal(3, model.Requests.Count);   // mai una chiamata oltre il tetto del root
        Assert.Equal(3, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_frozen_level_cap_leaves_a_residual_naming_the_view_and_the_remaining_nodes_and_no_revision()
    {
        await SetLimitsAsync(l => l with { MaxReconciliationLevels = 1 });
        var corpus = G.Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new G.Gate(corpus);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };

        var result = await RunAsync(claim, model);

        await AssertBlockedAsync(run, result, DocumentWorkException.CoverageIncomplete, "livelli congelati");
        Assert.Matches(@"Vista \w+: servono più dei 1 livelli congelati \(\d+ nodi al livello \d+\)", await Message(run));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(4, 6)]
    public void A_reduction_level_that_does_not_shrink_the_nodes_is_an_explicit_stall_and_never_forced_forward(int before, int after)
    {
        var failure = Assert.Throws<DocumentWorkException>(() => ReconciliationHierarchy.EnsureStrictlyDecreasing(ReconcileView.A, 2, before, after));

        Assert.Equal(DocumentWorkException.CoverageIncomplete, failure.Code);
        Assert.Contains($"{before} → {after}", failure.Message);
        Assert.Contains("stallo della riduzione", failure.Message);
    }

    [Fact]
    public void A_strictly_shrinking_reduction_level_is_not_a_stall()
    {
        ReconciliationHierarchy.EnsureStrictlyDecreasing(ReconcileView.A, 2, 6, 4);
    }

    [Fact]
    public async Task A_gold_loss_is_a_semantic_FAIL_even_when_the_map_is_complete_and_the_run_is_completed_while_the_intact_gold_passes()
    {
        var corpus = G.Corpus();
        var lost = await RunGoldAsync(corpus, dropPayment: true);
        var intact = await RunGoldAsync(corpus, dropPayment: false);

        // Strutturalmente identici: run Completed e mappa al 100% in entrambi...
        Assert.Equal(ProcessingRunStatus.Completed, lost.Status);
        Assert.Equal(ProcessingRunStatus.Completed, intact.Status);
        Assert.Equal(corpus.Count, lost.Mapped);
        Assert.Equal(corpus.Count, intact.Mapped);
        // ...ma il verdetto dipende dalla qualità: solo il gold intatto passa.
        Assert.Equal(["conflict-payment"], lost.MissingGold);
        Assert.Equal("FAIL", Verdict(lost));
        Assert.Empty(intact.MissingGold);
        Assert.Equal("PASS", Verdict(intact));
    }

    private sealed record GoldOutcome(ProcessingRunStatus Status, int Mapped, IReadOnlyList<string> MissingGold);

    private static string Verdict(GoldOutcome outcome) => outcome.Status == ProcessingRunStatus.Completed && outcome.MissingGold.Count == 0 ? "PASS" : "FAIL";

    private async Task<GoldOutcome> RunGoldAsync(List<G.Fact> corpus, bool dropPayment)
    {
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new G.Gate(corpus, dropPayment);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };
        var result = await RunAsync(claim, model);
        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var mapped = (int)await Count("SELECT count(DISTINCT \"EvidenceId\") FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run));
        return new GoldOutcome(result.Status, mapped, G.MissingGoldConflicts(content, G.Gold(G.AllGold)));
    }

    private async Task AssertBlockedAsync(Guid run, AnalysisRunResult result, string code, string reason)
    {
        var message = await Message(run);
        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == code, result.ErrorCode + ": " + message);
        Assert.Contains(reason, message);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));   // nessun Complete con residui
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
    }

    private async Task SetLimitsAsync(Func<DocumentLimits, DocumentLimits> change)
    {
        using var scope = App.Services.CreateScope();
        var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
        var limits = change(snapshot.Limits);
        Assert.Empty(limits.Validate());
        await Execute("UPDATE \"DocumentPolicies\" SET \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
    }

    private async Task<(Guid Run, AnalysisRunClaim Claim)> ArrangeAsync(List<G.Fact> corpus)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 0; n < corpus.Count; n++)
        {
            await NewDocument(client, project, $"doc{n}", G.Statement(corpus[n], n), corpus[n].Subject, corpus[n].Scope);
        }

        var run = await StartRunAsync(client, project);
        return (run, await ClaimAsync(run));
    }

    // ================================================================ chat: tetto di chiamate congelato

    [Fact]
    public async Task The_call_cap_stops_a_long_chat_before_the_next_scan_with_no_answer_and_every_scan_already_committed_is_kept()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var d = 0; d < 3; d++)
        {
            await NewDocument(client, project, $"chat{d}", string.Join(' ', Enumerable.Range(0, 900).Select(i => $"cx{d}w{i}")), subject: $"s{d}");
        }

        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text = "Cosa prevedono i documenti?" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var turn = (await Json(response)).GetProperty("chatTurnId").GetGuid();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => JsonSerializer.Serialize(new { outcome = "no_support", reason = "Nessuna evidenza di questo gruppo riguarda la domanda.", notes = Array.Empty<object>() }),
        };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunChatAsync(turn, model, maxCalls: 1));

        Assert.Equal(DocumentWorkException.CallLimitExceeded, ModelFailure.Classify(Root(failure)).Code);
        Assert.Single(model.Requests);   // esattamente il massimo, mai oltre
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));   // nessuna risposta completa con scansioni mancanti
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn)));
    }

    private static Exception Root(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException!)
        {
            if (current is DocumentWorkException || current.InnerException is null)
            {
                return current;
            }
        }

        return e;
    }

    private async Task<ChatTurnCommitResult> RunChatAsync(Guid turn, DocumentChatWorkflowTests.FakeChatModel model, int maxCalls)
    {
        await Execute(
            """
            UPDATE "ChatTurns" SET "Status" = 'Running', "LeaseOwner" = 'test-owner', "FencingToken" = "FencingToken" + 1, "AttemptCount" = "AttemptCount" + 1, "StartedAt" = now(),
                   "LeaseUntil" = now() + interval '10 minutes', "SessionRevisionAtStart" = (SELECT "ChatSessionRevision" FROM "Conversations" c WHERE c."Id" = "ConversationId")
            WHERE "Id" = @t
            """,
            ("t", turn));
        ChatTurnClaim claim;
        using (var scope = App.Services.CreateScope())
        {
            var t = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatTurns.AsNoTracking().SingleAsync(x => x.Id == turn, Ct);
            claim = new ChatTurnClaim(t.Id, t.ConversationId, t.TriggerMessageId, t.SourceCutoffSequence, "test-owner", t.FencingToken, t.AttemptCount, t.LeaseUntil!.Value, t.DeadlineAt);
        }

        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentChatWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions { MaxAiCallsPerRun = maxCalls }));
        return await workflow.ExecuteAsync(claim, null, Ct);
    }
}

/// <summary>
/// T089 (DET, senza database né LLM) — generazione: il planner editoriale non perde obbligazioni né fonti e, quando l'uscita o il contesto si esauriscono, lascia un item
/// irriducibile con il suo ID PRIMA di qualunque chiamata, senza aumentare la finestra né compattare la revisione confermata.
/// </summary>
public sealed class DocumentLongContextDraftLimitTests
{
    private const string Owner = "ProcessingRun|r";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Words(int count, string prefix) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private static int Count(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static (DocumentAnalysisContent Content, Dictionary<Guid, Guid> SourceOfItem) Revision(params int[] wordsPerRequirement)
    {
        var sources = new List<DocumentAnalysisSource>();
        var sourceOfItem = new Dictionary<Guid, Guid>();
        var items = new List<DocumentAnalysisItem>();
        for (var i = 0; i < wordsPerRequirement.Length; i++)
        {
            var quote = "citazione " + Words(8, $"q{i}x");
            var source = new DocumentAnalysisSource
            {
                Id = Guid.NewGuid(), Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = Guid.NewGuid(),
                DocumentVersionId = Guid.NewGuid(), PreparationId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = quote.Length, Quote = quote,
            };
            sources.Add(source);
            var item = new DocumentAnalysisItem
            {
                Id = Guid.NewGuid(), Text = Words(wordsPerRequirement[i], $"r{i}x"), Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [source.Id],
            };
            items.Add(item);
            sourceOfItem[item.Id] = source.Id;
        }

        var content = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), SourceCutoffSequence = 0,
            ValidationState = AnalysisContentState.PmConfirmed, Sources = sources, Goals = [],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
        };
        return (content, sourceOfItem);
    }

    /// <summary>40 parole fisse + 5 per item + testo degli item principali + citazioni delle fonti dell'unità.</summary>
    private static EditorialMeasurer Measurer() => (slice, _) =>
    {
        var owned = slice.Primary.Sum(i => Count(i.Text));
        var tokens = 40 + owned + slice.Context.Sum(i => 5 + Count(i.Text)) + slice.Primary.Count * 5 + slice.Sources.Sum(s => Count(s.Quote ?? ""));
        var key = string.Join('|', slice.Primary.Select(i => i.Id)) + "#" + string.Join('|', slice.Sources.Select(s => s.Id));
        return new ValueTask<UnitMeasure>(new UnitMeasure(tokens, TokenCountQuality.Measured, Sha(key), owned));
    };

    private static IReadOnlyList<string> Chain(int ordinal) => ordinal == 0 ? [] : [Sha($"output-{ordinal - 1}")];

    private static DocumentEditorialPlanner Planner(DocumentAnalysisContent content, PlannerLimits limits) =>
        new(content, Owner, "manifest-1", "def", limits, Measurer(), "schema-1", "prompt-1");

    [Fact]
    public async Task Every_required_item_is_primary_of_exactly_one_unit_with_its_sources_and_no_unit_exceeds_the_unchanged_window()
    {
        var (content, sourceOfItem) = Revision(80, 80, 80, 80, 80, 80, 80, 80);
        var limits = new PlannerLimits(400, 100_000);

        var units = await Planner(content, limits).PlanAsync(Chain, Ct);

        Assert.True(units.Count > 1);
        Assert.Equal(content.Requirements.Select(r => r.Id), units.SelectMany(u => u.PrimaryItemIds));   // ordine stabile, nessuno perso o duplicato
        Assert.All(units, u =>
        {
            Assert.All(u.PrimaryItemIds, id => Assert.Contains(sourceOfItem[id], u.SourceIds));   // nessuna fonte di un item principale omessa
            Assert.True(u.Measure.Tokens <= limits.InputBudgetTokens, $"unita' {u.Ordinal}: finestra aumentata");
        });
    }

    [Fact]
    public async Task A_single_item_that_alone_exhausts_the_output_estimate_is_irreducible_with_its_id_before_any_unit_and_is_never_truncated()
    {
        var (content, _) = Revision(20, 700, 20);
        var huge = content.Requirements[1].Id;

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => Planner(content, new PlannerLimits(100_000, 400, OutputExpansion: 1.0)).PlanAsync(Chain, Ct).AsTask());

        Assert.Equal(DocumentWorkException.DocumentIncomplete, failure.Code);
        Assert.Contains(huge.ToString("D"), failure.Message);
        Assert.DoesNotContain("r1x17", failure.Message);   // ID e motivo, mai testo
    }

    [Fact]
    public async Task A_single_item_that_alone_exhausts_the_context_is_irreducible_with_its_id_and_the_window_is_not_raised()
    {
        var (content, _) = Revision(20, 900, 20);
        var huge = content.Requirements[1].Id;

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => Planner(content, new PlannerLimits(500, 100_000)).PlanAsync(Chain, Ct).AsTask());

        Assert.Equal(DocumentWorkException.DocumentIncomplete, failure.Code);
        Assert.Contains(huge.ToString("D"), failure.Message);
    }
}
