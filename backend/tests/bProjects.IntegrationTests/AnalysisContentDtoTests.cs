using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>T026 — DTO AnalysisContent 1.0.0: roundtrip e rifiuto strutturale (extra, mancanti, enum errato).</summary>
public sealed class AnalysisContentDtoTests
{
    private static IEnumerable<JsonObject> Valid()
    {
        var w = AnalysisFixtures.World();
        yield return AnalysisFixtures.NominalAi(w);
        yield return AnalysisFixtures.MinimalAi(w);
        yield return AnalysisFixtures.NominalPmEdit(w);
        yield return AnalysisFixtures.PmResolvesConflict(w);
        yield return AnalysisFixtures.Confirmed(AnalysisFixtures.NominalPmEdit(w));
    }

    [Fact]
    public void Valid_fixtures_roundtrip_and_stay_schema_valid()
    {
        foreach (var fixture in Valid())
        {
            var dto = AnalysisContentJson.Deserialize(fixture.ToJsonString());
            var json = AnalysisContentJson.Serialize(dto);

            Assert.Empty(ContractSchema.Violations((JsonObject)JsonNode.Parse(json)!));
            Assert.True(JsonNode.DeepEquals(fixture, JsonNode.Parse(json)), "roundtrip non fedele");
        }
    }

    public static TheoryData<string> Rejected => new(
        "extra-root", "extra-item", "missing-root", "missing-item-field", "bad-enum", "int-enum", "bad-version", "null-array", "bad-role");

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Structurally_invalid_content_is_rejected(string name)
    {
        var c = AnalysisFixtures.NominalAi(AnalysisFixtures.World());
        var item = (JsonObject)c["goals"]![0]!;
        var source = (JsonObject)c["sources"]![0]!;
        switch (name)
        {
            case "extra-root": c["x"] = 1; break;
            case "extra-item": item["x"] = 1; break;
            case "missing-root": c.Remove("conflicts"); break;
            case "missing-item-field": item.Remove("evidenceRefs"); break;
            case "bad-enum": item["classification"] = "Certain"; break;
            case "int-enum": item["classification"] = 0; break;
            case "bad-version": c["schemaVersion"] = "2.0.0"; break;
            case "null-array": c["goals"] = null; break;
            case "bad-role": source["messageRole"] = "system"; break;
        }

        Assert.ThrowsAny<JsonException>(() => AnalysisContentJson.Deserialize(c.ToJsonString()));
    }
}
