using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>Stato di readiness di un modello del recupero in un istante: solo osservazione (stato, artefatto atteso/osservato, dimensione), mai la chiave né l'URL completo.</summary>
internal sealed record ModelReadinessRecord(
    string Moment, string Role, string LogicalEndpoint, string ModelId, string State, string ExpectedArtifact, string? ObservedArtifact, int? Dimension, string Sha256Status, DateTimeOffset? CheckedAt);

/// <summary>
/// Gate e diagnostica SOLO-TEST dell'indicizzazione di T040 (003). Nessun warm-up, nessuna richiesta di risveglio: si osserva e si conserva. Il Run 2 ha perso la causa del fallimento dell'indice (conteggio senza
/// <c>ErrorCode</c>, database eliminato, registratore in memoria); qui ogni passo scrive su file locale, prima dell'eliminazione del database, anche in caso di eccezione o blocco. Nessun testo di documento, segreto o URL
/// con credenziali: solo identificativi, stati, conteggi, codici HTTP, tipi di eccezione e messaggi tipizzati del recupero.
/// </summary>
internal static class RetrievalIndexGate
{
    /// <summary>Endpoint logico del modello: host:porta e percorso base, senza credenziali né query.</summary>
    public static string LogicalEndpoint(RetrievalModelOptions model) =>
        Uri.TryCreate(model.Endpoint, UriKind.Absolute, out var uri) ? $"{uri.Host}:{uri.Port}{uri.AbsolutePath}" : "(non configurato)";

    private static ModelReadinessRecord Record(string moment, string role, RetrievalModelOptions model, RetrievalModelSnapshot s) =>
        new(moment, role, LogicalEndpoint(model), s.ModelId, s.State.ToString(), s.ExpectedArtifact, s.Artifact, s.Dimension, s.Sha256Status.ToString(), s.CheckedAt);

    /// <summary>
    /// Snapshot di readiness di embedding e riordino. <paramref name="probe"/> = sonda reale (<c>/v1/models</c> e, se il modello è caricato, la richiesta funzionale minima del prodotto, come in produzione);
    /// altrimenti l'ultimo stato noto, senza I/O. Non invia richieste di risveglio.
    /// </summary>
    public static async Task<IReadOnlyList<ModelReadinessRecord>> CaptureAsync(RetrievalModelReadiness readiness, RetrievalOptions options, string moment, bool probe, CancellationToken ct)
    {
        var embedding = probe ? await readiness.GetEmbeddingSnapshotAsync(ct) : readiness.Current;
        var reranker = probe ? await readiness.GetRerankerSnapshotAsync(ct) : readiness.CurrentReranker;
        return [Record(moment, "embedding", options.Embedding, embedding), Record(moment, "reranker", options.Reranker, reranker)];
    }

    public static string Describe(ModelReadinessRecord r) =>
        $"{r.Moment}/{r.Role} {r.ModelId}@{r.LogicalEndpoint}: stato {r.State}; artefatto atteso {r.ExpectedArtifact}, osservato {r.ObservedArtifact ?? "n/d"}; dimensione {r.Dimension?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/d"}";

    public static string Describe(RetrievalIndexBuildResult r) =>
        $"versione {r.DocumentVersionId}: {r.Status}, blocchi={r.BlockCount} voci={r.EntryCount} embedding nuovi={r.EmbeddedNow} ErrorCode={r.ErrorCode ?? "-"}";

    /// <summary>Indicizzazione riuscita solo se ogni preparazione Ready ha un esito Ready senza errore e ci sono preparazioni (mai «riuscita» a vuoto, parziale o senza dettaglio).</summary>
    public static bool IsComplete(RetrievalRebuildSummary summary, int expectedPreparations) =>
        summary.Preparations == expectedPreparations && expectedPreparations > 0 && summary.Failed == 0 && summary.Ready == expectedPreparations
        && summary.Results is { } results && results.Count == expectedPreparations && results.All(r => r.Status == RetrievalIndexStatus.Ready && r.ErrorCode is null);

    /// <summary>Chiamate HTTP del recupero registrate (percorso, fase, codice HTTP, durata, tipo di eccezione): dei testi inviati solo numero e lunghezza totale.</summary>
    public static IReadOnlyList<object> HttpSummary(IEnumerable<RetrievalCallRecorder.Call> calls) =>
        [.. calls.Select(c => (object)new { c.At, c.Phase, c.Kind, c.Path, status = c.Status, ms = Math.Round(c.Milliseconds, 1), inputs = c.Inputs.Count, inputChars = c.Inputs.Sum(i => i.Length), c.Exception })];

    /// <summary>
    /// Esegue la ricostruzione conservando SEMPRE l'esito: con eccezione scrive tipo e messaggio nel file e la rilancia (nessun risultato perso, nessun fallimento nascosto).
    /// </summary>
    public static async Task<RetrievalRebuildSummary> RebuildAsync(Func<Task<RetrievalRebuildSummary>> rebuild, IndexDiagnostics diagnostics, string path)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var summary = await rebuild();
            diagnostics.Summary = summary;
            diagnostics.Seconds = clock.Elapsed.TotalSeconds;
            return summary;
        }
        catch (Exception e)
        {
            diagnostics.Seconds = clock.Elapsed.TotalSeconds;
            diagnostics.ExceptionType = e.GetType().FullName;
            diagnostics.ExceptionMessage = e is RetrievalException r ? r.ToString() : null;   // eccezioni non tipizzate (EF, Npgsql, HTTP): solo il tipo, il messaggio potrebbe contenere dati
            throw;
        }
        finally
        {
            diagnostics.Persist(path);
        }
    }
}

/// <summary>Raccolta, per scenario, di tutto ciò che serve a ricostruire l'indicizzazione senza accedere al database di test; <see cref="Persist"/> sovrascrive il file a ogni passo.</summary>
internal sealed class IndexDiagnostics(Func<string> scenario)
{
    public List<ModelReadinessRecord> Readiness { get; } = [];

    public RetrievalRebuildSummary? Summary { get; set; }

    public double? Seconds { get; set; }

    public string? ExceptionType { get; set; }

    public string? ExceptionMessage { get; set; }

    public IReadOnlyList<object> Http { get; set; } = [];

    /// <summary>Righe del database di test lette prima dell'eliminazione (stati d'indice, run, job falliti, batch derivati).</summary>
    public JsonNode? Database { get; set; }

    public JsonNode? AnalysisCalls { get; set; }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Scrittura best-effort e atomica (file temporaneo + sostituzione): un errore di I/O non altera mai l'esito del test né sostituisce l'eccezione originale, e un rapporto buono non resta mai a metà.</summary>
    public void Persist(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var node = new JsonObject
            {
                ["scenario"] = scenario(),
                ["writtenAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["readiness"] = JsonSerializer.SerializeToNode(Readiness, Web),
                ["rebuild"] = Summary is null ? null : JsonSerializer.SerializeToNode(new
                {
                    Summary.Preparations, Summary.Ready, Summary.Failed, Summary.EntriesRemoved, seconds = Seconds,
                    results = Summary.Results?.Select(r => new { r.DocumentVersionId, status = r.Status.ToString(), r.BlockCount, r.EntryCount, r.EmbeddedNow, r.ErrorCode, r.ErrorMessage }),
                }, Web),
                ["exception"] = ExceptionType is null ? null : new JsonObject { ["type"] = ExceptionType, ["message"] = ExceptionMessage },
                ["http"] = JsonSerializer.SerializeToNode(Http, Web),
                ["database"] = Database?.DeepClone(),
                ["analysisCalls"] = AnalysisCalls?.DeepClone(),
            };
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e)
        {
            PersistError = e.GetType().Name;
        }
    }

    /// <summary>Tipo dell'errore di scrittura dell'ultimo <see cref="Persist"/> fallito (null = riuscito): osservabile dai test senza eccezioni.</summary>
    public string? PersistError { get; private set; }
}

/// <summary>
/// Rapporto strutturato delle chiamate al modello dell'archivio grezzo (<c>MafDiagnostics</c>: <c>NNN-request.json</c>, <c>-result.json</c>, <c>-assistant.txt</c>): stadio e vista dei batch di riconciliazione, hash di
/// prompt di sistema e schema, token, finish reason, parsabilità, conteggi e la regola «items solo nelle foglie A». I testi grezzi restano nell'archivio locale (si indicano solo i nomi dei file).
/// </summary>
internal static class AnalysisCallReport
{
    private static readonly Dictionary<string, string> Stages = new(StringComparer.Ordinal)
    {
        ["foglia"] = "leaf", ["riduzione"] = "reduce", ["cross"] = "cross", ["espansione"] = "expand", ["confronto"] = "compare",
    };

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? Field(string text, string label)
    {
        var at = text.IndexOf(label + ": ", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + label.Length + 2;
        var end = text.IndexOf('\n', start);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    private static string TextOf(JsonNode? message) =>
        message?["content"] is JsonArray parts ? string.Concat(parts.Select(p => p?["text"]?.ToString())) : message?["content"]?.ToString() ?? string.Empty;

    public static JsonArray Build(string sessionDirectory, int afterId = 0)
    {
        var calls = new JsonArray();
        if (!Directory.Exists(sessionDirectory))
        {
            return calls;
        }

        foreach (var requestFile in Directory.GetFiles(sessionDirectory, "*-request.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(requestFile)[..3];
            if (int.TryParse(id, out var number) && number <= afterId)
            {
                continue;   // chiamate di uno scenario precedente nella stessa sessione (Single prima di Multi)
            }

            var entry = new JsonObject { ["id"] = id, ["rawRequestFile"] = Path.GetFileName(requestFile) };
            try
            {
                var request = JsonNode.Parse(File.ReadAllText(requestFile)) ?? throw new JsonException("richiesta vuota");
                var body = request["body"] is JsonValue v && v.TryGetValue<string>(out var asText) ? JsonNode.Parse(asText) : request["body"];
                var messages = body?["messages"] as JsonArray ?? [];
                var system = string.Join("\n", messages.Where(m => m?["role"]?.ToString() == "system").Select(TextOf));
                var user = string.Join("\n", messages.Where(m => m?["role"]?.ToString() == "user").Select(TextOf));
                var schema = body?["response_format"]?["json_schema"];
                var stageIt = Field(user, "Tipo di batch");
                var stage = stageIt is not null && Stages.TryGetValue(stageIt, out var mapped) ? mapped : null;
                var rawView = Field(user, "Vista");
                var view = rawView is "A" or "B" or "Cross" ? rawView : null;   // solo valori noti: mai testo libero del documento
                entry["startedAt"] = request["startedAt"]?.ToString();
                entry["phaseGuess"] = request["phaseGuess"]?.ToString();
                entry["model"] = body?["model"]?.ToString();
                entry["temperature"] = body?["temperature"]?.ToString();
                entry["maxOutputTokens"] = (body?["max_completion_tokens"] ?? body?["max_tokens"])?.ToString();
                entry["systemPromptSha256"] = system.Length > 0 ? Sha(system) : null;
                entry["systemPromptChars"] = system.Length;
                entry["schemaName"] = schema?["name"]?.ToString();
                entry["schemaSha256"] = schema is null ? null : Sha(schema.ToJsonString());
                entry["batchStage"] = stage;
                entry["batchStageLabel"] = stage is null ? null : stageIt;
                entry["batchView"] = view;
                entry["entriesInBatch"] = user.Split("<<<VOCE [").Length - 1;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                entry["requestParseError"] = e.GetType().Name;
            }

            var resultFile = requestFile.Replace("-request.json", "-result.json", StringComparison.Ordinal);
            if (File.Exists(resultFile))
            {
                try
                {
                    var result = JsonNode.Parse(File.ReadAllText(resultFile)) ?? throw new JsonException("risultato vuoto");
                    entry["outcome"] = result["outcome"]?.ToString();
                    entry["httpStatus"] = result["httpStatus"]?.DeepClone();
                    entry["finishReason"] = result["finishReason"]?.ToString();
                    entry["durationMs"] = result["totalMs"]?.DeepClone();
                    entry["promptTokens"] = result["usage"]?["prompt_tokens"]?.DeepClone();
                    entry["completionTokens"] = result["usage"]?["completion_tokens"]?.DeepClone();
                    entry["exception"] = result["exception"]?.ToString();
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
                {
                    entry["resultParseError"] = true;
                }
            }

            var assistantFile = requestFile.Replace("-request.json", "-assistant.txt", StringComparison.Ordinal);
            if (File.Exists(assistantFile))
            {
                string text;
                try
                {
                    text = File.ReadAllText(assistantFile);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    entry["assistantReadError"] = e.GetType().Name;
                    calls.Add(entry);
                    continue;
                }

                entry["rawAssistantFile"] = Path.GetFileName(assistantFile);
                entry["assistantChars"] = text.Length;
                entry["assistantSha256"] = Sha(text);
                try
                {
                    var output = JsonNode.Parse(text);
                    entry["jsonParsable"] = true;
                    entry["contract"] = output?["contract"]?.ToString();
                    var items = (output?["items"] as JsonArray)?.Count;
                    entry["itemsCount"] = items;
                    entry["candidatesCount"] = (output?["candidates"] as JsonArray)?.Count;
                    entry["patchesCount"] = (output?["patches"] as JsonArray)?.Count;
                    if (items is > 0 && entry["batchStage"] is not null)
                    {
                        // Replica della regola del validatore (ReconcileAliasComposer): items ammessi solo nelle foglie della vista A.
                        entry["itemsOutsideLeafA"] = !(entry["batchStage"]!.ToString() == "leaf" && entry["batchView"]?.ToString() == "A");
                    }
                }
                catch (JsonException)
                {
                    entry["jsonParsable"] = false;
                }
            }

            calls.Add(entry);
        }

        return calls;
    }
}
