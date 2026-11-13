using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-004 (T158) — contatore delle richieste di misura e scomposizione per misuratore, OFFLINE (nessuna rete, nessuna inferenza, nessun database).
/// Il piano di produzione <c>DocumentReconcilePlan</c> (v10 e v6) sul ledger della prova isolata Single (283 evidenze nel ledger, 282 nello snapshot: una è del documento escluso dal PM), profilo
/// W=32768, O=16384, M=1024, davanti a un runtime llama.cpp FINTO (tokenizer lunghezza/3,5, template a concatenazione) che conta <c>/apply-template</c> e <c>/tokenize</c> per tipo di richiesta,
/// fino al primo batch (<c>PrepareAsync(0)</c>: directory con foglie e pagine, più la rimisura del primo batch). Sono MISURE: i golden sono i valori OSSERVATI, non quelli attesi; le uguaglianze del §7
/// dell'assessment si verificano contro una ricostruzione indipendente (directory con i misuratori puri) e dove non valgono si asserisce ciò che vale.
/// </summary>
[Trait("Category", "Offline")]
public sealed class ReconcileMeasurementBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string V10 = DocumentAnalysisWorkflow.DefinitionVersionV10;
    private const string V6 = DocumentAnalysisWorkflow.DefinitionVersionV6;

    public static TheoryData<string, bool> Configurations() => new() { { V10, false }, { V10, true }, { V6, false }, { V6, true } };

    private sealed class NoOutputs : IBatchOutputs
    {
        public ValueTask<WorkOutput?> GetByOrdinalAsync(int ordinal, CancellationToken ct) => new((WorkOutput?)null);
    }

    internal static DocumentAnalysisInput SingleInput() => MeasurementLedger.Input(RetrievalIsolatedFixture.Build(RetrievalGapCampaign.Single));

    /// <summary>Esito di UNA misura: il piano di produzione preparato fino al primo batch e il registro del runtime finto.</summary>
    internal sealed record Measured(string Definition, bool Facts, IReadOnlyList<RuntimeRequest> Log, DerivedBatchInput First)
    {
        public int Apply(string kind) => Log.Count(r => r.Path == "/apply-template" && r.Kind == kind);

        public int Tokenize(string kind) => Log.Count(r => r.Path == "/tokenize" && r.Kind == kind);

        public int TokenizeRole(string kind, string role) => Log.Count(r => r.Path == "/tokenize" && r.Kind == kind && r.Role == role);

        public int Repeats(string kind) => Log.Count(r => r.Path == "/apply-template" && r.Kind == kind && r.Repeated);

        public int PagesOfKind(string pageKind) => Log.Count(r => r.Path == "/apply-template" && r.Kind == MeasureKinds.Page && r.View == pageKind);

        public int ApplyTotal => Log.Count(r => r.Path == "/apply-template");

        public int TokenizeTotal => Log.Count(r => r.Path == "/tokenize");

        public int Requests(string kind) => Apply(kind) + Tokenize(kind);

        /// <summary>Lunghezze delle sequenze crescenti 1,2,3… di voci delle misure di foglia (escluse le ripetute): ogni sequenza è il Pack di una foglia (k+1 misure, k per l'ultima della vista).</summary>
        public IReadOnlyList<int> LeafRuns()
        {
            var runs = new List<int>();
            foreach (var r in Log.Where(r => r.Path == "/apply-template" && r.Kind == MeasureKinds.Leaf && !r.Repeated))
            {
                if (r.Entries == 1)
                {
                    runs.Add(1);
                }
                else
                {
                    runs[^1]++;
                }
            }

            return runs;
        }
    }

    /// <summary>
    /// <c>DocumentReconcilePlan</c> e <c>ReconcileAliasMode</c> sono <c>internal</c> e l'assembly non espone gli internals ai test (e <c>backend/src</c> non si tocca): si costruiscono per riflessione e si usano
    /// attraverso l'interfaccia pubblica <see cref="IDocumentBatchPlan"/>. Il codice eseguito è quello di produzione.
    /// </summary>
    internal static IDocumentBatchPlan NewPlan(
        MeasurementRig rig, DocumentAnalysisInput input, IReadOnlyDictionary<Guid, DirectoryFacts> facts, AnalysisAliasCatalog catalog, string definition, DocumentLimits limits, IServiceScopeFactory scopes)
    {
        var assembly = typeof(DocumentAnalysisWorkflow).Assembly;
        var mode = assembly.GetType("bProjects.App.AI.ReconcileAliasMode", throwOnError: true)!;
        var alias = Activator.CreateInstance(
            mode, catalog, true, ReconcileAliasContracts.JsonSchema(), definition, DocumentAnalysisWorkflow.ReconcilePromptOf(definition),
            DocumentAnalysisWorkflow.ReconcileSchemaProfileOf(definition), DocumentAnalysisWorkflow.ReconcileExpansionOf(definition));
        var planType = assembly.GetType("bProjects.App.AI.DocumentReconcilePlan", throwOnError: true)!;
        var constructor = planType.GetConstructors().Single();
        var claim = new AnalysisRunClaim(MeasurementLedger.Id("run"), "owner", 1, 1);
        return (IDocumentBatchPlan)constructor.Invoke([claim, input, facts, rig.Preflight, rig.Profile, rig.Options, rig.Client, scopes, null, limits, alias, null]);
    }

    internal static async Task<Measured> MeasureAsync(string definition, bool facts, CancellationToken ct)
    {
        var input = SingleInput();
        using var rig = await MeasurementRig.CreateAsync(ct);
        var directoryFacts = facts ? MeasurementLedger.DeterministicFacts(input) : new Dictionary<Guid, DirectoryFacts>();
        var catalog = AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV3);
        var limits = new DocumentOptions().ToLimits();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var plan = NewPlan(rig, input, directoryFacts, catalog, definition, limits, scopes);
        var first = await plan.PrepareAsync(0, new NoOutputs(), ct);
        return new Measured(definition, facts, rig.Runtime.Log, first);
    }

    /// <summary>Directory costruita dal motore PURO di produzione con i misuratori puri (<see cref="PureReconcileMeasure"/>): ricostruzione INDIPENDENTE di foglie e pagine, senza rete.</summary>
    internal static async Task<ReconciliationDirectoryResult> PureDirectoryAsync(
        DocumentAnalysisInput input, IReadOnlyDictionary<Guid, DirectoryFacts> facts, string definition, DocumentRuntimeProfile profile, CancellationToken ct)
    {
        var catalog = AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV3);
        var pure = new PureReconcileMeasure(input, catalog, definition);
        var context = new ReconciliationContext(
            "owner", "manifest", definition, new PlannerLimits(Math.Max(1, profile.InputBudgetTokens - 256), profile.OutputTokens, OutputExpansion: 1.0), ReconcileAliasContracts.SchemaVersion,
            DocumentAnalysisWorkflow.ReconcilePromptOf(definition), 32);
        return await ReconciliationDirectory.BuildAsync(input.Snapshot, facts, context, pure.Measurers, ct);
    }

    /// <summary>Misure attese dalla directory ricostruita: <c>Naive</c> = Σ(k+1)−1 per vista e Σ(voci+interruzioni) per catena; <c>Distinct</c> = prefissi DISTINTI (la cache <c>_measures</c> del piano è per chiave di contenuto, non per vista).</summary>
    internal sealed record Expected(int LeafNaive, int LeafDistinct, int PageNaive, int PageDistinct, IReadOnlyDictionary<string, int> PageDistinctByKind, IReadOnlyList<int> LeafRuns, IReadOnlyDictionary<ReconcileView, IReadOnlyList<int>> LeafSizes);

    internal static Expected Expect(ReconciliationDirectoryResult directory)
    {
        int leafNaive = 0, pageNaive = 0;
        var leafSeen = new HashSet<string>(StringComparer.Ordinal);
        var pageSeen = new Dictionary<string, HashSet<string>> { ["Members"] = new(StringComparer.Ordinal), ["Keys"] = new(StringComparer.Ordinal), ["Relations"] = new(StringComparer.Ordinal) };
        var sizes = new Dictionary<ReconcileView, IReadOnlyList<int>>();
        var runs = new List<int>();
        var runSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in new[] { ReconcileView.A, ReconcileView.B })
        {
            var leaves = directory.Leaves.Where(l => l.View == view).OrderBy(l => l.Ordinal).ToList();
            sizes[view] = [.. leaves.Select(l => l.Entries.Count)];
            for (var i = 0; i < leaves.Count; i++)
            {
                var ids = leaves[i].Entries.Select(e => e.EntryId).ToList();
                var run = 0;
                for (var j = 1; j <= ids.Count; j++)
                {
                    leafNaive++;
                    if (leafSeen.Add(string.Join(',', ids.Take(j))))
                    {
                        run++;
                    }
                }

                if (i < leaves.Count - 1)
                {
                    leafNaive++;
                    if (leafSeen.Add(string.Join(',', ids.Append(leaves[i + 1].Entries[0].EntryId))))
                    {
                        run++;
                    }
                }

                if (run > 0)
                {
                    runs.Add(run);
                }

                foreach (var kind in new[] { CatalogPageKind.Members, CatalogPageKind.Keys, CatalogPageKind.Relations })
                {
                    var pages = leaves[i].Pages.Where(p => p.Kind == kind).OrderBy(p => p.Ordinal).ToList();
                    for (var p = 0; p < pages.Count; p++)
                    {
                        string Key(IReadOnlyList<PageEntry> entries) =>
                            ReconciliationCodec.Canonical(new CatalogPage { PageId = "x", NodeId = "x", Kind = kind, Ordinal = 0, Entries = entries, PreviousPageHash = null });
                        for (var j = 1; j <= pages[p].Entries.Count; j++)
                        {
                            pageNaive++;
                            pageSeen[kind.ToString()].Add(Key([.. pages[p].Entries.Take(j)]));
                        }

                        if (p < pages.Count - 1)
                        {
                            pageNaive++;
                            pageSeen[kind.ToString()].Add(Key([.. pages[p].Entries, pages[p + 1].Entries[0]]));
                        }
                    }
                }
            }
        }

        return new Expected(leafNaive, leafSeen.Count, pageNaive, pageSeen.Values.Sum(s => s.Count), pageSeen.ToDictionary(kv => kv.Key, kv => kv.Value.Count), runs, sizes);
    }

    // ================================================================ meccanismo elementare

    [Fact]
    public async Task One_payload_measure_costs_one_apply_template_and_one_tokenize_per_text_plus_one_per_schema()
    {
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var before = (Apply: rig.Runtime.Count("/apply-template"), Tokenize: rig.Runtime.Count("/tokenize"));
        Assert.Equal((1, 1), before);                                   // la sola sonda del monitor (capacità di conteggio), una per tutta la misura

        var messages = new[] { new ChatMessage(ChatRole.User, "Tipo di batch: foglia\nVista: A\n<<<VOCE [E1] x") };
        await rig.Preflight.MeasureAsync(new DocumentPayload(AiPhase.Analysis, "istruzioni", messages, "{\"type\":\"object\"}"), Ct, rig.Profile.Fingerprint);
        Assert.Equal((2, 3), (rig.Runtime.Count("/apply-template"), rig.Runtime.Count("/tokenize")));          // con lo schema: 1 apply-template, 2 tokenize

        await rig.Preflight.MeasureAsync(new DocumentPayload(AiPhase.Analysis, "istruzioni", [new ChatMessage(ChatRole.User, "{\"pageId\":\"x\"}")], null), Ct, rig.Profile.Fingerprint);
        Assert.Equal((3, 4), (rig.Runtime.Count("/apply-template"), rig.Runtime.Count("/tokenize")));          // senza schema (pagina): 1 + 1

        Assert.Equal((1, 1), (rig.Runtime.Count("/apply-template", MeasureKinds.Leaf), rig.Runtime.Count("/apply-template", MeasureKinds.Page)));
        Assert.Equal((1, 1), (rig.Runtime.Count("/tokenize", MeasureKinds.Leaf, "rendered"), rig.Runtime.Count("/tokenize", MeasureKinds.Leaf, "extra")));
        Assert.Equal(1, rig.Runtime.Count("/tokenize", MeasureKinds.Page, "rendered"));
        Assert.Equal(1, FakeTokenizer.Tokens("x"));
        Assert.Equal(3, FakeTokenizer.Tokens("12345678"));                                                  // 8 / 3,5 = 2,29, per eccesso
    }

    [Fact]
    public void The_classifier_reads_the_batch_type_from_the_rendered_user_message()
    {
        Assert.Equal(MeasureKinds.Leaf, MeasureKinds.Classify("Tipo di batch: foglia\nVista: A\n"));
        Assert.Equal(MeasureKinds.Reduce, MeasureKinds.Classify("Tipo di batch: riduzione\nVista: A\n"));
        Assert.Equal(MeasureKinds.Cross, MeasureKinds.Classify("Tipo di batch: cross\nVista: Cross\n"));
        Assert.Equal(MeasureKinds.Expand, MeasureKinds.Classify("Tipo di batch: espansione\nVista: A\n"));
        Assert.Equal(MeasureKinds.Compare, MeasureKinds.Classify("Tipo di batch: confronto\nVista: A\n"));
        Assert.Equal(MeasureKinds.Page, MeasureKinds.Classify("{\"entries\":[]}"));
        Assert.Equal(MeasureKinds.Other, MeasureKinds.Classify("probe"));
        Assert.Equal(MeasureKinds.Other, MeasureKinds.Classify("Tipo di batch: sconosciuto\n"));
    }

    [Fact]
    public void The_isolated_single_ledger_has_283_evidences_of_which_282_enter_the_snapshot()
    {
        var ledger = RetrievalIsolatedFixture.Build(RetrievalGapCampaign.Single);
        Assert.Equal(283, ledger.EvidenceCount);
        Assert.Equal(1, ledger.Documents.Where(d => d.ExcludedByPm).Sum(d => d.Evidences.Count));       // il documento escluso dal PM: fuori dallo snapshot
        var input = SingleInput();
        Assert.Equal(282, input.Evidence.Count);
        Assert.Equal(5, input.Evidence.Count(e => e.Kind == EvidenceKind.Gap));
        Assert.Equal(input.Evidence.Count, input.Snapshot.Evidence.Count);
    }

    // ================================================================ meccanismo del §7 sul piano di produzione

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task The_mechanism_of_section_7_holds_on_the_production_plan_with_the_corrections_the_measure_requires(string definition, bool facts)
    {
        var m = await MeasureAsync(definition, facts, Ct);
        var input = SingleInput();
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var directoryFacts = facts ? MeasurementLedger.DeterministicFacts(input) : new Dictionary<Guid, DirectoryFacts>();
        var expected = Expect(await PureDirectoryAsync(input, directoryFacts, definition, rig.Profile, Ct));

        var leafPack = m.Apply(MeasureKinds.Leaf) - m.Repeats(MeasureKinds.Leaf);       // misure del Pack delle foglie (la rimisura del primo batch è una ripetizione dell'ultimo prefisso)
        var pages = m.Apply(MeasureKinds.Page);
        var remeasures = m.Repeats(MeasureKinds.Leaf);
        var probes = m.Apply(MeasureKinds.Other);

        // La directory ricostruita in modo indipendente coincide con quella del piano: stesse foglie (sequenze di Pack osservate = prefissi attesi).
        Assert.Equal(expected.LeafRuns, m.LeafRuns());
        Assert.Equal(1, remeasures);                                                    // PrepareAsync(0) rimisura il solo primo batch
        Assert.Equal(1, probes);

        // (1) apply-template(foglia) = Σ foglie (k+1) − 1 per vista: vale per i PREFISSI DISTINTI. Con le due viste ordinate allo stesso modo (DirectoryFacts vuoti) i prefissi della vista B sono quelli
        // della A (la chiave della cache _measures non contiene la vista): la B costa zero e la formula per vista sull'insieme delle viste sovrastima del costo della B.
        Assert.Equal(expected.LeafDistinct, leafPack);
        Assert.Equal(facts, expected.LeafNaive == leafPack);                            // la formula per vista vale solo se le viste hanno ordini diversi (DirectoryFacts non vuoti)
        Assert.Equal(!facts, leafPack == expected.LeafNaive / 2);                        // senza fatti: costo della sola vista A (283 = 566 / 2)

        // (2) apply-template(pagina) = Σ catene (voci + interruzioni): vale per i prefissi distinti; i Members delle foglie con gli stessi primi membri ordinati per id coincidono fra le viste.
        Assert.Equal(expected.PageDistinct, pages);
        Assert.Equal(expected.PageDistinctByKind["Members"], m.PagesOfKind("Members"));
        Assert.Equal(expected.PageDistinctByKind["Keys"], m.PagesOfKind("Keys"));
        Assert.Equal(0, m.PagesOfKind("Relations"));
        Assert.True(pages <= expected.PageNaive);

        // (3) tokenize = 2·foglie + pagine + 2·rimisure (+ la sonda del monitor): 1 per il testo reso e 1 per lo schema di ogni misura di foglia; 1 per ogni pagina (nessuno schema).
        Assert.Equal(2 * leafPack + pages + 2 * remeasures + probes, m.TokenizeTotal);
        Assert.Equal(m.Apply(MeasureKinds.Leaf), m.TokenizeRole(MeasureKinds.Leaf, "extra"));      // un tokenize di schema per ogni misura con schema
        Assert.Equal(m.Apply(MeasureKinds.Leaf), m.TokenizeRole(MeasureKinds.Leaf, "rendered"));
        Assert.Equal(0, m.TokenizeRole(MeasureKinds.Page, "extra"));
        Assert.Equal(m.TokenizeTotal - m.ApplyTotal, m.Apply(MeasureKinds.Leaf));                  // tokenize − apply-template = misure con schema

        // Fino al primo batch non esistono richieste di riduzione, cross, espansione o confronto.
        foreach (var kind in new[] { MeasureKinds.Reduce, MeasureKinds.Cross, MeasureKinds.Expand, MeasureKinds.Compare })
        {
            Assert.Equal(0, m.Requests(kind));
        }
    }

    // ================================================================ golden dei conteggi (valori OSSERVATI)

    /// <summary>Per configurazione: (apply foglia, apply pagina Members, apply pagina Keys, apply altro, tokenize foglia, tokenize pagina, tokenize altro, Σ voci delle foglie A, B).</summary>
    private static readonly Dictionary<(string Definition, bool Facts), (int LeafApply, int Members, int Keys, int OtherApply, int LeafTokenize, int PageTokenize, int OtherTokenize, string LeavesA, string LeavesB)> Golden = new()
    {
        [(V10, false)] = (284, 282, 0, 1, 568, 282, 1, "150,132", "150,132"),
        [(V10, true)] = (567, 521, 269, 1, 1134, 790, 1, "151,131", "151,131"),
        [(V6, false)] = (284, 282, 0, 1, 568, 282, 1, "154,128", "154,128"),
        [(V6, true)] = (567, 521, 269, 1, 1134, 790, 1, "155,127", "155,127"),
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task The_request_counts_before_the_first_batch_are_the_observed_golden_and_are_deterministic(string definition, bool facts)
    {
        var first = await MeasureAsync(definition, facts, Ct);
        var second = await MeasureAsync(definition, facts, Ct);

        // Determinismo: due esecuzioni, stessi contatori per percorso, tipo e ruolo, stessa sequenza di richieste e stesso primo batch.
        Assert.Equal(first.Log.Select(r => (r.Path, r.Kind, r.Role, r.Entries, r.View, r.Repeated, r.Characters)), second.Log.Select(r => (r.Path, r.Kind, r.Role, r.Entries, r.View, r.Repeated, r.Characters)));
        Assert.Equal((first.First.BatchId, first.First.InputHash, first.First.MeasuredTokens, first.First.Ordinal), (second.First.BatchId, second.First.InputHash, second.First.MeasuredTokens, second.First.Ordinal));
        Assert.True(first.First.InputIdsJson == second.First.InputIdsJson);

        var g = Golden[(definition, facts)];
        Assert.Equal(g.LeafApply, first.Apply(MeasureKinds.Leaf));
        Assert.Equal(g.Members, first.PagesOfKind("Members"));
        Assert.Equal(g.Keys, first.PagesOfKind("Keys"));
        Assert.Equal(g.OtherApply, first.Apply(MeasureKinds.Other));
        Assert.Equal(g.LeafTokenize, first.Tokenize(MeasureKinds.Leaf));
        Assert.Equal(g.PageTokenize, first.Tokenize(MeasureKinds.Page));
        Assert.Equal(g.OtherTokenize, first.Tokenize(MeasureKinds.Other));
        Assert.Equal(g.LeafApply + g.Members + g.Keys + g.OtherApply, first.ApplyTotal);
        Assert.Equal(g.LeafTokenize + g.PageTokenize + g.OtherTokenize, first.TokenizeTotal);

        using var rig = await MeasurementRig.CreateAsync(Ct);
        var input = SingleInput();
        var expected = Expect(await PureDirectoryAsync(input, facts ? MeasurementLedger.DeterministicFacts(input) : new Dictionary<Guid, DirectoryFacts>(), definition, rig.Profile, Ct));
        Assert.Equal(g.LeavesA, string.Join(',', expected.LeafSizes[ReconcileView.A]));
        Assert.Equal(g.LeavesB, string.Join(',', expected.LeafSizes[ReconcileView.B]));
    }

    /// <summary>Le quote del criterio O3: le richieste dovute alle pagine sono quelle del tipo «pagina»; il resto è foglia e sonda.</summary>
    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task The_page_share_of_the_requests_is_at_least_30_percent_so_option_O3_is_not_closed_by_the_measure(string definition, bool facts)
    {
        var m = await MeasureAsync(definition, facts, Ct);
        var total = m.ApplyTotal + m.TokenizeTotal;
        var pageShare = 100.0 * m.Requests(MeasureKinds.Page) / total;
        Assert.InRange(pageShare, 30.0, 100.0);
        Assert.Equal(facts ? 48.1 : 39.8, Math.Round(pageShare, 1));
    }

    // ================================================================ tabella delle evidenze (non è un test: stampa e salva)

    [Fact]
    public async Task Evidence_table_for_M0_is_written()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Definizione | DirectoryFacts | Tipo | `/apply-template` | `/tokenize` | Totale | Quota |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|");
        foreach (var (definition, facts) in new[] { (V10, false), (V10, true), (V6, false), (V6, true) })
        {
            var m = await MeasureAsync(definition, facts, Ct);
            var total = m.ApplyTotal + m.TokenizeTotal;
            foreach (var kind in MeasureKinds.All)
            {
                var requests = m.Requests(kind);
                var label = kind switch
                {
                    MeasureKinds.Page => $"pagina (Members {m.PagesOfKind("Members")}, Keys {m.PagesOfKind("Keys")})",
                    MeasureKinds.Other => "altro (sonda del monitor)",
                    MeasureKinds.Leaf => "foglia (Pack + 1 rimisura)",
                    _ => kind,
                };
                sb.AppendLine($"| {definition.Replace("project-analysis-documents-", string.Empty, StringComparison.Ordinal)} | {(facts ? "deterministici" : "vuoti")} | {label} | {m.Apply(kind)} | {m.Tokenize(kind)} | {requests} | {100.0 * requests / total:0.0}% |");
            }

            sb.AppendLine($"| {definition.Replace("project-analysis-documents-", string.Empty, StringComparison.Ordinal)} | {(facts ? "deterministici" : "vuoti")} | **totale** | **{m.ApplyTotal}** | **{m.TokenizeTotal}** | **{total}** | 100% |");
        }

        var directory = Path.Combine(Path.GetTempPath(), "bprojects-reconcile-measurement");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "rh004-evidence.md"), sb.ToString());
        TestContext.Current.SendDiagnosticMessage(sb.ToString());
        Assert.NotEmpty(sb.ToString());
    }
}
