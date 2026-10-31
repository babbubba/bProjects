using System.Globalization;
using System.Security.Claims;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Projects;

/// <summary>
/// Catalogo e riapertura dei progetti. Tutti i PM vedono tutti i progetti (fallback policy dell'app).
/// Nessuna operazione qui avvia elaborazioni AI: sono sole letture/scritture su PostgreSQL.
/// </summary>
public static class ProjectEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public sealed record CreateProjectRequest(string? Name, string? Description);

    /// <summary>
    /// ProjectView del contratto HTTP. I riferimenti ad analisi, revisioni, run formali e bozze sono null
    /// finché le relative funzionalità non esistono; createdAt/createdBy distinguono i progetti omonimi.
    /// </summary>
    public sealed record ProjectView(
        Guid Id,
        Guid ConversationId,
        string Name,
        string? Description,
        DateTimeOffset CreatedAt,
        string CreatedBy,
        long LatestSequence,
        long LatestUserSequence,
        IReadOnlyList<Guid> PendingChatTurnIds,
        Guid? ActiveProcessingRunId,
        Guid? LatestAnalysisRunId,
        Guid? CurrentAnalysisId,
        Guid? CurrentRevisionId,
        string? ValidationState,
        Guid? LatestDraftId);

    public sealed record ProjectPage(IReadOnlyList<ProjectView> Items, string? NextCursor);

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");
        group.MapPost("/", Create);
        group.MapGet("/", List);
        group.MapGet("/{projectId:guid}", Get);
        return app;
    }

    private static async Task<IResult> Create(CreateProjectRequest request, ClaimsPrincipal user, AppDbContext db, TimeProvider time)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Il nome del progetto è obbligatorio e non può contenere solo spazi."];
        }
        else if (request.Name.Length > Project.MaxNameLength)
        {
            errors["name"] = [$"Il nome del progetto può avere al massimo {Project.MaxNameLength} caratteri."];
        }

        if (request.Description is { Length: > Project.MaxDescriptionLength })
        {
            errors["description"] = [$"La descrizione può avere al massimo {Project.MaxDescriptionLength} caratteri."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
        {
            return Results.Unauthorized();
        }

        var now = time.GetUtcNow();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = request.Name!,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            CreatedBy = userId,
            CreatedAt = now,
        };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CreatedAt = now,
            MafVersion = Conversation.CurrentMafVersion,
        };

        // Progetto e conversazione nello stesso SaveChanges: una sola transazione.
        db.Projects.Add(project);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        var row = await Rows(db, db.Projects.Where(p => p.Id == project.Id)).SingleAsync();
        return Results.Created($"/api/projects/{project.Id}", ToView(row, await LoadActivity(db, [row])));
    }

    private static async Task<IResult> List(AppDbContext db, string? cursor, int? limit)
    {
        var pageSize = limit ?? DefaultLimit;
        if (pageSize is < 1 or > MaxLimit)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["limit"] = [$"Il limite deve essere compreso tra 1 e {MaxLimit}."] },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var projects = db.Projects.AsQueryable();
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var createdAt, out var id))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Cursore non valido", extensions: new Dictionary<string, object?> { ["code"] = "invalid_cursor" });
            }

            projects = projects.Where(p => p.CreatedAt < createdAt || (p.CreatedAt == createdAt && p.Id.CompareTo(id) < 0));
        }

        var ordered = await projects
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Take(pageSize + 1)
            .Select(p => p.Id)
            .ToListAsync();
        var pageIds = ordered.Take(pageSize).ToList();
        var byId = (await Rows(db, db.Projects.Where(p => pageIds.Contains(p.Id))).ToListAsync()).ToDictionary(r => r.Project.Id);
        var page = pageIds.Select(id => byId[id]).ToList();
        var next = ordered.Count > pageSize ? EncodeCursor(page[^1].Project) : null;
        var activity = await LoadActivity(db, page);
        return Results.Ok(new ProjectPage(page.Select(r => ToView(r, activity)).ToList(), next));
    }

    private static async Task<IResult> Get(Guid projectId, AppDbContext db)
    {
        var row = await Rows(db, db.Projects.Where(p => p.Id == projectId)).SingleOrDefaultAsync();
        return row is null ? Results.NotFound() : Results.Ok(ToView(row, await LoadActivity(db, [row])));
    }

    private sealed record Row(
        Project Project, Guid ConversationId, long LastSequence, string? CreatedByName);

    // Filtri e ordinamento si applicano ai Project prima della proiezione: EF non li traduce su Row.
    private static IQueryable<Row> Rows(AppDbContext db, IQueryable<Project> projects) =>
        from p in projects
        join c in db.Conversations on p.Id equals c.ProjectId
        join u in db.Users on p.CreatedBy equals u.Id
        select new Row(
            p,
            c.Id,
            c.LastSequence,
            u.UserName);

    /// <summary>Stato dell'analisi corrente: ultima revisione (versione massima) di Project.CurrentAnalysisId.</summary>
    private sealed record CurrentRevision(Guid AnalysisId, Guid Id, AnalysisValidationState ValidationState);

    /// <summary>
    /// Dati derivati: turni pendenti (FIFO) e ultima sequenza PM per conversazione; run formale attivo, ultimo run di
    /// Analysis e revisione corrente per progetto. Le liste restano vuote e i riferimenti null finché non esistono.
    /// </summary>
    private sealed record Activity(
        ILookup<Guid, Guid> PendingTurns,
        Dictionary<Guid, long> LatestUserSequence,
        Dictionary<Guid, Guid> ActiveRuns,
        Dictionary<Guid, Guid> LatestAnalysisRuns,
        Dictionary<Guid, CurrentRevision> CurrentRevisions);

    private static async Task<Activity> LoadActivity(AppDbContext db, IReadOnlyCollection<Row> rows)
    {
        var ids = rows.Select(r => r.ConversationId).ToList();
        var projectIds = rows.Select(r => r.Project.Id).ToList();
        var activeRuns = await db.ProcessingRuns.AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId) && (r.Status == ProcessingRunStatus.Queued || r.Status == ProcessingRunStatus.Running))
            .Select(r => new { r.ProjectId, r.Id })
            .ToDictionaryAsync(r => r.ProjectId, r => r.Id); // l'indice formale ne ammette uno solo per progetto
        var latestRuns = (await db.ProcessingRuns.AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId) && r.Kind == ProcessingRunKind.Analysis)
            .Select(r => new { r.ProjectId, r.Id, r.RequestedAt })
            .ToListAsync())
            .GroupBy(r => r.ProjectId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id).First().Id);
        var currentIds = rows.Select(r => r.Project.CurrentAnalysisId).OfType<Guid>().ToList();
        var currentRevisions = (await db.AnalysisRevisions.AsNoTracking()
            .Where(r => currentIds.Contains(r.AnalysisId))
            .Select(r => new { r.AnalysisId, r.Id, r.Version, r.ValidationState })
            .ToListAsync())
            .GroupBy(r => r.AnalysisId)
            .ToDictionary(g => g.Key, g =>
            {
                var top = g.OrderByDescending(r => r.Version).First();
                return new CurrentRevision(top.AnalysisId, top.Id, top.ValidationState);
            });
        var turns = await db.ChatTurns
            .Where(t => ids.Contains(t.ConversationId) && (t.Status == ChatTurnStatus.Queued || t.Status == ChatTurnStatus.Running))
            .OrderBy(t => t.SourceCutoffSequence)
            .Select(t => new { t.ConversationId, t.Id })
            .ToListAsync();
        var latestUser = await db.ConversationMessages
            .Where(m => ids.Contains(m.ConversationId) && m.Role == MessageRole.User)
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, Sequence = g.Max(m => m.Sequence) })
            .ToDictionaryAsync(x => x.ConversationId, x => x.Sequence);
        return new Activity(turns.ToLookup(t => t.ConversationId, t => t.Id), latestUser, activeRuns, latestRuns, currentRevisions);
    }

    private static ProjectView ToView(Row row, Activity activity) => new(
        row.Project.Id,
        row.ConversationId,
        row.Project.Name,
        row.Project.Description,
        row.Project.CreatedAt,
        row.CreatedByName ?? string.Empty,
        row.LastSequence,
        activity.LatestUserSequence.GetValueOrDefault(row.ConversationId),
        activity.PendingTurns[row.ConversationId].ToList(),
        activity.ActiveRuns.TryGetValue(row.Project.Id, out var active) ? active : null,
        activity.LatestAnalysisRuns.TryGetValue(row.Project.Id, out var latest) ? latest : null,
        row.Project.CurrentAnalysisId,
        CurrentOf(row, activity)?.Id,
        CurrentOf(row, activity)?.ValidationState.ToString(),
        row.Project.LatestDraftId);

    private static CurrentRevision? CurrentOf(Row row, Activity activity) =>
        row.Project.CurrentAnalysisId is { } id && activity.CurrentRevisions.TryGetValue(id, out var revision) ? revision : null;

    private static string EncodeCursor(Project last)
    {
        var raw = $"{last.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{last.Id:N}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - (padded.Length % 4)) % 4, '=');
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "N", out id))
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
