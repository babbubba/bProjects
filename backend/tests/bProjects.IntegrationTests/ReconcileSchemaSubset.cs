using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace bProjects.IntegrationTests;

/// <summary>
/// Valutatore di JSON Schema LIMITATO all'insieme chiuso di keyword che il prodotto invia alla grammatica del provider (003, T040 dopo il Run 3): <c>type</c> (anche array e <c>null</c>), <c>enum</c>,
/// <c>pattern</c>, <c>minLength</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c>, <c>items</c>, <c>minItems</c>, <c>maxItems</c>, <c>anyOf</c>. Una keyword fuori dall'insieme
/// è un errore del test, mai ignorata: è la stessa disciplina richiesta al convertitore di llama.cpp, che invece ignorerebbe in silenzio <c>uniqueItems</c>, <c>if</c>, <c>not</c>.
/// Sostituisce il controllo minimale su tre vincoli che non aveva visto la discrepanza del <c>descriptor</c>.
/// </summary>
internal static class ReconcileSchemaSubset
{
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "enum", "pattern", "minLength", "required", "properties", "additionalProperties", "items", "minItems", "maxItems", "anyOf",
    };

    /// <summary>Violazioni del valore rispetto allo schema, con il percorso (<c>$.descriptor</c>, <c>$.candidates[0].left</c>…). Vuoto = ammesso dallo schema.</summary>
    public static List<string> Violations(JsonElement schema, string json)
    {
        var errors = new List<string>();
        Check(schema, JsonNode.Parse(json), "$", errors);
        return errors;
    }

    /// <summary>
    /// Problemi di compatibilità con il convertitore schema→grammatica di llama.cpp b10703 (sorgente <c>common/json-schema-to-grammar.cpp</c>): keyword fuori dall'insieme, pattern non ancorati o con
    /// classi <c>\d \w \s \S \b</c> (accettati come «qualunque stringa»), <c>minLength</c> senza <c>type: "string"</c> o insieme a <c>pattern</c> (ignorato), limiti di array senza <c>items</c> (ignorati).
    /// </summary>
    public static List<string> ProviderProblems(JsonElement schema)
    {
        var problems = new List<string>();
        Walk(schema, "$", problems);
        return problems;
    }

    private static void Walk(JsonElement s, string path, List<string> problems)
    {
        foreach (var p in s.EnumerateObject())
        {
            if (!Keywords.Contains(p.Name))
            {
                problems.Add($"{path}: keyword non ammessa «{p.Name}»");
            }
        }

        if (s.TryGetProperty("pattern", out var pattern))
        {
            var text = pattern.GetString()!;
            if (!text.StartsWith('^') || !text.EndsWith('$'))
            {
                problems.Add($"{path}: pattern non ancorato {text}");
            }

            if (Regex.IsMatch(text, @"\\[dwsbDWSB]"))
            {
                problems.Add($"{path}: classe di caratteri non supportata in {text}");
            }
        }

        if (s.TryGetProperty("minLength", out _)
            && (!s.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "string" || s.TryGetProperty("pattern", out _)))
        {
            problems.Add($"{path}: minLength richiede type \"string\" esplicito e nessun pattern");
        }

        if ((s.TryGetProperty("maxItems", out _) || s.TryGetProperty("minItems", out _)) && !s.TryGetProperty("items", out _))
        {
            problems.Add($"{path}: limiti dell'array senza items");
        }

        if (s.TryGetProperty("properties", out var properties))
        {
            foreach (var p in properties.EnumerateObject())
            {
                Walk(p.Value, $"{path}.{p.Name}", problems);
            }
        }

        if (s.TryGetProperty("items", out var items))
        {
            Walk(items, $"{path}[]", problems);
        }

        if (s.TryGetProperty("anyOf", out var anyOf))
        {
            var i = 0;
            foreach (var branch in anyOf.EnumerateArray())
            {
                Walk(branch, $"{path}|{i++}", problems);
            }
        }
    }

    private static string TypeOf(JsonNode? v) => v switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value when value.GetValueKind() == JsonValueKind.String => "string",
        JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "number",
    };

    private static void Check(JsonElement s, JsonNode? v, string path, List<string> errors)
    {
        foreach (var p in s.EnumerateObject().Where(p => !Keywords.Contains(p.Name)))
        {
            throw new InvalidOperationException($"{path}: keyword «{p.Name}» fuori dall'insieme valutato dal test.");
        }

        if (s.TryGetProperty("anyOf", out var anyOf))
        {
            if (!anyOf.EnumerateArray().Any(branch => { var e = new List<string>(); Check(branch, v, path, e); return e.Count == 0; }))
            {
                errors.Add($"{path}: nessuna alternativa di anyOf ammette il valore");
            }

            return;
        }

        var actual = TypeOf(v);
        if (s.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToList() : [type.GetString()!];
            if (!allowed.Contains(actual))
            {
                errors.Add($"{path}: tipo {actual} non ammesso (ammessi: {string.Join(", ", allowed)})");
                return;
            }
        }

        if (s.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(e => JsonNode.DeepEquals(JsonNode.Parse(e.GetRawText()), v)))
        {
            errors.Add($"{path}: valore fuori dall'enum");
        }

        switch (v)
        {
            case JsonValue when actual == "string":
                var text = v.GetValue<string>();
                if (s.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!))
                {
                    errors.Add($"{path}: «{text}» non rispetta {pattern.GetString()}");
                }

                if (s.TryGetProperty("minLength", out var minLength) && text.EnumerateRunes().Count() < minLength.GetInt32())
                {
                    errors.Add($"{path}: lunghezza inferiore a {minLength.GetInt32()}");
                }

                break;
            case JsonArray array:
                if (s.TryGetProperty("maxItems", out var max) && array.Count > max.GetInt32())
                {
                    errors.Add($"{path}: {array.Count} elementi > maxItems {max.GetInt32()}");
                }

                if (s.TryGetProperty("minItems", out var min) && array.Count < min.GetInt32())
                {
                    errors.Add($"{path}: {array.Count} elementi < minItems {min.GetInt32()}");
                }

                if (s.TryGetProperty("items", out var items))
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        Check(items, array[i], $"{path}[{i}]", errors);
                    }
                }

                break;
            case JsonObject obj:
                if (s.TryGetProperty("required", out var required))
                {
                    foreach (var name in required.EnumerateArray().Select(r => r.GetString()!).Where(name => !obj.ContainsKey(name)))
                    {
                        errors.Add($"{path}.{name}: proprietà obbligatoria mancante");
                    }
                }

                var properties = s.TryGetProperty("properties", out var props) ? props : default;
                foreach (var (name, value) in obj)
                {
                    if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var schema))
                    {
                        Check(schema, value, $"{path}.{name}", errors);
                    }
                    else if (s.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                    {
                        errors.Add($"{path}.{name}: proprietà non ammessa");
                    }
                }

                break;
        }
    }
}
