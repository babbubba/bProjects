using System.Reflection;
using System.Text;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>
/// Esito di un'esecuzione conversazionale, in attesa del commit atomico (T018): testo completo della risposta e
/// sessione MAF aggiornata. Nulla è stato scritto: né messaggi, né snapshot, né watermark.
/// </summary>
public sealed record ConversationalReply(
    string Text,
    ChatClientAgent Agent,
    AgentSession Session,
    RestoredChatSession Restored,
    Conversation Conversation,
    ChatTurn Turn,
    string ChatConfigVersion);

/// <summary>Chi committa atomicamente risposta, snapshot, watermark e Completed (T018). Deve rifiutare se lease/fencing/revisione non sono più validi.</summary>
public interface IChatTurnCommitter
{
    Task<ChatTurnCommitResult> CommitAsync(ChatTurnClaim claim, ConversationalReply reply, CancellationToken ct);
}

/// <summary>
/// ConversationalAgent in modalità dialogo (T017): un ChatClientAgent con prompt e sessione propri, separati
/// dall'analisi. Il modello legge la history dal solo <see cref="PostgresChatHistoryProvider"/> e riceve il trigger
/// una sola volta; non ha strumenti, quindi non può modificare requisiti o bozze né avviare comandi formali.
/// La risposta arriva in streaming (<c>RunStreamingAsync</c>); i delta sono provvisori e inoltrati a <c>onDelta</c>,
/// mentre la persistenza definitiva resta del commit di T018.
/// </summary>
public sealed class ConversationalAgent(AgentFactory factory, ChatOptions chatOptions)
{
    public const string AgentName = "ConversationalAgent";
    public const string PromptVersion = "conversation-v1";

    /// <summary>Versione della configurazione di chat risolta: cambia con prompt, modello o limiti e invalida gli snapshot.</summary>
    public const string ChatConfigVersion = PromptVersion;

    private static readonly Lazy<string> Prompt = new(LoadPrompt);

    /// <summary>Istruzioni del dialogo (AI/Prompts/conversation.md, risorsa incorporata).</summary>
    public static string Instructions => Prompt.Value;

    public async Task<ConversationalReply> RunAsync(
        AppDbContext db, ChatTurnClaim claim, Func<string, CancellationToken, ValueTask>? onDelta, CancellationToken ct)
    {
        var turn = await db.ChatTurns.AsNoTracking().SingleAsync(t => t.Id == claim.TurnId, ct);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == turn.ConversationId, ct);
        var trigger = await db.ConversationMessages.AsNoTracking()
            .SingleAsync(m => m.Id == turn.TriggerMessageId && m.ConversationId == conversation.Id && m.Role == MessageRole.User, ct);

        var options = new ChatOptions
        {
            ModelId = chatOptions.ModelId,
            MaxOutputTokens = chatOptions.MaxOutputTokens,
            Temperature = chatOptions.Temperature,
            RawRepresentationFactory = chatOptions.RawRepresentationFactory,
            Instructions = Instructions,
        };
        var agent = new ChatClientAgent(
            await factory.CreateCompactingClientAsync(Instructions, ct),
            new ChatClientAgentOptions
            {
                Name = AgentName,
                ChatOptions = options,
                ChatHistoryProvider = new PostgresChatHistoryProvider(db, conversation.Id, turn.SourceCutoffSequence, turn.TriggerMessageId),
            });

        var restored = await PostgresChatHistoryProvider.RestoreAsync(agent, db, conversation, turn, ChatConfigVersion, ct);

        var text = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync(trigger.Body, restored.Session, cancellationToken: ct))
        {
            // Solo testo assistant: il ragionamento (TextReasoningContent) non entra in Text e non viene mai esposto.
            if (update.Text is { Length: > 0 } delta)
            {
                text.Append(delta);
                if (onDelta is not null)
                {
                    await onDelta(delta, ct);
                }
            }
        }

        var reply = text.ToString();
        if (string.IsNullOrWhiteSpace(reply))
        {
            // Una risposta vuota non è un esito: errore permanente che il dispatcher registra come Failed.
            throw new InvalidOperationException("Il modello ha restituito una risposta vuota.");
        }

        return new(reply, agent, restored.Session, restored, conversation, turn, ChatConfigVersion);
    }

    private static string LoadPrompt()
    {
        using var stream = typeof(ConversationalAgent).Assembly.GetManifestResourceStream("Prompts.conversation.md")
            ?? throw new InvalidOperationException("Prompt conversation.md non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
}

/// <summary>
/// Esecutore dei ChatTurn collegato al dispatcher (T014): esegue il ConversationalAgent e consegna il risultato al
/// <see cref="IChatTurnCommitter"/>. Il token ricevuto si annulla quando il lease si perde.
/// </summary>
public sealed class ConversationalTurnExecutor(IServiceScopeFactory scopes, ConversationalAgent agent) : IChatTurnExecutor
{
    public async Task ExecuteAsync(ChatTurnClaim claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reply = await agent.RunAsync(db, claim, onDelta: null, ct);
        // Un commit rifiutato (lease perso, revisione cambiata) o già riconciliato non modifica nulla: il dispatcher
        // tenta Failed solo con owner/fencing, quindi un vecchio worker non cambia mai l'esito.
        await scope.ServiceProvider.GetRequiredService<IChatTurnCommitter>().CommitAsync(claim, reply, ct);
    }
}
