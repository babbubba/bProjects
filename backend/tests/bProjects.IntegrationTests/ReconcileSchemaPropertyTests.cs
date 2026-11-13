using System.Text;
using System.Text.RegularExpressions;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// RH-003 (reconciliation-hardening, M0) — test di proprietà della definizione v10 (profilo degli schemi -3): per ognuna delle sei varianti il generatore deterministico (seed 20261113, SplitMix64) produce
/// istanze AMMESSE per costruzione dallo schema inviato; ogni istanza è composta dal compositore di produzione e validata come in RH-002 (validatore canonico, riferimenti, visite, descriptor).
/// Il test è di CARATTERIZZAZIONE: registra le categorie di rifiuto (per messaggio normalizzato, ammesso solo qui) con i conteggi esatti e li fissa, così una classe nuova «ammessa dalla grammatica e rifiutata
/// dalla validazione» fa fallire il test. Nessun database, nessuna inferenza, nessuna modifica del codice di produzione.
/// </summary>
public sealed class ReconcileSchemaPropertyTests
{
    /// <summary>Istanze per variante (almeno 2.000 richieste da RH-003).</summary>
    internal const int InstancesPerVariant = 2_500;

    public static TheoryData<ReconcileBatchSchema> Variants() => new()
    {
        ReconcileBatchSchema.LeafA, ReconcileBatchSchema.LeafB, ReconcileBatchSchema.Reduce, ReconcileBatchSchema.Cross, ReconcileBatchSchema.Expand, ReconcileBatchSchema.Compare,
    };

    /// <summary>Etichette del task RH-003 (insieme atteso) più le classi osservate in aggiunta, documentate nel rapporto di M0.</summary>
    internal static readonly IReadOnlyList<(string Label, string Pattern)> Classes =
    [
        ("I-1 noFindings obbligatorio senza proposte", "noFindingsReason: obbligatorio quando"),
        ("I-2 Open senza motivo", @"focus\.reason: obbligatorio e non vuoto quando l'esito è Open"),
        ("alias sconosciuto o fuori batch", @"(alias <A> sconosciuto o fuori dal batch|item <A> inesistente|alias <A> sconosciuto)"),
        ("estremi coincidenti", "(endpoint coincidenti|l'estremo .* non è ammesso)"),
        ("candidato duplicato", "candidato duplicato"),
        ("chiave o alias duplicato", @"(chiave locale <A> duplicata|alias <A> duplicato)"),
        ("descriptor oltre 512 o di soli spazi", "descriptor: non vuoto e al massimo 512"),
        ("testo di soli spazi", @"(text: non vuoto|rationale: non vuota|noFindingsReason: non vuoto quando valorizzato|value: text e reason non vuoti|value\.rationale: non vuota)"),
        ("patch duplicata", "patch duplicata"),
        ("patch incoerente", @"(target: esattamente uno fra item e relation|evidence: obbligatoria per|AddRelation ancora un item|evidence: per AddRelation vuota|value: non coerente|assente dalla base|target\.item: item|target\.relation: alias|AddRelation ancora)"),
        ("patch incoerente (decodifica: value non coerente con op)", "JSON non conforme a reconcile-alias-1: value non coerente con op"),
        ("decodifica fallita (altro)", "JSON non conforme a reconcile-alias-1"),
        ("item: classificazione o evidenze (ItemShape)", @"(\.classification: |items\[\]\.evidence: obbligatoria)"),
    ];

    /// <summary>Classi dell'insieme atteso del task RH-003.</summary>
    internal static readonly IReadOnlySet<string> ExpectedByTask = new HashSet<string>(StringComparer.Ordinal)
    {
        "I-1 noFindings obbligatorio senza proposte", "I-2 Open senza motivo", "alias sconosciuto o fuori batch", "estremi coincidenti", "candidato duplicato", "chiave o alias duplicato",
        "descriptor oltre 512 o di soli spazi", "testo di soli spazi", "patch incoerente",
    };

    /// <summary>
    /// Classi osservate OLTRE l'insieme del task, documentate come esito di M0 (nessuna allenta una regola): la patch duplicata, le regole di classificazione/evidenze degli item della foglia A e il rifiuto,
    /// già in decodifica, di un <c>value</c> di patch non coerente con <c>op</c> (stessa classe semantica di «patch incoerente», rilevata prima del compositore).
    /// </summary>
    internal static readonly IReadOnlySet<string> AdditionalObserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "patch duplicata", "item: classificazione o evidenze (ItemShape)", "patch incoerente (decodifica: value non coerente con op)",
    };

    internal static string Normalize(string message)
    {
        var m = Regex.Replace(message, @"\[\d+\]", "[]");
        m = Regex.Replace(m, @"\b[0-9a-f]{64}\b", "<H>");
        m = Regex.Replace(m, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "<G>");
        m = Regex.Replace(m, @"(?<![A-Za-z<])[ENRSicfp][1-9][0-9]*(?![A-Za-z0-9>])", "<A>");
        m = Regex.Replace(m, @"(?s) Path:.*$", string.Empty);

        // Il testo di System.Text.Json cambia con la versione di .NET: le uniche eccezioni di decodifica attese (value dell'unione di patch non coerente con op) sono ridotte a una forma stabile.
        if (m.StartsWith("JSON non conforme a reconcile-alias-1:", StringComparison.Ordinal) && Regex.IsMatch(m, "Ai(AddSupport|AddRelation|ProposeAlternative)Value"))
        {
            return "JSON non conforme a reconcile-alias-1: value non coerente con op (union di patch deserializzata per op)";
        }

        return m.Trim();
    }

    internal static string Label(string normalized) =>
        Classes.FirstOrDefault(c => Regex.IsMatch(normalized, c.Pattern)).Label ?? "NON CLASSIFICATA";

    internal sealed record Summary(ReconcileBatchSchema Variant, int Instances, int Accepted, int RejectedByComposer, int RejectedByValidator, IReadOnlyDictionary<string, int> Categories, IReadOnlyDictionary<string, string> Examples)
    {
        public string Render()
        {
            var sb = new StringBuilder();
            sb.Append($"variant={Variant}\ninstances={Instances}\naccepted={Accepted}\nrejectedByComposer={RejectedByComposer}\nrejectedByValidator={RejectedByValidator}");
            foreach (var (category, count) in Categories.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                sb.Append($"\n{category} = {count}");
            }

            return sb.ToString();
        }
    }

    internal static Summary Run(ReconcileBatchSchema variant, int instances = InstancesPerVariant)
    {
        var world = ReconcileBatchWorld.Create(variant);
        var generator = new ReconcileSchemaInstanceGenerator(ReconcileSchemaInstanceGenerator.Seed, ReconcileAliasUniverse.For(world));
        var categories = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        int accepted = 0, byComposer = 0, byValidator = 0;
        for (var i = 0; i < instances; i++)
        {
            var json = generator.Generate(world.Schema);
            var verdict = world.Evaluate(json);
            Assert.True(verdict.SchemaAdmits, $"{variant} #{i}: l'istanza generata non è ammessa dallo schema ({string.Join(" | ", verdict.SchemaViolations)}): {json}");
            var found = verdict.ComposerViolations.Select(m => "K " + Label(Normalize(m)) + " :: " + Normalize(m))
                .Concat(verdict.ValidatorViolations.Select(m => "V " + Label(Normalize(m)) + " :: " + Normalize(m))).Distinct(StringComparer.Ordinal).ToList();
            if (found.Count == 0)
            {
                Assert.True(verdict.Accepted, verdict.Report);
                accepted++;
                continue;
            }

            if (!verdict.Composition.IsValid)
            {
                byComposer++;
            }
            else
            {
                byValidator++;
            }

            foreach (var category in found)
            {
                categories[category] = categories.GetValueOrDefault(category) + 1;
                examples.TryAdd(category, json);
            }
        }

        return new Summary(variant, instances, accepted, byComposer, byValidator, categories, examples);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ReconcileBatchSchema, Lazy<Summary>> Cache = new();

    /// <summary>Esecuzione completa (2.500 istanze) di una variante, calcolata una sola volta per processo di test.</summary>
    internal static Summary Full(ReconcileBatchSchema variant) => Cache.GetOrAdd(variant, v => new Lazy<Summary>(() => Run(v))).Value;

    /// <summary>
    /// Riepilogo pinnato per variante (seed 20261113, 2.500 istanze). Ogni riga di categoria è «origine (K compositore, V validatore) etichetta :: messaggio normalizzato = istanze»: un'istanza con più rifiuti
    /// conta in ciascuna delle sue categorie. Una classe nuova o un conteggio diverso richiede una diagnosi (STOP di RH-003), non un ritocco.
    /// </summary>
    internal static readonly IReadOnlyDictionary<ReconcileBatchSchema, string> Pinned = new Dictionary<ReconcileBatchSchema, string>
    {
        {
            ReconcileBatchSchema.LeafA,
            """
            variant=LeafA
            instances=2500
            accepted=120
            rejectedByComposer=2224
            rejectedByValidator=156
            K alias sconosciuto o fuori batch :: candidates[].left: alias <A> sconosciuto o fuori dal batch. = 592
            K alias sconosciuto o fuori batch :: candidates[].right: alias <A> sconosciuto o fuori dal batch. = 607
            K alias sconosciuto o fuori batch :: items[].evidence[]: alias <A> sconosciuto o fuori dal batch. = 769
            K candidato duplicato :: candidates[]: candidato duplicato (stessa identità canonica di candidates[]). = 225
            K chiave o alias duplicato :: candidates[].key: chiave locale <A> duplicata. = 671
            K chiave o alias duplicato :: items[].evidence[]: alias <A> duplicato. = 648
            K chiave o alias duplicato :: items[].key: chiave locale <A> duplicata: ogni chiave si usa una sola volta nel batch. = 546
            K estremi coincidenti :: candidates[]: endpoint coincidenti (<A>). = 609
            K item: classificazione o evidenze (ItemShape) :: items[].classification: Declared richiede almeno un'evidenza con origine Message (dichiarazione del project manager). = 326
            K item: classificazione o evidenze (ItemShape) :: items[].classification: Documented richiede almeno un'evidenza con origine Document. = 170
            K item: classificazione o evidenze (ItemShape) :: items[].classification: gli elementi di assumptions sono Assumed. = 225
            K item: classificazione o evidenze (ItemShape) :: items[].evidence: obbligatoria (solo una domanda aperta in openQuestions può non citare evidenze). = 655
            K testo di soli spazi :: candidates[].rationale: non vuota. = 615
            K testo di soli spazi :: items[].text: non vuoto. = 560
            V I-1 noFindings obbligatorio senza proposte :: noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch. = 64
            V descriptor oltre 512 o di soli spazi :: descriptor: non vuoto e al massimo 512 caratteri Unicode. = 86
            V testo di soli spazi :: noFindingsReason: non vuoto quando valorizzato. = 27
            """
        },
        {
            ReconcileBatchSchema.LeafB,
            """
            variant=LeafB
            instances=2500
            accepted=373
            rejectedByComposer=1637
            rejectedByValidator=490
            K alias sconosciuto o fuori batch :: candidates[].left: alias <A> sconosciuto o fuori dal batch. = 626
            K alias sconosciuto o fuori batch :: candidates[].right: alias <A> sconosciuto o fuori dal batch. = 648
            K candidato duplicato :: candidates[]: candidato duplicato (stessa identità canonica di candidates[]). = 205
            K chiave o alias duplicato :: candidates[].key: chiave locale <A> duplicata. = 649
            K estremi coincidenti :: candidates[]: endpoint coincidenti (<A>). = 610
            K testo di soli spazi :: candidates[].rationale: non vuota. = 585
            V I-1 noFindings obbligatorio senza proposte :: noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch. = 248
            V descriptor oltre 512 o di soli spazi :: descriptor: non vuoto e al massimo 512 caratteri Unicode. = 220
            V testo di soli spazi :: noFindingsReason: non vuoto quando valorizzato. = 102
            """
        },
        {
            ReconcileBatchSchema.Reduce,
            """
            variant=Reduce
            instances=2500
            accepted=341
            rejectedByComposer=1700
            rejectedByValidator=459
            K alias sconosciuto o fuori batch :: candidates[].left: alias <A> sconosciuto o fuori dal batch. = 626
            K alias sconosciuto o fuori batch :: candidates[].right: alias <A> sconosciuto o fuori dal batch. = 648
            K candidato duplicato :: candidates[]: candidato duplicato (stessa identità canonica di candidates[]). = 155
            K chiave o alias duplicato :: candidates[].key: chiave locale <A> duplicata. = 649
            K estremi coincidenti :: candidates[]: endpoint coincidenti (<A>). = 850
            K testo di soli spazi :: candidates[].rationale: non vuota. = 585
            V I-1 noFindings obbligatorio senza proposte :: noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch. = 248
            V descriptor oltre 512 o di soli spazi :: descriptor: non vuoto e al massimo 512 caratteri Unicode. = 201
            V testo di soli spazi :: noFindingsReason: non vuoto quando valorizzato. = 88
            """
        },
        {
            ReconcileBatchSchema.Cross,
            """
            variant=Cross
            instances=2500
            accepted=462
            rejectedByComposer=1744
            rejectedByValidator=294
            K alias sconosciuto o fuori batch :: candidates[].left: alias <A> sconosciuto o fuori dal batch. = 657
            K alias sconosciuto o fuori batch :: candidates[].right: alias <A> sconosciuto o fuori dal batch. = 620
            K candidato duplicato :: candidates[]: candidato duplicato (stessa identità canonica di candidates[]). = 166
            K chiave o alias duplicato :: candidates[].key: chiave locale <A> duplicata. = 690
            K estremi coincidenti :: candidates[]: endpoint coincidenti (<A>). = 902
            K testo di soli spazi :: candidates[].rationale: non vuota. = 582
            V I-1 noFindings obbligatorio senza proposte :: noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch. = 213
            V testo di soli spazi :: noFindingsReason: non vuoto quando valorizzato. = 81
            """
        },
        {
            ReconcileBatchSchema.Expand,
            """
            variant=Expand
            instances=2500
            accepted=138
            rejectedByComposer=2276
            rejectedByValidator=86
            K alias sconosciuto o fuori batch :: candidates[].left: alias <A> sconosciuto o fuori dal batch. = 218
            K alias sconosciuto o fuori batch :: candidates[].right: alias <A> sconosciuto o fuori dal batch. = 255
            K alias sconosciuto o fuori batch :: patches[].evidence[]: alias <A> sconosciuto o fuori dal batch. = 134
            K alias sconosciuto o fuori batch :: patches[].target.item: item <A> inesistente (nessun item già proposto cita evidenze di questo batch). = 27
            K alias sconosciuto o fuori batch :: patches[].target.relation: alias <A> sconosciuto o fuori dal batch. = 13
            K alias sconosciuto o fuori batch :: patches[].value.from: alias <A> sconosciuto o fuori dal batch. = 38
            K alias sconosciuto o fuori batch :: patches[].value.to: alias <A> sconosciuto o fuori dal batch. = 33
            K candidato duplicato :: candidates[]: candidato duplicato (stessa identità canonica di candidates[]). = 97
            K chiave o alias duplicato :: candidates[].key: chiave locale <A> duplicata. = 241
            K chiave o alias duplicato :: patches[].evidence[]: alias <A> duplicato. = 104
            K chiave o alias duplicato :: patches[].key: chiave locale <A> duplicata. = 100
            K estremi coincidenti :: candidates[]: endpoint coincidenti (<A>). = 248
            K estremi coincidenti :: patches[].value: endpoint coincidenti (<A>). = 35
            K patch duplicata :: patches[]: patch duplicata (stessa identità canonica di patches[]). = 37
            K patch incoerente (decodifica: value non coerente con op) :: JSON non conforme a reconcile-alias-1: value non coerente con op (union di patch deserializzata per op) = 1484
            K patch incoerente :: patches[].evidence: obbligatoria per AddSupport. = 35
            K patch incoerente :: patches[].evidence: obbligatoria per ProposeAlternative. = 37
            K patch incoerente :: patches[].evidence: per AddRelation vuota oppure esattamente gli endpoint <A> e <A>. = 49
            K patch incoerente :: patches[].target: AddRelation ancora un item esistente, non una relazione. = 41
            K patch incoerente :: patches[].target: esattamente uno fra item e relation. = 231
            K testo di soli spazi :: candidates[].rationale: non vuota. = 253
            V I-1 noFindings obbligatorio senza proposte :: noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch. = 49
            V testo di soli spazi :: noFindingsReason: non vuoto quando valorizzato. = 36
            V testo di soli spazi :: patches[].value: text e reason non vuoti. = 1
            """
        },
        {
            ReconcileBatchSchema.Compare,
            """
            variant=Compare
            instances=2500
            accepted=81
            rejectedByComposer=2359
            rejectedByValidator=60
            K I-2 Open senza motivo :: focus.reason: obbligatorio e non vuoto quando l'esito è Open. = 254
            K alias sconosciuto o fuori batch :: candidates[].left: alias <A> sconosciuto o fuori dal batch. = 262
            K alias sconosciuto o fuori batch :: candidates[].right: alias <A> sconosciuto o fuori dal batch. = 253
            K alias sconosciuto o fuori batch :: patches[].evidence[]: alias <A> sconosciuto o fuori dal batch. = 112
            K alias sconosciuto o fuori batch :: patches[].target.item: item <A> inesistente (nessun item già proposto cita evidenze di questo batch). = 24
            K alias sconosciuto o fuori batch :: patches[].target.relation: alias <A> sconosciuto o fuori dal batch. = 8
            K alias sconosciuto o fuori batch :: patches[].value.from: alias <A> sconosciuto o fuori dal batch. = 38
            K alias sconosciuto o fuori batch :: patches[].value.to: alias <A> sconosciuto o fuori dal batch. = 34
            K candidato duplicato :: candidates[]: candidato duplicato (stessa identità canonica di candidates[]). = 81
            K chiave o alias duplicato :: candidates[].key: chiave locale <A> duplicata. = 278
            K chiave o alias duplicato :: patches[].evidence[]: alias <A> duplicato. = 131
            K chiave o alias duplicato :: patches[].key: chiave locale <A> duplicata. = 92
            K estremi coincidenti :: candidates[]: endpoint coincidenti (<A>). = 336
            K estremi coincidenti :: patches[].value: endpoint coincidenti (<A>). = 42
            K patch duplicata :: patches[]: patch duplicata (stessa identità canonica di patches[]). = 31
            K patch incoerente (decodifica: value non coerente con op) :: JSON non conforme a reconcile-alias-1: value non coerente con op (union di patch deserializzata per op) = 1514
            K patch incoerente :: patches[].evidence: obbligatoria per AddSupport. = 39
            K patch incoerente :: patches[].evidence: obbligatoria per ProposeAlternative. = 29
            K patch incoerente :: patches[].evidence: per AddRelation vuota oppure esattamente gli endpoint <A> e <A>. = 23
            K patch incoerente :: patches[].target: AddRelation ancora un item esistente, non una relazione. = 29
            K patch incoerente :: patches[].target: esattamente uno fra item e relation. = 220
            K testo di soli spazi :: candidates[].rationale: non vuota. = 228
            V I-1 noFindings obbligatorio senza proposte :: noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch. = 38
            V testo di soli spazi :: noFindingsReason: non vuoto quando valorizzato. = 19
            V testo di soli spazi :: patches[].value: text e reason non vuoti. = 3
            """
        },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_generated_instance_is_admitted_by_the_schema_and_the_rejection_categories_are_exactly_the_pinned_ones(ReconcileBatchSchema variant)
    {
        var summary = Full(variant);

        Assert.True(summary.Instances >= 2_000);
        Assert.True(summary.Accepted > 0, "nessuna istanza accettata: il generatore non copre la classe valida.");
        Assert.Equal(summary.Instances, summary.Accepted + summary.RejectedByComposer + summary.RejectedByValidator);
        var rendered = summary.Render();
        Assert.True(
            Pinned.TryGetValue(variant, out var pinned) && pinned.ReplaceLineEndings("\n") == rendered,
            "Riepilogo non pinnato o diverso:\n" + rendered + "\nEsempi:\n" + string.Join("\n", summary.Examples.Select(e => e.Key + "\n    " + (e.Value.Length > 700 ? e.Value[..700] + "…" : e.Value))));
    }

    [Fact]
    public void Two_runs_of_the_generator_give_exactly_the_same_counts_and_the_same_instances()
    {
        foreach (var variant in Enum.GetValues<ReconcileBatchSchema>())
        {
            var first = Run(variant);
            var second = Full(variant);

            Assert.Equal(first.Render(), second.Render());
            Assert.Equal(first.Examples, second.Examples);
        }

        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare);
        var a = new ReconcileSchemaInstanceGenerator(ReconcileSchemaInstanceGenerator.Seed, ReconcileAliasUniverse.For(world));
        var b = new ReconcileSchemaInstanceGenerator(ReconcileSchemaInstanceGenerator.Seed, ReconcileAliasUniverse.For(world));
        var c = new ReconcileSchemaInstanceGenerator(ReconcileSchemaInstanceGenerator.Seed + 1, ReconcileAliasUniverse.For(world));
        var sequenceA = Enumerable.Range(0, 50).Select(_ => a.Generate(world.Schema)).ToList();
        Assert.Equal(sequenceA, Enumerable.Range(0, 50).Select(_ => b.Generate(world.Schema)).ToList());
        Assert.NotEqual(sequenceA, Enumerable.Range(0, 50).Select(_ => c.Generate(world.Schema)).ToList());   // un altro seme dà un'altra sequenza
    }

    [Fact]
    public void The_observed_rejection_classes_are_the_task_expected_set_plus_the_documented_additional_ones_and_nothing_else()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in Enum.GetValues<ReconcileBatchSchema>())
        {
            foreach (var category in Full(variant).Categories.Keys)
            {
                seen.Add(category.Split(" :: ")[0][2..]);
            }
        }

        Assert.Empty(ExpectedByTask.Except(seen));                                      // il generatore raggiunge tutta la classe attesa
        Assert.Empty(seen.Except(ExpectedByTask).Except(AdditionalObserved));           // nessuna classe non prevista né documentata (altrimenti: nuova incoerenza, STOP di RH-003)
    }

    [Fact]
    public void Whitespace_only_text_inside_patch_values_passes_the_schema_and_the_composer_and_is_rejected_only_by_the_canonical_validator()
    {
        // Classe «testo di soli spazi nelle patch»: il generatore la raggiunge raramente (text e reason di ProposeAlternative); le due forme si fissano qui con istanze costruite.
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare);
        string Json(string patch) => ReconcileContractMatrixTests.Dto(noFindings: "x", change: o =>
        {
            o["focus"] = System.Text.Json.Nodes.JsonNode.Parse("""{"outcome":"Compared","reason":null}""");
            o["patches"] = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonNode.Parse(patch));
        }).ToJsonString();

        var relation = world.Evaluate(Json("""{"key":"p1","op":"AddRelation","target":{"item":"i2","relation":null},"evidence":[],"value":{"from":"E3","to":"E5","type":"Confirms","basis":"Inferred","rationale":" "}}"""));
        var alternative = world.Evaluate(Json("""{"key":"p1","op":"ProposeAlternative","target":{"item":"i2","relation":null},"evidence":["E3"],"value":{"text":" ","classification":"Documented","reason":"x"}}"""));

        foreach (var verdict in new[] { relation, alternative })
        {
            Assert.True(verdict.SchemaAdmits, string.Join(" | ", verdict.SchemaViolations));
            Assert.True(verdict.Composition.IsValid, verdict.Report);
            Assert.False(verdict.Accepted);
            Assert.NotNull(verdict.HierarchyError);
        }

        Assert.Equal(["patches[0].value.rationale: non vuota quando valorizzata."], relation.ValidatorViolations);
        Assert.Equal(["patches[0].value: text e reason non vuoti."], alternative.ValidatorViolations);
    }

    [Fact]
    public void An_expansion_with_more_than_16_candidates_is_rejected_by_the_v10_grammar_and_by_the_composer_but_not_by_the_v6_single_schema()
    {
        // Il generatore emette al più tre elementi per array: il tetto del profilo -3 non è esercitato dalle istanze generate, quindi lo si fissa con un'istanza costruita (RH-005 lo usa come parametro).
        var types = new[] { "Qualifies", "Confirms", "Contradicts", "Corrects", "Supersedes", "DependsOn", "AlternativeTo" };
        var pairs = new[] { ("E3", "E4"), ("E3", "E5"), ("E4", "E5") };
        var candidates = pairs.SelectMany(p => types.Select(t => (p.Item1, p.Item2, t))).Take(17).Select((c, i) => System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { key = $"f{i + 1}", left = c.Item1, right = c.Item2, type = c.t, basis = "Inferred", rationale = "stessa regola" }))).ToArray();
        var json = ReconcileContractMatrixTests.Dto(change: o => o["candidates"] = new System.Text.Json.Nodes.JsonArray(candidates)).ToJsonString();

        var v10 = ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V10).Evaluate(json);
        var v6 = ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V6).Evaluate(json);

        Assert.Contains(v10.SchemaViolations, m => m.Contains("17 elementi > maxItems 16", StringComparison.Ordinal));
        Assert.Contains(v10.ComposerViolations, m => m.Contains("17 candidati oltre il tetto di 16", StringComparison.Ordinal));
        Assert.False(v10.Composition.IsValid);
        Assert.True(v6.SchemaAdmits, string.Join(" | ", v6.SchemaViolations));
        Assert.True(v6.Accepted, v6.Report);                                                       // senza membership (v6) nessun tetto: 17 candidati validi
    }

    [Fact]
    public void The_stage_and_cap_classes_that_the_grammar_excludes_never_reach_the_composer()
    {
        // Nel profilo -3 la grammatica esclude: item fuori dalla foglia A, patch fuori da espansione e confronto, focus fuori stadio, estremi con il prefisso sbagliato, oltre 16 candidati nell'espansione
        // (il generatore emette al più tre elementi: il tetto non è esercitato qui, ma dalla grammatica e dai test del profilo). Nessuna di queste violazioni deve comparire.
        foreach (var variant in Enum.GetValues<ReconcileBatchSchema>())
        {
            Assert.DoesNotContain(Full(variant).Categories.Keys, c => c.Contains("non è ammess", StringComparison.Ordinal) || c.Contains("ammessi solo", StringComparison.Ordinal)
                || c.Contains("ammesse solo", StringComparison.Ordinal) || c.Contains("oltre il tetto", StringComparison.Ordinal) || c.Contains("previsto solo", StringComparison.Ordinal)
                || c.Contains("deve dichiarare Compared", StringComparison.Ordinal) || c.Contains("solo null oppure Unresolved", StringComparison.Ordinal));
        }
    }
}
