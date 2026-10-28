using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

// T052 - percorso nominale completo su cinque casi e prova esplorativa di contesto grande.
//
// Lo stesso percorso (<see cref="AcceptanceRunner"/>: login, creazione, cinque scambi di chat, Analizza, correzione, conferma, Genera,
// riapertura) gira in due modi: con un modello finto "oracolo" (AcceptanceDeterministicTests, Integration: prova provenienza, validazione,
// isolamento, commit e verificatore) e con il DGX Spark reale (AcceptanceMafTests, MafReal: prova la qualita' del modello contro le
// attese annotate). Senza servizi i test risultano Skipped, mai superati. Un errore gestito (es. modello giu', JSON non valido) NON e'
// un caso nominale superato: il percorso termina con un esito negativo. Registrano durate e conteggi, non i contenuti integrali.
//
// T052 NON e' completato da questi test: mancano le revisioni umane (due PM e un tecnico estraneo, specs/001-project-draft/acceptance/).

/// <summary>Cosa il percorso ha osservato per un progetto: esiti verificabili, mai un giudizio di qualita'.</summary>
internal sealed class ProjectRun(ProjectScript script)
{
    public ProjectScript Script { get; } = script;
    public string Name { get; set; } = "";
    public Guid ProjectId { get; set; }
    public Guid AnalysisId { get; set; }
    public Guid ConfirmedRevisionId { get; set; }
    public List<double> ChatSeconds { get; } = [];
    public List<string> Replies { get; } = [];
    public double AnalysisSeconds { get; set; }
    public double DocumentSeconds { get; set; }
    public string? AiJson { get; set; }
    public AnalysisContent? Ai { get; set; }
    public AnalysisContent? Confirmed { get; set; }
    public string? Markdown { get; set; }
    public bool CorrectionApplied { get; set; }
    public string Reached { get; set; } = "creato";
    public Dictionary<Guid, int> UserIndex { get; } = [];
    public HashSet<Guid> MessageIds { get; } = [];
    public AcceptanceFindings Findings { get; } = new();

    public bool Passed => Findings.Passed && Reached == "bozza";

    public void Fail(string message) => Findings.Failures.Add(message);

    public string Summary() =>
        $"{Script.Key}: {(Passed ? "PASS" : "FAIL")} raggiunto={Reached} chat={string.Join("/", ChatSeconds.Select(s => s.ToString("F1")))}s "
        + $"analisi={AnalysisSeconds:F1}s generazione={DocumentSeconds:F1}s fonti={Ai?.Sources.Count.ToString() ?? "n/d"} "
        + $"fallimenti={Findings.Failures.Count} note={Findings.Notes.Count}";
}

/// <summary>Cio' che il percorso richiede all'ambiente: l'app ospitata e come contare le invocazioni del DocumentAgent.</summary>
internal sealed class AcceptanceEnv
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(180);

    public required ApiHarness App { get; init; }
    public required string Connection { get; init; }
    public required Func<int> DocumentCalls { get; init; }
    public Action<Guid, ProjectScript> Register { get; init; } = (_, _) => { };
}

internal static class AcceptanceRunner
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<IReadOnlyList<ProjectRun>> RunCaseAsync(AcceptanceEnv env, AcceptanceCase testCase)
    {
        using var pm = await env.App.LoginAsync();
        var runs = new List<ProjectRun>();
        foreach (var script in testCase.Projects)
        {
            var run = new ProjectRun(script) { Name = $"{script.Name} {Guid.NewGuid():N}"[..Math.Min(80, script.Name.Length + 9)] };
            var created = await pm.PostAsJsonAsync("/api/projects", new { name = run.Name }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            run.ProjectId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            env.Register(run.ProjectId, script);
            runs.Add(run);
        }

        // Chat: i messaggi dei progetti si alternano (nessun progetto vede quelli dell'altro); ogni scambio entro 180 s.
        var messageCount = testCase.Projects.Max(p => p.Messages.Length);
        for (var i = 0; i < messageCount; i++)
        {
            foreach (var run in runs.Where(r => i < r.Script.Messages.Length))
            {
                await ChatExchangeAsync(pm, run, i);
            }
        }

        foreach (var run in runs)
        {
            await AfterChatAsync(env, pm, run);
        }

        foreach (var run in runs)
        {
            if (run.Findings.Failures.Count == 0 || run.Reached == "chat")
            {
                await FormalAsync(env, pm, run);
            }
        }

        if (runs.Count > 1)
        {
            // Isolamento: l'analisi di un progetto non e' raggiungibile dall'altro.
            foreach (var (a, b) in runs.SelectMany(a => runs.Where(b => b != a).Select(b => (a, b))))
            {
                if (a.AnalysisId != Guid.Empty && b.AnalysisId != Guid.Empty)
                {
                    var foreign = await pm.GetAsync($"/api/projects/{a.ProjectId}/analyses/{b.AnalysisId}", Ct);
                    if (foreign.StatusCode != HttpStatusCode.NotFound)
                    {
                        a.Fail($"isolamento: l'analisi di un altro progetto e' raggiungibile ({(int)foreign.StatusCode})");
                    }
                }
            }

            if (runs.All(r => r.Markdown is not null) && runs.Select(r => r.Markdown).Distinct().Count() != runs.Count)
            {
                runs[0].Fail("isolamento: due progetti hanno la stessa bozza");
            }
        }

        Export(testCase, runs);
        return runs;
    }

    // ------------------------------------------------------------------------------------------------ chat

    private static async Task ChatExchangeAsync(HttpClient pm, ProjectRun run, int index)
    {
        var posted = await pm.PostAsJsonAsync($"/api/projects/{run.ProjectId}/messages", new { clientMessageId = Guid.NewGuid(), text = run.Script.Messages[index] }, Ct);
        if (posted.StatusCode != HttpStatusCode.Created)
        {
            run.Fail($"chat {index + 1}: messaggio PM non salvato ({(int)posted.StatusCode})");
            return;
        }

        var turn = (await posted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("chatTurnId").GetGuid();
        var clock = Stopwatch.StartNew();
        var done = await WaitAsync(pm, $"/api/projects/{run.ProjectId}/chat-turns/{turn}");
        clock.Stop();
        run.ChatSeconds.Add(Math.Round(clock.Elapsed.TotalSeconds, 1));
        if (done.GetProperty("status").GetString() != "Completed")
        {
            run.Fail($"chat {index + 1}: turno non completato ({ErrorOf(done)})");
        }
        else if (clock.Elapsed > AcceptanceEnv.Budget)
        {
            run.Fail($"chat {index + 1}: {clock.Elapsed.TotalSeconds:F1}s oltre i 180s");
        }
    }

    private static async Task AfterChatAsync(AcceptanceEnv env, HttpClient pm, ProjectRun run)
    {
        run.Reached = "chat";
        var messages = await MessagesAsync(pm, run.ProjectId);
        var users = messages.Where(m => m.Role == "user").ToList();
        if (!users.Select(m => m.Text).SequenceEqual(run.Script.Messages))
        {
            run.Fail("originali: i messaggi PM salvati non coincidono con quelli inviati");
        }

        foreach (var (m, n) in users.Select((m, n) => (m, n + 1)))
        {
            run.UserIndex[m.Id] = n;
        }

        foreach (var m in messages)
        {
            run.MessageIds.Add(m.Id);
        }

        run.Replies.AddRange(messages.Where(m => m.Role == "assistant").Select(m => m.Text));
        if (run.Replies.Count != run.Script.Messages.Length)
        {
            run.Fail($"chat: attese {run.Script.Messages.Length} risposte, trovate {run.Replies.Count}");
        }

        foreach (var (reply, n) in run.Replies.Select((r, n) => (r, n + 1)))
        {
            if (string.IsNullOrWhiteSpace(reply) || reply.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            {
                run.Fail($"chat {n}: risposta vuota o con ragionamento grezzo");
            }

            if (reply.Count(c => c == '?') > 1)
            {
                run.Fail($"chat {n}: piu' di una domanda nello stesso turno");
            }
        }

        var questions = run.Replies.Select(Question).Where(q => q is not null).ToList();
        if (questions.Count != questions.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            run.Fail("chat: domanda ripetuta");
        }

        // Nessuna elaborazione formale e nessuna invocazione di DocumentAgent dalla sola chat (SC-005).
        if (await ScalarAsync<long>(env, """SELECT COUNT(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p""", ("p", run.ProjectId)) != 0)
        {
            run.Fail("la chat ha avviato un'elaborazione formale");
        }

        if (env.DocumentCalls() != 0)
        {
            run.Fail("la chat ha invocato il DocumentAgent");
        }
    }

    private static string? Question(string reply)
    {
        var end = reply.IndexOf('?');
        if (end < 0)
        {
            return null;
        }

        var start = reply.LastIndexOfAny(['.', '!', '\n'], end) + 1;
        return reply[start..(end + 1)].Trim();
    }

    // ------------------------------------------------------------------------------------------------ Analizza -> correzione -> conferma -> Genera

    private static async Task FormalAsync(AcceptanceEnv env, HttpClient pm, ProjectRun run)
    {
        var project = run.ProjectId;
        var script = run.Script;
        var baseline = env.DocumentCalls(); // altri progetti dello stesso caso possono avere gia' generato

        // --- Analizza
        var analyze = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        analyze.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var clock = Stopwatch.StartNew();
        var accepted = await pm.SendAsync(analyze, Ct);
        if (accepted.StatusCode != HttpStatusCode.Accepted)
        {
            run.Fail($"analisi: richiesta rifiutata ({(int)accepted.StatusCode})");
            return;
        }

        run.AnalysisId = (await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var analysisRun = await WaitAsync(pm, $"/api/projects/{project}/processing-runs/{run.AnalysisId}");
        clock.Stop();
        run.AnalysisSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        if (analysisRun.GetProperty("status").GetString() != "Completed")
        {
            run.Fail($"analisi non riuscita: {ErrorOf(analysisRun)} (un errore gestito non e' un caso nominale superato)");
            run.Reached = "analisi-fallita";
            return;
        }

        if (clock.Elapsed > AcceptanceEnv.Budget)
        {
            run.Fail($"analisi: {clock.Elapsed.TotalSeconds:F1}s oltre i 180s");
        }

        run.Reached = "anteprima";
        var url = $"/api/projects/{project}/analyses/{run.AnalysisId}";
        var preview = await pm.GetAsync(url, Ct);
        var view = await preview.Content.ReadFromJsonAsync<JsonElement>(Ct);
        run.AiJson = view.GetProperty("content").GetRawText();
        try
        {
            run.Ai = AnalysisContentJson.Deserialize(run.AiJson);
        }
        catch (JsonException e)
        {
            run.Fail($"anteprima: JSON non valido ({e.GetType().Name})");
            return;
        }

        if (view.GetProperty("validationState").GetString() != "Unvalidated" || view.GetProperty("canGenerate").GetBoolean() || view.TryGetProperty("markdown", out _))
        {
            run.Fail("anteprima: attesa Unvalidated, senza Genera e senza Markdown");
        }

        run.Findings.Merge(AcceptanceChecker.CheckAnalysis(script, run.Ai, run.UserIndex, run.MessageIds));

        // --- Genera prima della conferma: vietato e senza invocare il DocumentAgent (SC-005)
        var early = await GenerateAsync(pm, project, run.AnalysisId, view.GetProperty("currentRevisionId").GetGuid());
        if (early.StatusCode != HttpStatusCode.Conflict || env.DocumentCalls() != baseline)
        {
            run.Fail($"Genera prima della conferma: atteso 409 senza invocazioni, ricevuto {(int)early.StatusCode} con {env.DocumentCalls() - baseline} invocazioni");
        }

        // --- Correzione del PM (nuova revisione attribuita) e conferma esplicita
        var current = view.GetProperty("currentRevisionId").GetGuid();
        var edited = ApplyCorrection(JsonNode.Parse(run.AiJson)!, script, run);
        if (edited is not null)
        {
            var put = new HttpRequestMessage(HttpMethod.Put, $"{url}/preview") { Content = new StringContent(edited.ToJsonString(), Encoding.UTF8, "application/json") };
            put.Headers.TryAddWithoutValidation("If-Match", $"\"{current}\"");
            var saved = await pm.SendAsync(put, Ct);
            if (saved.StatusCode == HttpStatusCode.OK)
            {
                current = (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("currentRevisionId").GetGuid();
                run.CorrectionApplied = true;
            }
            else
            {
                run.Fail($"correzione del PM rifiutata ({(int)saved.StatusCode})");
            }
        }

        var confirm = new HttpRequestMessage(HttpMethod.Post, $"{url}/confirm");
        confirm.Headers.TryAddWithoutValidation("If-Match", $"\"{current}\"");
        var confirmed = await pm.SendAsync(confirm, Ct);
        if (confirmed.StatusCode != HttpStatusCode.OK)
        {
            run.Fail($"conferma: {(int)confirmed.StatusCode}");
            return;
        }

        run.ConfirmedRevisionId = current;
        var afterConfirm = await pm.GetFromJsonAsync<JsonElement>(url, Ct);
        run.Confirmed = AnalysisContentJson.Deserialize(afterConfirm.GetProperty("content").GetRawText());
        if (run.Confirmed.ValidationState != AnalysisContentState.PmConfirmed || afterConfirm.GetProperty("currentRevisionId").GetGuid() != current)
        {
            run.Fail("conferma: la revisione corrente non risulta PmConfirmed");
        }

        if (run.CorrectionApplied && !run.Confirmed.Sources.Any(s => s.Kind == AnalysisSourceKind.PmRevision))
        {
            run.Fail("correzione: nessuna fonte PmRevision attribuita");
        }

        run.Reached = "confermata";
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // la conferma non genera: nessuna elaborazione o invocazione automatica
        if (env.DocumentCalls() != baseline || await ScalarAsync<long>(env, """SELECT COUNT(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Kind" = 'Document'""", ("p", project)) != 0)
        {
            run.Fail("la conferma ha avviato la generazione");
        }

        // --- Genera
        var callsBefore = env.DocumentCalls();
        clock.Restart();
        var generating = await GenerateAsync(pm, project, run.AnalysisId, current);
        if (generating.StatusCode != HttpStatusCode.Accepted)
        {
            run.Fail($"generazione: richiesta rifiutata ({(int)generating.StatusCode})");
            return;
        }

        var docRunId = (await generating.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var docRun = await WaitAsync(pm, $"/api/projects/{project}/processing-runs/{docRunId}");
        clock.Stop();
        run.DocumentSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        if (docRun.GetProperty("status").GetString() != "Completed")
        {
            run.Fail($"generazione non riuscita: {ErrorOf(docRun)} (un errore gestito non e' un caso nominale superato)");
            run.Reached = "generazione-fallita";
            return;
        }

        if (clock.Elapsed > AcceptanceEnv.Budget)
        {
            run.Fail($"generazione: {clock.Elapsed.TotalSeconds:F1}s oltre i 180s");
        }

        if (env.DocumentCalls() - callsBefore != 1)
        {
            run.Fail($"generazione: attesa una sola invocazione del DocumentAgent, osservate {env.DocumentCalls() - callsBefore}");
        }

        // --- Bozza: legata alla revisione confermata, Proposed/NotApproved, attese rispettate
        var draft = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/draft", Ct);
        run.Markdown = draft.GetProperty("markdown").GetString();
        if (draft.GetProperty("status").GetString() != "Proposed" || draft.GetProperty("approvalStatus").GetString() != "NotApproved"
            || draft.GetProperty("analysisRevisionId").GetGuid() != current || draft.GetProperty("analysisId").GetGuid() != run.AnalysisId
            || draft.GetProperty("processingRunId").GetGuid() != docRunId)
        {
            run.Fail("bozza: attesa Proposed/NotApproved legata alla revisione confermata e al run di generazione");
        }

        run.Reached = "bozza";
        run.Findings.Merge(AcceptanceChecker.CheckDocument(script, run.Markdown ?? "", run.Confirmed, run.CorrectionApplied));

        // --- Riapertura: originali e risultati identici; revisione AI v1 mai modificata; un solo run per fase, una sola bozza
        var again = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/draft", Ct);
        if (again.GetProperty("markdown").GetString() != run.Markdown || again.GetProperty("id").GetGuid() != draft.GetProperty("id").GetGuid())
        {
            run.Fail("riapertura: la bozza non e' identica");
        }

        var v1 = await pm.GetFromJsonAsync<JsonElement>($"{url}/revisions/1", Ct);
        if (v1.GetProperty("origin").GetString() != "Ai" || !JsonNode.DeepEquals(JsonNode.Parse(v1.GetProperty("content").GetRawText()), JsonNode.Parse(run.AiJson)))
        {
            run.Fail("la revisione AI iniziale e' stata modificata");
        }

        var runs = await ScalarAsync<string>(env, """SELECT string_agg("Kind" || ':' || "Status", ',' ORDER BY "RequestedAt") FROM "ProcessingRuns" WHERE "ProjectId" = @p""", ("p", project));
        if (runs != "Analysis:Completed,Document:Completed")
        {
            run.Fail($"elaborazioni formali inattese: {runs}");
        }

        if (await ScalarAsync<long>(env, """SELECT COUNT(*) FROM "Drafts" WHERE "ProjectId" = @p""", ("p", project)) != 1)
        {
            run.Fail("attesa una sola bozza");
        }

        var originals = (await MessagesAsync(pm, project)).Where(m => m.Role == "user").Select(m => m.Text);
        if (!originals.SequenceEqual(script.Messages))
        {
            run.Fail("riapertura: originali PM non identici");
        }
    }

    /// <summary>Corpo del PUT: l'AnalysisContent con la modifica, le integrazioni, l'assunzione e l'eventuale risoluzione (fonti e autore li assegna il server).</summary>
    private static JsonNode? ApplyCorrection(JsonNode content, ProjectScript script, ProjectRun run)
    {
        var correction = script.Correction;
        var changed = false;

        JsonArray Array(Sec s) => s switch
        {
            Sec.Goals => content["goals"]!.AsArray(),
            Sec.Overview => content["contextAndScope"]!["overview"]!.AsArray(),
            Sec.Included => content["contextAndScope"]!["included"]!.AsArray(),
            Sec.Excluded => content["contextAndScope"]!["excluded"]!.AsArray(),
            Sec.Requirements => content["requirements"]!.AsArray(),
            Sec.Flows => content["operationalFlows"]!.AsArray(),
            Sec.Constraints => content["constraints"]!.AsArray(),
            Sec.Dependencies => content["dependencies"]!.AsArray(),
            Sec.Assumptions => content["assumptions"]!.AsArray(),
            _ => content["openQuestions"]!.AsArray(),
        };

        var edit = script.Facts.Single(f => f.Key == correction.EditKey);
        var target = edit.Accept.SelectMany(s => Array(s))
            .FirstOrDefault(i => AcceptanceChecker.Matches(AcceptanceChecker.Norm(i!["text"]!.GetValue<string>()), edit.Match));
        if (target is not null)
        {
            target["text"] = correction.EditedText;
            changed = true;
        }
        else
        {
            run.Fail($"correzione non applicabile: la voce «{edit.Key}» da modificare non e' nell'anteprima");
        }

        foreach (var add in correction.Additions)
        {
            Array(add.Section).Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["text"] = add.Text,
                ["classification"] = add.Class.ToString(),
                ["evidenceRefs"] = new JsonArray(),
            });
            changed = true;
        }

        if (correction.Resolve is { } resolve)
        {
            var spec = script.Conflicts.Single(k => k.Key == resolve.ConflictKey);
            var conflict = content["conflicts"]!.AsArray().FirstOrDefault(k => k!["alternatives"]!.AsArray()
                .Any(a => AcceptanceChecker.Matches(AcceptanceChecker.Norm(a!["text"]!.GetValue<string>()), [spec.AltMatch[0]])));
            var chosen = conflict?["alternatives"]!.AsArray()
                .FirstOrDefault(a => AcceptanceChecker.Matches(AcceptanceChecker.Norm(a!["text"]!.GetValue<string>()), [spec.AltMatch[resolve.Alternative]]));
            if (conflict is null || chosen is null)
            {
                run.Fail($"correzione non applicabile: il conflitto «{resolve.ConflictKey}» non e' nell'anteprima");
            }
            else
            {
                conflict["status"] = "Resolved";
                conflict["resolution"] = new JsonObject
                {
                    ["selectedAlternativeIds"] = new JsonArray(JsonValue.Create(chosen["id"]!.GetValue<string>())),
                    ["reason"] = resolve.Reason,
                    ["evidenceRefs"] = new JsonArray(),
                };
                changed = true;
            }
        }

        return changed ? content : null;
    }

    private static Task<HttpResponseMessage> GenerateAsync(HttpClient pm, Guid project, Guid analysis, Guid revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return pm.SendAsync(request, Ct);
    }

    // ------------------------------------------------------------------------------------------------ supporto

    internal sealed record Message(Guid Id, string Role, string Text);

    internal static async Task<List<Message>> MessagesAsync(HttpClient pm, Guid project)
    {
        var all = new List<Message>();
        long? after = null;
        for (; ; )
        {
            var page = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/messages" + (after is null ? "" : $"?afterSequence={after}"), Ct);
            var items = page.GetProperty("items").EnumerateArray().ToList();
            all.AddRange(items.Select(i => new Message(i.GetProperty("id").GetGuid(), i.GetProperty("role").GetString()!, i.GetProperty("text").GetString()!)));
            var next = page.GetProperty("nextAfterSequence");
            if (items.Count == 0 || next.ValueKind == JsonValueKind.Null || next.GetInt64() == after)
            {
                return all;
            }

            after = next.GetInt64();
        }
    }

    /// <summary>Stato terminale (Completed/Failed) di un turno o di un run, entro 180 s piu' un margine di lettura.</summary>
    internal static async Task<JsonElement> WaitAsync(HttpClient pm, string url)
    {
        var deadline = DateTime.UtcNow + AcceptanceEnv.Budget + TimeSpan.FromSeconds(30);
        JsonElement item = default;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, Ct);
            item = (await pm.GetFromJsonAsync<JsonElement>(url, Ct)).Clone();
            if (item.GetProperty("status").GetString() is "Completed" or "Failed")
            {
                break;
            }
        }

        return item;
    }

    internal static string ErrorOf(JsonElement terminal) =>
        terminal.ValueKind != JsonValueKind.Object || !terminal.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null
            ? $"stato {(terminal.ValueKind == JsonValueKind.Object ? terminal.GetProperty("status").GetString() : "sconosciuto")}, nessun errore"
            : $"{error.GetProperty("code").GetString()}";

    private static async Task<T> ScalarAsync<T>(AcceptanceEnv env, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(env.Connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    /// <summary>Con BPROJECTS_ACCEPTANCE_EXPORT_DIR esporta dialogo, analisi AI, analisi confermata, bozza ed esito per la revisione umana.</summary>
    private static void Export(AcceptanceCase testCase, IReadOnlyList<ProjectRun> runs)
    {
        if (Environment.GetEnvironmentVariable("BPROJECTS_ACCEPTANCE_EXPORT_DIR") is not { Length: > 0 } root)
        {
            return;
        }

        foreach (var run in runs)
        {
            var dir = Path.Combine(root, testCase.Id, run.Script.Key);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "messaggi-pm.md"), string.Join("\n\n", run.Script.Messages.Select((m, i) => $"{i + 1}. {m}")));
            File.WriteAllText(Path.Combine(dir, "risposte-assistente.md"), string.Join("\n\n", run.Replies.Select((m, i) => $"{i + 1}. {m}")));
            if (run.AiJson is not null)
            {
                File.WriteAllText(Path.Combine(dir, "analisi-ai-v1.json"), run.AiJson);
            }

            if (run.Confirmed is not null)
            {
                File.WriteAllText(Path.Combine(dir, "analisi-confermata.json"), AnalysisContentJson.Serialize(run.Confirmed));
            }

            if (run.Markdown is not null)
            {
                File.WriteAllText(Path.Combine(dir, "bozza.md"), run.Markdown);
            }

            File.WriteAllText(Path.Combine(dir, "esito-automatico.txt"),
                run.Summary() + "\n\nFallimenti:\n" + string.Join("\n", run.Findings.Failures.Select(f => "- " + f))
                + "\n\nNote:\n" + string.Join("\n", run.Findings.Notes.Select(n => "- " + n))
                + "\n\nL'esito automatico NON e' il giudizio di accettazione (SC-006): serve la revisione umana.");
        }
    }
}

// ==================================================================================================== modello finto oracolo

/// <summary>
/// Modello finto che risponde come un modello "perfetto" per le attese del caso: costruisce l'analisi dal solo contesto realmente
/// ricevuto (id dei messaggi, analysisId, projectId, cutoff) e la bozza dal solo JSON confermato ricevuto. Serve a provare i NOSTRI
/// componenti (provenienza, validazione, commit, isolamento, verificatore), non la qualita' del modello.
/// </summary>
internal sealed class OracleModel(Func<Guid, ProjectScript?> scripts, OracleOptions? options = null) : IChatClient
{
    private int _chat;
    private int _analysis;
    private int _document;

    public int ChatCalls => Volatile.Read(ref _chat);

    public int AnalysisCalls => Volatile.Read(ref _analysis);

    public int DocumentCalls => Volatile.Read(ref _document);

    /// <summary>Se valorizzato, ogni chiamata di analisi o generazione fallisce con questo errore (guasto del modello).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Se valorizzato, sostituisce la risposta di analisi (output del modello da validare).</summary>
    public Func<string, string>? AnalysisOverride { get; set; }

    private string Reply(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        var all = string.Join("\n", list.Select(m => m.Text));
        if (all.Contains("Produci ora l'unico oggetto JSON AnalysisContent", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _analysis);
            if (Failure is not null)
            {
                throw Failure;
            }

            if (AnalysisOverride is { } over)
            {
                return over(all);
            }

            string Match(string pattern) => Regex.Match(all, pattern).Groups[1].Value;
            var projectId = Guid.Parse(Match(@"projectId: ([0-9a-f-]{36})"));
            var script = scripts(projectId) ?? throw new InvalidOperationException("progetto sconosciuto all'oracolo");
            var users = list.Where(m => m.Role == ChatRole.User && m.Text.StartsWith("[messaggio id=", StringComparison.Ordinal))
                .Select(m => Guid.Parse(Regex.Match(m.Text, @"\[messaggio id=([0-9a-f-]{36})").Groups[1].Value)).ToList();
            return AcceptanceOracle.BuildAnalysis(
                script, projectId, Guid.Parse(Match(@"analysisId: ([0-9a-f-]{36})")), long.Parse(Match(@"sourceCutoffSequence: (\d+)")), users, options).ToJsonString();
        }

        if (all.Contains("revisionId:", StringComparison.Ordinal) && all.Contains("\"schemaVersion\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _document);
            if (Failure is not null)
            {
                throw Failure;
            }

            var json = all[all.IndexOf('{', StringComparison.Ordinal)..(all.LastIndexOf('}') + 1)];
            var content = AnalysisContentJson.Deserialize(json);
            var script = scripts(content.ProjectId) ?? throw new InvalidOperationException("progetto sconosciuto all'oracolo");
            return AcceptanceOracle.RenderMarkdown(content, script.Name);
        }

        // Chat: presa d'atto e, nei primi quattro scambi, al piu' una domanda diversa a ogni turno.
        var n = list.Count(m => m.Role == ChatRole.User);
        Interlocked.Increment(ref _chat);
        return n < 5 ? $"Ho annotato il punto {n}. C'e' altro da precisare sul punto {n}?" : "Ho annotato tutto: se vuoi puoi procedere con Analizza.";
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply(messages))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, Reply(messages));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Conta le invocazioni del modello: il DocumentAgent non deve mai essere invocato senza un comando esplicito.</summary>
internal sealed class CallCounter(IChatClient inner) : DelegatingChatClient(inner)
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

// ==================================================================================================== prove deterministiche (PostgreSQL)

/// <summary>
/// Percorso nominale completo con il modello oracolo su PostgreSQL reale: stesso cablaggio di Program.cs (dispatcher, esecutore di chat,
/// workflow nativi di Analysis e Document, store e commit reali). PASS qui significa "i nostri componenti fanno arrivare le attese
/// alla bozza, con fonti valide, isolamento e nessuna invocazione automatica": NON dimostra la qualita' del modello (MafReal) ne' la
/// comprensibilita' per un tecnico (revisione umana).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AcceptanceDeterministicTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private readonly Dictionary<Guid, ProjectScript> _scripts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private async Task<(ApiHarness App, OracleModel Oracle, CallCounter Documents)> StartAsync(OracleOptions? options = null, Action<OracleModel>? tune = null)
    {
        var oracle = new OracleModel(id => _scripts.GetValueOrDefault(id), options);
        tune?.Invoke(oracle);
        var documents = new CallCounter(oracle);
        var aiOptions = new AiOptions { ContextWindowTokens = 32768 };
        TokenCounter words = (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = "fake", ApiKey = "non-usata" });
        var analysisOptions = client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0);
        var app = await ApiHarness.StartAsync(_database.ConnectionString, configure: services =>
        {
            var agent = new ConversationalAgent(new AgentFactory(aiOptions, oracle, words), new ChatOptions { ModelId = "fake", Temperature = 0.2f });
            services.AddSingleton(agent);
            services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
            services.AddSingleton(sp => new AnalysisWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), agent, analysisOptions));
            services.AddSingleton(new DocumentAgent(new AgentFactory(aiOptions, documents, words), new ChatOptions { ModelId = "fake", Temperature = 0.2f }));
            services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>()));
        });
        return (app, oracle, documents);
    }

    private AcceptanceEnv Env(ApiHarness app, CallCounter documents) => new()
    {
        App = app,
        Connection = _database.ConnectionString,
        DocumentCalls = () => documents.Calls,
        Register = (id, script) => _scripts[id] = script,
    };

    public static IEnumerable<object[]> CaseIds() => AcceptanceCases.NominalIds();

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task The_nominal_path_with_the_oracle_model_reaches_the_annotated_expectations_in_the_draft(string caseId)
    {
        TestEnvironment.RequirePostgres();
        var testCase = AcceptanceCases.Nominal.Single(c => c.Id == caseId);
        var (app, oracle, documents) = await StartAsync();
        await using var _ = app;

        var runs = await AcceptanceRunner.RunCaseAsync(Env(app, documents), testCase);

        foreach (var run in runs)
        {
            TestContext.Current.SendDiagnosticMessage($"T052 deterministico {caseId}/{run.Summary()}");
            Assert.True(run.Passed, $"{caseId}/{run.Script.Key}: " + string.Join("; ", run.Findings.Failures));
            Assert.Equal("bozza", run.Reached);
            Assert.Equal(run.Script.Messages.Length, run.ChatSeconds.Count);
        }

        Assert.Equal(runs.Count, oracle.DocumentCalls);                       // una sola generazione per progetto
        Assert.Equal(runs.Count, documents.Calls);
        Assert.Equal(runs.Count, oracle.AnalysisCalls);                       // una sola analisi per progetto
        Assert.Equal(runs.Sum(r => r.Script.Messages.Length), oracle.ChatCalls);
    }

    [Fact]
    public async Task A_model_failure_during_analysis_is_a_handled_error_and_never_a_passed_nominal_case()
    {
        TestEnvironment.RequirePostgres();
        var (app, oracle, documents) = await StartAsync(tune: o => o.Failure = new HttpRequestException("modello non disponibile", null, HttpStatusCode.Unauthorized));
        await using var _ = app;
        var testCase = AcceptanceCases.Nominal[0];

        var run = Assert.Single(await AcceptanceRunner.RunCaseAsync(Env(app, documents), testCase));

        Assert.False(run.Passed);
        Assert.Equal("analisi-fallita", run.Reached);
        Assert.Contains(run.Findings.Failures, f => f.Contains("un errore gestito non e' un caso nominale superato", StringComparison.Ordinal));
        Assert.Null(run.Markdown);
        Assert.Equal(0, oracle.DocumentCalls);
        // L'errore e' stato gestito bene (originali conservati, nessuna anteprima, nessuna bozza) ma questo non e' un successo.
        Assert.Equal(5, await Count("""SELECT COUNT(*) FROM "ConversationMessages" WHERE "Role" = 'User'"""));
        Assert.Equal(0, await Count("""SELECT COUNT(*) FROM "AnalysisRevisions" """));
        Assert.Equal(0, await Count("""SELECT COUNT(*) FROM "Drafts" """));
    }

    [Fact]
    public async Task An_invalid_model_output_is_rejected_by_the_validator_and_the_case_fails_without_a_preview()
    {
        TestEnvironment.RequirePostgres();
        var (app, oracle, documents) = await StartAsync(tune: o => o.AnalysisOverride = _ => "Ecco l'analisi richiesta: {}");
        await using var _ = app;

        var run = Assert.Single(await AcceptanceRunner.RunCaseAsync(Env(app, documents), AcceptanceCases.Nominal[1]));

        Assert.False(run.Passed);
        Assert.Equal("analisi-fallita", run.Reached);
        Assert.Contains(run.Findings.Failures, f => f.Contains("invalid_agent_output", StringComparison.Ordinal));
        Assert.Equal(0, await Count("""SELECT COUNT(*) FROM "AnalysisRevisions" """));
        Assert.Equal(0, oracle.DocumentCalls);
    }

    [Fact]
    public async Task A_model_that_omits_an_expected_requirement_makes_the_whole_case_fail()
    {
        TestEnvironment.RequirePostgres();
        var (app, _, documents) = await StartAsync(new OracleOptions { DropFact = "agenda" });
        await using var _ = app;

        var run = Assert.Single(await AcceptanceRunner.RunCaseAsync(Env(app, documents), AcceptanceCases.Nominal[0]));

        Assert.False(run.Passed);
        Assert.Contains(run.Findings.Failures, f => f.Contains("omissione: «agenda»", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Text_from_another_project_inside_an_analysis_makes_the_distinct_projects_case_fail()
    {
        TestEnvironment.RequirePostgres();
        var (app, _, documents) = await StartAsync(new OracleOptions { Contaminate = "integrazione in tempo reale con Colorweb per gli ordini" });
        await using var _ = app;

        var runs = await AcceptanceRunner.RunCaseAsync(Env(app, documents), AcceptanceCases.Nominal[4]);

        // La contaminazione e' attribuita al progetto che la subisce (Aurora); il testo e' nel suo vocabolario per Boreale.
        Assert.False(runs[0].Passed);
        Assert.Contains(runs[0].Findings.Failures, f => f.Contains("progetto-boreale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_large_context_case_runs_through_native_compaction_with_originals_unchanged_without_claiming_nominal_success()
    {
        TestEnvironment.RequirePostgres();
        var script = Assert.Single(AcceptanceCases.Exploratory.Projects);
        // Valori di produzione (infra/.env.example) e server finto con tokenizer a un token ogni quattro caratteri: ~185.000 caratteri
        // sono ~46.000 token contro un budget di input di 27.648, quindi la compattazione nativa DEVE attivarsi.
        var options = new AiOptions
        {
            Endpoint = "http://dgx.test/v1/", ApiKey = "chiave-di-prova", ModelId = "modello-finto",
            ContextWindowTokens = 32768, MaxOutputTokens = 4096, SafetyMarginTokens = 1024, CompactionTriggerTokens = 0,
        };
        await using var host = await CompactionHost.StartAsync(_database.ConnectionString, options, new FakeModelServer(), chat: false, analysis: true, prepare: true);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        foreach (var text in script.Messages)
        {
            await CompactionHost.PostAsync(pm, project, text);
        }

        var before = await CompactionHost.OriginalsAsync(_database.ConnectionString);
        var run = await CompactionHost.AnalyzeAsync(pm, project);
        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);

        Assert.Equal("Completed", terminal.GetProperty("status").GetString());
        Assert.NotEmpty(host.Recorder.Summaries);                              // la compattazione nativa si e' attivata
        Assert.All(host.Recorder.Summaries, s => Assert.Equal(200, s.Status));
        var main = Assert.Single(host.Recorder.Main);
        Assert.True(await host.CountSentAsync(main) <= options.InputBudgetTokens);
        Assert.Equal(before, await CompactionHost.OriginalsAsync(_database.ConnectionString));   // originali intatti
        Assert.Equal(script.Messages, before.Select(m => m.Body));
        TestContext.Current.SendDiagnosticMessage(
            $"T052 esplorativo deterministico: messaggi={script.Messages.Length} caratteri={script.Messages.Sum(m => m.Length)} sintesi={host.Recorder.Summaries.Count} "
            + "(server finto: non prova le attese sul contenuto ne' il modello reale)");
    }

    private async Task<long> Count(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}

// ==================================================================================================== prove con il DGX reale

/// <summary>
/// Percorso nominale con il modello reale: chat (5 scambi), Analizza, correzione, conferma, Genera, riapertura, verificati contro le
/// attese annotate di ciascun caso. Servono BPROJECTS_TEST_POSTGRES e BPROJECTS_TEST_DGX_ENDPOINT/API_KEY/MODEL: senza, i test risultano
/// Skipped, mai superati. Un errore o un timeout e' una prova non superata. Il confronto lessicale e' condizione necessaria: la qualita'
/// (SC-006) la valutano due PM e un tecnico estraneo. Con BPROJECTS_ACCEPTANCE_EXPORT_DIR esporta dialogo, analisi e bozza per la revisione.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class AcceptanceMafTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    public static IEnumerable<object[]> CaseIds() => AcceptanceCases.NominalIds();

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task The_real_model_meets_the_annotated_expectations_of_the_nominal_case(string caseId)
    {
        var options = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();
        var testCase = AcceptanceCases.Nominal.Single(c => c.Id == caseId);

        using var model = new InternalModelClient(options);
        var factory = AgentFactory.Create(options, model);
        CallCounter? counter = null;
        await using var app = await ApiHarness.StartAsync(_database.ConnectionString, configure: services =>
        {
            var agent = new ConversationalAgent(factory, model.CreateChatOptions(temperature: 0.2f));
            services.AddSingleton(agent);
            services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
            services.AddSingleton(sp => new AnalysisWorkflow(
                sp.GetRequiredService<IServiceScopeFactory>(), agent, model.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0)));
            counter = new CallCounter(model.ChatClient);
            var documentFactory = new AgentFactory(options, counter, async (t, ct) => await model.CountTokensAsync(t, ct));
            services.AddSingleton(new DocumentAgent(documentFactory, model.CreateChatOptions(temperature: 0.2f)));
            services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>()));
        });

        var env = new AcceptanceEnv { App = app, Connection = _database.ConnectionString, DocumentCalls = () => counter!.Calls };
        var runs = await AcceptanceRunner.RunCaseAsync(env, testCase);

        foreach (var run in runs)
        {
            TestContext.Current.SendDiagnosticMessage($"T052 reale {caseId}/{run.Summary()}");
        }

        Assert.All(runs, run => Assert.True(run.Passed, $"{caseId}/{run.Script.Key}: " + string.Join("; ", run.Findings.Failures)));
    }

    /// <summary>
    /// Prova di LIMITE (non nominale): i dieci messaggi (~185.000 caratteri) del primo test manuale con i valori di produzione. Esiti ammessi e
    /// distinti: (a) analisi riuscita con le attese verificate; (b) errore ESPLICITO <c>context_overflow</c>/<c>compaction_failed</c> con originali intatti.
    /// Nessuno dei due e' un successo nominale ne' certifica la gestione di documenti lunghi: qualunque altro esito (errore generico, omissione
    /// silenziosa, originali alterati) e' un fallimento.
    /// </summary>
    [Fact]
    public async Task The_large_context_exploratory_case_ends_with_a_verified_analysis_or_an_explicit_limit_error_never_silent_loss()
    {
        var services = TestEnvironment.RequireDgxOptions();
        TestEnvironment.RequirePostgres();
        var script = Assert.Single(AcceptanceCases.Exploratory.Projects);
        var options = new AiOptions
        {
            Endpoint = services.Endpoint, ApiKey = services.ApiKey, ModelId = services.ModelId,
            ContextWindowTokens = 32768, MaxOutputTokens = 4096, SafetyMarginTokens = 1024, CompactionTriggerTokens = 0,
        };

        await using var host = await CompactionHost.StartAsync(
            _database.ConnectionString, options, new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, chat: true, analysis: true, prepare: true, requestUsage: true);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;

        var chat = new List<string>();
        foreach (var text in script.Messages)
        {
            var turn = await CompactionHost.PostAsync(pm, project, text);
            var done = await CompactionHost.WaitTurnAsync(pm, project, turn);
            chat.Add(done.GetProperty("status").GetString() == "Completed" ? "Completed" : CompactionHost.ErrorOf(done));
        }

        var known = new[] { "context_overflow", "compaction_failed" };
        Assert.All(chat, c => Assert.True(c == "Completed" || known.Any(k => c.StartsWith(k, StringComparison.Ordinal)), $"turno chat con errore non previsto: {c}"));

        var originals = await CompactionHost.OriginalsAsync(_database.ConnectionString);
        Assert.Equal(script.Messages, originals.Where(m => m.Role == "User").Select(m => m.Body));   // nessuna perdita o troncamento degli originali

        var run = await CompactionHost.AnalyzeAsync(pm, project);
        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);
        string outcome;
        if (terminal.GetProperty("status").GetString() == "Completed")
        {
            var content = AnalysisContentJson.Deserialize(await CompactionHost.ScalarAsync<string>(
                _database.ConnectionString, """SELECT "ContentJson" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", terminal.GetProperty("resultRevisionId").GetGuid())));
            var users = originals.Where(m => m.Role == "User").Select((m, i) => (m.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
            var findings = AcceptanceChecker.CheckAnalysis(script, content, users, originals.Select(m => m.Id).ToHashSet());
            Assert.True(findings.Passed, "attese del caso esplorativo non rispettate: " + string.Join("; ", findings.Failures));
            outcome = "ANALISI RIUSCITA con le attese verificate (prova di limite superata; non certifica documenti lunghi)";
        }
        else
        {
            var code = terminal.GetProperty("error").GetProperty("code").GetString();
            Assert.Contains(code, known);
            Assert.Equal(0, await CompactionHost.ScalarAsync<long>(_database.ConnectionString, """SELECT COUNT(*) FROM "AnalysisRevisions" """));
            outcome = $"LIMITE ESPLICITO ({code}): errore gestito, NON un successo nominale";
        }

        TestContext.Current.SendDiagnosticMessage(
            $"T052 esplorativo reale: messaggi={script.Messages.Length} caratteri={script.Messages.Sum(m => m.Length)} chat=[{string.Join(", ", chat)}] "
            + $"sintesi={host.Recorder.Summaries.Count} esito={outcome}");
    }
}
