using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-001 (deterministici, nessun database, nessuna inferenza): le fixture di replay estratte dagli archivi dei Run 2-5 sono complete, non
/// alterate e senza segreti. Il corpus e' sintetico (T040, seed 20261106). Le fixture si rigenerano con Extract-ReplayFixtures.ps1; ogni modifica fa fallire questi test.
/// </summary>
public sealed partial class ReconcileReplayFixturesTests
{
    private static readonly string[] Stages = ["leaf", "reduce", "cross", "expand", "compare"];

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    [GeneratedRegex(@"\b\d{1,3}(\.\d{1,3}){3}\b")]
    private static partial Regex Ipv4();

    [Fact]
    public void Fixtures_are_complete_and_untampered()
    {
        var root = ReplayFixtures.Root;
        var expected = ReplayFixtures.Calls.Select(c => ReplayFixture.FileStem(c.Run, c.Call) + ".json").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(34, expected.Length);

        // Esattamente le 34 fixture e PROVENANCE.json: nessun file in meno, nessuno in piu'.
        var present = Directory.GetFiles(root, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([.. expected.Append("PROVENANCE.json").Order(StringComparer.Ordinal)], [.. present.Order(StringComparer.Ordinal)]);

        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "PROVENANCE.json"), Utf8));
        Assert.Equal("corpus sintetico T040 (seed 20261106), nessun dato cliente", provenance.RootElement.GetProperty("declaration").GetString());
        var listed = provenance.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);
        Assert.Equal(expected.Order(StringComparer.Ordinal), listed.Keys.Order(StringComparer.Ordinal));

        var fixtures = new List<ReplayFixture>();
        foreach (var name in expected)
        {
            var path = Path.Combine(root, name);
            var bytes = File.ReadAllBytes(path);
            var entry = listed[name];

            // PROVENANCE: hash e dimensione dei byte della fixture, e gli hash dei file d'archivio.
            Assert.Equal(entry.GetProperty("sha256").GetString(), ReplayFixtures.Sha256Hex(bytes));
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), bytes.LongLength);

            var fixture = ReplayFixtures.Load(path);
            fixtures.Add(fixture);
            Assert.Equal(name, fixture.Name + ".json");
            Assert.Equal(entry.GetProperty("run").GetInt32(), fixture.Run);
            Assert.Equal(entry.GetProperty("call").GetInt32(), fixture.Call);
            Assert.Equal(entry.GetProperty("requestSha256").GetString(), fixture.RequestSha256);
            Assert.Equal(entry.GetProperty("assistantSha256").GetString(), fixture.AssistantSha256);
            Assert.Matches("^[0-9a-f]{64}$", fixture.RequestSha256);
            Assert.Matches("^[0-9a-f]{64}$", fixture.AssistantSha256);

            // Il testo dell'assistente e' quello archiviato, invariato.
            Assert.Equal(fixture.AssistantSha256, ReplayFixtures.Sha256Hex(Utf8.GetBytes(fixture.Assistant)));
            Assert.Contains(fixture.Stage, Stages);
            Assert.False(string.IsNullOrWhiteSpace(fixture.System), name);
            Assert.False(string.IsNullOrWhiteSpace(fixture.Session), name);
        }

        // Ogni sessione d'archivio compare con le sole chiamate attese.
        var sessions = provenance.RootElement.GetProperty("sessions").EnumerateArray().ToDictionary(s => s.GetProperty("run").GetInt32());
        Assert.Equal([2, 3, 4, 5], sessions.Keys.Order());
        foreach (var group in fixtures.GroupBy(f => f.Run))
        {
            Assert.Equal(group.Select(f => f.Call).Order(), sessions[group.Key].GetProperty("calls").EnumerateArray().Select(c => c.GetInt32()).Order());
            Assert.Single(group.Select(f => f.Session).Distinct());
            Assert.Equal(sessions[group.Key].GetProperty("session").GetString(), group.First().Session);
        }

        Assert.Equal(34, fixtures.Count);
    }

    [Fact]
    public void Fixtures_carry_no_secret_endpoint_or_http_path()
    {
        // Nessun header, endpoint o percorso HTTP: ne' nelle fixture ne' in PROVENANCE.json. L'IP del DGX non e' scritto in chiaro nel repository: si cerca ogni indirizzo IPv4.
        foreach (var path in Directory.GetFiles(ReplayFixtures.Root, "*.json"))
        {
            var text = File.ReadAllText(path, Utf8);
            var name = Path.GetFileName(path);
            foreach (var forbidden in new[] { "Bearer", "Authorization", "api_key", "/v1/" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{name} contiene «{forbidden}».");
            }

            Assert.False(Ipv4().IsMatch(text), $"{name} contiene un indirizzo IPv4.");
            Assert.False(text.Contains("\"headers\"", StringComparison.Ordinal), $"{name} contiene un campo headers.");
            Assert.False(text.Contains("\"path\": \"/", StringComparison.Ordinal), $"{name} contiene un percorso HTTP.");
        }
    }

    [Fact]
    public void Stage_and_shape_of_each_fixture_follow_the_batch_type()
    {
        foreach (var f in ReplayFixtures.All())
        {
            switch (f.Stage)
            {
                case "leaf":
                    Assert.True(f.Entries.Count > 0 && f.Nodes.Count == 0 && f.Focus is null && f.Groups is null && f.Items.Count == 0, f.Name);
                    Assert.Contains(f.View, new[] { "A", "B" });
                    break;
                case "reduce":
                    Assert.True(f.Entries.Count == 0 && f.Nodes.Count >= 2 && f.Focus is null && f.Items.Count == 0, f.Name);
                    Assert.Contains(f.View, new[] { "A", "B" });
                    break;
                case "cross":
                    Assert.True(f.Entries.Count == 0 && f.Nodes.Count >= 2 && f.Focus is null && f.Items.Count == 0, f.Name);
                    Assert.Equal("Cross", f.View);
                    break;
                case "expand":
                    Assert.True(f.Entries.Count > 0 && f.Nodes.Count == 0 && f.Focus is { Left: "N1", Right: "N2" }, f.Name);
                    break;
                case "compare":
                    Assert.True(f.Entries.Count == 2 && f.Nodes.Count == 0 && f.Focus is not null && f.Focus.Left.StartsWith('E') && f.Focus.Right.StartsWith('E'), f.Name);
                    break;
            }

            Assert.All(f.Entries, e => Assert.Matches("^E[1-9][0-9]*$", e.Alias));
            Assert.All(f.Entries, e => Assert.NotEmpty(e.Sources));
        }

        // Distribuzione per stadio delle 34 chiamate osservate.
        var counts = ReplayFixtures.All().GroupBy(f => f.Stage).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(17, counts["leaf"]);
        Assert.Equal(6, counts["reduce"]);
        Assert.Equal(2, counts["cross"]);
        Assert.Equal(3, counts["expand"]);
        Assert.Equal(6, counts["compare"]);
    }
}
