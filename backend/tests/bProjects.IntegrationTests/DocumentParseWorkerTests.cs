using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T029 (PG + parser finto): il worker di parsing su PostgreSQL reale, filesystem isolato e un HTTP finto al posto del parser. Provati claim,
/// lease/fence 30/10 s, 3 tentativi con backoff 2/4 s, commit fenced di artefatto+blocchi+porzioni, Failed/Incomplete espliciti, scadenza del
/// padre e assenza di qualunque dipendenza dal modello (nessun servizio AI nel contenitore). NON prova il parser reale: resta NOT VERIFIED finché
/// il servizio parser non esiste (livello PARSER, T026/T031).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentParseWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string VersionJson = """{"schema":"document-parse-1","configHash":"cfg-hash-1","formats":["pdf","docx","md","txt"]}""";

    // ---- rig ---------------------------------------------------------------------------------------------------------------------

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bprojects-parse-" + Guid.NewGuid().ToString("N"));
        private ServiceProvider _services = null!;
        private int _convertCalls;

        public ParsingDatabase Db { get; private set; } = null!;
        public DocumentFileStore Store { get; private set; } = null!;
        public int ConvertCalls => Volatile.Read(ref _convertCalls);
        public string? LastRequestBody { get; private set; }

        /// <summary>Risposta del parser alla conversione; di default 200 con un manifest valido per l'originale seminato.</summary>
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnConvert { get; set; } = (_, _) => throw new InvalidOperationException("parser non programmato");

        public static async Task<Rig> CreateAsync(ExecutionOptions? execution = null, ParserOptions? parser = null)
        {
            var rig = new Rig { Db = await ParsingDatabase.CreateAsync() };
            Directory.CreateDirectory(rig._root);
            rig.Store = new DocumentFileStore(rig._root, [], NullLogger<DocumentFileStore>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(rig.Db.ConnectionString));
            services.AddSingleton(rig.Store);
            services.AddSingleton(Options.Create(execution ?? new ExecutionOptions()));
            services.AddSingleton(Options.Create(new DocumentOptions()));
            var parserOptions = parser ?? new ParserOptions { BaseUrl = "http://parser.test/", Secret = "segreto-di-test" };
            services.AddSingleton(Options.Create(parserOptions));
            services.AddSingleton(new ParserClient(new HttpClient(new Handler(rig)), Options.Create(parserOptions)));
            services.AddScoped<DocumentPolicyService>();
            services.AddScoped<DocumentJobStore>();
            services.AddScoped<DocumentParseRunner>();
            rig._services = services.BuildServiceProvider();
            return rig;
        }

        public async Task<ParseRunOutcome?> RunAsync(string owner = "worker-1", CancellationToken? token = null)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DocumentParseRunner>().RunNextAsync(owner, token ?? Ct);
        }

        public IServiceProvider Services => _services;

        public Task<SeededParse> SeedAsync(byte[]? content = null, int maxAttempts = 3) => Db.SeedParseAsync(Store, content, maxAttempts: maxAttempts);

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Db.DisposeAsync();
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed class Handler(Rig rig) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(VersionJson, Encoding.UTF8, "application/json") };
                }

                Interlocked.Increment(ref rig._convertCalls);
                rig.LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return await rig.OnConvert(request, cancellationToken);
            }
        }
    }

    // ---- manifest valido per l'originale seminato -------------------------------------------------------------------------------------

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JsonObject Block(string reference, int ordinal, string text, int line) => new()
    {
        ["ref"] = reference, ["order"] = ordinal, ["parentRef"] = null, ["type"] = ordinal == 0 ? "heading" : "paragraph", ["text"] = text,
        ["headingPath"] = new JsonArray(), ["locator"] = new JsonObject { ["lineStart"] = line, ["lineEnd"] = line },
    };

    private static JsonObject Range(string key, string block, string text) => new()
    {
        ["rangeKey"] = key, ["blockRef"] = block, ["start"] = 0, ["end"] = text.Length, ["ownership"] = "Primary", ["sourceTextSha256"] = Sha(text),
    };

    private static string Manifest(SeededParse seed, Action<JsonObject>? mutate = null)
    {
        const string t0 = "Titolo";
        const string t1 = "Testo del documento.";
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "document-parse-1", ["originalSha256"] = seed.Sha256, ["parserVersion"] = "1.0.0", ["dependencyLockHash"] = "lock",
            ["modelArtifactsHash"] = "model", ["configHash"] = "cfg-hash-1", ["status"] = "Complete", ["originFormat"] = "txt",
            ["originalByteLength"] = seed.ByteLength, ["pageCount"] = null, ["offsetConvention"] = "unicode-codepoint",
            ["document"] = new JsonObject { ["serialization"] = "text-1", ["content"] = Encoding.UTF8.GetString(seed.Content) },
            ["blocks"] = new JsonArray(Block("b0", 0, t0, 1), Block("b1", 1, t1, 3)),
            ["inventory"] = new JsonArray(new JsonObject { ["kind"] = "item", ["state"] = "represented", ["blockRefs"] = new JsonArray("b0", "b1"), ["issueRefs"] = new JsonArray() }),
            ["chunks"] = new JsonArray(
                new JsonObject { ["ranges"] = new JsonArray(Range("r0", "b0", t0)) },
                new JsonObject { ["ranges"] = new JsonArray(Range("r1", "b1", t1), With(Range("rc", "b0", t0), x => x["ownership"] = "Context")) }),
            ["issues"] = new JsonArray(),
        };
        mutate?.Invoke(manifest);
        return manifest.ToJsonString();
    }

    private static JsonObject With(JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private static HttpResponseMessage Status(int code, string body = "{}") => new((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<HttpResponseMessage> Respond(HttpResponseMessage response) => Task.FromResult(response);

    private Task<string> Str(Rig rig, string sql) => rig.Db.ScalarAsync<string>(sql);

    // ---- percorso felice ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_parse_is_claimed_converted_validated_and_published_under_the_fence()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        var manifest = Manifest(seed);
        rig.OnConvert = (_, _) => Respond(Ok(manifest));

        var outcome = await rig.RunAsync();

        Assert.Equal(ParseRunOutcome.Published, outcome);
        Assert.Contains("Testo del documento.", rig.LastRequestBody, StringComparison.Ordinal);   // i byte dell'originale sono stati trasmessi
        Assert.Equal("Extracting", await Str(rig, $"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));   // Parsed non è Ready
        Assert.Equal("Completed", await Str(rig, $"SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(1, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(1, await rig.Db.ScalarAsync<int>($"SELECT \"FencingToken\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>($"SELECT count(*) FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}' AND \"LeaseOwner\" IS NULL AND \"ResultHash\" IS NOT NULL"));

        await using var db = rig.Db.NewContext();
        var artifact = await db.ParsedArtifacts.AsNoTracking().SingleAsync(Ct);
        var bytes = Encoding.UTF8.GetBytes(manifest);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256);
        Assert.Equal(bytes.Length, artifact.ByteLength);
        Assert.Equal(1, artifact.FencingToken);
        Assert.Equal(seed.Job, artifact.JobId);
        await using (var stored = await rig.Store.OpenVerifiedAsync(artifact.StorageKey, artifact.Sha256, artifact.ByteLength, Ct))
        {
            using var copy = new MemoryStream();
            await stored.CopyToAsync(copy, Ct);
            Assert.Equal(bytes, copy.ToArray());   // manifest + Docling originale conservati byte per byte
        }

        var blocks = await db.DocumentBlocks.AsNoTracking().Where(b => b.ArtifactId == artifact.Id).OrderBy(b => b.Ordinal).ToListAsync(Ct);
        Assert.Equal(["b0", "b1"], blocks.Select(b => b.Ref));
        Assert.Equal(3, blocks[1].LineStart);
        var portions = await db.DocumentPortions.AsNoTracking().Where(p => p.PreparationId == seed.Preparation).OrderBy(p => p.Ordinal).ToListAsync(Ct);
        Assert.Equal(2, portions.Count);
        Assert.All(portions, p => Assert.Equal(PortionStatus.Pending, p.Status));
        Assert.Contains(blocks[1].Id.ToString(), portions[1].RangesJson, StringComparison.Ordinal);
        Assert.Contains(blocks[0].Id.ToString(), portions[1].ContextRangesJson, StringComparison.Ordinal);   // header ripetuto: contesto, mai proprietario
        Assert.DoesNotContain(blocks[0].Id.ToString(), portions[1].RangesJson, StringComparison.Ordinal);
        var preparation = await db.DocumentPreparations.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("1.0.0", preparation.ParserVersion);
        Assert.Equal("cfg-hash-1", preparation.ConfigHash);
        Assert.Equal(artifact.Sha256, preparation.ManifestHash);
        Assert.Null(preparation.CurrentEvidenceVersionId);
        Assert.Empty(await db.PreparationIssues.ToListAsync(Ct));
    }

    [Fact]
    public async Task An_incomplete_manifest_is_published_as_incomplete_with_its_issues_never_as_ready()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        var manifest = Manifest(seed, m =>
        {
            m["status"] = "Incomplete";
            m["issues"] = new JsonArray(new JsonObject { ["issueRef"] = "i1", ["code"] = "image_unprocessed", ["severity"] = "blocking", ["blockRef"] = "b1", ["reason"] = "Immagine non trattata", ["affectsCompleteness"] = true });
            m["inventory"]!.AsArray().Add(new JsonObject { ["kind"] = "item", ["state"] = "unprocessed", ["blockRefs"] = new JsonArray(), ["issueRefs"] = new JsonArray("i1") });
        });
        rig.OnConvert = (_, _) => Respond(Ok(manifest));

        var outcome = await rig.RunAsync();

        Assert.Equal(ParseRunOutcome.PublishedIncomplete, outcome);
        Assert.Equal("Incomplete", await Str(rig, $"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Equal("Completed", await Str(rig, $"SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));   // la conversione è terminata: è la preparazione a non essere completa
        await using var db = rig.Db.NewContext();
        var issue = await db.PreparationIssues.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("image_unprocessed", issue.Code);
        Assert.True(issue.AffectsCompleteness);
        Assert.NotNull(issue.BlockId);
        Assert.Equal(1, await db.ParsedArtifacts.CountAsync(Ct));
    }

    // ---- errori e tentativi -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Transient_parser_errors_back_off_two_then_four_seconds_with_the_same_deadline_and_succeed_on_the_third_attempt()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        var manifest = Manifest(seed);
        var deadline = await Str(rig, $"SELECT \"DeadlineAt\"::text FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'");
        rig.OnConvert = (_, _) => Respond(rig.ConvertCalls < 3 ? Status(503, "{\"code\":\"parser_crashed\"}") : Ok(manifest));

        Assert.Equal(ParseRunOutcome.Requeued, await rig.RunAsync());
        var first = await rig.Db.ScalarAsync<double>($"SELECT extract(epoch FROM (\"NextAttemptAt\" - clock_timestamp())) FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'");
        Assert.InRange(first, 0.5, 2.1);   // backoff dopo il primo tentativo: 2 s
        Assert.Null(await rig.RunAsync());   // non ancora dovuto: nessun reset né nuovo claim
        Assert.Equal("Queued", await Str(rig, $"SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));

        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(ParseRunOutcome.Requeued, await rig.RunAsync());
        var second = await rig.Db.ScalarAsync<double>($"SELECT extract(epoch FROM (\"NextAttemptAt\" - clock_timestamp())) FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'");
        Assert.InRange(second, 2.5, 4.1);   // dopo il secondo: 4 s

        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync());

        Assert.Equal(3, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(3, await rig.Db.ScalarAsync<int>($"SELECT \"FencingToken\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(deadline, await Str(rig, $"SELECT \"DeadlineAt\"::text FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));   // stessa deadline: nessun reset
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"ParsedArtifacts\""));
    }

    [Fact]
    public async Task Three_transient_failures_end_in_an_explicit_failed_job_and_preparation_without_any_artifact()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => Respond(Status(503));

        Assert.Equal(ParseRunOutcome.Requeued, await rig.RunAsync());
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"NextAttemptAt\" = clock_timestamp() WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(ParseRunOutcome.Requeued, await rig.RunAsync());
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"NextAttemptAt\" = clock_timestamp() WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(ParseRunOutcome.Failed, await rig.RunAsync());

        Assert.Equal("attempts_exhausted", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal("Failed", await Str(rig, $"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Equal("attempts_exhausted", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"ParsedArtifacts\""));
        Assert.Null(await rig.RunAsync());   // terminale: nessun nuovo tentativo automatico
        Assert.Equal(3, rig.ConvertCalls);
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>($"SELECT count(*) FROM \"DocumentVersions\" WHERE \"Id\" = '{seed.Version}'"));   // l'originale non viene toccato
    }

    [Theory]
    [InlineData(409, "{\"code\":\"parser_config_mismatch\"}", "parser_config_mismatch")]
    [InlineData(422, "{\"code\":\"parse_limit_exceeded\"}", "parse_limit_exceeded")]
    [InlineData(422, "{}", "parse_input_invalid")]
    [InlineData(415, "{}", "parser_unsupported_format")]
    public async Task Permanent_parser_errors_fail_at_once_with_their_stable_code(int status, string body, string code)
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => Respond(Status(status, body));

        Assert.Equal(ParseRunOutcome.Failed, await rig.RunAsync());

        Assert.Equal(code, await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(code, await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Equal(1, rig.ConvertCalls);
    }

    [Fact]
    public async Task A_hostile_manifest_fails_permanently_and_leaves_no_artifact_file_or_row()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => Respond(Ok(Manifest(seed, m => m["originalSha256"] = new string('0', 64))));

        Assert.Equal(ParseRunOutcome.Failed, await rig.RunAsync());

        Assert.Equal("manifest_hash_mismatch", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT (SELECT count(*) FROM \"ParsedArtifacts\") + (SELECT count(*) FROM \"DocumentBlocks\")"));
        Assert.Single(rig.Store.EnumerateObjects());   // solo l'originale
    }

    [Fact]
    public async Task A_tampered_original_is_never_sent_to_the_parser()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => throw new InvalidOperationException("il parser non deve essere chiamato");
        var file = FindOriginal(rig, seed);
        File.SetAttributes(file, FileAttributes.Normal);
        await File.WriteAllBytesAsync(file, "alterato"u8.ToArray(), Ct);

        Assert.Equal(ParseRunOutcome.Failed, await rig.RunAsync());

        Assert.Equal("original_integrity_failed", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(0, rig.ConvertCalls);
    }

    private static string FindOriginal(Rig rig, SeededParse seed)
    {
        var rootField = typeof(DocumentFileStore).GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var root = (string)rootField.GetValue(rig.Store)!;
        return Path.Combine(root, "objects", seed.StorageKey.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task An_output_above_the_frozen_derived_limit_is_a_permanent_failure()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"ConfigJson\" = '{{\"ParseTimeoutSeconds\":300,\"MaxPages\":1000,\"MaxBlocks\":100000,\"MaxExpandedBytes\":250000000,\"MaxDerivedBytes\":50}}' WHERE \"Id\" = '{seed.Job}'");
        rig.OnConvert = (_, _) => Respond(Ok(Manifest(seed)));

        Assert.Equal(ParseRunOutcome.Failed, await rig.RunAsync());

        Assert.Equal("parse_output_too_large", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"ParsedArtifacts\""));
    }

    // ---- fence, lease, scadenze -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_late_result_from_an_owner_that_lost_the_fence_publishes_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = async (_, _) =>
        {
            // Mentre il parser lavora un altro worker riprende il job: lease scaduto e fence incrementato.
            await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"LeaseOwner\" = 'altro', \"LeaseUntil\" = clock_timestamp() - interval '1 second', \"FencingToken\" = \"FencingToken\" + 1 WHERE \"Id\" = '{seed.Job}'");
            return Ok(Manifest(seed));
        };

        Assert.Equal(ParseRunOutcome.Rejected, await rig.RunAsync());

        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"ParsedArtifacts\""));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"DocumentBlocks\""));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"DocumentPortions\""));
        Assert.Equal("Running", await Str(rig, $"SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));   // lo stato del nuovo owner non è toccato
        Assert.Equal("altro", await Str(rig, $"SELECT \"LeaseOwner\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal("Parsing", await Str(rig, $"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Single(rig.Store.EnumerateObjects());   // il file dell'artefatto scritto dall'owner scaduto è stato rimosso
    }

    [Fact]
    public async Task Losing_the_lease_mid_conversion_cancels_the_parser_call_and_publishes_nothing()
    {
        await using var rig = await Rig.CreateAsync(new ExecutionOptions { LeaseDurationSeconds = 3, RenewIntervalSeconds = 1 });
        var seed = await rig.SeedAsync();
        var cancelled = new TaskCompletionSource();
        rig.OnConvert = async (_, token) =>
        {
            await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"LeaseOwner\" = 'altro', \"LeaseUntil\" = clock_timestamp() + interval '30 seconds', \"FencingToken\" = \"FencingToken\" + 1 WHERE \"Id\" = '{seed.Job}'");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return Ok(Manifest(seed));
        };
        var started = DateTime.UtcNow;

        Assert.Equal(ParseRunOutcome.Rejected, await rig.RunAsync());

        Assert.True(cancelled.Task.IsCompleted, "la chiamata al parser doveva essere annullata alla perdita del lease");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"ParsedArtifacts\""));
        Assert.Equal("altro", await Str(rig, $"SELECT \"LeaseOwner\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
    }

    [Fact]
    public async Task The_lease_is_renewed_during_a_long_conversion_so_no_other_worker_can_take_the_job()
    {
        await using var rig = await Rig.CreateAsync(new ExecutionOptions { LeaseDurationSeconds = 3, RenewIntervalSeconds = 1 });
        var seed = await rig.SeedAsync();
        rig.OnConvert = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4.5), token);   // oltre il lease iniziale di 3 s
            return Ok(Manifest(seed));
        };

        var first = rig.RunAsync("worker-1");
        await Task.Delay(TimeSpan.FromSeconds(3.7), Ct);
        var concurrent = await rig.RunAsync("worker-2");

        Assert.Null(concurrent);   // lease rinnovato: nessun reclaim
        Assert.Equal(ParseRunOutcome.Published, await first);
        Assert.Equal(1, rig.ConvertCalls);
        Assert.Equal(1, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
    }

    [Fact]
    public async Task A_running_job_with_a_live_lease_survives_a_restart_and_is_reclaimed_only_after_expiry_with_a_higher_fence()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => Respond(Ok(Manifest(seed)));
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"Status\" = 'Running', \"LeaseOwner\" = 'processo-morto', \"LeaseUntil\" = clock_timestamp() + interval '30 seconds', \"FencingToken\" = 1, \"AttemptCount\" = 1, \"StartedAt\" = clock_timestamp() WHERE \"Id\" = '{seed.Job}'");
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"Status\" = 'Parsing' WHERE \"Id\" = '{seed.Preparation}'");

        Assert.Null(await rig.RunAsync("dopo-riavvio"));   // nessun reset: il lease vive ancora
        Assert.Equal("processo-morto", await Str(rig, $"SELECT \"LeaseOwner\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));

        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync("dopo-riavvio"));

        Assert.Equal(2, await rig.Db.ScalarAsync<int>($"SELECT \"FencingToken\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(2, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
    }

    [Fact]
    public async Task A_parent_that_expires_during_the_conversion_blocks_the_publication_and_fails_the_job_explicitly()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = async (_, _) =>
        {
            await rig.Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Preparation}'");
            return Ok(Manifest(seed));
        };

        Assert.Equal(ParseRunOutcome.Failed, await rig.RunAsync());   // pubblicazione negata e job chiuso in modo esplicito

        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"ParsedArtifacts\""));
        Assert.Equal("Failed", await Str(rig, $"SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal("parent_deadline_exceeded", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal("Failed", await Str(rig, $"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
    }

    [Fact]
    public async Task A_queued_job_whose_parent_deadline_has_passed_is_failed_by_the_sweep_and_never_converted()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => throw new InvalidOperationException("nessuna conversione dopo la scadenza");
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Preparation}'");

        Assert.Null(await rig.RunAsync());

        Assert.Equal("parent_deadline_exceeded", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal("parent_deadline_exceeded", await Str(rig, $"SELECT \"ErrorCode\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Equal(0, rig.ConvertCalls);
    }

    [Fact]
    public async Task Two_workers_racing_for_one_job_convert_and_publish_it_once()
    {
        await using var rig = await Rig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = async (_, token) =>
        {
            await Task.Delay(500, token);
            return Ok(Manifest(seed));
        };

        var outcomes = await Task.WhenAll(rig.RunAsync("a"), rig.RunAsync("b"));

        Assert.Single(outcomes, o => o == ParseRunOutcome.Published);
        Assert.Single(outcomes, o => o is null);
        Assert.Equal(1, rig.ConvertCalls);
        Assert.Equal(1L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"ParsedArtifacts\""));
    }

    // ---- confini -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_configured_parser_nothing_is_claimed_and_jobs_stay_queued()
    {
        await using var rig = await Rig.CreateAsync(parser: new ParserOptions());
        var seed = await rig.SeedAsync();

        Assert.Null(await rig.RunAsync());

        Assert.Equal("Queued", await Str(rig, $"SELECT \"Status\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
        Assert.Equal(0, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" WHERE \"Id\" = '{seed.Job}'"));
    }

    [Fact]
    public async Task The_parsing_lane_has_no_model_dependency_and_leaves_ai_work_untouched()
    {
        await using var rig = await Rig.CreateAsync();

        // Nessun servizio AI nel contenitore: il worker CPU non può occupare uno slot DGX né accodarsi allo scheduler AI.
        Assert.Null(rig.Services.GetService<ExecutionLeaseStore>());
        Assert.Null(rig.Services.GetService<IChatTurnExecutor>());
        var dependencies = typeof(DocumentParseRunner).GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
        Assert.DoesNotContain(dependencies, t => t == typeof(ExecutionLeaseStore) || t.Namespace == "bProjects.App.AI" && t != typeof(ExecutionOptions) && !t.IsGenericType);

        // Il claim non legge né modifica le tabelle del lavoro AI.
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => Respond(Ok(Manifest(seed)));
        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync());
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT (SELECT count(*) FROM \"ChatTurns\") + (SELECT count(*) FROM \"ProcessingRuns\")"));
    }
}
