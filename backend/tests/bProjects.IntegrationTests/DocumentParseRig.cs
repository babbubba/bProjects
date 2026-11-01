using System.Net;
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
/// Worker di parsing su PostgreSQL reale e filesystem isolato. In modalita finta (<see cref="OnConvert"/>) il parser e un HTTP programmabile per
/// gare riproducibili; in modalita reale (<see cref="CreateRealAsync"/>) parla con il container Docling. Nessun servizio AI nel contenitore.
/// </summary>
internal sealed class ParseRig : IAsyncDisposable
{
    private const string VersionJson = """{"schema":"document-parse-1","configHash":"cfg-hash-1","formats":["pdf","docx","md","txt"]}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bprojects-parse-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private int _convertCalls;

    public ParsingDatabase Db { get; private set; } = null!;
    public DocumentFileStore Store { get; private set; } = null!;
    public int ConvertCalls => Volatile.Read(ref _convertCalls);
    public string? LastRequestBody { get; private set; }
    public IServiceProvider Services => _services;

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnConvert { get; set; } = (_, _) => throw new InvalidOperationException("parser non programmato");

    public static Task<ParseRig> CreateAsync(ExecutionOptions? execution = null, ParserOptions? parser = null) =>
        BuildAsync(execution, parser ?? new ParserOptions { BaseUrl = "http://parser.test/", Secret = "segreto-di-test" }, fake: true);

    public static Task<ParseRig> CreateRealAsync(ExecutionOptions? execution = null) => BuildAsync(execution, RealParser.Options(), fake: false);

    private static async Task<ParseRig> BuildAsync(ExecutionOptions? execution, ParserOptions parserOptions, bool fake)
    {
        var rig = new ParseRig { Db = await ParsingDatabase.CreateAsync() };
        Directory.CreateDirectory(rig._root);
        rig.Store = new DocumentFileStore(rig._root, [], NullLogger<DocumentFileStore>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(rig.Db.ConnectionString));
        services.AddSingleton(rig.Store);
        services.AddSingleton(Options.Create(execution ?? new ExecutionOptions()));
        services.AddSingleton(Options.Create(new DocumentOptions()));
        services.AddSingleton(Options.Create(parserOptions));
        HttpMessageHandler handler = fake ? new Handler(rig) : new HttpClientHandler();
        services.AddSingleton(new ParserClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, Options.Create(parserOptions)));
        services.AddScoped<DocumentPolicyService>();
        services.AddScoped<DocumentJobStore>();
        services.AddScoped<DocumentParseRunner>();
        rig._services = services.BuildServiceProvider();
        return rig;
    }

    public async Task<ParseRunOutcome?> RunAsync(string owner = "worker-1", CancellationToken? token = null)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DocumentParseRunner>().RunNextAsync(owner, token ?? TestContext.Current.CancellationToken);
    }

    public Task<SeededParse> SeedAsync(byte[]? content = null, int maxAttempts = 3, TimeSpan? hardWindow = null) =>
        Db.SeedParseAsync(Store, content, hardWindow, maxAttempts);

    /// <summary>Come <see cref="SeedAsync"/> ma con un fixture reale del corpus (tipo e nome coerenti col formato).</summary>
    public Task<SeededParse> SeedFixtureAsync(string fixture, int maxAttempts = 3) =>
        Db.SeedParseAsync(Store, RealParser.Fixture(fixture), null, maxAttempts, null, DocumentUploadValidation.MediaTypeOf(RealParser.FormatOf(fixture)), fixture);

    public string Original(SeededParse seed) => Path.Combine(_root, "objects", seed.StorageKey.Replace('/', Path.DirectorySeparatorChar));

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

    private sealed class Handler(ParseRig rig) : HttpMessageHandler
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

    // ---- manifest finto valido per l'originale testuale seminato --------------------------------------------------------------------------

    public static string Sha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string FakeManifest(SeededParse seed)
    {
        const string t0 = "Titolo";
        const string t1 = "Testo del documento.";
        JsonObject Block(string reference, int order, string text, int line) => new()
        {
            ["ref"] = reference, ["order"] = order, ["parentRef"] = null, ["type"] = order == 0 ? "heading" : "paragraph", ["text"] = text,
            ["headingPath"] = new JsonArray(), ["locator"] = new JsonObject { ["lineStart"] = line, ["lineEnd"] = line },
        };
        JsonObject Range(string key, string block, string text) => new()
        {
            ["rangeKey"] = key, ["blockRef"] = block, ["start"] = 0, ["end"] = text.Length, ["ownership"] = "Primary", ["sourceTextSha256"] = Sha(text),
        };
        return new JsonObject
        {
            ["schemaVersion"] = "document-parse-1", ["originalSha256"] = seed.Sha256, ["parserVersion"] = "1.0.0", ["dependencyLockHash"] = "lock",
            ["modelArtifactsHash"] = "model", ["configHash"] = "cfg-hash-1", ["status"] = "Complete", ["originFormat"] = "txt",
            ["originalByteLength"] = seed.ByteLength, ["pageCount"] = null, ["offsetConvention"] = "unicode-codepoint",
            ["document"] = new JsonObject { ["serialization"] = "text-1", ["content"] = Encoding.UTF8.GetString(seed.Content) },
            ["blocks"] = new JsonArray(Block("b0", 0, t0, 1), Block("b1", 1, t1, 3)),
            ["inventory"] = new JsonArray(new JsonObject { ["kind"] = "paragraph", ["state"] = "represented", ["blockRefs"] = new JsonArray("b0", "b1"), ["issueRefs"] = new JsonArray() }),
            ["chunks"] = new JsonArray(new JsonObject { ["ranges"] = new JsonArray(Range("r0", "b0", t0), Range("r1", "b1", t1)) }),
            ["issues"] = new JsonArray(),
        }.ToJsonString();
    }

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    public static HttpResponseMessage Status(int code, string body = "{}", int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    public Task<string> Str(string sql) => Db.ScalarAsync<string>(sql);
}
