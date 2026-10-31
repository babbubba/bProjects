using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Npgsql;

namespace bProjects.IntegrationTests;

// Diagnostica PERSISTENTE dei test con il modello reale (solo harness di test, nessuna modifica all'app).
// Cartella locale NON versionata: variabile BPROJECTS_DIAG_DIR, altrimenti %TEMP%/bprojects-diag. Una sottocartella per test:
//   meta.json / config.json  test, commit, configurazione AI per fase (senza chiave)
//   events.jsonl             eventi progressivi (sent / slots / headers / first-byte / completed / aborted / exception / db / replay)
//   NNN-request.json         richiesta HTTP effettivamente trasmessa (URL, intestazioni senza Authorization, corpo con prompt e schema)
//   NNN-response.txt         risposta del modello senza reasoning_content (SSE riga per riga, appesa man mano)
//   NNN-assistant.txt        testo finale dell'assistente (content), anche se poi scartato dal validatore
//   NNN-result.json          esito della chiamata: stato, tempi, byte, finish_reason, usage oppure eccezione/timeout
//   db-snapshot.json         ProcessingRuns / InferenceCalls / ChatTurns ogni pochi secondi (ultimo stato prima di un'interruzione)
//   db-final.json            export completo prima che il database temporaneo venga eliminato
//   replay-<runId>.json      ri-validazione dell'output rifiutato: TUTTE le violazioni (la colonna dell'app ne conserva 5)
//   layer-trace.json         compattazione: termini osservati negli originali / sintesi / input dopo la compattazione / AnalysisContent
// Contiene conversazioni e output completi: MAI da committare. Nessuna chiave API, nessun reasoning_content.

/// <summary>Una sessione diagnostica per test.</summary>
internal sealed class DiagSession(string dir, string test)
{
    private readonly object _gate = new();
    private int _seq;
    private CancellationTokenSource? _poller;
    private Task? _pollTask;

    public string Dir { get; } = dir;

    public string Test { get; } = test;

    public string? Connection { get; set; }

    public ConcurrentQueue<CallRecord> Calls { get; } = new();

    public List<string> Terms { get; } = [];

    public int NextId() => Interlocked.Increment(ref _seq);

    public void Write(string name, string content)
    {
        lock (_gate)
        {
            File.WriteAllText(Path.Combine(Dir, name), content, new UTF8Encoding(false));
        }
    }

    public void Append(string name, string text)
    {
        lock (_gate)
        {
            File.AppendAllText(Path.Combine(Dir, name), text, new UTF8Encoding(false));
        }
    }

    public void Log(string kind, JsonObject data)
    {
        data["at"] = DateTimeOffset.UtcNow.ToString("O");
        data["kind"] = kind;
        Append("events.jsonl", data.ToJsonString() + "\n");
    }

    public void StartPolling()
    {
        if (Connection is null || _poller is not null)
        {
            return;
        }

        _poller = new CancellationTokenSource();
        var token = _poller.Token;
        var connection = Connection;
        _pollTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Write("db-snapshot.json", await MafDiagnostics.ExportLiveAsync(connection, token));
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Log("db", new JsonObject { ["snapshotError"] = e.GetType().Name + ": " + e.Message });
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        });
    }

    public async Task StopPollingAsync()
    {
        if (_poller is null)
        {
            return;
        }

        await _poller.CancelAsync();
        await _pollTask!;
        _poller = null;
    }
}

/// <summary>Chiamata al modello registrata in memoria (oltre che su file) per la ricostruzione finale.</summary>
internal sealed record CallRecord(string Id, DateTimeOffset StartedAt, string Phase, string RequestText)
{
    public string AssistantText { get; set; } = "";

    public string Status { get; set; } = "sent";
}

internal static class MafDiagnostics
{
    private static readonly ConcurrentDictionary<string, DiagSession> Sessions = new();
    private static readonly ConcurrentDictionary<string, string> PendingConnection = new();
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static string RootDir => Environment.GetEnvironmentVariable("BPROJECTS_DIAG_DIR") is { Length: > 0 } d
        ? d
        : Path.Combine(Path.GetTempPath(), "bprojects-diag");

    public static string CurrentTestName => TestContext.Current.Test?.TestDisplayName ?? "no-test";

    /// <summary>Sessione del test corrente (creata alla prima richiesta). Tutti i dati restano in locale, fuori da Git.</summary>
    public static DiagSession Current()
    {
        var name = CurrentTestName;
        return Sessions.GetOrAdd(name, n =>
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(Path.Combine(RootDir, "LEGGIMI-SOLO-LOCALE.txt"),
                "Artefatti diagnostici dei test: contengono conversazioni e output del modello. Non committare, non condividere.");
            var safe = new string(n.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            var dir = Path.Combine(RootDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{(safe.Length > 60 ? safe[..60] : safe)}-{Guid.NewGuid():N}"[..Math.Min(15 + 1 + 60 + 1 + 8, 15 + 1 + Math.Min(60, safe.Length) + 1 + 8)]);
            Directory.CreateDirectory(dir);
            var session = new DiagSession(dir, n);
            session.Write("meta.json", new JsonObject
            {
                ["test"] = n,
                ["commit"] = GitCommit(),
                ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["dgxModelEnv"] = TestEnvironment.DgxModel,
            }.ToJsonString(Pretty));
            if (PendingConnection.TryGetValue(n, out var connection))
            {
                session.Connection = connection;
                session.StartPolling();
            }

            return session;
        });
    }

    /// <summary>Handler diagnostico davanti al server del modello (reale). Scrive i file man mano che arrivano gli eventi.</summary>
    public static HttpMessageHandler Wrap(HttpMessageHandler? inner = null) =>
        new DiagnosticHandler(inner ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, Current());

    /// <summary>Registra la configurazione AI per fase (modello, thinking, token, timeout, finestra): mai la chiave.</summary>
    public static void RecordConfig(AiOptions options)
    {
        var phases = new JsonObject();
        foreach (var phase in Enum.GetValues<AiPhase>())
        {
            var s = options.ForPhase(phase);
            phases[phase.ToString()] = new JsonObject
            {
                ["model"] = s.ModelId, ["enableThinking"] = s.EnableThinking, ["maxOutputTokens"] = s.MaxOutputTokens,
                ["requestTimeoutSeconds"] = s.RequestTimeoutSeconds, ["inputBudgetTokens"] = s.InputBudgetTokens,
            };
        }

        Current().Write("config.json", new JsonObject
        {
            ["endpointHost"] = Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var u) ? u.Host : "n/d",
            ["contextWindowTokens"] = options.ContextWindowTokens, ["safetyMarginTokens"] = options.SafetyMarginTokens,
            ["compactionTriggerTokens"] = options.EffectiveCompactionTriggerTokens, ["structuredOutputMode"] = options.StructuredOutputMode.ToString(),
            ["analysisRunDeadlineSecondsAppliedByAcceptanceHarness"] = TestEnvironment.ProductionAnalysisRunDeadlineSeconds,
            ["runDeadlineSecondsDefault"] = new ExecutionOptions().RunDeadlineSeconds,
            ["phases"] = phases,
        }.ToJsonString(Pretty));
    }

    /// <summary>Termini da seguire nei quattro strati (originali / sintesi MAF / input dopo la compattazione / AnalysisContent).</summary>
    public static void Watch(params string[] terms) => Current().Terms.AddRange(terms);

    /// <summary>Il fixture del database annuncia la connessione del test: la sessione, se esiste o nascerà, la usa per snapshot ed export.</summary>
    public static void AnnounceDatabase(string connectionString)
    {
        PendingConnection[CurrentTestName] = connectionString;
        if (Sessions.TryGetValue(CurrentTestName, out var session) && session.Connection is null)
        {
            session.Connection = connectionString;
            session.StartPolling();
        }
    }

    /// <summary>Prima di eliminare il database temporaneo: ferma gli snapshot, esporta tutto, riproduce le validazioni, traccia gli strati.</summary>
    public static async Task FinalizeAsync(string connectionString)
    {
        var name = CurrentTestName;
        PendingConnection.TryRemove(name, out _);
        if (!Sessions.TryGetValue(name, out var session))
        {
            return;
        }

        try
        {
            await session.StopPollingAsync();
            session.Write("db-final.json", await ExportLiveAsync(connectionString, CancellationToken.None, full: true));
            session.Log("db", new JsonObject { ["finalExport"] = "ok" });
            Replay(session);
            TraceLayers(session);
        }
        catch (Exception e)
        {
            session.Log("db", new JsonObject { ["finalizeError"] = e.GetType().Name + ": " + e.Message });
        }
    }

    // ------------------------------------------------------------------------------------------------ database

    public static async Task<string> ExportLiveAsync(string connectionString, CancellationToken ct, bool full = false)
    {
        var root = new JsonObject { ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O") };
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var names = new List<string> { "InferenceCalls", "ProcessingRuns", "ChatTurns" };
        if (full)
        {
            names.AddRange(["ConversationMessages", "AnalysisRevisions"]);
            await using var list = new NpgsqlCommand("""SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_name ILIKE '%Draft%'""", connection);
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var table in names)
        {
            try
            {
                await using var command = new NpgsqlCommand($"""SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text FROM "{table}" t""", connection);
                root[table] = JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct))!);
            }
            catch (PostgresException e)
            {
                root[table] = $"errore: {e.SqlState}";
            }
        }

        return root.ToJsonString(Pretty);
    }

    // ------------------------------------------------------------------------------------------------ riproduzione delle validazioni

    internal static void Replay(DiagSession session)
    {
        var db = JsonNode.Parse(File.ReadAllText(Path.Combine(session.Dir, "db-final.json")))!.AsObject();
        var runs = db["ProcessingRuns"] as JsonArray ?? [];
        foreach (var run in runs.OfType<JsonObject>().Where(r => r["ErrorCode"]?.ToString() == AnalysisContentValidator.InvalidAgentOutput))
        {
            var runId = run["Id"]!.ToString();
            var kind = run["Kind"]!.ToString();
            var completed = run["CompletedAt"] is { } c && DateTimeOffset.TryParse(c.ToString(), out var parsed) ? parsed : DateTimeOffset.MaxValue;
            var structured = kind.Contains("Analysis", StringComparison.OrdinalIgnoreCase) || kind == "0";
            var call = session.Calls.Where(x => x.StartedAt <= completed && !string.IsNullOrEmpty(x.AssistantText) && (x.Phase == "structured") == structured && x.Phase is "structured" or "document")
                .OrderBy(x => x.StartedAt).LastOrDefault();
            var result = new JsonObject { ["runId"] = runId, ["kind"] = kind, ["errorMessageInDb"] = run["ErrorMessage"]?.ToString(), ["call"] = call?.Id };
            try
            {
                if (call is null)
                {
                    result["note"] = "nessuna risposta catturata per questo run: output non ricostruibile";
                }
                else if (structured)
                {
                    var projectId = Guid.Parse(run["ProjectId"]!.ToString());
                    var cutoff = run["SourceCutoffSequence"] is { } cut ? long.Parse(cut.ToString()) : long.MaxValue;
                    var messages = (db["ConversationMessages"] as JsonArray ?? []).OfType<JsonObject>()
                        .Where(m => long.Parse(m["Sequence"]!.ToString()) <= cutoff)
                        .Select(m => new AnalysisMessageRef(
                            Guid.Parse(m["Id"]!.ToString()), projectId,
                            string.Equals(m["Role"]!.ToString(), "User", StringComparison.OrdinalIgnoreCase) ? AnalysisMessageRole.User : AnalysisMessageRole.Assistant,
                            long.Parse(m["Sequence"]!.ToString()))).ToList();
                    var analysisId = Guid.Parse((run["AnalysisId"] ?? run["Id"])!.ToString());
                    var validation = AnalysisContentValidator.ValidateText(call.AssistantText, new AnalysisValidationContext(projectId, analysisId, cutoff, messages, new HashSet<Guid>()), AnalysisActorKind.Ai);
                    result["accepted"] = validation.Accepted;
                    result["code"] = validation.Code;
                    result["violations"] = new JsonArray(validation.Violations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
                }
                else
                {
                    var revisionId = run["InputRevisionId"]!.ToString();
                    var revision = (db["AnalysisRevisions"] as JsonArray ?? []).OfType<JsonObject>().Single(r => r["Id"]!.ToString() == revisionId);
                    var content = AnalysisContentJson.Deserialize(revision["ContentJson"]!.ToString());
                    var validation = DocumentDraftValidator.Validate(call.AssistantText, new DocumentValidationContext(new HashSet<Guid>(content.Sources.Select(s => s.Id))));
                    result["accepted"] = validation.Accepted;
                    result["violations"] = new JsonArray(validation.Violations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
                }
            }
            catch (Exception e)
            {
                result["replayError"] = e.GetType().Name + ": " + e.Message;
            }

            session.Write($"replay-{runId}.json", result.ToJsonString(Pretty));
            session.Log("replay", new JsonObject { ["runId"] = runId, ["file"] = $"replay-{runId}.json" });
        }
    }

    // ------------------------------------------------------------------------------------------------ strati della compattazione

    internal static void TraceLayers(DiagSession session)
    {
        if (session.Terms.Count == 0)
        {
            return;
        }

        var db = JsonNode.Parse(File.ReadAllText(Path.Combine(session.Dir, "db-final.json")))!.AsObject();
        var originals = (db["ConversationMessages"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(m => string.Equals(m["Role"]!.ToString(), "User", StringComparison.OrdinalIgnoreCase)).Select(m => m["Body"]!.ToString()).ToList();
        var summaries = session.Calls.Where(c => c.Phase == "compaction").ToList();
        var inputs = session.Calls.Where(c => c.Phase != "compaction").ToList();
        var analysisJson = (db["AnalysisRevisions"] as JsonArray ?? []).OfType<JsonObject>().Select(r => r["ContentJson"]!.ToString()).ToList();

        static bool Has(string text, string term) => text.Contains(term, StringComparison.OrdinalIgnoreCase);
        static string Mark(bool present) => present ? "presente" : "ASSENTE";
        var trace = new JsonObject();
        foreach (var term in session.Terms.Distinct())
        {
            trace[term] = new JsonObject
            {
                ["1_originali_messaggi_pm_con_il_termine"] = $"{originals.Count(o => Has(o, term))}/{originals.Count}",
                ["2_sintesi_maf"] = new JsonArray(summaries.Select(c => (JsonNode)JsonValue.Create($"{c.Id}:{Mark(Has(c.AssistantText, term))}")!).ToArray()),
                ["3_input_inviato_al_modello_dopo_la_compattazione"] = new JsonArray(inputs.Select(c => (JsonNode)JsonValue.Create($"{c.Id}[{c.Phase}]:{Mark(Has(c.RequestText, term))}")!).ToArray()),
                ["4_analysiscontent_prodotto"] = new JsonArray(analysisJson.Select((j, i) => (JsonNode)JsonValue.Create($"rev{i + 1}:{Mark(Has(j, term))}")!).ToArray()),
            };
        }

        session.Write("layer-trace.json", trace.ToJsonString(Pretty));
    }

    private static string GitCommit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = AcceptanceFixtureTests.RepoRoot(),
            })!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return text;
        }
        catch (Exception)
        {
            return "n/d";
        }
    }
}

/// <summary>Registra ogni richiesta/risposta verso il modello man mano che avviene, senza bufferizzare gli stream.</summary>
internal sealed class DiagnosticHandler(HttpMessageHandler inner, DiagSession session) : DelegatingHandler(inner)
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (!path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            var quick = Stopwatch.StartNew();
            try
            {
                var other = await base.SendAsync(request, cancellationToken);
                session.Log("http", new JsonObject { ["path"] = path, ["status"] = (int)other.StatusCode, ["ms"] = quick.ElapsedMilliseconds });
                return other;
            }
            catch (Exception e)
            {
                session.Log("http", new JsonObject { ["path"] = path, ["exception"] = e.GetType().Name, ["ms"] = quick.ElapsedMilliseconds });
                throw;
            }
        }

        var id = session.NextId().ToString("D3");
        var started = DateTimeOffset.UtcNow;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var json = TryParse(body);
        var messages = json?["messages"] as JsonArray ?? [];
        static string TextOf(JsonNode? m) => m?["content"] is JsonArray parts ? string.Concat(parts.Select(p => p?["text"]?.ToString())) : m?["content"]?.ToString() ?? "";
        var systems = messages.Where(m => m?["role"]?.ToString() == "system").Select(TextOf).ToArray();
        var stream = json?["stream"]?.ToString() == "true" || json?["stream"]?.ToString() == "True";
        var phase = systems.Any(s => s.StartsWith(AgentFactory.SummarizationPrompt, StringComparison.Ordinal)) ? "compaction"
            : json?["response_format"] is not null ? "structured" : stream ? "chat" : "document";
        var record = new CallRecord(id, started, phase, string.Join("\n", messages.Select(TextOf)));
        session.Calls.Enqueue(record);

        var headers = new JsonObject();
        foreach (var h in request.Headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
        {
            headers[h.Key] = string.Join(",", h.Value);
        }

        session.Write($"{id}-request.json", new JsonObject
        {
            ["id"] = id, ["startedAt"] = started.ToString("O"), ["method"] = request.Method.Method, ["path"] = path,
            ["phaseGuess"] = phase, ["headers"] = headers, ["body"] = json ?? (JsonNode?)body,
        }.ToJsonString(Pretty));
        session.Log("sent", new JsonObject
        {
            ["id"] = id, ["phase"] = phase, ["model"] = json?["model"]?.ToString(), ["enable_thinking"] = json?["chat_template_kwargs"]?["enable_thinking"]?.ToString(),
            ["temperature"] = json?["temperature"]?.ToString(), ["max_tokens"] = (json?["max_completion_tokens"] ?? json?["max_tokens"])?.ToString(),
            ["stream"] = stream, ["messages"] = messages.Count, ["bodyChars"] = body.Length,
        });
        await LogSlotsAsync(request, id, "at-send", json?["model"]?.ToString());

        var clock = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception e)
        {
            record.Status = "exception";
            session.Write($"{id}-result.json", new JsonObject
            {
                ["id"] = id, ["outcome"] = "exception", ["exception"] = e.GetType().FullName, ["message"] = e.Message,
                ["cancellationRequestedOnToken"] = cancellationToken.IsCancellationRequested,
                ["isCancellation"] = e is OperationCanceledException,
                ["classificationNote"] = "timeout del client e cancellazione del chiamante non sono distinguibili qui: confrontare elapsedMs con il timeout di fase in config.json e con ErrorCode del run nel db",
                ["elapsedMs"] = clock.ElapsedMilliseconds, ["responseBytesReceived"] = 0,
                ["note"] = "nessuna risposta ricevuta prima dell'eccezione (non-stream: il server risponde solo a generazione finita)",
            }.ToJsonString(Pretty));
            session.Log("exception", new JsonObject { ["id"] = id, ["type"] = e.GetType().Name, ["elapsedMs"] = clock.ElapsedMilliseconds, ["cancellationRequestedOnToken"] = cancellationToken.IsCancellationRequested });
            await LogSlotsAsync(request, id, "after-exception", json?["model"]?.ToString());
            throw;
        }

        session.Log("headers", new JsonObject { ["id"] = id, ["status"] = (int)response.StatusCode, ["ttfbMs"] = clock.ElapsedMilliseconds });
        response.Content = new TeeContent(response.Content, new ResponseSink(session, record, id, clock, (int)response.StatusCode));
        return response;
    }

    private async Task LogSlotsAsync(HttpRequestMessage original, string id, string when, string? model)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(original.RequestUri!, "/slots" + (string.IsNullOrEmpty(model) ? "" : "?model=" + Uri.EscapeDataString(model))));
            req.Headers.Authorization = original.Headers.Authorization;
            using var res = await base.SendAsync(req, cts.Token);
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            var slots = new JsonArray();
            if (TryParse(text) is JsonArray array)
            {
                foreach (var slot in array.OfType<JsonObject>())
                {
                    slots.Add(new JsonObject { ["id"] = slot["id"]?.DeepClone(), ["is_processing"] = slot["is_processing"]?.DeepClone(), ["n_ctx"] = slot["n_ctx"]?.DeepClone(), ["id_task"] = slot["id_task"]?.DeepClone(), ["n_prompt_tokens"] = slot["n_prompt_tokens"]?.DeepClone(), ["n_prompt_tokens_processed"] = slot["n_prompt_tokens_processed"]?.DeepClone() });
                }
            }

            session.Log("slots", new JsonObject { ["id"] = id, ["when"] = when, ["status"] = (int)res.StatusCode, ["slots"] = slots });
        }
        catch (Exception e)
        {
            session.Log("slots", new JsonObject { ["id"] = id, ["when"] = when, ["unavailable"] = e.GetType().Name });
        }
    }

    internal static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Scrive la risposta man mano (SSE riga per riga; JSON a risposta completa) senza reasoning_content.</summary>
internal sealed class ResponseSink(DiagSession session, CallRecord record, string id, Stopwatch clock, int status)
{
    private readonly StringBuilder _line = new();
    private readonly MemoryStream _all = new();
    private readonly StringBuilder _assistant = new();
    private long _bytes;
    private bool _first;
    private bool _sse;
    private string? _finish;
    private JsonNode? _usage;
    private bool _ended;
    private bool _done;

    public void SetContentType(string? mediaType) => _sse = mediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;

    public void OnChunk(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0)
        {
            return;
        }

        _bytes += chunk.Length;
        if (!_first)
        {
            _first = true;
            session.Log("first-byte", new JsonObject { ["id"] = id, ["afterMs"] = clock.ElapsedMilliseconds });
        }

        if (!_sse)
        {
            _all.Write(chunk);
            return;
        }

        _line.Append(Encoding.UTF8.GetString(chunk));
        while (true)
        {
            var text = _line.ToString();
            var nl = text.IndexOf('\n');
            if (nl < 0)
            {
                break;
            }

            _line.Remove(0, nl + 1);
            WriteSseLine(text[..nl].TrimEnd('\r'));
        }
    }

    private void WriteSseLine(string line)
    {
        if (line.Trim() == "data: [DONE]")
        {
            _done = true;
        }

        if (line.StartsWith("data:", StringComparison.Ordinal) && DiagnosticHandler.TryParse(line[5..].Trim()) is JsonObject chunk)
        {
            Harvest(chunk);
            line = "data: " + chunk.ToJsonString();
        }

        session.Append($"{id}-response.txt", line + "\n");
    }

    private void Harvest(JsonObject root)
    {
        foreach (var choice in (root["choices"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (var holder in new[] { choice["delta"], choice["message"] }.OfType<JsonObject>())
            {
                holder.Remove("reasoning_content");
                holder.Remove("reasoning");
                if (holder["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                {
                    _assistant.Append(text);
                }
            }

            if (choice["finish_reason"] is JsonValue finish && finish.TryGetValue<string>(out var reason))
            {
                _finish = reason;
            }
        }

        _usage = root["usage"]?.DeepClone() ?? _usage;
    }

    public void End(bool aborted)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        if (!_sse && _all.Length > 0)
        {
            if (DiagnosticHandler.TryParse(Encoding.UTF8.GetString(_all.ToArray())) is JsonObject root)
            {
                Harvest(root);
                session.Write($"{id}-response.txt", root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                session.Write($"{id}-response.txt", $"[corpo non JSON, {_all.Length} byte non persistiti in chiaro per evitare reasoning]");
            }
        }
        else if (_sse && _line.Length > 0)
        {
            WriteSseLine(_line.ToString());
        }

        aborted = aborted && !_done;   // il client smette di leggere dopo [DONE]: non è un'interruzione
        var assistant = _assistant.ToString();
        record.AssistantText = assistant;
        record.Status = aborted ? "aborted" : "completed";
        session.Write($"{id}-assistant.txt", assistant);
        session.Write($"{id}-result.json", new JsonObject
        {
            ["id"] = id, ["outcome"] = aborted ? "aborted-before-end-of-stream" : "completed", ["httpStatus"] = status, ["totalMs"] = clock.ElapsedMilliseconds,
            ["bytes"] = _bytes, ["finishReason"] = _finish, ["usage"] = _usage, ["assistantChars"] = assistant.Length,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        session.Log(aborted ? "aborted" : "completed", new JsonObject { ["id"] = id, ["totalMs"] = clock.ElapsedMilliseconds, ["bytes"] = _bytes, ["finishReason"] = _finish });
    }
}

/// <summary>Contenuto che inoltra lo stream originale (anche SSE, senza bufferizzarlo) copiandolo nel sink.</summary>
internal sealed class TeeContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly ResponseSink _sink;

    public TeeContent(HttpContent inner, ResponseSink sink)
    {
        _inner = inner;
        _sink = sink;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        _sink.SetContentType(inner.Headers.ContentType?.MediaType);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await SerializeToStreamAsync(stream, context, default);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var source = await CreateContentReadStreamAsync(cancellationToken);
        await source.CopyToAsync(stream, cancellationToken);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new TeeStream(await _inner.ReadAsStreamAsync(cancellationToken), _sink);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed class TeeStream(Stream inner, ResponseSink sink) : Stream
{
    private bool _eof;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Observe(inner.Read(buffer, offset, count), buffer.AsSpan(offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n;
        try
        {
            n = await inner.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception)
        {
            sink.End(aborted: true);
            throw;
        }

        return Observe(n, buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Observe(int n, ReadOnlySpan<byte> span)
    {
        if (n > 0)
        {
            sink.OnChunk(span[..n]);
        }
        else if (!_eof)
        {
            _eof = true;
            sink.End(aborted: false);
        }

        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_eof)
            {
                sink.End(aborted: true);   // chiusa prima della fine dello stream: interruzione o cancellazione del chiamante
            }

            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
