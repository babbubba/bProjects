using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using Microsoft.Extensions.AI;
using Npgsql;

namespace bProjects.IntegrationTests;

// Diagnostica SOLO DELL'HARNESS DEI TEST sulle chiamate di estrazione (T082: perche un'unita ha dato finish=length). Nessun codice di produzione e toccato:
// il wrapper si innesta sul client del modello passato al costruttore PUBBLICO di AgentFactory (stesso punto in cui AgentFactory.Create passa
// InternalModelClient.ChatClient), quindi vede la risposta GREZZA del modello PRIMA di BatchSender, che scarta FinishReason.Length senza persistere.
// Output locale e NON versionato: %TEMP%\bprojects-i5-real\diag\<runId>\ (summary.json, calls.jsonl, texts\*.txt). Mai chiavi API, intestazioni o segreti;
// mai il testo grezzo nel log di test: solo un riepilogo sintetico per chiamata.

/// <summary>Una chiamata al modello osservata dal wrapper (in memoria solo il necessario alla correlazione: testo dei range proprietari, mai il prompt completo).</summary>
internal sealed class CapturedCall
{
    public int Seq { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public string Phase { get; set; } = "";

    public string? ModelRequested { get; set; }

    public string? ModelResponded { get; set; }

    public List<string> RuntimeFingerprints { get; } = [];

    public string PromptSha256 { get; set; } = "";

    public int PromptChars { get; set; }

    public string? SchemaName { get; set; }

    public string? SchemaSha256 { get; set; }

    public int? MaxOutputConfigured { get; set; }

    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }

    public string? FinishReason { get; set; }

    public long DurationMs { get; set; }

    public int OutputChars { get; set; }

    public string? OutputSha256 { get; set; }

    public string? TextFile { get; set; }

    public string? Error { get; set; }

    public bool Streaming { get; set; }

    public bool IsGuidedRetry { get; set; }

    public bool? JsonParsable { get; set; }

    public int? EvidenceCount { get; set; }

    public int? EvidenceCountApprox { get; set; }

    public List<(string Key, bool Owned)> Ranges { get; } = [];

    /// <summary>Testo dei range Primary uniti con "\n" (come la base della stima dell'output del planner); solo in memoria.</summary>
    public string OwnedText { get; set; } = "";

    public int OwnedChars { get; set; }
}

/// <summary>Registratore diagnostico di un run: wrapper del client, scrittura progressiva dei file e correlazione finale col database di test.</summary>
internal sealed partial class ExtractionDiagnosticsRecorder
{
    /// <summary>Moltiplicatore con cui il planner stima l'output (<c>OwnedTokens x OutputExpansion</c>): il default di produzione, congelato nel manifesto.</summary>
    public const double PlannerOutputExpansion = PlannerLimits.DefaultOutputExpansion;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly Regex Range = RangeRegex();
    private static readonly Regex Bearer = BearerRegex();
    private static readonly Regex ConnectionPassword = ConnectionPasswordRegex();

    private readonly object _gate = new();
    private readonly List<CapturedCall> _calls = [];
    private readonly string[] _secrets;
    private int _seq;

    public ExtractionDiagnosticsRecorder(string? root = null, IEnumerable<string?>? secrets = null, string? runId = null)
    {
        RunId = runId ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        Directory = Path.Combine(root ?? DefaultRoot, RunId);
        _secrets = [.. (secrets ?? []).Where(s => !string.IsNullOrEmpty(s) && s!.Length >= 4).Select(s => s!).Distinct()];
        System.IO.Directory.CreateDirectory(TextsDirectory);
    }

    /// <summary>Radice predefinita: FUORI dal repository, dal database di test e da ogni sandbox che i test eliminano (bprojects-archive-*, bprojects-parse-*, ...).</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "bprojects-i5-real", "diag");

    public string RunId { get; }

    public string Directory { get; }

    public string TextsDirectory => Path.Combine(Directory, "texts");

    public IReadOnlyList<CapturedCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Segreti conosciuti dall'ambiente di test (chiave del modello, segreto del parser, password del database): mai nei file.</summary>
    public static IEnumerable<string?> EnvironmentSecrets(string? apiKey = null)
    {
        yield return apiKey;
        yield return Environment.GetEnvironmentVariable(RealParser.SecretVariable);
        if (Environment.GetEnvironmentVariable("BPROJECTS_TEST_POSTGRES") is { Length: > 0 } postgres)
        {
            string? password = null;
            try
            {
                password = new NpgsqlConnectionStringBuilder(postgres).Password;
            }
            catch (ArgumentException)
            {
                // stringa non interpretabile: nessuna password da mascherare
            }

            yield return password;
        }
    }

    /// <summary>Client del modello avvolto dalla cattura; il comportamento di chiamata, risposta ed eccezioni e identico a quello del client interno.</summary>
    public IChatClient Wrap(IChatClient inner) => new ModelCallCaptureChatClient(inner, this);

    /// <summary>Come <see cref="AgentFactory.Create"/> ma con il client del modello avvolto: stessi parametri di produzione, nessun altro cambiamento.</summary>
    public AgentFactory CreateFactory(AiOptions options, InternalModelClient client, ModelRuntimeMonitor? runtime = null, PayloadCalibration? calibration = null) =>
        new(options, Wrap(client.ChatClient), async (text, ct) => await client.CountTokensAsync(text, ct),
            client.CreateChatOptions(temperature: 0, phase: AiPhase.Compaction), runtime, meter: client, calibration: calibration);

    internal CapturedCall Begin(IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool streaming)
    {
        var call = new CapturedCall { Seq = Interlocked.Increment(ref _seq), StartedAt = DateTimeOffset.UtcNow, Streaming = streaming };
        try
        {
            var system = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text);
            var instructions = string.Join("\n", new[] { options?.Instructions }.Concat(system).Where(t => !string.IsNullOrEmpty(t)));
            var userMessages = messages.Where(m => m.Role != ChatRole.System).ToList();
            var schema = options?.ResponseFormat is ChatResponseFormatJson { Schema: { } element } ? element.GetRawText() : null;
            var full = new StringBuilder(instructions).Append('\u0001');
            foreach (var m in userMessages)
            {
                full.Append(m.Role.Value).Append(':').Append(m.Text).Append('\u0002');
            }

            full.Append('\u0001').Append(schema);
            call.PromptSha256 = Sha(full.ToString());
            call.PromptChars = full.Length;
            call.SchemaName = (options?.ResponseFormat as ChatResponseFormatJson)?.SchemaName;
            call.SchemaSha256 = schema is null ? null : Sha(schema);
            call.MaxOutputConfigured = options?.MaxOutputTokens;
            call.ModelRequested = options?.ModelId;

            var userText = string.Join("\n", userMessages.Select(m => m.Text));
            var owned = new List<string>();
            var ownedChars = 0;
            foreach (Match match in Range.Matches(userText))
            {
                var isOwned = match.Groups[2].Value == "Primary";
                call.Ranges.Add((match.Groups[1].Value, isOwned));
                if (isOwned)
                {
                    owned.Add(match.Groups[3].Value);
                    ownedChars += match.Groups[3].Value.Length;
                }
            }

            call.OwnedText = string.Join("\n", owned);
            call.OwnedChars = ownedChars;
            call.IsGuidedRetry = userText.Contains("CORREZIONE (unico nuovo tentativo)", StringComparison.Ordinal);
            call.Phase = call.Ranges.Count > 0 ? "Extraction/DocumentUnit" : "Other/" + (call.SchemaName ?? "no-schema");
        }
        catch (Exception e)
        {
            call.Error = "capture-begin:" + e.GetType().Name;
        }

        return call;
    }

    internal void Complete(CapturedCall call, ChatResponse? response, string text, string? finish, long? inputTokens, long? outputTokens, string? model, IEnumerable<string> fingerprints, Exception? error, long durationMs)
    {
        try
        {
            call.DurationMs = durationMs;
            call.FinishReason = finish;
            call.PromptTokens = inputTokens;
            call.CompletionTokens = outputTokens;
            call.ModelResponded = model;
            call.RuntimeFingerprints.AddRange(fingerprints.Distinct());
            if (error is not null)
            {
                call.Error = Scrub(error.GetType().FullName + ": " + error.Message);
            }

            _ = response;
            call.OutputChars = text.Length;
            call.OutputSha256 = text.Length == 0 ? null : Sha(text);
            AnalyzeOutput(call, text);

            if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase))
            {
                // Il testo completo SOLO per le risposte troncate; per le altre solo lunghezza e SHA-256.
                var file = Path.Combine(TextsDirectory, $"{call.Seq:D4}-{(call.OutputSha256 ?? "empty")[..Math.Min(12, (call.OutputSha256 ?? "empty").Length)]}-finish-length.txt");
                File.WriteAllText(file, Scrub(text), new UTF8Encoding(false));
                call.TextFile = file;
            }

            lock (_gate)
            {
                _calls.Add(call);
                File.AppendAllText(Path.Combine(Directory, "calls.jsonl"), Scrub(ToJson(call, correlation: null).ToJsonString()) + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception e)
        {
            // La diagnostica non altera mai l'esito: al massimo la chiamata resta senza riga.
            call.Error ??= "capture-complete:" + e.GetType().Name;
            lock (_gate)
            {
                _calls.Add(call);
            }
        }
    }

    private static void AnalyzeOutput(CapturedCall call, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            call.JsonParsable = true;
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("evidences", out var evidences) && evidences.ValueKind == JsonValueKind.Array)
            {
                call.EvidenceCount = evidences.GetArrayLength();
            }
        }
        catch (JsonException)
        {
            call.JsonParsable = false;
            call.EvidenceCountApprox = Regex.Matches(text, "\"key\"\\s*:\\s*\"e\\d+\"").Count;
        }
    }

    // ================================================================ correlazione col database

    private sealed record JobRow(Guid Id, Guid PreparationId, Guid? UnitId, string Status, string? ErrorCode, int Attempts, string? RuntimeFingerprint);

    private sealed record BatchRow(Guid OwnerId, string BatchId, string Stage, int Ordinal, int? MeasuredTokens, string Provenance, string InputHash, List<(string Key, bool Owned)> Ranges, List<int> Portions, bool Committed);

    private sealed class CallRow
    {
        public Guid Id { get; init; }

        public Guid JobId { get; init; }

        public int Attempt { get; init; }

        public long Fence { get; init; }

        public string State { get; init; } = "";

        public string? Finish { get; init; }

        public int? Prompt { get; init; }

        public int? Completion { get; init; }

        public int? MaxOut { get; init; }

        public long? Duration { get; init; }

        public DateTimeOffset StartedAt { get; init; }

        public bool Used { get; set; }
    }

    /// <summary>
    /// Correla ogni chiamata catturata col batch responsabile e scrive summary.json; non lancia mai (un errore e riportato nel file). Chiamare nel finally del test,
    /// PRIMA che il database di test venga eliminato. <paramref name="measureOwned"/> misura i token del solo testo proprietario (stessa misura del planner).
    /// Restituisce le righe di riepilogo sintetiche (nessun testo del modello) gia pronte per il log di test.
    /// </summary>
    public async Task<IReadOnlyList<string>> FinalizeAsync(NpgsqlConnection connection, Func<string, CancellationToken, Task<int?>>? measureOwned, TimeSpan? timeout = null)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        var root = new JsonObject { ["runId"] = RunId, ["finalizedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["directory"] = Directory };
        var callNodes = new JsonArray();
        try
        {
            var jobs = await LoadJobsAsync(connection, ct);
            var batches = await LoadBatchesAsync(connection, ct);
            var rows = await LoadCallRowsAsync(connection, ct);
            var ownedCache = new Dictionary<string, int?>();
            root["criterion"] = "chiamata->batch: sequenza (rangeKey, Primary|Context) del prompt == InputIdsJson.ranges di un DerivedBatch stage 'extract' del DocumentJob; "
                + "se piu batch hanno la stessa sequenza (rangeKey ripetute fra unita), si tiene il batch il cui job ha una riga InferenceCalls con stessi token/finish e StartedAt entro 60 s; "
                + "altrimenti la chiamata e riportata 'ambiguous'. La riga InferenceCalls della chiamata (DocumentJobId, Attempt) e scelta per token/finish/vicinanza di StartedAt.";
            root["plannerExpectedOutput"] = $"ceil(OwnedTokens x {PlannerOutputExpansion.ToString(CultureInfo.InvariantCulture)}), OwnedTokens = token del testo dei soli range Primary uniti con \\n (payload senza istruzioni), come DocumentEvidenceAgent.MeasureAsync; "
                + "OwnedTokens non e persistito: e ricostruito dal testo del prompt catturato con il tokenizer del runtime.";

            foreach (var call in Calls.OrderBy(c => c.Seq))
            {
                var correlation = new JsonObject();
                try
                {
                    await CorrelateAsync(call, jobs, batches, rows, measureOwned, ownedCache, correlation, ct);
                }
                catch (Exception e)
                {
                    correlation["error"] = Scrub(e.GetType().Name + ": " + e.Message);
                }

                callNodes.Add(ToJson(call, correlation));
                lines.Add(Summary(call, correlation));
            }

            root["jobs"] = new JsonArray(jobs.Select(j => (JsonNode)new JsonObject
            {
                ["jobId"] = j.Id, ["unitId"] = j.UnitId, ["status"] = j.Status, ["errorCode"] = j.ErrorCode, ["attemptCount"] = j.Attempts, ["runtimeFingerprint"] = j.RuntimeFingerprint,
            }).ToArray());
        }
        catch (Exception e)
        {
            root["finalizeError"] = Scrub(e.GetType().Name + ": " + e.Message);
            lines.Add("diag: correlazione non riuscita (" + e.GetType().Name + "); le chiamate catturate restano in calls.jsonl");
            foreach (var call in Calls.OrderBy(c => c.Seq).Where(c => callNodes.Count == 0))
            {
                callNodes.Add(ToJson(call, null));
            }
        }

        root["callCount"] = callNodes.Count;
        root["truncatedCount"] = Calls.Count(c => string.Equals(c.FinishReason, "length", StringComparison.OrdinalIgnoreCase));
        root["calls"] = callNodes;
        File.WriteAllText(Path.Combine(Directory, "summary.json"), Scrub(root.ToJsonString(Pretty)), new UTF8Encoding(false));
        lines.Insert(0, $"diag run {RunId}: {callNodes.Count} chiamate catturate, {root["truncatedCount"]} con finish=length; file in {Directory}");
        return lines;
    }

    private async Task CorrelateAsync(
        CapturedCall call, List<JobRow> jobs, List<BatchRow> batches, List<CallRow> rows, Func<string, CancellationToken, Task<int?>>? measureOwned,
        Dictionary<string, int?> ownedCache, JsonObject correlation, CancellationToken ct)
    {
        if (call.Ranges.Count == 0)
        {
            correlation["matched"] = false;
            correlation["note"] = "chiamata senza range di unita documentale: nessun DerivedBatch correlabile";
            return;
        }

        var candidates = batches.Where(b => b.Stage == "extract" && b.Ranges.SequenceEqual(call.Ranges)).ToList();
        var matchedBy = "rangeKeys";
        if (candidates.Count > 1)
        {
            var refined = candidates.Where(b => rows.Any(r => r.JobId == b.OwnerId && !r.Used && Matches(r, call))).ToList();
            if (refined.Count >= 1)
            {
                candidates = refined;
                matchedBy = "rangeKeys+inferenceCall";
            }
        }

        if (candidates.Count != 1)
        {
            correlation["matched"] = false;
            correlation["ambiguity"] = candidates.Count == 0 ? "nessun batch con la stessa sequenza di range" : $"{candidates.Count} batch con la stessa sequenza di range e nessuna riga InferenceCalls che li distingua";
            correlation["candidates"] = new JsonArray(candidates.Select(b => (JsonNode)new JsonObject { ["jobId"] = b.OwnerId, ["batchId"] = b.BatchId, ["ordinal"] = b.Ordinal }).ToArray());
            return;
        }

        var batch = candidates[0];
        var job = jobs.FirstOrDefault(j => j.Id == batch.OwnerId);
        var row = rows.Where(r => r.JobId == batch.OwnerId && !r.Used && Matches(r, call)).OrderBy(r => Math.Abs((r.StartedAt - call.StartedAt).TotalSeconds)).FirstOrDefault();
        if (row is not null)
        {
            row.Used = true;
        }

        int? owned = null;
        if (measureOwned is not null && call.OwnedText.Length > 0)
        {
            var key = Sha(call.OwnedText);
            if (!ownedCache.TryGetValue(key, out owned))
            {
                try
                {
                    owned = await measureOwned(call.OwnedText, ct);
                }
                catch (Exception e)
                {
                    correlation["ownedTokensError"] = Scrub(e.GetType().Name + ": " + e.Message);
                }

                ownedCache[key] = owned;
            }
        }

        correlation["matched"] = true;
        correlation["matchedBy"] = matchedBy;
        correlation["jobId"] = batch.OwnerId;
        correlation["unitId"] = job?.UnitId;
        correlation["preparationId"] = job?.PreparationId;
        correlation["jobStatus"] = job?.Status;
        correlation["jobErrorCode"] = job?.ErrorCode;
        correlation["runtimeFingerprintManifest"] = job?.RuntimeFingerprint;
        correlation["batchId"] = batch.BatchId;
        correlation["batchOrdinal"] = batch.Ordinal;
        correlation["batchStage"] = batch.Stage;
        correlation["portions"] = new JsonArray(batch.Portions.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        correlation["rangeCount"] = batch.Ranges.Count;
        correlation["primaryRanges"] = batch.Ranges.Count(r => r.Owned);
        correlation["ownedChars"] = call.OwnedChars;
        correlation["measuredPayloadTokens"] = batch.MeasuredTokens;
        correlation["tokenProvenance"] = batch.Provenance;
        correlation["inputHash"] = batch.InputHash;
        correlation["outputCommitted"] = batch.Committed;
        correlation["ownedTokens"] = owned;
        if (owned is { } o)
        {
            var expected = (int)Math.Ceiling(o * PlannerOutputExpansion);
            correlation["plannerExpectedOutputTokens"] = expected;
            correlation["plannerOutputExpansion"] = PlannerOutputExpansion;
            if (call.CompletionTokens is { } generated && expected > 0)
            {
                correlation["generatedOverExpected"] = Math.Round((double)generated / expected, 3);
                correlation["generatedTokensPerOwnedToken"] = Math.Round((double)generated / o, 3);
            }
        }

        correlation["inferenceCall"] = row is null
            ? null
            : new JsonObject
            {
                ["id"] = row.Id, ["attempt"] = row.Attempt, ["fencingToken"] = row.Fence, ["state"] = row.State, ["finishReason"] = row.Finish, ["promptTokens"] = row.Prompt,
                ["completionTokens"] = row.Completion, ["maxOutputTokensRequested"] = row.MaxOut, ["durationMs"] = row.Duration, ["startedAt"] = row.StartedAt.ToString("O"),
            };
    }

    private static bool Matches(CallRow row, CapturedCall call) =>
        Math.Abs((row.StartedAt - call.StartedAt).TotalSeconds) <= 60
        && (row.Finish is null || call.FinishReason is null || string.Equals(row.Finish, call.FinishReason, StringComparison.OrdinalIgnoreCase))
        && (row.Prompt is null || call.PromptTokens is null || row.Prompt == call.PromptTokens)
        && (row.Completion is null || call.CompletionTokens is null || row.Completion == call.CompletionTokens);

    private static async Task<List<JobRow>> LoadJobsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT j."Id", j."PreparationId", j."UnitId", j."Status"::text, j."ErrorCode", j."AttemptCount", m."RuntimeFingerprint"
            FROM "DocumentJobs" j LEFT JOIN "WorkManifests" m ON m."OwnerKind"::text = 'DocumentJob' AND m."OwnerId" = j."Id"
            WHERE j."Kind"::text = 'ExtractUnit'
            """;
        var list = new List<JobRow>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new JobRow(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return list;
    }

    private static async Task<List<BatchRow>> LoadBatchesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT b."OwnerId", b."BatchId", b."Stage", b."Ordinal", b."MeasuredTokens", b."TokenProvenance", b."InputHash", b."InputIdsJson"::text,
                   EXISTS (SELECT 1 FROM "WorkOutputs" o WHERE o."OwnerKind"::text = b."OwnerKind"::text AND o."OwnerId" = b."OwnerId" AND o."BatchId" = b."BatchId" AND o."Status"::text = 'Committed')
            FROM "DerivedBatches" b WHERE b."OwnerKind"::text = 'DocumentJob' ORDER BY b."OwnerId", b."Ordinal"
            """;
        var list = new List<BatchRow>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ranges = new List<(string, bool)>();
            var portions = new List<int>();
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(7));
                if (document.RootElement.TryGetProperty("ranges", out var array))
                {
                    ranges.AddRange(array.EnumerateArray().Select(r => (r.GetProperty("rangeKey").GetString()!, r.GetProperty("owned").GetBoolean())));
                }

                if (document.RootElement.TryGetProperty("portions", out var ps))
                {
                    portions.AddRange(ps.EnumerateArray().Select(p => p.GetInt32()));
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // InputIdsJson non nel formato del planner: nessun range, il batch non e correlabile
            }

            list.Add(new BatchRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetString(5), reader.GetString(6), ranges, portions, reader.GetBoolean(8)));
        }

        return list;
    }

    private static async Task<List<CallRow>> LoadCallRowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id", "DocumentJobId", "Attempt", "FencingToken", "State"::text, "FinishReason", "PromptTokens", "CompletionTokens", "MaxOutputTokensRequested", "DurationMs", "StartedAt"
            FROM "InferenceCalls" WHERE "DocumentJobId" IS NOT NULL ORDER BY "StartedAt"
            """;
        var list = new List<CallRow>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CallRow
            {
                Id = reader.GetGuid(0), JobId = reader.GetGuid(1), Attempt = reader.GetInt32(2), Fence = reader.GetInt64(3), State = reader.GetString(4),
                Finish = reader.IsDBNull(5) ? null : reader.GetString(5), Prompt = reader.IsDBNull(6) ? null : reader.GetInt32(6), Completion = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                MaxOut = reader.IsDBNull(8) ? null : reader.GetInt32(8), Duration = reader.IsDBNull(9) ? null : reader.GetInt64(9), StartedAt = reader.GetFieldValue<DateTimeOffset>(10),
            });
        }

        return list;
    }

    // ================================================================ serializzazione, hash, mascheramento

    private JsonObject ToJson(CapturedCall c, JsonObject? correlation)
    {
        var node = new JsonObject
        {
            ["seq"] = c.Seq,
            ["startedAtUtc"] = c.StartedAt.ToString("O"),
            ["phase"] = c.Phase,
            ["modelRequested"] = c.ModelRequested,
            ["modelResponded"] = c.ModelResponded,
            ["runtimeFingerprints"] = new JsonArray(c.RuntimeFingerprints.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["promptSha256"] = c.PromptSha256,
            ["promptChars"] = c.PromptChars,
            ["schemaName"] = c.SchemaName,
            ["schemaSha256"] = c.SchemaSha256,
            ["promptTokensReported"] = c.PromptTokens,
            ["maxOutputTokensConfigured"] = c.MaxOutputConfigured,
            ["completionTokens"] = c.CompletionTokens,
            ["finishReason"] = c.FinishReason,
            ["durationMs"] = c.DurationMs,
            ["streaming"] = c.Streaming,
            ["outputChars"] = c.OutputChars,
            ["outputSha256"] = c.OutputSha256,
            ["truncatedTextFile"] = c.TextFile,
            ["isGuidedRetry"] = c.IsGuidedRetry,
            ["jsonParsable"] = c.JsonParsable,
            ["evidenceCount"] = c.EvidenceCount,
            ["evidenceCountApprox"] = c.EvidenceCountApprox,
            ["error"] = c.Error,
            ["ranges"] = new JsonArray(c.Ranges.Select(r => (JsonNode)new JsonObject { ["rangeKey"] = r.Key, ["owned"] = r.Owned }).ToArray()),
        };
        if (correlation is not null)
        {
            node["correlation"] = correlation;
        }

        return node;
    }

    private static string Summary(CapturedCall c, JsonObject correlation)
    {
        string V(string name) => correlation[name]?.ToString() ?? "?";
        var evidences = c.EvidenceCount is { } n ? $"{n}" : c.EvidenceCountApprox is { } a ? $"~{a}(json non parsabile)" : "-";
        var batch = correlation["matched"]?.GetValue<bool>() == true
            ? $"batch#{V("batchOrdinal")}/job {V("jobId")[..Math.Min(8, V("jobId").Length)]} owned={V("ownedTokens")} atteso={V("plannerExpectedOutputTokens")}"
            : $"batch=non correlato ({correlation["ambiguity"] ?? correlation["note"] ?? correlation["error"] ?? "?"})";
        return $"diag call#{c.Seq} {c.Phase}{(c.IsGuidedRetry ? " [retry]" : "")} {batch} generati={c.CompletionTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{c.MaxOutputConfigured?.ToString(CultureInfo.InvariantCulture) ?? "?"} finish={c.FinishReason ?? "?"} evidenze={evidences} "
            + $"sha256={(c.OutputSha256 ?? "-")[..Math.Min(12, (c.OutputSha256 ?? "-").Length)]}{(c.TextFile is null ? "" : " file=" + c.TextFile)}";
    }

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Mascheramento difensivo: segreti noti dell'ambiente, intestazioni Bearer e password di stringhe di connessione. Non altera altro testo.</summary>
    internal string Scrub(string text)
    {
        var result = text;
        foreach (var secret in _secrets)
        {
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        result = Bearer.Replace(result, "Bearer [REDACTED]");
        return ConnectionPassword.Replace(result, "$1=[REDACTED]");
    }

    /// <summary>Misura i token del solo testo proprietario con il preflight del runtime (stessa chiamata del planner); nessuna inferenza, solo tokenizer/template.</summary>
    public static async Task<Func<string, CancellationToken, Task<int?>>> OwnedMeasurerAsync(AgentFactory factory, CancellationToken ct)
    {
        var profile = await factory.Preflight.ResolveProfileAsync(AiPhase.Extraction, ct);
        return async (text, token) =>
        {
            var measure = await factory.Preflight.MeasureAsync(
                new DocumentPayload(AiPhase.Extraction, "", [new ChatMessage(ChatRole.User, text)]), token, profile.Fingerprint);
            return measure.Quality == TokenCountQuality.Unavailable ? null : measure.Tokens;
        };
    }

    [GeneratedRegex(@"<<<INTERVALLO rangeKey=(\S+) ruolo=(Primary|Context)>>>\n(.*?)\n<<<FINE rangeKey=\1>>>", RegexOptions.Singleline)]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=\-]{8,}")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)\b(Password|Pwd)\s*=\s*[^;\s]+")]
    private static partial Regex ConnectionPasswordRegex();
}

/// <summary>
/// Wrapper diagnostico: inoltra ogni chiamata invariata (stessi messaggi, stesse opzioni, stesso token, stessa risposta, stesse eccezioni) e ne registra i metadati.
/// Qualunque errore interno della cattura e ingoiato: la diagnostica non puo cambiare il comportamento del workflow.
/// </summary>
internal sealed class ModelCallCaptureChatClient(IChatClient inner, ExtractionDiagnosticsRecorder recorder) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var call = recorder.Begin(list, options, streaming: false);
        var clock = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(list, options, cancellationToken);
        }
        catch (Exception e)
        {
            recorder.Complete(call, null, "", null, null, null, null, [], e, clock.ElapsedMilliseconds);
            throw;
        }

        string text;
        try
        {
            text = response.Text ?? "";
        }
        catch (Exception)
        {
            text = "";
        }

        recorder.Complete(
            call, response, text, response.FinishReason?.Value, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.ModelId,
            Fingerprints(response.AdditionalProperties, response.RawRepresentation), null, clock.ElapsedMilliseconds);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var call = recorder.Begin(list, options, streaming: true);
        var clock = Stopwatch.StartNew();
        var text = new StringBuilder();
        string? finish = null;
        string? model = null;
        long? input = null;
        long? output = null;
        var fingerprints = new List<string>();
        var done = false;
        await using var updates = base.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }

                    update = updates.Current;
                }
                catch (Exception e)
                {
                    done = true;
                    recorder.Complete(call, null, text.ToString(), finish, input, output, model, fingerprints, e, clock.ElapsedMilliseconds);
                    throw;
                }

                try
                {
                    text.Append(update.Text);
                    finish = update.FinishReason?.Value ?? finish;
                    model = update.ModelId ?? model;
                    foreach (var usage in update.Contents.OfType<UsageContent>())
                    {
                        input = usage.Details.InputTokenCount ?? input;
                        output = usage.Details.OutputTokenCount ?? output;
                    }

                    fingerprints.AddRange(Fingerprints(update.AdditionalProperties, update.RawRepresentation));
                }
                catch (Exception)
                {
                    // cattura best-effort
                }

                yield return update;
            }

            done = true;
            recorder.Complete(call, null, text.ToString(), finish, input, output, model, fingerprints, null, clock.ElapsedMilliseconds);
        }
        finally
        {
            if (!done)
            {
                recorder.Complete(call, null, text.ToString(), finish, input, output, model, fingerprints, new OperationCanceledException("flusso interrotto dal consumatore"), clock.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>Impronta runtime dichiarata dal server (system_fingerprint), se l'adattatore la espone fra le proprieta aggiuntive o nella rappresentazione grezza.</summary>
    private static IEnumerable<string> Fingerprints(AdditionalPropertiesDictionary? properties, object? raw)
    {
        var found = new List<string>();
        try
        {
            if (properties is not null)
            {
                found.AddRange(properties.Where(p => p.Key.Contains("fingerprint", StringComparison.OrdinalIgnoreCase) && p.Value is not null).Select(p => p.Value!.ToString()!));
            }

            if (raw?.GetType().GetProperty("SystemFingerprint")?.GetValue(raw) is string s && s.Length > 0)
            {
                found.Add(s);
            }
        }
        catch (Exception)
        {
            // best-effort
        }

        return found;
    }
}
