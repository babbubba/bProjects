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
/// Definizioni e versioni di prompt/schema documentali sono persistite sul root alla richiesta: le NUOVE richieste di Analizza (T125, AI §7.8) e di
/// Genera (T127, AI §7.11) congelano la v3 con alias; un run già congelato con la v2 (storica) resta v2 e il suo instradamento è del dispatch (T123). Nessuna conversione di run.
/// </summary>
public sealed class SourceSnapshotStore(AppDbContext db)
{
    public const string AnalysisDefinition = DocumentAnalysisWorkflow.DefinitionVersionV3;
    public const string DocumentDefinition = DocumentSourceDraftWorkflow.DefinitionVersionV3;
    public const string AnalysisPromptVersion = DocumentAnalysisWorkflow.PromptVersionV3;
    public const string AnalysisSchemaVersion = "1.1.0";
    public const string DocumentPromptVersion = DocumentSourceDraftWorkflow.PromptVersionV3;

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
        var rows = evaluation.Entries.Select(e => new SourceSnapshotItem
        {
            Id = Guid.NewGuid(),
            SnapshotId = snapshotId,
            ProjectId = projectId,
            DocumentId = e.Document.Id,
            Included = e.Included,
            Availability = e.Availability,
            // Identità della versione selezionata, ove determinabile: una Pinned non pronta la conserva (senza preparazione né evidenza),
            // così una v2 successiva non cambia retroattivamente cosa il PM aveva scelto. Esclusa o senza versione risolta: nessuna.
            DocumentVersionId = e.Availability == SourceAvailability.Excluded ? null : e.Resolved?.Id,
            PreparationId = e.Availability == SourceAvailability.Ready ? e.ReadyPreparation!.Id : null,
            EvidenceVersionId = e.Availability == SourceAvailability.Ready ? e.ReadyPreparation!.CurrentEvidenceVersionId : null,
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

    /// <summary>Versioni congelate di una nuova richiesta di Analizza v3: prompt, schema del canonico, schema AI, catalogo, id canonici, algoritmo e validazione.</summary>
    public static string AnalysisPromptVersions() =>
        JsonSerializer.Serialize(new
        {
            analysis = AnalysisPromptVersion, analysisSchema = AnalysisSchemaVersion, analysisAiSchema = DocumentAnalysisWorkflow.SchemaVersionV3,
            aliasCatalog = AnalysisAliasCatalog.Version, canonIds = AnalysisAliasCatalog.CanonIdsVersion,
            algorithm = DocumentAnalysisWorkflow.AlgorithmVersionV3, validation = DocumentAnalysisWorkflow.ValidationVersionV3,
        });

    /// <summary>Versioni congelate di una nuova richiesta di Genera v3 (T127): prompt, schema del canonico 1.1.0, schema AI della bozza, catalogo, algoritmo e validazione.</summary>
    public static string DocumentPromptVersions() =>
        JsonSerializer.Serialize(new
        {
            document = DocumentPromptVersion, analysisSchema = AnalysisSchemaVersion, draftAiSchema = DocumentSourceDraftWorkflow.SchemaVersionV3,
            draftCatalog = DraftAliasCatalog.Version, algorithm = DocumentSourceDraftWorkflow.AlgorithmVersionV3, validation = DocumentSourceDraftWorkflow.ValidationVersionV3,
        });
}
