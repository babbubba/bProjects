using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace bProjects.IntegrationTests;

// 002 reconciliation-hardening RH-001: fixture di replay dagli archivi grezzi dei Run 2-5 (corpus sintetico T040). Contengono SOLO campi del corpo della richiesta
// (messaggio di sistema e messaggio utente scomposto) e il testo grezzo dell'assistente; mai header, percorso HTTP, endpoint o segreti.

internal sealed record ReplaySource(string Alias, string Role, string Quote);

internal sealed record ReplayEntry(string Alias, string Origin, string Kind, string Polarity, string Statement, IReadOnlyList<ReplaySource> Sources);

internal sealed record ReplayNode(string Alias, string View, string Descriptor);

internal sealed record ReplayItem(string Handle, IReadOnlyList<string> Evidence, string Text);

internal sealed record ReplayRelation(string Alias, string From, string Type, string To, string Basis);

internal sealed record ReplayFocus(string Type, string Left, string Right, string Rationale);

internal sealed record ReplayFixture(
    int Run, int Call, string Session, string RequestSha256, string AssistantSha256, string Stage, string View, string System, ReplayFocus? Focus,
    IReadOnlyDictionary<string, string>? Groups, IReadOnlyList<ReplayEntry> Entries, IReadOnlyList<ReplayNode> Nodes, IReadOnlyList<ReplayItem> Items,
    IReadOnlyList<ReplayRelation> Relations, string Assistant)
{
    public string Name => FileStem(Run, Call);

    public static string FileStem(int run, int call) => $"run{run}-{call:D3}";
}

internal static class ReplayFixtures
{
    /// <summary>Chiamate della gerarchia archiviate (assessment §1): Run 2: 022-026, Run 3: 022-026, Run 4: 022-036, Run 5: 022-030 (34 file).</summary>
    public static readonly IReadOnlyList<(int Run, int Call)> Calls =
    [
        .. Enumerable.Range(22, 5).Select(c => (2, c)),
        .. Enumerable.Range(22, 5).Select(c => (3, c)),
        .. Enumerable.Range(22, 15).Select(c => (4, c)),
        .. Enumerable.Range(22, 9).Select(c => (5, c)),
    ];

    public static string Root => Path.Combine(AppContext.BaseDirectory, "Reconciliation", "Replay");

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static ReplayFixture Load(int run, int call) => Load(Path.Combine(Root, ReplayFixture.FileStem(run, call) + ".json"));

    public static ReplayFixture Load(string path) =>
        JsonSerializer.Deserialize<ReplayFixture>(File.ReadAllText(path, new UTF8Encoding(false)), Json) ?? throw new JsonException($"Fixture nulla: {path}");

    public static IReadOnlyList<ReplayFixture> All() => [.. Calls.Select(c => Load(c.Run, c.Call))];

    public static TheoryData<int, int> Cases()
    {
        var data = new TheoryData<int, int>();
        foreach (var (run, call) in Calls)
        {
            data.Add(run, call);
        }

        return data;
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(new UTF8Encoding(false).GetBytes(text));
}
