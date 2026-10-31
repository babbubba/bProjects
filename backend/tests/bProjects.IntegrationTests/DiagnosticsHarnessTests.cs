using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// Verifica la diagnostica persistente (DiagnosticsHarness.cs) con un modello HTTP finto, senza DGX: un output JSON non valido, un output
/// formalmente valido ma rifiutato dalla validazione applicativa (con più di 5 violazioni), un timeout, e l'assenza di reasoning_content.
/// Per ciascun caso gli artefatti devono bastare a ricostruire la causa senza rieseguire il test.
/// </summary>
public sealed class DiagnosticsHarnessTests
{
    private sealed class FakeServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/slots", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"id":0,"is_processing":true,"n_ctx":131072,"id_task":7,"prompt":"NON DEVE COMPARIRE"}]""", Encoding.UTF8, "application/json"),
                })
                : respond(request, cancellationToken);
    }

    private static HttpResponseMessage Completion(string content, string reasoning = "RAGIONAMENTO-SEGRETO") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["finish_reason"] = "stop",
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content, ["reasoning_content"] = reasoning },
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 11, ["completion_tokens"] = 22 },
            }.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, bool structured)
    {
        var body = new JsonObject
        {
            ["model"] = "test-model", ["temperature"] = 0, ["max_completion_tokens"] = 123,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = structured },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "PROMPT-DI-SISTEMA" }, new JsonObject { ["role"] = "user", ["content"] = "ciao" }),
        };
        if (structured)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "SCHEMA-X" } };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Read(DiagSession s, string name) => File.ReadAllText(Path.Combine(s.Dir, name));

    [Fact]
    public async Task Invalid_json_output_is_saved_without_reasoning_and_its_rejection_can_be_rebuilt_from_the_artifacts()
    {
        var session = MafDiagnostics.Current();
        using var client = new HttpClient(new DiagnosticHandler(new FakeServer((_, _) => Task.FromResult(Completion("questo non è JSON {"))), session));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "CHIAVE-SEGRETA");

        using var response = await PostAsync(client, structured: true);
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);   // il chiamante consuma il corpo come farebbe l'app

        var request = Read(session, "001-request.json");
        Assert.Contains("PROMPT-DI-SISTEMA", request);
        Assert.Contains("SCHEMA-X", request);
        Assert.Contains("\"enable_thinking\": true", request);
        Assert.DoesNotContain("CHIAVE-SEGRETA", Read(session, "events.jsonl") + request);
        Assert.Equal("questo non è JSON {", Read(session, "001-assistant.txt"));
        Assert.DoesNotContain("RAGIONAMENTO-SEGRETO", Read(session, "001-response.txt"));
        var result = JsonNode.Parse(Read(session, "001-result.json"))!;
        Assert.Equal("completed", result["outcome"]!.ToString());
        Assert.Equal("stop", result["finishReason"]!.ToString());
        Assert.Equal("22", result["usage"]!["completion_tokens"]!.ToString());
        Assert.Contains("\"kind\":\"slots\"", Read(session, "events.jsonl"));
        Assert.DoesNotContain("NON DEVE COMPARIRE", Read(session, "events.jsonl"));

        // Il run fallito nel database: la ri-validazione dell'output salvato restituisce la causa esatta.
        var ids = AcceptanceFixtureTests.NewIds(AcceptanceFixtureTests.Script("N5-progetti-distinti", "boreale"));
        session.Write("db-final.json", new JsonObject
        {
            ["ProcessingRuns"] = new JsonArray(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(), ["Kind"] = "Analysis", ["ProjectId"] = ids.Project.ToString(), ["AnalysisId"] = ids.Analysis.ToString(),
                ["SourceCutoffSequence"] = 10, ["ErrorCode"] = "invalid_agent_output", ["ErrorMessage"] = "(prime 5 violazioni)", ["CompletedAt"] = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
            }),
            ["ConversationMessages"] = new JsonArray(),
        }.ToJsonString());
        MafDiagnostics.Replay(session);

        var replay = Directory.GetFiles(session.Dir, "replay-*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(replay))!;
        Assert.Equal("001", json["call"]!.ToString());
        Assert.False(json["accepted"]!.GetValue<bool>());
        Assert.NotEmpty(json["violations"]!.AsArray());
    }

    [Fact]
    public async Task A_formally_valid_draft_rejected_by_validation_keeps_every_violation_not_only_the_first_five()
    {
        var session = MafDiagnostics.Current();
        var unknown = string.Concat(Enumerable.Range(0, 8).Select(_ => $"- requisito [fonte:{Guid.NewGuid()}]\n"));
        using var client = new HttpClient(new DiagnosticHandler(new FakeServer((_, _) => Task.FromResult(Completion("# Bozza\n\n" + unknown))), session));

        using var response = await PostAsync(client, structured: false);
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var ids = AcceptanceFixtureTests.NewIds(AcceptanceFixtureTests.Script("N5-progetti-distinti", "boreale"));
        var analysis = AcceptanceOracle.BuildAnalysis(AcceptanceFixtureTests.Script("N5-progetti-distinti", "boreale"), ids.Project, ids.Analysis, ids.Users.Length, ids.Users);
        var revision = Guid.NewGuid().ToString();
        session.Write("db-final.json", new JsonObject
        {
            ["ProcessingRuns"] = new JsonArray(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(), ["Kind"] = "Document", ["ProjectId"] = ids.Project.ToString(), ["InputRevisionId"] = revision,
                ["ErrorCode"] = "invalid_agent_output", ["ErrorMessage"] = "(solo le prime 5)", ["CompletedAt"] = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
            }),
            ["AnalysisRevisions"] = new JsonArray(new JsonObject { ["Id"] = revision, ["ContentJson"] = analysis.ToJsonString() }),
        }.ToJsonString());
        MafDiagnostics.Replay(session);

        var json = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(session.Dir, "replay-*.json").Single()))!;
        Assert.False(json["accepted"]!.GetValue<bool>());
        Assert.True(json["violations"]!.AsArray().Count > 5, $"violazioni ricostruite: {json["violations"]!.AsArray().Count}");
        Assert.Contains("fonte", json["violations"]!.AsArray()[0]!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_timeout_leaves_the_request_and_an_exception_record_that_prove_the_call_was_sent_and_never_answered()
    {
        var session = MafDiagnostics.Current();
        var server = new FakeServer(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Completion("mai");
        });
        using var client = new HttpClient(new DiagnosticHandler(server, session)) { Timeout = TimeSpan.FromSeconds(1) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostAsync(client, structured: true));

        var events = Read(session, "events.jsonl");
        Assert.Contains("\"kind\":\"sent\"", events);
        Assert.Contains("\"kind\":\"exception\"", events);
        Assert.DoesNotContain("\"kind\":\"headers\"", events);
        Assert.DoesNotContain("\"kind\":\"completed\"", events);
        var result = JsonNode.Parse(Read(session, "001-result.json"))!;
        Assert.Equal("exception", result["outcome"]!.ToString());
        Assert.True(result["isCancellation"]!.GetValue<bool>());
        Assert.Contains("TaskCanceledException", result["exception"]!.ToString());
        Assert.InRange(result["elapsedMs"]!.GetValue<long>(), 900, 5000);
        Assert.Contains("\"when\":\"after-exception\"", events);   // stato degli slot dopo il timeout: serve a distinguere coda da calcolo
        Assert.True(File.Exists(Path.Combine(session.Dir, "001-request.json")));
    }

    [Fact]
    public async Task A_streamed_answer_is_written_while_it_arrives_without_reasoning_and_without_buffering_the_stream()
    {
        var session = MafDiagnostics.Current();
        static string Sse(string delta) => $"data: {{\"choices\":[{{\"delta\":{delta}}}]}}\n\n";
        var gate = new TaskCompletionSource();
        var server = new FakeServer((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GatedStream(
                Encoding.UTF8.GetBytes(Sse("{\"reasoning_content\":\"PENSIERO-SEGRETO\"}") + Sse("{\"content\":\"Ciao\"}")),
                Encoding.UTF8.GetBytes(Sse("{\"content\":\" mondo\"}") + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"), gate.Task))
            { Headers = { ContentType = new("text/event-stream") } },
        }));
        using var client = new HttpClient(new DiagnosticHandler(server, session));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"m","stream":true,"messages":[{"role":"user","content":"x"}]}""", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);   // prima parte: il resto non è ancora stato inviato

        // Mentre lo stream è ancora aperto, il file contiene già la parte arrivata (scrittura progressiva) e non il reasoning.
        var partial = Read(session, "001-response.txt");
        Assert.Contains("Ciao", partial);
        Assert.DoesNotContain("PENSIERO-SEGRETO", partial);
        Assert.False(File.Exists(Path.Combine(session.Dir, "001-result.json")));   // ancora in corso: nessun esito finale

        gate.SetResult();
        while (await stream.ReadAsync(buffer, TestContext.Current.CancellationToken) > 0)
        {
        }

        Assert.Equal("Ciao mondo", Read(session, "001-assistant.txt"));
        Assert.Equal("stop", JsonNode.Parse(Read(session, "001-result.json"))!["finishReason"]!.ToString());
    }

    /// <summary>Stream di test: consegna la prima parte subito e la seconda solo quando il gate si apre.</summary>
    private sealed class GatedStream(byte[] first, byte[] second, Task gate) : Stream
    {
        private int _phase;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_phase == 1 && _offset == 0)
            {
                await gate.WaitAsync(cancellationToken);
            }

            var source = _phase == 0 ? first : second;
            if (_phase > 1)
            {
                return 0;
            }

            var n = Math.Min(buffer.Length, source.Length - _offset);
            source.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            if (_offset == source.Length)
            {
                _phase++;
                _offset = 0;
            }

            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Snapshot ed export del database con le tabelle reali (PostgreSQL migrato): le query della diagnostica devono funzionare.</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DiagnosticsDatabaseTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task The_live_snapshot_and_the_final_export_read_every_table_the_diagnostics_need()
    {
        TestEnvironment.RequirePostgres();
        await using var app = await ApiHarness.StartAsync(_database.ConnectionString);

        var live = JsonNode.Parse(await MafDiagnostics.ExportLiveAsync(_database.ConnectionString, TestContext.Current.CancellationToken))!;
        foreach (var table in new[] { "InferenceCalls", "ProcessingRuns", "ChatTurns" })
        {
            Assert.IsType<JsonArray>(live[table]);
        }

        var full = JsonNode.Parse(await MafDiagnostics.ExportLiveAsync(_database.ConnectionString, TestContext.Current.CancellationToken, full: true))!;
        foreach (var table in new[] { "ConversationMessages", "AnalysisRevisions" })
        {
            Assert.IsType<JsonArray>(full[table]);
        }
    }
}
