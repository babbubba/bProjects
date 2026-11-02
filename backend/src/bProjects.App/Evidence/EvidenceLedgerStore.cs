using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Evidence;

/// <summary>Lavoro di estrazione preso in carico: owner e fencing token identificano in modo esclusivo questo tentativo.</summary>
public sealed record ExtractionJobClaim(Guid JobId, string LeaseOwner, long FencingToken, int AttemptCount = 0);

public enum ExtractionCommitOutcome
{
    Committed,
    AlreadyCompleted,
    Rejected,
}

public sealed record ExtractionCommitResult(ExtractionCommitOutcome Outcome, Guid? VersionId = null, string? Reason = null);

public enum PromotionOutcome
{
    Promoted,
    AlreadyCurrent,
    Conflict,
    NotFound,
    VersionNotOfMessage,
}

public sealed record PromotionResult(PromotionOutcome Outcome, EvidenceMessageState? State = null, EvidencePromotion? Promotion = null);

public enum SelectionOutcome
{
    /// <summary>Selezione scritta ora (primo tentativo del run).</summary>
    Frozen,

    /// <summary>Selezione già congelata da un tentativo precedente: riletta, mai ricalcolata.</summary>
    Existing,

    /// <summary>Messaggi PM entro il cutoff senza versione corrente: il run deve fallire con evidence_unavailable.</summary>
    Missing,

    /// <summary>Lease/fencing non più validi: nulla è stato scritto.</summary>
    Rejected,
}

public sealed record SelectionResult(SelectionOutcome Outcome, EvidenceSelectionView? View = null, IReadOnlyList<long>? MissingSequences = null);

/// <summary>Relazione interpretativa del run da persistere (append-only) insieme alla revisione.</summary>
public sealed record RunRelation(Guid From, Guid To, EvidenceRelationType Type, EvidenceRelationBasis Basis, string? Rationale);

/// <summary>
/// Scritture e letture del registro delle evidenze (data-model.md §Registro, contracts/ai-workflow.md §10.5–10.6). Ogni scrittura di un
/// lavoro è protetta da owner + FencingToken + lease + deadline sul tempo del database, sotto lock di riga; la numerazione delle versioni
/// e lo stato corrente di un messaggio si scrivono sotto il lock della riga del messaggio (originale append-only, mai aggiornato).
/// </summary>
public sealed class EvidenceLedgerStore(AppDbContext db)
{
    public const string EvidenceUnavailable = "evidence_unavailable";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Configurazione dell'estrattore congelata alla richiesta del lavoro: soltanto parametri, nessun endpoint né credenziale.</summary>
    public static string ExtractorConfig(AiOptions ai, ExecutionOptions policy)
    {
        var settings = ai.ForPhase(AiPhase.Extraction);
        return JsonSerializer.Serialize(new
        {
            modelId = settings.ModelId,
            phase = InferenceMetrics.PhaseName(AiPhase.Extraction),
            promptVersion = EvidenceExtractor.PromptVersion,
            schemaVersion = EvidenceExtractionVersion.CurrentSchemaVersion,
            structuredOutputMode = ai.StructuredOutputMode.ToString(),
            contextWindowTokens = ai.ContextWindowTokens,
            maxOutputTokens = settings.MaxOutputTokens,
            inputBudgetTokens = settings.InputBudgetTokens,
            enableThinking = settings.EnableThinking,
            temperature = 0,
            requestTimeoutSeconds = settings.RequestTimeoutSeconds,
            leaseSeconds = policy.LeaseDurationSeconds,
            maxAttempts = policy.MaxAttempts,
            deadlineSeconds = policy.ExtractionDeadlineSeconds,
        }, Json);
    }

    public static EvidenceExtractionJob NewJob(
        Guid projectId, Guid conversationId, Guid messageId, long sequence, ExtractionJobReason reason, DateTimeOffset now,
        ExecutionOptions policy, string config, Guid? requestedBy = null, Guid? batchId = null, Guid? retryOf = null, Guid? requestKey = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        ConversationId = conversationId,
        MessageId = messageId,
        MessageSequence = sequence,
        Reason = reason,
        BatchId = batchId,
        RetryOfJobId = retryOf,
        RequestKey = requestKey,
        Status = ExtractionJobStatus.Queued,
        RequestedBy = requestedBy,
        RequestedAt = now,
        DeadlineAt = now + policy.ExtractionDeadline,
        MaxAttempts = policy.MaxAttempts,
        ExtractorConfig = config,
    };

    /// <summary>
    /// Accoda (senza SaveChanges) un lavoro per ogni messaggio PM fino a <paramref name="throughSequence"/> che non ne ha uno aperto; con
    /// <paramref name="onlyMissing"/> salta anche i messaggi che hanno già una versione corrente. Il chiamante tiene il lock della
    /// conversazione, come per il salvataggio dei messaggi: niente gare sull'indice «un lavoro aperto per messaggio».
    /// </summary>
    public async Task<(IReadOnlyList<EvidenceExtractionJob> Created, int Skipped)> EnqueueAsync(
        Guid projectId, Guid conversationId, long throughSequence, bool onlyMissing, ExtractionJobReason reason, Guid? requestedBy,
        Guid? batchId, DateTimeOffset now, ExecutionOptions policy, string config, CancellationToken ct)
    {
        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.User && m.Sequence <= throughSequence)
            .OrderBy(m => m.Sequence)
            .Select(m => new { m.Id, m.Sequence })
            .ToListAsync(ct);
        var ids = messages.Select(m => m.Id).ToList();
        var open = (await db.EvidenceExtractionJobs.AsNoTracking()
            .Where(j => ids.Contains(j.MessageId!.Value) && (j.Status == ExtractionJobStatus.Queued || j.Status == ExtractionJobStatus.Running))
            .Select(j => j.MessageId!.Value).ToListAsync(ct)).ToHashSet();
        var current = onlyMissing
            ? (await db.EvidenceMessageStates.AsNoTracking().Where(s => ids.Contains(s.MessageId)).Select(s => s.MessageId).ToListAsync(ct)).ToHashSet()
            : [];

        var created = new List<EvidenceExtractionJob>();
        var skipped = 0;
        foreach (var m in messages)
        {
            if (open.Contains(m.Id) || current.Contains(m.Id))
            {
                skipped++;
                continue;
            }

            created.Add(NewJob(projectId, conversationId, m.Id, m.Sequence, reason, now, policy, config, requestedBy, batchId));
        }

        db.EvidenceExtractionJobs.AddRange(created);
        return (created, skipped);
    }

    /// <summary>
    /// Input fidato dell'estrazione: il messaggio PM, gli originali PM precedenti, l'ultimo messaggio dell'assistente precedente (solo
    /// referente) e le evidenze correnti dei messaggi precedenti. Nulla con sequenza maggiore del messaggio estratto.
    /// </summary>
    public async Task<ExtractionContext> LoadContextAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.EvidenceExtractionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
        var messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == job.ConversationId && m.Sequence <= job.MessageSequence)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);
        var target = messages.Single(m => m.Id == job.MessageId && m.Role == MessageRole.User);
        var priorUsers = messages.Where(m => m.Role == MessageRole.User && m.Sequence < target.Sequence).ToList();
        var referent = messages.LastOrDefault(m => m.Role == MessageRole.Assistant && m.Sequence < target.Sequence);

        var priorIds = priorUsers.Select(m => m.Id).ToList();
        var currentVersions = await db.EvidenceMessageStates.AsNoTracking()
            .Where(s => priorIds.Contains(s.MessageId))
            .Select(s => s.CurrentVersionId)
            .ToListAsync(ct);
        var sequenceOf = priorUsers.ToDictionary(m => m.Id, m => m.Sequence);
        var priorEvidence = (await db.Evidences.AsNoTracking().Where(e => currentVersions.Contains(e.VersionId)).ToListAsync(ct))
            .OrderBy(e => sequenceOf[e.MessageId!.Value]).ThenBy(e => e.Ordinal)
            .Select((e, i) => new PriorEvidence($"P{i + 1}", e.Id, e.MessageId!.Value, sequenceOf[e.MessageId!.Value], e.Kind, e.Polarity, e.Statement))
            .ToList();

        return new ExtractionContext(
            job.ProjectId,
            job.Id,
            ToMessage(target),
            [.. priorUsers.Select(ToMessage)],
            referent is null ? null : ToMessage(referent),
            priorEvidence);
    }

    private static ExtractionMessage ToMessage(ConversationMessage m) => new(m.Id, m.Sequence, m.Role, m.Body);

    /// <summary>
    /// Commit unico e atomico dell'estrazione valida: versione append-only con numero successivo (sotto lock del messaggio), evidenze,
    /// relazioni, lavoro Completed con ResultVersionId e telemetria del tentativo; se il messaggio non ha ancora una versione corrente,
    /// stato e promozione automatica tracciata. Un worker con lease/fencing/deadline non più validi non scrive nulla.
    /// </summary>
    public async Task<ExtractionCommitResult> CommitAsync(
        ExtractionJobClaim claim, ExtractionContext context, ExtractionValidationResult validation, string modelId, string parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.Accepted)
        {
            throw new ArgumentException("Solo un'estrazione validata può essere committata.", nameof(validation));
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = (await db.EvidenceExtractionJobs
            .FromSql($"SELECT * FROM \"EvidenceExtractionJobs\" WHERE \"Id\" = {claim.JobId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(ct)).SingleOrDefault();
        if (job is null || job.OriginKind != EvidenceOriginKind.Message || job.MessageId != context.Target.Id)
        {
            return new(ExtractionCommitOutcome.Rejected, Reason: "job_missing");
        }

        var state = (await db.Database.SqlQuery<string>($"""
            SELECT CASE
                WHEN "Status" = 'Completed' THEN 'completed'
                WHEN "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
                     AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp() THEN 'valid'
                ELSE 'stale' END AS "Value"
            FROM "EvidenceExtractionJobs" WHERE "Id" = {claim.JobId}
            """).ToListAsync(ct)).Single();
        if (state == "completed")
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ExtractionCommitOutcome.AlreadyCompleted, job.ResultVersionId);
        }

        if (state != "valid")
        {
            return new(ExtractionCommitOutcome.Rejected, Reason: "lease_lost");
        }

        await LockMessageAsync(job.MessageId!.Value, ct);
        var now = await NowAsync(ct);
        var number = (await db.EvidenceExtractionVersions.Where(v => v.MessageId == job.MessageId).MaxAsync(v => (int?)v.Version, ct) ?? 0) + 1;
        var version = new EvidenceExtractionVersion
        {
            Id = Guid.NewGuid(),
            ProjectId = job.ProjectId,
            ConversationId = job.ConversationId,
            MessageId = job.MessageId,
            MessageSequence = job.MessageSequence,
            Version = number,
            JobId = job.Id,
            SchemaVersion = EvidenceExtractionVersion.CurrentSchemaVersion,
            PromptVersion = EvidenceExtractor.PromptVersion,
            ModelId = modelId.Length <= 256 ? modelId : modelId[..256],
            Parameters = parameters,
            EvidenceCount = validation.Evidences.Count,
            NoEvidenceReason = validation.Evidences.Count == 0 ? validation.NoEvidenceReason : null,
            CreatedAt = now,
        };
        db.EvidenceExtractionVersions.Add(version);

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var e in validation.Evidences)
        {
            var id = Guid.NewGuid();
            ids[e.Key] = id;
            db.Evidences.Add(new EvidenceItem
            {
                Id = id,
                VersionId = version.Id,
                ProjectId = job.ProjectId,
                MessageId = job.MessageId,
                Ordinal = e.Ordinal,
                Key = e.Key,
                Kind = e.Kind,
                Polarity = e.Polarity,
                Certainty = e.Certainty,
                Statement = e.Statement,
                Content = EvidenceJson.Serialize(e.Content),
            });
        }

        foreach (var r in validation.Relations)
        {
            db.EvidenceRelations.Add(new EvidenceRelation
            {
                Id = Guid.NewGuid(),
                ProjectId = job.ProjectId,
                VersionId = version.Id,
                FromEvidenceId = ids[r.FromKey],
                ToEvidenceId = r.ToKey is { } key ? ids[key] : r.ToPrior!.EvidenceId,
                Type = r.Type,
                Basis = r.Basis,
                Rationale = r.Rationale is { Length: > 2_000 } text ? text[..2_000] : r.Rationale,
            });
        }

        // Prima la versione (FK del risultato), poi il lavoro Completed che la referenzia.
        await db.SaveChangesAsync(ct);
        job.Status = ExtractionJobStatus.Completed;
        job.ResultVersionId = version.Id;
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.ErrorCode = null;
        job.ErrorMessage = null;

        if (!await db.EvidenceMessageStates.AnyAsync(s => s.MessageId == job.MessageId, ct))
        {
            db.EvidenceMessageStates.Add(new EvidenceMessageState
            {
                MessageId = job.MessageId!.Value,
                ProjectId = job.ProjectId,
                CurrentVersionId = version.Id,
                Revision = 1,
                UpdatedAt = now,
            });
            db.EvidencePromotions.Add(new EvidencePromotion
            {
                Id = Guid.NewGuid(),
                ProjectId = job.ProjectId,
                MessageId = job.MessageId!.Value,
                FromVersionId = null,
                ToVersionId = version.Id,
                PromotedBy = null,
                PromotedAt = now,
                Reason = EvidencePromotion.FirstValidVersionReason,
                RelationImpact = EmptyImpact,
            });
        }

        var inference = InferenceScope.Current;
        InferenceCallLog.Stage(db, inference, job.ProjectId, Owner(claim), result: true);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        db.ChangeTracker.Clear();
        return new(ExtractionCommitOutcome.Committed, version.Id);
    }

    // ------------------------------------------------------------------ aggregazione documentale (002 T051)

    /// <summary>
    /// Ricava, in sola lettura, la versione Evidence 1.1.0 di una Preparation dagli output immutabili delle sue unità: copertura dei range,
    /// integrità di hash/manifesto/ordinali, fonti risolte sul testo canonico. <see cref="DocumentAggregationException"/> se non è aggregabile.
    /// Il commit (autorità, numerazione, Completed, Ready, bundle corrente) è di <c>DocumentBundleStore</c>, che lo chiama sotto i propri lock.
    /// </summary>
    public Task<DocumentAggregate> LoadDocumentAggregateAsync(Guid preparationId, string? configuredModelId, CancellationToken ct) =>
        DocumentEvidenceAggregator.LoadAsync(db, preparationId, configuredModelId, ct);

    /// <summary>
    /// Accoda (senza SaveChanges) versione, evidenze e relazioni dell'aggregazione documentale. La numerazione è per Preparation ed è calcolata qui:
    /// il chiamante tiene il lock della Preparation, quindi due pubblicazioni non assegnano lo stesso numero (e Unique(JobId) rifiuta comunque la seconda).
    /// Gli ID delle evidenze sono derivati dalla chiave globale, quindi stabili nel replay; le righe sono append-only e nessuna versione precedente è toccata.
    /// </summary>
    public async Task<EvidenceExtractionVersion> StageDocumentVersionAsync(
        EvidenceExtractionJob job, DocumentAggregate aggregate, DateTimeOffset now, CancellationToken ct)
    {
        if (job.OriginKind != EvidenceOriginKind.Document || job.PreparationId != aggregate.PreparationId)
        {
            throw new ArgumentException("L'aggregatore non appartiene alla preparazione aggregata.", nameof(job));
        }

        var number = (await db.EvidenceExtractionVersions.AsNoTracking().Where(v => v.PreparationId == job.PreparationId).MaxAsync(v => (int?)v.Version, ct) ?? 0) + 1;
        var version = new EvidenceExtractionVersion
        {
            Id = Guid.NewGuid(),
            ProjectId = job.ProjectId,
            OriginKind = EvidenceOriginKind.Document,
            PreparationId = job.PreparationId,
            ManifestHash = aggregate.ManifestHash,
            Version = number,
            JobId = job.Id,
            SchemaVersion = EvidenceExtractionVersion.DocumentSchemaVersion,
            PromptVersion = DocumentEvidenceAgent.PromptVersion,
            ModelId = aggregate.ModelId,
            Parameters = aggregate.ParametersJson,
            EvidenceCount = aggregate.Evidences.Count,
            NoEvidenceReason = aggregate.Evidences.Count == 0 ? aggregate.NoEvidenceReason : null,
            CreatedAt = now,
        };
        db.EvidenceExtractionVersions.Add(version);
        foreach (var e in aggregate.Evidences)
        {
            db.Evidences.Add(new EvidenceItem
            {
                Id = e.Id,
                VersionId = version.Id,
                ProjectId = job.ProjectId,
                OriginKind = EvidenceOriginKind.Document,
                PreparationId = job.PreparationId,
                Ordinal = e.Ordinal,
                Key = e.Key,
                Kind = e.Kind,
                Polarity = e.Polarity,
                Certainty = e.Certainty,
                Statement = e.Statement,
                Content = DocumentEvidenceCodec.EncodeContent(e.Content),
            });
        }

        foreach (var r in aggregate.Relations)
        {
            db.EvidenceRelations.Add(new EvidenceRelation
            {
                Id = r.Id,
                ProjectId = job.ProjectId,
                VersionId = version.Id,
                FromEvidenceId = r.FromEvidenceId,
                ToEvidenceId = r.ToEvidenceId,
                Type = r.Type,
                Basis = r.Basis,
                Rationale = r.Rationale,
            });
        }

        return version;
    }

    public static InferenceOwner Owner(ExtractionJobClaim claim) => new(null, null, claim.AttemptCount, claim.FencingToken, claim.JobId);

    /// <summary>Failed deciso dall'owner (es. output non valido); le chiamate del tentativo si scrivono nella stessa transazione.</summary>
    public async Task<bool> FailAsync(ExtractionJobClaim claim, string errorCode, string errorMessage, CancellationToken ct)
    {
        var inference = InferenceScope.Current;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "EvidenceExtractionJobs" SET "Status" = 'Failed', "ErrorCode" = {errorCode}, "ErrorMessage" = {Truncate(errorMessage)},
                "CompletedAt" = clock_timestamp(), "LeaseOwner" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = {claim.JobId} AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner} AND "FencingToken" = {claim.FencingToken}
            """, ct) == 1;
        if (!failed)
        {
            return false;
        }

        await InferenceCallLog.AppendAttemptAsync(db, inference, Owner(claim), ct);
        await transaction.CommitAsync(ct);
        inference?.MarkWritten();
        return true;
    }

    private const string EmptyImpact = """{"remapped":0,"stale":0,"relations":[]}""";

    /// <summary>
    /// Promozione esplicita di una versione a corrente del messaggio, con concorrenza ottimistica sulla revisione dello stato. Atomica e
    /// tracciata con l'impatto sulle relazioni che altri messaggi (versioni correnti) puntano verso la versione uscente: rimappate se il
    /// confronto le abbina in modo univoco, altrimenti stale. Non riscrive nessuna relazione né artefatto storico.
    /// </summary>
    public async Task<PromotionResult> PromoteAsync(
        Guid projectId, Guid messageId, Guid versionId, int expectedRevision, Guid promotedBy, string? reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owned = await (from m in db.ConversationMessages
                           join c in db.Conversations on m.ConversationId equals c.Id
                           where m.Id == messageId && c.ProjectId == projectId && m.Role == MessageRole.User
                           select m.Id).AnyAsync(ct);
        if (!owned)
        {
            return new(PromotionOutcome.NotFound);
        }

        await LockMessageAsync(messageId, ct);
        var version = await db.EvidenceExtractionVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.ProjectId == projectId, ct);
        if (version is null)
        {
            return new(PromotionOutcome.NotFound);
        }

        if (version.MessageId != messageId)
        {
            return new(PromotionOutcome.VersionNotOfMessage);
        }

        var state = await db.EvidenceMessageStates.AsTracking().SingleOrDefaultAsync(s => s.MessageId == messageId, ct);
        if (state is null || state.Revision != expectedRevision)
        {
            return new(PromotionOutcome.Conflict, state);
        }

        if (state.CurrentVersionId == versionId)
        {
            return new(PromotionOutcome.AlreadyCurrent, state);
        }

        var now = await NowAsync(ct);
        var impact = await RelationImpactAsync(projectId, state.CurrentVersionId, versionId, ct);
        var promotion = new EvidencePromotion
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            MessageId = messageId,
            FromVersionId = state.CurrentVersionId,
            ToVersionId = versionId,
            PromotedBy = promotedBy,
            PromotedAt = now,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() is { Length: > 1_000 } r ? r[..1_000] : reason.Trim(),
            RelationImpact = impact,
        };
        state.CurrentVersionId = versionId;
        state.Revision += 1;
        state.UpdatedAt = now;
        db.EvidencePromotions.Add(promotion);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(PromotionOutcome.Promoted, state, promotion);
    }

    private async Task<string> RelationImpactAsync(Guid projectId, Guid fromVersionId, Guid toVersionId, CancellationToken ct)
    {
        var old = await SnapshotsAsync([fromVersionId], ct);
        var oldIds = old.Select(e => e.Id).ToList();
        var currentVersions = await db.EvidenceMessageStates.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.CurrentVersionId).ToListAsync(ct);
        var incoming = await db.EvidenceRelations.AsNoTracking()
            .Where(r => r.ProjectId == projectId && oldIds.Contains(r.ToEvidenceId) && currentVersions.Contains(r.VersionId) && r.VersionId != fromVersionId)
            .ToListAsync(ct);
        if (incoming.Count == 0)
        {
            return EmptyImpact;
        }

        var map = EvidenceVersionComparer.Match(old, await SnapshotsAsync([toVersionId], ct));
        var relations = incoming.Select(r => new
        {
            relationId = r.Id,
            type = r.Type.ToString(),
            fromEvidenceId = r.FromEvidenceId,
            toEvidenceId = r.ToEvidenceId,
            remappedTo = map.TryGetValue(r.ToEvidenceId, out var to) ? to : (Guid?)null,
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            remapped = relations.Count(r => r.remappedTo is not null),
            stale = relations.Count(r => r.remappedTo is null),
            relations,
        }, Json);
    }

    /// <summary>
    /// Congela la selezione del run al primo tentativo (contracts/ai-workflow.md §10.6): le versioni correnti dei messaggi PM entro il
    /// cutoff e la vista deterministica, scritte una sola volta sotto lease/fencing del run. I tentativi successivi la rileggono anche se
    /// nel frattempo una rianalisi o una promozione hanno cambiato il registro corrente.
    /// </summary>
    public async Task<SelectionResult> FreezeSelectionAsync(AnalysisRunClaim claim, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (await LoadSelectionAsync(claim.RunId, ct) is { } existing)
        {
            return new(SelectionOutcome.Existing, existing);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var valid = (await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "ProcessingRuns"
            WHERE "Id" = {claim.RunId} AND "Kind" = 'Analysis' AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner}
              AND "FencingToken" = {claim.FencingToken} AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp()
            FOR UPDATE
            """).ToListAsync(ct)).Count == 1;
        if (!valid)
        {
            return new(SelectionOutcome.Rejected);
        }

        if (await LoadSelectionAsync(claim.RunId, ct) is { } raced)
        {
            return new(SelectionOutcome.Existing, raced);
        }

        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId, ct);
        var users = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == run.ConversationId && m.Role == MessageRole.User && m.Sequence <= run.SourceCutoffSequence)
            .OrderBy(m => m.Sequence)
            .Select(m => new { m.Id, m.Sequence })
            .ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var states = await db.EvidenceMessageStates.AsNoTracking().Where(s => ids.Contains(s.MessageId)).ToDictionaryAsync(s => s.MessageId, ct);
        var missing = users.Where(u => !states.ContainsKey(u.Id)).Select(u => u.Sequence).ToList();
        if (missing.Count > 0)
        {
            return new(SelectionOutcome.Missing, MissingSequences: missing);
        }

        var versionIds = states.Values.Select(s => s.CurrentVersionId).ToList();
        var versions = await db.EvidenceExtractionVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.MessageId!.Value, ct);
        var items = users.Select(u => versions[u.Id]).Select(v => new SelectionItem(v.MessageId!.Value, v.MessageSequence!.Value, v.Id, v.Version, v.EvidenceCount, v.NoEvidenceReason)).ToList();
        var view = await BuildViewAsync(items, ct);

        var now = await NowAsync(ct);
        db.AnalysisEvidenceSelections.Add(new AnalysisEvidenceSelection
        {
            RunId = run.Id,
            ProjectId = run.ProjectId,
            CreatedAt = now,
            ViewHash = view.Hash(),
            View = view.Serialize(),
        });
        await db.SaveChangesAsync(ct);
        db.AnalysisEvidenceSelectionItems.AddRange(items.Select(i => new AnalysisEvidenceSelectionItem
        {
            RunId = run.Id,
            MessageId = i.MessageId,
            MessageSequence = i.Sequence,
            VersionId = i.VersionId,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(SelectionOutcome.Frozen, view);
    }

    /// <summary>
    /// Congela la selezione del run documentale (002 T059): come <see cref="FreezeSelectionAsync"/> per le versioni correnti dei messaggi PM entro il
    /// cutoff (vista 001 immutata), e in più le versioni Evidence dei documenti Ready inclusi nello snapshot di fonti del run, scritte come
    /// righe di selezione Document (preparazione e versione) nella stessa transazione e una sola volta, sotto lease/fencing del run. Con cutoff 0 non ci
    /// sono messaggi PM e la selezione contiene i soli documenti. I tentativi successivi e il resume la rileggono identica.
    /// </summary>
    public async Task<SelectionResult> FreezeDocumentSelectionAsync(AnalysisRunClaim claim, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (await LoadSelectionAsync(claim.RunId, ct) is { } existing)
        {
            return new(SelectionOutcome.Existing, existing);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var valid = (await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "ProcessingRuns"
            WHERE "Id" = {claim.RunId} AND "Kind" = 'Analysis' AND "Status" = 'Running' AND "LeaseOwner" = {claim.LeaseOwner}
              AND "FencingToken" = {claim.FencingToken} AND "LeaseUntil" > clock_timestamp() AND "DeadlineAt" > clock_timestamp()
            FOR UPDATE
            """).ToListAsync(ct)).Count == 1;
        if (!valid)
        {
            return new(SelectionOutcome.Rejected);
        }

        if (await LoadSelectionAsync(claim.RunId, ct) is { } raced)
        {
            return new(SelectionOutcome.Existing, raced);
        }

        var run = await db.ProcessingRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId, ct);
        var users = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == run.ConversationId && m.Role == MessageRole.User && m.Sequence <= run.SourceCutoffSequence)
            .OrderBy(m => m.Sequence)
            .Select(m => new { m.Id, m.Sequence })
            .ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var states = await db.EvidenceMessageStates.AsNoTracking().Where(s => ids.Contains(s.MessageId)).ToDictionaryAsync(s => s.MessageId, ct);
        var missing = users.Where(u => !states.ContainsKey(u.Id)).Select(u => u.Sequence).ToList();
        if (missing.Count > 0)
        {
            return new(SelectionOutcome.Missing, MissingSequences: missing);
        }

        var versionIds = states.Values.Select(s => s.CurrentVersionId).ToList();
        var versions = await db.EvidenceExtractionVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.MessageId!.Value, ct);
        var items = users.Select(u => versions[u.Id]).Select(v => new SelectionItem(v.MessageId!.Value, v.MessageSequence!.Value, v.Id, v.Version, v.EvidenceCount, v.NoEvidenceReason)).ToList();
        var view = await BuildViewAsync(items, ct);

        var documents = run.SourceSnapshotId is { } snapshotId
            ? await db.SourceSnapshotItems.AsNoTracking()
                .Where(i => i.SnapshotId == snapshotId && i.Included && i.Availability == SourceAvailability.Ready && i.EvidenceVersionId != null && i.PreparationId != null)
                .OrderBy(i => i.DocumentId).ToListAsync(ct)
            : [];

        var now = await NowAsync(ct);
        db.AnalysisEvidenceSelections.Add(new AnalysisEvidenceSelection
        {
            RunId = run.Id,
            ProjectId = run.ProjectId,
            CreatedAt = now,
            ViewHash = view.Hash(),
            View = view.Serialize(),
        });
        await db.SaveChangesAsync(ct);
        db.AnalysisEvidenceSelectionItems.AddRange(items.Select(i => new AnalysisEvidenceSelectionItem
        {
            RunId = run.Id,
            OriginKind = EvidenceOriginKind.Message,
            MessageId = i.MessageId,
            MessageSequence = i.Sequence,
            VersionId = i.VersionId,
        }));
        db.AnalysisEvidenceSelectionItems.AddRange(documents.Select(d => new AnalysisEvidenceSelectionItem
        {
            RunId = run.Id,
            OriginKind = EvidenceOriginKind.Document,
            PreparationId = d.PreparationId,
            VersionId = d.EvidenceVersionId!.Value,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(SelectionOutcome.Frozen, view);
    }

    /// <summary>Accoda (nel contesto corrente, senza salvare) le relazioni interpretative del run: append-only, deduplicate per (da, a, tipo).</summary>
    public void AppendRunRelations(Guid runId, Guid projectId, IEnumerable<RunRelation> relations) =>
        db.AnalysisEvidenceRelations.AddRange(relations
            .GroupBy(r => (r.From, r.To, r.Type))
            .Select(g => g.First())
            .Select(r => new AnalysisEvidenceRelation
            {
                RunId = runId, FromEvidenceId = r.From, ToEvidenceId = r.To, Type = r.Type, Basis = r.Basis, Rationale = r.Rationale, ProjectId = projectId,
            }));

    /// <summary>Vista deterministica per le versioni date: evidenze, relazioni asserite e versioni dei bersagli non selezionati.</summary>
    public async Task<EvidenceSelectionView> BuildViewAsync(IReadOnlyList<SelectionItem> items, CancellationToken ct)
    {
        var versionIds = items.Select(i => i.VersionId).ToList();
        var selected = await SnapshotsAsync(versionIds, ct);
        var relations = await RelationsAsync(versionIds, ct);
        var others = relations.Select(r => r.ToVersionId).Where(v => !versionIds.Contains(v)).Distinct().ToList();
        var otherSnapshots = (await SnapshotsAsync(others, ct)).GroupBy(e => e.VersionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EvidenceSnapshot>)[.. g]);
        return EvidenceSelectionView.Build(items, selected, relations, otherSnapshots);
    }

    public async Task<EvidenceSelectionView?> LoadSelectionAsync(Guid runId, CancellationToken ct)
    {
        var json = await db.AnalysisEvidenceSelections.AsNoTracking().Where(s => s.RunId == runId).Select(s => s.View).SingleOrDefaultAsync(ct);
        return json is null ? null : EvidenceSelectionView.Deserialize(json);
    }

    public async Task<IReadOnlyList<EvidenceSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken ct)
    {
        if (versionIds.Count == 0)
        {
            return [];
        }

        var rows = await (from e in db.Evidences.AsNoTracking()
                          join v in db.EvidenceExtractionVersions.AsNoTracking() on e.VersionId equals v.Id
                          where versionIds.Contains(e.VersionId)
                          select new { e, v.MessageSequence }).ToListAsync(ct);
        return [.. rows.Select(r => EvidenceSnapshot.From(r.e, r.MessageSequence!.Value)).OrderBy(e => e.MessageSequence).ThenBy(e => e.Ordinal)];
    }

    /// <summary>Relazioni asserite dalle versioni date, con versione e messaggio del bersaglio.</summary>
    public async Task<IReadOnlyList<RelationRecord>> RelationsAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken ct)
    {
        if (versionIds.Count == 0)
        {
            return [];
        }

        return await (from r in db.EvidenceRelations.AsNoTracking()
                      join t in db.Evidences.AsNoTracking() on r.ToEvidenceId equals t.Id
                      where versionIds.Contains(r.VersionId)
                      orderby r.Id
                      select new RelationRecord(r.Id, r.VersionId, r.FromEvidenceId, r.ToEvidenceId, t.VersionId, t.MessageId!.Value, r.Type, r.Basis, r.Rationale))
            .ToListAsync(ct);
    }

    /// <summary>Lock della riga del messaggio originale (mai aggiornata): serializza numerazione delle versioni e stato corrente.</summary>
    private async Task LockMessageAsync(Guid messageId, CancellationToken ct) =>
        _ = await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"ConversationMessages\" WHERE \"Id\" = {messageId} FOR UPDATE").ToListAsync(ct);

    private async Task<DateTimeOffset> NowAsync(CancellationToken ct) =>
        (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();

    private static string Truncate(string message) => message.Length <= 1_000 ? message : message[..1_000];
}
