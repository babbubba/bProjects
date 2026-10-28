using System.Text.Json;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Riferimento a un originale usato dalla sessione: identità e posizione nella history.</summary>
public sealed record ChatMessageRef(Guid Id, long Sequence);

/// <summary>
/// Snapshot derivato della sessione MAF di chat (colonna <c>Conversation.MafChatSessionState</c>): la sessione
/// serializzata più la provenienza verificabile al restore. Mai fonte autorevole: lo è la tabella dei messaggi.
/// </summary>
public sealed record ChatSessionEnvelope(
    Guid ConversationId,
    string MafVersion,
    string? ChatConfigVersion,
    List<ChatMessageRef> Messages,
    JsonElement Session);

/// <summary>Esito del restore: la sessione da usare e, se è stata ricostruita, il motivo (diagnostico, mai mostrato al PM).</summary>
public sealed record RestoredChatSession(AgentSession Session, bool Reused, string? RebuildReason);

/// <summary>
/// ChatHistoryProvider di un singolo ChatTurn (T015). La vista è sempre ricostruita dagli originali PostgreSQL:
/// user/assistant con Sequence &lt;= cutoff, trigger escluso (arriva una sola volta come input del run). Il provider
/// non scrive nulla: la risposta, lo snapshot e il watermark sono committati insieme da T018. Il riuso di una
/// sessione salvata è deciso da <see cref="RestoreAsync"/>, che altrimenti ne crea una nuova dagli originali.
/// </summary>
public sealed class PostgresChatHistoryProvider(AppDbContext db, Guid conversationId, long cutoffSequence, Guid triggerMessageId)
    : ChatHistoryProvider
{
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var history = await LoadAdmittedAsync(db, conversationId, cutoffSequence, triggerMessageId, cancellationToken);
        return history.Select(m => new ChatMessage(m.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Body));
    }

    // Nessuna persistenza anticipata: T018 committa risposta, snapshot e watermark nella stessa transazione.
    protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default) => default;

    /// <summary>Originali ammessi dal cutoff, in ordine di Sequence, escluso il trigger.</summary>
    internal static async Task<List<ConversationMessage>> LoadAdmittedAsync(
        AppDbContext db, Guid conversationId, long cutoffSequence, Guid triggerMessageId, CancellationToken ct) =>
        await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Sequence <= cutoffSequence && m.Id != triggerMessageId)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

    /// <summary>
    /// Restituisce la sessione del turno: lo snapshot salvato solo se watermark, versioni, provenienza e prefisso
    /// ordinato dei riferimenti sono tutti compatibili con la history ammessa dal cutoff; altrimenti una sessione
    /// nuova, senza deserializzare nulla dello snapshot (nessuna sintesi o indice oltre il cutoff).
    /// </summary>
    public static async Task<RestoredChatSession> RestoreAsync(
        AIAgent agent, AppDbContext db, Conversation conversation, ChatTurn turn, string? chatConfigVersion, CancellationToken ct)
    {
        var rebuildReason = await IncompatibilityAsync(db, conversation, turn, chatConfigVersion, ct);
        if (rebuildReason is null)
        {
            var envelope = JsonSerializer.Deserialize<ChatSessionEnvelope>(conversation.MafChatSessionState!)!;
            try
            {
                return new(await agent.DeserializeSessionAsync(envelope.Session, cancellationToken: ct), true, null);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
            {
                rebuildReason = "session_unreadable";
            }
        }

        return new(await agent.CreateSessionAsync(ct), false, rebuildReason);
    }

    /// <summary>Snapshot da committare con la risposta (T018): riferimenti = originali &lt;= cutoff, trigger incluso, più la risposta.</summary>
    public static async Task<string> CreateSnapshotAsync(
        AIAgent agent, AgentSession session, AppDbContext db, Conversation conversation, ChatTurn turn,
        ChatMessageRef response, string? chatConfigVersion, CancellationToken ct)
    {
        var used = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Sequence <= turn.SourceCutoffSequence)
            .OrderBy(m => m.Sequence)
            .Select(m => new ChatMessageRef(m.Id, m.Sequence))
            .ToListAsync(ct);
        used.Add(response);
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        return JsonSerializer.Serialize(new ChatSessionEnvelope(conversation.Id, Conversation.CurrentMafVersion, chatConfigVersion, used, serialized));
    }

    private static async Task<string?> IncompatibilityAsync(
        AppDbContext db, Conversation conversation, ChatTurn turn, string? chatConfigVersion, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(conversation.MafChatSessionState))
        {
            return "no_snapshot";
        }

        if (conversation.ContextThroughSequence > turn.SourceCutoffSequence)
        {
            return "watermark_beyond_cutoff";
        }

        if (conversation.MafVersion != Conversation.CurrentMafVersion || conversation.ChatConfigVersion != chatConfigVersion)
        {
            return "version_or_config";
        }

        ChatSessionEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ChatSessionEnvelope>(conversation.MafChatSessionState);
        }
        catch (JsonException)
        {
            return "snapshot_unreadable";
        }

        if (envelope is null || envelope.Messages is null || envelope.Messages.Count == 0
            || envelope.ConversationId != conversation.Id
            || envelope.MafVersion != conversation.MafVersion
            || envelope.ChatConfigVersion != chatConfigVersion
            || envelope.Messages[^1].Sequence != conversation.ContextThroughSequence)
        {
            return "provenance_mismatch";
        }

        var admitted = await LoadAdmittedAsync(db, conversation.Id, turn.SourceCutoffSequence, turn.TriggerMessageId, ct);
        if (envelope.Messages.Count > admitted.Count)
        {
            return "not_a_prefix";
        }

        for (var i = 0; i < envelope.Messages.Count; i++)
        {
            if (envelope.Messages[i].Id != admitted[i].Id || envelope.Messages[i].Sequence != admitted[i].Sequence)
            {
                return "not_a_prefix";
            }
        }

        return null;
    }
}
