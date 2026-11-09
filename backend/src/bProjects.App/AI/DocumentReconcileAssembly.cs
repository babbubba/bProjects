using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

/// <summary>Esito del motore di riconciliazione rigiocato sugli output Committed: directory, gerarchia, candidati e round finale (T072–T075).</summary>
internal sealed record ReconcileEngineResult(ReconciliationDirectoryResult Directory, HierarchyResult Hierarchy, CandidateRun Candidates, FinalPassResult Final);

/// <summary>
/// Sezioni dichiarate dal modello per gli item dei batch alias (T131, AI §11): il canonico <c>reconcile-1</c> non ha sezioni, quindi le porta la mappa (chiave locale canonica →
/// sezione) ricostruita dagli output Committed; <see cref="Ungrounded"/> sono le domande aperte senza evidenze (non proposte del motore, fuori dalla mappa delle evidenze).
/// Con <c>null</c> (percorso v2) vale la regola storica: requirements e assumptions.
/// </summary>
internal sealed record HierarchicalItemSections(IReadOnlyDictionary<string, AiSection> SectionOf, IReadOnlyList<UngroundedAliasItem> Ungrounded);

/// <summary>Risultato dell'assemblaggio server: tutto ciò che serve a calcolare la copertura e a committare revisione, mappa, relazioni e proiezioni in un'unica transazione.</summary>
internal sealed record ReconcileAssembly(
    HierarchicalRun Run, DocumentAnalysisContent Analysis, IReadOnlyList<EvidenceMapEntry> Map, IReadOnlyList<RunRelation> Relations, IReadOnlyList<BatchOutcome> Outcomes);

/// <summary>
/// Assemblaggio dell'analisi dal risultato gerarchico (AI §4.5): item dalle proposte validate (id canonici del server, mai del modello), conflitti dai gruppi di
/// patch preservati e dai candidati Open (alternative integrali con tutte le fonti), nessuna scelta automatica. Con le sezioni dei batch alias (<paramref name="sections"/>,
/// T131) gli item vanno nella sezione dichiarata dal modello (obiettivi, ambito, requisiti, flussi, vincoli, dipendenze, assunzioni, domande aperte), con classificazione e
/// fonti delle proposte; senza sezioni (v2) quelli senza sezione nello schema delle proposte vanno in `requirements` (le assunzioni in `assumptions`).
/// Nessuna evidenza è sintetizzata dal server: ciò che il modello non ha proposto resta non mappato.
/// </summary>
internal static class DocumentReconcileAssembly
{
    public static ReconcileAssembly Assemble(DocumentAnalysisInput input, ReconcileEngineResult engine, HierarchicalItemSections? sections = null)
    {
        var snapshot = input.Snapshot;
        var outcomes = engine.Hierarchy.Outcomes.Concat(engine.Candidates.Outcomes).GroupBy(o => o.Batch.BatchId).Select(g => g.First()).ToList();
        var baseState = DocumentAnalysisBase.ComputeHierarchical(outcomes, snapshot);
        var patches = ReconciliationPatchComposer.ComposeHierarchical(outcomes, baseState, snapshot);
        if (patches.Composition.HasIntegrityConflict)
        {
            throw new DocumentWorkException(
                DocumentWorkException.IntegrityConflict,
                "Stesso patchId con payload diverso: " + string.Join(", ", patches.Composition.Dispositions.Where(d => d.Kind == PatchDispositionKind.IntegrityConflict).Select(d => d.PatchId)));
        }

        var run = new HierarchicalRun(snapshot, engine.Directory, engine.Hierarchy, engine.Candidates, engine.Final, patches);

        var proposals = new List<ItemProposal>();
        foreach (var proposal in engine.Hierarchy.Outcomes.Where(o => o.Batch.Stage == "leaf").SelectMany(o => o.Result.ItemProposals))
        {
            if (proposals.FirstOrDefault(p => p.LocalKey == proposal.LocalKey) is { } existing)
            {
                if (ReconciliationCodec.Canonical(existing) != ReconciliationCodec.Canonical(proposal))
                {
                    throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"La localKey {proposal.LocalKey} è proposta con contenuti diversi da batch diversi.");
                }

                continue;
            }

            proposals.Add(proposal);
        }

        var persisted = snapshot.Evidence.SelectMany(e => e.Sources).GroupBy(s => s.SourceId).ToDictionary(g => g.Key, g => g.First());
        var evidence = input.Evidence.ToDictionary(e => e.EvidenceId);
        var map = new List<EvidenceMapEntry>();
        var usedSources = new HashSet<Guid>();

        Guid IdOf(string kind, string key) => AnalysisAliasComposer.CanonicalId(kind, input.AnalysisId, snapshot.SourceHash, key);

        DocumentAnalysisItem MakeItem(Guid id, string text, DocumentAnalysisClassification classification, IReadOnlyList<Guid> sources, IReadOnlyList<Guid> evidenceIds)
        {
            usedSources.UnionWith(sources);
            map.Add(new EvidenceMapEntry { ItemId = id, EvidenceIds = evidenceIds });
            return new DocumentAnalysisItem { Id = id, Text = text, Classification = classification, EvidenceRefs = sources };
        }

        var items = proposals.Select(p => (Proposal: p, Item: MakeItem(IdOf("item", p.LocalKey), p.Text, p.Classification, p.SourceIds, p.EvidenceIds))).ToList();

        var conflicts = new List<DocumentAnalysisConflict>();
        var patchById = outcomes.SelectMany(o => o.Result.Patches).GroupBy(p => p.PatchId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var group in patches.Composition.Conflicts)
        {
            var alternatives = group.PatchIds.Select(id =>
            {
                var patch = patchById[id];
                var (text, classification) = patch.Value switch
                {
                    ProposeAlternativeValue a => (a.Text, a.Classification),
                    AddRelationValue r => ($"{r.Type}: {r.FromEvidenceId:D} -> {r.ToEvidenceId:D}" + (r.Rationale is null ? string.Empty : $" ({r.Rationale})"), DocumentAnalysisClassification.Inferred),
                    _ => ("Fonte aggiuntiva proposta", DocumentAnalysisClassification.Inferred),
                };
                return MakeItem(IdOf("item", $"conflict:{group.GroupId}:{id}"), text, classification, patch.SourceIds, patch.EvidenceIds);
            }).ToList();
            conflicts.Add(new DocumentAnalysisConflict
            {
                Id = IdOf("conflict", $"group:{group.GroupId}"), Description = $"Proposte incompatibili ({group.Kind}): nessuna prevale, decisione al PM.", Status = AnalysisConflictStatus.Open,
                Alternatives = alternatives, Resolution = null,
            });
        }

        foreach (var open in engine.Candidates.Dispositions.Where(d => d.State == CandidateState.Open).OrderBy(d => d.CandidateId, StringComparer.Ordinal))
        {
            var candidate = engine.Candidates.Candidates.Single(c => c.CandidateId == open.CandidateId);
            var alternatives = new[] { ("L", candidate.Left), ("R", candidate.Right) }.Select(side =>
            {
                var e = evidence[Guid.Parse(side.Item2.Id)];
                var sources = snapshot.Evidence.Single(x => x.EvidenceId == e.EvidenceId).Sources.Select(s => s.SourceId).ToList();
                var classification = e.Origin == EvidenceOriginKind.Document ? DocumentAnalysisClassification.Documented : DocumentAnalysisClassification.Declared;
                return MakeItem(IdOf("item", $"open:{candidate.CandidateId}:{side.Item1}"), e.Statement, classification, sources, [e.EvidenceId]);
            }).ToList();
            conflicts.Add(new DocumentAnalysisConflict
            {
                Id = IdOf("conflict", $"open:{candidate.CandidateId}"), Description = string.IsNullOrWhiteSpace(open.Reason) ? "Alternative integrali da sottoporre al PM." : open.Reason,
                Status = AnalysisConflictStatus.Open, Alternatives = alternatives, Resolution = null,
            });
        }

        // Domande aperte senza evidenze (solo batch alias): item della revisione senza fonti e senza voce nella mappa delle evidenze, come nel percorso a chiamata singola.
        var ungrounded = (sections?.Ungrounded ?? []).Select(u => new DocumentAnalysisItem
        {
            Id = IdOf("item", u.LocalKey), Text = u.Text, Classification = u.Classification, EvidenceRefs = [],
        }).ToList();

        IReadOnlyList<DocumentAnalysisItem> InSection(AiSection section) =>
            [.. items.Where(i => sections!.SectionOf.TryGetValue(i.Proposal.LocalKey, out var s) ? s == section : throw new DocumentWorkException(
                DocumentWorkException.WorkOutputInvalid, $"L'item {i.Proposal.LocalKey} non ha una sezione dichiarata dal modello.")).Select(i => i.Item)];

        var sources2 = usedSources.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).Select(id => Source(input, persisted[id])).ToList();
        var analysis = sections is null
            ? new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = input.AnalysisId, ProjectId = input.ProjectId, SourceCutoffSequence = input.Cutoff,
                ValidationState = AnalysisContentState.Unvalidated, Sources = sources2, Goals = [],
                ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
                Requirements = [.. items.Where(i => i.Proposal.Classification != DocumentAnalysisClassification.Assumed).Select(i => i.Item)], OperationalFlows = [], Constraints = [],
                Dependencies = [], Assumptions = [.. items.Where(i => i.Proposal.Classification == DocumentAnalysisClassification.Assumed).Select(i => i.Item)], OpenQuestions = [],
                Conflicts = conflicts,
            }
            : new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = input.AnalysisId, ProjectId = input.ProjectId, SourceCutoffSequence = input.Cutoff,
                ValidationState = AnalysisContentState.Unvalidated, Sources = sources2, Goals = InSection(AiSection.Goals),
                ContextAndScope = new DocumentAnalysisContextAndScope
                {
                    Overview = InSection(AiSection.ScopeOverview), Included = InSection(AiSection.ScopeIncluded), Excluded = InSection(AiSection.ScopeExcluded),
                },
                Requirements = InSection(AiSection.Requirements), OperationalFlows = InSection(AiSection.Flows), Constraints = InSection(AiSection.Constraints),
                Dependencies = InSection(AiSection.Dependencies), Assumptions = InSection(AiSection.Assumptions),
                OpenQuestions = [.. InSection(AiSection.OpenQuestions), .. ungrounded], Conflicts = conflicts,
            };

        var relations = new List<RunRelation>();
        var accepted = patches.RunDispositions.Where(d => d.Kind is RunPatchDispositionKind.Applied or RunPatchDispositionKind.Duplicate or RunPatchDispositionKind.PreservedAsConflict)
            .Select(d => d.PatchId).ToHashSet(StringComparer.Ordinal);
        foreach (var patch in patchById.Values.Where(p => accepted.Contains(p.PatchId)).OrderBy(p => p.PatchId, StringComparer.Ordinal))
        {
            if (patch.Value is AddRelationValue r && !relations.Any(x => x.From == r.FromEvidenceId && x.To == r.ToEvidenceId && x.Type == r.Type))
            {
                relations.Add(new RunRelation(r.FromEvidenceId, r.ToEvidenceId, r.Type, r.Basis, r.Rationale));
            }
        }

        return new(run, analysis, map, relations, outcomes);
    }

    private static DocumentAnalysisSource Source(DocumentAnalysisInput input, PersistedSource s) => s.Kind == EvidenceSourceKind.Message
        ? new DocumentAnalysisSource
        {
            Id = s.SourceId, Kind = DocumentAnalysisSourceKind.Message, MessageId = s.MessageId, RevisionId = null, DocumentId = null, DocumentVersionId = null, PreparationId = null,
            ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null, MessageRole = input.Dialog.First(m => m.Id == s.MessageId).Role,
        }
        : new DocumentAnalysisSource
        {
            Id = s.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = s.DocumentId,
            DocumentVersionId = s.DocumentVersionId, PreparationId = s.PreparationId, ArtifactId = s.ArtifactId, BlockId = s.BlockId, Start = s.Start, End = s.End, Quote = s.Quote,
        };

    /// <summary>
    /// Righe T122 del run (append-only, payload autorevole in WorkOutput/DerivedBatch): nodi, pagine, membri, insiemi, candidati con origini e disposition, patch con le
    /// loro disposition. Nessun EvidenceItem.Content copiato. Sono scritte nella stessa transazione della revisione.
    /// </summary>
    public static IReadOnlyList<object> Projections(DocumentAnalysisInput input, Guid runId, HierarchicalRun run, IReadOnlyList<BatchOutcome> outcomes)
    {
        var rows = new List<object>();
        var hierarchy = run.Hierarchy;
        var batchOfOutput = outcomes.GroupBy(o => o.OutputHash).ToDictionary(g => g.Key, g => g.First().Batch.BatchId, StringComparer.Ordinal);
        var hashOfBatch = outcomes.GroupBy(o => o.Batch.BatchId).ToDictionary(g => g.Key, g => g.First().OutputHash, StringComparer.Ordinal);
        foreach (var node in hierarchy.Nodes)
        {
            var pages = hierarchy.Pages.Where(p => p.NodeId == node.NodeId).ToList();
            rows.AddRange(ReconciliationProjection.Catalog(input.ProjectId, runId, batchOfOutput[node.DescriptorOutputHash], new ReconciliationCatalog([node], pages, [])));
        }

        rows.AddRange(ReconciliationProjection.Catalog(input.ProjectId, runId, outcomes[0].Batch.BatchId, new ReconciliationCatalog([], [], run.Directory.Sets)));

        var sourceHash = input.Snapshot.SourceHash;
        foreach (var candidate in run.Candidates.Candidates)
        {
            rows.AddRange(ReconciliationProjection.Candidate(input.ProjectId, runId, sourceHash, run.Directory.InputHash, candidate, hashOfBatch));
        }

        rows.AddRange(run.Candidates.Dispositions.Select(d => new RunCandidateDisposition
        {
            RunId = runId, CandidateId = d.CandidateId, Version = 1, State = d.State, ChildCandidateIdsJson = ReconciliationCodec.Encode(d.ChildCandidateIds.ToList()),
            ComparisonOutputHashesJson = ReconciliationCodec.Encode(d.ComparisonOutputHashes.ToList()), Reason = d.Reason,
        }));

        var patchById = outcomes.SelectMany(o => o.Result.Patches).GroupBy(p => p.PatchId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var patch in patchById.Values.OrderBy(p => p.PatchId, StringComparer.Ordinal))
        {
            rows.Add(ReconciliationProjection.Patch(input.ProjectId, runId, sourceHash, patch, run.Patches.OriginOutputHashes[patch.PatchId]));
        }

        rows.AddRange(run.Patches.RunDispositions.Select(d => new RunPatchDisposition { RunId = runId, PatchId = d.PatchId, Version = 1, Kind = d.Kind, ResultHash = d.ResultHash, Reason = d.Reason }));
        return rows;
    }
}

/// <summary>Valutazione di un Gap verificata dall'assembly dello stadio Gap (v6): il record del modello con gli id risolti del catalogo e dell'analisi assemblata.</summary>
internal sealed record GapStageResolved(string GapAlias, Guid GapEvidenceId, AiGapAssessment Record, Guid? OpenItemId, Guid? OpenConflictId);

internal static class DocumentReconcileGapStage
{
    /// <summary>
    /// Assembly dello stadio Gap (D1, AI §13.7): verifica che il payload committato abbia ESATTAMENTE un record finale per Gap del catalogo e che <c>openItem</c>/<c>openConflict</c> si risolvano in una domanda
    /// o in un conflitto canonico dell'analisi assemblata. NON modifica item, conflitti, EvidenceMap o EvidenceRefs e non sintetizza né corregge valutazioni: un record mancante è <c>coverage_incomplete</c>
    /// (<c>gap_not_assessed</c>), un riferimento non risolvibile è <c>work_output_invalid</c>.
    /// </summary>
    public static IReadOnlyList<GapStageResolved> Verify(ReconcileAssembly assembled, AnalysisAliasCatalog catalog, GapStageReferences references, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(assembled);
        var read = GapStagePayload.Read(payloadJson, catalog, references);
        if (read.MissingGaps.Count > 0)
        {
            throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete,
                $"Lo stadio Gap è valido ma {read.MissingGaps.Count} Gap non hanno una valutazione finale: " + string.Join(" | ", read.MissingGaps.Take(5).Select(g => $"gap_not_assessed: {g}")));
        }

        var gaps = catalog.Data.Evidence.Where(e => e.Kind == EvidenceKind.Gap).ToList();
        var byGap = read.Records.GroupBy(r => r.Gap, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (gaps.Any(g => !byGap.TryGetValue(g.Alias, out var list) || list.Count != 1) || byGap.Keys.Any(k => gaps.All(g => g.Alias != k)))
        {
            throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, "Lo stadio Gap non porta esattamente un record finale per ogni Gap del catalogo.");
        }

        var questions = assembled.Analysis.OpenQuestions.Select(q => q.Id).ToHashSet();
        var conflicts = assembled.Analysis.Conflicts.Select(c => c.Id).ToHashSet();
        var resolved = new List<GapStageResolved>(gaps.Count);
        foreach (var gap in gaps)
        {
            var record = byGap[gap.Alias][0];
            Guid? item = null;
            Guid? conflict = null;
            if (record.OpenItem is not null)
            {
                var question = references.Questions.FirstOrDefault(q => q.Key == record.OpenItem);
                item = question is not null && questions.Contains(question.ItemId)
                    ? question.ItemId
                    : throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"La domanda aperta {record.OpenItem} del Gap {gap.Alias} non è una domanda canonica dell'analisi assemblata.");
            }

            if (record.OpenConflict is not null)
            {
                var found = references.Conflicts.FirstOrDefault(c => c.Key == record.OpenConflict);
                conflict = found is not null && conflicts.Contains(found.ConflictId)
                    ? found.ConflictId
                    : throw new DocumentWorkException(DocumentWorkException.WorkOutputInvalid, $"Il conflitto aperto {record.OpenConflict} del Gap {gap.Alias} non è un conflitto canonico dell'analisi assemblata.");
            }

            resolved.Add(new(gap.Alias, gap.EvidenceId, record, item, conflict));
        }

        return resolved;
    }
}
