using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Compositore deterministico alias → canonico `reconcile-1` PER BATCH (contracts/ai-workflow.md §11, T131). Risolve gli alias del DTO `reconcile-alias-1` contro il
// catalogo del batch, deriva batchId, inputHash, visited*, candidateId, patchId, baseHash, expectedHash, sourceIds, originBatchIds e gli id canonici `canon-ids-1`
// (questi ultimi nel solo assembly, con <see cref="AnalysisAliasComposer.CanonicalId"/>) e produce il <see cref="ReconcileResult"/> che il motore condiviso
// (ReconciliationHierarchy/Candidates/FinalPass) valida con le regole di sempre, mai allentate. Non prende decisioni semantiche: compone solo ciò che il modello ha
// dichiarato in modo esplicito. Alias sconosciuti, fuori dal batch, duplicati o malformati sono rifiutati (§7.6): nessuna normalizzazione né ricerca fuzzy.

/// <summary>Item senza evidenze ammesso solo come domanda aperta (sezione openQuestions): non è una proposta del motore e non entra nella mappa delle evidenze.</summary>
public sealed record UngroundedAliasItem(string LocalKey, AiSection Section, string Text, DocumentAnalysisClassification Classification);

/// <summary>
/// Classe di una violazione del compositore (reconciliation-hardening RH-013): SOLO diagnostica, nessun retry né riparazione (opzione differita O1).
/// <see cref="Undecodable"/> = errore di deserializzazione del DTO (<see cref="JsonException"/>, comprese le incoerenze <c>op</c>/<c>value</c> delle patch, che <c>AiPatchConverter</c> rifiuta in decodifica);
/// <see cref="PatchShape"/> = violazione SEMANTICA del compositore su un DTO già decodificato (target non esclusivo, evidenze mancanti o diverse dagli estremi, bersaglio assente dalla base).
/// </summary>
public enum ReconcileViolationKind
{
    Undecodable,
    UnknownAlias,
    CoincidentEndpoints,
    Duplicate,
    DuplicateCandidate,
    InvalidText,
    PatchShape,
    ItemShape,
    StageShape,

    /// <summary>Solo al replay: il canonico ricomposto ha un hash diverso da quello committato (<c>integrity_conflict</c>). Non è una violazione del modello né una decodifica: nessuna riga della tabella di etichettatura.</summary>
    IntegrityMismatch,
}

/// <summary>Violazione tipizzata: <paramref name="Message"/> è il messaggio storico, identico a quello di <see cref="ReconcileAliasComposition.Violations"/>; <paramref name="Path"/> è il percorso nel DTO.</summary>
public sealed record ReconcileViolation(ReconcileViolationKind Kind, string Path, string Message);

/// <summary>
/// Opzioni del compositore. <see cref="StageContract"/> (solo definizione v11, <see cref="ReconcileAliasContracts.StageContract"/>): contratto per stadio, che aggiunge tre controlli di difesa in profondità
/// rispetto alla grammatica dello schema — candidati nel confronto (<c>StageShape</c>), <c>descriptor</c> di soli spazi o oltre 512 caratteri e testi di soli spazi nelle patch (<c>InvalidText</c>). Senza opzioni
/// (v4–v10) il compositore è quello di sempre, con gli stessi messaggi.
/// </summary>
public sealed record ReconcileComposeOptions(string? StageContract = null);

/// <summary>
/// Esito della composizione di un batch. <see cref="Result"/> è valorizzato solo se l'output è composto; un rifiuto porta le violazioni (alias e percorso) e il codice permanente.
/// <see cref="Sections"/> associa la chiave locale canonica di ogni proposta alla sezione dichiarata dal modello (il canonico <c>reconcile-1</c> non ha sezioni).
/// <see cref="TypedViolations"/> ha un elemento per ogni messaggio di <see cref="Violations"/>, nello stesso ordine: è vuota se e solo se <see cref="Violations"/> lo è.
/// </summary>
public sealed record ReconcileAliasComposition(
    string? ErrorCode, IReadOnlyList<string> Violations, ReconcileResult? Result, string? AiOutputHash, string CatalogHash, string? CanonicalHash,
    IReadOnlyDictionary<string, AiSection> Sections, IReadOnlyList<UngroundedAliasItem> Ungrounded, IReadOnlyList<ReconcileViolation> TypedViolations)
{
    public bool IsValid => Result is not null;
}

public static class ReconcileAliasComposer
{
    /// <summary>`canonicalHash`: SHA-256 della forma canonica del <see cref="ReconcileResult"/> composto.</summary>
    public static string CanonicalHash(ReconcileResult result) => CanonicalJson.Sha256Hex(ReconciliationCodec.Encode(result));

    /// <summary>Chiave locale canonica di un item: la chiave del modello è locale al batch, quella del server è unica nel run (stadio, vista e ordinale della foglia).</summary>
    public static string LocalKey(ReconcileBatchRequest request, string key) => $"{request.Batch.Stage}-{request.View}{request.Batch.Ordinal}.{key}";

    /// <summary>
    /// Decodifica strict del DTO e composizione. <paramref name="baseState"/> è la base immutabile del passaggio (proposte delle foglie e relazioni note), nota solo dopo le foglie:
    /// senza di essa nessuna patch può essere firmata. <paramref name="expectedCanonicalHash"/> (replay) verifica che il canonico ricomposto sia quello committato.
    /// <paramref name="options"/> (v11) attiva i controlli del contratto per stadio; <c>null</c> è il compositore storico.
    /// </summary>
    public static ReconcileAliasComposition Compose(
        string aiJson, ReconcileAliasCatalog catalog, ReconcileBatchRequest request, ReconciliationSnapshot snapshot, ReconciliationBase? baseState = null, string? expectedCanonicalHash = null,
        ReconcileComposeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (options?.StageContract is { } contract && contract != ReconcileAliasContracts.StageContract)
        {
            throw new ArgumentOutOfRangeException(nameof(options), contract, "Contratto per stadio non supportato dal compositore.");
        }

        AiReconcileOutput dto;
        try
        {
            dto = ReconcileAliasContracts.Decode(aiJson);
        }
        catch (JsonException e)
        {
            return Reject(catalog, null, DocumentWorkException.WorkOutputInvalid, [new(ReconcileViolationKind.Undecodable, "$", $"JSON non conforme a {ReconcileAliasContracts.SchemaVersion}: {e.Message}")]);
        }

        return new Builder(dto, catalog, request, snapshot, baseState, options?.StageContract is not null).Run(expectedCanonicalHash);
    }

    private static ReconcileAliasComposition Reject(ReconcileAliasCatalog catalog, string? aiHash, string code, IReadOnlyList<ReconcileViolation> violations)
    {
        // Un solo elemento per messaggio distinto (primo in ordine di emissione): le stringhe storiche e le violazioni tipizzate hanno la stessa lunghezza e lo stesso ordine.
        var typed = violations.DistinctBy(v => v.Message, StringComparer.Ordinal).ToList();
        return new(code, [.. typed.Select(v => v.Message)], null, aiHash, catalog.CatalogHash, null, new Dictionary<string, AiSection>(), [], typed);
    }

    private sealed class Builder(
        AiReconcileOutput dto, ReconcileAliasCatalog catalog, ReconcileBatchRequest request, ReconciliationSnapshot snapshot, ReconciliationBase? baseState, bool stageContract)
    {
        private readonly List<ReconcileViolation> _errors = [];
        private readonly string _stage = request.Batch.Stage;
        private readonly string _batchId = request.Batch.BatchId;

        private bool LeafA => _stage == "leaf" && request.View == ReconcileView.A;

        private bool CandidateStage => _stage is "expand" or "compare";

        private void Fail(ReconcileViolationKind kind, string path, string message) => _errors.Add(new(kind, path, message));

        public ReconcileAliasComposition Run(string? expectedCanonicalHash)
        {
            var aiHash = ReconcileAliasContracts.Hash(dto);
            if (stageContract)
            {
                // Difesa in profondità (v11): la grammatica non può esprimere la lunghezza massima né i soli spazi del descriptor (`\S` non è supportato dal convertitore di llama.cpp).
                if (dto.Descriptor is not null && (string.IsNullOrWhiteSpace(dto.Descriptor) || dto.Descriptor.EnumerateRunes().Count() > 512))
                {
                    Fail(ReconcileViolationKind.InvalidText, "descriptor", "descriptor: non vuoto e al massimo 512 caratteri Unicode.");
                }
            }

            var (proposals, sections, ungrounded) = Items();
            var candidates = Candidates();
            var dispositions = Focus(aiHash);
            var drafts = PatchDrafts();
            var patches = Stamp(drafts);
            if (_errors.Count > 0)
            {
                return Reject(catalog, aiHash, DocumentWorkException.WorkOutputInvalid, _errors);
            }

            var result = new ReconcileResult
            {
                BatchId = _batchId, InputHash = request.Batch.InputHash, View = request.View, Level = request.Level,
                VisitedEntryIds = [.. request.Entries.Select(e => e.EntryId).Order(StringComparer.Ordinal)],
                VisitedNodeIds = [.. request.Nodes.Select(n => n.NodeId).Order(StringComparer.Ordinal)],
                Candidates = candidates, Dispositions = dispositions, ItemProposals = proposals, Patches = patches, Descriptor = dto.Descriptor, NoFindingsReason = dto.NoFindings,
            };
            var canonicalHash = CanonicalHash(result);
            if (expectedCanonicalHash is not null && expectedCanonicalHash != canonicalHash)
            {
                return Reject(catalog, aiHash, DocumentWorkException.IntegrityConflict, [new(ReconcileViolationKind.IntegrityMismatch, "canonicalHash", "canonicalHash: il canonico ricomposto è diverso da quello committato.")]);
            }

            return new ReconcileAliasComposition(null, [], result, aiHash, catalog.CatalogHash, canonicalHash, sections, ungrounded, []);
        }

        private List<ReconcileCatalogEntry> EvidenceList(IReadOnlyList<string> aliases, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ReconcileCatalogEntry>();
            for (var i = 0; i < aliases.Count; i++)
            {
                if (!seen.Add(aliases[i]))
                {
                    Fail(ReconcileViolationKind.Duplicate, $"{path}[{i}]", $"{path}[{i}]: alias {aliases[i]} duplicato.");
                }
                else if (catalog.Entry(aliases[i]) is { } entry)
                {
                    result.Add(entry);
                }
                else
                {
                    Fail(ReconcileViolationKind.UnknownAlias, $"{path}[{i}]", $"{path}[{i}]: alias {aliases[i]} sconosciuto o fuori dal batch.");
                }
            }

            return result;
        }

        private IReadOnlyList<Guid> SortedEvidenceIds(IEnumerable<ReconcileCatalogEntry> evidence) =>
            [.. ReconciliationIds.SortedIds(evidence.Select(e => e.EvidenceId).Distinct()).Select(Guid.Parse)];

        private IReadOnlyList<Guid> SortedSourceIds(IEnumerable<ReconcileCatalogEntry> evidence)
        {
            var ids = evidence.Select(e => e.EvidenceId).ToHashSet();
            return [.. ReconciliationIds.SortedIds(request.Entries.Where(e => ids.Contains(e.EvidenceId)).SelectMany(e => e.SourceIds).Distinct()).Select(Guid.Parse)];
        }

        private (List<ItemProposal> Proposals, Dictionary<string, AiSection> Sections, List<UngroundedAliasItem> Ungrounded) Items()
        {
            var proposals = new List<ItemProposal>();
            var sections = new Dictionary<string, AiSection>(StringComparer.Ordinal);
            var ungrounded = new List<UngroundedAliasItem>();
            if (dto.Items.Count > 0 && !LeafA)
            {
                Fail(ReconcileViolationKind.StageShape, "items", $"items: ammessi solo nelle foglie della vista A (stadio {_stage}, vista {request.View}); le altre viste e gli altri stadi non propongono item.");
                return (proposals, sections, ungrounded);
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.Items.Count; i++)
            {
                var item = dto.Items[i];
                var path = $"items[{i}]";
                if (!keys.Add(item.Key))
                {
                    Fail(ReconcileViolationKind.Duplicate, $"{path}.key", $"{path}.key: chiave locale {item.Key} duplicata: ogni chiave si usa una sola volta nel batch.");
                }

                var evidence = EvidenceList(item.Evidence, $"{path}.evidence");
                var localKey = LocalKey(request, item.Key);
                if (string.IsNullOrWhiteSpace(item.Text))
                {
                    Fail(ReconcileViolationKind.InvalidText, $"{path}.text", $"{path}.text: non vuoto.");
                }

                // Stesse regole di AnalysisContent 1.1 (DocumentAnalysisContentRules), applicate subito al batch: un errore di classificazione non deve emergere solo all'assemblaggio,
                // dopo tutte le chiamate. Declared richiede evidenze di un messaggio del PM, Documented di un documento, assumptions solo Assumed.
                if (item.Section == AiSection.Assumptions && item.Classification != DocumentAnalysisClassification.Assumed)
                {
                    Fail(ReconcileViolationKind.ItemShape, $"{path}.classification", $"{path}.classification: gli elementi di assumptions sono Assumed.");
                }

                if (item.Evidence.Count > 0 && item.Classification == DocumentAnalysisClassification.Declared && !evidence.Any(e => e.Origin == EvidenceOriginKind.Message))
                {
                    Fail(ReconcileViolationKind.ItemShape, $"{path}.classification", $"{path}.classification: Declared richiede almeno un'evidenza con origine Message (dichiarazione del project manager).");
                }

                if (item.Evidence.Count > 0 && item.Classification == DocumentAnalysisClassification.Documented && !evidence.Any(e => e.Origin == EvidenceOriginKind.Document))
                {
                    Fail(ReconcileViolationKind.ItemShape, $"{path}.classification", $"{path}.classification: Documented richiede almeno un'evidenza con origine Document.");
                }

                if (item.Evidence.Count == 0)
                {
                    if (item.Section != AiSection.OpenQuestions)
                    {
                        Fail(ReconcileViolationKind.ItemShape, $"{path}.evidence", $"{path}.evidence: obbligatoria (solo una domanda aperta in openQuestions può non citare evidenze).");
                    }
                    else
                    {
                        ungrounded.Add(new(localKey, item.Section, item.Text, item.Classification));
                    }

                    continue;
                }

                proposals.Add(new ItemProposal
                {
                    LocalKey = localKey, Text = item.Text, Classification = item.Classification, EvidenceIds = SortedEvidenceIds(evidence), SourceIds = SortedSourceIds(evidence),
                });
                sections[localKey] = item.Section;
            }

            return (proposals, sections, ungrounded);
        }

        private CandidateEndpoint? Endpoint(string alias, string path)
        {
            // Foglie, espansione e confronto collegano evidenze; riduzione e cross collegano nodi.
            if ((alias[0] == 'N') == (_stage is "leaf" or "expand" or "compare"))
            {
                Fail(ReconcileViolationKind.StageShape, path, $"{path}: l'estremo {alias} non è ammesso nello stadio {_stage} (gli estremi sono {(_stage is "leaf" or "expand" or "compare" ? "evidenze E<n>" : "nodi N<n>")}).");
                return null;
            }

            if (alias[0] == 'E')
            {
                if (catalog.Entry(alias) is not { } entry)
                {
                    Fail(ReconcileViolationKind.UnknownAlias, path, $"{path}: alias {alias} sconosciuto o fuori dal batch.");
                    return null;
                }

                return new CandidateEndpoint { Kind = CandidateEndpointKind.Evidence, Id = entry.EvidenceId.ToString("D"), Hash = entry.ContentHash };
            }

            if (catalog.Node(alias) is not { } node)
            {
                Fail(ReconcileViolationKind.UnknownAlias, path, $"{path}: alias {alias} sconosciuto o fuori dal batch.");
                return null;
            }

            return new CandidateEndpoint { Kind = node.Kind, Id = node.Id, Hash = node.Hash };
        }

        private List<ReconcileCandidate> Candidates()
        {
            var candidates = new List<ReconcileCandidate>();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            // v11 (D4): il confronto non ha candidati. La grammatica lo impone già (`candidates.maxItems = 0`); qui lo si ripete come difesa in profondità (output senza schema o provider che lo ignora):
            // un candidato nel confronto non è mai composto, quindi non può essere risolto per cache con la disposizione del padre (I-3). Il dubbio si esprime solo con `focus` Open e il motivo.
            if (stageContract && _stage == "compare" && dto.Candidates.Count > 0)
            {
                Fail(ReconcileViolationKind.StageShape, "candidates", $"candidates: il confronto non ammette candidati ({dto.Candidates.Count} ricevuti): il dubbio si esprime solo con focus Open e il motivo.");
                return candidates;
            }

            // v10 (membership-1): il tetto del profilo -3 è una protezione. La grammatica lo impone già; qui lo si ripete come difesa in profondità (output senza schema o provider che lo ignora):
            // oltre il tetto è un rifiuto, mai un troncamento. Un'espansione che RAGGIUNGE il tetto è valida ma satura (lo decide il motore, che non dichiara la copertura).
            if (_stage == "expand" && request.Groups is not null && dto.Candidates.Count > ReconcileAliasContracts.ExpansionCandidateCap)
            {
                Fail(ReconcileViolationKind.StageShape, "candidates", $"candidates: {dto.Candidates.Count} candidati oltre il tetto di {ReconcileAliasContracts.ExpansionCandidateCap} dell'espansione (nessun troncamento: output non valido).");
            }

            for (var i = 0; i < dto.Candidates.Count; i++)
            {
                var c = dto.Candidates[i];
                var path = $"candidates[{i}]";
                if (!keys.Add(c.Key))
                {
                    Fail(ReconcileViolationKind.Duplicate, $"{path}.key", $"{path}.key: chiave locale {c.Key} duplicata.");
                }

                var left = Endpoint(c.Left, $"{path}.left");
                var right = Endpoint(c.Right, $"{path}.right");
                if (c.Left == c.Right)
                {
                    Fail(ReconcileViolationKind.CoincidentEndpoints, path, $"{path}: endpoint coincidenti ({c.Left}).");
                }

                if (string.IsNullOrWhiteSpace(c.Rationale))
                {
                    Fail(ReconcileViolationKind.InvalidText, $"{path}.rationale", $"{path}.rationale: non vuota.");
                }

                if (left is null || right is null || c.Left == c.Right)
                {
                    continue;
                }

                var kind = c.Type == ReconcileProposedType.Alias ? CandidateKind.Alias
                    : c.Type is ReconcileProposedType.Contradicts or ReconcileProposedType.AlternativeTo ? CandidateKind.Conflict : CandidateKind.Relation;
                var id = ReconciliationIds.CandidateId(kind, left, right, c.Type, snapshot.SourceHash);
                if (!ids.TryAdd(id, path))
                {
                    Fail(ReconcileViolationKind.DuplicateCandidate, path, $"{path}: candidato duplicato (stessa identità canonica di {ids[id]}).");
                    continue;
                }

                candidates.Add(new ReconcileCandidate
                {
                    CandidateId = id, Kind = kind, Left = left, Right = right, ProposedType = c.Type, Rationale = c.Rationale, Basis = c.Basis, OriginBatchIds = [_batchId],
                    ParentCandidateId = CandidateStage ? catalog.Focus?.CandidateId : null,
                });
            }

            return candidates;
        }

        /// <summary>Candidato in esame: il confronto dichiara Compared/Open, l'espansione solo «non risolto» (disposition fissa NotProcessed candidate_unresolved); altrove nessun focus.</summary>
        private List<CandidateDisposition> Focus(string aiHash)
        {
            var focus = dto.Focus;
            var result = new List<CandidateDisposition>();
            if (_stage == "compare")
            {
                if (focus is null || focus.Outcome == AiFocusOutcome.Unresolved)
                {
                    Fail(ReconcileViolationKind.StageShape, "focus", "focus: il confronto deve dichiarare Compared oppure Open per il candidato in esame.");
                }
                else if (focus.Outcome == AiFocusOutcome.Open && string.IsNullOrWhiteSpace(focus.Reason))
                {
                    Fail(ReconcileViolationKind.InvalidText, "focus.reason", "focus.reason: obbligatorio e non vuoto quando l'esito è Open.");
                }
                else if (catalog.Focus is { } candidate)
                {
                    result.Add(new CandidateDisposition
                    {
                        CandidateId = candidate.CandidateId, State = focus.Outcome == AiFocusOutcome.Open ? CandidateState.Open : CandidateState.Compared, ChildCandidateIds = [],
                        ComparisonOutputHashes = [aiHash], Reason = string.IsNullOrWhiteSpace(focus.Reason) ? null : focus.Reason,
                    });
                }
            }
            else if (_stage == "expand")
            {
                if (focus is { Outcome: not AiFocusOutcome.Unresolved })
                {
                    Fail(ReconcileViolationKind.StageShape, "focus", "focus: l'espansione è una visita, non un confronto: solo null oppure Unresolved (nessun Compared né Open).");
                }
                else if (focus is not null && catalog.Focus is { } candidate)
                {
                    result.Add(new CandidateDisposition
                    {
                        CandidateId = candidate.CandidateId, State = CandidateState.NotProcessed, ChildCandidateIds = [], ComparisonOutputHashes = [],
                        Reason = ReconciliationCandidates.CandidateUnresolved,
                    });
                }
            }
            else if (focus is not null)
            {
                Fail(ReconcileViolationKind.StageShape, "focus", $"focus: previsto solo negli stadi di espansione e confronto (stadio {_stage}): null.");
            }

            return result;
        }

        private sealed record PatchDraft(string Path, AiPatch Dto, PatchTargetKind TargetKind, string TargetId, List<ReconcileCatalogEntry> Evidence, PatchValue Value);

        private List<PatchDraft> PatchDrafts()
        {
            var drafts = new List<PatchDraft>();
            if (dto.Patches.Count > 0 && !CandidateStage)
            {
                Fail(ReconcileViolationKind.StageShape, "patches", $"patches: ammesse solo negli stadi di espansione e confronto: la base del passaggio è nota solo dopo le foglie (stadio {_stage}).");
                return drafts;
            }

            if (dto.Patches.Count > 0 && baseState is null)
            {
                Fail(ReconcileViolationKind.StageShape, "patches", "patches: la base del passaggio non è disponibile: nessuna patch può essere firmata.");
                return drafts;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.Patches.Count; i++)
            {
                var p = dto.Patches[i];
                var path = $"patches[{i}]";
                if (!keys.Add(p.Key))
                {
                    Fail(ReconcileViolationKind.Duplicate, $"{path}.key", $"{path}.key: chiave locale {p.Key} duplicata.");
                }

                var evidence = EvidenceList(p.Evidence, $"{path}.evidence");
                var hasItem = p.Target.Item is not null;
                var hasRelation = p.Target.Relation is not null;
                PatchTargetKind? kind = null;
                var targetId = string.Empty;
                if (hasItem == hasRelation)
                {
                    Fail(ReconcileViolationKind.PatchShape, $"{path}.target", $"{path}.target: esattamente uno fra item e relation.");
                }
                else if (hasItem)
                {
                    kind = PatchTargetKind.Item;
                    if (catalog.Item(p.Target.Item!) is { } item)
                    {
                        targetId = item.LocalKey;
                    }
                    else
                    {
                        Fail(ReconcileViolationKind.UnknownAlias, $"{path}.target.item", $"{path}.target.item: item {p.Target.Item} inesistente (nessun item già proposto cita evidenze di questo batch).");
                    }
                }
                else
                {
                    kind = PatchTargetKind.Relation;
                    if (catalog.Relation(p.Target.Relation!) is { } relation)
                    {
                        targetId = relation.RelationId.ToString("D");
                    }
                    else
                    {
                        Fail(ReconcileViolationKind.UnknownAlias, $"{path}.target.relation", $"{path}.target.relation: alias {p.Target.Relation} sconosciuto o fuori dal batch.");
                    }
                }

                PatchValue? value = null;
                switch (p.Op, p.Value)
                {
                    case (PatchOperation.AddSupport, AiAddSupportValue):
                        if (p.Evidence.Count == 0)
                        {
                            Fail(ReconcileViolationKind.PatchShape, $"{path}.evidence", $"{path}.evidence: obbligatoria per AddSupport.");
                        }

                        value = new AddSupportValue { EvidenceIds = SortedEvidenceIds(evidence), SourceIds = SortedSourceIds(evidence) };
                        break;
                    case (PatchOperation.AddRelation, AiAddRelationValue relationValue):
                        if (kind == PatchTargetKind.Relation)
                        {
                            Fail(ReconcileViolationKind.PatchShape, $"{path}.target", $"{path}.target: AddRelation ancora un item esistente, non una relazione.");
                        }

                        var from = catalog.Entry(relationValue.From);
                        var to = catalog.Entry(relationValue.To);
                        if (from is null)
                        {
                            Fail(ReconcileViolationKind.UnknownAlias, $"{path}.value.from", $"{path}.value.from: alias {relationValue.From} sconosciuto o fuori dal batch.");
                        }

                        if (to is null)
                        {
                            Fail(ReconcileViolationKind.UnknownAlias, $"{path}.value.to", $"{path}.value.to: alias {relationValue.To} sconosciuto o fuori dal batch.");
                        }

                        if (relationValue.From == relationValue.To)
                        {
                            Fail(ReconcileViolationKind.CoincidentEndpoints, $"{path}.value", $"{path}.value: endpoint coincidenti ({relationValue.From}).");
                        }

                        // v11: stessa regola di CheckPatch (`rationale` non nulla ma di soli spazi), applicata subito come difesa in profondità.
                        if (stageContract && relationValue.Rationale is not null && string.IsNullOrWhiteSpace(relationValue.Rationale))
                        {
                            Fail(ReconcileViolationKind.InvalidText, $"{path}.value.rationale", $"{path}.value.rationale: non vuota quando valorizzata.");
                        }

                        if (from is not null && to is not null && relationValue.From != relationValue.To)
                        {
                            var endpoints = new[] { relationValue.From, relationValue.To }.Order(StringComparer.Ordinal).ToList();
                            if (p.Evidence.Count > 0 && !p.Evidence.Order(StringComparer.Ordinal).SequenceEqual(endpoints, StringComparer.Ordinal))
                            {
                                Fail(ReconcileViolationKind.PatchShape, $"{path}.evidence", $"{path}.evidence: per AddRelation vuota oppure esattamente gli endpoint {relationValue.From} e {relationValue.To}.");
                            }

                            evidence = [from, to];
                            value = new AddRelationValue
                            {
                                FromEvidenceId = from.EvidenceId, ToEvidenceId = to.EvidenceId, Type = relationValue.Type, Basis = relationValue.Basis, Rationale = relationValue.Rationale,
                            };
                        }

                        break;
                    case (PatchOperation.ProposeAlternative, AiProposeAlternativeValue alternative):
                        if (p.Evidence.Count == 0)
                        {
                            Fail(ReconcileViolationKind.PatchShape, $"{path}.evidence", $"{path}.evidence: obbligatoria per ProposeAlternative.");
                        }

                        // v11: stessa regola di CheckPatch (`text` e `reason` non vuoti), applicata subito come difesa in profondità.
                        if (stageContract && (string.IsNullOrWhiteSpace(alternative.Text) || string.IsNullOrWhiteSpace(alternative.Reason)))
                        {
                            Fail(ReconcileViolationKind.InvalidText, $"{path}.value", $"{path}.value: text e reason non vuoti.");
                        }

                        value = new ProposeAlternativeValue
                        {
                            Text = alternative.Text, Classification = alternative.Classification, EvidenceIds = SortedEvidenceIds(evidence), SourceIds = SortedSourceIds(evidence),
                            Reason = alternative.Reason,
                        };
                        break;
                    default:
                        // Non raggiungibile dal percorso alias: AiPatchConverter deserializza `value` secondo `op` e un'incoerenza è già `Undecodable` (assessment C-4). Difesa in profondità.
                        Fail(ReconcileViolationKind.PatchShape, $"{path}.value", $"{path}.value: non coerente con op {p.Op}.");
                        break;
                }

                if (value is not null && kind is not null && targetId.Length > 0)
                {
                    drafts.Add(new PatchDraft(path, p, kind.Value, targetId, evidence, value));
                }
            }

            return drafts;
        }

        private List<Patch> Stamp(List<PatchDraft> drafts)
        {
            var patches = new List<Patch>();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in drafts)
            {
                if (baseState is null || !baseState.TargetHashes.TryGetValue(ReconciliationBase.TargetKey(d.TargetKind, d.TargetId), out var expected))
                {
                    Fail(ReconcileViolationKind.PatchShape, $"{d.Path}.target", $"{d.Path}.target: {d.TargetKind} {d.TargetId} assente dalla base del passaggio.");
                    continue;
                }

                var patch = new Patch
                {
                    PatchId = "pending", BaseHash = baseState.BaseHash, Target = new PatchTarget { Kind = d.TargetKind, Id = d.TargetId, ExpectedHash = expected },
                    Operation = d.Dto.Op, Value = d.Value, EvidenceIds = SortedEvidenceIds(d.Evidence), SourceIds = SortedSourceIds(d.Evidence), OriginBatchIds = [_batchId],
                };
                patch = patch with { PatchId = ReconciliationIds.PatchId(patch) };
                if (!ids.TryAdd(patch.PatchId, d.Path))
                {
                    Fail(ReconcileViolationKind.Duplicate, d.Path, $"{d.Path}: patch duplicata (stessa identità canonica di {ids[patch.PatchId]}).");
                    continue;
                }

                patches.Add(patch);
            }

            return patches;
        }
    }
}
