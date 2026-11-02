using System.Security.Claims;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace bProjects.App.Evidence;

public sealed record JobError(string Code, string? Message);

public sealed record ExtractionJobView(
    Guid Id, Guid MessageId, long MessageSequence, string Reason, Guid? BatchId, Guid? RetryOfJobId, string Status,
    DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset DeadlineAt,
    int AttemptCount, int MaxAttempts, DateTimeOffset? NextAttemptAt, Guid? ResultVersionId, JobError? Error);

public sealed record VersionSummaryView(
    Guid Id, int Version, DateTimeOffset CreatedAt, int EvidenceCount, string? NoEvidenceReason, string PromptVersion, string SchemaVersion,
    string ModelId, Guid JobId, string Reason, bool IsCurrent);

public sealed record CurrentVersionView(Guid Id, int Version, int EvidenceCount, string? NoEvidenceReason, DateTimeOffset CreatedAt);

public sealed record MessageEvidenceStatusView(
    Guid MessageId, long Sequence, DateTimeOffset CreatedAt, int StateRevision, CurrentVersionView? CurrentVersion, int CandidateCount,
    ExtractionJobView? LatestJob, string State);

public sealed record EvidenceQueueView(int Queued, int Running, int Failed);

public sealed record EvidenceStatusView(IReadOnlyList<MessageEvidenceStatusView> Messages, EvidenceQueueView Queue);

public sealed record QualifierView(string Type, string Text);

public sealed record SourceView(Guid MessageId, long Sequence, string Role, int Start, int End, string Quote);

public sealed record EvidenceView(
    Guid Id, Guid VersionId, int Version, bool IsCurrent, Guid MessageId, long MessageSequence, int Ordinal, string Key, string Kind,
    string? KindRationale, string Statement, string? Subject, string? Action, string? Object, string? Scope, string? Purpose,
    string? Instrument, string? Channel, string? Recipient, IReadOnlyList<QualifierView> Qualifiers, string Polarity, string Certainty,
    IReadOnlyList<SourceView> Sources);

public sealed record SourceMessageView(Guid Id, long Sequence, string Role, string Text);

public sealed record EvidenceRelationDetailView(
    Guid Id, string Type, string Basis, string? Rationale, string Direction, Guid OtherEvidenceId, string OtherStatement, long OtherMessageSequence);

public sealed record EvidenceDetailView(EvidenceView Evidence, IReadOnlyList<SourceMessageView> Messages, IReadOnlyList<EvidenceRelationDetailView> Relations);

public sealed record PromotionView(Guid Id, Guid? FromVersionId, Guid ToVersionId, string? PromotedBy, DateTimeOffset PromotedAt, string? Reason, JsonElement RelationImpact);

public sealed record MessageVersionsView(
    Guid MessageId, long Sequence, int StateRevision, Guid? CurrentVersionId, IReadOnlyList<VersionSummaryView> Versions, IReadOnlyList<PromotionView> Promotions);

public sealed record RelationView(Guid Id, Guid FromEvidenceId, Guid ToEvidenceId, string Type, string Basis, string? Rationale);

public sealed record ExtractionVersionView(VersionSummaryView Version, Guid MessageId, long MessageSequence, JsonElement Parameters, IReadOnlyList<EvidenceView> Evidences, IReadOnlyList<RelationView> Relations);

public sealed record FieldChangeView(string Field, string? From, string? To);

public sealed record DiffEntryView(string Status, EvidenceView? From, EvidenceView? To, IReadOnlyList<FieldChangeView> Changes, IReadOnlyList<Guid> Candidates, double Overlap);

public sealed record DiffSummaryView(int Added, int Removed, int Unchanged, int Modified, int Ambiguous);

public sealed record VersionDiffView(Guid FromVersionId, Guid ToVersionId, Guid MessageId, DiffSummaryView Summary, IReadOnlyList<DiffEntryView> Entries);

public sealed record PromotionResultView(Guid MessageId, int StateRevision, Guid CurrentVersionId, PromotionView? Promotion);

public sealed record BatchProgressView(int Queued, int Running, int Completed, int Failed);

public sealed record EvidenceBatchView(Guid Id, string Kind, DateTimeOffset RequestedAt, long ThroughSequence, int JobCount, int SkippedCount, BatchProgressView Progress);

public sealed record SelectionItemView(Guid MessageId, long Sequence, Guid VersionId, int Version);

public sealed record SelectionEvidenceView(string Alias, EvidenceView Evidence);

public sealed record SelectionRelationView(string FromAlias, string ToAlias, string Type, string Basis, bool Open, bool Remapped);

public sealed record SelectionLinkView(Guid ItemId, IReadOnlyList<Guid> EvidenceIds);

public sealed record EvidenceSelectionApiView(
    Guid RunId, DateTimeOffset CreatedAt, string ViewHash, IReadOnlyList<SelectionItemView> Items, IReadOnlyList<SelectionEvidenceView> Evidences,
    IReadOnlyList<SelectionRelationView> Relations, IReadOnlyList<StaleRelation> StaleRelations, IReadOnlyList<SelectionLinkView> Links);

public sealed record PromotionRequest(Guid? VersionId, int? ExpectedStateRevision, string? Reason);

/// <summary>
/// Pubblica <c>evidence.state</c> sullo stream del progetto con la vista corrente del lavoro: notifica effimera e best-effort come
/// <c>processing.state</c>, letta dal database dopo la transizione.
/// </summary>
public sealed class EvidenceEvents(ProjectEventHub hub, IServiceScopeFactory scopes, ILogger<EvidenceEvents> logger)
{
    public const string EvidenceState = "evidence.state";

    private readonly KeyedSerializer<Guid> _serializer = new();

    public Task PublishStateAsync(Guid jobId) => _serializer.RunAsync(jobId, async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var job = await scope.ServiceProvider.GetRequiredService<AppDbContext>().EvidenceExtractionJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId);
            if (job is not null)
            {
                hub.Publish(job.ProjectId, EvidenceState, EvidenceEndpoints.ToView(job));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Notifica evidence.state non pubblicata: {ExceptionType}", ex.GetType().Name);
        }
    });
}

/// <summary>
/// API del registro delle evidenze (contracts/http-api.md §Registro). Ogni ID è cercato dentro il progetto: un ID di un altro progetto è
/// 404. I comandi accodano soltanto lavori durevoli sotto il lock della conversazione (lo stesso del salvataggio dei messaggi e di
/// Analizza) e non invocano mai il modello; nessuna operazione modifica messaggi, versioni, analisi, revisioni o bozze esistenti.
/// </summary>
public static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapEvidenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}");
        group.MapGet("/evidence/status", Status);
        group.MapGet("/evidence", List);
        group.MapGet("/evidence/{evidenceId:guid}", Detail);
        group.MapGet("/messages/{messageId:guid}/evidence/versions", Versions);
        group.MapGet("/evidence/versions/{versionId:guid}", Version);
        group.MapGet("/evidence/versions/{fromId:guid}/compare/{toId:guid}", Compare);
        group.MapPost("/messages/{messageId:guid}/evidence/reanalyses", ReanalyzeMessage);
        group.MapPost("/evidence/jobs/{jobId:guid}/retry", Retry);
        group.MapGet("/evidence/jobs/{jobId:guid}", Job);
        group.MapPost("/evidence/reanalyses", (Guid projectId, HttpContext http, ClaimsPrincipal user, AppDbContext db, IOptions<ExecutionOptions> execution, IOptions<AiOptions> ai, EvidenceEvents events, CancellationToken ct)
            => StartBatch(projectId, EvidenceBatchKind.Reanalysis, http, user, db, execution, ai, events, ct));
        group.MapPost("/evidence/backfills", (Guid projectId, HttpContext http, ClaimsPrincipal user, AppDbContext db, IOptions<ExecutionOptions> execution, IOptions<AiOptions> ai, EvidenceEvents events, CancellationToken ct)
            => StartBatch(projectId, EvidenceBatchKind.Backfill, http, user, db, execution, ai, events, ct));
        group.MapGet("/evidence/batches/{batchId:guid}", Batch);
        group.MapPost("/messages/{messageId:guid}/evidence/promotions", Promote);
        group.MapGet("/analyses/{analysisId:guid}/evidence-selection", Selection);
        group.MapCoverageEndpoints();
        return app;
    }

    // ---------------------------------------------------------------- letture

    private static async Task<IResult> Status(Guid projectId, AppDbContext db, CancellationToken ct)
    {
        var conversationId = await db.Conversations.AsNoTracking().Where(c => c.ProjectId == projectId).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
        if (conversationId is null)
        {
            return Results.NotFound();
        }

        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.User)
            .OrderBy(m => m.Sequence)
            .Select(m => new { m.Id, m.Sequence, m.CreatedAt })
            .ToListAsync(ct);
        var states = await db.EvidenceMessageStates.AsNoTracking().Where(s => s.ProjectId == projectId).ToDictionaryAsync(s => s.MessageId, ct);
        var versions = await db.EvidenceExtractionVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.OriginKind == EvidenceOriginKind.Message)
            .Select(v => new { v.Id, v.MessageId, v.Version, v.EvidenceCount, v.NoEvidenceReason, v.CreatedAt })
            .ToListAsync(ct);
        var jobs = (await db.EvidenceExtractionJobs.AsNoTracking().Where(j => j.ProjectId == projectId && j.OriginKind == EvidenceOriginKind.Message).ToListAsync(ct))
            .GroupBy(j => j.MessageId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.RequestedAt).ThenByDescending(j => j.Id).First());

        var items = new List<MessageEvidenceStatusView>();
        foreach (var m in messages)
        {
            var state = states.GetValueOrDefault(m.Id);
            var current = state is null ? null : versions.Single(v => v.Id == state.CurrentVersionId);
            var job = jobs.GetValueOrDefault(m.Id);
            var label = job?.Status switch
            {
                ExtractionJobStatus.Queued => "pending",
                ExtractionJobStatus.Running => "running",
                _ when current is not null => current.EvidenceCount == 0 ? "empty" : "extracted",
                ExtractionJobStatus.Failed => "failed",
                _ => "none",
            };
            items.Add(new(
                m.Id, m.Sequence, m.CreatedAt, state?.Revision ?? 0,
                current is null ? null : new CurrentVersionView(current.Id, current.Version, current.EvidenceCount, current.NoEvidenceReason, current.CreatedAt),
                current is null ? 0 : versions.Count(v => v.MessageId == m.Id && v.Version > current.Version),
                job is null ? null : ToView(job),
                label));
        }

        var latest = jobs.Values.ToList();
        return Results.Ok(new EvidenceStatusView(items, new(
            latest.Count(j => j.Status == ExtractionJobStatus.Queued),
            latest.Count(j => j.Status == ExtractionJobStatus.Running),
            latest.Count(j => j.Status == ExtractionJobStatus.Failed))));
    }

    private static async Task<IResult> List(
        Guid projectId, AppDbContext db, Guid? messageId, string? kind, string? polarity, string? certainty, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        EvidenceKind? k = null;
        EvidencePolarity? p = null;
        EvidenceCertainty? c = null;
        if (kind is not null)
        {
            if (Enum.TryParse<EvidenceKind>(kind, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)) { k = parsed; }
            else { errors["kind"] = ["Tipo di evidenza non valido."]; }
        }

        if (polarity is not null)
        {
            if (Enum.TryParse<EvidencePolarity>(polarity, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)) { p = parsed; }
            else { errors["polarity"] = ["Polarità non valida."]; }
        }

        if (certainty is not null)
        {
            if (Enum.TryParse<EvidenceCertainty>(certainty, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)) { c = parsed; }
            else { errors["certainty"] = ["Certezza non valida."]; }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct))
        {
            return Results.NotFound();
        }

        var current = db.EvidenceMessageStates.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.CurrentVersionId);
        var query = from e in db.Evidences.AsNoTracking()
                    join v in db.EvidenceExtractionVersions.AsNoTracking() on e.VersionId equals v.Id
                    where e.ProjectId == projectId && e.OriginKind == EvidenceOriginKind.Message && current.Contains(e.VersionId)
                    select new { e, v };
        if (messageId is { } m) { query = query.Where(x => x.e.MessageId == m); }
        if (k is { } kk) { query = query.Where(x => x.e.Kind == kk); }
        if (p is { } pp) { query = query.Where(x => x.e.Polarity == pp); }
        if (c is { } cc) { query = query.Where(x => x.e.Certainty == cc); }
        var rows = await query.ToListAsync(ct);
        return Results.Ok(new
        {
            items = rows.OrderBy(x => x.v.MessageSequence).ThenBy(x => x.e.Ordinal).Select(x => ToView(x.e, x.v, isCurrent: true)).ToList(),
        });
    }

    private static async Task<IResult> Detail(Guid projectId, Guid evidenceId, AppDbContext db, CancellationToken ct)
    {
        var row = await (from e in db.Evidences.AsNoTracking()
                         join v in db.EvidenceExtractionVersions.AsNoTracking() on e.VersionId equals v.Id
                         where e.Id == evidenceId && e.ProjectId == projectId && e.OriginKind == EvidenceOriginKind.Message
                         select new { e, v }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        var isCurrent = await db.EvidenceMessageStates.AsNoTracking().AnyAsync(s => s.MessageId == row.e.MessageId && s.CurrentVersionId == row.v.Id, ct);
        var view = ToView(row.e, row.v, isCurrent);
        var messageIds = view.Sources.Select(s => s.MessageId).Distinct().ToList();
        var messages = await db.ConversationMessages.AsNoTracking().Where(m => messageIds.Contains(m.Id)).OrderBy(m => m.Sequence)
            .Select(m => new SourceMessageView(m.Id, m.Sequence, m.Role == MessageRole.User ? "user" : "assistant", m.Body)).ToListAsync(ct);
        var relations = await db.EvidenceRelations.AsNoTracking()
            .Where(r => r.ProjectId == projectId && (r.FromEvidenceId == evidenceId || r.ToEvidenceId == evidenceId))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
        var otherIds = relations.Select(r => r.FromEvidenceId == evidenceId ? r.ToEvidenceId : r.FromEvidenceId).Distinct().ToList();
        var others = await (from o in db.Evidences.AsNoTracking()
                            join ov in db.EvidenceExtractionVersions.AsNoTracking() on o.VersionId equals ov.Id
                            where otherIds.Contains(o.Id)
                            select new { o.Id, o.Statement, ov.MessageSequence }).ToDictionaryAsync(x => x.Id, ct);
        return Results.Ok(new EvidenceDetailView(view, messages, [.. relations.Select(r =>
        {
            var outgoing = r.FromEvidenceId == evidenceId;
            var other = others[outgoing ? r.ToEvidenceId : r.FromEvidenceId];
            return new EvidenceRelationDetailView(
                r.Id, r.Type.ToString(), r.Basis.ToString(), r.Rationale, outgoing ? "outgoing" : "incoming", other.Id, other.Statement, other.MessageSequence!.Value);
        })]));
    }

    private static async Task<IResult> Versions(Guid projectId, Guid messageId, AppDbContext db, CancellationToken ct)
    {
        var message = await UserMessageAsync(db, projectId, messageId, ct);
        if (message is null)
        {
            return Results.NotFound();
        }

        var state = await db.EvidenceMessageStates.AsNoTracking().SingleOrDefaultAsync(s => s.MessageId == messageId, ct);
        var versions = await (from v in db.EvidenceExtractionVersions.AsNoTracking()
                              join j in db.EvidenceExtractionJobs.AsNoTracking() on v.JobId equals j.Id
                              where v.MessageId == messageId && v.ProjectId == projectId
                              orderby v.Version
                              select new { v, j.Reason }).ToListAsync(ct);
        var promotions = await db.EvidencePromotions.AsNoTracking().Where(p => p.MessageId == messageId).OrderBy(p => p.PromotedAt).ToListAsync(ct);
        var names = await UserNames(db, promotions.Select(p => p.PromotedBy), ct);
        return Results.Ok(new MessageVersionsView(
            messageId, message.Sequence, state?.Revision ?? 0, state?.CurrentVersionId,
            [.. versions.Select(x => Summary(x.v, x.Reason, x.v.Id == state?.CurrentVersionId))],
            [.. promotions.Select(p => ToView(p, names))]));
    }

    private static async Task<IResult> Version(Guid projectId, Guid versionId, AppDbContext db, CancellationToken ct)
    {
        var row = await (from v in db.EvidenceExtractionVersions.AsNoTracking()
                         join j in db.EvidenceExtractionJobs.AsNoTracking() on v.JobId equals j.Id
                         where v.Id == versionId && v.ProjectId == projectId && v.OriginKind == EvidenceOriginKind.Message
                         select new { v, j.Reason }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        var isCurrent = await db.EvidenceMessageStates.AsNoTracking().AnyAsync(s => s.MessageId == row.v.MessageId && s.CurrentVersionId == versionId, ct);
        var evidences = await db.Evidences.AsNoTracking().Where(e => e.VersionId == versionId).OrderBy(e => e.Ordinal).ToListAsync(ct);
        var relations = await db.EvidenceRelations.AsNoTracking().Where(r => r.VersionId == versionId).OrderBy(r => r.Id).ToListAsync(ct);
        using var parameters = JsonDocument.Parse(row.v.Parameters);
        return Results.Ok(new ExtractionVersionView(
            Summary(row.v, row.Reason, isCurrent), row.v.MessageId!.Value, row.v.MessageSequence!.Value, parameters.RootElement.Clone(),
            [.. evidences.Select(e => ToView(e, row.v, isCurrent))],
            [.. relations.Select(r => new RelationView(r.Id, r.FromEvidenceId, r.ToEvidenceId, r.Type.ToString(), r.Basis.ToString(), r.Rationale))]));
    }

    private static async Task<IResult> Compare(Guid projectId, Guid fromId, Guid toId, AppDbContext db, CancellationToken ct)
    {
        var versions = await db.EvidenceExtractionVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.OriginKind == EvidenceOriginKind.Message && (v.Id == fromId || v.Id == toId)).ToListAsync(ct);
        var from = versions.SingleOrDefault(v => v.Id == fromId);
        var to = versions.SingleOrDefault(v => v.Id == toId);
        if (from is null || to is null)
        {
            return Results.NotFound();
        }

        if (from.MessageId != to.MessageId)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Si confrontano solo versioni dello stesso messaggio", "versions_not_comparable");
        }

        var store = new EvidenceLedgerStore(db);
        var snapshots = await store.SnapshotsAsync([fromId, toId], ct);
        var relationRecords = await store.RelationsAsync([fromId, toId], ct);
        var statements = (await store.SnapshotsAsync([.. relationRecords.Select(r => r.ToVersionId).Distinct()], ct)).ToDictionary(e => e.Id, e => e.Statement);
        var relations = relationRecords.GroupBy(r => r.FromEvidenceId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)[.. g.Select(r => $"{r.Type} ({r.Basis}) → «{statements.GetValueOrDefault(r.ToEvidenceId)}»")]);
        var diff = EvidenceVersionComparer.Compare(
            [.. snapshots.Where(e => e.VersionId == fromId)], [.. snapshots.Where(e => e.VersionId == toId)], relations);
        var currentId = await db.EvidenceMessageStates.AsNoTracking().Where(s => s.MessageId == from.MessageId).Select(s => (Guid?)s.CurrentVersionId).SingleOrDefaultAsync(ct);
        EvidenceView? View(EvidenceSnapshot? s) => s is null ? null : ToView(s, s.VersionId == fromId ? from.Version : to.Version, s.VersionId == currentId);
        return Results.Ok(new VersionDiffView(
            fromId, toId, from.MessageId!.Value,
            new DiffSummaryView(diff.Count(DiffStatus.Added), diff.Count(DiffStatus.Removed), diff.Count(DiffStatus.Unchanged), diff.Count(DiffStatus.Modified), diff.Count(DiffStatus.Ambiguous)),
            [.. diff.Entries.Select(e => new DiffEntryView(
                e.Status.ToString(), View(e.From), View(e.To), [.. e.Changes.Select(c => new FieldChangeView(c.Field, c.From, c.To))], e.Candidates, e.Overlap))]));
    }

    private static async Task<IResult> Job(Guid projectId, Guid jobId, AppDbContext db, CancellationToken ct)
    {
        var job = await db.EvidenceExtractionJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId && j.OriginKind == EvidenceOriginKind.Message, ct);
        return job is null ? Results.NotFound() : Results.Ok(ToView(job));
    }

    private static async Task<IResult> Batch(Guid projectId, Guid batchId, AppDbContext db, CancellationToken ct)
    {
        var batch = await db.EvidenceBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == batchId && b.ProjectId == projectId, ct);
        return batch is null ? Results.NotFound() : Results.Ok(await ToViewAsync(db, batch, ct));
    }

    private static async Task<IResult> Selection(Guid projectId, Guid analysisId, AppDbContext db, CancellationToken ct)
    {
        var selection = await db.AnalysisEvidenceSelections.AsNoTracking().SingleOrDefaultAsync(s => s.RunId == analysisId && s.ProjectId == projectId, ct);
        if (selection is null)
        {
            var exists = await db.ProcessingRuns.AsNoTracking().AnyAsync(r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct);
            return exists ? Problem(StatusCodes.Status404NotFound, "Selezione delle evidenze non disponibile per questo run", "selection_not_available") : Results.NotFound();
        }

        var view = EvidenceSelectionView.Deserialize(selection.View);
        var versionNumbers = view.Items.ToDictionary(i => i.VersionId, i => i.Version);
        var links = await db.AnalysisEvidenceLinks.AsNoTracking().Where(l => l.RunId == analysisId).ToListAsync(ct);
        return Results.Ok(new EvidenceSelectionApiView(
            analysisId, selection.CreatedAt, selection.ViewHash,
            [.. view.Items.Select(i => new SelectionItemView(i.MessageId, i.Sequence, i.VersionId, i.Version))],
            [.. view.Evidences.Select(e => new SelectionEvidenceView(e.Alias, ToView(
                new EvidenceSnapshot(e.EvidenceId, view.Items.Single(i => i.MessageId == e.MessageId).VersionId, e.MessageId, e.Sequence, e.Ordinal, "",
                    e.Kind, e.Polarity, e.Certainty, e.Statement, e.Content),
                versionNumbers[view.Items.Single(i => i.MessageId == e.MessageId).VersionId], isCurrent: false)))],
            [.. view.Relations.Select(r => new SelectionRelationView(r.FromAlias, r.ToAlias, r.Type.ToString(), r.Basis.ToString(), r.Open, r.Remapped))],
            view.StaleRelations,
            [.. links.GroupBy(l => l.ItemId).OrderBy(g => g.Key).Select(g => new SelectionLinkView(g.Key, [.. g.Select(l => l.EvidenceId).Order()]))]));
    }

    // ---------------------------------------------------------------- comandi

    private static async Task<IResult> ReanalyzeMessage(
        Guid projectId, Guid messageId, HttpContext http, ClaimsPrincipal user, AppDbContext db, IOptions<ExecutionOptions> execution,
        IOptions<AiOptions> ai, EvidenceEvents events, CancellationToken ct)
    {
        if (RequestKey(http) is not { } key)
        {
            return Problem(StatusCodes.Status400BadRequest, "Idempotency-Key mancante o non valida", "idempotency_key_required");
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var requestedBy))
        {
            return Results.Unauthorized();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockConversationAsync(db, projectId, ct) is not { } conversationId)
        {
            return Results.NotFound();
        }

        if (await ReplayAsync(db, projectId, key, ct) is { } replay)
        {
            return replay;
        }

        var message = await UserMessageAsync(db, projectId, messageId, ct);
        if (message is null)
        {
            return Results.NotFound();
        }

        if (await OpenJobAsync(db, messageId, ct) is { } open)
        {
            return Problem(StatusCodes.Status409Conflict, "Un'estrazione di questo messaggio è già in corso", "extraction_active", open);
        }

        var now = await NowAsync(db, ct);
        var job = EvidenceLedgerStore.NewJob(projectId, conversationId, messageId, message.Sequence, ExtractionJobReason.Reanalysis, now,
            execution.Value, EvidenceLedgerStore.ExtractorConfig(ai.Value, execution.Value), requestedBy, requestKey: key);
        db.EvidenceExtractionJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await events.PublishStateAsync(job.Id);
        return Results.Accepted(JobLocation(projectId, job.Id), ToView(job));
    }

    private static async Task<IResult> Retry(
        Guid projectId, Guid jobId, HttpContext http, ClaimsPrincipal user, AppDbContext db, IOptions<ExecutionOptions> execution,
        IOptions<AiOptions> ai, EvidenceEvents events, CancellationToken ct)
    {
        if (RequestKey(http) is not { } key)
        {
            return Problem(StatusCodes.Status400BadRequest, "Idempotency-Key mancante o non valida", "idempotency_key_required");
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var requestedBy))
        {
            return Results.Unauthorized();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockConversationAsync(db, projectId, ct) is not { } conversationId)
        {
            return Results.NotFound();
        }

        if (await ReplayAsync(db, projectId, key, ct) is { } replay)
        {
            return replay;
        }

        var failed = await db.EvidenceExtractionJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId && j.OriginKind == EvidenceOriginKind.Message, ct);
        if (failed is null)
        {
            return Results.NotFound();
        }

        if (failed.Status != ExtractionJobStatus.Failed)
        {
            return Problem(StatusCodes.Status409Conflict, "Si può ritentare soltanto un lavoro fallito", "job_not_failed", failed.Id);
        }

        if (await OpenJobAsync(db, failed.MessageId!.Value, ct) is { } open)
        {
            return Problem(StatusCodes.Status409Conflict, "Un'estrazione di questo messaggio è già in corso", "extraction_active", open);
        }

        var now = await NowAsync(db, ct);
        var job = EvidenceLedgerStore.NewJob(projectId, conversationId, failed.MessageId!.Value, failed.MessageSequence!.Value, ExtractionJobReason.Reanalysis, now,
            execution.Value, EvidenceLedgerStore.ExtractorConfig(ai.Value, execution.Value), requestedBy, failed.BatchId, failed.Id, key);
        db.EvidenceExtractionJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await events.PublishStateAsync(job.Id);
        return Results.Accepted(JobLocation(projectId, job.Id), ToView(job));
    }

    private static async Task<IResult> StartBatch(
        Guid projectId, EvidenceBatchKind kind, HttpContext http, ClaimsPrincipal user, AppDbContext db, IOptions<ExecutionOptions> execution,
        IOptions<AiOptions> ai, EvidenceEvents events, CancellationToken ct)
    {
        if (RequestKey(http) is not { } key)
        {
            return Problem(StatusCodes.Status400BadRequest, "Idempotency-Key mancante o non valida", "idempotency_key_required");
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var requestedBy))
        {
            return Results.Unauthorized();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockConversationAsync(db, projectId, ct) is not { } conversationId)
        {
            return Results.NotFound();
        }

        var existing = await db.EvidenceBatches.AsNoTracking().SingleOrDefaultAsync(b => b.ProjectId == projectId && b.Kind == kind && b.RequestKey == key, ct);
        if (existing is not null)
        {
            var replay = await ToViewAsync(db, existing, ct);
            return replay.Progress.Queued + replay.Progress.Running > 0
                ? Results.Accepted(BatchLocation(projectId, existing.Id), replay)
                : Results.Ok(replay);
        }

        var through = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.User).MaxAsync(m => (long?)m.Sequence, ct);
        if (through is null)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Serve almeno un messaggio del project manager", "no_pm_input");
        }

        var now = await NowAsync(db, ct);
        var batch = new EvidenceBatch
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = kind,
            RequestKey = key,
            RequestedBy = requestedBy,
            RequestedAt = now,
            ThroughSequence = through.Value,
        };
        db.EvidenceBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        var (created, skipped) = await new EvidenceLedgerStore(db).EnqueueAsync(
            projectId, conversationId, through.Value, onlyMissing: kind == EvidenceBatchKind.Backfill,
            kind == EvidenceBatchKind.Backfill ? ExtractionJobReason.Backfill : ExtractionJobReason.Reanalysis,
            requestedBy, batch.Id, now, execution.Value, EvidenceLedgerStore.ExtractorConfig(ai.Value, execution.Value), ct);
        batch.JobCount = created.Count;
        batch.SkippedCount = skipped;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var job in created)
        {
            await events.PublishStateAsync(job.Id);
        }

        var view = await ToViewAsync(db, batch, ct);
        return created.Count > 0 ? Results.Accepted(BatchLocation(projectId, batch.Id), view) : Results.Ok(view);
    }

    private static async Task<IResult> Promote(
        Guid projectId, Guid messageId, PromotionRequest request, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.VersionId is null || request.VersionId == Guid.Empty)
        {
            errors["versionId"] = ["versionId è obbligatorio."];
        }

        if (request.ExpectedStateRevision is null or < 0)
        {
            errors["expectedStateRevision"] = ["expectedStateRevision è obbligatorio (revisione dello stato letta dal client)."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (!Guid.TryParse(user.FindFirstValue("sub"), out var promotedBy))
        {
            return Results.Unauthorized();
        }

        var result = await new EvidenceLedgerStore(db).PromoteAsync(projectId, messageId, request.VersionId!.Value, request.ExpectedStateRevision!.Value, promotedBy, request.Reason, ct);
        switch (result.Outcome)
        {
            case PromotionOutcome.NotFound:
                return Results.NotFound();
            case PromotionOutcome.VersionNotOfMessage:
                return Problem(StatusCodes.Status422UnprocessableEntity, "La versione non appartiene a questo messaggio", "version_not_of_message");
            case PromotionOutcome.Conflict:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Lo stato delle evidenze del messaggio è cambiato",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "promotion_conflict",
                        ["currentVersionId"] = result.State?.CurrentVersionId,
                        ["stateRevision"] = result.State?.Revision ?? 0,
                    });
            default:
                var names = await UserNames(db, [result.Promotion?.PromotedBy], ct);
                return Results.Ok(new PromotionResultView(
                    messageId, result.State!.Revision, result.State.CurrentVersionId, result.Promotion is null ? null : ToView(result.Promotion, names)));
        }
    }

    // ---------------------------------------------------------------- supporto

    private static Guid? RequestKey(HttpContext http) =>
        http.Request.Headers.TryGetValue("Idempotency-Key", out var raw) && raw.Count == 1 && Guid.TryParse(raw[0], out var key) && key != Guid.Empty ? key : null;

    /// <summary>Stessa chiave di un comando già eseguito (rianalisi o retry): stesso lavoro, 202 se aperto e 200 se terminale.</summary>
    private static async Task<IResult?> ReplayAsync(AppDbContext db, Guid projectId, Guid key, CancellationToken ct)
    {
        var job = await db.EvidenceExtractionJobs.AsNoTracking().SingleOrDefaultAsync(j => j.ProjectId == projectId && j.RequestKey == key, ct);
        return job is null
            ? null
            : job.Status is ExtractionJobStatus.Queued or ExtractionJobStatus.Running
                ? Results.Accepted(JobLocation(projectId, job.Id), ToView(job))
                : Results.Ok(ToView(job));
    }

    private static Task<Guid?> OpenJobAsync(AppDbContext db, Guid messageId, CancellationToken ct) =>
        db.EvidenceExtractionJobs.AsNoTracking()
            .Where(j => j.MessageId == messageId && (j.Status == ExtractionJobStatus.Queued || j.Status == ExtractionJobStatus.Running))
            .Select(j => (Guid?)j.Id).FirstOrDefaultAsync(ct);

    private static async Task<ConversationMessage?> UserMessageAsync(AppDbContext db, Guid projectId, Guid messageId, CancellationToken ct) =>
        await (from m in db.ConversationMessages.AsNoTracking()
               join c in db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
               where m.Id == messageId && c.ProjectId == projectId && m.Role == MessageRole.User
               select m).SingleOrDefaultAsync(ct);

    /// <summary>Lock della conversazione del progetto: lo stesso del salvataggio dei messaggi PM e di Analizza.</summary>
    internal static async Task<Guid?> LockConversationAsync(AppDbContext db, Guid projectId, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Conversations\" WHERE \"ProjectId\" = {projectId} FOR UPDATE")
            .ToListAsync(ct)).Cast<Guid?>().FirstOrDefault();

    private static async Task<DateTimeOffset> NowAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();

    private static async Task<EvidenceBatchView> ToViewAsync(AppDbContext db, EvidenceBatch batch, CancellationToken ct)
    {
        // Avanzamento reale per messaggio: conta l'ultimo lavoro del batch (un retry autorizzato sostituisce il lavoro fallito).
        var jobs = await db.EvidenceExtractionJobs.AsNoTracking().Where(j => j.BatchId == batch.Id)
            .Select(j => new { j.MessageId, j.Status, j.RequestedAt, j.Id }).ToListAsync(ct);
        var latest = jobs.GroupBy(j => j.MessageId).Select(g => g.OrderByDescending(j => j.RequestedAt).ThenByDescending(j => j.Id).First().Status).ToList();
        int Of(ExtractionJobStatus s) => latest.Count(x => x == s);
        return new(batch.Id, batch.Kind.ToString(), batch.RequestedAt, batch.ThroughSequence, batch.JobCount, batch.SkippedCount,
            new(Of(ExtractionJobStatus.Queued), Of(ExtractionJobStatus.Running), Of(ExtractionJobStatus.Completed), Of(ExtractionJobStatus.Failed)));
    }

    public static ExtractionJobView ToView(EvidenceExtractionJob j) => new(
        j.Id, j.MessageId!.Value, j.MessageSequence!.Value, j.Reason.ToString(), j.BatchId, j.RetryOfJobId, j.Status.ToString(), j.RequestedAt, j.StartedAt,
        j.CompletedAt, j.DeadlineAt, j.AttemptCount, j.MaxAttempts, j.NextAttemptAt, j.ResultVersionId,
        j.ErrorCode is null ? null : new JobError(j.ErrorCode, j.ErrorMessage));

    private static VersionSummaryView Summary(EvidenceExtractionVersion v, ExtractionJobReason reason, bool isCurrent) => new(
        v.Id, v.Version, v.CreatedAt, v.EvidenceCount, v.NoEvidenceReason, v.PromptVersion, v.SchemaVersion, v.ModelId, v.JobId, reason.ToString(), isCurrent);

    public static EvidenceView ToView(EvidenceItem e, EvidenceExtractionVersion v, bool isCurrent) =>
        ToView(EvidenceSnapshot.From(e, v.MessageSequence!.Value), v.Version, isCurrent);

    public static EvidenceView ToView(EvidenceSnapshot e, int version, bool isCurrent)
    {
        var s = e.Content.Structure;
        return new(
            e.Id, e.VersionId, version, isCurrent, e.MessageId, e.MessageSequence, e.Ordinal, e.Key, e.Kind.ToString(), e.Content.KindRationale,
            e.Statement, s.Subject, s.Action, s.Object, s.Scope, s.Purpose, s.Instrument, s.Channel, s.Recipient,
            [.. e.Content.Qualifiers.Select(q => new QualifierView(q.Type.ToString(), q.Text))], e.Polarity.ToString(), e.Certainty.ToString(),
            [.. e.Content.Sources.Select(x => new SourceView(x.MessageId, x.Sequence, x.Role.ToString(), x.Start, x.End, x.Quote))]);
    }

    private static PromotionView ToView(EvidencePromotion p, IReadOnlyDictionary<Guid, string?> names)
    {
        using var impact = JsonDocument.Parse(p.RelationImpact);
        return new(p.Id, p.FromVersionId, p.ToVersionId, p.PromotedBy is { } by ? names.GetValueOrDefault(by) : null, p.PromotedAt, p.Reason, impact.RootElement.Clone());
    }

    internal static async Task<Dictionary<Guid, string?>> UserNames(AppDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
    }

    private static string JobLocation(Guid projectId, Guid jobId) => $"/api/projects/{projectId}/evidence/jobs/{jobId}";

    private static string BatchLocation(Guid projectId, Guid batchId) => $"/api/projects/{projectId}/evidence/batches/{batchId}";

    internal static IResult Problem(int status, string title, string code, Guid? jobId = null) => Results.Problem(
        statusCode: status,
        title: title,
        extensions: jobId is null
            ? new Dictionary<string, object?> { ["code"] = code }
            : new Dictionary<string, object?> { ["code"] = code, ["jobId"] = jobId });
}
