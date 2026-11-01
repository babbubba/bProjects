using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Documents;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T027 (DET): validatore del manifest document-parse-1 e adapter HTTP del parser, provati con manifest costruiti a mano e con un HTTP finto:
/// nessun parser, nessun DGX, nessun database. Il backend non si fida del 200: hash, limiti, riferimenti, gerarchia, range (con conversione
/// codepoint→UTF-16 verificata), celle e inventario sono controllati qui; un manifest incompleto diventa Incomplete, uno ostile è respinto.
/// </summary>
public sealed class ParsedManifestTests
{
    private const string Config = "cfg-hash-1";
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("# Titolo\n\nTesto");

    // ---- costruzione del manifest -----------------------------------------------------------------------------------------------

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Sottostringa per codepoint, come la calcola il parser Python (mai per unità UTF-16).</summary>
    private static string Slice(string text, int start, int end) =>
        string.Concat(text.EnumerateRunes().Skip(start).Take(end - start).Select(r => r.ToString()));

    private static JsonObject Block(string reference, int ordinal, string type, string text, string? parent = null, int? line = null, int? page = null) => new()
    {
        ["ref"] = reference,
        ["ordinal"] = ordinal,
        ["parentRef"] = parent,
        ["type"] = type,
        ["text"] = text,
        ["headingPath"] = new JsonArray(),
        ["locator"] = new JsonObject { ["page"] = page, ["lineStart"] = line, ["lineEnd"] = line },
    };

    private static JsonObject Range(string key, string blockRef, string blockText, int start, int end, string ownership = "Primary") => new()
    {
        ["rangeKey"] = key,
        ["blockRef"] = blockRef,
        ["start"] = start,
        ["end"] = end,
        ["ownership"] = ownership,
        ["textHash"] = Sha(Slice(blockText, start, end)),
    };

    private const string EmojiText = "Testo con \U0001F600 emoji";   // 17 codepoint, 18 unità UTF-16

    /// <summary>Manifest Markdown valido e completo: titolo + paragrafo con un carattere non-BMP, un chunk, un item di inventario.</summary>
    private static JsonObject Valid(Action<JsonObject>? mutate = null, string format = "md")
    {
        var b0 = Block("b0", 0, "heading", "Titolo", line: 1);
        var b1 = Block("b1", 1, "paragraph", EmojiText, parent: "b0", line: 3);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "document-parse-1",
            ["originalSha256"] = Sha256Hex(Original),
            ["parserVersion"] = "1.0.0",
            ["dependencyLockHash"] = "lock-hash",
            ["modelArtifactsHash"] = "model-hash",
            ["configHash"] = Config,
            ["status"] = "Complete",
            ["originFormat"] = format,
            ["originalByteLength"] = Original.Length,
            ["pageCount"] = null,
            ["offsetUnit"] = "unicode-codepoint",
            ["document"] = new JsonObject { ["serializationVersion"] = "docling-1", ["content"] = new JsonObject() },
            ["blocks"] = new JsonArray(b0, b1),
            ["inventory"] = new JsonArray(new JsonObject
            {
                ["kind"] = "item",
                ["status"] = "represented",
                ["blockRefs"] = new JsonArray("b0", "b1"),
                ["issueRefs"] = new JsonArray(),
            }),
            ["chunks"] = new JsonArray(new JsonObject
            {
                ["ranges"] = new JsonArray(
                    Range("r0", "b0", "Titolo", 0, 6),
                    Range("r1", "b1", EmojiText, 0, 17)),
            }),
            ["issues"] = new JsonArray(),
        };
        mutate?.Invoke(manifest);
        return manifest;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ManifestExpectations Expect(DocumentFormat format = DocumentFormat.Markdown, int maxPages = 1_000, int maxBlocks = 100_000, long maxExpanded = 250_000_000) =>
        new(Sha256Hex(Original), Original.Length, format, Config, maxPages, maxBlocks, maxExpanded);

    private static ValidatedManifest Accept(JsonObject manifest, ManifestExpectations? expected = null) =>
        ParsedManifestValidator.Validate(Encoding.UTF8.GetBytes(manifest.ToJsonString()), expected ?? Expect());

    private static ManifestRejectedException Reject(JsonObject manifest, ManifestExpectations? expected = null) =>
        Assert.Throws<ManifestRejectedException>(() => Accept(manifest, expected));

    private static JsonArray Blocks(JsonObject m) => m["blocks"]!.AsArray();

    private static JsonObject BlockAt(JsonObject m, int i) => Blocks(m)[i]!.AsObject();

    private static JsonObject Chunk(JsonObject m, int i = 0) => m["chunks"]!.AsArray()[i]!.AsObject();

    // ---- accettazione ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_complete_manifest_is_accepted_with_exact_blocks_and_hashes()
    {
        var result = Accept(Valid());

        Assert.Equal(ManifestStatus.Complete, result.Status);
        Assert.Equal(["b0", "b1"], result.Blocks.Select(b => b.Ref));
        Assert.Equal("b0", result.Blocks[1].ParentRef);
        Assert.Equal(Sha(EmojiText), result.Blocks[1].TextHash);   // hash calcolato dal backend sul testo canonico
        Assert.Equal(1, result.Blocks[0].LineStart);
        Assert.Null(result.Blocks[0].Page);
        Assert.Equal("lock-hash", result.DependencyLockHash);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Non_BMP_text_is_converted_from_codepoints_to_utf16_exactly()
    {
        // «Testo con 😀 emoji»: codepoint [10,11) = la sola emoji (2 unità UTF-16, [10,12)); [11,17) = « emoji» → UTF-16 [12,18).
        var result = Accept(Valid(m => Chunk(m)["ranges"] = new JsonArray(
            Range("r0", "b0", "Titolo", 0, 6),
            Range("r1", "b1", EmojiText, 0, 10),
            Range("r2", "b1", EmojiText, 10, 11),
            Range("r3", "b1", EmojiText, 11, 17))));

        var ranges = result.Chunks[0].Ranges.Where(r => r.BlockRef == "b1").ToList();
        Assert.Equal([(0, 10), (10, 12), (12, 18)], ranges.Select(r => (r.Start, r.End)));
        var text = result.Blocks[1].Text;
        Assert.Equal("\U0001F600", text[ranges[1].Start..ranges[1].End]);   // la citazione in UTF-16 riproduce esattamente il testo
        Assert.Equal(" emoji", text[ranges[2].Start..ranges[2].End]);
    }

    [Fact]
    public void A_passage_spanning_several_blocks_keeps_ordered_ranges_with_one_origin_each()
    {
        var m = Valid(m => Chunk(m)["ranges"] = new JsonArray(
            Range("r0", "b0", "Titolo", 0, 6),
            Range("r1", "b1", EmojiText, 0, 17),
            Range("rc", "b0", "Titolo", 0, 6, "Context")));   // header ripetuto: stesso blocco e stesso range dell'originale

        var result = Accept(m);

        Assert.Equal(["r0", "r1", "rc"], result.Chunks[0].Ranges.Select(r => r.RangeKey));
        Assert.Equal(RangeOwnership.Context, result.Chunks[0].Ranges[2].Ownership);
        Assert.Equal(ManifestStatus.Complete, result.Status);
    }

    [Fact]
    public void A_context_range_may_repeat_in_other_chunks_but_primary_ownership_is_exclusive()
    {
        var repeated = Valid(m => m["chunks"]!.AsArray().Add(new JsonObject
        {
            ["ranges"] = new JsonArray(Range("r9", "b0", "Titolo", 0, 6, "Context")),
        }));
        Assert.Equal(2, Accept(repeated).Chunks.Count);

        var duplicatedOwner = Valid(m => m["chunks"]!.AsArray().Add(new JsonObject
        {
            ["ranges"] = new JsonArray(Range("r9", "b0", "Titolo", 3, 6)),   // secondo proprietario di una parte già Primary
        }));
        Assert.Equal("manifest_inconsistent", Reject(duplicatedOwner).Code);
    }

    [Fact]
    public void Unassigned_text_makes_the_manifest_incomplete_instead_of_silently_ready()
    {
        var m = Valid(m => Chunk(m)["ranges"] = new JsonArray(Range("r0", "b0", "Titolo", 0, 6), Range("r1", "b1", EmojiText, 0, 10)));

        var result = Accept(m);

        Assert.Equal(ManifestStatus.Incomplete, result.Status);
        var issue = Assert.Single(result.Issues, i => i.Code == "range_uncovered");
        Assert.True(issue.AffectsCompleteness);
        Assert.Equal("b1", issue.BlockRef);
    }

    [Fact]
    public void An_incomplete_manifest_with_an_unprocessed_item_is_accepted_as_incomplete_and_keeps_its_issues()
    {
        var m = Valid(m =>
        {
            m["status"] = "Incomplete";
            m["issues"] = new JsonArray(new JsonObject
            {
                ["id"] = "i1", ["code"] = "image_unprocessed", ["severity"] = "blocking", ["page"] = null, ["ref"] = null,
                ["reason"] = "Immagine potenzialmente informativa non trattata", ["affectsCompleteness"] = true,
            });
            m["inventory"]!.AsArray().Add(new JsonObject
            {
                ["kind"] = "item", ["status"] = "unprocessed", ["blockRefs"] = new JsonArray(), ["issueRefs"] = new JsonArray("i1"),
            });
        });

        var result = Accept(m);

        Assert.Equal(ManifestStatus.Incomplete, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        Assert.True(issue.AffectsCompleteness);
    }

    [Fact]
    public void A_manifest_claiming_complete_with_an_unprocessed_item_is_rejected_as_hostile()
    {
        var m = Valid(m => m["inventory"]!.AsArray().Add(new JsonObject
        {
            ["kind"] = "item", ["status"] = "unprocessed", ["blockRefs"] = new JsonArray(), ["issueRefs"] = new JsonArray(),
        }));

        Assert.Equal("manifest_inconsistent", Reject(m).Code);
    }

    [Fact]
    public void A_complete_manifest_with_a_completeness_affecting_issue_is_rejected()
    {
        var m = Valid(m => m["issues"] = new JsonArray(new JsonObject
        {
            ["id"] = "i1", ["code"] = "x", ["severity"] = "warning", ["reason"] = "r", ["affectsCompleteness"] = true,
        }));

        Assert.Equal("manifest_inconsistent", Reject(m).Code);
    }

    [Fact]
    public void An_unexplained_incomplete_status_stays_incomplete_with_a_backend_issue()
    {
        var result = Accept(Valid(m => m["status"] = "Incomplete"));

        Assert.Equal(ManifestStatus.Incomplete, result.Status);
        Assert.Contains(result.Issues, i => i.Code == "incomplete_unexplained" && i.AffectsCompleteness);
    }

    // ---- hash, versioni, formato -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("originalSha256", "0000000000000000000000000000000000000000000000000000000000000000", "manifest_hash_mismatch")]
    [InlineData("originalByteLength", 3, "manifest_hash_mismatch")]
    [InlineData("schemaVersion", "document-parse-2", "manifest_version_unsupported")]
    [InlineData("configHash", "altro", "manifest_config_mismatch")]
    [InlineData("originFormat", "pdf", "manifest_format_mismatch")]
    [InlineData("offsetUnit", "utf16", "manifest_malformed")]
    [InlineData("status", "Ready", "manifest_malformed")]
    public void Identity_fields_that_do_not_match_the_request_are_rejected(string field, object value, string code)
    {
        var m = Valid(m => m[field] = JsonValue.Create(value));

        Assert.Equal(code, Reject(m).Code);
    }

    [Theory]
    [InlineData("parserVersion")]
    [InlineData("dependencyLockHash")]
    [InlineData("modelArtifactsHash")]
    [InlineData("document")]
    [InlineData("blocks")]
    [InlineData("inventory")]
    [InlineData("chunks")]
    [InlineData("issues")]
    public void Missing_required_sections_are_malformed(string field)
    {
        var m = Valid(m => m.Remove(field));

        Assert.Equal("manifest_malformed", Reject(m).Code);
    }

    [Fact]
    public void The_serialised_document_must_declare_its_serialisation_version()
    {
        Assert.Equal("manifest_malformed", Reject(Valid(m => m["document"] = new JsonObject { ["content"] = new JsonObject() })).Code);
        Assert.Equal("manifest_malformed", Reject(Valid(m => m["document"] = "testo")).Code);
    }

    [Fact]
    public void Truncated_duplicate_or_non_json_bodies_are_malformed_never_partially_accepted()
    {
        var json = Valid().ToJsonString();
        Assert.Equal("manifest_malformed", Assert.Throws<ManifestRejectedException>(() => ParsedManifestValidator.Validate(Encoding.UTF8.GetBytes(json[..^20]), Expect())).Code);
        Assert.Equal("manifest_malformed", Assert.Throws<ManifestRejectedException>(() => ParsedManifestValidator.Validate("non json"u8.ToArray(), Expect())).Code);
        Assert.Equal("manifest_malformed", Assert.Throws<ManifestRejectedException>(() => ParsedManifestValidator.Validate(Encoding.UTF8.GetBytes(json.Replace("\"status\":\"Complete\"", "\"status\":\"Complete\",\"status\":\"Complete\"", StringComparison.Ordinal)), Expect())).Code);
        Assert.Equal("manifest_malformed", Assert.Throws<ManifestRejectedException>(() => ParsedManifestValidator.Validate([], Expect())).Code);
    }

    [Fact]
    public void A_lone_surrogate_in_a_text_is_rejected_because_codepoint_offsets_would_be_ambiguous()
    {
        var json = Valid().ToJsonString().Replace("Titolo\",\"headingPath", "Tit\\ud800olo\",\"headingPath", StringComparison.Ordinal);
        Assert.Contains("\\ud800", json, StringComparison.Ordinal);

        Assert.Equal("manifest_malformed", Assert.Throws<ManifestRejectedException>(() => ParsedManifestValidator.Validate(Encoding.UTF8.GetBytes(json), Expect())).Code);
    }

    // ---- blocchi, gerarchia, limiti ----------------------------------------------------------------------------------------------

    [Fact]
    public void Duplicate_refs_and_non_increasing_ordinals_are_rejected()
    {
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 1)["ref"] = "b0")).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 1)["ordinal"] = 0)).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 0)["ordinal"] = 5)).Code);
    }

    [Fact]
    public void Parents_must_exist_and_form_no_cycle()
    {
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 1)["parentRef"] = "fantasma")).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 0)["parentRef"] = "b0")).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 0)["parentRef"] = "b1")).Code);   // b0 → b1 → b0
    }

    [Fact]
    public void Block_pages_and_spans_beyond_the_declared_limits_are_a_permanent_limit_error()
    {
        var many = Valid(m =>
        {
            var blocks = Blocks(m);
            for (var i = 2; i < 6; i++)
            {
                blocks.Add(Block("x" + i, i, "paragraph", "t", line: i + 3));
            }
        });
        Assert.Equal("parse_limit_exceeded", Reject(many, Expect(maxBlocks: 5)).Code);

        Assert.Equal("parse_limit_exceeded", Reject(Valid(), Expect(maxExpanded: 10)).Code);   // testo canonico oltre i byte espansi ammessi
    }

    [Fact]
    public void Line_mapping_is_required_for_markdown_and_text_and_pages_are_never_invented()
    {
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 0)["locator"]!["lineStart"] = null)).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => { BlockAt(m, 0)["locator"]!["lineStart"] = 4; BlockAt(m, 0)["locator"]!["lineEnd"] = 2; })).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 0)["locator"]!["page"] = 1)).Code);   // niente pagine simulate per Markdown
    }

    [Fact]
    public void A_pdf_requires_valid_pages_and_a_complete_page_inventory()
    {
        JsonObject Pdf(Action<JsonObject>? mutate = null) => Valid(m =>
        {
            m["originFormat"] = "pdf";
            m["pageCount"] = 2;
            foreach (var block in Blocks(m))
            {
                block!["locator"] = new JsonObject
                {
                    ["page"] = 1, ["bbox"] = new JsonArray(0.0, 0.0, 10.0, 10.0), ["coordinateSystem"] = "pdf-bottom-left", ["doclingRef"] = "#/texts/0",
                };
            }

            m["inventory"] = new JsonArray(
                new JsonObject { ["kind"] = "page", ["page"] = 1, ["status"] = "represented", ["blockRefs"] = new JsonArray("b0", "b1"), ["issueRefs"] = new JsonArray() },
                new JsonObject { ["kind"] = "page", ["page"] = 2, ["status"] = "empty", ["reason"] = "Pagina vuota", ["blockRefs"] = new JsonArray(), ["issueRefs"] = new JsonArray() });
            mutate?.Invoke(m);
        });
        var expected = Expect(DocumentFormat.Pdf);

        var ok = Accept(Pdf(), expected);
        Assert.Equal(ManifestStatus.Complete, ok.Status);
        Assert.Equal(1, ok.Blocks[0].Page);
        Assert.Equal([0.0, 0.0, 10.0, 10.0], ok.Blocks[0].Bbox!);
        Assert.Equal("pdf-bottom-left", ok.Blocks[0].CoordinateSystem);

        Assert.Equal("manifest_inconsistent", Reject(Pdf(m => BlockAt(m, 0)["locator"]!["page"] = 3), expected).Code);   // pagina oltre pageCount
        Assert.Equal("manifest_inconsistent", Reject(Pdf(m => BlockAt(m, 0)["locator"]!["coordinateSystem"] = null), expected).Code);
        Assert.Equal("manifest_inconsistent", Reject(Pdf(m => m["inventory"]!.AsArray().RemoveAt(1)), expected).Code);   // pagina 2 non inventariata
        Assert.Equal("manifest_inconsistent", Reject(Pdf(m => m["inventory"]!.AsArray()[1]!["reason"] = null), expected).Code);   // «vuota» senza motivazione
        Assert.Equal("parse_limit_exceeded", Reject(Pdf(), Expect(DocumentFormat.Pdf, maxPages: 1)).Code);
        Assert.Equal("manifest_inconsistent", Reject(Pdf(m => BlockAt(m, 1)["locator"]!["lineStart"] = 2), expected).Code);   // righe sorgente solo per MD/TXT
    }

    // ---- tabelle ----------------------------------------------------------------------------------------------------------------

    private static JsonObject Table(int rows, int columns, params (int Row, int Col, int RowSpan, int ColSpan, bool Header, string Text)[] cells) => new()
    {
        ["rows"] = rows,
        ["columns"] = columns,
        ["cells"] = new JsonArray(cells.Select(c => (JsonNode)new JsonObject
        {
            ["row"] = c.Row, ["col"] = c.Col, ["rowSpan"] = c.RowSpan, ["colSpan"] = c.ColSpan, ["header"] = c.Header, ["text"] = c.Text,
        }).ToArray()),
    };

    private static JsonObject WithTable(JsonObject table) => Valid(m => BlockAt(m, 1)["table"] = table.DeepClone()).Also(m => BlockAt(m, 1)["type"] = "table");

    [Fact]
    public void Table_cells_keep_row_column_span_and_header_and_must_stay_inside_the_grid()
    {
        var table = Table(2, 2, (0, 0, 1, 2, true, "Intestazione"), (1, 0, 1, 1, false, "a"), (1, 1, 1, 1, false, "b"));

        var result = Accept(WithTable(table));

        using var json = JsonDocument.Parse(result.Blocks[1].TableCellsJson!);
        Assert.Equal(2, json.RootElement.GetProperty("rows").GetInt32());
        var first = json.RootElement.GetProperty("cells")[0];
        Assert.Equal(2, first.GetProperty("colSpan").GetInt32());
        Assert.True(first.GetProperty("header").GetBoolean());

        Assert.Equal("manifest_inconsistent", Reject(WithTable(Table(2, 2, (1, 1, 2, 1, false, "x")))).Code);   // rowSpan oltre la griglia
        Assert.Equal("manifest_inconsistent", Reject(WithTable(Table(2, 2, (2, 0, 1, 1, false, "x")))).Code);   // riga fuori griglia
        Assert.Equal("manifest_inconsistent", Reject(WithTable(Table(2, 2, (0, 0, 0, 1, false, "x")))).Code);   // span nullo
        Assert.Equal("manifest_inconsistent", Reject(WithTable(Table(2, 2, (0, 0, 1, 1, false, "x"), (0, 0, 1, 1, false, "y")))).Code);   // celle sovrapposte
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 1)["table"] = table.DeepClone())).Code);   // celle su un blocco non tabella
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => BlockAt(m, 1)["type"] = "table")).Code);   // tabella senza celle
    }

    // ---- inventario ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Inventory_references_must_resolve_and_unprocessed_items_must_point_to_a_completeness_issue()
    {
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => m["inventory"]![0]!["blockRefs"] = new JsonArray("b0", "fantasma"))).Code);
        Assert.Equal("manifest_malformed", Reject(Valid(m => m["inventory"]![0]!["status"] = "stato-strano")).Code);

        var unprocessedWithoutIssue = Valid(m =>
        {
            m["status"] = "Incomplete";
            m["inventory"]!.AsArray().Add(new JsonObject { ["kind"] = "item", ["status"] = "unprocessed", ["blockRefs"] = new JsonArray(), ["issueRefs"] = new JsonArray() });
        });
        Assert.Equal("manifest_inconsistent", Reject(unprocessedWithoutIssue).Code);
    }

    [Fact]
    public void Issue_fields_are_bounded_and_enumerations_closed()
    {
        JsonObject Issue(Action<JsonObject>? change = null)
        {
            var issue = new JsonObject { ["id"] = "i1", ["code"] = "x", ["severity"] = "warning", ["reason"] = "r", ["affectsCompleteness"] = false };
            change?.Invoke(issue);
            return issue;
        }

        Assert.Single(Accept(Valid(m => m["issues"] = new JsonArray(Issue()))).Issues);
        Assert.Equal("manifest_malformed", Reject(Valid(m => m["issues"] = new JsonArray(Issue(i => i["severity"] = "critical")))).Code);
        Assert.Equal("manifest_malformed", Reject(Valid(m => m["issues"] = new JsonArray(Issue(i => i["reason"] = new string('x', 5_000))))).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => m["issues"] = new JsonArray(Issue(), Issue()))).Code);   // id duplicato
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => m["issues"] = new JsonArray(Issue(i => i["ref"] = "fantasma")))).Code);   // blocco inesistente
    }

    // ---- range -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Range_errors_are_rejected_before_anything_is_persisted()
    {
        void Ranges(JsonObject m, params JsonNode[] ranges) => Chunk(m)["ranges"] = new JsonArray(ranges);

        Assert.Equal("manifest_inconsistent", Reject(Valid(m => Ranges(m, Range("r0", "b0", "Titolo", 0, 6), Range("r1", "b1", EmojiText, 0, 18)))).Code);   // oltre il testo (17 codepoint)
        var empty = Range("r1", "b1", EmojiText, 4, 4);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => Ranges(m, empty))).Code);   // end deve superare start
        var negative = Range("r1", "b1", EmojiText, 0, 17);
        negative["start"] = -1;
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => Ranges(m, negative))).Code);
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => Ranges(m, Range("r1", "fantasma", EmojiText, 0, 5)))).Code);   // chunk → blocco inesistente
        Assert.Equal("manifest_inconsistent", Reject(Valid(m => Ranges(m, Range("r0", "b0", "Titolo", 0, 6), Range("r0", "b1", EmojiText, 0, 17)))).Code);   // rangeKey duplicata
        var tampered = Range("r1", "b1", EmojiText, 0, 17);
        tampered["textHash"] = Sha("altro testo");
        Assert.Equal("manifest_hash_mismatch", Reject(Valid(m => Ranges(m, Range("r0", "b0", "Titolo", 0, 6), tampered))).Code);
        var owner = Range("r1", "b1", EmojiText, 0, 17);
        owner["ownership"] = "Maybe";
        Assert.Equal("manifest_malformed", Reject(Valid(m => Ranges(m, owner))).Code);
    }

    [Fact]
    public void A_range_hash_computed_over_utf16_units_instead_of_codepoints_is_detected()
    {
        // Un parser che contasse unità UTF-16 farebbe slittare [0,17): l'hash del testo sorgente non coincide e il manifest è respinto.
        var wrong = Range("r1", "b1", EmojiText, 0, 17);
        wrong["textHash"] = Sha(EmojiText[..17]);

        Assert.Equal("manifest_hash_mismatch", Reject(Valid(m => Chunk(m)["ranges"] = new JsonArray(Range("r0", "b0", "Titolo", 0, 6), wrong))).Code);
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> action)
    {
        action(node);
        return node;
    }
}

/// <summary>
/// T027 (DET): adapter HTTP verso il parser con trasporto finto. Provato il contratto operativo: richiesta senza path né URL, errori
/// classificati transitori/permanenti, budget del tentativo (timeout subprocess + al più 5 s di cleanup, mai oltre la deadline residua),
/// output limitato e diagnostica mai derivata dal corpo dell'errore.
/// </summary>
public sealed class ParserClientTests
{
    private const string VersionJson = """
        {"schemaVersion":"document-parse-1","doclingVersion":"2.0","doclingCoreVersion":"2.0","configHash":"cfg-hash-1","modelArtifactsHash":"m","formats":["pdf","docx","md","txt"]}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? ConvertBody { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            LastUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Method == HttpMethod.Post && request.Content is not null)
            {
                ConvertBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static StubHandler Handler(Func<HttpResponseMessage> convert, string version = VersionJson) =>
        new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, version) : convert()));

    private static ParserClient Client(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(new ParserOptions { BaseUrl = "http://parser.test/", Secret = "segreto-di-test" }));

    private static ParserRequest Request(MemoryStream? content = null, int timeoutSeconds = 300, long maxOutput = 1_000_000) =>
        new(content ?? new MemoryStream("ciao"u8.ToArray()), DocumentFormat.Text, Guid.NewGuid(), new string('a', 64), 4,
            new ParserLimits(250_000_000, 1_000, 100_000, maxOutput, timeoutSeconds));

    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(600);

    private static async Task<ParserFailure> Failure(StubHandler handler, TimeSpan? remaining = null, ParserRequest? request = null)
    {
        var ex = await Assert.ThrowsAsync<ParserException>(() => Client(handler).ConvertAsync(request ?? Request(), remaining ?? Plenty, TestContext.Current.CancellationToken));
        return ex.Failure;
    }

    [Fact]
    public async Task A_conversion_sends_bytes_and_limits_with_no_path_or_url_and_returns_the_raw_manifest()
    {
        var handler = Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("{\"ok\":true}"u8.ToArray()) });
        var request = Request(timeoutSeconds: 120);

        var response = await Client(handler).ConvertAsync(request, Plenty, TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(response.Body));
        Assert.Equal(["/version", "/convert"], handler.Paths);
        Assert.Equal("Bearer segreto-di-test", handler.Authorization);
        var body = handler.ConvertBody!;
        foreach (var field in new[] { "declaredFormat", "documentVersionId", "originalSha256", "configHash", "maxExpandedBytes", "maxPages", "maxBlocks", "maxOutputBytes", "timeoutSeconds" })
        {
            Assert.Contains($"name={field}", body.Replace("\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.Contains("cfg-hash-1", body, StringComparison.Ordinal);   // il configHash è quello pubblicato da /version
        Assert.Contains("txt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", body, StringComparison.Ordinal);   // nessun URL sorgente
        Assert.Equal("parser.test", handler.LastUri!.Host);
    }

    [Fact]
    public async Task The_parse_timeout_is_the_minimum_of_the_configured_one_and_the_remaining_deadline()
    {
        var handler = Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("{}"u8.ToArray()) });

        await Client(handler).ConvertAsync(Request(timeoutSeconds: 300), TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);

        Assert.Matches("name=\"?timeoutSeconds\"?\\s+40\\b", handler.ConvertBody!.ReplaceLineEndings(" "));
    }

    [Theory]
    [InlineData(429, "{\"code\":\"parser_busy\"}", true, "parser_busy")]
    [InlineData(503, "{\"code\":\"parser_crashed\"}", true, "parser_unavailable")]
    [InlineData(409, "{\"code\":\"parser_config_mismatch\"}", false, "parser_config_mismatch")]
    [InlineData(415, "{\"code\":\"format_mismatch\"}", false, "parser_unsupported_format")]
    [InlineData(422, "{\"code\":\"parse_limit_exceeded\"}", false, "parse_limit_exceeded")]
    [InlineData(422, "{\"code\":\"parse_timeout\"}", true, "parse_timeout")]
    [InlineData(422, "{\"code\":\"qualcosa\"}", false, "parse_input_invalid")]
    [InlineData(401, "", false, "parser_unauthorized")]
    [InlineData(404, "", false, "parser_error")]
    public async Task Parser_errors_are_classified_transient_or_permanent_with_a_stable_code(int status, string body, bool transient, string code)
    {
        var failure = await Failure(Handler(() => Json((HttpStatusCode)status, body.Length == 0 ? "{}" : body)));

        Assert.Equal(transient, failure.IsTransient);
        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public async Task Retry_after_of_a_busy_parser_is_carried_to_the_caller_and_the_error_body_is_never_echoed()
    {
        var busy = Handler(() =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{\"code\":\"parser_busy\",\"message\":\"/srv/parser/tmp/segreto.pdf\"}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var failure = await Failure(busy);

        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
        Assert.DoesNotContain("segreto", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/srv", failure.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_parser_is_transient()
    {
        var down = new StubHandler((_, _) => throw new HttpRequestException("connessione rifiutata"));

        var failure = await Failure(down);

        Assert.True(failure.IsTransient);
        Assert.Equal("parser_unreachable", failure.Code);
    }

    [Fact]
    public async Task A_hanging_parser_is_cut_by_the_transport_budget_and_reported_as_a_transient_timeout()
    {
        var hanging = new StubHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, VersionJson);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var started = DateTime.UtcNow;

        var failure = await Failure(hanging, remaining: TimeSpan.FromSeconds(1.5));

        Assert.True(failure.IsTransient);
        Assert.Equal("parse_timeout", failure.Code);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "il trasporto non deve superare il budget residuo");
    }

    [Fact]
    public async Task With_no_meaningful_budget_left_nothing_is_sent_and_the_failure_is_permanent_deadline_exceeded()
    {
        var handler = Handler(() => new HttpResponseMessage(HttpStatusCode.OK));

        var failure = await Failure(handler, remaining: TimeSpan.FromMilliseconds(400));

        Assert.False(failure.IsTransient);
        Assert.Equal("deadline_exceeded", failure.Code);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task The_caller_cancellation_is_propagated_and_not_classified_as_a_parser_failure()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, VersionJson);
            }

            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ConvertAsync(Request(), Plenty, cts.Token));
    }

    [Fact]
    public async Task An_oversized_output_is_a_permanent_failure_never_a_truncated_manifest()
    {
        var big = Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5_000]) });

        var failure = await Failure(big, request: Request(maxOutput: 1_000));

        Assert.False(failure.IsTransient);
        Assert.Equal("parse_output_too_large", failure.Code);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"document-parse-9\",\"configHash\":\"c\",\"formats\":[\"txt\"]}")]
    [InlineData("{\"schemaVersion\":\"document-parse-1\",\"configHash\":\"c\",\"formats\":[\"pdf\"]}")]
    [InlineData("{\"schemaVersion\":\"document-parse-1\",\"formats\":[\"txt\"]}")]
    [InlineData("non json")]
    public async Task An_incompatible_version_document_blocks_the_conversion_permanently(string version)
    {
        var handler = Handler(() => new HttpResponseMessage(HttpStatusCode.OK), version);

        var failure = await Failure(handler);

        Assert.False(failure.IsTransient);
        Assert.Equal("parser_incompatible", failure.Code);
        Assert.DoesNotContain("/convert", handler.Paths);
    }
}
