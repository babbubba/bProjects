using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// DIAGNOSI OFFLINE (nessuna rete, nessun database, nessuna inferenza) della prova DGX isolata Single del 2026-11-13: ri-esegue il validatore di PRODUZIONE
/// (<see cref="GapStageContracts.Decode"/> + <see cref="GapAssessmentRules.Check"/>, cioè il percorso di <c>GapStageContracts.Validate</c>) sulla risposta ORIGINALE conservata, con gli input del validatore ricostruiti
/// dalla richiesta realmente inviata allo stadio (alias→tipo delle 112 voci offerte, chiavi delle domande aperte, nessun conflitto). Le copie modificate sono SOLO diagnostiche: non sono evidenza PASS.
/// Artefatti: <c>specs/003-retrieval-evidence-search/validation/isolated-single-run1/</c>.
/// </summary>
public sealed class IsolatedSingleRunDiagnosticTests
{
    // SHA-256 della risposta originale del modello (002-assistant.txt dell'archivio MafDiagnostics) e del corpo della richiesta dello stadio (stage-request.json): immutabili.
    private const string ResponseSha256 = "760738b4342afe2e6358dc2df77d008241ceb84dff804cbf28ed74f41bf5c2c7";
    private const string RequestSha256 = "99d4cae20d58ceb65a6e87f0228aa315a23faafae2eea3ce60d49de96df7b108";

    private static string Dir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "specs", "003-retrieval-evidence-search", "validation", "isolated-single-run1");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("isolated-single-run1 non raggiungibile da questo albero di build.");
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Response() => Lf(File.ReadAllText(Path.Combine(Dir(), "stage-response.json")));

    private static string RequestFile() => Lf(File.ReadAllText(Path.Combine(Dir(), "stage-request.json")));

    private static string Fold(string text) => DocumentSmallFlowMafRealTests.Fold(text);

    /// <summary>Testo del messaggio dell'utente realmente inviato allo stadio (le istruzioni di sistema sono escluse).</summary>
    private static string UserText()
    {
        using var doc = JsonDocument.Parse(RequestFile());
        return string.Join('\n', doc.RootElement.GetProperty("body").GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() is not ("system" or "developer"))
            .Select(m => m.GetProperty("content").ValueKind == JsonValueKind.String ? m.GetProperty("content").GetString()! : m.GetProperty("content").GetRawText()));
    }

    private sealed record Voice(string Alias, string Kind, string Document, string Statement);

    private static List<Voice> Voices(string text) =>
        [.. Regex.Matches(text, "<<<VOCE \\[(E\\d+)\\] origine=\\w+ tipo=(\\w+)[^>\\n]*? documento=\"([^\"]+)\"[^>\\n]*>>>\\n([^\\n]*)\\n")
            .Select(m => new Voice(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value))];

    /// <summary>Input del validatore ricostruiti dalla richiesta: alias→tipo delle voci offerte, chiavi delle domande aperte e dei conflitti offerti.</summary>
    private static (IReadOnlyDictionary<string, EvidenceKind> Assigned, string[] Questions, string[] Conflicts) ValidatorInputs()
    {
        var text = UserText();
        var assigned = Voices(text).ToDictionary(v => v.Alias, v => v.Kind == "Gap" ? EvidenceKind.Gap : EvidenceKind.Requirement, StringComparer.Ordinal);
        var questions = Regex.Matches(text, @"^\[(i\d+)\] \(E\d+\)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        var conflictsSection = text[text.IndexOf("Conflitti aperti canonici", StringComparison.Ordinal)..];
        var conflicts = Regex.Matches(conflictsSection, @"^\[(c\d+)\]", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        return (assigned, questions, conflicts);
    }

    private static IReadOnlyList<string> Validate(string responseJson)
    {
        // Percorso di GapStageContracts.Validate: la decodifica strict applica GIA le regole di forma (un JsonException ⇒ work_output_invalid), poi Check applica quelle sull'input assegnato.
        var (assigned, questions, conflicts) = ValidatorInputs();
        AiGapStageOutput output;
        try
        {
            output = GapStageContracts.Decode(responseJson);
        }
        catch (JsonException e)
        {
            return [e.Message];
        }

        return GapAssessmentRules.Check(output.GapAssessments, assigned, questions, conflicts, allowMissing: true);
    }

    private static string WithE5(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(Response())!.AsObject();
        change(node["gapAssessments"]!.AsArray().Single(r => r!["gap"]!.GetValue<string>() == "E5")!.AsObject());
        return node.ToJsonString();
    }

    [Fact]
    public void The_original_response_and_request_are_preserved_with_their_hashes()
    {
        Assert.Equal(ResponseSha256, Sha(Response()));
        Assert.Equal(RequestSha256, Sha(RequestFile()));
    }

    [Fact]
    public void The_validator_inputs_reconstructed_from_the_request_are_the_ones_the_stage_was_offered()
    {
        var (assigned, questions, conflicts) = ValidatorInputs();
        Assert.Equal(112, assigned.Count);
        Assert.Equal(["E2", "E3", "E4", "E5", "E6"], assigned.Where(a => a.Value == EvidenceKind.Gap).Select(a => a.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["i1", "i2", "i3", "i4", "i5"], questions.Order(StringComparer.Ordinal));
        Assert.Empty(conflicts);   // «Conflitti aperti canonici dell'analisi: (nessuno)»
    }

    [Fact]
    public void The_production_validator_rejects_the_original_response_for_exactly_the_E5_missing_open_references()
    {
        var violations = Validate(Response());

        // Rifiutata gia alla decodifica strict dello stadio (regola di forma): messaggio identico a quello del run («JSON non conforme a gap-stage-alias-1: gapAssessments non valide: …»).
        Assert.Equal(["gapAssessments non valide: gapAssessments[E5]: Open richiede esattamente uno fra openItem e openConflict."], violations);
        var e5 = JsonNode.Parse(Response())!["gapAssessments"]!.AsArray().Single(r => r!["gap"]!.GetValue<string>() == "E5")!;
        Assert.Null(e5["openItem"]);
        Assert.Null(e5["openConflict"]);
    }

    [Fact]
    public void DIAGNOSTIC_ONLY_a_copy_of_the_response_with_E5_openItem_i3_passes_the_same_validator_and_covers_every_gap()
    {
        // COPIA diagnostica in memoria: non e la risposta del modello e NON e evidenza di PASS.
        var repaired = WithE5(e5 => e5["openItem"] = "i3");

        Assert.Empty(Validate(repaired));
        var (assigned, _, _) = ValidatorInputs();
        Assert.Empty(GapAssessmentRules.MissingGaps(GapStageContracts.Decode(repaired).GapAssessments, assigned));   // Decode non lancia: nessuna violazione di forma
        Assert.Equal(ResponseSha256, Sha(Response()));   // l'originale non e stato toccato
    }

    [Fact]
    public void DIAGNOSTIC_ONLY_the_other_ways_to_set_the_references_are_judged_by_the_same_rules()
    {
        var conflict = Validate(WithE5(e5 => e5["openConflict"] = "c1"));
        Assert.Single(conflict);                                                                              // c1 non e offerto: il messaggio non aveva conflitti
        Assert.Contains("openConflict", conflict[0], StringComparison.Ordinal);

        var both = Validate(WithE5(e5 =>
        {
            e5["openItem"] = "i3";
            e5["openConflict"] = "c1";
        }));
        Assert.Contains(both, v => v.Contains("esattamente uno", StringComparison.Ordinal));

        var unknownItem = Validate(WithE5(e5 => e5["openItem"] = "i9"));
        Assert.Contains(unknownItem, v => v.Contains("openItem", StringComparison.Ordinal) && v.Contains("i9", StringComparison.Ordinal));
    }

    [Fact]
    public void E3_cites_E1_as_support_and_the_rationale_misnames_it_as_E135()
    {
        var text = UserText();
        var voices = Voices(text).ToDictionary(v => v.Alias);
        var e3 = JsonNode.Parse(Response())!["gapAssessments"]!.AsArray().Single(r => r!["gap"]!.GetValue<string>() == "E3")!;

        // Il supporto strutturato e E1: «tre giornate» di formazione (documento d'estensione parziale): giustifica «durata» e non offre alcun costo.
        Assert.Equal(["E1"], e3["supportingEvidence"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("tre giornate", voices["E1"].Statement, StringComparison.Ordinal);
        Assert.Contains("formazione", voices["E1"].Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("costo", Fold(voices["E1"].Statement), StringComparison.Ordinal);

        // La motivazione cita E135, che e un'altra cosa (segnalazioni di guasto, 40 minuti): alias sbagliato nel solo testo libero.
        var rationale = e3["rationale"]!.GetValue<string>();
        Assert.Contains("E135", rationale, StringComparison.Ordinal);
        Assert.Contains("tre giornate", rationale, StringComparison.Ordinal);
        Assert.Contains("40 minuti", voices["E135"].Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("formazione", Fold(voices["E135"].Statement), StringComparison.Ordinal);
        Assert.DoesNotContain(voices.Values, v => v.Alias != "E1" && Fold(v.Statement).Contains("tre giornate", StringComparison.Ordinal));   // nessun altro candidato ha «tre giornate»
    }

    [Fact]
    public void The_real_package_contains_the_expected_evidence_for_every_gap_and_never_the_excluded_document()
    {
        // Fatti verificabili sul messaggio REALMENTE inviato (retrieval e composizione del pacchetto reali): non sono un PASS end-to-end.
        var text = UserText();
        var voices = Voices(text);
        var candidates = voices.Where(v => v.Kind != "Gap").ToList();
        Assert.Equal(5, voices.Count(v => v.Kind == "Gap"));
        Assert.Equal(107, candidates.Count);
        Assert.Contains("searchBasis: retrieval", text, StringComparison.Ordinal);
        Assert.Contains("retrievalComplete: false", text, StringComparison.Ordinal);

        foreach (var expected in RetrievalGapCampaign.Single.Expectations.Where(e => e.SupportQuotes.Length > 0))
        {
            foreach (var side in expected.SupportQuotes)
            {
                Assert.Contains(candidates, c => side.Any(q => Fold(c.Statement).Contains(Fold(q), StringComparison.Ordinal)));   // present, partial e i DUE lati del conflitto
            }
        }

        Assert.DoesNotContain(RetrievalGapCampaign.ExcludedDocument.FileName, text, StringComparison.Ordinal);
        Assert.DoesNotContain("cinquantamila", Fold(text), StringComparison.Ordinal);
        Assert.DoesNotContain(candidates, c => c.Document == RetrievalGapCampaign.ExcludedDocument.FileName);

        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir(), "stage-result.json")));
        Assert.InRange(result.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32(), 1, RetrievalGapCampaign.BudgetTokens);
        Assert.Equal("stop", result.RootElement.GetProperty("finishReason").GetString());
    }
}
