using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Conversations;

/// <summary>
/// Pubblica sullo stream SSE del progetto (<see cref="ProjectEventHub"/>) lo stato dei ChatTurn e i delta provvisori.
/// Le notifiche sono effimere e best-effort: un errore qui non tocca mai il lavoro durabile. I delta partono solo
/// dall'owner con lease e fencing ancora validi sul database; lo stato si legge dal database dopo la transizione.
/// </summary>
public sealed class ChatTurnEvents(ProjectEventHub hub, IServiceScopeFactory scopes, ILogger<ChatTurnEvents> logger)
{
    /// <summary>Pubblica chat.state con la vista corrente del turno (da chiamare dopo il commit della transizione).</summary>
    private readonly KeyedSerializer<Guid> _serializer = new();

    public Task PublishStateAsync(Guid turnId) => _serializer.RunAsync(turnId, async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await (
                from t in db.ChatTurns
                join c in db.Conversations on t.ConversationId equals c.Id
                where t.Id == turnId
                select new { Turn = t, c.ProjectId }).AsNoTracking().SingleOrDefaultAsync();
            if (row is not null)
            {
                hub.Publish(row.ProjectId, ProjectEventHub.ChatState, ConversationEndpoints.ToView(row.Turn));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Notifica chat.state non pubblicata: {ExceptionType}", ex.GetType().Name);
        }
    });

    /// <summary>
    /// Sink dei delta di testo di un tentativo (deltaIndex da 0). Ogni delta verifica owner/fencing/lease sul database:
    /// al primo rifiuto il tentativo è stantio e non emette più nulla.
    /// </summary>
    public Func<string, CancellationToken, ValueTask> DeltaSink(ChatTurnClaim claim)
    {
        var index = 0;
        var stale = false;
        return async (text, ct) =>
        {
            if (stale)
            {
                return;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var projectId = (await db.Database.SqlQuery<Guid>($"""
                    SELECT c."ProjectId" AS "Value" FROM "ChatTurns" t JOIN "Conversations" c ON c."Id" = t."ConversationId"
                    WHERE t."Id" = {claim.TurnId} AND t."Status" = 'Running' AND t."LeaseOwner" = {claim.LeaseOwner}
                      AND t."FencingToken" = {claim.FencingToken}
                      AND t."LeaseUntil" > clock_timestamp() AND t."DeadlineAt" > clock_timestamp()
                    """).ToListAsync(ct)).SingleOrDefault();
                if (projectId == Guid.Empty)
                {
                    stale = true;
                    return;
                }

                hub.Publish(projectId, ProjectEventHub.ChatDelta, new
                {
                    chatTurnId = claim.TurnId,
                    attemptCount = claim.AttemptCount,
                    deltaIndex = index++,
                    text,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Notifica chat.delta non pubblicata: {ExceptionType}", ex.GetType().Name);
            }
        };
    }
}
