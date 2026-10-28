using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Conversations;

public enum ChatTurnCommitOutcome
{
    /// <summary>Risposta, snapshot, watermark e Completed sono stati scritti da questa chiamata.</summary>
    Committed,

    /// <summary>Il turno era già Completed (commit precedente riuscito): nulla è stato modificato, ResultMessageId è quello esistente.</summary>
    AlreadyCompleted,

    /// <summary>Lease/fencing o revisione della sessione non più validi: nulla è stato modificato né pubblicato.</summary>
    Rejected,
}

public sealed record ChatTurnCommitResult(ChatTurnCommitOutcome Outcome, Guid? ResultMessageId = null, string? Reason = null);

/// <summary>
/// Commit atomico del turno conversazionale (T018, contracts/ai-workflow.md §1). In una sola transazione PostgreSQL,
/// sotto lo stesso lock di conversazione usato da POST /messages per assegnare le Sequence: verifica owner,
/// fencing e lease (tempo del database) e la ChatSessionRevision su cui il run si è basato, aggiunge l'unica risposta
/// assistant collegata al trigger, salva lo snapshot della sessione, porta ContextThroughSequence alla risposta,
/// incrementa la revisione e porta il turno a Completed con ResultMessageId. Un worker vecchio (lease perso, fence
/// diverso, revisione cambiata) non scrive nulla; un turno già Completed è riconciliato senza duplicare la risposta.
/// </summary>
public sealed class ChatTurnCompletion(AppDbContext db, ExecutionLeaseStore leases) : IChatTurnCommitter
{
    public async Task<ChatTurnCommitResult> CommitAsync(ChatTurnClaim claim, ConversationalReply reply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.Turn.Id != claim.TurnId || reply.Conversation.Id != claim.ConversationId || string.IsNullOrWhiteSpace(reply.Text))
        {
            throw new ArgumentException("La risposta non appartiene al turno preso in carico o è vuota.", nameof(reply));
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Stesso lock di POST /messages: serializza Sequence, snapshot e revisione con chiunque altro scriva la conversazione.
        var conversation = (await db.Conversations
            .FromSql($"SELECT * FROM \"Conversations\" WHERE \"Id\" = {claim.ConversationId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct)).SingleOrDefault();

        // La riga del turno è bloccata fino al commit: nessun reclaim può interporsi tra verifica e Completed.
        var state = (await db.Database.SqlQuery<string>($"""
            SELECT CASE
                WHEN "Status" = 'Completed' THEN 'completed'
                WHEN "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
                     AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp() THEN 'valid'
                ELSE 'stale' END AS "Value"
            FROM "ChatTurns" WHERE "Id" = {claim.TurnId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();

        if (conversation is null || state is null)
        {
            return new(ChatTurnCommitOutcome.Rejected, Reason: "turn_missing");
        }

        if (state == "completed")
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return await ReconcileAsync(claim.TurnId, ct) ?? new(ChatTurnCommitOutcome.Rejected, Reason: "completed_without_result");
        }

        if (state != "valid")
        {
            return new(ChatTurnCommitOutcome.Rejected, Reason: "lease_lost");
        }

        var startRevision = (await db.Database.SqlQuery<long>(
            $"""SELECT "SessionRevisionAtStart" AS "Value" FROM "ChatTurns" WHERE "Id" = {claim.TurnId}""").ToListAsync(ct)).Single();
        if (conversation.ChatSessionRevision != reply.Conversation.ChatSessionRevision
            || conversation.ChatSessionRevision != startRevision)
        {
            return new(ChatTurnCommitOutcome.Rejected, Reason: "session_revision_changed");
        }

        var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Sequence = ++conversation.LastSequence,
            Role = MessageRole.Assistant,
            Body = reply.Text,
            CreatedAt = now,
            AgentId = ConversationalAgent.AgentName,
            ReplyToMessageId = claim.TriggerMessageId,
            ChatTurnId = claim.TurnId,
        };
        db.ConversationMessages.Add(message);

        conversation.MafChatSessionState = await PostgresChatHistoryProvider.CreateSnapshotAsync(
            reply.Agent, reply.Session, db, conversation, reply.Turn, new(message.Id, message.Sequence), reply.ChatConfigVersion, ct);
        conversation.MafVersion = Conversation.CurrentMafVersion;
        conversation.ChatConfigVersion = reply.ChatConfigVersion;
        conversation.ContextThroughSequence = message.Sequence; // include la risposta appena committata
        conversation.ChatSessionRevision++;

        // Telemetria del tentativo vincente nella stessa transazione: rifiutata insieme alla risposta se il lease non è più valido.
        var inference = InferenceScope.Current;
        InferenceCallLog.Stage(db, inference, conversation.ProjectId, new(claim.TurnId, null, claim.AttemptCount, claim.FencingToken), result: true);
        await db.SaveChangesAsync(ct);

        if (!await leases.CompleteAsync(claim, message.Id, ct))
        {
            // Lease scaduto nell'intervallo: la transazione si annulla, la risposta non è mai stata visibile.
            return new(ChatTurnCommitOutcome.Rejected, Reason: "lease_lost");
        }

        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        db.ChangeTracker.Clear();
        return new(ChatTurnCommitOutcome.Committed, message.Id);
    }

    /// <summary>
    /// Riconciliazione post-commit, idempotente e di sola lettura: per un turno Completed restituisce il suo unico
    /// ResultMessageId dopo aver verificato il collegamento al trigger. Null se il turno non è Completed.
    /// Serve a chi deve notificare l'esito (SSE) dopo un crash o un commit di esito incerto, senza duplicare la risposta.
    /// </summary>
    public async Task<ChatTurnCommitResult?> ReconcileAsync(Guid turnId, CancellationToken ct)
    {
        var turn = await db.ChatTurns.AsNoTracking().SingleOrDefaultAsync(t => t.Id == turnId, ct);
        if (turn is not { Status: ChatTurnStatus.Completed, ResultMessageId: { } resultId })
        {
            return null;
        }

        var linked = await db.ConversationMessages.AsNoTracking().AnyAsync(m =>
            m.Id == resultId && m.Role == MessageRole.Assistant && m.ChatTurnId == turnId && m.ReplyToMessageId == turn.TriggerMessageId, ct);
        return linked ? new(ChatTurnCommitOutcome.AlreadyCompleted, resultId) : null;
    }
}
