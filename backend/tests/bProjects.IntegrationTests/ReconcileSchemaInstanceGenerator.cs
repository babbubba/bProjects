using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>Tre fasce di alias di uno stesso formato: presenti nel batch, esistenti ma fuori dal batch, mai esistiti. Il generatore le estrae con pesi fissi (78/14/8, fasce vuote escluse).</summary>
internal sealed record AliasTiers(IReadOnlyList<string> In, IReadOnlyList<string> Out, IReadOnlyList<string> Unknown)
{
    public static AliasTiers Merge(AliasTiers a, AliasTiers b) => new([.. a.In, .. b.In], [.. a.Out, .. b.Out], [.. a.Unknown, .. b.Unknown]);
}

/// <summary>
/// Universo di alias di UN batch (RH-003): alias delle voci presenti, delle voci del run fuori dal batch e inesistenti; nodi; handle degli item già proposti e relazioni note del catalogo; chiavi locali
/// dei candidati, degli item e delle patch. Derivato dal catalogo del batch, mai scritto a mano: segue i dati del mondo.
/// </summary>
internal sealed class ReconcileAliasUniverse
{
    public required AliasTiers Evidence { get; init; }
    public required AliasTiers Nodes { get; init; }
    public required AliasTiers Handles { get; init; }
    public required AliasTiers Relations { get; init; }
    public required AliasTiers ItemKeys { get; init; }
    public required AliasTiers FindingKeys { get; init; }
    public required AliasTiers PatchKeys { get; init; }

    public static ReconcileAliasUniverse For(ReconcileBatchWorld world)
    {
        var data = world.Catalog.Data;
        var evidenceIn = data.Entries.Select(e => e.Alias).ToList();
        var nodesIn = data.Nodes.Select(n => n.Alias).ToList();
        var handlesIn = data.Items.Select(i => i.Handle).ToList();
        return new ReconcileAliasUniverse
        {
            Evidence = new AliasTiers(evidenceIn, [.. world.RunCatalog.Data.Evidence.Select(e => e.Alias).Except(evidenceIn)], ["E9", "E42"]),
            Nodes = new AliasTiers(nodesIn, [.. new[] { "N3", "N4" }.Except(nodesIn)], ["N99"]),
            Handles = new AliasTiers(handlesIn, [.. new[] { "i1", "i2", "i3", "i4" }.Except(handlesIn)], ["i17"]),
            Relations = new AliasTiers([.. data.Relations.Select(r => r.Alias)], [], ["R9"]),
            ItemKeys = new AliasTiers(["i1", "i2", "i3", "i4", "i5", "i6"], [], []),
            FindingKeys = new AliasTiers(["f1", "f2", "f3", "f4"], [], []),
            PatchKeys = new AliasTiers(["p1", "p2", "p3", "p4"], [], []),
        };
    }

    /// <summary>Fasce per la proprietà di <paramref name="path"/> con il <paramref name="pattern"/> indicato. Un pattern non previsto è un errore del test, mai un ripiego.</summary>
    public AliasTiers TiersFor(string path, string pattern) => pattern switch
    {
        AnalysisAliasPattern.Evidence => Evidence,
        ReconcileAliasContracts.NodeAliasPattern => Nodes,
        "^[EN][1-9][0-9]*$" => AliasTiers.Merge(Evidence, Nodes),
        AnalysisAliasPattern.ItemKey => path.EndsWith(".target.item", StringComparison.Ordinal) ? Handles : ItemKeys,
        AnalysisAliasPattern.Relation => Relations,
        AnalysisAliasPattern.FindingKey => FindingKeys,
        AnalysisAliasPattern.PatchKey => PatchKeys,
        _ => throw new InvalidOperationException($"{path}: pattern «{pattern}» senza fascia di alias nell'universo del generatore."),
    };
}

/// <summary>
/// Generatore DETERMINISTICO di istanze JSON di uno schema (RH-003), limitato alle keyword di <see cref="ReconcileSchemaSubset.Keywords"/>: <c>type</c> (anche array con <c>null</c>), <c>enum</c>, <c>pattern</c>
/// (alias dall'universo del batch), <c>minLength</c>, <c>minItems</c>/<c>maxItems</c>, <c>anyOf</c>, <c>properties</c>/<c>required</c>/<c>additionalProperties</c>. Una keyword fuori dall'insieme è un errore.
/// Ogni istanza è valida per costruzione (il test lo verifica comunque con <see cref="ReconcileSchemaSubset"/>).
/// Il generatore di numeri è un SplitMix64 scritto qui (nessun <see cref="Random"/>, nessun <see cref="Guid.NewGuid"/>, nessun orologio): la sequenza non dipende dalla versione di .NET.
/// </summary>
internal sealed class ReconcileSchemaInstanceGenerator(ulong seed, ReconcileAliasUniverse universe)
{
    public const ulong Seed = 20261113UL;

    private static readonly string[] Sentences =
    [
        "Il limite giornaliero è di 5 ordini.", "Il cliente ordina tramite il portale web.", "Timeout di riprocessamento portato da 2 a 4 ore.",
    ];

    private static readonly string LongText = new('x', 600);

    private ulong _state = seed;

    public int Next(int maxExclusive)
    {
        _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
        var z = _state;
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return (int)((z ^ (z >> 31)) % (ulong)maxExclusive);
    }

    /// <summary>Prossima istanza dello schema, serializzata.</summary>
    public string Generate(JsonElement schema) => Gen(schema, "$")!.ToJsonString();

    private JsonNode? Gen(JsonElement s, string path)
    {
        foreach (var p in s.EnumerateObject().Where(p => !ReconcileSchemaSubset.Keywords.Contains(p.Name)))
        {
            throw new InvalidOperationException($"{path}: keyword «{p.Name}» fuori dall'insieme generato dal test.");
        }

        if (s.TryGetProperty("anyOf", out var anyOf))
        {
            var branches = anyOf.EnumerateArray().ToList();
            return Gen(branches[Next(branches.Count)], path);
        }

        if (s.TryGetProperty("enum", out var values))
        {
            var options = values.EnumerateArray().ToList();
            return JsonNode.Parse(options[Next(options.Count)].GetRawText());
        }

        var types = !s.TryGetProperty("type", out var type) ? throw new InvalidOperationException($"{path}: schema senza type.")
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToList() : [type.GetString()!];
        var chosen = types.Count > 1 && types.Contains("null") && Next(100) < 38 ? "null" : types.Where(t => t != "null").DefaultIfEmpty("null").ElementAt(Next(Math.Max(1, types.Count(t => t != "null"))));
        return chosen switch
        {
            "null" => null,
            "string" => GenString(s, path),
            "array" => GenArray(s, path),
            "object" => GenObject(s, path),
            _ => throw new InvalidOperationException($"{path}: tipo «{chosen}» non generato dal test."),
        };
    }

    private JsonNode GenString(JsonElement s, string path)
    {
        if (s.TryGetProperty("pattern", out var pattern))
        {
            var tiers = universe.TiersFor(path, pattern.GetString()!);
            var alias = Pick(tiers);
            return Regex.IsMatch(alias, pattern.GetString()!) ? alias : throw new InvalidOperationException($"{path}: l'alias «{alias}» non rispetta {pattern.GetString()}.");
        }

        var minLength = s.TryGetProperty("minLength", out var m) ? m.GetInt32() : 0;
        string text;
        do
        {
            var roll = Next(100);
            text = roll < 68 ? Sentences[Next(Sentences.Length)] : roll < 80 ? LongText : roll < 90 ? string.Empty : " ";
        }
        while (text.EnumerateRunes().Count() < minLength);
        return text;
    }

    private string Pick(AliasTiers tiers)
    {
        var weighted = new List<(IReadOnlyList<string> Tier, int Weight)> { (tiers.In, 78), (tiers.Out, 14), (tiers.Unknown, 8) }.Where(t => t.Tier.Count > 0).ToList();
        var roll = Next(weighted.Sum(t => t.Weight));
        foreach (var (tier, weight) in weighted)
        {
            if (roll < weight)
            {
                return tier[Next(tier.Count)];
            }

            roll -= weight;
        }

        throw new InvalidOperationException("Universo di alias vuoto.");
    }

    private JsonArray GenArray(JsonElement s, string path)
    {
        var min = s.TryGetProperty("minItems", out var lo) ? lo.GetInt32() : 0;
        var max = Math.Min(3, s.TryGetProperty("maxItems", out var hi) ? hi.GetInt32() : 3);
        var count = Math.Clamp(Next(4), min, max);
        var items = s.GetProperty("items");
        var elements = new List<JsonNode?>();
        for (var i = 0; i < count; i++)
        {
            var roll = Next(100);
            if (i > 0 && roll < 12)
            {
                elements.Add(elements[Next(i)]?.DeepClone());   // gemello esatto: stessa chiave, stessa identità
            }
            else if (i > 0 && roll < 30 && elements[Next(i)] is JsonObject twin && twin["key"] is not null && items.TryGetProperty("properties", out var props) && props.TryGetProperty("key", out var keySchema))
            {
                var copy = (JsonObject)twin.DeepClone();
                copy["key"] = Gen(keySchema, path + "[].key");   // gemello con un'altra chiave: stessa identità canonica, chiave diversa
                elements.Add(copy);
            }
            else
            {
                elements.Add(Gen(items, path + "[]"));
            }
        }

        return new JsonArray([.. elements]);
    }

    private JsonObject GenObject(JsonElement s, string path)
    {
        var o = new JsonObject();
        var required = s.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        if (s.TryGetProperty("properties", out var properties))
        {
            foreach (var p in properties.EnumerateObject())
            {
                if (required.Contains(p.Name) || Next(2) == 0)
                {
                    o[p.Name] = Gen(p.Value, $"{path}.{p.Name}");
                }
            }
        }

        return o;
    }
}
