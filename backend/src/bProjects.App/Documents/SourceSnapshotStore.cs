using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

/// <summary>
/// Snapshot di selezione fissati al comando (002 T057; data-model «SourceSnapshot / Item», contracts/http-api.md §Selezione). Unico punto che
/// scrive snapshot: chat e Analizza ne creano uno sotto il lock di progetto, la generazione ne eredita uno dalla revisione, mai quello corrente.
/// Definizioni e versioni di prompt/schema documentali sono persistite sul root alla richiesta: le NUOVE richieste di Analizza (T141, AI §13.1/§13.6: v5, single-call con valutazione dei Gap più gerarchia v4 e policy Gap temporanea) e di
/// Genera (T127, AI §7.11) e di Chat documentale (T128, AI §8) congelano la v3 con alias; un run già congelato con la v2, v3 o v4 di Analizza (storiche) resta tale e il suo instradamento è del dispatch (T123). Nessuna conversione di run.
/// </summary>
public sealed class SourceSnapshotStore(AppDbContext db)
{
    /// <summary>Definizione congelata dalle NUOVE richieste di Analizza (D1, AI §13.7): v6, con lo stadio Gap dedicato oltre una chiamata. Le costanti `AnalysisDefinition` e `AnalysisPromptVersions()` della v5 restano come storico (T141) e come fixture dei test.</summary>
    public const string AnalysisDefinitionV6 = DocumentAnalysisWorkflow.DefinitionVersionV6;

    public const string AnalysisDefinition = DocumentAnalysisWorkflow.DefinitionVersionV5;
    public const string DocumentDefinition = DocumentSourceDraftWorkflow.DefinitionVersionV3;
    public const string AnalysisPromptVersion = DocumentAnalysisWorkflow.PromptVersionV6;
    public const string AnalysisSchemaVersion = "1.1.0";
    public const string DocumentPromptVersion = DocumentSourceDraftWorkflow.PromptVersionV3;

    public const string ChatDefinition = DocumentChatWorkflow.DefinitionVersionV3;
    public const string ChatPromptVersion = DocumentChatWorkflow.PromptVersionV3;

    /// <summary>Versioni congelate di un nuovo turno di chat documentale v3 (T128): prompt, schema AI, catalogo, algoritmo e validazione (in <c>ChatTurn.AgentConfig</c>).</summary>
    public static string ChatPromptVersions() =>
        JsonSerializer.Serialize(new
        {
            chatDefinition = ChatDefinition, chat = ChatPromptVersion, chatAiSchema = DocumentChatWorkflow.SchemaVersionV3, chatCatalog = ChatAliasCatalog.Version,
            algorithm = DocumentChatWorkflow.AlgorithmVersionV3, validation = DocumentChatWorkflow.ValidationVersionV3,
        });

    /// <summary>
    /// Congela sul turno la definizione della chat documentale v3 (<c>PromptVersion</c> e <c>AgentConfig</c>, colonne esistenti: nessuna migrazione): solo per le NUOVE richieste con
    /// almeno una fonte Ready e inclusa. I turni storici restano senza versioni (v2) e non vengono mai convertiti.
    /// </summary>
    public static void FreezeChat(bProjects.App.Conversations.ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        turn.PromptVersion = ChatPromptVersion;
        turn.AgentConfig = ChatPromptVersions();
    }

    /// <summary>Ramo documentale solo se almeno una fonte è Ready nello snapshot: senza, il run resta sul grafo e sul prompt 001.</summary>
    public static bool HasReadySource(SourceEvaluation? evaluation) =>
        evaluation is not null && evaluation.Entries.Any(e => e.Availability == SourceAvailability.Ready);

    /// <summary>
    /// Fissa la selezione alla richiesta (chat o Analizza): un SourceSnapshot immutabile con una riga per documento. Chiamare sotto il
    /// lock di progetto e nella transazione del comando. Solo Ready porta preparazione ed evidenza; la versione è registrata anche se non pronta e risolta.
    /// </summary>
    public SourceSnapshot Add(Guid projectId, SourceEvaluation evaluation, DateTimeOffset now)
    {
        var snapshotId = Guid.NewGuid();
        var rows = evaluation.Entries.Select(e =>
        {
            var d = Describe(e);
            return new SourceSnapshotItem
            {
                Id = Guid.NewGuid(),
                SnapshotId = snapshotId,
                ProjectId = projectId,
                DocumentId = e.Document.Id,
                Included = d.Included,
                Availability = d.Availability,
                DocumentVersionId = d.VersionId,
                PreparationId = d.PreparationId,
                EvidenceVersionId = d.EvidenceVersionId,
            };
        }).ToList();

        var canonical = new StringBuilder().Append("snapshot-v1;").Append(evaluation.Revision);
        foreach (var row in rows.OrderBy(r => r.DocumentId))
        {
            canonical.Append(';').Append(row.DocumentId.ToString("N")).Append('|').Append(row.Included).Append('|').Append(row.Availability)
                .Append('|').Append(row.DocumentVersionId?.ToString("N")).Append('|').Append(row.PreparationId?.ToString("N")).Append('|').Append(row.EvidenceVersionId?.ToString("N"));
        }

        var snapshot = new SourceSnapshot
        {
            Id = snapshotId,
            ProjectId = projectId,
            SelectionRevision = evaluation.Revision,
            Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))),
            CreatedAt = now,
        };
        db.SourceSnapshots.Add(snapshot);
        db.SourceSnapshotItems.AddRange(rows);
        return snapshot;
    }

    /// <summary>Come una voce di selezione compare in uno snapshot: la stessa chiave per scrivere le righe e per confrontare lo snapshot con la risoluzione attuale.</summary>
    private static (bool Included, SourceAvailability Availability, Guid? VersionId, Guid? PreparationId, Guid? EvidenceVersionId) Describe(SourceEntry e) => (
        e.Included,
        e.Availability,
        // Identità della versione selezionata, ove determinabile: una Pinned non pronta la conserva (senza preparazione né evidenza),
        // così una v2 successiva non cambia retroattivamente cosa il PM aveva scelto. Esclusa o senza versione risolta: nessuna.
        e.Availability == SourceAvailability.Excluded ? null : e.Resolved?.Id,
        e.Availability == SourceAvailability.Ready ? e.ReadyPreparation!.Id : null,
        e.Availability == SourceAvailability.Ready ? e.ReadyPreparation!.CurrentEvidenceVersionId : null);

    /// <summary>
    /// Snapshot con fonti cambiate (T097): la risoluzione attuale del progetto (bundle correnti, inclusioni, nuovi documenti) differisce dal manifest
    /// fissato alla richiesta. È solo un avviso di lettura: nessuna revisione, conferma, bozza o <c>canGenerate</c> ne dipende e nulla viene scritto.
    /// </summary>
    public async Task<HashSet<Guid>> ChangedAsync(Guid projectId, IReadOnlyCollection<Guid> snapshotIds, CancellationToken ct)
    {
        var changed = new HashSet<Guid>();
        if (snapshotIds.Count == 0)
        {
            return changed;
        }

        var rows = await db.SourceSnapshotItems.AsNoTracking().Where(i => i.ProjectId == projectId && snapshotIds.Contains(i.SnapshotId)).ToListAsync(ct);
        var current = (await new SourceSelectionService(db).EvaluateAsync(projectId, ct)).Entries.ToDictionary(e => e.Document.Id, Describe);
        foreach (var snapshotId in snapshotIds.Distinct())
        {
            var items = rows.Where(i => i.SnapshotId == snapshotId).ToList();
            var same = items.Count == current.Count && items.All(i =>
                current.TryGetValue(i.DocumentId, out var now)
                && now == (i.Included, i.Availability, i.DocumentVersionId, i.PreparationId, i.EvidenceVersionId));
            if (!same)
            {
                changed.Add(snapshotId);
            }
        }

        return changed;
    }

    /// <summary>Manifest storico (GET /source-snapshots/{s}): le righe fissate alla richiesta, con nome/numero di versione e se il bundle fissato è ancora quello corrente. Solo lettura.</summary>
    public async Task<SourceSnapshotManifest?> ManifestAsync(Guid projectId, Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await db.SourceSnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == snapshotId && s.ProjectId == projectId, ct);
        if (snapshot is null)
        {
            return null;
        }

        var items = await db.SourceSnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshotId).ToListAsync(ct);
        var documents = await db.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == projectId && db.SourceSnapshotItems.Any(i => i.SnapshotId == snapshotId && i.DocumentId == d.Id))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(ct);
        var versionIds = items.Where(i => i.DocumentVersionId is not null).Select(i => i.DocumentVersionId!.Value).Distinct().ToList();
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var preparationIds = items.Where(i => i.PreparationId is not null).Select(i => i.PreparationId!.Value).Distinct().ToList();
        var generations = await db.DocumentPreparations.AsNoTracking().Where(p => preparationIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Generation, ct);
        var currents = await db.EvidenceDocumentStates.AsNoTracking().Where(s => versionIds.Contains(s.DocumentVersionId)).ToDictionaryAsync(s => s.DocumentVersionId, s => s.CurrentPreparationId, ct);
        var byDocument = items.ToDictionary(i => i.DocumentId);
        var rows = documents.Select(d =>
        {
            var i = byDocument[d.Id];
            var version = i.DocumentVersionId is { } v ? versions.GetValueOrDefault(v) : null;
            var bundle = i.PreparationId is { } p ? (currents.GetValueOrDefault(i.DocumentVersionId!.Value) == p ? "Current" : "Superseded") : null;
            return new SourceSnapshotManifestItem(
                d.Id, d.DisplayName, i.Included, i.Availability.ToString(), i.DocumentVersionId, version?.Version, version?.OriginalName,
                i.PreparationId, i.PreparationId is { } prep ? generations.GetValueOrDefault(prep) : null, i.EvidenceVersionId, bundle);
        }).ToList();
        return new(snapshot.Id, snapshot.ProjectId, snapshot.SelectionRevision, snapshot.Hash, snapshot.CreatedAt, rows);
    }
    /// <summary>
    /// Snapshot della generazione: quello dell'analisi da cui nasce la revisione (le revisioni di un'analisi ne condividono il root), mai la
    /// selezione corrente. Null per le analisi legacy e per i progetti senza documenti.
    /// </summary>
    public async Task<Guid?> InheritedFromAnalysisAsync(Guid projectId, Guid analysisId, CancellationToken ct) =>
        await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis)
            .Select(r => r.SourceSnapshotId).SingleOrDefaultAsync(ct);

    /// <summary>True se lo snapshot contiene almeno una fonte Ready (ramo documentale ereditato dalla generazione).</summary>
    public async Task<bool> HasReadyAsync(Guid snapshotId, CancellationToken ct) =>
        await db.SourceSnapshotItems.AsNoTracking().AnyAsync(i => i.SnapshotId == snapshotId && i.Availability == SourceAvailability.Ready, ct);

    /// <summary>
    /// Versioni congelate di una nuova richiesta di Analizza v5 (T141, AI §13.1 e §13.6): quelle del single-call con valutazione dei Gap (prompt document-analysis-v4, schema del canonico, schema AI alias-2, catalogo, id canonici, algoritmo e
    /// validazione) più quelle della gerarchia v4 invariata (prompt, schema AI del batch, algoritmo del motore, catalogo del batch) e la sola policy Gap temporanea <c>reconcileGapPolicy</c>.
    /// La scelta single-call/gerarchico e la presenza di Gap si decidono a runtime dal catalogo congelato e dalla misura: nessuna decisione al POST.
    /// </summary>
    public static string AnalysisPromptVersions() =>
        JsonSerializer.Serialize(new
        {
            analysis = AnalysisPromptVersion, analysisSchema = AnalysisSchemaVersion, analysisAiSchema = DocumentAnalysisWorkflow.SchemaVersionV5,
            aliasCatalog = AnalysisAliasCatalog.Version, canonIds = AnalysisAliasCatalog.CanonIdsVersion,
            algorithm = DocumentAnalysisWorkflow.AlgorithmVersionV5, validation = DocumentAnalysisWorkflow.ValidationVersionV5,
            reconcile = ReconcileAliasContracts.PromptVersion, reconcileAiSchema = ReconcileAliasContracts.SchemaVersion,
            reconcileAlgorithm = DocumentReconcilePlan.AlgorithmVersion, reconcileCatalog = ReconcileAliasContracts.CatalogVersion,
            reconcileGapPolicy = DocumentAnalysisWorkflow.ReconcileGapPolicyV1,
            analysisOutputPolicy = AnalysisOutputEstimate.PolicyVersion,
        });

    /// <summary>
    /// Versioni congelate di una nuova richiesta di Analizza v6 (D1, AI §13.7, T148): le chiavi del single-call v6 (prompt document-analysis-v6, schema del canonico 1.1.0, schema AI alias-2, catalogo, id canonici, algoritmo
    /// analysis-single-v3, validazione v3, policy di stima dell'output single-call-output-estimate-1), quelle della gerarchia v4 invariata (prompt, schema AI del batch, algoritmo del motore, catalogo del batch) e le sei chiavi dello
    /// stadio Gap (`gapStage`, `gapPrompt`, `gapAiSchema`, `gapMessageFormat`, `gapPolicy`, `gapEstimate`). `reconcileGapPolicy` NON c'è: è sostituita dalle chiavi `gap*`. Instradamento e presenza di Gap si decidono a runtime.
    /// </summary>
    public static string AnalysisPromptVersionsV6() =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["analysis"] = AnalysisPromptVersion, ["analysisSchema"] = AnalysisSchemaVersion, ["analysisAiSchema"] = DocumentAnalysisWorkflow.SchemaVersionV5,
            ["aliasCatalog"] = AnalysisAliasCatalog.Version, ["canonIds"] = AnalysisAliasCatalog.CanonIdsVersion,
            ["algorithm"] = DocumentAnalysisWorkflow.AlgorithmVersionV5, ["validation"] = DocumentAnalysisWorkflow.ValidationVersionV5,
            ["reconcile"] = ReconcileAliasContracts.PromptVersion, ["reconcileAiSchema"] = ReconcileAliasContracts.SchemaVersion,
            ["reconcileAlgorithm"] = DocumentReconcilePlan.AlgorithmVersion, ["reconcileCatalog"] = ReconcileAliasContracts.CatalogVersion,
            ["analysisOutputPolicy"] = AnalysisOutputEstimate.PolicyVersion,
            ["gapStage"] = GapStage.FreezeKeys["gapStage"], ["gapPrompt"] = GapStage.FreezeKeys["gapPrompt"], ["gapAiSchema"] = GapStage.FreezeKeys["gapAiSchema"],
            ["gapMessageFormat"] = GapStage.FreezeKeys["gapMessageFormat"], ["gapPolicy"] = GapStage.FreezeKeys["gapPolicy"], ["gapEstimate"] = GapStage.FreezeKeys["gapEstimate"],
        });

    /// <summary>Versioni congelate di una nuova richiesta di Genera v3 (T127): prompt, schema del canonico 1.1.0, schema AI della bozza, catalogo, algoritmo e validazione.</summary>
    public static string DocumentPromptVersions() =>
        JsonSerializer.Serialize(new
        {
            document = DocumentPromptVersion, analysisSchema = AnalysisSchemaVersion, draftAiSchema = DocumentSourceDraftWorkflow.SchemaVersionV3,
            draftCatalog = DraftAliasCatalog.Version, algorithm = DocumentSourceDraftWorkflow.AlgorithmVersionV3, validation = DocumentSourceDraftWorkflow.ValidationVersionV3,
        });
}

/// <summary>Riga del manifest storico: <c>BundleState</c> è Current o Superseded per le righe Ready (il bundle fissato può non essere più quello corrente della versione file), altrimenti null.</summary>
public sealed record SourceSnapshotManifestItem(
    Guid DocumentId, string DisplayName, bool Included, string Availability, Guid? DocumentVersionId, int? Version, string? OriginalName,
    Guid? PreparationId, int? Generation, Guid? EvidenceVersionId, string? BundleState);

public sealed record SourceSnapshotManifest(
    Guid Id, Guid ProjectId, long SelectionRevision, string Hash, DateTimeOffset CreatedAt, IReadOnlyList<SourceSnapshotManifestItem> Items);
