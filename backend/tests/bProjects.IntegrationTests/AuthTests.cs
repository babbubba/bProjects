using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using bProjects.App.Identity;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace bProjects.IntegrationTests;

/// <summary>T007 + T008 su PostgreSQL reale: migrazione, bootstrap esplicito e sessione locale. Skipped senza BPROJECTS_TEST_POSTGRES.</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AuthTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private TestApp? _app;

    public async ValueTask InitializeAsync()
    {
        if (TestEnvironment.PostgresAdminConnectionString is null)
        {
            return;
        }

        _app = new TestApp(database.ConnectionString);
        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await AccountBootstrap.RunAsync(_app.Services, _app.Services.GetRequiredService<IConfiguration>());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private TestApp App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    private HttpClient NewClient() => App.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });

    [Fact]
    public async Task Bootstrap_is_idempotent_and_creates_two_pm_accounts()
    {
        var created = await AccountBootstrap.RunAsync(App.Services, App.Services.GetRequiredService<IConfiguration>());

        Assert.Equal(0, created); // già creati all'inizializzazione: nessuna modifica agli esistenti
        using var scope = App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        Assert.Equal(2, (await users.GetUsersInRoleAsync(AccountBootstrap.PmRole)).Count(u => u.UserName is TestApp.PmUser or TestApp.SecondPmUser));
    }

    [Fact]
    public async Task Login_returns_access_token_and_protected_cookies()
    {
        using var client = NewClient();

        var response = await Login(client, TestApp.PmUser, TestApp.PmPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthEndpoints.TokenResponse>(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));
        Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);

        var refresh = SetCookie(response, "bp_refresh");
        Assert.Contains("httponly", refresh, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", refresh, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refresh, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("httponly", SetCookie(response, "XSRF-TOKEN"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TestApp.PmUser, "password-sbagliata")]
    [InlineData("sconosciuto", "qualunque")]
    public async Task Login_with_invalid_credentials_is_unauthorized(string user, string password)
    {
        using var client = NewClient();

        var response = await Login(client, user, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_without_pm_role_is_forbidden()
    {
        using (var scope = App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var created = await users.CreateAsync(new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "senza.ruolo" }, "Test-Password-Zero-0!");
            Assert.True(created.Succeeded);
        }

        using var client = NewClient();
        var response = await Login(client, "senza.ruolo", "Test-Password-Zero-0!");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_requires_csrf_header()
    {
        using var client = NewClient();
        var login = await Login(client, TestApp.PmUser, TestApp.PmPassword);

        var withoutHeader = await Post(client, "/api/auth/refresh", CookieHeader(login), csrf: null);
        var wrongHeader = await Post(client, "/api/auth/refresh", CookieHeader(login), csrf: "altro-valore");

        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongHeader.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_rejects_reuse()
    {
        using var client = NewClient();
        var login = await Login(client, TestApp.PmUser, TestApp.PmPassword);
        var firstRefresh = CookieValue(login, "bp_refresh");

        var rotated = await Post(client, "/api/auth/refresh", CookieHeader(login), CookieValue(login, "XSRF-TOKEN"));

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.NotEqual(firstRefresh, CookieValue(rotated, "bp_refresh"));
        Assert.False(string.IsNullOrEmpty((await rotated.Content.ReadFromJsonAsync<AuthEndpoints.TokenResponse>(TestContext.Current.CancellationToken))!.AccessToken));

        var replay = await Post(client, "/api/auth/refresh", CookieHeader(login), CookieValue(login, "XSRF-TOKEN"));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var next = await Post(client, "/api/auth/refresh", CookieHeader(rotated), CookieValue(rotated, "XSRF-TOKEN"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Concurrent_refresh_with_the_same_token_succeeds_only_once()
    {
        using var client = NewClient();
        var login = await Login(client, TestApp.PmUser, TestApp.PmPassword);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Post(client, "/api/auth/refresh", CookieHeader(login), CookieValue(login, "XSRF-TOKEN"))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_clears_cookies()
    {
        using var client = NewClient();
        var login = await Login(client, TestApp.PmUser, TestApp.PmPassword);

        var forbidden = await Post(client, "/api/auth/logout", CookieHeader(login), csrf: null);
        var logout = await Post(client, "/api/auth/logout", CookieHeader(login), CookieValue(login, "XSRF-TOKEN"));
        var refreshAfter = await Post(client, "/api/auth/refresh", CookieHeader(login), CookieValue(login, "XSRF-TOKEN"));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfter.StatusCode);
    }

    [Fact]
    public async Task Issued_access_token_passes_the_pm_policy_and_garbage_does_not()
    {
        using var client = NewClient();
        var login = await Login(client, TestApp.PmUser, TestApp.PmPassword);
        var token = (await login.Content.ReadFromJsonAsync<AuthEndpoints.TokenResponse>(TestContext.Current.CancellationToken))!.AccessToken;

        var parameters = App.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        var valid = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        var garbage = await new JsonWebTokenHandler().ValidateTokenAsync("non.un.token", parameters);

        Assert.True(valid.IsValid);
        Assert.False(garbage.IsValid);

        var provider = App.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = App.Services.GetRequiredService<IAuthorizationService>();
        var policy = (await provider.GetFallbackPolicyAsync())!;
        var pm = new ClaimsPrincipal(valid.ClaimsIdentity);
        var other = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "Altro")], "test", "name", "role"));

        Assert.True((await authorization.AuthorizeAsync(pm, null, policy)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(other, null, policy)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, policy)).Succeeded);
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string user, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { username = user, password }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, string cookies, string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Cookie", cookies);
        if (csrf is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", csrf);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(name + "=", StringComparison.Ordinal));

    private static string CookieValue(HttpResponseMessage response, string name) =>
        SetCookie(response, name).Split(';')[0][(name.Length + 1)..];

    private static string CookieHeader(HttpResponseMessage response) =>
        $"bp_refresh={CookieValue(response, "bp_refresh")}; XSRF-TOKEN={CookieValue(response, "XSRF-TOKEN")}";
}
