using System.Globalization;
using bProjects.App.Analysis;

namespace bProjects.App.AI;

// Planner editoriale della generazione documentale oltre contesto (002 T085; contracts/ai-workflow.md §4.5 «Generazione», plan.md «Generare unità editoriali»).
// Funzione pura della revisione confermata e del misuratore: nessun Guid né orologio, nessun accesso a database o modello, quindi il replay ricostruisce gli stessi
// input e gli stessi hash. Non riassume né compatta la revisione: ogni item integrale è «principale» di UNA sola unità; ciò che serve a leggerlo (le altre alternative
// dei suoi conflitti come contesto, il conflitto con stato e risoluzione motivata, le fonti con la citazione) viaggia con l'unità e conta nel payload misurato.

/// <summary>Item integrale della revisione confermata nell'ordine editoriale stabile; <see cref="ConflictId"/> è valorizzato per le alternative di un conflitto.</summary>
public sealed record EditorialItem(
    Guid Id, string SectionKey, string Text, DocumentAnalysisClassification Classification, IReadOnlyList<Guid> SourceIds, Guid? ConflictId);

/// <summary>Conflitto con le sue alternative, lo stato e la risoluzione motivata del PM (qualificatori di lettura): mai omesso per un item che vi partecipa.</summary>
public sealed record EditorialConflict(
    Guid Id, string Description, AnalysisConflictStatus Status, IReadOnlyList<Guid> AlternativeIds, IReadOnlyList<Guid> SelectedAlternativeIds, string? ResolutionReason);

/// <summary>Ciò che il messaggio di un'unità deve portare: item principali, contesto non principale, conflitti e fonti (con le citazioni letterali) necessari.</summary>
public sealed record EditorialSlice(
    IReadOnlyList<EditorialItem> Primary, IReadOnlyList<EditorialItem> Context, IReadOnlyList<EditorialConflict> Conflicts, IReadOnlyList<DocumentAnalysisSource> Sources);

/// <summary>Misura il payload COMPLETO di una fetta (istruzioni, schema, framing, contesto replicato inclusi); <c>OwnedTokens</c> sono i token dei soli item principali.</summary>
public delegate ValueTask<UnitMeasure> EditorialMeasurer(EditorialSlice slice, CancellationToken ct);

/// <summary>Unità editoriale pianificata: identità append-only (batch derivato con hash dei genitori) e membership verificabile.</summary>
public sealed record EditorialUnit(
    int Ordinal, bool SingleCall, IReadOnlyList<Guid> PrimaryItemIds, IReadOnlyList<Guid> ContextItemIds, IReadOnlyList<Guid> ConflictIds, IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<string> SectionKeys, UnitMeasure Measure, DerivedBatchInput Derived);

public sealed class DocumentEditorialPlanner
{
    /// <summary>Stadio dei batch derivati editoriali (<see cref="DerivedBatchInput.Stage"/>).</summary>
    public const string Stage = "draft";

    private readonly DocumentAnalysisContent _content;
    private readonly PlannerLimits _limits;
    private readonly EditorialMeasurer _measure;
    private readonly string _owner;
    private readonly string _manifestIdentity;
    private readonly string _definition;
    private readonly string _schemaVersion;
    private readonly string _promptVersion;
    private readonly Dictionary<Guid, EditorialItem> _byId = [];
    private readonly Dictionary<Guid, EditorialConflict> _conflicts = [];
    private readonly Dictionary<Guid, DocumentAnalysisSource> _sources;

    public DocumentEditorialPlanner(
        DocumentAnalysisContent content, string owner, string manifestIdentity, string definition, PlannerLimits limits, EditorialMeasurer measure, string schemaVersion,
        string promptVersion)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _owner = owner;
        _manifestIdentity = manifestIdentity;
        _definition = definition;
        _limits = limits;
        _measure = measure;
        _schemaVersion = schemaVersion;
        _promptVersion = promptVersion;
        _sources = content.Sources.ToDictionary(s => s.Id);
        Items = Build();
    }

    /// <summary>Item principali nell'ordine editoriale stabile: sezioni nell'ordine fisso della revisione, poi le alternative dei conflitti nell'ordine dei conflitti.</summary>
    public IReadOnlyList<EditorialItem> Items { get; }

    /// <summary>
    /// Tutta la revisione in una sola chiamata se il payload completo entra nel budget (nessuna compattazione); altrimenti null e si procede per unità con
    /// <see cref="NextAsync"/>. Una revisione senza item produce comunque un'unica unità vuota misurata.
    /// </summary>
    public async ValueTask<EditorialUnit?> TrySingleCallAsync(CancellationToken ct)
    {
        var slice = SliceOf(Items);
        var measure = await MeasureAsync(slice, ct);
        if (!_limits.Fits(measure))
        {
            return null;
        }

        return Unit(new PackedBatch<EditorialItem>(0, Items, measure), slice, singleCall: true, []);
    }

    /// <summary>
    /// Prossima unità a partire dall'item <paramref name="start"/>: massimo prefisso contiguo i cui item principali, con contesto, conflitti e fonti necessari, entrano nel
    /// budget MISURATO sul payload completo (Pack T069). Un item che da solo non entra è <see cref="DocumentWorkException.DocumentIncomplete"/> con il suo ID: nessuna
    /// compattazione, nessun taglio e nessun aumento della finestra. <paramref name="parentOutputHashes"/> sono gli output Committed da cui l'unità dipende
    /// (append-only): fanno parte dell'identità del batch.
    /// </summary>
    public async ValueTask<EditorialUnit> NextAsync(int start, int ordinal, IReadOnlyList<string> parentOutputHashes, CancellationToken ct)
    {
        var packed = await DocumentBatchPacking.PackNextAsync(
            Items, start, ordinal, 1, _limits, IdOf, async (members, token) => await MeasureAsync(SliceOf(members), token), ct);
        return Unit(packed, SliceOf(packed.Members), singleCall: false, parentOutputHashes);
    }

    /// <summary>
    /// Piano completo (anteprima deterministica e replay): singola chiamata se entra, altrimenti unità contigue. <paramref name="parentsOf"/> dà i genitori Committed
    /// dell'unità con quell'ordinale (di norma l'hash dell'output della precedente); la verifica di copertura chiude il piano.
    /// </summary>
    public async ValueTask<IReadOnlyList<EditorialUnit>> PlanAsync(Func<int, IReadOnlyList<string>> parentsOf, CancellationToken ct)
    {
        if (await TrySingleCallAsync(ct) is { } single)
        {
            return [single];
        }

        var units = new List<EditorialUnit>();
        var start = 0;
        while (start < Items.Count)
        {
            var unit = await NextAsync(start, units.Count, parentsOf(units.Count), ct);
            units.Add(unit);
            start += unit.PrimaryItemIds.Count;
        }

        VerifyCovered(units);
        return units;
    }

    /// <summary>Ogni item della revisione è principale di esattamente una unità: nessuno perso o duplicato. Altrimenti <see cref="DocumentWorkException.CoverageIncomplete"/> con l'ID.</summary>
    public void VerifyCovered(IEnumerable<EditorialUnit> units)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var id in units.SelectMany(u => u.PrimaryItemIds))
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        var missing = Items.Where(i => !counts.ContainsKey(i.Id)).Select(i => i.Id).ToList();
        var duplicated = counts.Where(c => c.Value > 1 || !_byId.ContainsKey(c.Key)).Select(c => c.Key).ToList();
        if (missing.Count > 0 || duplicated.Count > 0)
        {
            throw new DocumentWorkException(
                DocumentWorkException.CoverageIncomplete,
                $"Copertura editoriale non valida: {missing.Count} item senza unità principale (primo: {(missing.Count > 0 ? missing[0].ToString("D") : "n/d")}), "
                + $"{duplicated.Count} item duplicati o estranei (primo: {(duplicated.Count > 0 ? duplicated[0].ToString("D") : "n/d")}).");
        }
    }

    /// <summary>
    /// Fetta di un insieme contiguo di item principali: per ogni conflitto cui partecipano, il conflitto con stato e risoluzione e le alternative NON principali come
    /// contesto; le fonti di principali e contesto, nell'ordine della revisione. Funzione pura: stesso insieme, stessa fetta.
    /// </summary>
    public EditorialSlice SliceOf(IReadOnlyList<EditorialItem> primaries)
    {
        var primaryIds = primaries.Select(p => p.Id).ToHashSet();
        var conflictIds = primaries.Where(p => p.ConflictId is not null).Select(p => p.ConflictId!.Value).Distinct().ToList();
        var conflicts = _content.Conflicts.Where(c => conflictIds.Contains(c.Id)).Select(c => _conflicts[c.Id]).ToList();
        var context = conflicts.SelectMany(c => c.AlternativeIds).Where(id => !primaryIds.Contains(id)).Distinct().Select(id => _byId[id]).ToList();
        var sourceIds = primaries.Concat(context).SelectMany(i => i.SourceIds).ToHashSet();
        var sources = _content.Sources.Where(s => sourceIds.Contains(s.Id)).ToList();
        return new EditorialSlice(primaries, context, conflicts, sources);
    }

    private static string IdOf(EditorialItem item) => item.Id.ToString("D");

    private async ValueTask<UnitMeasure> MeasureAsync(EditorialSlice slice, CancellationToken ct)
    {
        var measure = await _measure(slice, ct);
        return measure.Quality == TokenCountQuality.Unavailable
            ? throw new RuntimeUnverifiedException("conteggio del payload della generazione non disponibile (nessun tokenizer o calibrazione identificata)")
            : measure;
    }

    private EditorialUnit Unit(PackedBatch<EditorialItem> packed, EditorialSlice slice, bool singleCall, IReadOnlyList<string> parentOutputHashes)
    {
        var derived = DocumentBatchPacking.ToDerived(
            packed, _owner, _manifestIdentity, _definition, Stage, null, null, parentOutputHashes, IdOf, _schemaVersion, _promptVersion);
        return new EditorialUnit(
            packed.Ordinal, singleCall, [.. packed.Members.Select(i => i.Id)], [.. slice.Context.Select(i => i.Id)], [.. slice.Conflicts.Select(c => c.Id)],
            [.. slice.Sources.Select(s => s.Id)], [.. packed.Members.Select(i => i.SectionKey).Distinct(StringComparer.Ordinal)], packed.Measure!, derived);
    }

    private List<EditorialItem> Build()
    {
        var items = new List<EditorialItem>();
        void Section(string key, IReadOnlyList<DocumentAnalysisItem> section, Guid? conflict = null)
        {
            foreach (var item in section)
            {
                foreach (var source in item.EvidenceRefs.Where(r => !_sources.ContainsKey(r)))
                {
                    throw new DocumentWorkException(
                        DocumentWorkException.IntegrityConflict, $"L'item {item.Id:D} cita la fonte {source:D}, assente dalla revisione confermata.");
                }

                var entry = new EditorialItem(item.Id, key, item.Text, item.Classification, [.. item.EvidenceRefs], conflict);
                if (!_byId.TryAdd(item.Id, entry))
                {
                    throw new DocumentWorkException(DocumentWorkException.InvalidInput, $"L'item {item.Id:D} compare più volte nella revisione confermata.");
                }

                items.Add(entry);
            }
        }

        Section("goals", _content.Goals);
        Section("contextAndScope.overview", _content.ContextAndScope.Overview);
        Section("contextAndScope.included", _content.ContextAndScope.Included);
        Section("contextAndScope.excluded", _content.ContextAndScope.Excluded);
        Section("requirements", _content.Requirements);
        Section("operationalFlows", _content.OperationalFlows);
        Section("constraints", _content.Constraints);
        Section("dependencies", _content.Dependencies);
        Section("assumptions", _content.Assumptions);
        Section("openQuestions", _content.OpenQuestions);
        foreach (var conflict in _content.Conflicts)
        {
            Section("conflicts", conflict.Alternatives, conflict.Id);
            _conflicts[conflict.Id] = new EditorialConflict(
                conflict.Id, conflict.Description, conflict.Status, [.. conflict.Alternatives.Select(a => a.Id)],
                [.. conflict.Resolution?.SelectedAlternativeIds ?? []], conflict.Resolution?.Reason);
        }

        return items;
    }

    /// <summary>Descrizione stabile di una unità per diagnostica non sensibile (ID e conteggi, mai testo).</summary>
    public static string Describe(EditorialUnit unit) =>
        string.Create(CultureInfo.InvariantCulture, $"unità {unit.Ordinal}: {unit.PrimaryItemIds.Count} principali, {unit.ContextItemIds.Count} di contesto, {unit.SourceIds.Count} fonti");
}
