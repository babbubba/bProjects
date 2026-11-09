using System.Text.Json;
using System.Text.Json.Serialization;

namespace bProjects.App.Analysis;

// AnalysisContent 1.1.0 (contracts/ai-workflow.md §1): stessa struttura della 1.0.0 con classificazione Documented e fonti
// DocumentPassage. Tipi e decoder distinti da quelli 1.0.0 (AnalysisContent.cs), che restano invariati: nessun decoder permissivo.

public enum DocumentAnalysisClassification { Declared, Documented, Inferred, Assumed }

public enum DocumentAnalysisSourceKind { Message, PmRevision, DocumentPassage }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentAnalysisSource
{
    public required Guid Id { get; init; }
    public required DocumentAnalysisSourceKind Kind { get; init; }
    public required Guid? MessageId { get; init; }
    public required AnalysisMessageRole? MessageRole { get; init; }
    public required Guid? RevisionId { get; init; }
    public required Guid? DocumentId { get; init; }
    public required Guid? DocumentVersionId { get; init; }
    public required Guid? PreparationId { get; init; }
    public required Guid? ArtifactId { get; init; }
    public required Guid? BlockId { get; init; }
    public required int? Start { get; init; }
    public required int? End { get; init; }
    public required string? Quote { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentAnalysisItem
{
    public required Guid Id { get; init; }
    public required string Text { get; init; }
    public required DocumentAnalysisClassification Classification { get; init; }
    public required IReadOnlyList<Guid> EvidenceRefs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentAnalysisConflict
{
    public required Guid Id { get; init; }
    public required string Description { get; init; }
    public required AnalysisConflictStatus Status { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Alternatives { get; init; }
    public required AnalysisConflictResolution? Resolution { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentAnalysisContextAndScope
{
    public required IReadOnlyList<DocumentAnalysisItem> Overview { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Included { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Excluded { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentAnalysisContent
{
    public const string SchemaVersionValue = "1.1.0";

    [JsonConverter(typeof(DocumentAnalysisSchemaVersionConverter))]
    public required string SchemaVersion { get; init; }
    public required Guid AnalysisId { get; init; }
    public required Guid ProjectId { get; init; }
    public required long SourceCutoffSequence { get; init; }
    public required AnalysisContentState ValidationState { get; init; }
    public required IReadOnlyList<DocumentAnalysisSource> Sources { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Goals { get; init; }
    public required DocumentAnalysisContextAndScope ContextAndScope { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Requirements { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> OperationalFlows { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Constraints { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Dependencies { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> Assumptions { get; init; }
    public required IReadOnlyList<DocumentAnalysisItem> OpenQuestions { get; init; }
    public required IReadOnlyList<DocumentAnalysisConflict> Conflicts { get; init; }
}

/// <summary>schemaVersion: const "1.1.0".</summary>
internal sealed class DocumentAnalysisSchemaVersionConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == DocumentAnalysisContent.SchemaVersionValue
            ? DocumentAnalysisContent.SchemaVersionValue
            : throw new JsonException($"schemaVersion deve essere \"{DocumentAnalysisContent.SchemaVersionValue}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>Opzioni JSON strette: camelCase, enum solo per nome, null/mancanti/extra/duplicati rifiutati.</summary>
public static class DocumentAnalysisContentJson
{
    /// <summary>Come la 1.0.0 ma con numeri rigorosi: «4» fra virgolette non è un intero.</summary>
    public static JsonSerializerOptions Options { get; } = new(AnalysisContentJson.Options) { NumberHandling = JsonNumberHandling.Strict };

    public static DocumentAnalysisContent Deserialize(string json) =>
        JsonSerializer.Deserialize<DocumentAnalysisContent>(json, Options) ?? throw new JsonException("AnalysisContent 1.1.0 nullo.");

    public static string Serialize(DocumentAnalysisContent content) => JsonSerializer.Serialize(content, Options);
}

/// <summary>Fatti del server contro cui si controlla un contenuto 1.1 (senza database): identità, cutoff e composizione dello snapshot.</summary>
public sealed record DocumentAnalysisFacts(Guid ProjectId, Guid AnalysisId, long ExpectedCutoff, int PmMessageCount, int ReadyDocumentSourceCount);

/// <summary>
/// Regole strutturali di AnalysisContent 1.1.0 (§1) verificabili senza leggere messaggi, revisioni o blocchi: identità, cutoff, XOR delle
/// fonti, Declared/Documented, assunzioni, riferimenti risolti e conflitti. La risoluzione di messaggi, revisioni, blocchi e quote contro
/// l'originale e le regole degli attori (AI/PM) sono del validatore di revisione (T060).
/// </summary>
public static class DocumentAnalysisContentRules
{
    public static IReadOnlyList<string> Check(DocumentAnalysisContent content, DocumentAnalysisFacts facts)
    {
        var checker = new Checker(content, facts);
        checker.Run();
        return checker.Violations;
    }

    private sealed class Checker(DocumentAnalysisContent content, DocumentAnalysisFacts facts)
    {
        private readonly Dictionary<Guid, DocumentAnalysisSource> _sources = [];
        private readonly HashSet<Guid> _ids = [];

        public List<string> Violations { get; } = [];

        public void Run()
        {
            Envelope();
            CheckSources();
            Items(content.Goals, "goals");
            Items(content.ContextAndScope.Overview, "contextAndScope.overview");
            Items(content.ContextAndScope.Included, "contextAndScope.included");
            Items(content.ContextAndScope.Excluded, "contextAndScope.excluded");
            Items(content.Requirements, "requirements");
            Items(content.OperationalFlows, "operationalFlows");
            Items(content.Constraints, "constraints");
            Items(content.Dependencies, "dependencies");
            Items(content.Assumptions, "assumptions", mustBeAssumed: true);
            Items(content.OpenQuestions, "openQuestions", evidenceOptional: true);
            Conflicts();
        }

        private void Fail(string message) => Violations.Add(message);

        private void Envelope()
        {
            if (content.ProjectId != facts.ProjectId)
            {
                Fail("projectId: diverso dal progetto del server.");
            }

            if (content.AnalysisId != facts.AnalysisId)
            {
                Fail("analysisId: diverso dall'analisi del server.");
            }

            if (content.SourceCutoffSequence < 0)
            {
                Fail("sourceCutoffSequence: minimo 0.");
            }
            else if (content.SourceCutoffSequence != facts.ExpectedCutoff)
            {
                Fail("sourceCutoffSequence: diverso dal cutoff del server.");
            }
            else if (content.SourceCutoffSequence == 0 && (facts.PmMessageCount > 0 || facts.ReadyDocumentSourceCount < 1))
            {
                Fail("sourceCutoffSequence: zero ammesso solo senza messaggi PM e con almeno una fonte documentale pronta nello snapshot.");
            }
        }

        private void CheckSources()
        {
            for (var i = 0; i < content.Sources.Count; i++)
            {
                var source = content.Sources[i];
                var path = $"sources[{i}]";
                if (!_ids.Add(source.Id) || !_sources.TryAdd(source.Id, source))
                {
                    Fail($"{path}.id: duplicato.");
                }

                var documentFields = new object?[] { source.DocumentId, source.DocumentVersionId, source.PreparationId, source.ArtifactId, source.BlockId, source.Start, source.End, source.Quote };
                switch (source.Kind)
                {
                    case DocumentAnalysisSourceKind.Message:
                        if (source.MessageId is null || source.MessageRole is null || source.RevisionId is not null || documentFields.Any(f => f is not null))
                        {
                            Fail($"{path}: una fonte Message richiede messageId e messageRole, revisionId e campi documentali null.");
                        }

                        break;
                    case DocumentAnalysisSourceKind.PmRevision:
                        if (source.MessageId is not null || source.MessageRole is not null || source.RevisionId is null || documentFields.Any(f => f is not null))
                        {
                            Fail($"{path}: una fonte PmRevision richiede revisionId, messageId/messageRole e campi documentali null.");
                        }

                        break;
                    case DocumentAnalysisSourceKind.DocumentPassage:
                        if (source.MessageId is not null || source.MessageRole is not null || source.RevisionId is not null || documentFields.Any(f => f is null))
                        {
                            Fail($"{path}: una fonte DocumentPassage richiede tutti i campi documentali e messageId/messageRole/revisionId null.");
                        }
                        else if (source.Start < 0 || source.End <= source.Start)
                        {
                            Fail($"{path}: range [start,end) non valido (start>=0, end>start).");
                        }
                        else if (string.IsNullOrWhiteSpace(source.Quote) || source.Quote.Length != source.End - source.Start)
                        {
                            Fail($"{path}.quote: non vuota e lunga quanto il range UTF-16.");
                        }

                        break;
                    default:
                        Fail($"{path}.kind: sconosciuto.");
                        break;
                }
            }
        }

        private void Items(IReadOnlyList<DocumentAnalysisItem> items, string section, bool mustBeAssumed = false, bool evidenceOptional = false)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var path = $"{section}[{i}]";
                CheckItem(items[i], path, evidenceOptional, evidenceRequired: false);
                if (mustBeAssumed && items[i].Classification != DocumentAnalysisClassification.Assumed)
                {
                    Fail($"{path}.classification: gli elementi di assumptions sono Assumed.");
                }
            }
        }

        private void CheckItem(DocumentAnalysisItem item, string path, bool evidenceOptional, bool evidenceRequired)
        {
            if (!_ids.Add(item.Id))
            {
                Fail($"{path}.id: duplicato.");
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                Fail($"{path}.text: vuoto o solo spazi.");
            }

            var resolved = Refs(item.EvidenceRefs, $"{path}.evidenceRefs");

            // Declared: dichiarazione del PM (messaggio user o revisione), mai un documento né l'assistente. Un item misto conserva
            // tutte le fonti e la classificazione dell'enunciato, senza elevare l'autorità della fonte documentale.
            if (item.Classification == DocumentAnalysisClassification.Declared && !evidenceOptional && !resolved.Any(IsPmSupport))
            {
                Fail($"{path}.evidenceRefs: Declared richiede una fonte PM (messaggio utente o revisione PM).");
            }

            if (item.Classification == DocumentAnalysisClassification.Documented && !resolved.Any(s => s.Kind == DocumentAnalysisSourceKind.DocumentPassage))
            {
                Fail($"{path}.evidenceRefs: Documented richiede almeno una fonte DocumentPassage.");
            }

            if (evidenceRequired && !resolved.Any(s => IsPmSupport(s) || s.Kind == DocumentAnalysisSourceKind.DocumentPassage))
            {
                Fail($"{path}.evidenceRefs: richiesta evidenza di un messaggio utente, di una revisione PM o di un passaggio documentale.");
            }
        }

        private static bool IsPmSupport(DocumentAnalysisSource s) =>
            s.Kind == DocumentAnalysisSourceKind.PmRevision || (s.Kind == DocumentAnalysisSourceKind.Message && s.MessageRole == AnalysisMessageRole.User);

        private List<DocumentAnalysisSource> Refs(IReadOnlyList<Guid> refs, string path)
        {
            var resolved = new List<DocumentAnalysisSource>();
            if (refs.Distinct().Count() != refs.Count)
            {
                Fail($"{path}: riferimenti duplicati.");
            }

            foreach (var reference in refs)
            {
                if (_sources.TryGetValue(reference, out var source))
                {
                    resolved.Add(source);
                }
                else
                {
                    Fail($"{path}: riferimento {reference} non risolto tra le fonti.");
                }
            }

            return resolved;
        }

        private void Conflicts()
        {
            for (var i = 0; i < content.Conflicts.Count; i++)
            {
                var conflict = content.Conflicts[i];
                var path = $"conflicts[{i}]";
                if (!_ids.Add(conflict.Id))
                {
                    Fail($"{path}.id: duplicato.");
                }

                if (string.IsNullOrWhiteSpace(conflict.Description))
                {
                    Fail($"{path}.description: vuota o solo spazi.");
                }

                if (conflict.Alternatives.Count < 2)
                {
                    Fail($"{path}.alternatives: servono almeno due alternative.");
                }

                for (var a = 0; a < conflict.Alternatives.Count; a++)
                {
                    CheckItem(conflict.Alternatives[a], $"{path}.alternatives[{a}]", evidenceOptional: false, evidenceRequired: true);
                }

                if (conflict.Status == AnalysisConflictStatus.Open && conflict.Resolution is not null)
                {
                    Fail($"{path}.resolution: un conflitto Open richiede resolution=null.");
                }
                else if (conflict.Status == AnalysisConflictStatus.Resolved)
                {
                    if (conflict.Resolution is null)
                    {
                        Fail($"{path}.resolution: un conflitto Resolved richiede la risoluzione.");
                    }
                    else
                    {
                        Refs(conflict.Resolution.EvidenceRefs, $"{path}.resolution.evidenceRefs");
                    }
                }
            }
        }
    }
}
