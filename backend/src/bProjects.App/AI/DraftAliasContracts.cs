using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;

namespace bProjects.App.AI;

// DTO di output AI `document-draft-alias-1` e composizione server-side (contracts/ai-workflow.md §7.11, T127): il modello restituisce soli alias
// (`I<n>` per item e alternative, `F<n>` per le fonti), mai UUID o hash. Il compositore li risolve contro il catalogo della revisione confermata,
// ricostruisce il canonico `{sectionKey,blocks:[{itemIds,markdown,sourceIds}]}` e lo passa al DocumentDraftValidator invariato. Nessun database, nessun LLM.

public sealed record DraftAliasBlock(IReadOnlyList<string> Items, string Markdown, IReadOnlyList<string> Sources);

public sealed record DraftAliasOutput(string SectionKey, IReadOnlyList<DraftAliasBlock> Blocks);

public static class DraftAliasContracts
{
    public const string SchemaVersion = "document-draft-alias-1";
    public const string ItemPattern = "^I[1-9][0-9]*$";
    public const string SourcePattern = "^F[1-9][0-9]*$";

    private static readonly string[] OutputProperties = ["sectionKey", "blocks"];
    private static readonly string[] BlockProperties = ["items", "markdown", "sources"];

    /// <summary>Codec strict: oggetto chiuso, nessun duplicato, null o proprietà extra, alias nel formato esatto (maiuscola fissa, intero positivo senza zeri iniziali). Altro è <see cref="JsonException"/>.</summary>
    public static DraftAliasOutput Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("Output vuoto.");
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        RequireClosedObject(root, OutputProperties, "output");
        var sectionKey = root.GetProperty("sectionKey");
        var blocksElement = root.GetProperty("blocks");
        if (sectionKey.ValueKind != JsonValueKind.String || blocksElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("sectionKey deve essere una stringa e blocks un array.");
        }

        var blocks = new List<DraftAliasBlock>();
        var index = 0;
        foreach (var element in blocksElement.EnumerateArray())
        {
            var where = $"blocks[{index}]";
            RequireClosedObject(element, BlockProperties, where);
            var markdown = element.GetProperty("markdown");
            if (markdown.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"{where}.markdown deve essere una stringa.");
            }

            blocks.Add(new DraftAliasBlock(Aliases(element.GetProperty("items"), 'I', $"{where}.items"), markdown.GetString()!, Aliases(element.GetProperty("sources"), 'F', $"{where}.sources")));
            index++;
        }

        return new DraftAliasOutput(sectionKey.GetString()!, blocks);
    }

    private static void RequireClosedObject(JsonElement element, string[] expected, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{where} deve essere un oggetto.");
        }

        var names = element.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != expected.Length || expected.Any(e => !names.Contains(e, StringComparer.Ordinal)))
        {
            throw new JsonException($"{where} deve avere esattamente le proprietà {string.Join(", ", expected)}.");
        }
    }

    private static List<string> Aliases(JsonElement element, char prefix, string where)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{where} deve essere un array di alias {prefix}<n>.");
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            list.Add(AnalysisAliasPattern.Matches(text, prefix) ? text! : throw new JsonException($"{where}: formato non canonico, atteso {prefix}<n> con n intero positivo senza zeri iniziali."));
        }

        return list;
    }

    private static string Encode(DraftAliasOutput output) => new JsonObject
    {
        ["sectionKey"] = output.SectionKey,
        ["blocks"] = new JsonArray([.. output.Blocks.Select(b => (JsonNode)new JsonObject
        {
            ["items"] = new JsonArray([.. b.Items.Select(i => (JsonNode)JsonValue.Create(i)!)]),
            ["markdown"] = b.Markdown,
            ["sources"] = new JsonArray([.. b.Sources.Select(i => (JsonNode)JsonValue.Create(i)!)]),
        })]),
    }.ToJsonString();

    /// <summary>Forma canonica RFC 8785 del DTO.</summary>
    public static string Canonical(DraftAliasOutput output) => CanonicalJson.Canonicalize(Encode(output));

    /// <summary>`aiOutputHash`: SHA-256 della forma canonica del DTO.</summary>
    public static string Hash(DraftAliasOutput output) => CanonicalJson.HashText(Canonical(output));

    private static readonly Lazy<JsonElement> Schema = new(BuildSchema);

    /// <summary>JSON Schema vincolante da inviare al modello: oggetti chiusi, alias con pattern esatto, nessun UUID. La risoluzione degli alias è del compositore.</summary>
    public static JsonElement JsonSchema() => Schema.Value;

    private static JsonElement BuildSchema()
    {
        var item = new JsonObject { ["type"] = "string", ["pattern"] = ItemPattern };
        var source = new JsonObject { ["type"] = "string", ["pattern"] = SourcePattern };
        var block = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("items", "markdown", "sources"),
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = item },
                ["markdown"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["sources"] = new JsonObject { ["type"] = "array", ["items"] = source },
            },
        };
        var schema = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("sectionKey", "blocks"),
            ["properties"] = new JsonObject
            {
                ["sectionKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64 },
                ["blocks"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = block },
            },
        };
        return JsonDocument.Parse(schema.ToJsonString()).RootElement.Clone();
    }
}

/// <summary>Esito della composizione: accettata (con la validazione canonica e i tre hash) oppure rifiutata con codice e violazioni; mai un output parziale.</summary>
public sealed record DraftComposition(
    bool Accepted, string? ErrorCode, IReadOnlyList<string> Violations, DraftValidation? Validation, string? AiOutputHash, string CatalogHash, string? CanonicalHash);

public static partial class DraftAliasComposer
{
    public const string AlgorithmVersion = "draft-single-v2";
    public const string ValidationVersion = "document-draft-validation-v2";

    // Marcatore atomico: stessa forma del validatore (`[fonte:<id>]`, nessuno spazio). Il confronto con il catalogo è case-sensitive.
    [GeneratedRegex(@"\[fonte:([^\]\s]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrictMarker();

    [GeneratedRegex(@"\[\s*fonte\s*:[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LooseMarker();

    /// <summary>
    /// Risolve gli alias contro il catalogo, ricostruisce il canonico e lo valida con <see cref="DocumentDraftValidator.ValidateBlocks"/> (regole invariate).
    /// Alias sconosciuti, duplicati o non citabili e marcatori non alias sono violazioni <c>alias:</c>; il server non sposta né assegna mai item ai blocchi.
    /// Con <paramref name="expectedCanonicalHash"/> (ricomposizione da WorkOutput) un canonico diverso è <c>integrity_conflict</c>.
    /// </summary>
    public static DraftComposition Compose(string? responseText, DraftAliasCatalog catalog, DocumentDraftPackage package, string? expectedCanonicalHash = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(package);
        DraftAliasOutput dto;
        try
        {
            dto = DraftAliasContracts.Decode(responseText);
        }
        catch (JsonException e)
        {
            return Reject(catalog, null, DocumentWorkException.WorkOutputInvalid, [$"struttura: l'output non è l'oggetto JSON {DraftAliasContracts.SchemaVersion} ({FirstLine(e.Message)})."]);
        }

        var aiHash = DraftAliasContracts.Hash(dto);
        var violations = new List<string>();
        var blocks = new JsonArray();
        for (var i = 0; i < dto.Blocks.Count; i++)
        {
            var block = dto.Blocks[i];
            var items = Resolve(block.Items, $"blocks[{i}].items", alias => catalog.Item(alias)?.ItemId, violations);
            var sources = Resolve(block.Sources, $"blocks[{i}].sources", alias => catalog.Source(alias)?.SourceId, violations);
            var markdown = Rewrite(block.Markdown, $"blocks[{i}].markdown", catalog, violations);
            blocks.Add(new JsonObject
            {
                ["itemIds"] = new JsonArray([.. items.Select(g => (JsonNode)JsonValue.Create(g.ToString("D"))!)]),
                ["markdown"] = markdown,
                ["sourceIds"] = new JsonArray([.. sources.Select(g => (JsonNode)JsonValue.Create(g.ToString("D"))!)]),
            });
        }

        if (violations.Count > 0)
        {
            return Reject(catalog, aiHash, DocumentWorkException.WorkOutputInvalid, violations);
        }

        var canonical = new JsonObject { ["sectionKey"] = dto.SectionKey, ["blocks"] = blocks }.ToJsonString();
        var validation = DocumentDraftValidator.ValidateBlocks(canonical, package);
        if (!validation.Accepted)
        {
            return Reject(catalog, aiHash, DocumentWorkException.WorkOutputInvalid, validation.Violations);
        }

        var canonicalHash = CanonicalJson.Sha256Hex(canonical);
        if (expectedCanonicalHash is not null && expectedCanonicalHash != canonicalHash)
        {
            return Reject(catalog, aiHash, DocumentWorkException.IntegrityConflict, ["canonicalHash: il canonico ricomposto ha un hash diverso da quello committato."]);
        }

        return new DraftComposition(true, null, [], validation, aiHash, catalog.CatalogHash, canonicalHash);
    }

    private static DraftComposition Reject(DraftAliasCatalog catalog, string? aiHash, string code, IEnumerable<string> violations) =>
        new(false, code, [.. violations.Distinct(StringComparer.Ordinal)], null, aiHash, catalog.CatalogHash, null);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static List<Guid> Resolve(IReadOnlyList<string> aliases, string where, Func<string, Guid?> lookup, List<string> violations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<Guid>();
        foreach (var alias in aliases)
        {
            if (!seen.Add(alias))
            {
                violations.Add($"alias: {where} ripete l'alias {alias}.");
            }
            else if (lookup(alias) is { } id)
            {
                ids.Add(id);
            }
            else
            {
                violations.Add($"alias: {where} contiene l'alias {alias}, sconosciuto o non citabile.");
            }
        }

        return ids;
    }

    /// <summary>Riscrive ogni marcatore <c>[fonte:F&lt;n&gt;]</c> in <c>[fonte:&lt;UUID&gt;]</c>; un marcatore che non è un alias noto o non è atomico è una violazione.</summary>
    private static string Rewrite(string markdown, string where, DraftAliasCatalog catalog, List<string> violations)
    {
        foreach (Match loose in LooseMarker().Matches(markdown))
        {
            var strict = StrictMarker().Match(loose.Value);
            if (!strict.Success || strict.Length != loose.Length)
            {
                violations.Add($"alias: {where} contiene il marcatore {loose.Value} non atomico (un solo alias, senza spazi).");
            }
        }

        return StrictMarker().Replace(markdown, m =>
        {
            if (catalog.Source(m.Groups[1].Value) is { } source)
            {
                return $"[fonte:{source.SourceId:D}]";
            }

            violations.Add($"alias: {where} contiene il marcatore {m.Value}, che non è un alias di fonte noto e citabile.");
            return m.Value;
        });
    }
}
