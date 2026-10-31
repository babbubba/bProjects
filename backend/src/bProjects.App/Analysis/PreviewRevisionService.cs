using System.Text.Json;
using bProjects.App.Conversations;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Analysis;

public enum PreviewStatus
{
    Ok,
    NotFound,
    AnalysisNotCurrent,
    ProcessingActive,
    RevisionConflict,
    Invalid,
}

/// <summary>Esito di una modifica o conferma: la revisione risultante, quella corrente in caso di conflitto, il run attivo o le violazioni per campo.</summary>
public sealed record PreviewResult(
    PreviewStatus Status,
    Guid? RevisionId = null,
    Guid? ProcessingRunId = null,
    IReadOnlyList<string>? Violations = null);

/// <summary>
/// Modifica e conferma dell'anteprima (contracts/http-api.md, «Modifica e conferma dell'anteprima»; ai-workflow §4-§5).
/// Tutto avviene in una transazione sotto lo stesso lock della conversazione di Analizza e dei messaggi PM: modifiche, conferma e
/// nuova Analysis si serializzano, e ogni predicato (analisi corrente, Analysis attiva, revisione corrente) è letto dopo il lock.
/// Nessun ProcessingRun, lease o scadenza: l'attesa del PM è soltanto la revisione corrente Unvalidated.
/// </summary>
public sealed class PreviewRevisionService(AppDbContext db)
{
    /// <summary>Nuova revisione PmEdit (Version+1, ParentRevisionId, Unvalidated) con fonte PmRevision assegnata dal server alle parti modificate.</summary>
    public async Task<PreviewResult> EditAsync(Guid projectId, Guid analysisId, Guid expectedRevisionId, Guid author, string contentJson, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await LoadAsync(projectId, analysisId, expectedRevisionId, ct);
        if (state.Rejection is { } rejection)
        {
            return rejection;
        }

        if (state.ActiveAnalysis is { } active)
        {
            return new(PreviewStatus.ProcessingActive, ProcessingRunId: active);
        }

        var current = state.Current;
        if (!TryParse(contentJson, out var incoming, out var parseError))
        {
            return new(PreviewStatus.Invalid, Violations: [parseError!]);
        }

        if (!TryParse(current.ContentJson, out var currentContent, out _))
        {
            return new(PreviewStatus.Invalid, Violations: ["La revisione corrente non è un AnalysisContent valido."]);
        }

        var revisionId = Guid.NewGuid();
        var attributed = PreviewAttribution.Apply(incoming!, currentContent!, revisionId, Guid.NewGuid());
        var context = await ContextAsync(state, extraPmRevision: revisionId, ct);
        var validation = AnalysisContentValidator.Validate(attributed.Content, context, AnalysisActorKind.PmEdit, currentContent);
        var violations = attributed.Violations.Concat(validation.Violations).Distinct().ToList();
        if (violations.Count > 0)
        {
            return new(PreviewStatus.Invalid, Violations: violations);
        }

        var now = await NowAsync(ct);
        db.AnalysisRevisions.Add(new AnalysisRevision
        {
            Id = revisionId,
            AnalysisId = analysisId,
            Version = current.Version + 1,
            ParentRevisionId = current.Id,
            Origin = AnalysisRevisionOrigin.PmEdit,
            CreatedBy = author,
            CreatedAt = now,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion,
            ContentJson = AnalysisContentJson.Serialize(attributed.Content),
            ValidationState = AnalysisValidationState.Unvalidated,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(PreviewStatus.Ok, revisionId);
    }

    /// <summary>Conferma della revisione corrente: cambia soltanto stato e metadati, poi il JSON è congelato. Ripetuta sulla stessa revisione è idempotente.</summary>
    public async Task<PreviewResult> ConfirmAsync(Guid projectId, Guid analysisId, Guid expectedRevisionId, Guid confirmer, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await LoadAsync(projectId, analysisId, expectedRevisionId, ct, tracking: true);
        if (state.Rejection is { } rejection)
        {
            return rejection;
        }

        var current = state.Current;
        if (current.ValidationState == AnalysisValidationState.PmConfirmed)
        {
            return new(PreviewStatus.Ok, current.Id); // già confermata e ancora corrente: nulla da scrivere
        }

        if (state.ActiveAnalysis is { } active)
        {
            return new(PreviewStatus.ProcessingActive, ProcessingRunId: active);
        }

        if (!TryParse(current.ContentJson, out var currentContent, out _))
        {
            return new(PreviewStatus.Invalid, Violations: ["La revisione corrente non è un AnalysisContent valido."]);
        }

        var confirmed = currentContent! with { ValidationState = AnalysisContentState.PmConfirmed };
        var validation = AnalysisContentValidator.Validate(confirmed, await ContextAsync(state, extraPmRevision: null, ct), AnalysisActorKind.PmConfirm, currentContent);
        if (!validation.Accepted)
        {
            return new(PreviewStatus.Invalid, Violations: validation.Violations);
        }

        current.ContentJson = AnalysisContentJson.Serialize(confirmed);
        current.ValidationState = AnalysisValidationState.PmConfirmed;
        current.ConfirmedBy = confirmer;
        current.ConfirmedAt = await NowAsync(ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(PreviewStatus.Ok, current.Id);
    }

    private sealed record State(
        PreviewResult? Rejection,
        ProcessingRun Run,
        AnalysisRevision Current,
        IReadOnlyList<Guid> PmRevisionIds,
        Guid? ActiveAnalysis);

    /// <summary>Prende il lock della conversazione e rilegge, sotto lock, analisi corrente, revisione corrente e Analysis attiva.</summary>
    private async Task<State> LoadAsync(Guid projectId, Guid analysisId, Guid expectedRevisionId, CancellationToken ct, bool tracking = false)
    {
        State Reject(PreviewResult result) => new(result, null!, null!, [], null);

        var conversationId = (await db.Database
            .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Conversations\" WHERE \"ProjectId\" = {projectId} FOR UPDATE")
            .ToListAsync(ct)).Cast<Guid?>().FirstOrDefault();
        if (conversationId is null)
        {
            return Reject(new(PreviewStatus.NotFound));
        }

        var run = await db.ProcessingRuns.AsNoTracking().SingleOrDefaultAsync(
            r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct);
        if (run is null)
        {
            return Reject(new(PreviewStatus.NotFound));
        }

        // L'Analysis attiva si legge PRIMA dell'analisi corrente: il commit di una Analysis (revisione AI, Completed e CurrentAnalysisId, nella stessa
        // transazione) non prende il lock della conversazione, quindi in READ COMMITTED può avvenire fra due letture. Letta in questo ordine, o si vede
        // l'Analysis ancora attiva (409 processing_active) o la si vede completata e allora anche CurrentAnalysisId è già quello nuovo (analysis_not_current).
        // Durante Document attiva gli edit sono ammessi (l'input del run è già congelato): blocca solo un'Analysis attiva.
        var active = await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis
                && (r.Status == ProcessingRunStatus.Queued || r.Status == ProcessingRunStatus.Running))
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
        var currentAnalysisId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.CurrentAnalysisId).SingleAsync(ct);
        if (currentAnalysisId != analysisId)
        {
            return Reject(new(PreviewStatus.AnalysisNotCurrent));
        }

        var revisions = tracking ? db.AnalysisRevisions.AsTracking() : db.AnalysisRevisions.AsNoTracking();
        var current = await revisions.Where(r => r.AnalysisId == analysisId).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        if (current is null)
        {
            return Reject(new(PreviewStatus.NotFound));
        }

        if (current.Id != expectedRevisionId)
        {
            return Reject(new(PreviewStatus.RevisionConflict, current.Id));
        }

        var pmRevisionIds = await db.AnalysisRevisions.AsNoTracking()
            .Where(r => r.AnalysisId == analysisId && r.Origin == AnalysisRevisionOrigin.PmEdit)
            .Select(r => r.Id)
            .ToListAsync(ct);
        return new(null, run, current, pmRevisionIds, active);
    }

    private async Task<AnalysisValidationContext> ContextAsync(State state, Guid? extraPmRevision, CancellationToken ct)
    {
        var run = state.Run;
        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == run.ConversationId && m.Sequence <= run.SourceCutoffSequence)
            .Select(m => new { m.Id, m.Role, m.Sequence })
            .ToListAsync(ct);
        var pm = state.PmRevisionIds.ToHashSet();
        if (extraPmRevision is { } id)
        {
            pm.Add(id);
        }

        return new AnalysisValidationContext(
            run.ProjectId,
            run.Id,
            run.SourceCutoffSequence!.Value,
            [.. messages.Select(m => new AnalysisMessageRef(
                m.Id, run.ProjectId, m.Role == MessageRole.User ? AnalysisMessageRole.User : AnalysisMessageRole.Assistant, m.Sequence))],
            pm);
    }

    private async Task<DateTimeOffset> NowAsync(CancellationToken ct) =>
        (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();

    private static bool TryParse(string json, out AnalysisContent? content, out string? error)
    {
        try
        {
            content = AnalysisContentJson.Deserialize(json);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            content = null;
            error = "JSON non valido per AnalysisContent 1.0.0: " + ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Attribuzione server-side delle modifiche del PM (ai-workflow §4, regola 5). Il client invia il contenuto completo del form
/// senza poter dichiarare fonti: il server aggiunge una sola fonte PmRevision legata alla revisione in creazione (autore e data
/// sono quelli della revisione) e la cita come evidenza negli elementi nuovi o modificati e nelle risoluzioni nuove o modificate.
/// Fonti storiche, alternative dei conflitti esistenti e classificazioni scelte dal PM non sono toccate.
/// </summary>
internal static class PreviewAttribution
{
    internal sealed record Result(AnalysisContent Content, IReadOnlyList<string> Violations);

    public static Result Apply(AnalysisContent incoming, AnalysisContent current, Guid revisionId, Guid pmSourceId)
    {
        var violations = new List<string>();
        var currentSources = current.Sources.ToDictionary(s => s.Id);
        for (var i = 0; i < incoming.Sources.Count; i++)
        {
            if (!currentSources.TryGetValue(incoming.Sources[i].Id, out var known) || known != incoming.Sources[i])
            {
                violations.Add($"sources[{i}]: il PM non può aggiungere o modificare fonti; le assegna il server.");
            }
        }

        var currentItems = Items(current).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var currentConflicts = current.Conflicts.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var changed = false;

        AnalysisItem Attribute(AnalysisItem item)
        {
            if (currentItems.TryGetValue(item.Id, out var before)
                && before.Text == item.Text && before.Classification == item.Classification && before.EvidenceRefs.SequenceEqual(item.EvidenceRefs))
            {
                return item;
            }

            changed = true;
            return item with { EvidenceRefs = Cite(item.EvidenceRefs, pmSourceId) };
        }

        IReadOnlyList<AnalysisItem> Section(IReadOnlyList<AnalysisItem> items) => [.. items.Select(Attribute)];

        var conflicts = new List<AnalysisConflict>();
        for (var i = 0; i < incoming.Conflicts.Count; i++)
        {
            var conflict = incoming.Conflicts[i];
            currentConflicts.TryGetValue(conflict.Id, out var before);
            if (before is null)
            {
                changed = true;
            }
            else
            {
                if (before.Description != conflict.Description || before.Status != conflict.Status)
                {
                    changed = true;
                }

                var kept = conflict.Alternatives.Select(a => a.Id).ToHashSet();
                if (before.Alternatives.Any(a => !kept.Contains(a.Id)))
                {
                    violations.Add($"conflicts[{i}].alternatives: le alternative esistenti non possono essere rimosse.");
                }
            }

            var resolution = conflict.Resolution;
            if (resolution is not null && (before?.Resolution is null || !SameResolution(before.Resolution, resolution)))
            {
                changed = true;
                resolution = resolution with { EvidenceRefs = Cite(resolution.EvidenceRefs, pmSourceId) };
            }

            conflicts.Add(conflict with { Alternatives = Section(conflict.Alternatives), Resolution = resolution });
        }

        var content = incoming with
        {
            ValidationState = AnalysisContentState.Unvalidated, // lo impone il server a ogni nuovo edit
            Goals = Section(incoming.Goals),
            ContextAndScope = new AnalysisContextAndScope
            {
                Overview = Section(incoming.ContextAndScope.Overview),
                Included = Section(incoming.ContextAndScope.Included),
                Excluded = Section(incoming.ContextAndScope.Excluded),
            },
            Requirements = Section(incoming.Requirements),
            OperationalFlows = Section(incoming.OperationalFlows),
            Constraints = Section(incoming.Constraints),
            Dependencies = Section(incoming.Dependencies),
            Assumptions = Section(incoming.Assumptions),
            OpenQuestions = Section(incoming.OpenQuestions),
            Conflicts = conflicts,
        };

        // Una cancellazione è a sua volta una modifica del PM.
        var remaining = Items(content).Select(i => i.Id).Concat(content.Conflicts.Select(c => c.Id)).ToHashSet();
        if (currentItems.Keys.Concat(currentConflicts.Keys).Any(id => !remaining.Contains(id)))
        {
            changed = true;
        }

        if (changed)
        {
            content = content with
            {
                Sources =
                [
                    .. content.Sources,
                    new AnalysisSource { Id = pmSourceId, Kind = AnalysisSourceKind.PmRevision, MessageId = null, MessageRole = null, RevisionId = revisionId },
                ],
            };
        }

        return new(content, violations);
    }

    private static IReadOnlyList<Guid> Cite(IReadOnlyList<Guid> refs, Guid source) => refs.Contains(source) ? refs : [.. refs, source];

    private static bool SameResolution(AnalysisConflictResolution a, AnalysisConflictResolution b) =>
        a.Reason == b.Reason
        && a.SelectedAlternativeIds.SequenceEqual(b.SelectedAlternativeIds)
        && a.EvidenceRefs.SequenceEqual(b.EvidenceRefs);

    private static IEnumerable<AnalysisItem> Items(AnalysisContent c) => c.Goals
        .Concat(c.ContextAndScope.Overview)
        .Concat(c.ContextAndScope.Included)
        .Concat(c.ContextAndScope.Excluded)
        .Concat(c.Requirements)
        .Concat(c.OperationalFlows)
        .Concat(c.Constraints)
        .Concat(c.Dependencies)
        .Concat(c.Assumptions)
        .Concat(c.OpenQuestions)
        .Concat(c.Conflicts.SelectMany(x => x.Alternatives));
}
