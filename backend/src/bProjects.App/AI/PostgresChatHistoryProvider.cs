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
public sealed class PostgresChatHistoryProvider(
    AppDbContext db, Guid conversationId, long cutoffSequence, Guid triggerMessageId, string? documentSnapshotHash = null)
    : ChatHistoryProvider
{
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var history = await LoadAdmittedAsync(db, conversationId, cutoffSequence, triggerMessageId, cancellationToken, documentSnapshotHash);
        return history.Select(m => new ChatMessage(m.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Body));
    }

    // Nessuna persistenza anticipata: T018 committa risposta, snapshot e watermark nella stessa transazione.
    protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default) => default;

    /// <summary>
    /// Originali ammessi dal cutoff, in ordine di Sequence, escluso il trigger. Con <paramref name="documentSnapshotHash"/> (chat documentale,
    /// 002 T058) il contesto operativo contiene tutti i messaggi PM ma solo gli assistant nati da un turno con la STESSA selezione congelata
    /// (hash dello snapshot): un assistant di un'altra selezione, o del ramo senza snapshot, resta nello storico ma non è input operativo,
    /// così fonti escluse o non più valide non rientrano indirettamente tramite vecchie risposte o sintesi.
    /// </summary>
    internal static async Task<List<ConversationMessage>> LoadAdmittedAsync(
        AppDbContext db, Guid conversationId, long cutoffSequence, Guid triggerMessageId, CancellationToken ct, string? documentSnapshotHash = null)
    {
        var query = db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Sequence <= cutoffSequence && m.Id != triggerMessageId);
        if (documentSnapshotHash is not null)
        {
            query = query.Where(m => m.Role == MessageRole.User
                || db.ChatTurns.Any(t => t.Id == m.ChatTurnId
                    && db.SourceSnapshots.Any(s => s.Id == t.SourceSnapshotId && s.Hash == documentSnapshotHash)));
        }

        return await query.OrderBy(m => m.Sequence).ToListAsync(ct);
    }

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
                return new(await agent.DeserializeSessionAsync(WithMetadataFirst(envelope.Session), cancellationToken: ct), true, null);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
            {
                rebuildReason = "session_unreadable";
            }
        }

        return new(await agent.CreateSessionAsync(ct), false, rebuildReason);
    }

    /// <summary>
    /// jsonb non conserva l'ordine delle proprietà, ma il polimorfismo di System.Text.Json (es. lo stato della
    /// compattazione) richiede "$type" come prima proprietà: lo si riporta in testa in ogni oggetto prima del restore.
    /// </summary>
    internal static JsonElement WithMetadataFirst(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(element, writer);
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();

        static void Write(JsonElement e, Utf8JsonWriter w)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var p in e.EnumerateObject().Where(p => p.Name == "$type"))
                    {
                        w.WritePropertyName(p.Name);
                        Write(p.Value, w);
                    }

                    foreach (var p in e.EnumerateObject().Where(p => p.Name != "$type"))
                    {
                        w.WritePropertyName(p.Name);
                        Write(p.Value, w);
                    }

                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in e.EnumerateArray())
                    {
                        Write(item, w);
                    }

                    w.WriteEndArray();
                    break;
                default:
                    e.WriteTo(w);
                    break;
            }
        }
    }

    /// <summary>Snapshot da committare con la risposta (T018): riferimenti = originali &lt;= cutoff, trigger incluso, più la risposta.</summary>
    public static async Task<string> CreateSnapshotAsync(
        AIAgent agent, AgentSession session, AppDbContext db, Conversation conversation, ChatTurn turn,
        ChatMessageRef response, string? chatConfigVersion, CancellationToken ct, string? documentSnapshotHash = null)
    {
        // Riferimenti = originali operativi fino al cutoff (trigger incluso); con la selezione documentale gli stessi che il turno ha visto.
        var used = (await LoadAdmittedAsync(db, conversation.Id, turn.SourceCutoffSequence, Guid.Empty, ct, documentSnapshotHash))
            .Select(m => new ChatMessageRef(m.Id, m.Sequence))
            .ToList();
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
