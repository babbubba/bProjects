using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-016 (M1) — equivalenza schema ⇔ compositore ⇔ validazione per la definizione v11. Con il generatore di RH-003 (seed 20261113) si producono 2.500 istanze AMMESSE dallo schema
/// <c>reconcile-alias-schema-by-stage-1</c> per ognuna delle sei varianti, composte con le opzioni del contratto per stadio e validate con V2, riferimenti e gerarchia. Criterio: ogni istanza ammessa dalla grammatica
/// è accettata, oppure rifiutata dal SOLO compositore con classi RESIDUE dichiarate (<c>UnknownAlias</c>, <c>CoincidentEndpoints</c>, <c>Duplicate</c>, <c>DuplicateCandidate</c>, <c>InvalidText</c>, <c>PatchShape</c>);
/// <c>StageShape</c>, <c>ItemShape</c>, <c>Undecodable</c> e <c>IntegrityMismatch</c> non compaiono mai; nessun rifiuto di <c>CheckResult</c>, riferimenti o gerarchia su un'istanza accettata dal compositore.
/// La baseline del profilo -3 (59–61% di <c>Undecodable</c> in Expand e Compare, RH-003) si legge dal golden di M0 e si confronta: la classe sparisce perché la grammatica lega <c>value</c> a <c>op</c>.
/// Limite dichiarato: l'equivalenza è con lo schema valutato da <see cref="ReconcileSchemaSubset"/>, non con la grammatica reale di llama.cpp (sonda RH-050, M5). Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileStageContractV11Tests
{
    private const int Instances = 2_500;

    private static readonly ReconcileViolationKind[] ResidualClasses =
    [
        ReconcileViolationKind.UnknownAlias, ReconcileViolationKind.CoincidentEndpoints, ReconcileViolationKind.Duplicate, ReconcileViolationKind.DuplicateCandidate,
        ReconcileViolationKind.InvalidText, ReconcileViolationKind.PatchShape,
    ];

    public static TheoryData<ReconcileBatchSchema> Variants() => new()
    {
        ReconcileBatchSchema.LeafA, ReconcileBatchSchema.LeafB, ReconcileBatchSchema.Reduce, ReconcileBatchSchema.Cross, ReconcileBatchSchema.Expand, ReconcileBatchSchema.Compare,
    };

    internal sealed record Summary(ReconcileBatchSchema Variant, int Instances, int Accepted, int RejectedByComposer, int RejectedByValidator, IReadOnlyDictionary<ReconcileViolationKind, int> Kinds, IReadOnlyList<string> Unexpected)
    {
        public string Render()
        {
            var sb = new StringBuilder($"variant={Variant}\ninstances={Instances}\naccepted={Accepted}\nrejectedByComposer={RejectedByComposer}\nrejectedByValidator={RejectedByValidator}");
            foreach (var (kind, count) in Kinds.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            {
                sb.Append($"\n{kind} = {count}");
            }

            return sb.ToString();
        }
    }

    internal static Summary Run(ReconcileBatchSchema variant, int instances = Instances)
    {
        var world = ReconcileBatchWorld.Create(variant, ContractProfile.V11);
        var generator = new ReconcileSchemaInstanceGenerator(ReconcileSchemaInstanceGenerator.Seed, ReconcileAliasUniverse.For(world));
        var kinds = new SortedDictionary<ReconcileViolationKind, int>();
        var unexpected = new List<string>();
        int accepted = 0, byComposer = 0, byValidator = 0;
        for (var i = 0; i < instances; i++)
        {
            var json = generator.Generate(world.Schema);
            var verdict = world.Evaluate(json);
            Assert.True(verdict.SchemaAdmits, $"{variant} #{i}: l'istanza non è ammessa dallo schema ({string.Join(" | ", verdict.SchemaViolations)}): {json}");
            if (verdict.Composition.IsValid)
            {
                // Accettata dal compositore: validatore canonico (V2), riferimenti, visite, descriptor e gerarchia non devono rifiutarla.
                if (verdict.ValidatorViolations.Count > 0 || verdict.HierarchyError is not null)
                {
                    byValidator++;
                    unexpected.Add($"#{i} rifiutata a valle del compositore: {verdict.Report} :: {json}");
                }
                else
                {
                    accepted++;
                }

                continue;
            }

            byComposer++;
            foreach (var kind in verdict.Composition.TypedViolations.Select(v => v.Kind).Distinct())
            {
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                if (!ResidualClasses.Contains(kind))
                {
                    unexpected.Add($"#{i} classe non dichiarata {kind}: {string.Join(" | ", verdict.ComposerViolations)} :: {json}");
                }
            }
        }

        return new Summary(variant, instances, accepted, byComposer, byValidator, kinds, unexpected);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ReconcileBatchSchema, Lazy<Summary>> Cache = new();

    internal static Summary Full(ReconcileBatchSchema variant) => Cache.GetOrAdd(variant, v => new Lazy<Summary>(() => Run(v))).Value;

    /// <summary>
    /// Tabella delle classi residue per variante (seed 20261113, 2.500 istanze): numero di istanze rifiutate dal compositore in cui compare ciascuna classe (un'istanza con più violazioni conta in ciascuna).
    /// È il dato per l'opzione O1 (retry): una classe nuova o un conteggio diverso richiede una diagnosi (STOP di RH-016), non un ritocco.
    /// </summary>
    internal static readonly IReadOnlyDictionary<ReconcileBatchSchema, string> Pinned = new Dictionary<ReconcileBatchSchema, string>
    {
        [ReconcileBatchSchema.LeafA] =
            """
            variant=LeafA
            instances=2500
            accepted=643
            rejectedByComposer=1857
            rejectedByValidator=0
            CoincidentEndpoints = 610
            Duplicate = 649
            DuplicateCandidate = 205
            InvalidText = 1080
            UnknownAlias = 1038
            """,
        [ReconcileBatchSchema.LeafB] =
            """
            variant=LeafB
            instances=2500
            accepted=643
            rejectedByComposer=1857
            rejectedByValidator=0
            CoincidentEndpoints = 610
            Duplicate = 649
            DuplicateCandidate = 205
            InvalidText = 1080
            UnknownAlias = 1038
            """,
        [ReconcileBatchSchema.Reduce] =
            """
            variant=Reduce
            instances=2500
            accepted=599
            rejectedByComposer=1901
            rejectedByValidator=0
            CoincidentEndpoints = 850
            Duplicate = 649
            DuplicateCandidate = 155
            InvalidText = 1080
            UnknownAlias = 1038
            """,
        [ReconcileBatchSchema.Cross] =
            """
            variant=Cross
            instances=2500
            accepted=756
            rejectedByComposer=1744
            rejectedByValidator=0
            CoincidentEndpoints = 902
            Duplicate = 690
            DuplicateCandidate = 166
            InvalidText = 582
            UnknownAlias = 1028
            """,
        [ReconcileBatchSchema.Expand] =
            """
            variant=Expand
            instances=2500
            accepted=235
            rejectedByComposer=2265
            rejectedByValidator=0
            CoincidentEndpoints = 784
            Duplicate = 1446
            DuplicateCandidate = 206
            InvalidText = 926
            PatchShape = 1568
            UnknownAlias = 1632
            """,
        [ReconcileBatchSchema.Compare] =
            """
            variant=Compare
            instances=2500
            accepted=621
            rejectedByComposer=1879
            rejectedByValidator=0
            CoincidentEndpoints = 317
            Duplicate = 1180
            InvalidText = 574
            PatchShape = 1569
            UnknownAlias = 1062
            """,
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_instance_admitted_by_the_grammar_is_accepted_or_rejected_by_the_composer_only_with_a_declared_residual_class(ReconcileBatchSchema variant)
    {
        var summary = Full(variant);

        Assert.True(summary.Instances >= 2_000);
        Assert.True(summary.Accepted > 0, "nessuna istanza accettata: il generatore non copre la classe valida.");
        Assert.Equal(summary.Instances, summary.Accepted + summary.RejectedByComposer + summary.RejectedByValidator);
        Assert.True(summary.Unexpected.Count == 0, "Rifiuti inattesi (STOP di RH-016):\n" + string.Join("\n", summary.Unexpected.Take(5)));
        Assert.Equal(0, summary.RejectedByValidator);                                                  // nessun rifiuto di CheckResult, riferimenti o gerarchia su un'istanza accettata dal compositore
        Assert.All(new[] { ReconcileViolationKind.StageShape, ReconcileViolationKind.ItemShape, ReconcileViolationKind.Undecodable, ReconcileViolationKind.IntegrityMismatch }, k => Assert.DoesNotContain(k, summary.Kinds.Keys));
        var rendered = summary.Render();
        Assert.True(Pinned[variant].ReplaceLineEndings("\n") == rendered, "Tabella non pinnata o diversa:\n" + rendered);
    }

    [Fact]
    public void The_residual_class_table_is_deterministic_on_two_runs()
    {
        foreach (var variant in Enum.GetValues<ReconcileBatchSchema>())
        {
            Assert.Equal(Run(variant, 400).Render(), Run(variant, 400).Render());
        }
    }

    // ---------------------------------------------------------------- baseline del profilo -3 (RH-003) spiegata e confrontata

    private static int Baseline(ReconcileBatchSchema variant)
    {
        var line = ReconcileSchemaPropertyTests.Pinned[variant].Split('\n').Single(l => l.Contains("value non coerente con op", StringComparison.Ordinal));
        return int.Parse(Regex.Match(line, @"= (\d+)$").Groups[1].Value);
    }

    [Fact]
    public void The_undecodable_class_of_the_minus_3_profile_is_the_unbound_value_of_the_patches_and_it_disappears_in_the_by_stage_profile()
    {
        // M0 (profilo -3): 1.484/2.500 (Expand) e 1.514/2.500 (Compare) istanze con almeno una patch il cui `value` non è coerente con `op`.
        var (expand, compare) = (Baseline(ReconcileBatchSchema.Expand), Baseline(ReconcileBatchSchema.Compare));
        Assert.Equal((1484, 1514), (expand, compare));

        // Spiegazione: tre rami di `value` indipendenti da `op` ⇒ 1/3 di coerenza per patch; 0–3 patch equiprobabili ⇒ attesa teorica di almeno una incoerente.
        var expected = 1 - 0.25 * (1 + 1.0 / 3 + 1.0 / 9 + 1.0 / 27);
        Assert.InRange(expected, 0.629, 0.631);
        Assert.InRange(expand / (double)Instances, expected - 0.06, expected);                         // osservato 59,4% e 60,6%: poco sotto, per le repliche esatte (12%) e per le proprietà facoltative
        Assert.InRange(compare / (double)Instances, expected - 0.06, expected);

        // Nel profilo by-stage-1 lo stesso seme non produce alcuna istanza non decodificabile, in nessuna variante.
        foreach (var variant in Enum.GetValues<ReconcileBatchSchema>())
        {
            Assert.DoesNotContain(ReconcileViolationKind.Undecodable, Full(variant).Kinds.Keys);
        }

        // Per Expand e Compare le patch ci sono davvero (altrimenti l'assenza di Undecodable non proverebbe nulla): almeno una istanza accettata o rifiutata contiene una patch.
        foreach (var variant in new[] { ReconcileBatchSchema.Expand, ReconcileBatchSchema.Compare })
        {
            var world = ReconcileBatchWorld.Create(variant, ContractProfile.V11);
            var generator = new ReconcileSchemaInstanceGenerator(ReconcileSchemaInstanceGenerator.Seed, ReconcileAliasUniverse.For(world));
            var withPatches = Enumerable.Range(0, 500).Count(_ => JsonNode.Parse(generator.Generate(world.Schema))!["patches"]!.AsArray().Count > 0);
            Assert.InRange(withPatches, 250, 450);
        }
    }

    // ---------------------------------------------------------------- casi espliciti (D3, D4)

    private static string Dto(Action<JsonObject>? change = null, string? noFindings = null, string? descriptor = null) => ReconcileContractMatrixTests.Dto(noFindings, descriptor, change).ToJsonString();

    [Fact]
    public void The_explicit_cases_of_the_task_hold_under_the_v11_contract()
    {
        var compare = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, ContractProfile.V11);

        // Confronto Open con motivo, 0 candidati, noFindings null ⇒ accettato.
        var open = compare.Evaluate(Dto(o => o["focus"] = JsonNode.Parse("""{"outcome":"Open","reason":"30 giorni contro 45"}""")));
        Assert.True(open.SchemaAdmits && open.Accepted, open.Report);
        Assert.Equal(CandidateState.Open, Assert.Single(open.Composition.Result!.Dispositions).State);

        // Confronto Open senza motivo ⇒ non valido per la grammatica (e respinto anche dal compositore, difesa in profondità).
        var noReason = compare.Evaluate(Dto(o => o["focus"] = JsonNode.Parse("""{"outcome":"Open","reason":null}""")));
        Assert.False(noReason.SchemaAdmits);
        Assert.Equal([ReconcileViolationKind.InvalidText], noReason.Composition.TypedViolations.Select(v => v.Kind));

        // Confronto con candidati ⇒ non valido per la grammatica (e respinto dal compositore).
        var withCandidates = compare.Evaluate(Dto(o =>
        {
            o["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""");
            o["candidates"] = new JsonArray(JsonNode.Parse("""{"key":"f1","left":"E3","right":"E5","type":"Contradicts","basis":"Inferred","rationale":"x"}"""));
        }));
        Assert.False(withCandidates.SchemaAdmits);
        Assert.Equal([ReconcileViolationKind.StageShape], withCandidates.Composition.TypedViolations.Select(v => v.Kind));

        // Blocco di espansione vuoto ⇒ accettato; foglia B senza candidati né noFindings ⇒ accettata.
        var expand = ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V11).Evaluate(Dto());
        Assert.True(expand.SchemaAdmits && expand.Accepted, expand.Report);
        var leafB = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11).Evaluate(Dto(descriptor: "gruppo"));
        Assert.True(leafB.SchemaAdmits && leafB.Accepted, leafB.Report);

        // Una foglia A senza item (come produce il modello nella v11) è accettata dal compositore: la proiezione è di M2, quindi qui nessun item.
        var leafA = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafA, ContractProfile.V11).Evaluate(Dto(descriptor: "gruppo"));
        Assert.True(leafA.SchemaAdmits && leafA.Accepted, leafA.Report);
        Assert.Empty(leafA.Composition.Result!.ItemProposals);
    }
}
