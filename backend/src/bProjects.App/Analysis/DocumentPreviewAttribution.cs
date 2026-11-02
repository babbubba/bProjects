namespace bProjects.App.Analysis;

/// <summary>
/// Attribuzione server-side delle modifiche del PM su AnalysisContent 1.1.0 (stessa regola della 1.0.0, ai-workflow §4): una sola fonte PmRevision
/// legata alla revisione in creazione, citata dagli item e dalle risoluzioni nuovi o modificati. Le fonti (comprese quelle documentali) non sono
/// modificabili dal PM; la classificazione scelta dal PM non viene mai elevata o abbassata dal server (Documented resta Documented con i suoi passaggi).
/// </summary>
internal static class DocumentPreviewAttribution
{
    internal sealed record Result(DocumentAnalysisContent Content, IReadOnlyList<string> Violations);

    public static Result Apply(DocumentAnalysisContent incoming, DocumentAnalysisContent current, Guid revisionId, Guid pmSourceId)
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

        DocumentAnalysisItem Attribute(DocumentAnalysisItem item)
        {
            if (currentItems.TryGetValue(item.Id, out var before)
                && before.Text == item.Text && before.Classification == item.Classification && before.EvidenceRefs.SequenceEqual(item.EvidenceRefs))
            {
                return item;
            }

            changed = true;
            return item with { EvidenceRefs = Cite(item.EvidenceRefs, pmSourceId) };
        }

        IReadOnlyList<DocumentAnalysisItem> Section(IReadOnlyList<DocumentAnalysisItem> items) => [.. items.Select(Attribute)];

        var conflicts = new List<DocumentAnalysisConflict>();
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
            ContextAndScope = new DocumentAnalysisContextAndScope
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
                    new DocumentAnalysisSource
                    {
                        Id = pmSourceId,
                        Kind = DocumentAnalysisSourceKind.PmRevision,
                        MessageId = null,
                        MessageRole = null,
                        RevisionId = revisionId,
                        DocumentId = null,
                        DocumentVersionId = null,
                        PreparationId = null,
                        ArtifactId = null,
                        BlockId = null,
                        Start = null,
                        End = null,
                        Quote = null,
                    },
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

    private static IEnumerable<DocumentAnalysisItem> Items(DocumentAnalysisContent c) => c.Goals
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
