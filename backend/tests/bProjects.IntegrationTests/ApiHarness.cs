using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Identity;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace bProjects.IntegrationTests;

/// <summary>
/// Applicazione reale su PostgreSQL reale per le prove HTTP di progetti e conversazioni.
/// Il client del modello interno è sostituito da uno che fallisce se costruito: dimostra che
/// creare, elencare e riaprire progetti non inizializza né invoca alcuna risorsa AI.
/// </summary>
public sealed class ApiHarness : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private ApiHarness(string connectionString)
    {
        ConnectionString = connectionString;
        _factory = new TestApp(connectionString).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<InternalModelClient>(_ => throw new InvalidOperationException("Il client AI non deve essere inizializzato."))));
    }

    public string ConnectionString { get; }

    public IServiceProvider Services => _factory.Services;

    /// <summary>Avvia l'app; con <paramref name="prepare"/> applica le migrazioni e crea i due PM di test.</summary>
    public static async Task<ApiHarness> StartAsync(string connectionString, bool prepare = true)
    {
        var harness = new ApiHarness(connectionString);
        if (prepare)
        {
            using var scope = harness.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
            await AccountBootstrap.RunAsync(harness.Services, harness.Services.GetRequiredService<IConfiguration>());
        }

        return harness;
    }

    public HttpClient Anonymous() =>
        _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });

    public async Task<HttpClient> LoginAsync(string user = TestApp.PmUser, string password = TestApp.PmPassword)
    {
        var client = Anonymous();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = user, password }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<AuthEndpoints.TokenResponse>(TestContext.Current.CancellationToken))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Token firmato correttamente ma senza il ruolo PM: autenticato, non autorizzato.</summary>
    public HttpClient WithoutPmRole()
    {
        var client = Anonymous();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "bProjects",
            Audience = "bProjects",
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("name", "senza.ruolo"), new Claim("role", "Altro")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestApp.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
