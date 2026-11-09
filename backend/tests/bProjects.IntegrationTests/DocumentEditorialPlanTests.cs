using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// T085 (DET) — planner editoriale (contracts/ai-workflow.md §4.5): ogni item integrale della revisione confermata è principale di UNA unità; contesto, conflitti con
/// risoluzione e fonti necessari viaggiano con l'unità e contano nel payload misurato; tutto entro budget = singola chiamata, altrimenti unità contigue misurate;
/// un item irriducibile fallisce con ID senza compattare né aumentare la finestra; il replay ricostruisce gli stessi input e hash. Nessun database né LLM: il
/// misuratore finto conta parole (istruzioni fisse + cornice per item + testo di principali, contesto, conflitti e citazioni delle fonti).
/// </summary>
public sealed class DocumentEditorialPlanTests
{
    private const string Owner = "ProcessingRun|r";
    private const string Manifest = "manifest-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Words(int count, string prefix) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private static int Count(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed record Built(DocumentAnalysisContent Content, List<Guid> ItemIds, List<Guid> SourceIds, Guid ConflictId, List<Guid> Alternatives);

    /// <summary>
    /// Revisione confermata con <paramref name="requirements"/> requisiti di <paramref name="words"/> parole ciascuno (una fonte con citazione propria per ognuno) e un
    /// conflitto Risolto di tre alternative: la prima scelta dal PM con un motivo.
    /// </summary>
    private static Built Revision(int requirements, int words, int alternativeWords = 40)
    {
        var sources = new List<DocumentAnalysisSource>();
        DocumentAnalysisSource Source(string quote)
        {
            var source = new DocumentAnalysisSource
            {
                Id = Guid.NewGuid(), Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = Guid.NewGuid(),
                DocumentVersionId = Guid.NewGuid(), PreparationId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = quote.Length, Quote = quote,
            };
            sources.Add(source);
            return source;
        }

        var items = new List<DocumentAnalysisItem>();
        for (var i = 0; i < requirements; i++)
        {
            var source = Source("citazione " + Words(10, $"q{i}x"));
            items.Add(new DocumentAnalysisItem
            {
                Id = Guid.NewGuid(), Text = Words(words, $"r{i}x"), Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [source.Id],
            });
        }

        var alternatives = Enumerable.Range(0, 3).Select(a => new DocumentAnalysisItem
        {
            Id = Guid.NewGuid(), Text = Words(alternativeWords, $"alt{a}x"), Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [Source("alt " + Words(5, $"a{a}x")).Id],
        }).ToList();
        var conflict = new DocumentAnalysisConflict
        {
            Id = Guid.NewGuid(), Description = "Orario discordante", Status = AnalysisConflictStatus.Resolved, Alternatives = alternatives,
            Resolution = new AnalysisConflictResolution { SelectedAlternativeIds = [alternatives[0].Id], Reason = "Scelta del PM per MOTIVOSCELTA", EvidenceRefs = [] },
        };
        var content = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), SourceCutoffSequence = 0,
            ValidationState = AnalysisContentState.PmConfirmed, Sources = sources, Goals = [],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [conflict],
        };
        return new Built(content, [.. items.Select(i => i.Id), .. alternatives.Select(a => a.Id)], [.. sources.Select(s => s.Id)], conflict.Id, [.. alternatives.Select(a => a.Id)]);
    }

    /// <summary>40 parole fisse + 5 per item (principale o contesto) + testo + 10 per conflitto con descrizione, motivo e stato + citazioni delle fonti.</summary>
    private static EditorialMeasurer Measurer(List<EditorialSlice>? seen = null) => (slice, _) =>
    {
        seen?.Add(slice);
        var owned = slice.Primary.Sum(i => Count(i.Text));
        var tokens = 40 + owned + slice.Context.Sum(i => 5 + Count(i.Text)) + slice.Primary.Count * 5
            + slice.Conflicts.Sum(c => 10 + Count(c.Description) + Count(c.ResolutionReason ?? "")) + slice.Sources.Sum(s => Count(s.Quote ?? ""));
        var key = string.Join('|', slice.Primary.Select(i => i.Id)) + "#" + string.Join('|', slice.Context.Select(i => i.Id)) + "#" + string.Join('|', slice.Sources.Select(s => s.Id));
        return new ValueTask<UnitMeasure>(new UnitMeasure(tokens, TokenCountQuality.Measured, Sha(key), owned));
    };

    private static DocumentEditorialPlanner Planner(Built built, PlannerLimits limits, EditorialMeasurer? measure = null) =>
        new(built.Content, Owner, Manifest, "project-document-documents-v2", limits, measure ?? Measurer(), "schema-1", "prompt-1");

    private static IReadOnlyList<string> Chain(int ordinal) => ordinal == 0 ? [] : [Sha($"output-{ordinal - 1}")];

    // ================================================================ scenari

    [Fact]
    public async Task A_revision_that_fits_is_a_single_call_with_every_item_as_primary_and_nothing_compacted()
    {
        var built = Revision(5, 30);

        var units = await Planner(built, new PlannerLimits(2000, 100_000)).PlanAsync(Chain, Ct);

        var unit = Assert.Single(units);
        Assert.True(unit.SingleCall);
        Assert.Equal(built.ItemIds, unit.PrimaryItemIds);   // ordine stabile: requisiti, poi le alternative del conflitto
        Assert.Empty(unit.ContextItemIds);                    // tutto e' gia' principale
        Assert.Contains(built.ConflictId, unit.ConflictIds);
        Assert.Equal(built.SourceIds.Order(), unit.SourceIds.Order());
        Assert.Empty(unit.Derived.ParentOutputHashes);
    }

    [Fact]
    public async Task A_total_beyond_the_window_is_split_into_bounded_units_without_losing_items_qualifiers_or_raising_the_window()
    {
        var built = Revision(30, 60);   // ~30 x 60 parole + conflitto: molto oltre il budget di 700
        var limits = new PlannerLimits(700, 100_000);
        var seen = new List<EditorialSlice>();
        var planner = Planner(built, limits, Measurer(seen));

        var units = await planner.PlanAsync(Chain, Ct);

        Assert.True(units.Count > 1);
        Assert.All(units, u => Assert.False(u.SingleCall));
        Assert.All(units, u => Assert.True(u.Measure.Tokens <= limits.InputBudgetTokens, $"unita' {u.Ordinal}: {u.Measure.Tokens} token"));   // finestra mai aumentata
        Assert.Equal(new PlannerLimits(700, 100_000), limits);
        // Ogni item e' principale di una e una sola unita', nell'ordine stabile.
        Assert.Equal(built.ItemIds, units.SelectMany(u => u.PrimaryItemIds));
        planner.VerifyCovered(units);
        Assert.Equal(units.Select((_, i) => i), units.Select(u => u.Ordinal));
        // Le fonti dei principali e del contesto viaggiano con l'unita': nessun riferimento perso.
        var items = planner.Items.ToDictionary(i => i.Id);
        foreach (var unit in units)
        {
            var needed = unit.PrimaryItemIds.Concat(unit.ContextItemIds).SelectMany(id => items[id].SourceIds).ToHashSet();
            Assert.Subset(unit.SourceIds.ToHashSet(), needed);
        }

        // Il conflitto: ogni unita' che ha una sua alternativa come principale lo porta con stato e risoluzione motivata, e le altre alternative come contesto.
        foreach (var slice in seen.Where(s => s.Primary.Any(p => p.ConflictId == built.ConflictId)))
        {
            var conflict = Assert.Single(slice.Conflicts);
            Assert.Equal(AnalysisConflictStatus.Resolved, conflict.Status);
            Assert.Equal([built.Alternatives[0]], conflict.SelectedAlternativeIds);
            Assert.Contains("MOTIVOSCELTA", conflict.ResolutionReason);
            var present = slice.Primary.Concat(slice.Context).Where(i => i.ConflictId == built.ConflictId).Select(i => i.Id);
            Assert.Equal(built.Alternatives.Order(), present.Order());   // nessuna alternativa del conflitto manca dalla fetta
        }

        Assert.All(planner.Items.Where(i => i.ConflictId is null), i => Assert.Equal(DocumentAnalysisClassification.Documented, i.Classification));
    }

    [Fact]
    public async Task Only_the_alternatives_not_in_the_unit_are_context_and_primaries_are_never_repeated_as_context()
    {
        var built = Revision(1, 20, alternativeWords: 100);
        var planner = Planner(built, new PlannerLimits(100_000, 150, OutputExpansion: 1.0));   // l'output stimato spezza il conflitto: le alternative fuori dall'unita' diventano contesto

        var units = await planner.PlanAsync(Chain, Ct);

        Assert.True(units.Count >= 2, string.Join(" / ", units.Select(u => $"{u.PrimaryItemIds.Count}p {u.ContextItemIds.Count}c {u.Measure.Tokens}t")));
        Assert.All(units, u => Assert.Empty(u.PrimaryItemIds.Intersect(u.ContextItemIds)));
        var withContext = units.Where(u => u.ContextItemIds.Count > 0).ToList();
        Assert.NotEmpty(withContext);
        Assert.All(withContext, u => Assert.All(u.ContextItemIds, id => Assert.Contains(id, built.Alternatives)));
    }

    [Fact]
    public async Task An_item_that_alone_exceeds_the_budget_is_irreducible_with_its_id_and_never_truncated()
    {
        var built = Revision(3, 20);
        var huge = built.Content with { Requirements = [.. built.Content.Requirements, built.Content.Requirements[0] with { Id = Guid.NewGuid(), Text = Words(900, "enorme") }] };
        var planner = new DocumentEditorialPlanner(huge, Owner, Manifest, "def", new PlannerLimits(500, 100_000), Measurer(), "schema-1", "prompt-1");

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => planner.PlanAsync(Chain, Ct).AsTask());

        Assert.Equal(DocumentWorkException.DocumentIncomplete, failure.Code);
        Assert.Contains(huge.Requirements[^1].Id.ToString("D"), failure.Message);
        Assert.DoesNotContain("enorme17", failure.Message);   // ID e motivo, mai testo
    }

    [Fact]
    public async Task An_alternative_that_fits_alone_but_not_with_its_conflict_context_is_irreducible_and_the_context_is_never_dropped()
    {
        var built = Revision(0, 10, alternativeWords: 300);   // ogni alternativa ~345 token; con le altre due come contesto ~1100 token

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(() => Planner(built, new PlannerLimits(900, 100_000)).PlanAsync(Chain, Ct).AsTask());

        Assert.Equal(DocumentWorkException.DocumentIncomplete, failure.Code);
        Assert.Contains(built.Alternatives[0].ToString("D"), failure.Message);
    }

    [Fact]
    public async Task The_output_estimate_bounds_a_unit_even_when_the_input_window_is_large()
    {
        var built = Revision(10, 100);
        var planner = Planner(built, new PlannerLimits(100_000, 400, OutputExpansion: 1.0));   // l'output stimato (parole dei principali) non puo' superare 400

        var units = await planner.PlanAsync(Chain, Ct);

        Assert.True(units.Count > 1);
        Assert.All(units, u => Assert.True(u.Measure.OwnedTokens <= 400));
        Assert.Equal(built.ItemIds, units.SelectMany(u => u.PrimaryItemIds));
    }

    [Fact]
    public async Task Replay_gives_the_same_units_inputs_and_hashes_and_a_changed_parent_changes_only_the_identities_that_depend_on_it()
    {
        var built = Revision(30, 60);
        var limits = new PlannerLimits(700, 100_000);

        var a = await Planner(built, limits).PlanAsync(Chain, Ct);
        var b = await Planner(built, limits).PlanAsync(Chain, Ct);

        Assert.Equal(a.Select(u => u.Derived.BatchId), b.Select(u => u.Derived.BatchId));
        Assert.Equal(a.Select(u => u.Derived.InputHash), b.Select(u => u.Derived.InputHash));
        Assert.Equal(a.Select(u => u.Measure.PayloadHash), b.Select(u => u.Measure.PayloadHash));
        Assert.Equal(a.Select(u => string.Join(',', u.PrimaryItemIds)), b.Select(u => string.Join(',', u.PrimaryItemIds)));
        Assert.Equal(a.Count, a.Select(u => u.Derived.BatchId).Distinct().Count());
        Assert.Null(a[1].Derived.Level);   // nessun livello: le unita' editoriali non sono una riduzione
        Assert.Equal(DocumentEditorialPlanner.Stage, a[0].Derived.Stage);
        Assert.Equal([Sha("output-0")], a[1].Derived.ParentOutputHashes);

        var c = await Planner(built, limits).PlanAsync(n => n == 0 ? [] : [Sha($"altro-{n - 1}")], Ct);

        Assert.Equal(a[0].Derived.BatchId, c[0].Derived.BatchId);       // senza genitori, identica
        Assert.NotEqual(a[1].Derived.BatchId, c[1].Derived.BatchId);    // i genitori Committed fanno parte dell'identita'
        Assert.Equal(a[1].Derived.InputHash, c[1].Derived.InputHash);   // ma il payload di input e' lo stesso
    }

    [Fact]
    public async Task Planning_is_incremental_and_append_only_the_next_unit_depends_only_on_the_items_after_the_previous_one()
    {
        var built = Revision(30, 60);
        var limits = new PlannerLimits(700, 100_000);
        var full = await Planner(built, limits).PlanAsync(Chain, Ct);
        var planner = Planner(built, limits);

        var first = await planner.NextAsync(0, 0, [], Ct);
        var second = await planner.NextAsync(first.PrimaryItemIds.Count, 1, [first.Derived.InputHash], Ct);

        Assert.Equal(full[0].PrimaryItemIds, first.PrimaryItemIds);
        Assert.Equal(full[1].PrimaryItemIds, second.PrimaryItemIds);   // riparte esattamente dopo il prefisso gia' impacchettato
        Assert.Equal(full[1].Derived.InputHash, second.Derived.InputHash);
        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    public async Task A_lost_or_duplicated_primary_makes_the_coverage_check_fail_with_the_item_id()
    {
        var built = Revision(30, 60);
        var planner = Planner(built, new PlannerLimits(700, 100_000));
        var units = await planner.PlanAsync(Chain, Ct);

        var lost = units.Skip(1).ToList();   // la prima unita' non e' stata prodotta
        var missing = Assert.Throws<DocumentWorkException>(() => planner.VerifyCovered(lost));
        Assert.Equal(DocumentWorkException.CoverageIncomplete, missing.Code);
        Assert.Contains(units[0].PrimaryItemIds[0].ToString("D"), missing.Message);

        var twice = Assert.Throws<DocumentWorkException>(() => planner.VerifyCovered([.. units, units[0]]));
        Assert.Equal(DocumentWorkException.CoverageIncomplete, twice.Code);
        planner.VerifyCovered(units);   // il piano intero e' coerente
    }

    [Fact]
    public async Task An_unavailable_token_count_is_runtime_unverified_never_an_estimate()
    {
        var built = Revision(3, 10);
        EditorialMeasurer unavailable = (_, _) => new ValueTask<UnitMeasure>(new UnitMeasure(0, TokenCountQuality.Unavailable, Sha("x"), 0));

        var failure = await Assert.ThrowsAsync<RuntimeUnverifiedException>(() => Planner(built, new PlannerLimits(1000, 1000), unavailable).PlanAsync(Chain, Ct).AsTask());

        Assert.Equal("runtime_unverified", failure.Code);
    }

    [Fact]
    public void A_duplicated_item_or_a_dangling_source_in_the_revision_is_an_explicit_integrity_error()
    {
        var built = Revision(2, 10);
        var duplicated = built.Content with { Requirements = [.. built.Content.Requirements, built.Content.Requirements[0]] };
        var dangling = built.Content with { Requirements = [built.Content.Requirements[0] with { EvidenceRefs = [Guid.NewGuid()] }] };

        Assert.Equal(DocumentWorkException.InvalidInput, Assert.Throws<DocumentWorkException>(() => new DocumentEditorialPlanner(duplicated, Owner, Manifest, "d", new PlannerLimits(1000, 1000), Measurer(), "s", "p")).Code);
        Assert.Equal(DocumentWorkException.IntegrityConflict, Assert.Throws<DocumentWorkException>(() => new DocumentEditorialPlanner(dangling, Owner, Manifest, "d", new PlannerLimits(1000, 1000), Measurer(), "s", "p")).Code);
    }
}
