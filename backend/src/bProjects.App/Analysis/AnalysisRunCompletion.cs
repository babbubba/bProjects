using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Analysis;

/// <summary>Esecuzione di Analysis presa in carico: owner e fencing token del lease del ProcessingRun.</summary>
public sealed record AnalysisRunClaim(Guid RunId, string LeaseOwner, long FencingToken, int AttemptCount = 0);

public enum AnalysisCommitOutcome
{
    /// <summary>Revisione AI, Completed e CurrentAnalysisId sono stati scritti da questa chiamata.</summary>
    Committed,

    /// <summary>Il run era già Completed (commit precedente riuscito): nulla è stato modificato.</summary>
    AlreadyCompleted,

    /// <summary>Lease/fencing/deadline non più validi: nulla è stato modificato.</summary>
    Rejected,
}

/// <summary>Messaggio originale della conversazione, ammesso dal cutoff.</summary>
public sealed record AnalysisDialogMessage(Guid Id, AnalysisMessageRole Role, long Sequence, string Body);

/// <summary>
/// Input immutabile del run: identità fidate, dialogo fino al cutoff e, una volta congelata, la vista delle evidenze selezionate
/// (contracts/ai-workflow.md §10.6), riletta identica da ogni tentativo.
/// </summary>
public sealed record AnalysisRunInput(
    Guid ProjectId, Guid AnalysisId, long SourceCutoffSequence, IReadOnlyList<AnalysisDialogMessage> Dialog, EvidenceSelectionView? Evidence = null)
{
    public AnalysisValidationContext ToValidationContext() => new(
        ProjectId,
        AnalysisId,
        SourceCutoffSequence,
        [.. Dialog.Select(m => new AnalysisMessageRef(m.Id, ProjectId, m.Role, m.Sequence))],
        new HashSet<Guid>());
}

public sealed record AnalysisCommitResult(AnalysisCommitOutcome Outcome, Guid? RevisionId = null, string? Reason = null);

/// <summary>Chi committa atomicamente il risultato dell'analisi (T029). Deve rifiutare se lease/fencing non sono più validi.</summary>
public interface IAnalysisRunStore
{
    /// <summary>Input fidato del run: la conversazione fino al cutoff immutabile, mai oltre.</summary>
    Task<AnalysisRunInput> LoadInputAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Commit atomico della revisione AI, di Completed e della mappa interna elemento→evidenze (<paramref name="evidenceKeys"/>: id
    /// dell'elemento → alias della vista congelata del run).
    /// </summary>
    Task<AnalysisCommitResult> CommitAsync(
        AnalysisRunClaim claim, AnalysisContent content, IReadOnlyDictionary<Guid, IReadOnlyList<string>> evidenceKeys, CancellationToken ct);

    /// <summary>Commit senza mappa elemento→evidenze (nessun elemento collegato dal modello).</summary>
    Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content, CancellationToken ct) =>
        CommitAsync(claim, content, new Dictionary<Guid, IReadOnlyList<string>>(), ct);

    /// <summary>Congela (al primo tentativo) o rilegge la selezione delle evidenze del run, sotto lease/fencing.</summary>
    Task<SelectionResult> FreezeEvidenceAsync(AnalysisRunClaim claim, CancellationToken ct);

    /// <summary>Failed terminale deciso dall'owner. False se owner/fencing non sono più validi.</summary>
    Task<bool> FailAsync(AnalysisRunClaim claim, string errorCode, string errorMessage, CancellationToken ct);

    /// <summary>Proiezione informativa della fase osservata (mai usata per decidere il passo successivo). False se non più owner.</summary>
    Task<bool> RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct);
}

/// <summary>
/// Scritture del ProcessingRun di Analysis (T029, contracts/ai-workflow.md §8). Il commit è unico e atomico: sotto il
/// lock di riga del run, con owner/fencing/lease/deadline verificati sul tempo del database, inserisce la prima revisione
/// AI Unvalidated, porta il run a Completed con ResultRevisionId e aggiorna Project.CurrentAnalysisId. Un worker vecchio
/// non scrive nulla; un run già Completed è riconciliato senza duplicare la revisione.
/// </summary>
public sealed class AnalysisRunCompletion(AppDbContext db) : IAnalysisRunStore
{
    /// <summary>Fase che esegue il commit (id dell'executor ValidatePersistAnalysis): la proiezione la registra nel commit stesso, senza gara con l'osservatore degli eventi.</summary>
    public const string CommitStage = "ValidatePersistAnalysis";

    public async Task<AnalysisRunInput> LoadInputAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == runId && r.Kind == ProcessingRunKind.Analysis, ct);
        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == run.ConversationId && m.Sequence <= run.SourceCutoffSequence)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

        return new AnalysisRunInput(
            run.ProjectId,
            run.Id,
            run.SourceCutoffSequence!.Value,
            [.. messages.Select(m => new AnalysisDialogMessage(
                m.Id, m.Role == Conversations.MessageRole.User ? AnalysisMessageRole.User : AnalysisMessageRole.Assistant, m.Sequence, m.Body))],
            await new EvidenceLedgerStore(db).LoadSelectionAsync(runId, ct));
    }

    public Task<SelectionResult> FreezeEvidenceAsync(AnalysisRunClaim claim, CancellationToken ct) => new EvidenceLedgerStore(db).FreezeSelectionAsync(claim, ct);

    public Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content, CancellationToken ct) =>
        CommitAsync(claim, content, new Dictionary<Guid, IReadOnlyList<string>>(), ct);

    public async Task<AnalysisCommitResult> CommitAsync(
        AnalysisRunClaim claim, AnalysisContent content, IReadOnlyDictionary<Guid, IReadOnlyList<string>> evidenceKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(content);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var run = (await db.ProcessingRuns
            .FromSql($"SELECT * FROM \"ProcessingRuns\" WHERE \"Id\" = {claim.RunId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct)).SingleOrDefault();
        if (run is null || run.Kind != ProcessingRunKind.Analysis)
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "run_missing");
        }

        // Stato di commit dalla guard dell'autorità temporale (T118): lease, fence e scadenza corrente (DeadlineAt ed EffectiveDeadlineAt) sotto lock di riga.
        var state = await DocumentDeadlineGuard.CommitStateAsync(db, turn: false, claim.RunId, claim.LeaseOwner, claim.FencingToken, ct);

        if (state == RootCommitState.Completed)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return run.ResultRevisionId is { } existing
                ? new(AnalysisCommitOutcome.AlreadyCompleted, existing)
                : new(AnalysisCommitOutcome.Rejected, Reason: "completed_without_result");
        }

        if (state != RootCommitState.Valid)
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "lease_lost");
        }

        if (content.AnalysisId != run.Id || content.ProjectId != run.ProjectId || content.SourceCutoffSequence != run.SourceCutoffSequence)
        {
            // Difesa in profondità: la validazione ha già imposto questi valori dall'input fidato.
            return new(AnalysisCommitOutcome.Rejected, Reason: "content_not_bound_to_run");
        }

        var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(),
            AnalysisId = run.Id,
            Version = 1,
            ParentRevisionId = null,
            Origin = AnalysisRevisionOrigin.Ai,
            CreatedBy = run.RequestedBy,
            CreatedAt = now,
            SchemaVersion = AnalysisRevision.CurrentSchemaVersion,
            ContentJson = AnalysisContentJson.Serialize(content),
            ValidationState = AnalysisValidationState.Unvalidated,
        };
        db.AnalysisRevisions.Add(revision);
        await db.SaveChangesAsync(ct);

        run.Status = ProcessingRunStatus.Completed;
        run.ResultRevisionId = revision.Id;
        run.CompletedAt = now;
        run.UpdatedAt = now;
        run.LastObservedStage = CommitStage;
        run.LeaseOwner = null;
        run.LeaseUntil = null;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        // Mappa interna elemento→evidenze nella stessa transazione: solo alias della selezione congelata del run, solo elementi esistenti.
        if (await new EvidenceLedgerStore(db).LoadSelectionAsync(run.Id, ct) is { } selection)
        {
            var aliases = selection.Evidences.ToDictionary(e => e.Alias, e => e.EvidenceId, StringComparer.Ordinal);
            var items = Evidence.EvidenceCoverage.Items(content).Select(i => i.Item.Id).ToHashSet();
            db.AnalysisEvidenceLinks.AddRange(evidenceKeys
                .Where(k => items.Contains(k.Key))
                .SelectMany(k => k.Value.Where(aliases.ContainsKey).Select(a => (Item: k.Key, Evidence: aliases[a])))
                .Distinct()
                .Select(x => new AnalysisEvidenceLink { RunId = run.Id, ItemId = x.Item, EvidenceId = x.Evidence, ProjectId = run.ProjectId }));
        }

        // Telemetria del tentativo vincente nella stessa transazione del risultato (stesso controllo di lease/fencing).
        var inference = InferenceScope.Current;
        InferenceCallLog.Stage(db, inference, run.ProjectId, new(null, run.Id, claim.AttemptCount, claim.FencingToken), result: true);
        await db.SaveChangesAsync(ct);

        await db.Projects.Where(p => p.Id == run.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrentAnalysisId, run.Id), ct);

        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        db.ChangeTracker.Clear();
        return new(AnalysisCommitOutcome.Committed, revision.Id);
    }

    /// <summary>
    /// Commit atomico del ramo documentale (002 T059, AnalysisContent 1.1.0): stesso lock di riga, guard dell'autorità temporale e telemetria del
    /// ramo 001, ma revisione AI con schema 1.1.0, mappa item→evidenze dall'evidenceMap validata (solo item della revisione ed evidenze della
    /// selezione congelata) e relazioni interpretative del run append-only, tutto nella stessa transazione di Completed e CurrentAnalysisId.
    /// </summary>
    public async Task<AnalysisCommitResult> CommitDocumentAsync(
        AnalysisRunClaim claim, DocumentAnalysisContent content, IReadOnlyList<EvidenceMapEntry> evidenceMap, IReadOnlyList<RunRelation> relations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(evidenceMap);
        ArgumentNullException.ThrowIfNull(relations);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var run = (await db.ProcessingRuns
            .FromSql($"SELECT * FROM \"ProcessingRuns\" WHERE \"Id\" = {claim.RunId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct)).SingleOrDefault();
        if (run is null || run.Kind != ProcessingRunKind.Analysis)
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "run_missing");
        }

        var state = await DocumentDeadlineGuard.CommitStateAsync(db, turn: false, claim.RunId, claim.LeaseOwner, claim.FencingToken, ct);
        if (state == RootCommitState.Completed)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return run.ResultRevisionId is { } existing
                ? new(AnalysisCommitOutcome.AlreadyCompleted, existing)
                : new(AnalysisCommitOutcome.Rejected, Reason: "completed_without_result");
        }

        if (state != RootCommitState.Valid)
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "lease_lost");
        }

        if (content.AnalysisId != run.Id || content.ProjectId != run.ProjectId || content.SourceCutoffSequence != run.SourceCutoffSequence)
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "content_not_bound_to_run");
        }

        var selected = await db.AnalysisEvidenceSelectionItems.AsNoTracking().Where(i => i.RunId == run.Id).Select(i => i.VersionId).ToListAsync(ct);
        var inSelection = (await db.Evidences.AsNoTracking().Where(e => selected.Contains(e.VersionId)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var items = ReconciliationContractRules.ItemIds(content).ToHashSet();
        if (evidenceMap.Any(m => !items.Contains(m.ItemId) || m.EvidenceIds.Any(e => !inSelection.Contains(e)))
            || relations.Any(r => !inSelection.Contains(r.From) || !inSelection.Contains(r.To)))
        {
            return new(AnalysisCommitOutcome.Rejected, Reason: "evidence_outside_frozen_selection");
        }

        var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
        var revision = new AnalysisRevision
        {
            Id = Guid.NewGuid(),
            AnalysisId = run.Id,
            Version = 1,
            ParentRevisionId = null,
            Origin = AnalysisRevisionOrigin.Ai,
            CreatedBy = run.RequestedBy,
            CreatedAt = now,
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue,
            ContentJson = DocumentAnalysisContentJson.Serialize(content),
            ValidationState = AnalysisValidationState.Unvalidated,
        };
        db.AnalysisRevisions.Add(revision);
        await db.SaveChangesAsync(ct);

        run.Status = ProcessingRunStatus.Completed;
        run.ResultRevisionId = revision.Id;
        run.CompletedAt = now;
        run.UpdatedAt = now;
        run.LastObservedStage = DocumentBatchWorkflow.CommitId;
        run.LeaseOwner = null;
        run.LeaseUntil = null;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        db.AnalysisEvidenceLinks.AddRange(evidenceMap
            .SelectMany(m => m.EvidenceIds.Select(e => (Item: m.ItemId, Evidence: e)))
            .Distinct()
            .Select(x => new AnalysisEvidenceLink { RunId = run.Id, ItemId = x.Item, EvidenceId = x.Evidence, ProjectId = run.ProjectId }));
        new EvidenceLedgerStore(db).AppendRunRelations(run.Id, run.ProjectId, relations);

        var inference = InferenceScope.Current;
        InferenceCallLog.Stage(db, inference, run.ProjectId, new(null, run.Id, claim.AttemptCount, claim.FencingToken), result: true);
        await db.SaveChangesAsync(ct);

        await db.Projects.Where(p => p.Id == run.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrentAnalysisId, run.Id), ct);

        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        db.ChangeTracker.Clear();
        return new(AnalysisCommitOutcome.Committed, revision.Id);
    }

    /// <summary>Failed deciso dall'owner; le chiamate del tentativo (es. output rifiutato dal validatore) si scrivono nella stessa transazione.</summary>
    public async Task<bool> FailAsync(AnalysisRunClaim claim, string errorCode, string errorMessage, CancellationToken ct)
    {
        var inference = InferenceScope.Current;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ProcessingRuns" SET "Status" = 'Failed', "ErrorCode" = {errorCode}, "ErrorMessage" = {Truncate(errorMessage)},
                "CompletedAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = {claim.RunId} AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            """, ct) == 1;
        if (!failed)
        {
            return false;
        }

        await InferenceCallLog.AppendAttemptAsync(db, inference, new(null, claim.RunId, claim.AttemptCount, claim.FencingToken), ct);
        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        return true;
    }

    public async Task<bool> RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ProcessingRuns" SET "LastObservedStage" = {stage}, "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {claim.RunId} AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            """, ct) == 1;

    private static string Truncate(string message) => message.Length <= 1_000 ? message : message[..1_000];
}
