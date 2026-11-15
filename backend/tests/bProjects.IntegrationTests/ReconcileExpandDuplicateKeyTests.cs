using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// RH-051 (4ª run reale): l'espansione v11 ha emesso due candidati con la stessa chiave locale `f13` e ha fatto fallire il run con `work_output_invalid`. La chiave locale di un candidato non è persistita né
/// referenziata (il canonico non la contiene; l'identità è <c>CandidateId</c>), quindi nello stadio expand della v11 una chiave ripetuta non è un rifiuto. Replay OFFLINE dell'output reale conservato in
/// m5-evidence/run4: nessuna inferenza, nessun database. Le altre regole (identità canonica duplicata, tetto di 16, alias, v6/v10, compare) restano fail-closed.
/// </summary>
public sealed class ReconcileExpandDuplicateKeyTests
{
    private const string DuplicateKeyMessage = "chiave locale f13 duplicata";

    private static string RealOutput()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "specs", "002-project-documents")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "specs", "002-project-documents", "validation", "reconciliation-hardening", "m5-evidence", "run4", "failed-expand-batch-assistant.txt"));
    }

    /// <summary>Mondo con un ledger di 194 evidenze (come la run) e un batch di espansione i cui estremi sono gli alias realmente usati dall'output del modello.</summary>
    private static ReconcileBatchWorld World(string json, ContractProfile profile)
    {
        var aliases = JsonNode.Parse(json)!["candidates"]!.AsArray().SelectMany(c => new[] { c!["left"]!.GetValue<string>(), c["right"]!.GetValue<string>() }).Select(a => int.Parse(a[1..])).Distinct().Order().ToArray();
        var specs = Enumerable.Range(1, 194).Select(n => new M2Evidence(EvidenceOriginKind.Document, EvidenceKind.Requirement, EvidenceCertainty.Declared, $"Requisito sintetico numero {n}.")).ToList();
        return ReconcileBatchWorld.Create(ReconcileBatchSchema.Expand, profile, M2Ledger.Input(specs), aliases);
    }

    private static string WithKeys(string json, Func<int, string> key)
    {
        var node = JsonNode.Parse(json)!;
        var candidates = node["candidates"]!.AsArray();
        for (var i = 0; i < candidates.Count; i++)
        {
            candidates[i]!["key"] = key(i);
        }

        return node.ToJsonString();
    }

    [Fact]
    public void The_original_run4_output_is_rejected_only_for_the_duplicate_key_by_the_historic_composer_and_accepted_by_the_v11_expand_stage()
    {
        var real = RealOutput();
        var keys = JsonNode.Parse(real)!["candidates"]!.AsArray().Select(c => c!["key"]!.GetValue<string>()).ToList();
        Assert.Equal(16, keys.Count);
        Assert.Equal(["f13"], keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));   // premessa: l'unica anomalia è f13 due volte

        // Compositore storico (v10: nessuna opzione per stadio): rifiuto con UNA sola violazione, quella della chiave.
        var historic = World(real, ContractProfile.V10).ComposeOnly(real);
        Assert.False(historic.IsValid);
        var violation = Assert.Single(historic.Violations);
        Assert.Contains(DuplicateKeyMessage, violation, StringComparison.Ordinal);

        // v11, espansione: accettato.
        var v11 = World(real, ContractProfile.V11).ComposeOnly(real);
        Assert.True(v11.IsValid, string.Join(" | ", v11.Violations));
        Assert.Equal(16, v11.Result!.Candidates.Count);   // numero, ordine e contenuto dei candidati preservati
    }

    [Fact]
    public void The_accepted_output_has_the_same_canonical_identities_and_content_as_the_same_output_with_unique_keys_and_the_raw_output_hash_is_preserved()
    {
        var real = RealOutput();
        var renumbered = WithKeys(real, i => $"f{i + 1}");
        var world = World(real, ContractProfile.V11);

        var duplicate = world.ComposeOnly(real);
        var unique = world.ComposeOnly(renumbered);

        Assert.True(duplicate.IsValid, string.Join(" | ", duplicate.Violations));
        Assert.True(unique.IsValid, string.Join(" | ", unique.Violations));
        Assert.Equal(unique.Result!.Candidates.Select(c => c.CandidateId), duplicate.Result!.Candidates.Select(c => c.CandidateId));   // identità canoniche, nello stesso ordine
        Assert.Equal(unique.CanonicalHash, duplicate.CanonicalHash);   // il canonico non dipende dalla chiave locale
        Assert.Equal(16, duplicate.Result.Candidates.Select(c => c.CandidateId).Distinct().Count());
        Assert.NotEqual(unique.AiOutputHash, duplicate.AiOutputHash);   // l'output grezzo resta tracciabile: l'hash AI copre il DTO così com'è stato emesso
        Assert.Equal(
            unique.Result.Candidates.Select(c => (c.Left, c.Right, c.ProposedType, c.Basis, c.Rationale)),
            duplicate.Result.Candidates.Select(c => (c.Left, c.Right, c.ProposedType, c.Basis, c.Rationale)));

        // Replay: la ricomposizione con l'hash canonico committato coincide (nessuna dipendenza dalla chiave).
        var replay = world.ComposeOnly(real);
        Assert.Equal(duplicate.CanonicalHash, replay.CanonicalHash);
    }

    [Fact]
    public void A_duplicate_canonical_candidate_is_still_rejected_in_the_v11_expand_stage_even_with_duplicate_keys()
    {
        var real = RealOutput();
        var node = JsonNode.Parse(real)!;
        var candidates = node["candidates"]!.AsArray();
        var clone = JsonNode.Parse(candidates[0]!.ToJsonString())!;   // stessi estremi e tipo: stessa identità canonica
        clone["key"] = "f1";
        candidates.RemoveAt(15);
        candidates.Add(clone);
        var json = node.ToJsonString();

        var composition = World(json, ContractProfile.V11).ComposeOnly(json);

        Assert.False(composition.IsValid);
        Assert.Contains(composition.Violations, v => v.Contains("candidato duplicato", StringComparison.Ordinal));
    }

    [Fact]
    public void Other_violations_stay_fail_closed_unknown_alias_cap_and_every_other_stage_or_contract()
    {
        var real = RealOutput();
        var world = World(real, ContractProfile.V11);

        // alias sconosciuto con chiavi duplicate: l'alias resta una violazione
        var unknown = JsonNode.Parse(real)!;
        unknown["candidates"]![13]!["left"] = "E999";
        var unknownResult = world.ComposeOnly(unknown.ToJsonString());
        Assert.False(unknownResult.IsValid);
        Assert.Contains(unknownResult.Violations, v => v.Contains("E999", StringComparison.Ordinal));

        // oltre il tetto di 16 candidati: nessun troncamento, rifiuto
        var over = JsonNode.Parse(real)!;
        var list = over["candidates"]!.AsArray();
        list.Add(JsonNode.Parse("""{"key":"f13","left":"E3","right":"E6","type":"Qualifies","basis":"Inferred","rationale":"extra"}"""));
        var overResult = world.ComposeOnly(over.ToJsonString());
        Assert.False(overResult.IsValid);
        Assert.Contains(overResult.Violations, v => v.Contains("oltre il tetto", StringComparison.Ordinal));

        // altri contratti e stadi: la chiave duplicata resta un rifiuto (v10 espansione, v6 espansione)
        foreach (var profile in new[] { ContractProfile.V10, ContractProfile.V6 })
        {
            var other = World(real, profile).ComposeOnly(real);
            Assert.False(other.IsValid);
            Assert.Contains(other.Violations, v => v.Contains(DuplicateKeyMessage, StringComparison.Ordinal));
        }
    }
}
