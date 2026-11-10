using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using bProjects.App.Documents;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// Base delle prove dell'archivio documenti su API reale + PostgreSQL reale + filesystem isolato: ogni classe ha un database nuovo e una
/// radice di storage temporanea propria (<c>Documents:StorageRoot</c>), rimossa alla fine. Nessun modello AI: il client è sostituito dall'harness.
/// </summary>
public abstract class ArchiveTestBase : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "bprojects-archive-" + Guid.NewGuid().ToString("N"));
    private ApiHarness? _app;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected string StorageRoot => Path.Combine(_sandbox, "storage");

    protected ApiHarness App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    protected DocumentFileStore Store => App.Services.GetRequiredService<DocumentFileStore>();

    /// <summary>Limiti di prova (es. quota piccola) applicati alle opzioni prima dell'avvio dell'applicazione.</summary>
    protected virtual void Tune(DocumentOptions options)
    {
    }

    /// <summary>Servizi aggiuntivi prima dell avvio (es. parser reale o irraggiungibile per il worker); di default nessuno.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            Directory.CreateDirectory(_sandbox);
            _app = await StartAppAsync();
        }
    }

    private Task<ApiHarness> StartAppAsync() =>
        ApiHarness.StartAsync(_database.ConnectionString, configure: services =>
        {
            services.Configure<DocumentOptions>(options =>
            {
                options.StorageRoot = StorageRoot;
                Tune(options);
            });
            ConfigureServices(services);
        });

    /// <summary>Riavvio del processo: nuova applicazione sullo stesso database e sullo stesso volume di storage.</summary>
    protected async Task RestartAppAsync()
    {
        await _app!.DisposeAsync();
        _app = await StartAppAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _database.DisposeAsync();
        if (Directory.Exists(_sandbox))
        {
            foreach (var file in Directory.EnumerateFiles(_sandbox, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_sandbox, recursive: true);
        }
    }

    // ---- HTTP -----------------------------------------------------------------------------------------------------------------

    protected async Task<Guid> NewProject(HttpClient client) =>
        (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Archivio {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();

    /// <summary>Upload multipart: nuovo documento (documentId null) oppure nuova versione di un documento esistente.</summary>
    protected Task<HttpResponseMessage> Upload(
        HttpClient client, Guid project, byte[] bytes, string fileName = "nota.txt", string contentType = "text/plain",
        Guid? key = null, Guid? documentId = null, string? displayName = null, bool withKey = true, HttpContent? fileContent = null)
    {
        var form = new MultipartFormDataContent();
        var file = fileContent ?? new ByteArrayContent(bytes);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", fileName);
        if (displayName is not null)
        {
            form.Add(new StringContent(displayName), "displayName");
        }

        var url = documentId is null ? $"/api/projects/{project}/documents" : $"/api/projects/{project}/documents/{documentId}/versions";
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        if (withKey)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        return client.SendAsync(request, Ct);
    }

    protected static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    protected static byte[] Text(string seed, int length = 200)
    {
        var builder = new StringBuilder();
        while (builder.Length < length)
        {
            builder.Append(seed).Append(' ').Append(builder.Length).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    // ---- database -------------------------------------------------------------------------------------------------------------

    protected async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(Ct))!, typeof(T));
    }

    /// <summary>Connessione aperta al database di prova (per tenere transazioni o sonde di lock fuori dall'applicazione).</summary>
    protected async Task<NpgsqlConnection> OpenConnection()
    {
        var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>Come <see cref="OpenConnection"/> ma con un token PROPRIO: serve alla diagnostica di fine test, quando il token del test può essere già annullato.</summary>
    protected async Task<NpgsqlConnection> OpenConnectionWithToken(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Chiave dell'advisory lock di progetto (stessa derivazione di ProjectLock).</summary>
    protected static string ProjectLockKey(Guid projectId) => "bprojects:project-sources:" + projectId.ToString("N");

    protected Task<long> Count(string sql, params (string Name, object Value)[] args) => Scalar<long>(sql, args);

    protected async Task Execute(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Originali presenti sul volume (file sotto objects/), indipendenti da ciò che dice il database.</summary>
    protected string[] StoredObjects()
    {
        var objects = Path.Combine(StorageRoot, "objects");
        return Directory.Exists(objects) ? Directory.GetFiles(objects, "*", SearchOption.AllDirectories) : [];
    }

    protected string[] TemporaryFiles()
    {
        var tmp = Path.Combine(StorageRoot, "tmp");
        return Directory.Exists(tmp) ? Directory.GetFiles(tmp) : [];
    }
}
