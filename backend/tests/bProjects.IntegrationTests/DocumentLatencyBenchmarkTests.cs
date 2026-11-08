using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static bProjects.IntegrationTests.DocumentEvidenceAgentTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// Benchmark circoscritto e riproducibile (T082, diagnosi della latenza fra una chiamata al modello e la successiva): una preparazione con molte unità
/// di estrazione, dispatcher e agente REALI, PostgreSQL reale, modello FINTO a latenza nulla. Con latenza di inferenza zero, il tempo per unità è
/// interamente overhead della pipeline. Gli intervalli di <see cref="DocumentTiming"/> sono raccolti con un <see cref="ActivityListener"/> e ridotti a tempo
/// proprio (self) per fase. Non è un test di correttezza: si esegue solo con <c>BPROJECTS_BENCH=1</c> e stampa la tabella; non asserisce soglie.
/// </summary>
public sealed class DocumentLatencyBenchmarkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Span(string Id, string? ParentId, string Name, DateTime Start, double Ms);

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Per_unit_pipeline_overhead_with_a_zero_latency_model_is_decomposed_by_phase()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BPROJECTS_BENCH") == "1", "Benchmark diagnostico: impostare BPROJECTS_BENCH=1.");
        TestEnvironment.RequirePostgres();
        var units = int.TryParse(Environment.GetEnvironmentVariable("BPROJECTS_BENCH_UNITS"), CultureInfo.InvariantCulture, out var u) ? u : 120;
        const int portionsPerUnit = 12;
        const int wordsPerPortion = 10;

        await using var db = await ParsingDatabase.CreateAsync();
        var seed = await db.SeedParseAsync();
        await SeedUnitsAsync(db, seed, units, portionsPerUnit, wordsPerPortion);

        var spans = new List<Span>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DocumentTiming.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                {
                    spans.Add(new Span(a.Id!, a.ParentId, a.OperationName, a.StartTimeUtc, a.Duration.TotalMilliseconds));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var execution = new ExecutionOptions();
        using var services = new ServiceCollection().AddLogging().AddDbContext<AppDbContext>(o => o.UseNpgsql(db.ConnectionString))
            .AddSingleton<IOptions<ExecutionOptions>>(Options.Create(execution)).AddScoped<ExecutionLeaseStore>().BuildServiceProvider();
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = FakeUnitModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        var model = new FakeUnitModel();
        var counting = new CountingHandler(model, TimeSpan.FromMilliseconds(EnvInt("BPROJECTS_BENCH_HTTP_MS", 0)));
        using var client = new InternalModelClient(options, counting);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var agent = new DocumentEvidenceAgent(scopes, factory, client, Options.Create(new DocumentOptions()));
        var dispatcher = new AiDispatchService(scopes, Options.Create(execution), NullLogger<AiDispatchService>.Instance, unitExecutor: agent);

        var cycleMs = new List<double>();
        var wall = Stopwatch.StartNew();
        var done = 0;
        while (done < units)
        {
            var cycle = Stopwatch.StartNew();
            if (!await dispatcher.RunOnceAsync(Ct))
            {
                break;
            }

            cycleMs.Add(cycle.Elapsed.TotalMilliseconds);
            done++;
        }

        wall.Stop();
        var report = Report(spans, cycleMs, wall.Elapsed.TotalSeconds, done, model.Requests.Count) + counting.Summary(done);
        var path = Path.Combine(Path.GetTempPath(), "bprojects-latency-bench-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        await File.WriteAllTextAsync(path, report, Ct);
        TestContext.Current.SendDiagnosticMessage(report);
        Assert.True(done > 0, "Nessuna unità eseguita dal dispatcher.");
    }

    private static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Conta le richieste HTTP per percorso e, se richiesto, simula la latenza di rete di ogni richiesta (diagnostica: il modello finto non ne ha).</summary>
    private sealed class CountingHandler(HttpMessageHandler inner, TimeSpan delay) : DelegatingHandler(inner)
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _counts = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _counts.AddOrUpdate(request.Method + " " + request.RequestUri!.AbsolutePath, 1, (_, c) => c + 1);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }

        public string Summary(int units)
        {
            var sb = new StringBuilder("richieste HTTP per percorso (totale, per unita):").AppendLine();
            foreach (var (path, count) in _counts.OrderByDescending(c => c.Value))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {path,-32} {count,6} {(double)count / Math.Max(1, units),8:0.0}");
            }

            return sb.ToString();
        }
    }

    private static string Report(List<Span> spans, List<double> cycles, double wallSeconds, int done, int modelCalls)
    {
        var byId = spans.ToDictionary(s => s.Id);
        var childMs = new Dictionary<string, double>();
        foreach (var s in spans.Where(s => s.ParentId is not null && byId.ContainsKey(s.ParentId)))
        {
            childMs[s.ParentId!] = childMs.GetValueOrDefault(s.ParentId!) + s.Ms;
        }

        var groups = spans.GroupBy(s => s.Name).Select(g => new
        {
            Name = g.Key, Count = g.Count(), Total = g.Sum(s => s.Ms), Self = g.Sum(s => s.Ms - childMs.GetValueOrDefault(s.Id)),
        }).OrderByDescending(g => g.Self).ToList();
        cycles.Sort();
        double Q(double p) => cycles.Count == 0 ? 0 : cycles[(int)Math.Floor(p * (cycles.Count - 1))];
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"unità eseguite {done}, chiamate al modello (finto, latenza 0) {modelCalls}, tempo totale {wallSeconds:0.0} s, per unità {wallSeconds / Math.Max(1, done) * 1000:0} ms");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ciclo del dispatcher ms: p50 {Q(.5):0} p90 {Q(.9):0} max {Q(1):0}");
        sb.AppendLine("fase                         n     totale ms   proprio(self) ms   self/unità ms");
        foreach (var g in groups)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{g.Name,-28} {g.Count,5} {g.Total,12:0} {g.Self,18:0} {g.Self / Math.Max(1, done),14:0.0}");
        }

        return sb.ToString();
    }

    private static async Task SeedUnitsAsync(ParsingDatabase db, SeededParse seed, int units, int portionsPerUnit, int wordsPerPortion)
    {
        await using var context = db.NewContext();
        var total = units * portionsPerUnit;
        var artifact = new ParsedArtifact
        {
            Id = Guid.NewGuid(), ProjectId = seed.Project, PreparationId = seed.Preparation, JobId = seed.Job, FencingToken = 1, SchemaVersion = "parsed-1.0.0",
            StorageKey = "k/" + Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64), ByteLength = 10, ParserConfigHash = new string('b', 64),
            BlockCount = total, IssueCount = 0,
        };
        context.ParsedArtifacts.Add(artifact);
        await context.SaveChangesAsync(Ct);

        var blocks = new List<DocumentBlock>(total);
        for (var i = 0; i < total; i++)
        {
            var text = string.Join(' ', Enumerable.Range(0, wordsPerPortion).Select(w => $"u{i}w{w}"));
            blocks.Add(new DocumentBlock
            {
                Id = Guid.NewGuid(), ProjectId = seed.Project, ArtifactId = artifact.Id, Ordinal = i, Ref = $"b{i}", Type = BlockType.Paragraph,
                CanonicalText = text, TextHash = Hash(text), HeadingPathJson = "[]",
            });
        }

        context.DocumentBlocks.AddRange(blocks);
        await context.SaveChangesAsync(Ct);

        var hard = DateTimeOffset.UtcNow.AddHours(2);
        var portions = new List<DocumentPortion>(total);
        for (var unit = 0; unit < units; unit++)
        {
            var job = Guid.NewGuid();
            var group = blocks.Skip(unit * portionsPerUnit).Take(portionsPerUnit).ToList();
            var groupPortions = group.Select((block, k) => new DocumentPortion
            {
                Id = Guid.NewGuid(), ProjectId = seed.Project, PreparationId = seed.Preparation, Ordinal = unit * portionsPerUnit + k, JobId = job,
                RangesJson = JsonSerializer.Serialize(new[] { new { rangeKey = $"r{unit}x{k}", blockId = block.Id, blockRef = block.Ref, start = 0, end = block.CanonicalText.Length, textHash = Hash(block.CanonicalText) } }),
                ContextRangesJson = "[]", TextHash = Hash(block.CanonicalText), Status = PortionStatus.Pending,
            }).ToList();
            context.DocumentJobs.Add(new DocumentJob
            {
                Id = job, ProjectId = seed.Project, PreparationId = seed.Preparation, Kind = DocumentJobKind.ExtractUnit, UnitId = groupPortions[0].Id,
                Status = DocumentJobStatus.Queued, RequestedBy = seed.User, RequestedAt = DateTimeOffset.UtcNow.AddMilliseconds(unit), DeadlineAt = hard, AttemptCount = 0,
                MaxAttempts = 3, FencingToken = 0, ConfigJson = """{"promptVersion":"document-extraction-v3"}""",
            });
            portions.AddRange(groupPortions);
        }

        await context.SaveChangesAsync(Ct);
        context.DocumentPortions.AddRange(portions);
        await context.SaveChangesAsync(Ct);
        await db.ExecuteAsync($"""UPDATE "DocumentPreparations" SET "Status" = 'Extracting' WHERE "Id" = '{seed.Preparation}'""");
    }

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
