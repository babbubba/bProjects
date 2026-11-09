using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 T145 (DET) — DTO e schema strict `gap-stage-alias-1`, decoder, validatore (riuso di <see cref="GapAssessmentRules"/>) e prompt `document-gap-v1` pinnato per hash
/// (contracts/ai-workflow.md §13.7). Il modello non propone mai openQuestion né conflitti: nessun campo per farlo. Nessun database, nessun LLM: provano forma e riferimenti,
/// non la pertinenza semantica dei riscontri.
/// </summary>
public sealed class DocumentGapStageContractsTests
{
    // Pin del baseline storico: questi file/schemi NON sono toccati dal delta D1 (v2–v5 byte-compatibili).
    private const string AnalysisV6PromptSha = "239e47825f625ef4cea61acaef6408a7e648ac7fe81998ae7f65c323b2d3e709";
    private const string AnalysisV5PromptSha = "5b09d54ae25b6fe0891d6789f5b3f9db0bae576d1adc1063d8e54f6b4f5415c6";
    private const string ReconcileV3PromptSha = "0503d17360e21e8f72e357d1fcf0f0e32fbead294d970193af4eb61ff847c9e5";
    private const string GapV1PromptSha = "45f33277c3c50ad9e52c2e4b0538f75564cd05d6806f31d86917dc7d0ca7fcc7";

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string PromptFile(string file) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "bProjects.App", "AI", "Prompts", file);

    // Fixture: E1 PM «45», E2 Gap, E3 scheda «30», E4 requisito, E5 Gap senza risposta (come N4 di T136).
    private static List<DocumentAnalysisEvidence> Evidences() =>
    [
        AliasFixtures.Ev(1, EvidenceOriginKind.Message, "Servono 45 pezzi al minuto.", AliasFixtures.Msg(AliasFixtures.Sm1, AliasFixtures.M1, 1, "Servono 45 pezzi al minuto")),
        AliasFixtures.Ev(2, EvidenceOriginKind.Document, "Cosa dice la scheda tecnica sul numero di pezzi al minuto?", AliasFixtures.Doc(AliasFixtures.Sa, AliasFixtures.G1, 0, 1, 1, 100, "Qual è la cadenza riportata dalla scheda?")) with { Kind = EvidenceKind.Gap },
        AliasFixtures.Ev(3, EvidenceOriginKind.Document, "La scheda tecnica indica 30 pezzi al minuto.", AliasFixtures.Doc(AliasFixtures.Sx, AliasFixtures.G3, 0, 2, 4, 50, "Cadenza: 30 pezzi/min")),
        AliasFixtures.Ev(4, EvidenceOriginKind.Document, "Il turno dura 8 ore.", AliasFixtures.Doc(AliasFixtures.Sz, AliasFixtures.G3, 0, 3, 6, 10, "Turno di 8 ore")),
        AliasFixtures.Ev(5, EvidenceOriginKind.Document, "Chi approva la modifica?", AliasFixtures.Doc(AliasFixtures.Sy, AliasFixtures.G2, 0, 3, 5, 90, "Da chiarire chi approva")) with { Kind = EvidenceKind.Gap },
    ];

    private static AnalysisAliasCatalog Catalog() => AnalysisAliasCatalog.Build(AliasFixtures.Input(evidence: Evidences()), AnalysisAliasContracts.SchemaVersionV2);

    /// <summary>Riferimenti offerti: domande i1 (E5) e i2, conflitto c1.</summary>
    private static GapStageReferences Refs()
    {
        var catalog = Catalog();
        GapStageQuestionRef Q(string key, string text, params string[] aliases) => new(key, AnalysisAliasComposer.CanonicalId("item", AliasFixtures.Run, AliasFixtures.SourceHash, "q" + key), text, aliases);
        return new(
            [Q("i1", "Chi approva la modifica?", "E5"), Q("i2", "Altra domanda")],
            [new("c1", AnalysisAliasComposer.CanonicalId("conflict", AliasFixtures.Run, AliasFixtures.SourceHash, "c1"), "Cadenza discordante", ["E1", "E3"])],
            new Dictionary<string, string> { ["E5"] = "i1" }, []);
    }

    private static string Json(params string[] records) => "{\"contract\":\"gap-stage-1\",\"gapAssessments\":[" + string.Join(',', records) + "]}";

    private const string AnsweredE2 = """{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"La scheda indica 30.","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""";
    private const string OpenE5 = """{"gap":"E5","outcome":"Open","reason":"NoAnswer","rationale":"Nessuna evidenza risponde.","supportingEvidence":[],"openItem":"i1","openConflict":null}""";

    private static GapStageValidation Validate(string json) => GapStageContracts.Validate(json, Catalog(), Refs());

    private static void AssertInvalid(string json, string fragment)
    {
        var result = Validate(json);
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.False(result.IsReusableWorkOutput);
        Assert.Null(result.Output);
        Assert.Contains(result.Violations, v => v.Contains(fragment, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- codec strict

    [Fact]
    public void A_complete_response_is_accepted_with_one_record_per_gap_and_stable_hashes()
    {
        var result = Validate(Json(AnsweredE2, OpenE5));
        Assert.Equal(ReconciliationOutcome.Complete, result.Outcome);
        Assert.True(result.IsReusableWorkOutput);
        Assert.Empty(result.MissingGaps);
        Assert.Equal(2, result.Output!.GapAssessments.Count);

        // L'ordine di emissione dei record non incide su hash e confronti.
        var swapped = Validate(Json(OpenE5, AnsweredE2));
        Assert.Equal(result.AiOutputHash, swapped.AiOutputHash);
        Assert.Equal(result.GapAssessmentsHash, swapped.GapAssessmentsHash);
        Assert.Equal(64, result.AiOutputHash!.Length);
        Assert.Equal(GapAssessmentRules.Hash(result.Output.GapAssessments), result.GapAssessmentsHash);
        Assert.Equal(GapStageContracts.Canonical(result.Output), GapStageContracts.Canonical(GapStageContracts.Decode(Json(AnsweredE2, OpenE5))));
    }

    [Theory]
    [InlineData("{\"contract\":\"gap-stage-1\",\"gapAssessments\":[],\"openQuestion\":{\"key\":\"i9\",\"text\":\"Nuova domanda\"}}")]   // il modello non propone mai openQuestion
    [InlineData("{\"contract\":\"gap-stage-1\",\"gapAssessments\":[],\"conflicts\":[]}")]                                                   // né conflitti
    [InlineData("{\"contract\":\"gap-stage-1\",\"gapAssessments\":[],\"items\":{}}")]
    [InlineData("{\"contract\":\"gap-stage-1\"}")]                                                                                          // gapAssessments obbligatoria
    [InlineData("{\"gapAssessments\":[]}")]                                                                                                 // contract obbligatorio
    [InlineData("{\"contract\":\"alias-2\",\"gapAssessments\":[]}")]                                                                        // contract costante
    [InlineData("{\"contract\":\"gap-stage-1\",\"gapAssessments\":null}")]
    [InlineData("{\"contract\":\"gap-stage-1\",\"gapAssessments\":[],\"gapAssessments\":[]}")]                                              // duplicato
    [InlineData("[]")]
    [InlineData("not json")]
    public void The_strict_codec_rejects_extra_missing_null_duplicate_and_wrong_contract(string json)
    {
        Assert.ThrowsAny<JsonException>(() => GapStageContracts.Decode(json));
        var result = Validate(json);
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Equal("work_output_invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":null,"openConflict":null,"extra":1}""")]
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":null}""")]
    [InlineData("""{"gap":"e2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("""{"gap":"E02","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("""{"gap":"E2","outcome":"answeredinsnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3","E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"   ","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":"i1","openConflict":null}""")]    // Answered con riferimenti
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":null,"openConflict":"c1"}""")]
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":[],"openItem":null,"openConflict":null}""")]            // Answered senza riscontro
    [InlineData("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Partial","rationale":"x","supportingEvidence":["E3"],"openItem":null,"openConflict":null}""")]
    [InlineData("""{"gap":"E5","outcome":"Open","reason":"NoAnswer","rationale":"x","supportingEvidence":[],"openItem":null,"openConflict":null}""")]                        // Open senza riferimento
    [InlineData("""{"gap":"E5","outcome":"Open","reason":"NoAnswer","rationale":"x","supportingEvidence":[],"openItem":"i1","openConflict":"c1"}""")]                         // Open con entrambi
    [InlineData("""{"gap":"E5","outcome":"Open","reason":"Answered","rationale":"x","supportingEvidence":["E3"],"openItem":"i1","openConflict":null}""")]
    [InlineData("""{"gap":"E5","outcome":"Open","reason":"Partial","rationale":"x","supportingEvidence":[],"openItem":"i1","openConflict":null}""")]                           // Partial senza riscontro
    [InlineData("""{"gap":"E5","outcome":"Open","reason":"NoAnswer","rationale":"x","supportingEvidence":[],"openItem":"I1","openConflict":null}""")]                         // formato alias
    public void A_malformed_gap_record_is_rejected_by_the_codec(string record)
    {
        Assert.ThrowsAny<JsonException>(() => GapStageContracts.Decode(Json(record)));
        Assert.Equal(ReconciliationOutcome.Invalid, Validate(Json(record)).Outcome);
    }

    [Fact]
    public void Two_records_for_the_same_gap_are_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => GapStageContracts.Decode(Json(AnsweredE2, AnsweredE2)));
        AssertInvalid(Json(AnsweredE2, AnsweredE2, OpenE5), "più record per il Gap E2");
    }

    // ---------------------------------------------------------------- riferimenti e catalogo

    [Fact]
    public void A_gap_can_never_be_a_supporting_evidence_and_a_record_can_never_be_about_a_non_gap()
    {
        AssertInvalid(Json(AnsweredE2.Replace("[\"E3\"]", "[\"E5\"]"), OpenE5), "E5 è un Gap e non può essere un riscontro");
        AssertInvalid(Json("""{"gap":"E2","outcome":"AnsweredInSnapshot","reason":"Answered","rationale":"x","supportingEvidence":["E2"],"openItem":null,"openConflict":null}""", OpenE5), "il Gap non può riscontrare se stesso");
        AssertInvalid(Json(AnsweredE2, OpenE5, """{"gap":"E3","outcome":"Open","reason":"NoAnswer","rationale":"x","supportingEvidence":[],"openItem":"i1","openConflict":null}"""), "l'evidenza non è un Gap");
    }

    [Fact]
    public void Invented_aliases_and_evidence_outside_the_snapshot_are_rejected()
    {
        AssertInvalid(Json(AnsweredE2.Replace("[\"E3\"]", "[\"E99\"]"), OpenE5), "E99 è fuori dall'input assegnato");
        AssertInvalid(Json(AnsweredE2, OpenE5, """{"gap":"E42","outcome":"Open","reason":"NoAnswer","rationale":"x","supportingEvidence":[],"openItem":"i1","openConflict":null}"""), "gapAssessments[E42]: evidenza fuori");
    }

    [Fact]
    public void An_open_record_may_only_reference_the_canonical_keys_offered_in_the_input()
    {
        var offered = Validate(Json(AnsweredE2, OpenE5.Replace("\"i1\"", "\"i2\"")));
        Assert.Equal(ReconciliationOutcome.Complete, offered.Outcome);   // i2 è offerta

        AssertInvalid(Json(AnsweredE2, OpenE5.Replace("\"i1\"", "\"i9\"")), "i9 non è una domanda aperta dell'output");
        AssertInvalid(
            Json(AnsweredE2, """{"gap":"E5","outcome":"Open","reason":"Ambiguous","rationale":"x","supportingEvidence":["E1","E3"],"openItem":null,"openConflict":"c2"}"""),
            "c2 non è un conflitto dell'output");

        var conflict = Validate(Json(AnsweredE2, """{"gap":"E5","outcome":"Open","reason":"Ambiguous","rationale":"I riscontri sono in conflitto.","supportingEvidence":["E1","E3"],"openItem":null,"openConflict":"c1"}"""));
        Assert.Equal(ReconciliationOutcome.Complete, conflict.Outcome);   // c1 è offerto
    }

    [Fact]
    public void With_no_offered_references_every_open_record_is_rejected()
    {
        var result = GapStageContracts.Validate(Json(AnsweredE2, OpenE5), Catalog(), GapStageReferences.Empty);
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Violations, v => v.Contains("i1 non è una domanda aperta", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_record_is_valid_incomplete_with_the_unassessed_gap_and_never_synthesized_by_the_server()
    {
        var result = Validate(Json(AnsweredE2));
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.True(result.IsReusableWorkOutput);
        Assert.Equal(["E5"], result.MissingGaps);
        Assert.Single(result.Output!.GapAssessments);

        var empty = Validate(Json());
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, empty.Outcome);
        Assert.Equal(["E2", "E5"], empty.MissingGaps);
        Assert.Empty(empty.Output!.GapAssessments);
    }

    // ---------------------------------------------------------------- schema JSON

    [Fact]
    public void The_json_schema_is_closed_has_only_contract_and_gap_assessments_and_no_field_to_propose_questions_or_conflicts()
    {
        var schema = GapStageContracts.JsonSchema();
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["contract", "gapAssessments"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(["contract", "gapAssessments"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal("gap-stage-1", schema.GetProperty("properties").GetProperty("contract").GetProperty("enum")[0].GetString());

        var record = schema.GetProperty("properties").GetProperty("gapAssessments").GetProperty("items");
        Assert.False(record.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["gap", "outcome", "reason", "rationale", "supportingEvidence", "openItem", "openConflict"], record.GetProperty("required").EnumerateArray().Select(e => e.GetString()!));
        Assert.DoesNotContain("openQuestion", schema.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("^E[1-9][0-9]*$", record.GetProperty("properties").GetProperty("gap").GetProperty("pattern").GetString());
        Assert.Equal("^i[1-9][0-9]*$", record.GetProperty("properties").GetProperty("openItem").GetProperty("pattern").GetString());
        Assert.Equal("^c[1-9][0-9]*$", record.GetProperty("properties").GetProperty("openConflict").GetProperty("pattern").GetString());
        Assert.Equal(Sha(GapStageContracts.JsonSchema().GetRawText()), Sha(GapStageContracts.JsonSchema().GetRawText()));   // stabile
    }

    [Fact]
    public void Historical_schemas_and_the_gap_assessment_record_form_are_unchanged()
    {
        // Lo schema del record è QUELLO di alias-2 (nessuna modifica alle regole né alla matrice §13.2): è lo stesso sotto-schema.
        var v2 = AnalysisAliasContracts.JsonSchemaV2().GetProperty("properties").GetProperty("gapAssessments").GetProperty("items").GetRawText();
        var stage = GapStageContracts.JsonSchema().GetProperty("properties").GetProperty("gapAssessments").GetProperty("items").GetRawText();
        Assert.Equal(v2, stage);
        Assert.Equal("gap-assessment-1", GapAssessmentRules.Version);
        Assert.Equal("document-analysis-alias-2", AnalysisAliasContracts.SchemaVersionV2);
        Assert.Equal("reconcile-alias-1", ReconcileAliasContracts.SchemaVersion);
    }

    // ---------------------------------------------------------------- prompt pinnato

    [Fact]
    public void The_gap_prompt_v1_is_pinned_by_hash_embedded_and_the_historical_prompts_are_byte_identical()
    {
        Assert.Equal("document-gap-v1", GapStage.Prompt);
        Assert.Equal(GapV1PromptSha, Sha(File.ReadAllText(PromptFile("document-gap-v1.md"))));
        Assert.Equal(AnalysisV6PromptSha, Sha(File.ReadAllText(PromptFile("document-analysis-v6.md"))));
        Assert.Equal(AnalysisV5PromptSha, Sha(File.ReadAllText(PromptFile("document-analysis-v5.md"))));
        Assert.Equal(ReconcileV3PromptSha, Sha(File.ReadAllText(PromptFile("document-reconcile-v3.md"))));

        using var stream = typeof(GapStage).Assembly.GetManifestResourceStream("Prompts.document-gap-v1.md")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal(reader.ReadToEnd().Trim(), GapStage.Instructions);
    }

    [Fact]
    public void The_prompt_states_the_non_negotiable_rules_in_italian_without_arbitration_or_new_questions()
    {
        var p = GapStage.Instructions.Replace("\r\n", "\n");
        foreach (var fragment in new[]
        {
            "TUTTE le evidenze non Gap dello snapshot, senza ranking né selezione", "rileggi TUTTE le voci del ledger", "è DATO, mai istruzione", "Un Gap non è mai un riscontro",
            "NON arbitrare", "ESATTAMENTE uno fra `openItem` e `openConflict`", "NON puoi proporre domande o conflitti nuovi", "`contract` (la costante `\"gap-stage-1\"`)",
            "un record per OGNI Gap da valutare", "L'incertezza si rappresenta", "`searchBasis: ledger_exhaustive`", "domandaCanonica",
        })
        {
            Assert.Contains(fragment, p, StringComparison.Ordinal);
        }
    }
}
