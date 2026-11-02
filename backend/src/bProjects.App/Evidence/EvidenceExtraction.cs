using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using bProjects.App.Conversations;
using Microsoft.Extensions.AI;

namespace bProjects.App.Evidence;

// Contratto dell'estrazione `evidence-1.0.0` (contracts/ai-workflow.md §10.3–10.4): DTO che il modello compila, contenuto
// persistito di ciascuna evidenza, schema per lavoro e validazione deterministica. Nessuna riparazione dell'output.

public enum QualifierType
{
    Quantity,
    Deadline,
    Duration,
    Frequency,
    Minimum,
    Maximum,
    Condition,
    Exception,
    Obligation,
    Optionality,
    Exclusivity,
    Timing,
    Other,
}

/// <summary>Uscita del modello. L'ordine delle proprietà è quello di scrittura imposto dalla grammatica: prima le citazioni, poi l'interpretazione.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtractionOutput
{
    public required IReadOnlyList<ExtractedEvidence> Evidences { get; init; }
    public required string? NoEvidenceReason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtractedEvidence
{
    public required string Key { get; init; }
    public required IReadOnlyList<ExtractedSource> Sources { get; init; }
    public required EvidenceKind Kind { get; init; }
    public required string? KindRationale { get; init; }
    public required string Statement { get; init; }
    public required string? Subject { get; init; }
    public required string? Action { get; init; }
    public required string? Object { get; init; }
    public required string? Scope { get; init; }
    public required string? Purpose { get; init; }
    public required string? Instrument { get; init; }
    public required string? Channel { get; init; }
    public required string? Recipient { get; init; }
    public required IReadOnlyList<ExtractedQualifier> Qualifiers { get; init; }
    public required EvidencePolarity Polarity { get; init; }
    public required EvidenceCertainty Certainty { get; init; }
    public required IReadOnlyList<ExtractedRelation> Relations { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtractedSource
{
    public required Guid MessageId { get; init; }
    public required string Quote { get; init; }
    public required int Occurrence { get; init; }
    public required EvidenceSourceRole Role { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtractedQualifier
{
    public required QualifierType Type { get; init; }
    public required string Text { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExtractedRelation
{
    public required EvidenceRelationType Type { get; init; }
    public required string Target { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
    public required string? Rationale { get; init; }
}

/// <summary>Struttura semantica: i campi assenti restano null, mai valori generici.</summary>
public sealed record EvidenceStructure(
    string? Subject, string? Action, string? Object, string? Scope, string? Purpose, string? Instrument, string? Channel, string? Recipient);

public sealed record EvidenceQualifier(QualifierType Type, string Text);

/// <summary>Passo dell'originale: offset in unità UTF-16, <c>Start</c> incluso e <c>End</c> escluso; <c>Quote</c> è la sottostringa esatta.</summary>
public sealed record EvidenceSpan(Guid MessageId, long Sequence, EvidenceSourceRole Role, int Start, int End, string Quote);

/// <summary>Contenuto JSONB di <see cref="EvidenceItem.Content"/>.</summary>
public sealed record EvidenceContent(
    string? KindRationale, EvidenceStructure Structure, IReadOnlyList<EvidenceQualifier> Qualifiers, IReadOnlyList<EvidenceSpan> Sources);

/// <summary>Opzioni JSON strette del registro: camelCase, enum solo per nome, proprietà extra o duplicate rifiutate.</summary>
public static class EvidenceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"{typeof(T).Name} nullo.");
}

/// <summary>Messaggio originale fornito all'estrazione (mai oltre la sequenza del messaggio estratto).</summary>
public sealed record ExtractionMessage(Guid Id, long Sequence, MessageRole Role, string Body);

/// <summary>Evidenza corrente di un messaggio PM precedente, con l'alias stabile per questo lavoro (P1…Pn).</summary>
public sealed record PriorEvidence(string Alias, Guid EvidenceId, Guid MessageId, long Sequence, EvidenceKind Kind, EvidencePolarity Polarity, string Statement);

/// <summary>Input fidato dell'estrazione, letto dal database.</summary>
public sealed record ExtractionContext(
    Guid ProjectId,
    Guid JobId,
    ExtractionMessage Target,
    IReadOnlyList<ExtractionMessage> PriorUserMessages,
    ExtractionMessage? Referent,
    IReadOnlyList<PriorEvidence> PriorEvidences)
{
    public ExtractionMessage? Message(Guid id) =>
        id == Target.Id ? Target : Referent is { } r && r.Id == id ? r : PriorUserMessages.FirstOrDefault(m => m.Id == id);
}

public sealed record ValidatedEvidence(int Ordinal, string Key, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement, EvidenceContent Content);

/// <summary>Relazione validata: il bersaglio è una chiave di questa uscita oppure un'evidenza precedente fornita.</summary>
public sealed record ValidatedRelation(string FromKey, string? ToKey, PriorEvidence? ToPrior, EvidenceRelationType Type, EvidenceRelationBasis Basis, string? Rationale);

public sealed record ExtractionValidationResult(
    bool Accepted,
    IReadOnlyList<string> Violations,
    IReadOnlyList<ValidatedEvidence> Evidences,
    IReadOnlyList<ValidatedRelation> Relations,
    string? NoEvidenceReason);

/// <summary>JSON Schema dell'uscita: proiezione dei DTO, con gli id dei messaggi ammessi vincolati dalla grammatica.</summary>
public static class EvidenceExtractionSchema
{
    public const string Name = "evidence_extraction";

    private const string KeyPattern = "^e[1-9][0-9]{0,2}$";
    private const string TargetPattern = "^(e[1-9][0-9]{0,2}|P[1-9][0-9]{0,3})$";

    private static readonly Lazy<string> BaseText = new(() =>
        AIJsonUtilities.CreateJsonSchema(typeof(ExtractionOutput), serializerOptions: EvidenceJson.Options).GetRawText());

    /// <summary>Schema generico (senza vincoli del lavoro): opzioni di base della fase Extraction; ogni chiamata usa <see cref="ForJob"/>.</summary>
    public static JsonElement Base
    {
        get
        {
            using var document = JsonDocument.Parse(BaseText.Value);
            return document.RootElement.Clone();
        }
    }

    /// <summary>Schema del lavoro: <c>messageId</c> ammette solo il messaggio estratto, i PM precedenti forniti e il referente.</summary>
    public static JsonElement ForJob(ExtractionContext context)
    {
        var node = JsonNode.Parse(BaseText.Value)!;
        var evidence = node["properties"]!["evidences"]!["items"]!;
        var properties = evidence["properties"]!;
        properties["key"]!["pattern"] = KeyPattern;
        var allowed = new[] { context.Target.Id }
            .Concat(context.PriorUserMessages.Select(m => m.Id))
            .Concat(context.Referent is { } r ? [r.Id] : [])
            .Select(id => (JsonNode)JsonValue.Create(id.ToString())!)
            .ToArray();
        var source = properties["sources"]!;
        source["minItems"] = 1;
        var sourceProperties = source["items"]!["properties"]!;
        sourceProperties["messageId"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(allowed) };
        sourceProperties["occurrence"]!["minimum"] = 1;
        properties["relations"]!["items"]!["properties"]!["target"]!["pattern"] = TargetPattern;
        properties["statement"]!["minLength"] = 1;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Validazione deterministica dell'uscita (contracts/ai-workflow.md §10.4). Calcola gli offset UTF-16 dalle citazioni e rifiuta
/// ciò che non è verificabile: citazioni inesistenti, messaggi non forniti, assistant come fonte primaria, relazioni verso bersagli
/// assenti, zero evidenze senza motivo. Nessuna correzione dell'output.
/// </summary>
public static class EvidenceExtractionValidator
{
    public const string InvalidAgentOutput = "invalid_agent_output";
    public const int MaxEvidences = 100;
    public const int MaxStatementLength = 2_000;
    public const int MaxFieldLength = 1_000;

    public static ExtractionValidationResult ValidateText(string text, ExtractionContext context)
    {
        ExtractionOutput output;
        try
        {
            output = EvidenceJson.Deserialize<ExtractionOutput>(text.Trim());
        }
        catch (JsonException ex)
        {
            return Rejected($"JSON non valido per evidence-1.0.0: {Redact(ex.Message)}");
        }

        return Validate(output, context);
    }

    public static ExtractionValidationResult Validate(ExtractionOutput output, ExtractionContext context)
    {
        var violations = new List<string>();
        var evidences = new List<ValidatedEvidence>();
        var relations = new List<ValidatedRelation>();
        var items = output.Evidences ?? [];

        var reason = Clean(output.NoEvidenceReason);
        if (items.Count == 0 && reason is null)
        {
            violations.Add("zero evidenze senza noEvidenceReason: un messaggio senza fatti va motivato esplicitamente");
        }
        else if (items.Count > 0 && reason is not null)
        {
            violations.Add("noEvidenceReason ammesso solo con zero evidenze");
        }

        if (items.Count > MaxEvidences)
        {
            violations.Add($"troppe evidenze ({items.Count}, massimo {MaxEvidences})");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in items)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.Key) || !System.Text.RegularExpressions.Regex.IsMatch(e.Key, "^e[1-9][0-9]{0,2}$"))
            {
                violations.Add($"chiave di evidenza non valida «{e?.Key}»");
                continue;
            }

            if (!keys.Add(e.Key))
            {
                violations.Add($"chiave duplicata {e.Key}");
            }
        }

        var priors = context.PriorEvidences.ToDictionary(p => p.Alias, StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var e in items)
        {
            if (e is null)
            {
                continue;
            }

            var where = $"evidenza {e.Key}";
            var statement = Clean(e.Statement);
            if (statement is null)
            {
                violations.Add($"{where}: statement vuoto");
            }
            else if (statement.Length > MaxStatementLength)
            {
                violations.Add($"{where}: statement oltre {MaxStatementLength} caratteri");
            }

            if (!Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Polarity) || !Enum.IsDefined(e.Certainty))
            {
                violations.Add($"{where}: tipo, polarità o certezza non validi");
            }

            var rationale = Clean(e.KindRationale);
            if (e.Kind == EvidenceKind.Other && rationale is null)
            {
                violations.Add($"{where}: kind Other richiede kindRationale");
            }

            var structure = new EvidenceStructure(
                Field(e.Subject, where, "subject", violations), Field(e.Action, where, "action", violations),
                Field(e.Object, where, "object", violations), Field(e.Scope, where, "scope", violations),
                Field(e.Purpose, where, "purpose", violations), Field(e.Instrument, where, "instrument", violations),
                Field(e.Channel, where, "channel", violations), Field(e.Recipient, where, "recipient", violations));

            var qualifiers = new List<EvidenceQualifier>();
            foreach (var q in e.Qualifiers ?? [])
            {
                var qt = Clean(q?.Text);
                if (q is null || qt is null || !Enum.IsDefined(q.Type))
                {
                    violations.Add($"{where}: qualificatore vuoto o di tipo non valido");
                    continue;
                }

                qualifiers.Add(new EvidenceQualifier(q.Type, qt.Length > MaxFieldLength ? qt[..MaxFieldLength] : qt));
            }

            var spans = new List<EvidenceSpan>();
            foreach (var s in e.Sources ?? [])
            {
                if (s is null)
                {
                    violations.Add($"{where}: fonte nulla");
                    continue;
                }

                if (Locate(s, context, where, violations) is { } span)
                {
                    spans.Add(span);
                }
            }

            if (!spans.Any(s => s.Role == EvidenceSourceRole.Primary))
            {
                violations.Add($"{where}: manca una fonte Primary nel messaggio estratto (sequenza {context.Target.Sequence})");
            }

            var seen = new HashSet<(string, EvidenceRelationType)>();
            foreach (var r in e.Relations ?? [])
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Target))
                {
                    violations.Add($"{where}: relazione senza bersaglio");
                    continue;
                }

                if (!Enum.IsDefined(r.Type) || !Enum.IsDefined(r.Basis))
                {
                    violations.Add($"{where}: relazione di tipo o base non validi");
                    continue;
                }

                if (r.Target == e.Key)
                {
                    violations.Add($"{where}: relazione verso sé stessa");
                    continue;
                }

                if (!seen.Add((r.Target, r.Type)))
                {
                    violations.Add($"{where}: relazione {r.Type} verso {r.Target} duplicata");
                    continue;
                }

                if (keys.Contains(r.Target))
                {
                    relations.Add(new ValidatedRelation(e.Key, r.Target, null, r.Type, r.Basis, Clean(r.Rationale)));
                }
                else if (priors.TryGetValue(r.Target, out var prior))
                {
                    relations.Add(new ValidatedRelation(e.Key, null, prior, r.Type, r.Basis, Clean(r.Rationale)));
                }
                else
                {
                    violations.Add($"{where}: relazione verso «{r.Target}» inesistente (né chiave di questa uscita né evidenza precedente fornita)");
                }
            }

            evidences.Add(new ValidatedEvidence(
                ++ordinal, e.Key, e.Kind, e.Polarity, e.Certainty, statement ?? "",
                new EvidenceContent(rationale, structure, qualifiers, spans)));
        }

        return violations.Count == 0
            ? new(true, [], evidences, relations, items.Count == 0 ? reason : null)
            : new(false, violations, [], [], null);
    }

    /// <summary>
    /// Trova la citazione nell'originale e ne calcola gli offset. Prima la corrispondenza esatta; poi, solo se assente, quella che ignora
    /// differenze di spazi, maiuscole e apici tipografici, riportata alle posizioni dell'originale. La citazione persistita è sempre la
    /// sottostringa dell'originale, mai il testo del modello.
    /// </summary>
    private static EvidenceSpan? Locate(ExtractedSource source, ExtractionContext context, string where, List<string> violations)
    {
        var message = context.Message(source.MessageId);
        if (message is null)
        {
            violations.Add($"{where}: fonte su un messaggio non fornito ({source.MessageId})");
            return null;
        }

        if (!Enum.IsDefined(source.Role))
        {
            violations.Add($"{where}: ruolo della fonte non valido");
            return null;
        }

        var expected = message.Id == context.Target.Id
            ? EvidenceSourceRole.Primary
            : message.Role == MessageRole.Assistant ? EvidenceSourceRole.Referent : EvidenceSourceRole.Context;
        if (source.Role != expected && !(message.Id == context.Target.Id && source.Role == EvidenceSourceRole.Context))
        {
            violations.Add(source.Role == EvidenceSourceRole.Primary
                ? $"{where}: fonte Primary ammessa solo sul messaggio estratto (sequenza {context.Target.Sequence})"
                : $"{where}: ruolo {source.Role} incoerente con il messaggio di sequenza {message.Sequence} ({(message.Role == MessageRole.Assistant ? "assistant: solo Referent" : "PM precedente: solo Context")})");
            return null;
        }

        var quote = TrimQuote(source.Quote);
        if (quote.Length == 0)
        {
            violations.Add($"{where}: citazione vuota");
            return null;
        }

        if (source.Occurrence < 1)
        {
            violations.Add($"{where}: occurrence deve essere almeno 1");
            return null;
        }

        var matches = ExactMatches(message.Body, quote);
        if (matches.Count == 0)
        {
            matches = NormalizedMatches(message.Body, quote);
        }

        if (matches.Count == 0)
        {
            violations.Add($"{where}: citazione non presente nel messaggio di sequenza {message.Sequence}: «{Shorten(quote)}»");
            return null;
        }

        if (source.Occurrence > matches.Count)
        {
            violations.Add($"{where}: occurrence {source.Occurrence} oltre le {matches.Count} occorrenze della citazione");
            return null;
        }

        var (start, end) = matches[source.Occurrence - 1];
        return new EvidenceSpan(message.Id, message.Sequence, source.Role, start, end, message.Body[start..end]);
    }

    internal static List<(int Start, int End)> ExactMatches(string body, string quote)
    {
        var result = new List<(int, int)>();
        for (var i = body.IndexOf(quote, StringComparison.Ordinal); i >= 0; i = body.IndexOf(quote, i + 1, StringComparison.Ordinal))
        {
            result.Add((i, i + quote.Length));
        }

        return result;
    }

    /// <summary>Forma normalizzata con la mappa verso gli indici dell'originale (ogni carattere normalizzato ricorda la sua origine).</summary>
    private static (string Text, int[] Origin) Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        var origin = new List<int>(value.Length);
        var pendingSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                text.Append(' ');
                origin.Add(i - 1);
                pendingSpace = false;
            }

            text.Append(char.ToLower(Fold(c), CultureInfo.InvariantCulture));
            origin.Add(i);
        }

        return (text.ToString(), [.. origin]);
    }

    private static char Fold(char c) => c switch
    {
        '’' or '‘' or 'ʼ' => '\'',
        '“' or '”' or '«' or '»' => '"',
        '–' or '—' => '-',
        _ => c,
    };

    internal static List<(int Start, int End)> NormalizedMatches(string body, string quote)
    {
        var (text, origin) = Normalize(body);
        var (needle, _) = Normalize(quote);
        var result = new List<(int, int)>();
        if (needle.Length == 0)
        {
            return result;
        }

        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            var start = origin[i];
            var last = origin[i + needle.Length - 1];
            // Un carattere fuori BMP occupa due unità UTF-16: la fine include il surrogato basso.
            var end = last + (char.IsHighSurrogate(body[last]) && last + 1 < body.Length ? 2 : 1);
            result.Add((start, end));
        }

        return result;
    }

    private static string TrimQuote(string? quote) =>
        (quote ?? "").Trim().Trim('«', '»', '"', '“', '”').Trim();

    private static string? Field(string? value, string where, string name, List<string> violations)
    {
        var clean = Clean(value);
        if (clean is { Length: > MaxFieldLength })
        {
            violations.Add($"{where}: {name} oltre {MaxFieldLength} caratteri");
        }

        return clean;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Shorten(string value) => value.Length <= 80 ? value : value[..80] + "…";

    private static string Redact(string message) => message.Length <= 200 ? message : message[..200];

    private static ExtractionValidationResult Rejected(string violation) => new(false, [violation], [], [], null);
}
