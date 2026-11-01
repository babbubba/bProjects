using System.Globalization;
using System.Text;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Un passo del consolidamento: i messaggi del dialogo (per sequenza) e le evidenze (alias globali) che vi entrano interi.</summary>
public sealed record ConsolidationStep(int Number, IReadOnlyList<long> Sequences, IReadOnlyList<string> Aliases);

/// <summary>
/// Piano del consolidamento di un run (contracts/ai-workflow.md §10.7). Un solo passo quando dialogo, istruzioni e vista del registro
/// stanno nel budget della fase Analysis, eventualmente con la compattazione nativa (la cui sintesi deve a sua volta stare nel budget della
/// fase Compaction); altrimenti passi successivi in ordine di sequenza, entro il cutoff, ciascuno con gli originali del proprio intervallo,
/// le sue evidenze complete e l'analisi corrente da aggiornare. Nessun limite al numero di evidenze, nessun testo troncato.
/// </summary>
public sealed record ConsolidationPlan(IReadOnlyList<ConsolidationStep> Steps)
{
    public bool Incremental => Steps.Count > 1;

    /// <summary>Alias introdotti fino al passo <paramref name="step"/> compreso: gli unici ammessi in evidenceKeys a quel punto.</summary>
    public IReadOnlyList<string> AliasesThrough(int step) => [.. Steps.Take(step).SelectMany(s => s.Aliases).Distinct()];

    public static ConsolidationPlan Single(AnalysisRunInput run) =>
        new([new ConsolidationStep(1, [.. run.Dialog.Select(m => m.Sequence)], run.Evidence?.Aliases ?? [])]);
}

public static class AnalysisConsolidation
{
    /// <summary>Promemoria finale del consolidamento, uguale per il passo singolo e per ogni passo incrementale.</summary>
    public const string Reminder =
        "Promemoria: compila le proprietà nell'ordine dello schema; ogni informazione in una sola sezione, con le parole del project manager; "
        + "negazioni ed esclusioni («non», «niente», «senza», «no») in contextAndScope.excluded; posizioni incompatibili solo in conflicts, mai anche come requisiti o vincoli; "
        + "openQuestions solo per ciò che il project manager dichiara di non sapere o di non aver deciso; nessuna informazione dei messaggi dell'assistente; non omettere nessuna frase del project manager che porti un fatto, un numero, un nome o un'esclusione; una frase con contrasto («mentre», «invece») resta in una voce sola.";

    public const string EvidenceKeysReminder =
        "In ogni voce, evidenceKeys elenca gli alias E… delle evidenze che la voce rappresenta; ogni evidenza del registro deve comparire in almeno una voce.";

    public const string Produce = "Produci ora l'unico oggetto JSON AnalysisContent 1.0.0.";

    /// <summary>Un messaggio del dialogo con il suo ruolo, id e sequenza.</summary>
    public static ChatMessage DialogMessage(AnalysisDialogMessage m) => new(
        m.Role == AnalysisMessageRole.User ? ChatRole.User : ChatRole.Assistant, $"[messaggio id={m.Id} sequenza={m.Sequence}]\n{m.Body}");

    /// <summary>Messaggi da inviare al passo <paramref name="step"/> (1-based); <paramref name="prior"/> è l'uscita del passo precedente.</summary>
    public static List<ChatMessage> Messages(AnalysisRunInput run, ConsolidationPlan plan, int step, string? prior)
    {
        var current = plan.Steps[step - 1];
        var sequences = current.Sequences.ToHashSet();
        var messages = run.Dialog.Where(m => sequences.Contains(m.Sequence)).Select(DialogMessage).ToList();
        messages.Add(new ChatMessage(ChatRole.User, plan.Incremental ? StepMessage(run, current, plan.Steps.Count, prior) : FinalMessage(run)));
        return messages;
    }

    /// <summary>Messaggio finale del passo singolo: vista completa del registro (preservata dalla compattazione) e dati del server.</summary>
    public static string FinalMessage(AnalysisRunInput run)
    {
        var final = new StringBuilder();
        if (run.Evidence is { } evidence)
        {
            // Nel messaggio finale, che la compattazione nativa preserva sempre: il registro non dipende dalla sintesi della history.
            final.AppendLine(evidence.ToPromptText());
        }

        return ServerData(final, run)
            .AppendLine(Reminder)
            .AppendLine(run.Evidence is null ? "" : EvidenceKeysReminder)
            .AppendLine(Produce)
            .ToString();
    }

    /// <summary>
    /// Messaggio finale di un passo incrementale: l'analisi corrente (uscita validata del passo precedente, con evidenceKeys), la parte del
    /// registro del passo e l'istruzione di restituire l'analisi completa aggiornata.
    /// </summary>
    public static string StepMessage(AnalysisRunInput run, ConsolidationStep step, int steps, string? prior)
    {
        var text = new StringBuilder();
        if (prior is not null)
        {
            text.AppendLine($"Analisi corrente dopo i passi 1–{step.Number - 1} di {steps} (AnalysisContent con evidenceKeys), da aggiornare e restituire per intero:")
                .AppendLine(prior);
        }

        text.AppendLine($"Consolidamento incrementale, passo {step.Number} di {steps}: i messaggi qui sopra sono gli originali delle sequenze "
                + string.Join(", ", step.Sequences.Select(s => s.ToString(CultureInfo.InvariantCulture)))
                + "; il registro qui sotto contiene le sole evidenze di questo passo (alias globali). Gli altri messaggi entro il cutoff sono già "
                + "rappresentati nell'analisi corrente o arriveranno nei passi successivi.")
            .AppendLine(run.Evidence!.ToPromptText(step.Aliases, step.Sequences));
        return ServerData(text, run)
            .AppendLine(Reminder)
            .AppendLine("Conserva tutte le voci e le fonti dell'analisi corrente con i loro evidenceKeys (puoi unire, correggere o spostare una voce se i "
                + "nuovi messaggi lo richiedono, senza perdere fatti) e aggiungi ciò che portano i messaggi e le evidenze di questo passo. "
                + "In ogni voce, evidenceKeys elenca gli alias E… delle evidenze che la voce rappresenta; ogni evidenza vista finora deve comparire in almeno una voce.")
            .AppendLine(Produce)
            .ToString();
    }

    private static StringBuilder ServerData(StringBuilder text, AnalysisRunInput run) => text
        .AppendLine("Dati affidabili del server per questa analisi:")
        .AppendLine($"- analysisId: {run.AnalysisId}")
        .AppendLine($"- projectId: {run.ProjectId}")
        .AppendLine($"- sourceCutoffSequence: {run.SourceCutoffSequence}");

    /// <summary>
    /// Pianifica il consolidamento con i token reali del modello. Passo singolo se la chiamata è eseguibile con le regole della compattazione
    /// nativa; altrimenti ogni passo riserva lo spazio dell'analisi corrente (al più il tetto di output della fase, perché è l'uscita del
    /// modello) e riceve messaggi interi in ordine di sequenza con tutte le loro evidenze. Le evidenze di un messaggio che non stanno in un
    /// passo si distribuiscono su più passi, ripetendo il messaggio originale; un messaggio che con una sola sua evidenza non sta nel passo
    /// è un limite strutturale esplicito (<see cref="ContextOverflowException"/>, context_overflow), mai una riduzione silenziosa.
    /// Deterministico: lo stesso input congelato e le stesse opzioni danno lo stesso piano, quindi la stessa topologia al resume.
    /// </summary>
    public static async Task<ConsolidationPlan> PlanAsync(AnalysisRunInput run, AgentFactory factory, CancellationToken ct)
    {
        var single = ConsolidationPlan.Single(run);
        var instructions = ConversationalAgent.ConsolidationInstructions;
        var dialog = new List<int>(run.Dialog.Count);
        foreach (var m in run.Dialog)
        {
            dialog.Add(await factory.CountAsync(DialogMessage(m).Text, ct));
        }

        if (run.Evidence is not { } view
            || await factory.FitsWithCompactionAsync(instructions, [.. dialog, await factory.CountAsync(FinalMessage(run), ct)], AiPhase.Analysis, ct))
        {
            return single;
        }

        var budget = ContextBudget.From(factory.Options, AiPhase.Analysis).InputBudgetTokens;
        var fixedTokens = await factory.CountAsync(instructions, ct)
            + factory.Options.ForPhase(AiPhase.Analysis).MaxOutputTokens   // l'analisi corrente è un'uscita del modello
            + await factory.CountAsync(StepMessage(run, new ConsolidationStep(run.Dialog.Count, [.. run.Dialog.Select(m => m.Sequence)], []), run.Dialog.Count, ""), ct);
        var capacity = budget - fixedTokens;
        if (capacity <= 0)
        {
            throw new ContextOverflowException(fixedTokens, budget);
        }

        var owned = view.Relations.GroupBy(view.RelationOwner).ToDictionary(g => g.Key, g => g.ToList());
        var steps = new List<ConsolidationStep>();
        var sequences = new List<long>();
        var aliases = new List<string>();
        var used = 0;

        void Close()
        {
            if (sequences.Count > 0)
            {
                steps.Add(new ConsolidationStep(steps.Count + 1, [.. sequences], [.. aliases]));
            }

            sequences = [];
            aliases = [];
            used = 0;
        }

        for (var i = 0; i < run.Dialog.Count; i++)
        {
            var message = run.Dialog[i];
            var blocks = new List<(string Alias, int Tokens)>();
            foreach (var e in view.Evidences.Where(e => e.MessageId == message.Id))
            {
                var block = view.EvidenceBlock(e) + string.Concat(owned.GetValueOrDefault(e.Alias, []).Select(EvidenceSelectionView.RelationLine));
                blocks.Add((e.Alias, await factory.CountAsync(block, ct)));
            }

            if (message.Role == AnalysisMessageRole.User && blocks.Count == 0)
            {
                blocks.Add(("", await factory.CountAsync($"Messaggi PM senza evidenze: sequenza {message.Sequence} ({EmptyReason(view, message.Id)})", ct)));
            }

            var need = dialog[i] + blocks.Sum(b => b.Tokens);
            if (used + need > capacity)
            {
                Close();
            }

            if (need <= capacity)
            {
                sequences.Add(message.Sequence);
                aliases.AddRange(blocks.Where(b => b.Alias.Length > 0).Select(b => b.Alias));
                used += need;
                continue;
            }

            // Le evidenze del messaggio non stanno tutte in un passo: più passi, ciascuno con il messaggio originale intero.
            foreach (var (alias, tokens) in blocks)
            {
                if (dialog[i] + tokens > capacity)
                {
                    throw new ContextOverflowException(fixedTokens + dialog[i] + tokens, budget);
                }

                if (used + tokens + (sequences.Contains(message.Sequence) ? 0 : dialog[i]) > capacity)
                {
                    Close();
                }

                if (!sequences.Contains(message.Sequence))
                {
                    sequences.Add(message.Sequence);
                    used += dialog[i];
                }

                aliases.Add(alias);
                used += tokens;
            }
        }

        Close();
        return steps.Count == 1 ? single : new ConsolidationPlan(steps);
    }

    /// <summary>Stato nativo del workflow (scope condiviso, nei checkpoint): alias già rappresentati dall'analisi corrente.</summary>
    public const string StateScope = "consolidation";

    public const string RepresentedKey = "representedAliases";

    /// <summary>
    /// Alias rappresentati nel passo precedente che l'uscita corrente non rappresenta più: un passo incrementale non può perdere evidenze
    /// già consolidate (sarebbe una perdita silenziosa), quindi ogni mancanza è una violazione esplicita.
    /// </summary>
    public static List<string> Lost(IReadOnlyCollection<string>? previous, IReadOnlyDictionary<Guid, IReadOnlyList<string>> keys)
    {
        if (previous is null || previous.Count == 0)
        {
            return [];
        }

        var now = keys.Values.SelectMany(k => k).ToHashSet();
        return [.. previous.Where(a => !now.Contains(a))];
    }

    public static string LostViolation(IReadOnlyList<string> lost) =>
        $"evidenze già rappresentate nell'analisi corrente e assenti dall'uscita: {string.Join(", ", lost.Take(20))}{(lost.Count > 20 ? $" (+{lost.Count - 20})" : "")}";

    private static string? EmptyReason(EvidenceSelectionView view, Guid messageId) =>
        view.Items.FirstOrDefault(i => i.MessageId == messageId)?.NoEvidenceReason;
}

/// <summary>
/// Passo intermedio del consolidamento incrementale: valida l'uscita dell'agente del passo come analisi completa (evidenceKeys ammessi
/// solo fra gli alias già introdotti) e consegna al passo successivo i suoi originali, la sua parte del registro e l'analisi corrente.
/// Output non valido ⇒ Failed <c>invalid_agent_output</c> come in ValidatePersist: nessuna riparazione, nessuna seconda chiamata.
/// </summary>
internal sealed class ConsolidationStepExecutor(IServiceScopeFactory scopes, AnalysisRunClaim claim, ConsolidationPlan plan, int step)
    : ChatProtocolExecutor(IdOf(step), new ChatProtocolExecutorOptions { AutoSendTurnToken = false })
{
    public static string IdOf(int step) => $"ConsolidationStep{step}";

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<List<ChatMessage>>().SendsMessage<TurnToken>();

    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken = default)
    {
        var text = string.Concat(messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text));
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>();
        var input = await store.LoadInputAsync(claim.RunId, cancellationToken);
        var (content, keys, keyViolations) = AnalysisSchema.SplitEvidenceKeys(text, plan.AliasesThrough(step));
        var validation = AnalysisContentValidator.ValidateText(content, input.ToValidationContext(), AnalysisActorKind.Ai);
        var lost = AnalysisConsolidation.Lost(
            await context.ReadStateAsync<List<string>>(AnalysisConsolidation.RepresentedKey, AnalysisConsolidation.StateScope, cancellationToken), keys);
        if (lost.Count > 0)
        {
            keyViolations.Add(AnalysisConsolidation.LostViolation(lost));
        }

        if (!validation.Accepted || keyViolations.Count > 0)
        {
            var violations = validation.Violations.Concat(keyViolations).ToList();
            await store.FailAsync(claim, validation.Code ?? AnalysisContentValidator.InvalidAgentOutput,
                $"L'output del modello al passo {step} di {plan.Steps.Count} non è un'analisi valida ({violations.Count} violazioni): "
                + string.Join(" | ", violations.Take(5)), cancellationToken);
            return;
        }

        await context.QueueStateUpdateAsync(AnalysisConsolidation.RepresentedKey, keys.Values.SelectMany(k => k).Distinct().Order().ToList(),
            AnalysisConsolidation.StateScope, cancellationToken);
        await context.SendMessageAsync(AnalysisConsolidation.Messages(input, plan, step + 1, text), cancellationToken: cancellationToken);
        await context.SendMessageAsync(new TurnToken(emitEvents), cancellationToken: cancellationToken);
    }
}
