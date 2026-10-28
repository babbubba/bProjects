using System.Security.Claims;
using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace bProjects.App.Conversations;

/// <summary>
/// Dialogo del progetto (contracts/http-api.md): salvataggio dei messaggi PM, storico paginato e stato dei turni.
/// Il salvataggio non invoca alcuna risorsa AI: scrive soltanto originale e ChatTurn Queued su PostgreSQL.
/// </summary>
public static class ConversationEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public sealed record PostMessageRequest(Guid? ClientMessageId, string? Text);

    public sealed record MessageView(
        Guid Id,
        long Sequence,
        string Role,
        string Text,
        DateTimeOffset CreatedAt,
        Guid? AuthorId,
        string? AuthorDisplayName,
        string? AgentId,
        Guid? ChatTurnId,
        Guid? ReplyToMessageId);

    public sealed record PostMessageResponse(MessageView Message, Guid ChatTurnId);

    public sealed record MessagePage(IReadOnlyList<MessageView> Items, long? NextAfterSequence);

    public sealed record TurnError(string Code, string? Message);

    public sealed record ChatTurnView(
        Guid Id,
        Guid TriggerMessageId,
        long SourceCutoffSequence,
        string Status,
        DateTimeOffset RequestedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset DeadlineAt,
        int AttemptCount,
        int MaxAttempts,
        DateTimeOffset? NextAttemptAt,
        Guid? ResultMessageId,
        TurnError? Error);

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}");
        group.MapPost("/messages", PostMessage);
        group.MapGet("/messages", ListMessages);
        group.MapGet("/chat-turns/{turnId:guid}", GetChatTurn);
        return app;
    }

    private static async Task<IResult> PostMessage(
        Guid projectId,
        PostMessageRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        IOptions<ExecutionOptions> execution,
        ChatTurnEvents events,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.ClientMessageId is null || request.ClientMessageId == Guid.Empty)
        {
            errors["clientMessageId"] = ["clientMessageId è obbligatorio (UUID generato dal client)."];
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            errors["text"] = ["Il messaggio non può essere vuoto né contenere solo spazi."];
        }
        else if (request.Text.Length > ConversationMessage.MaxUserTextLength)
        {
            errors["text"] = [$"Il messaggio può avere al massimo {ConversationMessage.MaxUserTextLength} caratteri."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // Autore e ruolo vengono solo dalla sessione autenticata: il payload non può impostarli.
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var authorId))
        {
            return Results.Unauthorized();
        }

        var clientMessageId = request.ClientMessageId!.Value;
        var text = request.Text!;
        var policy = execution.Value;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Lock della conversazione: serializza assegnazione di Sequence, replay e fissaggio del cutoff.
        var conversationId = await LockConversation(db, projectId, ct);
        if (conversationId is null)
        {
            return Results.NotFound();
        }

        var existing = await db.ConversationMessages
            .Where(m => m.ConversationId == conversationId && m.ClientMessageId == clientMessageId)
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            // Ordinal: l'originale non viene normalizzato, quindi neppure il confronto.
            if (!string.Equals(existing.Body, text, StringComparison.Ordinal))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "clientMessageId già usato con un testo diverso",
                    extensions: new Dictionary<string, object?> { ["code"] = "idempotency_conflict" });
            }

            var existingTurnId = await db.ChatTurns
                .Where(t => t.TriggerMessageId == existing.Id)
                .Select(t => t.Id)
                .SingleAsync(ct);
            return Results.Ok(new PostMessageResponse(await ViewOf(db, existing, ct), existingTurnId));
        }

        var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId, ct);
        var now = await DatabaseNow(db, ct);
        conversation.LastSequence += 1;

        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Sequence = conversation.LastSequence,
            Role = MessageRole.User,
            Body = text,
            CreatedAt = now,
            ClientMessageId = clientMessageId,
            AuthorId = authorId,
        };
        var turn = new ChatTurn
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TriggerMessageId = message.Id,
            SourceCutoffSequence = message.Sequence,
            Status = ChatTurnStatus.Queued,
            RequestedAt = now,
            DeadlineAt = now + policy.RunDeadline,
            MaxAttempts = policy.MaxAttempts,
        };

        // Un solo SaveChanges: messaggio, turno e LastSequence sono scritti nella stessa transazione.
        db.ConversationMessages.Add(message);
        db.ChatTurns.Add(turn);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await events.PublishStateAsync(turn.Id); // post-commit; legge lo stato corrente, mai uno vecchio
        var view = await ViewOf(db, message, ct);
        return Results.Created($"/api/projects/{projectId}/messages/{message.Id}", new PostMessageResponse(view, turn.Id));
    }

    private static async Task<IResult> ListMessages(Guid projectId, AppDbContext db, long? afterSequence, int? limit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var pageSize = limit ?? DefaultLimit;
        if (pageSize is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"Il limite deve essere compreso tra 1 e {MaxLimit}."];
        }

        if (afterSequence is < 0)
        {
            errors["afterSequence"] = ["afterSequence non può essere negativo."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var conversationId = await db.Conversations.Where(c => c.ProjectId == projectId).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
        if (conversationId is null)
        {
            return Results.NotFound();
        }

        var after = afterSequence ?? 0;
        var items = await (
            from m in db.ConversationMessages
            where m.ConversationId == conversationId && m.Sequence > after
            orderby m.Sequence
            join u in db.Users on m.AuthorId equals u.Id into authors
            from author in authors.DefaultIfEmpty()
            select new { Message = m, AuthorName = author.UserName })
            .Take(pageSize)
            .ToListAsync(ct);

        var views = items.Select(i => ToView(i.Message, i.AuthorName)).ToList();
        return Results.Ok(new MessagePage(views, views.Count > 0 ? views[^1].Sequence : null));
    }

    private static async Task<IResult> GetChatTurn(Guid projectId, Guid turnId, AppDbContext db, CancellationToken ct)
    {
        // Il turno è raggiungibile solo attraverso la conversazione del proprio progetto.
        var turn = await (
            from t in db.ChatTurns
            join c in db.Conversations on t.ConversationId equals c.Id
            where t.Id == turnId && c.ProjectId == projectId
            select t).AsNoTracking().SingleOrDefaultAsync(ct);
        return turn is null ? Results.NotFound() : Results.Ok(ToView(turn));
    }

    private static async Task<Guid?> LockConversation(AppDbContext db, Guid projectId, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Conversations\" WHERE \"ProjectId\" = {projectId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Orario del database: stessa fonte temporale dei lease, così la deadline non dipende dall'orologio dell'app.</summary>
    private static async Task<DateTimeOffset> DatabaseNow(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct);
        return rows[0];
    }

    private static async Task<MessageView> ViewOf(AppDbContext db, ConversationMessage message, CancellationToken ct)
    {
        var authorName = message.AuthorId is { } id
            ? await db.Users.Where(u => u.Id == id).Select(u => u.UserName).SingleOrDefaultAsync(ct)
            : null;
        return ToView(message, authorName);
    }

    private static MessageView ToView(ConversationMessage m, string? authorName) => new(
        m.Id,
        m.Sequence,
        m.Role == MessageRole.User ? "user" : "assistant",
        m.Body,
        m.CreatedAt,
        m.AuthorId,
        authorName,
        m.AgentId,
        m.ChatTurnId,
        m.ReplyToMessageId);

    internal static ChatTurnView ToView(ChatTurn t) => new(
        t.Id,
        t.TriggerMessageId,
        t.SourceCutoffSequence,
        t.Status.ToString(),
        t.RequestedAt,
        t.StartedAt,
        t.CompletedAt,
        t.DeadlineAt,
        t.AttemptCount,
        t.MaxAttempts,
        t.NextAttemptAt,
        t.ResultMessageId,
        t.ErrorCode is null ? null : new TurnError(t.ErrorCode, t.ErrorMessage));
}
