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
/// Esito della composizione di un batch. <see cref="Result"/> è valorizzato solo se l'output è composto; un rifiuto porta le violazioni (alias e percorso) e il codice permanente.
/// <see cref="Sections"/> associa la chiave locale canonica di ogni proposta alla sezione dichiarata dal modello (il canonico <c>reconcile-1</c> non ha sezioni).
/// </summary>
public sealed record ReconcileAliasComposition(
    string? ErrorCode, IReadOnlyList<string> Violations, ReconcileResult? Result, string? AiOutputHash, string CatalogHash, string? CanonicalHash,
    IReadOnlyDictionary<string, AiSection> Sections, IReadOnlyList<UngroundedAliasItem> Ungrounded)
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
    /// </summary>
    public static ReconcileAliasComposition Compose(
        string aiJson, ReconcileAliasCatalog catalog, ReconcileBatchRequest request, ReconciliationSnapshot snapshot, ReconciliationBase? baseState = null, string? expectedCanonicalHash = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        AiReconcileOutput dto;
        try
        {
            dto = ReconcileAliasContracts.Decode(aiJson);
        }
        catch (JsonException e)
        {
            return Reject(catalog, null, DocumentWorkException.WorkOutputInvalid, [$"JSON non conforme a {ReconcileAliasContracts.SchemaVersion}: {e.Message}"]);
        }

        return new Builder(dto, catalog, request, snapshot, baseState).Run(expectedCanonicalHash);
    }

    private static ReconcileAliasComposition Reject(ReconcileAliasCatalog catalog, string? aiHash, string code, IReadOnlyList<string> violations) =>
        new(code, [.. violations.Distinct(StringComparer.Ordinal)], null, aiHash, catalog.CatalogHash, null, new Dictionary<string, AiSection>(), []);

    private sealed class Builder(AiReconcileOutput dto, ReconcileAliasCatalog catalog, ReconcileBatchRequest request, ReconciliationSnapshot snapshot, ReconciliationBase? baseState)
    {
        private readonly List<string> _errors = [];
        private readonly string _stage = request.Batch.Stage;
        private readonly string _batchId = request.Batch.BatchId;

        private bool LeafA => _stage == "leaf" && request.View == ReconcileView.A;

        private bool CandidateStage => _stage is "expand" or "compare";

        public ReconcileAliasComposition Run(string? expectedCanonicalHash)
        {
            var aiHash = ReconcileAliasContracts.Hash(dto);
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
                return Reject(catalog, aiHash, DocumentWorkException.IntegrityConflict, ["canonicalHash: il canonico ricomposto è diverso da quello committato."]);
            }

            return new ReconcileAliasComposition(null, [], result, aiHash, catalog.CatalogHash, canonicalHash, sections, ungrounded);
        }

        private List<ReconcileCatalogEntry> EvidenceList(IReadOnlyList<string> aliases, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ReconcileCatalogEntry>();
            for (var i = 0; i < aliases.Count; i++)
            {
                if (!seen.Add(aliases[i]))
                {
                    _errors.Add($"{path}[{i}]: alias {aliases[i]} duplicato.");
                }
                else if (catalog.Entry(aliases[i]) is { } entry)
                {
                    result.Add(entry);
                }
                else
                {
                    _errors.Add($"{path}[{i}]: alias {aliases[i]} sconosciuto o fuori dal batch.");
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
                _errors.Add($"items: ammessi solo nelle foglie della vista A (stadio {_stage}, vista {request.View}); le altre viste e gli altri stadi non propongono item.");
                return (proposals, sections, ungrounded);
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.Items.Count; i++)
            {
                var item = dto.Items[i];
                var path = $"items[{i}]";
                if (!keys.Add(item.Key))
                {
                    _errors.Add($"{path}.key: chiave locale {item.Key} duplicata: ogni chiave si usa una sola volta nel batch.");
                }

                var evidence = EvidenceList(item.Evidence, $"{path}.evidence");
                var localKey = LocalKey(request, item.Key);
                if (string.IsNullOrWhiteSpace(item.Text))
                {
                    _errors.Add($"{path}.text: non vuoto.");
                }

                // Stesse regole di AnalysisContent 1.1 (DocumentAnalysisContentRules), applicate subito al batch: un errore di classificazione non deve emergere solo all'assemblaggio,
                // dopo tutte le chiamate. Declared richiede evidenze di un messaggio del PM, Documented di un documento, assumptions solo Assumed.
                if (item.Section == AiSection.Assumptions && item.Classification != DocumentAnalysisClassification.Assumed)
                {
                    _errors.Add($"{path}.classification: gli elementi di assumptions sono Assumed.");
                }

                if (item.Evidence.Count > 0 && item.Classification == DocumentAnalysisClassification.Declared && !evidence.Any(e => e.Origin == EvidenceOriginKind.Message))
                {
                    _errors.Add($"{path}.classification: Declared richiede almeno un'evidenza con origine Message (dichiarazione del project manager).");
                }

                if (item.Evidence.Count > 0 && item.Classification == DocumentAnalysisClassification.Documented && !evidence.Any(e => e.Origin == EvidenceOriginKind.Document))
                {
                    _errors.Add($"{path}.classification: Documented richiede almeno un'evidenza con origine Document.");
                }

                if (item.Evidence.Count == 0)
                {
                    if (item.Section != AiSection.OpenQuestions)
                    {
                        _errors.Add($"{path}.evidence: obbligatoria (solo una domanda aperta in openQuestions può non citare evidenze).");
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
                _errors.Add($"{path}: l'estremo {alias} non è ammesso nello stadio {_stage} (gli estremi sono {(_stage is "leaf" or "expand" or "compare" ? "evidenze E<n>" : "nodi N<n>")}).");
                return null;
            }

            if (alias[0] == 'E')
            {
                if (catalog.Entry(alias) is not { } entry)
                {
                    _errors.Add($"{path}: alias {alias} sconosciuto o fuori dal batch.");
                    return null;
                }

                return new CandidateEndpoint { Kind = CandidateEndpointKind.Evidence, Id = entry.EvidenceId.ToString("D"), Hash = entry.ContentHash };
            }

            if (catalog.Node(alias) is not { } node)
            {
                _errors.Add($"{path}: alias {alias} sconosciuto o fuori dal batch.");
                return null;
            }

            return new CandidateEndpoint { Kind = node.Kind, Id = node.Id, Hash = node.Hash };
        }

        private List<ReconcileCandidate> Candidates()
        {
            var candidates = new List<ReconcileCandidate>();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            // v10 (membership-1): il tetto del profilo -3 è una protezione. La grammatica lo impone già; qui lo si ripete come difesa in profondità (output senza schema o provider che lo ignora):
            // oltre il tetto è un rifiuto, mai un troncamento. Un'espansione che RAGGIUNGE il tetto è valida ma satura (lo decide il motore, che non dichiara la copertura).
            if (_stage == "expand" && request.Groups is not null && dto.Candidates.Count > ReconcileAliasContracts.ExpansionCandidateCap)
            {
                _errors.Add($"candidates: {dto.Candidates.Count} candidati oltre il tetto di {ReconcileAliasContracts.ExpansionCandidateCap} dell'espansione (nessun troncamento: output non valido).");
            }

            for (var i = 0; i < dto.Candidates.Count; i++)
            {
                var c = dto.Candidates[i];
                var path = $"candidates[{i}]";
                if (!keys.Add(c.Key))
                {
                    _errors.Add($"{path}.key: chiave locale {c.Key} duplicata.");
                }

                var left = Endpoint(c.Left, $"{path}.left");
                var right = Endpoint(c.Right, $"{path}.right");
                if (c.Left == c.Right)
                {
                    _errors.Add($"{path}: endpoint coincidenti ({c.Left}).");
                }

                if (string.IsNullOrWhiteSpace(c.Rationale))
                {
                    _errors.Add($"{path}.rationale: non vuota.");
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
                    _errors.Add($"{path}: candidato duplicato (stessa identità canonica di {ids[id]}).");
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
                    _errors.Add("focus: il confronto deve dichiarare Compared oppure Open per il candidato in esame.");
                }
                else if (focus.Outcome == AiFocusOutcome.Open && string.IsNullOrWhiteSpace(focus.Reason))
                {
                    _errors.Add("focus.reason: obbligatorio e non vuoto quando l'esito è Open.");
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
                    _errors.Add("focus: l'espansione è una visita, non un confronto: solo null oppure Unresolved (nessun Compared né Open).");
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
                _errors.Add($"focus: previsto solo negli stadi di espansione e confronto (stadio {_stage}): null.");
            }

            return result;
        }

        private sealed record PatchDraft(string Path, AiPatch Dto, PatchTargetKind TargetKind, string TargetId, List<ReconcileCatalogEntry> Evidence, PatchValue Value);

        private List<PatchDraft> PatchDrafts()
        {
            var drafts = new List<PatchDraft>();
            if (dto.Patches.Count > 0 && !CandidateStage)
            {
                _errors.Add($"patches: ammesse solo negli stadi di espansione e confronto: la base del passaggio è nota solo dopo le foglie (stadio {_stage}).");
                return drafts;
            }

            if (dto.Patches.Count > 0 && baseState is null)
            {
                _errors.Add("patches: la base del passaggio non è disponibile: nessuna patch può essere firmata.");
                return drafts;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < dto.Patches.Count; i++)
            {
                var p = dto.Patches[i];
                var path = $"patches[{i}]";
                if (!keys.Add(p.Key))
                {
                    _errors.Add($"{path}.key: chiave locale {p.Key} duplicata.");
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
                    if (catalog.Item(p.Target.Item!) is { } item)
                    {
                        targetId = item.LocalKey;
                    }
                    else
                    {
                        _errors.Add($"{path}.target.item: item {p.Target.Item} inesistente (nessun item già proposto cita evidenze di questo batch).");
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
                        _errors.Add($"{path}.target.relation: alias {p.Target.Relation} sconosciuto o fuori dal batch.");
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

                        var from = catalog.Entry(relationValue.From);
                        var to = catalog.Entry(relationValue.To);
                        if (from is null)
                        {
                            _errors.Add($"{path}.value.from: alias {relationValue.From} sconosciuto o fuori dal batch.");
                        }

                        if (to is null)
                        {
                            _errors.Add($"{path}.value.to: alias {relationValue.To} sconosciuto o fuori dal batch.");
                        }

                        if (relationValue.From == relationValue.To)
                        {
                            _errors.Add($"{path}.value: endpoint coincidenti ({relationValue.From}).");
                        }

                        if (from is not null && to is not null && relationValue.From != relationValue.To)
                        {
                            var endpoints = new[] { relationValue.From, relationValue.To }.Order(StringComparer.Ordinal).ToList();
                            if (p.Evidence.Count > 0 && !p.Evidence.Order(StringComparer.Ordinal).SequenceEqual(endpoints, StringComparer.Ordinal))
                            {
                                _errors.Add($"{path}.evidence: per AddRelation vuota oppure esattamente gli endpoint {relationValue.From} e {relationValue.To}.");
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

        private List<Patch> Stamp(List<PatchDraft> drafts)
        {
            var patches = new List<Patch>();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in drafts)
            {
                if (baseState is null || !baseState.TargetHashes.TryGetValue(ReconciliationBase.TargetKey(d.TargetKind, d.TargetId), out var expected))
                {
                    _errors.Add($"{d.Path}.target: {d.TargetKind} {d.TargetId} assente dalla base del passaggio.");
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
                    _errors.Add($"{d.Path}: patch duplicata (stessa identità canonica di {ids[patch.PatchId]}).");
                    continue;
                }

                patches.Add(patch);
            }

            return patches;
        }
    }
}
