using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bProjects.App.Documents;

public enum ManifestStatus
{
    Complete,
    Incomplete,
}

public enum BlockType
{
    Heading,
    Paragraph,
    List,
    Table,
    Code,
    Picture,
    Other,
}

public enum IssueSeverity
{
    Blocking,
    Warning,
}

/// <summary>Primary: il range appartiene a questo gruppo. Context: copia di contesto (es. header ripetuto), mai proprietaria né citabile da sola.</summary>
public enum RangeOwnership
{
    Primary,
    Context,
}

/// <summary>Cosa il backend si aspetta dalla conversione richiesta: tutto ciò che il manifest dichiara deve coincidere o restare nei limiti.</summary>
public sealed record ManifestExpectations(
    string OriginalSha256, long OriginalByteLength, DocumentFormat Format, string ConfigHash, int MaxPages, int MaxBlocks, long MaxExpandedBytes);

public sealed record ValidatedBlock(
    string Ref, int Ordinal, string? ParentRef, BlockType Type, string Text, string TextHash, IReadOnlyList<string> HeadingPath,
    int? Page, double[]? Bbox, string? CoordinateSystem, string? DoclingRef, int? LineStart, int? LineEnd, string? TableCellsJson);

/// <summary>Range di un blocco: <c>Start</c>/<c>End</c> sono unità UTF-16 sul testo canonico (convertiti dai codepoint del parser e verificati).</summary>
public sealed record ValidatedRange(string RangeKey, string BlockRef, int Start, int End, RangeOwnership Ownership, string TextHash);

public sealed record ValidatedChunk(int Ordinal, IReadOnlyList<ValidatedRange> Ranges);

public sealed record ValidatedIssue(string Code, IssueSeverity Severity, int? Page, string? BlockRef, string Reason, bool AffectsCompleteness);

public sealed record ValidatedManifest(
    string ParserVersion, string DependencyLockHash, string ModelArtifactsHash, string ConfigHash, ManifestStatus Status, int? PageCount,
    IReadOnlyList<ValidatedBlock> Blocks, IReadOnlyList<ValidatedChunk> Chunks, IReadOnlyList<ValidatedIssue> Issues, int InventoryCount);

/// <summary>
/// Manifest respinto: ostile, incoerente o oltre i limiti. Il codice è stabile e permanente (nessun retry cambia un manifest invalido);
/// il motivo è non sensibile e non contiene testo del documento.
/// </summary>
public sealed class ManifestRejectedException(string code, string reason) : Exception(reason)
{
    public string Code { get; } = code;
}

/// <summary>
/// Validazione backend del manifest document-parse-1 (contracts/parser.md §Manifest di risposta): un 200 significa «conversione terminata»,
/// non «documento valido o completo». Controlla identità e hash, limiti, unicità di ref e ordine, gerarchia aciclica, locator, celle di tabella,
/// inventario e range; converte gli offset da codepoint (parser Python) a unità UTF-16 (.NET) e li verifica sul testo e sul suo hash.
/// Un manifest dichiarato Complete con elementi non trattati è ostile (respinto); uno Incomplete o con testo non assegnato è accettato come
/// Incomplete, mai promosso: nessun percorso di questo validatore produce uno stato Ready.
/// </summary>
public static class ParsedManifestValidator
{
    public const string OffsetUnit = "unicode-codepoint";

    private const int MaxRefLength = 256;
    private const int MaxCodeLength = 64;
    private const int MaxReasonLength = 1_000;
    private const int MaxHeadingDepth = 64;
    private const int MaxTableSide = 10_000;
    private const long MaxTableArea = 4_000_000;
    private const int MaxIssues = 100_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        MaxDepth = 256,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly JsonSerializerOptions CellsOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ValidatedManifest Validate(ReadOnlySpan<byte> body, ManifestExpectations expected)
    {
        var dto = Parse(body);
        var origin = ParserClient.FormatCode(expected.Format);

        Require(dto.SchemaVersion == ParserClient.SchemaVersion, "manifest_version_unsupported", "Versione dello schema del manifest non supportata.");
        Require(
            string.Equals(dto.OriginalSha256, expected.OriginalSha256, StringComparison.OrdinalIgnoreCase) && dto.OriginalByteLength == expected.OriginalByteLength,
            "manifest_hash_mismatch", "Hash o lunghezza dell'originale non corrispondono alla richiesta.");
        Require(!string.IsNullOrEmpty(dto.ConfigHash) && dto.ConfigHash == expected.ConfigHash, "manifest_config_mismatch", "Configurazione del parser diversa da quella richiesta.");
        Require(dto.OriginFormat == origin, "manifest_format_mismatch", "Formato di origine diverso da quello dichiarato.");
        Malformed(dto.OffsetConvention == OffsetUnit, "Convenzione di offset non ammessa.");
        Malformed(
            !string.IsNullOrWhiteSpace(dto.ParserVersion) && !string.IsNullOrWhiteSpace(dto.DependencyLockHash) && !string.IsNullOrWhiteSpace(dto.ModelArtifactsHash),
            "Versioni o hash del parser mancanti.");
        Malformed(
            dto.Document.ValueKind == JsonValueKind.Object
            && dto.Document.TryGetProperty("serialization", out var serialization)
            && serialization.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(serialization.GetString()),
            "Documento originale senza versione di serializzazione.");
        var declared = dto.Status switch
        {
            "Complete" => ManifestStatus.Complete,
            "Incomplete" => ManifestStatus.Incomplete,
            _ => throw Malformed("Stato del manifest sconosciuto."),
        };
        if (dto.Blocks is null || dto.Inventory is null || dto.Chunks is null || dto.Issues is null)
        {
            throw Malformed("Sezioni obbligatorie mancanti.");
        }

        Require(dto.PageCount is null || dto.PageCount >= 0, "manifest_inconsistent", "Conteggio pagine negativo.");
        Require(dto.PageCount is null || dto.PageCount <= expected.MaxPages, "parse_limit_exceeded", "Pagine oltre il limite ammesso.");
        Require(dto.Blocks.Count <= expected.MaxBlocks, "parse_limit_exceeded", "Blocchi oltre il limite ammesso.");
        Malformed(dto.Issues.Count <= MaxIssues, "Troppe segnalazioni.");
        // PDF: sempre paginato. DOCX: pagine non stabili, pageCount nullable (mai pagine simulate). MD/TXT: mai paginati.
        Require(origin switch { "pdf" => dto.PageCount is not null, "docx" => true, _ => dto.PageCount is null }, "manifest_inconsistent", "Conteggio pagine incoerente col formato.");

        var blocks = ValidateBlocks(dto.Blocks, dto.PageCount, origin, expected.MaxExpandedBytes);
        var byRef = blocks.ToDictionary(b => b.Ref, StringComparer.Ordinal);
        ValidateHierarchy(blocks, byRef);
        var (issues, issuesById) = ValidateIssues(dto.Issues, byRef, dto.PageCount);
        var unprocessed = ValidateInventory(dto.Inventory, byRef, issuesById, dto.PageCount);
        var (chunks, synthetic) = ValidateChunks(dto.Chunks, blocks, byRef);

        var explained = unprocessed > 0 || issues.Any(i => i.AffectsCompleteness || i.Severity == IssueSeverity.Blocking);
        if (declared == ManifestStatus.Complete)
        {
            Require(unprocessed == 0 && !issues.Any(i => i.AffectsCompleteness), "manifest_inconsistent", "Manifest Complete con elementi non trattati.");
        }

        var all = new List<ValidatedIssue>(issues);
        all.AddRange(synthetic);
        if (declared == ManifestStatus.Incomplete && !explained && synthetic.Count == 0)
        {
            all.Add(new("incomplete_unexplained", IssueSeverity.Blocking, null, null, "Il parser ha dichiarato il documento incompleto senza indicarne il motivo.", true));
        }

        var status = declared == ManifestStatus.Incomplete || explained || synthetic.Count > 0 ? ManifestStatus.Incomplete : ManifestStatus.Complete;
        return new(dto.ParserVersion!, dto.DependencyLockHash!, dto.ModelArtifactsHash!, dto.ConfigHash!, status, dto.PageCount, blocks, chunks, all, dto.Inventory.Count);
    }

    // ---- struttura --------------------------------------------------------------------------------------------------------------

    private static ManifestDto Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<ManifestDto>(body, Options) ?? throw Malformed("Manifest vuoto.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw Malformed("Manifest non è JSON valido o non rispetta lo schema.");
        }
    }

    private static List<ValidatedBlock> ValidateBlocks(List<BlockDto?> input, int? pageCount, string origin, long maxExpandedBytes)
    {
        var textual = origin is "md" or "txt";
        var blocks = new List<ValidatedBlock>(input.Count);
        var refs = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        var previous = -1;
        foreach (var dto in input)
        {
            Malformed(dto is not null, "Blocco nullo.");
            Malformed(!string.IsNullOrEmpty(dto!.Ref) && dto.Ref.Length <= MaxRefLength, "Ref di blocco non valido.");
            Malformed(dto.ItemRef is null || dto.ItemRef.Length <= MaxRefLength, "Riferimento Docling non valido.");
            Require(refs.Add(dto.Ref!), "manifest_inconsistent", "Ref di blocco duplicato.");
            Require(dto.Order is { } ordinal && ordinal > previous, "manifest_inconsistent", "Ordine dei blocchi non crescente.");
            previous = dto.Order!.Value;
            var type = dto.Type switch
            {
                "heading" => BlockType.Heading,
                "paragraph" => BlockType.Paragraph,
                "list" => BlockType.List,
                "table" => BlockType.Table,
                "code" => BlockType.Code,
                "picture" => BlockType.Picture,
                "other" => BlockType.Other,
                _ => throw Malformed("Tipo di blocco sconosciuto."),
            };
            var text = dto.Text ?? throw Malformed("Testo del blocco mancante.");
            Malformed(!HasLoneSurrogate(text), "Testo con surrogati non accoppiati.");
            Require(dto.TextSha256 is null || string.Equals(dto.TextSha256, Sha256(text), StringComparison.OrdinalIgnoreCase), "manifest_hash_mismatch", "Hash del testo del blocco non corrisponde.");
            expanded += Encoding.UTF8.GetByteCount(text);
            Require(expanded <= maxExpandedBytes, "parse_limit_exceeded", "Testo estratto oltre i byte espansi ammessi.");
            var headingPath = dto.HeadingPath ?? throw Malformed("Percorso dei titoli mancante.");
            Malformed(headingPath.Count <= MaxHeadingDepth && headingPath.All(h => h is not null && !HasLoneSurrogate(h)), "Percorso dei titoli non valido.");
            var locator = dto.Locator ?? throw Malformed("Locator del blocco mancante.");
            ValidateLocator(locator, pageCount, textual);
            blocks.Add(new(
                dto.Ref!, dto.Order.Value, dto.ParentRef, type, text, Sha256(text), headingPath!, locator.Page,
                locator.Bbox is { } box ? [box.L!.Value, box.T!.Value, box.R!.Value, box.B!.Value] : null,
                locator.CoordinateSystem, locator.Path ?? dto.ItemRef, locator.LineStart, locator.LineEnd, ValidateTable(dto.Table, type)));
        }

        return blocks;
    }

    private static void ValidateLocator(LocatorDto locator, int? pageCount, bool textual)
    {
        Malformed(locator.Path is null || locator.Path.Length <= MaxRefLength, "Percorso strutturale non valido.");
        Malformed(locator.CoordinateSystem is null || locator.CoordinateSystem.Length <= MaxCodeLength, "Sistema di coordinate non valido.");
        if (textual)
        {
            // Markdown/TXT: mapping alla sorgente per righe; nessuna pagina simulata né coordinate.
            Require(locator.LineStart is >= 1 && locator.LineEnd >= locator.LineStart, "manifest_inconsistent", "Righe sorgente mancanti o non valide.");
            Require(locator.Page is null && locator.Bbox is null, "manifest_inconsistent", "Pagina o coordinate in un formato testuale.");
            return;
        }

        Require(locator.LineStart is null && locator.LineEnd is null, "manifest_inconsistent", "Righe sorgente su un formato non testuale.");
        if (locator.Page is { } page)
        {
            Require(pageCount is not null && page >= 1 && page <= pageCount, "manifest_inconsistent", "Pagina fuori dal documento.");
        }

        if (locator.Bbox is { } bbox)
        {
            Require(
                locator.Page is not null && bbox is { L: { } l, T: { } t, R: { } r, B: { } b } && double.IsFinite(l) && double.IsFinite(t) && double.IsFinite(r) && double.IsFinite(b)
                && !string.IsNullOrWhiteSpace(locator.CoordinateSystem),
                "manifest_inconsistent", "Coordinate senza pagina, sistema o valori finiti.");
        }
    }

    private static string? ValidateTable(TableDto? table, BlockType type)
    {
        if (type != BlockType.Table)
        {
            Require(table is null, "manifest_inconsistent", "Celle su un blocco che non è una tabella.");
            return null;
        }

        Require(table is { Cells.Count: > 0 } && table.Rows is >= 1 && table.Cols is >= 1, "manifest_inconsistent", "Tabella senza celle o dimensioni.");
        var (rows, columns) = (table!.Rows!.Value, table.Cols!.Value);
        Require(rows <= MaxTableSide && columns <= MaxTableSide && (long)rows * columns <= MaxTableArea, "parse_limit_exceeded", "Tabella oltre i limiti ammessi.");
        var occupied = new HashSet<long>();
        var cells = new List<object>(table.Cells!.Count);
        foreach (var cell in table.Cells)
        {
            Malformed(cell is not null && cell.Text is not null && !HasLoneSurrogate(cell.Text), "Cella non valida.");
            var (row, col, rowSpan, colSpan) = (cell!.Row, cell.Col, cell.RowSpan, cell.ColSpan);
            Require(
                row is >= 0 && col is >= 0 && rowSpan is >= 1 && colSpan is >= 1 && row + rowSpan <= rows && col + colSpan <= columns,
                "manifest_inconsistent", "Cella fuori dalla griglia o con span non valido.");
            for (var r = row!.Value; r < row + rowSpan; r++)
            {
                for (var c = col!.Value; c < col + colSpan; c++)
                {
                    Require(occupied.Add((long)r * columns + c), "manifest_inconsistent", "Celle sovrapposte.");
                }
            }

            cells.Add(new { row, col, rowSpan, colSpan, header = cell.Header, text = cell.Text, path = cell.Path });
        }

        return JsonSerializer.Serialize(new { rows, columns, cells }, CellsOptions);
    }

    /// <summary>Ogni parent esiste (≠ sé) e risalendo non si torna mai su un blocco già visitato.</summary>
    private static void ValidateHierarchy(List<ValidatedBlock> blocks, Dictionary<string, ValidatedBlock> byRef)
    {
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);   // false = in visita, true = risolto
        foreach (var start in blocks)
        {
            var path = new List<string>();
            var current = start;
            while (true)
            {
                if (state.TryGetValue(current.Ref, out var done))
                {
                    Require(done, "manifest_inconsistent", "Gerarchia dei blocchi ciclica.");
                    break;
                }

                state[current.Ref] = false;
                path.Add(current.Ref);
                if (current.ParentRef is null)
                {
                    break;
                }

                Require(byRef.TryGetValue(current.ParentRef, out var parent) && parent.Ref != current.Ref, "manifest_inconsistent", "Parent inesistente o uguale al blocco.");
                current = parent!;
            }

            foreach (var reference in path)
            {
                state[reference] = true;
            }
        }
    }

    private static (List<ValidatedIssue> Issues, Dictionary<string, ValidatedIssue> ById) ValidateIssues(
        List<IssueDto?> input, Dictionary<string, ValidatedBlock> byRef, int? pageCount)
    {
        var issues = new List<ValidatedIssue>(input.Count);
        var ids = new Dictionary<string, ValidatedIssue>(StringComparer.Ordinal);
        foreach (var dto in input)
        {
            Malformed(dto is not null, "Segnalazione nulla.");
            Malformed(!string.IsNullOrEmpty(dto!.IssueRef) && dto.IssueRef.Length <= MaxRefLength, "Id di segnalazione non valido.");
            Malformed(!string.IsNullOrEmpty(dto.Code) && dto.Code.Length <= MaxCodeLength, "Codice di segnalazione non valido.");
            Malformed(!string.IsNullOrWhiteSpace(dto.Reason) && dto.Reason.Length <= MaxReasonLength && !HasLoneSurrogate(dto.Reason), "Motivo non valido o troppo lungo.");
            Malformed(dto.AffectsCompleteness is not null, "affectsCompleteness mancante.");
            var severity = dto.Severity switch
            {
                "blocking" => IssueSeverity.Blocking,
                "warning" => IssueSeverity.Warning,
                _ => throw Malformed("Gravità sconosciuta."),
            };
            Require(!ids.ContainsKey(dto.IssueRef!), "manifest_inconsistent", "Id di segnalazione duplicato.");
            Require(dto.BlockRef is null || byRef.ContainsKey(dto.BlockRef), "manifest_inconsistent", "Segnalazione su un blocco inesistente.");
            Require(dto.Page is null || (pageCount is not null && dto.Page >= 1 && dto.Page <= pageCount), "manifest_inconsistent", "Segnalazione su una pagina inesistente.");
            var issue = new ValidatedIssue(dto.Code!, severity, dto.Page, dto.BlockRef, dto.Reason!, dto.AffectsCompleteness!.Value);
            ids[dto.IssueRef!] = issue;
            issues.Add(issue);
        }

        return (issues, ids);
    }

    /// <summary>Inventario: ogni voce rappresentata, vuota con motivo o non trattata con una segnalazione che incide sulla completezza. Ritorna le voci non trattate.</summary>
    private static int ValidateInventory(List<InventoryDto?> input, Dictionary<string, ValidatedBlock> byRef, Dictionary<string, ValidatedIssue> issuesById, int? pageCount)
    {
        Require(input.Count > 0, "manifest_inconsistent", "Inventario vuoto.");
        var unprocessed = 0;
        var pages = new HashSet<int>();
        foreach (var dto in input)
        {
            Malformed(dto is not null, "Voce di inventario nulla.");
            Malformed(!string.IsNullOrEmpty(dto!.Kind) && dto.Kind.Length <= MaxCodeLength, "Tipo di voce di inventario non valido.");
            Malformed(dto.ItemRef is null || dto.ItemRef.Length <= MaxRefLength, "Riferimento di voce di inventario non valido.");
            Malformed(dto.State is "represented" or "unprocessed" or "empty", "Stato di voce di inventario sconosciuto.");
            var blockRefs = dto.BlockRefs ?? throw Malformed("blockRefs mancante.");
            var issueRefs = dto.IssueRefs ?? throw Malformed("issueRefs mancante.");
            Require(blockRefs.All(r => r is not null && byRef.ContainsKey(r)), "manifest_inconsistent", "Voce di inventario con blocco inesistente.");
            Require(issueRefs.All(r => r is not null && issuesById.ContainsKey(r)), "manifest_inconsistent", "Voce di inventario con segnalazione inesistente.");
            if (dto.Kind == "page")
            {
                Require(pageCount is not null && dto.Page is >= 1 && dto.Page <= pageCount && pages.Add(dto.Page.Value), "manifest_inconsistent", "Pagina di inventario non valida o duplicata.");
            }

            switch (dto.State)
            {
                case "represented":
                    Require(blockRefs.Count > 0, "manifest_inconsistent", "Voce rappresentata senza blocchi.");
                    break;
                case "empty":
                    Require(!string.IsNullOrWhiteSpace(dto.Reason) && dto.Reason.Length <= MaxReasonLength, "manifest_inconsistent", "Voce vuota senza motivazione.");
                    break;
                default:
                    unprocessed++;
                    Require(issueRefs.Any(r => issuesById[r!].AffectsCompleteness), "manifest_inconsistent", "Voce non trattata senza una segnalazione che incide sulla completezza.");
                    break;
            }
        }

        Require(pageCount is null || pages.Count == pageCount, "manifest_inconsistent", "Inventario non copre tutte le pagine.");
        return unprocessed;
    }

    // ---- chunk e range ------------------------------------------------------------------------------------------------------------

    private static (List<ValidatedChunk> Chunks, List<ValidatedIssue> Synthetic) ValidateChunks(
        List<ChunkDto?> input, List<ValidatedBlock> blocks, Dictionary<string, ValidatedBlock> byRef)
    {
        var offsets = new Dictionary<string, int[]?>(StringComparer.Ordinal);   // blocco → prefisso UTF-16 per codepoint; null = nessun surrogato
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var primary = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        var chunks = new List<ValidatedChunk>(input.Count);
        foreach (var dto in input)
        {
            Malformed(dto is not null && dto.Ranges is { Count: > 0 }, "Chunk senza range.");
            var ranges = new List<ValidatedRange>(dto!.Ranges!.Count);
            foreach (var range in dto.Ranges)
            {
                Malformed(range is not null && !string.IsNullOrEmpty(range.RangeKey) && range.RangeKey.Length <= MaxRefLength, "Range non valido.");
                var ownership = range!.Ownership switch
                {
                    "Primary" => RangeOwnership.Primary,
                    "Context" => RangeOwnership.Context,
                    _ => throw Malformed("Proprietà del range sconosciuta."),
                };
                Require(keys.Add(range.RangeKey!), "manifest_inconsistent", "rangeKey duplicata.");
                Require(range.BlockRef is not null && byRef.TryGetValue(range.BlockRef, out _), "manifest_inconsistent", "Range su un blocco inesistente.");
                var block = byRef[range.BlockRef!];
                if (!offsets.TryGetValue(block.Ref, out var prefix))
                {
                    prefix = PrefixOf(block.Text);
                    offsets[block.Ref] = prefix;
                }

                var length = prefix is null ? block.Text.Length : prefix.Length - 1;   // lunghezza in codepoint
                Require(range.Start is >= 0 && range.End > range.Start && range.End <= length, "manifest_inconsistent", "Range fuori dal testo del blocco.");
                var (start, end) = prefix is null ? (range.Start!.Value, range.End!.Value) : (prefix[range.Start!.Value], prefix[range.End!.Value]);
                Require(
                    string.Equals(range.SourceTextSha256, Sha256(block.Text[start..end]), StringComparison.OrdinalIgnoreCase),
                    "manifest_hash_mismatch", "Hash del testo del range non corrisponde al testo canonico.");
                ranges.Add(new(range.RangeKey!, block.Ref, start, end, ownership, range.SourceTextSha256!.ToLowerInvariant()));
                if (ownership == RangeOwnership.Primary)
                {
                    if (!primary.TryGetValue(block.Ref, out var list))
                    {
                        primary[block.Ref] = list = [];
                    }

                    list.Add((start, end));
                }
            }

            chunks.Add(new(chunks.Count, ranges));
        }

        var synthetic = new List<ValidatedIssue>();
        foreach (var block in blocks)
        {
            primary.TryGetValue(block.Ref, out var owned);
            var sorted = (owned ?? []).OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
            for (var i = 1; i < sorted.Count; i++)
            {
                Require(sorted[i].Start >= sorted[i - 1].End, "manifest_inconsistent", "Più proprietari per lo stesso testo.");
            }

            if (HasUncoveredContent(block.Text, sorted))
            {
                synthetic.Add(new("range_uncovered", IssueSeverity.Blocking, block.Page, block.Ref, "Testo del blocco non assegnato a nessun passaggio.", true));
            }
        }

        return (chunks, synthetic);
    }

    /// <summary>Prefisso codepoint→UTF-16 (lunghezza codepoint+1), oppure null se il testo non ha coppie surrogate (offset identici).</summary>
    private static int[]? PrefixOf(string text)
    {
        if (!text.Any(char.IsSurrogate))
        {
            return null;
        }

        var prefix = new List<int> { 0 };
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            position += rune.Utf16SequenceLength;
            prefix.Add(position);
        }

        return [.. prefix];
    }

    private static bool HasUncoveredContent(string text, List<(int Start, int End)> sorted)
    {
        var cursor = 0;
        foreach (var (start, end) in sorted)
        {
            if (start > cursor && ContainsContent(text.AsSpan(cursor, start - cursor)))
            {
                return true;
            }

            cursor = Math.Max(cursor, end);
        }

        return cursor < text.Length && ContainsContent(text.AsSpan(cursor));
    }

    private static bool ContainsContent(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    // ---- utilità ------------------------------------------------------------------------------------------------------------------

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Require(bool condition, string code, string reason)
    {
        if (!condition)
        {
            throw new ManifestRejectedException(code, reason);
        }
    }

    private static void Malformed(bool condition, string reason) => Require(condition, "manifest_malformed", reason);

    private static ManifestRejectedException Malformed(string reason) => new("manifest_malformed", reason);

    // ---- DTO di lettura (campi del contratto, camelCase) -------------------------------------------------------------------------------

    private sealed class ManifestDto
    {
        public string? SchemaVersion { get; set; }
        public string? OriginalSha256 { get; set; }
        public string? ParserVersion { get; set; }
        public string? DependencyLockHash { get; set; }
        public string? ModelArtifactsHash { get; set; }
        public string? ConfigHash { get; set; }
        public string? Status { get; set; }
        public string? OriginFormat { get; set; }
        public long? OriginalByteLength { get; set; }
        public int? PageCount { get; set; }
        public string? OffsetConvention { get; set; }
        public JsonElement Document { get; set; }
        public List<BlockDto?>? Blocks { get; set; }
        public List<InventoryDto?>? Inventory { get; set; }
        public List<ChunkDto?>? Chunks { get; set; }
        public List<IssueDto?>? Issues { get; set; }
    }

    private sealed class BlockDto
    {
        public string? Ref { get; set; }
        public int? Order { get; set; }
        public string? ItemRef { get; set; }
        public string? ParentRef { get; set; }
        public string? Type { get; set; }
        public string? Text { get; set; }
        public string? TextSha256 { get; set; }
        public List<string?>? HeadingPath { get; set; }
        public LocatorDto? Locator { get; set; }
        public TableDto? Table { get; set; }
    }

    private sealed class LocatorDto
    {
        public int? Page { get; set; }
        public BboxDto? Bbox { get; set; }
        public string? CoordinateSystem { get; set; }
        public string? Path { get; set; }
        public int? LineStart { get; set; }
        public int? LineEnd { get; set; }
    }

    private sealed class BboxDto
    {
        public double? L { get; set; }
        public double? T { get; set; }
        public double? R { get; set; }
        public double? B { get; set; }
    }

    private sealed class TableDto
    {
        public int? Rows { get; set; }
        public int? Cols { get; set; }
        public List<CellDto?>? Cells { get; set; }
    }

    private sealed class CellDto
    {
        public int? Row { get; set; }
        public int? Col { get; set; }
        public int? RowSpan { get; set; }
        public int? ColSpan { get; set; }
        public bool Header { get; set; }
        public string? Text { get; set; }
        public string? Path { get; set; }
    }

    private sealed class InventoryDto
    {
        public string? ItemRef { get; set; }
        public string? Kind { get; set; }
        public int? Page { get; set; }
        public string? State { get; set; }
        public string? Reason { get; set; }
        public List<string?>? BlockRefs { get; set; }
        public List<string?>? IssueRefs { get; set; }
    }

    private sealed class ChunkDto
    {
        public List<RangeDto?>? Ranges { get; set; }
    }

    private sealed class RangeDto
    {
        public string? RangeKey { get; set; }
        public string? BlockRef { get; set; }
        public int? Start { get; set; }
        public int? End { get; set; }
        public string? Ownership { get; set; }
        public string? SourceTextSha256 { get; set; }
    }

    private sealed class IssueDto
    {
        public string? IssueRef { get; set; }
        public string? Code { get; set; }
        public string? Severity { get; set; }
        public int? Page { get; set; }
        public string? BlockRef { get; set; }
        public string? Reason { get; set; }
        public bool? AffectsCompleteness { get; set; }
    }
}
