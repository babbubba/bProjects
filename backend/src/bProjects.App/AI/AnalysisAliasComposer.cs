using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Compositore deterministico alias → canonico (contracts/ai-workflow.md §7.5–7.7, T125). Risolve gli alias del DTO `document-analysis-alias-1`
// contro il catalogo congelato, genera gli id canonici e compone `SingleCallOutput` (AnalysisContent 1.1.0, risultati A/B/Cross, mappa
// item→evidenze) che poi valida con i validatori esistenti, mai allentati. Non prende decisioni semantiche: compone solo ciò che il modello
// ha dichiarato in modo esplicito (evidenze citate, visite, finding, verdetti, patch). Nessun database, nessun LLM.

/// <summary>
/// Esito della composizione. <see cref="Output"/> è valorizzato solo se l'esito è riutilizzabile (Complete o ValidIncomplete); un rifiuto porta le
/// violazioni (alias e percorso) e il codice permanente. <see cref="Validation"/> è l'esito dei validatori di dominio sul canonico composto.
/// <see cref="DuplicateKeys"/> (T130) è valorizzato solo se l'UNICA causa del rifiuto sono chiavi locali duplicate: segnale tipizzato per il retry guidato.
/// <see cref="GapAssessments"/> e <see cref="GapAssessmentsHash"/> (T136) sono valorizzati solo per il contratto alias-2: i record `gap-assessment-1` accettati (interpretazione
/// AI non validata semanticamente) e l'hash della loro forma canonica.
/// </summary>
public sealed record AliasComposition(
    ReconciliationOutcome Outcome, string? ErrorCode, IReadOnlyList<string> Violations, SingleCallOutput? Output, ReconciliationValidation? Validation,
    string? AiOutputHash, string CatalogHash, string? CanonicalHash, IReadOnlyList<string>? DuplicateKeys = null,
    IReadOnlyList<AiGapAssessment>? GapAssessments = null, string? GapAssessmentsHash = null)
{
    public bool IsReusableWorkOutput => Outcome != ReconciliationOutcome.Invalid;
}

public static class AnalysisAliasComposer
{
    public const string AlgorithmVersion = "analysis-single-v2";
    public const string ValidationVersion = "document-analysis-validation-v2";

    /// <summary>Single-call v5 (T136, contracts/ai-workflow.md §13.1): DTO alias-2 con valutazioni dei Gap e coerenze §13.4. I valori v2 sopra restano storici e invariati.</summary>
    public const string AlgorithmVersionV3 = "analysis-single-v3";

    public const string ValidationVersionV3 = "document-analysis-validation-v3";

    /// <summary>`canon-ids-1`: UUIDv8 da SHA-256(JCS(["canon-ids-1", kind, rootId, sourceHash, localKey]))[0..16], kind in {item, conflict} (le alternative sono item).</summary>
    public static Guid CanonicalId(string kind, Guid rootId, string sourceHash, string localKey)
    {
        if (kind is not ("item" or "conflict"))
        {
            throw new ArgumentException("kind ammessi: item, conflict.", nameof(kind));
        }

        ArgumentException.ThrowIfNullOrEmpty(sourceHash);
        ArgumentException.ThrowIfNullOrEmpty(localKey);
        var tuple = new JsonArray(
            JsonValue.Create(AnalysisAliasCatalog.CanonIdsVersion), JsonValue.Create(kind), JsonValue.Create(rootId.ToString("D")), JsonValue.Create(sourceHash), JsonValue.Create(localKey));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(tuple.ToJsonString())))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>`canonicalHash`: SHA-256 della forma canonica del <see cref="SingleCallOutput"/> composto.</summary>
    public static string CanonicalHash(SingleCallOutput output) => CanonicalJson.Sha256Hex(ReconciliationCodec.Encode(output));

    /// <summary>
    /// `canonicalHash` del contratto alias-2: copre il canonico composto E le valutazioni dei Gap (<paramref name="gapAssessmentsHash"/>), che non entrano in AnalysisContent 1.1.0.
    /// Forma distinta da quella di alias-1: un canonico alias-2 non coincide mai con uno alias-1, neppure a valutazioni vuote.
    /// </summary>
    public static string CanonicalHash(SingleCallOutput output, string gapAssessmentsHash)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(gapAssessmentsHash);
        var node = new JsonObject
        {
            ["contract"] = AnalysisAliasContracts.ContractValueV2, ["gapAssessmentsHash"] = gapAssessmentsHash, ["output"] = JsonNode.Parse(ReconciliationCodec.Encode(output)),
        };
        return CanonicalJson.HashText(CanonicalJson.Canonicalize(node.ToJsonString()));
    }

    /// <summary>
    /// Decodifica strict del DTO e composizione. Un catalogo non coincidente con lo snapshot congelato (manomissione, altro snapshot) è
    /// <c>checkpoint_incompatible</c> (eccezione): non è un errore del modello. Un output del modello non conforme è un rifiuto permanente.
    /// Il contratto del DTO (alias-1 o alias-2) è quello del catalogo: il decoder dell'altro contratto non viene mai usato.
    /// </summary>
    public static AliasComposition Compose(
        string aiJson, AnalysisAliasCatalog catalog, DocumentAnalysisInput input, string? batchKey = null, string? inputHash = null, string? expectedCanonicalHash = null)
    {
        VerifyCatalog(catalog, input);
        if (catalog.Data.ContractVersion == AnalysisAliasContracts.SchemaVersionV2)
        {
            AiAnalysisOutputV2 v2;
            try
            {
                v2 = AnalysisAliasContracts.DecodeV2(aiJson);
            }
            catch (JsonException e)
            {
                return Reject(catalog, null, DocumentWorkException.WorkOutputInvalid, [$"JSON non conforme a {AnalysisAliasContracts.SchemaVersionV2}: {e.Message}"]);
            }

            return new Builder(AsAlias1(v2), v2.GapAssessments, AnalysisAliasContracts.Hash(v2), catalog, input, batchKey ?? input.BatchKey, inputHash ?? input.InputHash).Run(expectedCanonicalHash);
        }

        AiAnalysisOutput dto;
        try
        {
            dto = AnalysisAliasContracts.Decode(aiJson);
        }
        catch (JsonException e)
        {
            return Reject(catalog, null, DocumentWorkException.WorkOutputInvalid, [$"JSON non conforme a {AnalysisAliasContracts.SchemaVersion}: {e.Message}"]);
        }

        return new Builder(dto, null, AnalysisAliasContracts.Hash(dto), catalog, input, batchKey ?? input.BatchKey, inputHash ?? input.InputHash).Run(expectedCanonicalHash);
    }

    public static AliasComposition Compose(
        AiAnalysisOutput dto, AnalysisAliasCatalog catalog, DocumentAnalysisInput input, string? batchKey = null, string? inputHash = null, string? expectedCanonicalHash = null)
    {
        ArgumentNullException.ThrowIfNull(dto);
        VerifyCatalog(catalog, input, AnalysisAliasContracts.SchemaVersion);
        return new Builder(dto, null, AnalysisAliasContracts.Hash(dto), catalog, input, batchKey ?? input.BatchKey, inputHash ?? input.InputHash).Run(expectedCanonicalHash);
    }

    /// <summary>Composizione del DTO `document-analysis-alias-2` (T136): come alias-1 più le valutazioni dei Gap, la completezza v5 e le coerenze di §13.4.</summary>
    public static AliasComposition Compose(
        AiAnalysisOutputV2 dto, AnalysisAliasCatalog catalog, DocumentAnalysisInput input, string? batchKey = null, string? inputHash = null, string? expectedCanonicalHash = null)
    {
        ArgumentNullException.ThrowIfNull(dto);
        VerifyCatalog(catalog, input, AnalysisAliasContracts.SchemaVersionV2);
        return new Builder(AsAlias1(dto), dto.GapAssessments, AnalysisAliasContracts.Hash(dto), catalog, input, batchKey ?? input.BatchKey, inputHash ?? input.InputHash).Run(expectedCanonicalHash);
    }

    /// <summary>Gli otto campi comuni di alias-2 come DTO alias-1: il Builder condivide la composizione e riceve a parte le valutazioni e l'hash del DTO originale.</summary>
    private static AiAnalysisOutput AsAlias1(AiAnalysisOutputV2 dto) => new()
    {
        Contract = AnalysisAliasContracts.ContractValue, Visited = dto.Visited, NoFindings = dto.NoFindings, Items = dto.Items, Conflicts = dto.Conflicts, Findings = dto.Findings,
        KnownRelationChecks = dto.KnownRelationChecks, Patches = dto.Patches,
    };

    /// <summary>Il catalogo deve essere la funzione pura dello snapshot per la sua versione di contratto; <paramref name="expectedContract"/> vincola il contratto del DTO ricevuto.</summary>
    private static void VerifyCatalog(AnalysisAliasCatalog catalog, DocumentAnalysisInput input, string? expectedContract = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(input);
        var contract = catalog.Data.ContractVersion;
        if (contract is not (AnalysisAliasContracts.SchemaVersion or AnalysisAliasContracts.SchemaVersionV2) || (expectedContract is not null && contract != expectedContract)
            || AnalysisAliasCatalog.Build(input, contract).CatalogHash != catalog.CatalogHash)
        {
            throw new DocumentWorkException(DocumentWorkException.CheckpointIncompatible, "Il catalogo alias non coincide con lo snapshot congelato del run.");
        }
    }

    private static AliasComposition Reject(
        AnalysisAliasCatalog catalog, string? aiHash, string code, IReadOnlyList<string> violations, ReconciliationValidation? validation = null, IReadOnlyList<string>? duplicateKeys = null) =>
        new(ReconciliationOutcome.Invalid, code, [.. violations.Distinct(StringComparer.Ordinal)], null, validation, aiHash, catalog.CatalogHash, null, duplicateKeys);

    private sealed record ItemRecord(string Key, string Path, AiItem Dto, Guid Id, List<AliasEvidenceEntry> Evidence);

    private sealed class Builder(
        AiAnalysisOutput dto, IReadOnlyList<AiGapAssessment>? gapAssessments, string aiHash, AnalysisAliasCatalog catalog, DocumentAnalysisInput input, string batchKey, string inputHash)
    {
        private readonly List<string> _errors = [];
        private readonly List<string> _duplicateKeys = [];
        private readonly string _sourceHash = catalog.Data.SourceHash;
        private readonly Guid _root = catalog.Data.RunId;

        public AliasComposition Run(string? expectedCanonicalHash)
        {
            var visitedA = Visited(dto.Visited.A, "visited.A");
            var visitedB = Visited(dto.Visited.B, "visited.B");

            var sections = new List<(string Name, List<ItemRecord> Items)>();
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, list) in dto.Items.Sections())
            {
                sections.Add((name, [.. list.Select((item, index) => Item(item, $"items.{name}[{index}]", keys))]));
            }

            var conflicts = new List<(AiConflict Dto, Guid Id, List<ItemRecord> Alternatives)>();
            var conflictKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var c = 0; c < dto.Conflicts.Count; c++)
            {
                var conflict = dto.Conflicts[c];
                if (!conflictKeys.Add(conflict.Key))
                {
                    DuplicateKey(conflict.Key, $"conflicts[{c}].key: chiave locale {conflict.Key} duplicata.");
                }

                conflicts.Add((conflict, CanonicalId("conflict", _root, _sourceHash, conflict.Key),
                    [.. conflict.Alternatives.Select((alt, a) => Item(alt, $"conflicts[{c}].alternatives[{a}]", keys))]));
            }

            var allItems = sections.SelectMany(s => s.Items).Concat(conflicts.SelectMany(c => c.Alternatives)).ToList();
            var proposals = allItems.Where(i => i.Evidence.Count > 0).ToList();
            var proposalKeys = proposals.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

            var candidateIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var perView = new Dictionary<ReconcileView, List<(ReconcileCandidate Candidate, CandidateDisposition Disposition)>>
            {
                [ReconcileView.A] = [], [ReconcileView.B] = [], [ReconcileView.Cross] = [],
            };
            Findings(aiHash, candidateIds, perView);
            KnownRelationChecks(aiHash, candidateIds, perView);
            var patchDrafts = PatchDrafts(proposalKeys);
            var gapContext = GapAssessments(sections, conflicts);

            if (_errors.Count > 0)
            {
                // T130: chiavi duplicate come UNICA causa (ogni altro errore ne annulla il segnale) = candidato al retry guidato.
                return Reject(catalog, aiHash, DocumentWorkException.WorkOutputInvalid, _errors, duplicateKeys: _errors.Count == _duplicateKeys.Count ? [.. _duplicateKeys.Distinct(StringComparer.Ordinal)] : null);
            }

            // Fonti di analysis: solo le originali delle evidenze citate dagli item, in ordine di lettura (alias S).
            var used = new SortedDictionary<int, AliasSourceEntry>();
            foreach (var record in allItems)
            {
                foreach (var source in record.Evidence.SelectMany(catalog.SourcesOf))
                {
                    used[AnalysisAliasPattern.Number(source.Alias)] = source;
                }
            }

            var analysis = new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue,
                AnalysisId = _root,
                ProjectId = catalog.Data.ProjectId,
                SourceCutoffSequence = input.Cutoff,
                ValidationState = AnalysisContentState.Unvalidated,
                Sources = [.. used.Values.Select(Source)],
                Goals = Items(sections, "goals"),
                ContextAndScope = new DocumentAnalysisContextAndScope
                {
                    Overview = Items(sections, "scopeOverview"), Included = Items(sections, "scopeIncluded"), Excluded = Items(sections, "scopeExcluded"),
                },
                Requirements = Items(sections, "requirements"),
                OperationalFlows = Items(sections, "flows"),
                Constraints = Items(sections, "constraints"),
                Dependencies = Items(sections, "dependencies"),
                Assumptions = Items(sections, "assumptions"),
                OpenQuestions = Items(sections, "openQuestions"),
                Conflicts =
                [
                    .. conflicts.Select(c => new DocumentAnalysisConflict
                    {
                        Id = c.Id, Description = c.Dto.Description, Status = AnalysisConflictStatus.Open, Alternatives = [.. c.Alternatives.Select(ToCanonical)], Resolution = null,
                    }),
                ],
            };

            var itemProposals = proposals.Select(p => new ItemProposal
            {
                LocalKey = p.Key, Text = p.Dto.Text, Classification = p.Dto.Classification, EvidenceIds = SortedEvidenceIds(p.Evidence), SourceIds = SortedSourceIds(p.Evidence),
            }).ToList();

            var visitedCross = visitedA.Intersect(visitedB, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            ReconcileResult Result(ReconcileView view, IReadOnlyList<string> visited, string? noFindings, IReadOnlyList<ItemProposal> items, IReadOnlyList<Patch> patches) => new()
            {
                BatchId = batchKey, InputHash = inputHash, View = view, Level = 0, VisitedEntryIds = visited, VisitedNodeIds = [],
                Candidates = [.. perView[view].Select(x => x.Candidate)], Dispositions = [.. perView[view].Select(x => x.Disposition)],
                ItemProposals = items, Patches = patches, Descriptor = null, NoFindingsReason = noFindings,
            };

            // La base si calcola PRIMA delle patch, dalle sole proposte e relazioni note; poi tutte le patch si firmano contro quella base.
            var interim = new SingleCallOutput
            {
                Results =
                [
                    Result(ReconcileView.A, visitedA, dto.NoFindings.A, [], []), Result(ReconcileView.B, visitedB, dto.NoFindings.B, [], []),
                    Result(ReconcileView.Cross, visitedCross, dto.NoFindings.Cross, itemProposals, []),
                ],
                Analysis = analysis,
                EvidenceMap = [],
            };
            var baseState = DocumentAnalysisBase.Compute(interim, input.Snapshot);
            var patches = StampPatches(patchDrafts, baseState);
            if (_errors.Count > 0)
            {
                return Reject(catalog, aiHash, DocumentWorkException.WorkOutputInvalid, _errors);
            }

            var output = interim with
            {
                Results = [interim.Results[0], interim.Results[1], interim.Results[2] with { Patches = patches }],
                EvidenceMap =
                [
                    .. proposals.Select(p => new EvidenceMapEntry { ItemId = p.Id, EvidenceIds = SortedEvidenceIds(p.Evidence) }),
                ],
            };

            var validation = DocumentAnalysisValidator.Validate(ReconciliationCodec.Encode(output), input.Snapshot, input.Facts, batchKey, inputHash, gapContext);
            if (!validation.IsReusableWorkOutput)
            {
                return Reject(catalog, aiHash, validation.ErrorCode ?? DocumentWorkException.WorkOutputInvalid, validation.Violations, validation);
            }

            var gapHash = gapAssessments is null ? null : GapAssessmentRules.Hash(gapAssessments);
            var canonicalHash = gapHash is null ? CanonicalHash(output) : CanonicalHash(output, gapHash);
            if (expectedCanonicalHash is not null && expectedCanonicalHash != canonicalHash)
            {
                return Reject(catalog, aiHash, DocumentWorkException.IntegrityConflict, ["canonicalHash: il canonico ricomposto è diverso da quello committato."], validation);
            }

            return new AliasComposition(
                validation.Outcome, null, [], output, validation, aiHash, catalog.CatalogHash, canonicalHash, null, gapAssessments,
                gapHash);
        }

        /// <summary>
        /// Solo contratto alias-2 (T136, §13.2/§13.4): verifica forma e riferimenti dei record `gap-assessment-1` contro l'input assegnato e le chiavi dell'output (un record
        /// mancante NON è un rifiuto: diventa <c>gap_not_assessed</c> nella completezza), applica le coerenze verificabili e restituisce il contesto della completezza v5.
        /// Il server non valuta mai pertinenza, prossimità o correttezza del riscontro, e non aggiunge alcun supporto: gli item restano quelli dichiarati dal modello.
        /// </summary>
        private GapAssessmentContext? GapAssessments(List<(string Name, List<ItemRecord> Items)> sections, List<(AiConflict Dto, Guid Id, List<ItemRecord> Alternatives)> conflicts)
        {
            if (gapAssessments is null)
            {
                return null;
            }

            var assigned = GapAssessmentRules.Assigned(catalog);
            var openQuestionKeys = sections.Single(s => s.Name == "openQuestions").Items.Select(i => i.Key).ToList();
            _errors.AddRange(GapAssessmentRules.Check(gapAssessments, assigned, openQuestionKeys, [.. dto.Conflicts.Select(c => c.Key)], allowMissing: true));
            Coherence(sections, conflicts);
            var gapIds = catalog.Data.Evidence.Where(e => e.Kind == EvidenceKind.Gap).Select(e => e.EvidenceId).ToHashSet();
            var assessed = gapAssessments.Select(r => catalog.Evidence(r.Gap)).Where(e => e is { Kind: EvidenceKind.Gap }).Select(e => e!.EvidenceId).ToHashSet();
            return new GapAssessmentContext(gapIds, assessed, GapAssessmentRules.Hash(gapAssessments));
        }

        /// <summary>
        /// §13.4, solo alias-2. (1) Un finding esplicito <c>Contradicts</c>/<c>AlternativeTo</c> con esito Open richiede un conflitto che citi, nelle sue alternative, entrambe le
        /// evidenze endpoint (le fonti seguono dalle evidenze citate): il server verifica il collegamento ma non crea il conflitto. (2) Un item ordinario identico a un'alternativa
        /// (stesso testo dopo la sola normalizzazione dei newline, stessa classificazione, stesso insieme di EvidenceId) è rifiutato. Nessun fuzzy matching né giudizio semantico:
        /// parafrasi e contraddizioni non dichiarate restano al gate semantico.
        /// </summary>
        private void Coherence(List<(string Name, List<ItemRecord> Items)> sections, List<(AiConflict Dto, Guid Id, List<ItemRecord> Alternatives)> conflicts)
        {
            for (var i = 0; i < dto.Findings.Count; i++)
            {
                var f = dto.Findings[i];
                if (f.Outcome != AiFindingOutcome.Open || f.Type is not (EvidenceRelationType.Contradicts or EvidenceRelationType.AlternativeTo) || f.Left == f.Right
                    || catalog.Evidence(f.Left) is null || catalog.Evidence(f.Right) is null)
                {
                    continue;
                }

                var represented = conflicts.Any(c =>
                {
                    var cited = c.Alternatives.SelectMany(a => a.Evidence).Select(e => e.Alias).ToHashSet(StringComparer.Ordinal);
                    return cited.Contains(f.Left) && cited.Contains(f.Right);
                });
                if (!represented)
                {
                    _errors.Add($"findings[{i}]: finding {f.Type} con esito Open senza un conflitto che citi nelle alternative entrambe le evidenze {f.Left} e {f.Right} (conflitto non rappresentato).");
                }
            }

            var alternatives = conflicts.SelectMany(c => c.Alternatives).ToList();
            foreach (var ordinary in sections.SelectMany(s => s.Items))
            {
                var text = NormalizeNewlines(ordinary.Dto.Text);
                var ids = ordinary.Evidence.Select(e => e.EvidenceId).ToHashSet();
                foreach (var alternative in alternatives.Where(a => a.Dto.Classification == ordinary.Dto.Classification && NormalizeNewlines(a.Dto.Text) == text && a.Evidence.Select(e => e.EvidenceId).ToHashSet().SetEquals(ids)))
                {
                    _errors.Add($"{ordinary.Path}: l'elemento {ordinary.Key} duplica l'alternativa {alternative.Key} ({alternative.Path}): stesso testo, classificazione ed evidenze; le alternative di un conflitto vanno solo in conflicts[].alternatives.");
                }
            }
        }

        private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        private void DuplicateKey(string key, string error)
        {
            _errors.Add(error);
            _duplicateKeys.Add(key);
        }

        private List<string> Visited(IReadOnlyList<string> aliases, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            for (var i = 0; i < aliases.Count; i++)
            {
                if (!seen.Add(aliases[i]))
                {
                    _errors.Add($"{path}[{i}]: alias {aliases[i]} duplicato.");
                }
                else if (Evidence(aliases[i], $"{path}[{i}]") is { } entry)
                {
                    result.Add(entry.EntryId);
                }
            }

            return [.. result.Order(StringComparer.Ordinal)];
        }

        private AliasEvidenceEntry? Evidence(string alias, string path)
        {
            var entry = catalog.Evidence(alias);
            if (entry is null)
            {
                _errors.Add($"{path}: alias {alias} sconosciuto o fuori snapshot.");
            }

            return entry;
        }

        private List<AliasEvidenceEntry> EvidenceList(IReadOnlyList<string> aliases, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<AliasEvidenceEntry>();
            for (var i = 0; i < aliases.Count; i++)
            {
                if (!seen.Add(aliases[i]))
                {
                    _errors.Add($"{path}[{i}]: alias {aliases[i]} duplicato.");
                }
                else if (Evidence(aliases[i], $"{path}[{i}]") is { } entry)
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        private ItemRecord Item(AiItem item, string path, Dictionary<string, string> keys)
        {
            if (!keys.TryAdd(item.Key, path))
            {
                // Mai deduplicato: il rifiuto nomina chiave e posizioni, perché la correzione (una sola sezione; le alternative solo nel conflitto) spetta al modello.
                var first = keys[item.Key];
                var hint = first.StartsWith("conflicts[", StringComparison.Ordinal) || path.StartsWith("conflicts[", StringComparison.Ordinal)
                    ? "le alternative di un conflitto vanno solo in conflicts[].alternatives, non anche come item di una sezione"
                    : "ogni elemento compare una sola volta, in una sola sezione";
                DuplicateKey(item.Key, $"{path}.key: chiave locale {item.Key} duplicata: già usata in {first} e ripetuta in {path} ({hint}).");
            }

            return new ItemRecord(item.Key, path, item, CanonicalId("item", _root, _sourceHash, item.Key), EvidenceList(item.Evidence, $"{path}.evidence"));
        }

        private DocumentAnalysisItem ToCanonical(ItemRecord record) => new()
        {
            Id = record.Id, Text = record.Dto.Text, Classification = record.Dto.Classification, EvidenceRefs = SourceIdsInCitationOrder(record.Evidence),
        };

        private IReadOnlyList<DocumentAnalysisItem> Items(List<(string Name, List<ItemRecord> Items)> sections, string name) =>
            [.. sections.Single(s => s.Name == name).Items.Select(ToCanonical)];

        private IReadOnlyList<Guid> SourceIdsInCitationOrder(List<AliasEvidenceEntry> evidence) =>
            [.. evidence.SelectMany(catalog.SourcesOf).Select(s => s.SourceId).Distinct()];

        private static IReadOnlyList<Guid> SortedEvidenceIds(IEnumerable<AliasEvidenceEntry> evidence) =>
            [.. ReconciliationIds.SortedIds(evidence.Select(e => e.EvidenceId).Distinct()).Select(Guid.Parse)];

        private IReadOnlyList<Guid> SortedSourceIds(IEnumerable<AliasEvidenceEntry> evidence) =>
            [.. ReconciliationIds.SortedIds(evidence.SelectMany(catalog.SourcesOf).Select(s => s.SourceId).Distinct()).Select(Guid.Parse)];

        private DocumentAnalysisSource Source(AliasSourceEntry s) => s.Provenance == EvidenceSourceKind.Message
            ? new DocumentAnalysisSource
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.Message, MessageId = s.MessageId, RevisionId = null, DocumentId = null, DocumentVersionId = null,
                PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
                MessageRole = input.Dialog.First(m => m.Id == s.MessageId).Role,
            }
            : new DocumentAnalysisSource
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = s.DocumentId,
                DocumentVersionId = s.DocumentVersionId, PreparationId = s.PreparationId, ArtifactId = s.ArtifactId, BlockId = s.BlockId, Start = s.Start, End = s.End, Quote = s.Quote,
            };

        private static CandidateEndpoint Endpoint(AliasEvidenceEntry e) => new() { Kind = CandidateEndpointKind.Evidence, Id = e.EvidenceId.ToString("D"), Hash = e.ContentHash };

        private static CandidateKind KindOf(EvidenceRelationType type) => type is EvidenceRelationType.Contradicts or EvidenceRelationType.AlternativeTo ? CandidateKind.Conflict : CandidateKind.Relation;

        private static ReconcileProposedType ProposedOf(EvidenceRelationType type) => Enum.Parse<ReconcileProposedType>(type.ToString());

        private void AddCandidate(
            Dictionary<string, string> ids, Dictionary<ReconcileView, List<(ReconcileCandidate, CandidateDisposition)>> perView, ReconcileView view, string path, AliasEvidenceEntry left,
            AliasEvidenceEntry right, EvidenceRelationType type, EvidenceRelationBasis basis, string rationale, CandidateState state, string? reason, string aiHash)
        {
            var kind = KindOf(type);
            var proposed = ProposedOf(type);
            var l = Endpoint(left);
            var r = Endpoint(right);
            var id = ReconciliationIds.CandidateId(kind, l, r, proposed, _sourceHash);
            if (!ids.TryAdd(id, path))
            {
                _errors.Add($"{path}: candidato duplicato (stessa identità canonica di {ids[id]}).");
                return;
            }

            perView[view].Add((
                new ReconcileCandidate
                {
                    CandidateId = id, Kind = kind, Left = l, Right = r, ProposedType = proposed, Rationale = rationale, Basis = basis, OriginBatchIds = [batchKey], ParentCandidateId = null,
                },
                new CandidateDisposition { CandidateId = id, State = state, ChildCandidateIds = [], ComparisonOutputHashes = [aiHash], Reason = reason }));
        }

        private void Findings(string aiHash, Dictionary<string, string> candidateIds, Dictionary<ReconcileView, List<(ReconcileCandidate, CandidateDisposition)>> perView)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.Findings.Count; i++)
            {
                var f = dto.Findings[i];
                var path = $"findings[{i}]";
                if (!keys.Add(f.Key))
                {
                    DuplicateKey(f.Key, $"{path}.key: chiave locale {f.Key} duplicata.");
                }

                var left = Evidence(f.Left, $"{path}.left");
                var right = Evidence(f.Right, $"{path}.right");
                if (f.Left == f.Right)
                {
                    _errors.Add($"{path}: endpoint coincidenti ({f.Left}).");
                }

                if (f.Outcome == AiFindingOutcome.Open && string.IsNullOrWhiteSpace(f.Reason))
                {
                    _errors.Add($"{path}.reason: obbligatoria quando outcome è Open.");
                }

                if (left is not null && right is not null && f.Left != f.Right)
                {
                    AddCandidate(
                        candidateIds, perView, f.View, path, left, right, f.Type, f.Basis, f.Rationale,
                        f.Outcome == AiFindingOutcome.Open ? CandidateState.Open : CandidateState.Compared, f.Reason, aiHash);
                }
            }
        }

        private void KnownRelationChecks(string aiHash, Dictionary<string, string> candidateIds, Dictionary<ReconcileView, List<(ReconcileCandidate, CandidateDisposition)>> perView)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.KnownRelationChecks.Count; i++)
            {
                var check = dto.KnownRelationChecks[i];
                var path = $"knownRelationChecks[{i}]";
                var relation = catalog.Relation(check.Relation);
                if (relation is null)
                {
                    _errors.Add($"{path}.relation: alias {check.Relation} sconosciuto o fuori snapshot.");
                    continue;
                }

                if (!seen.Add(check.Relation))
                {
                    _errors.Add($"{path}.relation: relazione {check.Relation} duplicata.");
                    continue;
                }

                var (state, reason) = check.Verdict switch
                {
                    AiKnownRelationVerdict.Confirmed => (CandidateState.Compared, (string?)null),
                    AiKnownRelationVerdict.NoRelation => (CandidateState.Compared, "no_relation"),
                    _ => (CandidateState.Open, check.Rationale),
                };
                AddCandidate(
                    candidateIds, perView, ReconcileView.Cross, path, catalog.Evidence(relation.FromAlias)!, catalog.Evidence(relation.ToAlias)!, relation.Type, relation.Basis,
                    check.Rationale, state, reason, aiHash);
            }
        }

        private sealed record PatchDraft(string Path, AiPatch Dto, PatchTargetKind TargetKind, string TargetId, List<AliasEvidenceEntry> Evidence, PatchValue Value);

        private List<PatchDraft> PatchDrafts(HashSet<string> proposalKeys)
        {
            var drafts = new List<PatchDraft>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.Patches.Count; i++)
            {
                var p = dto.Patches[i];
                var path = $"patches[{i}]";
                if (!keys.Add(p.Key))
                {
                    DuplicateKey(p.Key, $"{path}.key: chiave locale {p.Key} duplicata.");
                }

                var evidence = EvidenceList(p.Evidence, $"{path}.evidence");
                var hasItem = p.Target.Item is not null;
                var hasRelation = p.Target.Relation is not null;
                PatchTargetKind? kind = null;
                var targetId = string.Empty;
                if (hasItem == hasRelation)
                {
                    _errors.Add($"{path}.target: esattamente uno fra item e relation.");
                }
                else if (hasItem)
                {
                    kind = PatchTargetKind.Item;
                    targetId = p.Target.Item!;
                    if (!proposalKeys.Contains(targetId))
                    {
                        _errors.Add($"{path}.target.item: chiave {targetId} inesistente (nessun item con evidenze con questa chiave).");
                    }
                }
                else
                {
                    kind = PatchTargetKind.Relation;
                    var relation = catalog.Relation(p.Target.Relation!);
                    if (relation is null)
                    {
                        _errors.Add($"{path}.target.relation: alias {p.Target.Relation} sconosciuto o fuori snapshot.");
                    }
                    else
                    {
                        targetId = relation.RelationId.ToString("D");
                    }
                }

                PatchValue? value = null;
                switch (p.Op, p.Value)
                {
                    case (PatchOperation.AddSupport, AiAddSupportValue):
                        if (p.Evidence.Count == 0)
                        {
                            _errors.Add($"{path}.evidence: obbligatoria per AddSupport.");
                        }

                        value = new AddSupportValue { EvidenceIds = SortedEvidenceIds(evidence), SourceIds = SortedSourceIds(evidence) };
                        break;
                    case (PatchOperation.AddRelation, AiAddRelationValue relationValue):
                        if (kind == PatchTargetKind.Relation)
                        {
                            _errors.Add($"{path}.target: AddRelation ancora un item esistente, non una relazione.");
                        }

                        var fromEvidence = Evidence(relationValue.From, $"{path}.value.from");
                        var toEvidence = Evidence(relationValue.To, $"{path}.value.to");
                        if (relationValue.From == relationValue.To)
                        {
                            _errors.Add($"{path}.value: endpoint coincidenti ({relationValue.From}).");
                        }

                        if (fromEvidence is not null && toEvidence is not null && relationValue.From != relationValue.To)
                        {
                            var endpoints = new[] { relationValue.From, relationValue.To }.Order(StringComparer.Ordinal).ToList();
                            if (p.Evidence.Count > 0 && !p.Evidence.Order(StringComparer.Ordinal).SequenceEqual(endpoints, StringComparer.Ordinal))
                            {
                                _errors.Add($"{path}.evidence: per AddRelation vuota oppure esattamente gli endpoint {relationValue.From} e {relationValue.To}.");
                            }

                            evidence = [fromEvidence, toEvidence];
                            value = new AddRelationValue
                            {
                                FromEvidenceId = fromEvidence.EvidenceId, ToEvidenceId = toEvidence.EvidenceId, Type = relationValue.Type, Basis = relationValue.Basis, Rationale = relationValue.Rationale,
                            };
                        }

                        break;
                    case (PatchOperation.ProposeAlternative, AiProposeAlternativeValue alternative):
                        if (p.Evidence.Count == 0)
                        {
                            _errors.Add($"{path}.evidence: obbligatoria per ProposeAlternative.");
                        }

                        value = new ProposeAlternativeValue
                        {
                            Text = alternative.Text, Classification = alternative.Classification, EvidenceIds = SortedEvidenceIds(evidence), SourceIds = SortedSourceIds(evidence),
                            Reason = alternative.Reason,
                        };
                        break;
                    default:
                        _errors.Add($"{path}.value: non coerente con op {p.Op}.");
                        break;
                }

                if (value is not null && kind is not null && targetId.Length > 0)
                {
                    drafts.Add(new PatchDraft(path, p, kind.Value, targetId, evidence, value));
                }
            }

            return drafts;
        }

        private List<Patch> StampPatches(List<PatchDraft> drafts, ReconciliationBase baseState)
        {
            var patches = new List<Patch>();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in drafts)
            {
                if (!baseState.TargetHashes.TryGetValue(ReconciliationBase.TargetKey(d.TargetKind, d.TargetId), out var expected))
                {
                    _errors.Add($"{d.Path}.target: {d.TargetKind} {d.TargetId} assente dalla base del passaggio.");
                    continue;
                }

                var patch = new Patch
                {
                    PatchId = "pending", BaseHash = baseState.BaseHash, Target = new PatchTarget { Kind = d.TargetKind, Id = d.TargetId, ExpectedHash = expected },
                    Operation = d.Dto.Op, Value = d.Value, EvidenceIds = SortedEvidenceIds(d.Evidence), SourceIds = SortedSourceIds(d.Evidence), OriginBatchIds = [batchKey],
                };
                patch = patch with { PatchId = ReconciliationIds.PatchId(patch) };
                if (!ids.TryAdd(patch.PatchId, d.Path))
                {
                    _errors.Add($"{d.Path}: patch duplicata (stessa identità canonica di {ids[patch.PatchId]}).");
                    continue;
                }

                patches.Add(patch);
            }

            return patches;
        }
    }
}
