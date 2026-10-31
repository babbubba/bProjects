using System.Text;
using System.Text.Json;
using bProjects.App.Conversations;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Esecuzione di un lavoro di estrazione già in lease; deve onorare il token, annullato quando il lease si perde.</summary>
public interface IEvidenceExtractionExecutor
{
    Task ExecuteAsync(ExtractionJobClaim claim, CancellationToken ct);
}

/// <summary>
/// Estrattore delle evidenze atomiche (contracts/ai-workflow.md §10.2–10.5): un <see cref="ChatClientAgent"/> MAF con prompt e opzioni
/// della fase Extraction, senza history, strumenti né compattazione; un solo messaggio utente costruito dal server con il messaggio da
/// estrarre e il contesto ammesso (mai sequenze successive), output JSON Schema vincolato al lavoro. Nessun workflow né checkpoint: la
/// chiamata è singola e ripetibile, la durabilità è del lavoro in PostgreSQL. Un output non valido rende il lavoro Failed
/// (invalid_agent_output) senza riparazioni; un errore del modello risale al dispatcher, che lo classifica.
/// </summary>
public sealed class EvidenceExtractor(IServiceScopeFactory scopes, AgentFactory factory, ChatOptions extractionOptions, EvidenceEvents? events = null)
    : IEvidenceExtractionExecutor
{
    public const string AgentName = "EvidenceExtractor";
    public const string PromptVersion = "extraction-v1";

    private static readonly Lazy<string> Prompt = new(() => LoadPrompt("extraction.md"));

    public static string Instructions => Prompt.Value;

    public async Task ExecuteAsync(ExtractionJobClaim claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new EvidenceLedgerStore(db);
        var context = await store.LoadContextAsync(claim.JobId, ct);

        // Contesto mirato entro il budget della fase: si tolgono prima gli originali PM precedenti più vecchi (già estratti, le loro
        // evidenze restano nel contesto), poi le evidenze precedenti più vecchie, infine il referente. Il messaggio da estrarre non si
        // riduce mai: se da solo supera il budget, context_overflow esplicito e permanente. Le omissioni sono registrate nella versione.
        var omittedMessages = new List<long>();
        var omittedEvidence = 0;
        var messages = BuildMessages(context);
        while (!await factory.FitsExtractionBudgetAsync(messages, Instructions, ct))
        {
            if (context.PriorUserMessages.Count > 0)
            {
                omittedMessages.Add(context.PriorUserMessages[0].Sequence);
                context = context with { PriorUserMessages = [.. context.PriorUserMessages.Skip(1)] };
            }
            else if (context.PriorEvidences.Count > 0)
            {
                omittedEvidence++;
                context = context with { PriorEvidences = [.. context.PriorEvidences.Skip(1)] };
            }
            else if (context.Referent is not null)
            {
                context = context with { Referent = null };
            }
            else
            {
                await factory.EnsureExtractionWithinBudgetAsync(messages, Instructions, ct); // lancia ContextOverflowException con i token reali
            }

            messages = BuildMessages(context);
        }

        var options = InternalModelClient.WithJsonSchema(extractionOptions, EvidenceExtractionSchema.ForJob(context), EvidenceExtractionSchema.Name);
        var agent = new ChatClientAgent(factory.CreateExtractionClient(), new ChatClientAgentOptions
        {
            Name = AgentName,
            ChatOptions = new ChatOptions
            {
                ModelId = options.ModelId,
                MaxOutputTokens = options.MaxOutputTokens,
                Temperature = options.Temperature,
                RawRepresentationFactory = options.RawRepresentationFactory,
                ResponseFormat = options.ResponseFormat,
                Instructions = Instructions,
            },
        });
        var response = await agent.RunAsync(messages, cancellationToken: ct);

        // Solo il testo dell'assistente: il ragionamento non è testo e non entra mai nel contenuto né nel database.
        var text = string.Concat(response.Messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text));
        var validation = EvidenceExtractionValidator.ValidateText(text, context);
        if (!validation.Accepted)
        {
            var summary = $"L'output del modello non è un'estrazione valida ({validation.Violations.Count} violazioni): " + string.Join(" | ", validation.Violations.Take(5));
            if (!await store.FailAsync(claim, EvidenceExtractionValidator.InvalidAgentOutput, summary, ct))
            {
                throw new LeaseLostException("Failed dell'estrazione rifiutato: il lease non è più valido.");
            }

            await Publish(claim.JobId);
            return;
        }

        var config = await db.EvidenceExtractionJobs.AsNoTracking().Where(j => j.Id == claim.JobId).Select(j => j.ExtractorConfig).SingleAsync(ct);
        using var configDocument = JsonDocument.Parse(config);
        var parameters = JsonSerializer.Serialize(new
        {
            config = configDocument.RootElement,
            context = new
            {
                messageSequence = context.Target.Sequence,
                priorUserSequences = context.PriorUserMessages.Select(m => m.Sequence),
                omittedPriorUserSequences = omittedMessages,
                referentSequence = context.Referent?.Sequence,
                priorEvidenceIds = context.PriorEvidences.Select(p => p.EvidenceId),
                omittedPriorEvidence = omittedEvidence,
            },
        });
        var commit = await store.CommitAsync(claim, context, validation, options.ModelId ?? "", parameters, ct);
        if (commit.Outcome == ExtractionCommitOutcome.Rejected)
        {
            throw new LeaseLostException($"Commit dell'estrazione rifiutato ({commit.Reason}): il lease non è più valido.");
        }

        await Publish(claim.JobId);
    }

    private Task Publish(Guid jobId) => events is null ? Task.CompletedTask : events.PublishStateAsync(jobId);

    /// <summary>
    /// Il messaggio utente unico: dati del server, evidenze precedenti (alias P…), originali PM precedenti come contesto, l'eventuale
    /// messaggio dell'assistente come referente e, per ultimo, il messaggio da estrarre. Ogni messaggio porta id, sequenza e ruolo.
    /// </summary>
    public static List<ChatMessage> BuildMessages(ExtractionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = new StringBuilder()
            .AppendLine("Dati affidabili del server per questa estrazione:")
            .AppendLine($"- messaggio da estrarre: id {context.Target.Id}, sequenza {context.Target.Sequence}")
            .AppendLine($"- schema: {EvidenceExtractionVersion.CurrentSchemaVersion}");

        if (context.PriorEvidences.Count > 0)
        {
            text.AppendLine()
                .AppendLine("Evidenze già registrate per i messaggi PM precedenti (usa gli alias P… solo come bersaglio delle relazioni; non ripeterle):");
            foreach (var p in context.PriorEvidences)
            {
                text.AppendLine($"{p.Alias} · sequenza {p.Sequence} · {p.Kind} · {p.Polarity} · {p.Statement}");
            }
        }

        if (context.PriorUserMessages.Count > 0)
        {
            text.AppendLine()
                .AppendLine("Messaggi PM precedenti (solo contesto per chiarire riferimenti; citali con ruolo Context solo quando servono a capire il messaggio da estrarre):");
            foreach (var m in context.PriorUserMessages)
            {
                text.AppendLine($"[messaggio id={m.Id} sequenza={m.Sequence} ruolo=user]").AppendLine(m.Body);
            }
        }

        if (context.Referent is { } referent)
        {
            text.AppendLine()
                .AppendLine("Ultimo messaggio dell'assistente prima del messaggio da estrarre (mai fonte di fatti: citalo con ruolo Referent solo se il PM vi risponde):")
                .AppendLine($"[messaggio id={referent.Id} sequenza={referent.Sequence} ruolo=assistant]")
                .AppendLine(referent.Body);
        }

        text.AppendLine()
            .AppendLine("Messaggio PM da estrarre:")
            .AppendLine($"[messaggio id={context.Target.Id} sequenza={context.Target.Sequence} ruolo=user]")
            .AppendLine(context.Target.Body)
            .AppendLine()
            .Append("Produci ora l'unico oggetto JSON evidence-1.0.0 per il messaggio da estrarre.");
        return [new ChatMessage(ChatRole.User, text.ToString())];
    }

    private static string LoadPrompt(string file)
    {
        using var stream = typeof(EvidenceExtractor).Assembly.GetManifestResourceStream("Prompts." + file)
            ?? throw new InvalidOperationException($"Prompt {file} non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
}
