using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// Piano del consolidamento (contracts/ai-workflow.md §10.7) con un contatore a parole e finestre ridotte: passo singolo quando sta nel
/// budget, altrimenti passi incrementali in ordine di sequenza che coprono ogni evidenza una sola volta, ogni messaggio almeno una volta,
/// entro il budget con lo spazio dell'analisi corrente; nessun troncamento, overflow strutturale esplicito.
/// </summary>
public sealed class AnalysisConsolidationPlanTests
{
    private const int Output = 200;
    private const int Margin = 50;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static readonly int InstructionWords = Words(ConversationalAgent.ConsolidationInstructions);

    [Fact]
    public async Task A_dialog_and_registry_within_the_budget_are_consolidated_in_one_step_with_the_full_view()
    {
        var run = Run(messages: 4, words: 40, evidencesPerMessage: 2, evidenceWords: 10);
        var plan = await AnalysisConsolidation.PlanAsync(run, Factory(InstructionWords + 20_000), Ct);

        var step = Assert.Single(plan.Steps);
        Assert.False(plan.Incremental);
        Assert.Equal(run.Evidence!.Aliases, step.Aliases);
        var messages = AnalysisConsolidation.Messages(run, plan, 1, prior: null);
        Assert.Equal(run.Dialog.Count + 1, messages.Count);
        Assert.Equal(AnalysisConsolidation.FinalMessage(run), messages[^1].Text);
        Assert.Contains(run.Evidence.ToPromptText(), messages[^1].Text);
    }

    [Fact]
    public async Task A_registry_beyond_the_budget_is_split_in_sequence_order_with_every_evidence_once_and_every_step_within_the_budget()
    {
        var run = Run(messages: 12, words: 120, evidencesPerMessage: 4, evidenceWords: 25);
        var window = InstructionWords + Output + Margin + 1_400;
        var plan = await AnalysisConsolidation.PlanAsync(run, Factory(window), Ct);

        Assert.True(plan.Steps.Count >= 3, $"atteso un piano incrementale, passi={plan.Steps.Count}");
        Assert.Equal(run.Evidence!.Aliases, plan.Steps.SelectMany(s => s.Aliases));     // tutte, una sola volta, in ordine
        Assert.Equal(run.Dialog.Select(m => m.Sequence), plan.Steps.SelectMany(s => s.Sequences).Distinct());
        Assert.All(plan.Steps.Zip(plan.Steps.Skip(1)), p => Assert.True(p.First.Sequences[^1] <= p.Second.Sequences[0]));
        AssertStepsFit(run, plan, window);
    }

    [Fact]
    public async Task The_evidences_of_one_message_that_exceed_a_step_are_spread_over_steps_repeating_the_whole_original()
    {
        var run = Run(messages: 3, words: 60, evidencesPerMessage: 30, evidenceWords: 20);
        var window = InstructionWords + Output + Margin + 1_000;
        var plan = await AnalysisConsolidation.PlanAsync(run, Factory(window), Ct);

        Assert.Equal(run.Evidence!.Aliases, plan.Steps.SelectMany(s => s.Aliases));
        Assert.Contains(run.Dialog, m => plan.Steps.Count(s => s.Sequences.Contains(m.Sequence)) > 1);
        foreach (var step in plan.Steps)
        {
            var messages = AnalysisConsolidation.Messages(run, plan, step.Number, Prior());
            foreach (var alias in step.Aliases)
            {
                var owner = run.Evidence.Evidences.Single(e => e.Alias == alias);
                Assert.Contains(messages, m => m.Text == AnalysisConsolidation.DialogMessage(run.Dialog.Single(d => d.Id == owner.MessageId)).Text);
            }
        }

        AssertStepsFit(run, plan, window);
    }

    [Fact]
    public async Task A_message_that_cannot_fit_even_with_a_single_evidence_is_an_explicit_context_overflow()
    {
        var run = Run(messages: 2, words: 2_000, evidencesPerMessage: 1, evidenceWords: 10);
        var window = InstructionWords + Output + Margin + 900;

        var overflow = await Assert.ThrowsAsync<ContextOverflowException>(() => AnalysisConsolidation.PlanAsync(run, Factory(window), Ct));
        Assert.True(overflow.Tokens > overflow.BudgetTokens);
    }

    [Fact]
    public async Task A_relation_is_shown_in_the_step_of_its_most_recent_evidence_once_both_ends_are_known()
    {
        var run = Run(messages: 12, words: 120, evidencesPerMessage: 4, evidenceWords: 25, relateLastToFirst: true);
        var plan = await AnalysisConsolidation.PlanAsync(run, Factory(InstructionWords + Output + Margin + 1_400), Ct);
        var first = run.Evidence!.Aliases[0];
        var last = run.Evidence.Aliases[^1];
        var line = $"- {last} Contradicts {first}";

        var step = Assert.Single(plan.Steps, s => AnalysisConsolidation.Messages(run, plan, s.Number, Prior())[^1].Text.Contains(line));
        Assert.Contains(last, step.Aliases);
        Assert.DoesNotContain(first, step.Aliases);   // E1 è già nell'analisi corrente
        Assert.Contains(first, plan.AliasesThrough(step.Number));
    }

    // ---------------------------------------------------------------- supporto

    /// <summary>Ogni passo, con l'analisi corrente lunga quanto il tetto di output, resta entro il budget della fase Analysis.</summary>
    private static void AssertStepsFit(AnalysisRunInput run, ConsolidationPlan plan, int window)
    {
        var budget = window - Output - Margin;
        foreach (var step in plan.Steps)
        {
            var messages = AnalysisConsolidation.Messages(run, plan, step.Number, step.Number == 1 ? null : Prior());
            var tokens = InstructionWords + messages.Sum(m => Words(m.Text));
            Assert.True(tokens <= budget, $"passo {step.Number}: {tokens} token oltre il budget {budget}");
            foreach (var alias in step.Aliases)
            {
                Assert.Contains(run.Evidence!.EvidenceBlock(run.Evidence.Evidences.Single(e => e.Alias == alias)), messages[^1].Text); // evidenza intera
            }
        }
    }

    /// <summary>Analisi corrente della dimensione massima possibile: quanto il tetto di output della fase.</summary>
    private static string Prior() => string.Join(' ', Enumerable.Repeat("x", Output));

    private static AgentFactory Factory(int window) =>
        new(new AiOptions { ContextWindowTokens = window, MaxOutputTokens = Output, SafetyMarginTokens = Margin }, new NoModel(),
            (text, _) => new(Words(text)));

    private static AnalysisRunInput Run(int messages, int words, int evidencesPerMessage, int evidenceWords, bool relateLastToFirst = false)
    {
        var dialog = Enumerable.Range(1, messages)
            .Select(i => new AnalysisDialogMessage(Guid.NewGuid(), AnalysisMessageRole.User, i, string.Join(' ', Enumerable.Repeat($"parola{i}", words))))
            .ToList();
        var evidences = new List<ViewEvidence>();
        foreach (var m in dialog)
        {
            for (var o = 1; o <= evidencesPerMessage; o++)
            {
                var quote = string.Join(' ', Enumerable.Repeat($"fatto{m.Sequence}.{o}", evidenceWords));
                evidences.Add(new ViewEvidence($"E{evidences.Count + 1}", Guid.NewGuid(), m.Id, m.Sequence, o, EvidenceKind.Requirement,
                    EvidencePolarity.Affirmed, EvidenceCertainty.Declared, quote,
                    new EvidenceContent(null, new EvidenceStructure(null, null, null, null, null, null, null, null), [],
                        [new EvidenceSpan(m.Id, m.Sequence, EvidenceSourceRole.Primary, 0, quote.Length, quote)])));
            }
        }

        List<ViewRelation> relations = relateLastToFirst
            ? [new(evidences[^1].Alias, evidences[0].Alias, EvidenceRelationType.Contradicts, EvidenceRelationBasis.Declared, null, true, false)]
            : [];
        var view = new EvidenceSelectionView(
            [.. dialog.Select(m => new SelectionItem(m.Id, m.Sequence, Guid.NewGuid(), 1, evidencesPerMessage, null))], evidences, relations, []);
        return new AnalysisRunInput(Guid.NewGuid(), Guid.NewGuid(), messages, dialog, view);
    }

    private sealed class NoModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Il piano non chiama il modello.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Il piano non chiama il modello.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
