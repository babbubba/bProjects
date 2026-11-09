using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Evidence;

// Aggregazione documentale del ledger (002 T051; data-model.md §Commit aggregato Evidence, contracts/ai-workflow.md §2.3). Legge i WorkOutput
// immutabili delle unità di una Preparation, ne verifica integrità e copertura e ne ricava, in modo deterministico, UNA sola versione Evidence
// 1.1.0. Non scrive nulla: l'inserimento e il resto del commit (Completed, Ready, bundle corrente) sono di DocumentBundleStore, sotto lock e fence.
// Non c'è dedup semantico fra unità: ogni intervallo proprietario ha esattamente una unità (planner esaustivo ed esclusivo) e le chiavi globali
// portano l'ordinale dell'unità, quindi non possono collidere; la riconciliazione semantica globale appartiene al run Analizza.

/// <summary>Aggregazione impossibile con codice stabile: <see cref="DocumentWorkException.CoverageIncomplete"/> e <see cref="DocumentWorkException.DocumentIncomplete"/> lasciano la preparazione Incomplete, gli altri Failed.</summary>
public sealed class DocumentAggregationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>Limite o copertura noti (Incomplete) invece di un errore di integrità o di output (Failed).</summary>
    public bool LeavesPreparationIncomplete => Code is DocumentWorkException.CoverageIncomplete or DocumentWorkException.DocumentIncomplete;
}

/// <summary>Evidenza della versione aggregata: identità deterministica, ordinale stabile, chiave globale (ordinale dell'unità + chiave locale).</summary>
public sealed record AggregatedEvidence(
    Guid Id, int Ordinal, string Key, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement, DocumentEvidenceContent Content);

public sealed record AggregatedRelation(
    Guid Id, Guid FromEvidenceId, Guid ToEvidenceId, EvidenceRelationType Type, EvidenceRelationBasis Basis, string? Rationale);

/// <summary>Versione documentale pronta per essere inserita (modello, parametri e hash del manifest compresi).</summary>
public sealed record DocumentAggregate(
    Guid PreparationId, IReadOnlyList<AggregatedEvidence> Evidences, IReadOnlyList<AggregatedRelation> Relations, string? NoEvidenceReason, string ManifestHash,
    string ModelId, string ParametersJson, int UnitCount, int BatchCount);

internal static class DocumentEvidenceAggregator
{
    public const string UnrecordedModel = "unrecorded";

    private sealed record Range(string Key, Guid BlockId, int Start, int End);

    public static async Task<DocumentAggregate> LoadAsync(AppDbContext db, Guid preparationId, string? configuredModelId, CancellationToken ct)
    {
        var preparation = await db.DocumentPreparations.AsNoTracking().SingleAsync(p => p.Id == preparationId, ct);
        var version = await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == preparation.DocumentVersionId, ct);
        var artifact = await db.ParsedArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.PreparationId == preparationId, ct)
            ?? throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "La preparazione non ha un artefatto di parsing pubblicato.");

        if (await db.PreparationIssues.AsNoTracking().AnyAsync(i => i.PreparationId == preparationId && i.AffectsCompleteness, ct))
        {
            throw new DocumentAggregationException(DocumentWorkException.DocumentIncomplete, "La preparazione ha segnalazioni che incidono sulla completezza: non può diventare Ready.");
        }

        var portions = await db.DocumentPortions.AsNoTracking().Where(p => p.PreparationId == preparationId).OrderBy(p => p.Ordinal).ToListAsync(ct);
        if (portions.Count == 0)
        {
            throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, "La preparazione non ha porzioni da aggregare.");
        }

        if (portions.Any(p => p.Status != PortionStatus.Completed || p.JobId is null || p.ResultHash is null))
        {
            throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, "Almeno una porzione non ha un'estrazione valida.");
        }

        var units = await db.DocumentJobs.AsNoTracking().Where(j => j.PreparationId == preparationId && j.Kind == DocumentJobKind.ExtractUnit).ToListAsync(ct);
        if (units.Count == 0 || units.Any(u => u.Status != DocumentJobStatus.Completed || u.ResultHash is null))
        {
            throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, "Almeno un'unità di estrazione non è valida.");
        }

        var portionsOf = portions.GroupBy(p => p.JobId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        if (portionsOf.Keys.Any(id => units.All(u => u.Id != id)) || units.Any(u => !portionsOf.ContainsKey(u.Id)))
        {
            throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "Le porzioni e le unità di estrazione non corrispondono.");
        }

        var ordered = units.OrderBy(u => portionsOf[u.Id][0].Ordinal).ToList();
        var policyHash = System.Text.RegularExpressions.Regex.IsMatch(preparation.PolicyHash, "^[0-9a-f]{64}$") ? preparation.PolicyHash : Sha(preparation.PolicyHash);

        var evidences = new List<AggregatedEvidence>();
        var relations = new List<AggregatedRelation>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var configHashes = new SortedSet<string>(StringComparer.Ordinal);
        var runtimes = new SortedSet<string>(StringComparer.Ordinal);
        var batchCount = 0;

        foreach (var unit in ordered)
        {
            var unitPortions = portionsOf[unit.Id];
            var batches = await db.DerivedBatches.AsNoTracking().Where(b => b.OwnerKind == WorkOwnerKind.DocumentJob && b.OwnerId == unit.Id).OrderBy(b => b.Ordinal).ToListAsync(ct);
            var outputs = await db.WorkOutputs.AsNoTracking().Where(o => o.OwnerKind == WorkOwnerKind.DocumentJob && o.OwnerId == unit.Id).ToListAsync(ct);
            var manifest = await db.WorkManifests.AsNoTracking().SingleOrDefaultAsync(m => m.OwnerKind == WorkOwnerKind.DocumentJob && m.OwnerId == unit.Id, ct)
                ?? throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "L'unità non ha un manifesto di lavoro.");
            if (manifest.PolicyHash != policyHash || manifest.DefinitionVersion != DocumentEvidenceAgent.DefinitionVersion || manifest.AlgorithmVersion != DocumentEvidenceAgent.AlgorithmVersion)
            {
                throw new DocumentAggregationException(DocumentWorkException.CheckpointIncompatible, "Il manifesto dell'unità non è compatibile con la preparazione o con l'algoritmo corrente.");
            }

            configHashes.Add(manifest.ConfigHash);
            runtimes.Add(manifest.RuntimeFingerprint);
            if (batches.Count == 0 || batches.Select((b, i) => b.Ordinal == i).Contains(false) || outputs.Count != batches.Count)
            {
                throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, "L'unità non ha un output valido per ciascun batch pianificato.");
            }

            var byBatch = outputs.ToDictionary(o => o.BatchId, StringComparer.Ordinal);
            var ofUnit = new List<WorkOutput>();
            foreach (var batch in batches)
            {
                if (!byBatch.TryGetValue(batch.BatchId, out var output) || output.Status != WorkOutputStatus.Committed || output.InputHash != batch.InputHash
                    || output.ManifestHash != manifest.Hash || output.SchemaVersion != DocumentEvidenceContent.SchemaVersionValue
                    || output.ValidationVersion != DocumentEvidenceAgent.ValidationVersion)
                {
                    throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "Un output di batch non coincide con il batch, il manifesto o le versioni attese.");
                }

                ofUnit.Add(output);
            }

            if (unit.ResultHash != Sha("unit-result|" + string.Join(',', ofUnit.Select(o => o.OutputHash))))
            {
                throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "L'hash di risultato dell'unità non corrisponde ai suoi output.");
            }

            VerifyCoverage(unitPortions, ofUnit);
            batchCount += batches.Count;

            var blockIds = new HashSet<Guid>();
            var decodedOutputs = new List<(WorkOutput Output, DocumentUnitOutput Decoded)>();
            foreach (var output in ofUnit)
            {
                if (output.PayloadJson is null || CanonicalJson.Sha256Hex(output.PayloadJson) != output.OutputHash)
                {
                    throw new DocumentAggregationException(DocumentWorkException.WorkOutputInvalid, "Il payload dell'output manca o non corrisponde all'hash registrato.");
                }

                DocumentUnitOutput decoded;
                try
                {
                    decoded = JsonSerializer.Deserialize<DocumentUnitOutput>(output.PayloadJson, DocumentEvidenceCodec.Options)
                        ?? throw new JsonException("Output nullo.");
                }
                catch (JsonException)
                {
                    throw new DocumentAggregationException(DocumentWorkException.WorkOutputInvalid, "Il payload dell'output non è un'estrazione evidence-1.1.0 valida.");
                }

                var batchOrdinal = batches.First(b => b.BatchId == output.BatchId).Ordinal;
                if (decoded.SchemaVersion != DocumentEvidenceContent.SchemaVersionValue
                    || decoded.UnitOrdinal != unitPortions[0].Ordinal * DocumentEvidenceAgent.UnitOrdinalStride + batchOrdinal)
                {
                    throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "L'ordinale o lo schema dell'output non coincidono con l'unità.");
                }

                foreach (var source in decoded.Evidences.SelectMany(e => e.Content.Sources))
                {
                    if (source.BlockId is { } block)
                    {
                        blockIds.Add(block);
                    }
                }

                decodedOutputs.Add((output, decoded));
            }

            var blocks = await db.DocumentBlocks.AsNoTracking().Where(b => b.ArtifactId == artifact.Id && blockIds.Contains(b.Id))
                .Select(b => new { b.Id, b.CanonicalText }).ToDictionaryAsync(b => b.Id, b => b.CanonicalText, ct);
            foreach (var (_, decoded) in decodedOutputs)
            {
                var ids = new Dictionary<string, AggregatedEvidence>(StringComparer.Ordinal);
                foreach (var evidence in decoded.Evidences)
                {
                    VerifyContent(evidence, decoded.UnitOrdinal, preparation, version, artifact, blocks);
                    if (evidence.GlobalKey.Length > 32 || !keys.Add(evidence.GlobalKey))
                    {
                        throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "Chiave globale di evidenza duplicata o troppo lunga.");
                    }

                    var aggregated = new AggregatedEvidence(
                        DeterministicIds.Derive("evidence:document", preparationId, evidence.GlobalKey), evidences.Count + 1, evidence.GlobalKey, evidence.Kind,
                        evidence.Polarity, evidence.Certainty, evidence.Statement, evidence.Content);
                    ids[evidence.Key] = aggregated;
                    evidences.Add(aggregated);
                }

                foreach (var relation in decoded.Relations)
                {
                    if (!ids.TryGetValue(relation.FromKey, out var from) || !ids.TryGetValue(relation.ToKey, out var to) || from.Id == to.Id)
                    {
                        throw new DocumentAggregationException(DocumentWorkException.WorkOutputInvalid, "Una relazione cita un'evidenza che non appartiene all'output.");
                    }

                    relations.Add(new AggregatedRelation(
                        DeterministicIds.Derive("relation:document", preparationId, from.Key, to.Key, relation.Type), from.Id, to.Id, relation.Type, relation.Basis,
                        relation.Rationale is { Length: > 2_000 } text ? text[..2_000] : relation.Rationale));
                }
            }
        }

        string? noEvidence = null;
        if (evidences.Count == 0)
        {
            // Zero evidenze è un esito esplicito: la motivazione deve esistere per OGNI porzione, altrimenti sarebbe una perdita non distinguibile.
            if (portions.Any(p => string.IsNullOrWhiteSpace(p.NoEvidenceReason)))
            {
                throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, "Zero evidenze senza una motivazione per ogni porzione.");
            }

            var reason = string.Join(" / ", portions.Select(p => p.NoEvidenceReason!.Trim()).Distinct(StringComparer.Ordinal));
            noEvidence = reason.Length <= 2_000 ? reason : reason[..2_000];
        }

        var manifestHash = Sha("document-bundle|" + artifact.Sha256 + "|" + string.Join(',', ordered.Select(u => u.ResultHash)));
        var unitIds = ordered.Select(u => u.Id).ToList();
        var models = await db.InferenceCalls.AsNoTracking().Where(c => c.DocumentJobId != null && unitIds.Contains(c.DocumentJobId.Value)).Select(c => c.ModelId).Distinct().OrderBy(m => m).ToListAsync(ct);
        var modelId = !string.IsNullOrWhiteSpace(configuredModelId) ? configuredModelId.Trim() : models.FirstOrDefault() ?? UnrecordedModel;
        var parameters = JsonSerializer.Serialize(new
        {
            aggregation = DocumentBundleStore.AlgorithmVersion, units = ordered.Count, batches = batchCount, portions = portions.Count, policyHash,
            configHashes, runtimeFingerprints = runtimes, models,
        });
        return new(preparationId, evidences, relations, noEvidence, manifestHash, modelId.Length <= 256 ? modelId : modelId[..256], parameters, ordered.Count, batchCount);
    }

    /// <summary>Ogni range proprietario della unità è coperto, da pezzi contigui e in ordine, esattamente una volta; nessun pezzo estraneo.</summary>
    private static void VerifyCoverage(IReadOnlyList<DocumentPortion> portions, IReadOnlyList<WorkOutput> outputs)
    {
        var expected = portions.SelectMany(p => ReadRanges(p.RangesJson)).ToList();
        List<string> covered;
        try
        {
            covered = [.. outputs.SelectMany(o => JsonSerializer.Deserialize<string[]>(o.CoveredIdsJson) ?? [])];
        }
        catch (JsonException)
        {
            throw new DocumentAggregationException(DocumentWorkException.WorkOutputInvalid, "La mappa di copertura di un output non è valida.");
        }

        var byOrigin = covered.GroupBy(DocumentPortionPlanner.OriginOf, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (byOrigin.Count != expected.Count || expected.Any(r => !byOrigin.ContainsKey(r.Key)))
        {
            throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, "Gli output dell'unità non coprono esattamente i suoi range proprietari.");
        }

        foreach (var range in expected)
        {
            var position = range.Start;
            foreach (var piece in byOrigin[range.Key])
            {
                var (start, end) = Offsets(piece, range);
                if (start != position || end <= start)
                {
                    throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, $"Il range {range.Key} non è coperto in modo contiguo.");
                }

                position = end;
            }

            if (position != range.End)
            {
                throw new DocumentAggregationException(DocumentWorkException.CoverageIncomplete, $"Il range {range.Key} non è coperto fino alla fine.");
            }
        }
    }

    private static (int Start, int End) Offsets(string piece, Range range)
    {
        if (piece == range.Key)
        {
            return (range.Start, range.End);
        }

        var marker = piece.IndexOf(DocumentPortionPlanner.PieceMarker, StringComparison.Ordinal);
        var parts = marker < 0 ? [] : piece[(marker + 1)..].Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var start) || !int.TryParse(parts[1], out var end))
        {
            throw new DocumentAggregationException(DocumentWorkException.WorkOutputInvalid, "Chiave di sotto-intervallo non valida nella mappa di copertura.");
        }

        return (start, end);
    }

    private static List<Range> ReadRanges(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(r => new Range(
            r.GetProperty("rangeKey").GetString()!, r.GetProperty("blockId").GetGuid(), r.GetProperty("start").GetInt32(), r.GetProperty("end").GetInt32()))];
    }

    /// <summary>Contenuto 1.1 rilegge e rispetta il contratto strict; ogni fonte risolve alla preparazione, alla versione e al blocco, con citazione esatta.</summary>
    private static void VerifyContent(
        DocumentUnitEvidence evidence, int unitOrdinal, DocumentPreparation preparation, DocumentVersion version, ParsedArtifact artifact, Dictionary<Guid, string> blocks)
    {
        if (evidence.GlobalKey != $"{unitOrdinal}{evidence.Key}")
        {
            throw new DocumentAggregationException(DocumentWorkException.IntegrityConflict, "La chiave globale non coincide con l'ordinale dell'unità.");
        }

        try
        {
            DocumentEvidenceCodec.DecodeContent(DocumentEvidenceCodec.EncodeContent(evidence.Content));
        }
        catch (JsonException)
        {
            throw new DocumentAggregationException(DocumentWorkException.WorkOutputInvalid, "Il contenuto di un'evidenza non rispetta evidence-1.1.0.");
        }

        foreach (var source in evidence.Content.Sources)
        {
            if (source.Kind != EvidenceSourceKind.DocumentPassage || source.PreparationId != preparation.Id || source.DocumentVersionId != version.Id
                || source.DocumentId != version.DocumentId || source.ArtifactId != artifact.Id
                || source.BlockId is not { } block || !blocks.TryGetValue(block, out var text)
                || source.End > text.Length || !string.Equals(text[source.Start..source.End], source.Quote, StringComparison.Ordinal))
            {
                throw new DocumentAggregationException(DocumentWorkException.WorkOutputInvalid, "Una fonte non risolve al blocco canonico della preparazione con la citazione esatta.");
            }
        }
    }

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
