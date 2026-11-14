using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-013 (M1) — violazioni TIPIZZATE del compositore alias e controlli del contratto per stadio (v11). Un'istanza minima per ogni riga della tabella di etichettatura
/// (tipo atteso e messaggio storico invariato); invarianti di <see cref="ReconcileAliasComposition.TypedViolations"/> (stessa lunghezza e ordine di <c>Violations</c>); C-4: <c>Undecodable</c> solo per la
/// decodifica, anche per <c>op</c>/<c>value</c> incoerenti, <c>PatchShape</c> solo per violazioni semantiche; i tre controlli di difesa in profondità sono attivi solo con <see cref="ReconcileComposeOptions"/>.
/// I tipi sono solo diagnostici (nessun retry). Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileViolationTypingTests
{
    private static JsonNode Item(string key, string section, string classification, string text = "testo", params string[] evidence) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { key, section, text, classification, evidence }))!;

    private static JsonNode Cand(string key, string left, string right, string type = "Confirms", string rationale = "stessa regola") =>
        JsonNode.Parse(JsonSerializer.Serialize(new { key, left, right, type, basis = "Inferred", rationale }))!;

    private static JsonNode Focus(string outcome, string? reason) => JsonNode.Parse(JsonSerializer.Serialize(new { outcome, reason }))!;

    private const string AddRelationOk = """{"from":"E3","to":"E5","type":"Confirms","basis":"Inferred","rationale":null}""";
    private const string AlternativeOk = """{"text":"alternativa","classification":"Documented","reason":"motivo"}""";

    private static JsonNode Patch(string key, string op, string value, string? item = "i2", string? relation = null, params string[] evidence) =>
        JsonNode.Parse($$"""{"key":"{{key}}","op":"{{op}}","target":{"item":{{(item is null ? "null" : $"\"{item}\"")}},"relation":{{(relation is null ? "null" : $"\"{relation}\"")}}},"evidence":{{JsonSerializer.Serialize(evidence)}},"value":{{value}}}""")!;

    private static string Dto(Action<JsonObject> change, string? noFindings = "x", string? descriptor = null) => ReconcileContractMatrixTests.Dto(noFindings, descriptor, change).ToJsonString();

    private sealed record Case(string Label, ReconcileBatchSchema Variant, string Json, ReconcileViolationKind Kind, string Fragment, Func<ReconcileBatchWorld, ReconcileAliasComposition>? Compose = null);

    private static IEnumerable<Case> TableCases()
    {
        var focusCompared = Focus("Compared", null);

        // --- decodifica
        yield return new("decodifica fallita (JSON non valido)", ReconcileBatchSchema.LeafB, "{", ReconcileViolationKind.Undecodable, "JSON non conforme a reconcile-alias-1");
        yield return new("decodifica fallita (contract errato)", ReconcileBatchSchema.LeafB, Dto(o => o["contract"] = "reconcile-alias-9", descriptor: "g"), ReconcileViolationKind.Undecodable, "contract deve essere");
        yield return new(
            "value incoerente con op: decodifica (C-4), non PatchShape", ReconcileBatchSchema.Expand,
            Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", AlternativeOk, evidence: "E3"))), ReconcileViolationKind.Undecodable, "JSON non conforme a reconcile-alias-1");

        // --- item
        yield return new("item fuori stadio", ReconcileBatchSchema.LeafB, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "t", "E2")), descriptor: "g"), ReconcileViolationKind.StageShape, "items: ammessi solo nelle foglie");
        yield return new("chiave di item ripetuta", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "t", "E2"), Item("i1", "requirements", "Documented", "t", "E3")), descriptor: "g"), ReconcileViolationKind.Duplicate, "chiave locale i1 duplicata");
        yield return new("alias ripetuto in una lista", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "t", "E2", "E2")), descriptor: "g"), ReconcileViolationKind.Duplicate, "alias E2 duplicato");
        yield return new("alias sconosciuto (item)", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "t", "E9")), descriptor: "g"), ReconcileViolationKind.UnknownAlias, "alias E9 sconosciuto o fuori dal batch");
        yield return new("testo dell'item vuoto", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "  ", "E2")), descriptor: "g"), ReconcileViolationKind.InvalidText, "items[0].text: non vuoto");
        yield return new("assumptions non Assumed", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "assumptions", "Documented", "t", "E2")), descriptor: "g"), ReconcileViolationKind.ItemShape, "gli elementi di assumptions sono Assumed");
        yield return new("Declared senza Message", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Declared", "t", "E2")), descriptor: "g"), ReconcileViolationKind.ItemShape, "Declared richiede almeno un'evidenza con origine Message");
        yield return new("Documented senza Document", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "t", "E1")), descriptor: "g"), ReconcileViolationKind.ItemShape, "Documented richiede almeno un'evidenza con origine Document");
        yield return new("item senza evidenze fuori da openQuestions", ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented")), descriptor: "g"), ReconcileViolationKind.ItemShape, "evidence: obbligatoria");

        // --- candidati
        yield return new("estremo con prefisso sbagliato (nodo in una foglia)", ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "N1", "E2")), descriptor: "g"), ReconcileViolationKind.StageShape, "l'estremo N1 non è ammesso");
        yield return new("estremo con prefisso sbagliato (evidenza in una riduzione)", ReconcileBatchSchema.Reduce, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "N2")), descriptor: "g"), ReconcileViolationKind.StageShape, "l'estremo E1 non è ammesso");
        yield return new("alias sconosciuto (estremo evidenza)", ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E9", "E2")), descriptor: "g"), ReconcileViolationKind.UnknownAlias, "candidates[0].left: alias E9 sconosciuto");
        yield return new("alias sconosciuto (estremo nodo)", ReconcileBatchSchema.Reduce, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "N1", "N9")), descriptor: "g"), ReconcileViolationKind.UnknownAlias, "candidates[0].right: alias N9 sconosciuto");
        yield return new("tetto dell'espansione superato", ReconcileBatchSchema.Expand, Dto(o => o["candidates"] = new JsonArray([.. Enumerable.Range(1, 17).Select(i => Cand($"f{i}", "E3", "E4", new[] { "Qualifies", "Confirms", "Contradicts", "Corrects", "Supersedes", "DependsOn", "AlternativeTo", "SameRule", "Alias" }[(i - 1) % 9], i <= 9 ? "r" : "s")).Select(c => c.DeepClone())])), ReconcileViolationKind.StageShape, "oltre il tetto di 16");
        yield return new("chiave di candidato ripetuta", ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E2"), Cand("f1", "E1", "E3")), descriptor: "g"), ReconcileViolationKind.Duplicate, "chiave locale f1 duplicata");
        yield return new("estremi coincidenti (candidato)", ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E2", "E2")), descriptor: "g"), ReconcileViolationKind.CoincidentEndpoints, "endpoint coincidenti (E2)");
        yield return new("rationale vuota", ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E2", rationale: " ")), descriptor: "g"), ReconcileViolationKind.InvalidText, "rationale: non vuota");
        yield return new("candidato duplicato", ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E2"), Cand("f2", "E1", "E2")), descriptor: "g"), ReconcileViolationKind.DuplicateCandidate, "candidato duplicato");

        // --- focus
        yield return new("focus del confronto mancante", ReconcileBatchSchema.Compare, Dto(_ => { }), ReconcileViolationKind.StageShape, "il confronto deve dichiarare Compared oppure Open");
        yield return new("focus del confronto Unresolved", ReconcileBatchSchema.Compare, Dto(o => o["focus"] = Focus("Unresolved", null)), ReconcileViolationKind.StageShape, "il confronto deve dichiarare Compared oppure Open");
        yield return new("Open senza motivo", ReconcileBatchSchema.Compare, Dto(o => o["focus"] = Focus("Open", null)), ReconcileViolationKind.InvalidText, "focus.reason: obbligatorio e non vuoto");
        yield return new("focus dell'espansione diverso da Unresolved", ReconcileBatchSchema.Expand, Dto(o => o["focus"] = focusCompared.DeepClone()), ReconcileViolationKind.StageShape, "solo null oppure Unresolved");
        yield return new("focus fuori stadio", ReconcileBatchSchema.LeafB, Dto(o => o["focus"] = focusCompared.DeepClone(), descriptor: "g"), ReconcileViolationKind.StageShape, "previsto solo negli stadi di espansione e confronto");

        // --- patch
        yield return new("patch fuori stadio", ReconcileBatchSchema.LeafB, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", evidence: "E2")), descriptor: "g"), ReconcileViolationKind.StageShape, "patches: ammesse solo negli stadi di espansione e confronto");
        yield return new(
            "base del passaggio assente", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", evidence: "E3"))), ReconcileViolationKind.StageShape, "la base del passaggio non è disponibile",
            w => w.ComposeOnly(Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", evidence: "E3"))), baseOverride: null, useBaseOverride: true));
        yield return new("chiave di patch ripetuta", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", evidence: "E3"), Patch("p1", "AddSupport", "{}", evidence: "E4"))), ReconcileViolationKind.Duplicate, "chiave locale p1 duplicata");
        yield return new("target non esclusivo (entrambi nulli)", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", item: null, evidence: "E3"))), ReconcileViolationKind.PatchShape, "esattamente uno fra item e relation");
        yield return new("target item sconosciuto", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", item: "i1", evidence: "E3"))), ReconcileViolationKind.UnknownAlias, "item i1 inesistente");
        yield return new("target relation sconosciuta", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", item: null, relation: "R9", evidence: "E3"))), ReconcileViolationKind.UnknownAlias, "target.relation: alias R9 sconosciuto");
        yield return new("AddSupport senza evidenze", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}"))), ReconcileViolationKind.PatchShape, "evidence: obbligatoria per AddSupport");
        yield return new("ProposeAlternative senza evidenze", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "ProposeAlternative", AlternativeOk))), ReconcileViolationKind.PatchShape, "evidence: obbligatoria per ProposeAlternative");
        yield return new("AddRelation su una relazione", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddRelation", AddRelationOk, item: null, relation: "R1"))), ReconcileViolationKind.PatchShape, "AddRelation ancora un item esistente");
        yield return new("AddRelation con evidenze diverse dagli estremi", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddRelation", AddRelationOk, evidence: ["E3", "E4"]))), ReconcileViolationKind.PatchShape, "per AddRelation vuota oppure esattamente gli endpoint");
        yield return new(
            "AddRelation: alias degli estremi sconosciuto", ReconcileBatchSchema.Expand,
            Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddRelation", """{"from":"E9","to":"E5","type":"Confirms","basis":"Inferred","rationale":null}"""))), ReconcileViolationKind.UnknownAlias, "value.from: alias E9 sconosciuto");
        yield return new(
            "AddRelation: alias dell'estremo to sconosciuto", ReconcileBatchSchema.Expand,
            Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddRelation", """{"from":"E3","to":"E9","type":"Confirms","basis":"Inferred","rationale":null}"""))), ReconcileViolationKind.UnknownAlias, "value.to: alias E9 sconosciuto");
        yield return new(
            "estremi coincidenti (AddRelation)", ReconcileBatchSchema.Expand,
            Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddRelation", """{"from":"E3","to":"E3","type":"Confirms","basis":"Inferred","rationale":null}"""))), ReconcileViolationKind.CoincidentEndpoints, "value: endpoint coincidenti (E3)");
        yield return new(
            "bersaglio assente dalla base", ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", evidence: "E3"))), ReconcileViolationKind.PatchShape, "assente dalla base del passaggio",
            w => w.ComposeOnly(Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", evidence: "E3"))), DocumentAnalysisBase.Compute([], w.Input.Snapshot), useBaseOverride: true));
        yield return new(
            "patch duplicata (stessa identità, chiavi diverse)", ReconcileBatchSchema.Expand,
            Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", evidence: "E3"), Patch("p2", "AddSupport", "{}", evidence: "E3"))), ReconcileViolationKind.Duplicate, "patch duplicata");
    }

    [Theory]
    [InlineData(ContractProfile.V10)]
    [InlineData(ContractProfile.V11)]
    public void Every_row_of_the_labelling_table_has_a_minimal_instance_with_the_expected_kind_and_the_historical_message(ContractProfile profile)
    {
        var problems = new List<string>();
        foreach (var c in TableCases())
        {
            // Nel profilo v11 i controlli aggiuntivi non cambiano le righe della tabella storica: stesso tipo, stesso messaggio.
            var world = ReconcileBatchWorld.Create(c.Variant, profile);
            var composition = c.Compose?.Invoke(world) ?? world.ComposeOnly(c.Json);
            var hit = composition.TypedViolations.FirstOrDefault(v => v.Kind == c.Kind && v.Message.Contains(c.Fragment, StringComparison.Ordinal));
            if (hit is null)
            {
                problems.Add($"{c.Label}: atteso {c.Kind} con «{c.Fragment}», ottenuto [{string.Join(" | ", composition.TypedViolations.Select(v => $"{v.Kind}: {v.Message}"))}]");
            }
            else if (!composition.Violations.Contains(hit.Message))
            {
                problems.Add($"{c.Label}: il messaggio tipizzato non è fra i messaggi storici.");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void TypedViolations_has_one_element_per_historical_message_in_the_same_order_and_is_empty_only_when_accepted()
    {
        foreach (var profile in new[] { ContractProfile.V10, ContractProfile.V11, ContractProfile.V6 })
        {
            foreach (var c in TableCases())
            {
                var world = ReconcileBatchWorld.Create(c.Variant, profile);
                var composition = c.Compose?.Invoke(world) ?? world.ComposeOnly(c.Json);
                Assert.Equal(composition.Violations, composition.TypedViolations.Select(v => v.Message));
                Assert.Equal(composition.Violations.Count > 0, composition.TypedViolations.Count > 0);
                Assert.Equal(composition.Violations.Count > 0, !composition.IsValid);
                Assert.Equal(composition.Violations.Distinct(StringComparer.Ordinal).Count(), composition.Violations.Count);   // nessun messaggio ripetuto
                Assert.All(composition.TypedViolations, v => Assert.False(string.IsNullOrEmpty(v.Path)));
            }
        }

        var accepted = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11).ComposeOnly(Dto(_ => { }, descriptor: "gruppo"));
        Assert.True(accepted.IsValid);
        Assert.Empty(accepted.Violations);
        Assert.Empty(accepted.TypedViolations);
    }

    [Fact]
    public void The_path_of_a_violation_is_the_location_in_the_DTO_that_the_message_starts_with()
    {
        // Il percorso tipizzato è la parte del messaggio storico che precede i due punti (o l'intero percorso dell'elemento): uno scambio di percorsi non passa inosservato.
        var checks = new (ReconcileBatchSchema Variant, string Json, string Path)[]
        {
            (ReconcileBatchSchema.LeafA, Dto(o => o["items"] = new JsonArray(Item("i1", "requirements", "Documented", "t", "E9")), descriptor: "g"), "items[0].evidence[0]"),
            (ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E9", "E2")), descriptor: "g"), "candidates[0].left"),
            (ReconcileBatchSchema.LeafB, Dto(o => o["candidates"] = new JsonArray(Cand("f1", "E1", "E2"), Cand("f2", "E1", "E2")), descriptor: "g"), "candidates[1]"),
            (ReconcileBatchSchema.Compare, Dto(o => o["focus"] = Focus("Open", null)), "focus.reason"),
            (ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", item: null, evidence: "E3"))), "patches[0].target"),
            (ReconcileBatchSchema.Expand, Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddRelation", """{"from":"E3","to":"E9","type":"Confirms","basis":"Inferred","rationale":null}"""))), "patches[0].value.to"),
        };
        foreach (var (variant, json, path) in checks)
        {
            var violations = ReconcileBatchWorld.Create(variant, ContractProfile.V11).ComposeOnly(json).TypedViolations;
            var violation = Assert.Single(violations, v => v.Path == path);   // altre violazioni dello stesso DTO (es. la classificazione dell'item) hanno il proprio percorso
            Assert.StartsWith(path, violation.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_op_value_mismatch_is_Undecodable_and_never_PatchShape_while_a_semantic_patch_violation_is_PatchShape_and_never_Undecodable()
    {
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V11);
        foreach (var (op, value) in new[] { ("AddSupport", AlternativeOk), ("AddSupport", AddRelationOk), ("AddRelation", "{}"), ("AddRelation", AlternativeOk), ("ProposeAlternative", "{}"), ("ProposeAlternative", AddRelationOk) })
        {
            var composition = world.ComposeOnly(Dto(o => o["patches"] = new JsonArray(Patch("p1", op, value, evidence: "E3"))));
            Assert.Equal([ReconcileViolationKind.Undecodable], composition.TypedViolations.Select(v => v.Kind));
            Assert.DoesNotContain(composition.TypedViolations, v => v.Kind == ReconcileViolationKind.PatchShape);
        }

        var semantic = world.ComposeOnly(Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddSupport", "{}", item: null, evidence: "E3"))));
        Assert.Equal([ReconcileViolationKind.PatchShape], semantic.TypedViolations.Select(v => v.Kind));
    }

    // ---------------------------------------------------------------- controlli solo-v11 (difesa in profondità)

    [Fact]
    public void A_compare_with_candidates_is_StageShape_with_the_stage_contract_and_unchanged_without_it()
    {
        var json = Dto(o =>
        {
            o["focus"] = Focus("Compared", null);
            o["candidates"] = new JsonArray(Cand("f1", "E3", "E5", "Contradicts"));
        });

        var v10 = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, ContractProfile.V10).ComposeOnly(json);
        Assert.True(v10.IsValid, string.Join(" | ", v10.Violations));                                // v10: il confronto con candidati resta accettato (caratterizzazione M0)

        var v11 = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, ContractProfile.V11).ComposeOnly(json);
        Assert.False(v11.IsValid);
        var violation = Assert.Single(v11.TypedViolations);
        Assert.Equal((ReconcileViolationKind.StageShape, "candidates"), (violation.Kind, violation.Path));
        Assert.Contains("il confronto non ammette candidati (1 ricevuti)", violation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_whitespace_descriptor_is_InvalidText_with_the_stage_contract_and_accepted_by_the_historical_composer(string descriptor)
    {
        var json = Dto(_ => { }, descriptor: descriptor);
        Assert.True(ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V10).ComposeOnly(json).IsValid);
        var v11 = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11).ComposeOnly(json);
        var violation = Assert.Single(v11.TypedViolations);
        Assert.Equal((ReconcileViolationKind.InvalidText, "descriptor", "descriptor: non vuoto e al massimo 512 caratteri Unicode."), (violation.Kind, violation.Path, violation.Message));
    }

    [Fact]
    public void A_descriptor_over_512_runes_is_InvalidText_with_the_stage_contract_and_512_is_accepted()
    {
        var long513 = new string('x', 513);
        var exactly512 = new string('x', 512);
        Assert.True(ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V10).ComposeOnly(Dto(_ => { }, descriptor: long513)).IsValid);
        var v11 = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11);
        Assert.Equal(ReconcileViolationKind.InvalidText, Assert.Single(v11.ComposeOnly(Dto(_ => { }, descriptor: long513)).TypedViolations).Kind);
        Assert.True(v11.ComposeOnly(Dto(_ => { }, descriptor: exactly512)).IsValid);
        Assert.True(v11.ComposeOnly(Dto(_ => { }, descriptor: string.Concat(Enumerable.Repeat("😀", 512)))).IsValid);   // 512 punti di codice (surrogati compresi)
    }

    [Fact]
    public void Whitespace_patch_texts_are_InvalidText_with_the_stage_contract_and_accepted_by_the_historical_composer_with_the_same_messages_as_the_validator()
    {
        var relation = Dto(o => o["patches"] = new JsonArray(Patch("p1", "AddRelation", """{"from":"E3","to":"E5","type":"Confirms","basis":"Inferred","rationale":" "}""")));
        var alternativeText = Dto(o => o["patches"] = new JsonArray(Patch("p1", "ProposeAlternative", """{"text":" ","classification":"Documented","reason":"x"}""", evidence: "E3")));
        var alternativeReason = Dto(o => o["patches"] = new JsonArray(Patch("p1", "ProposeAlternative", """{"text":"x","classification":"Documented","reason":"\t"}""", evidence: "E3")));

        foreach (var (json, expected) in new[]
        {
            (relation, "patches[0].value.rationale: non vuota quando valorizzata."),
            (alternativeText, "patches[0].value: text e reason non vuoti."),
            (alternativeReason, "patches[0].value: text e reason non vuoti."),
        })
        {
            Assert.True(ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V10).ComposeOnly(json).IsValid);      // storico: lo scarta solo il validatore
            var v11 = ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, ContractProfile.V11).ComposeOnly(json);
            var violation = Assert.Single(v11.TypedViolations);
            Assert.Equal((ReconcileViolationKind.InvalidText, expected), (violation.Kind, violation.Message));
        }
    }

    [Fact]
    public void An_unknown_stage_contract_is_an_error_and_the_replay_hash_mismatch_has_its_own_diagnostic_kind()
    {
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasComposer.Compose(Dto(_ => { }, descriptor: "g"), world.Catalog, world.Request, world.Input.Snapshot, null, null, new ReconcileComposeOptions("reconcile-stage-contract-9")));

        var mismatch = ReconcileAliasComposer.Compose(Dto(_ => { }, descriptor: "g"), world.Catalog, world.Request, world.Input.Snapshot, null, new string('0', 64), world.ComposeOptions);
        Assert.Equal(DocumentWorkException.IntegrityConflict, mismatch.ErrorCode);
        Assert.Equal([ReconcileViolationKind.IntegrityMismatch], mismatch.TypedViolations.Select(v => v.Kind));
    }
}
