using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using bProjects.App.Documents;
using bProjects.App.Identity;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace bProjects.IntegrationTests;

/// <summary>
/// T005 su PostgreSQL e API reali, senza DGX: provisioning Admin idempotente, login/refresh con i claim giusti, GET/PUT dei limiti
/// riservati al ruolo Admin con If-Match atomico. Il modello interno di TestApp non è raggiungibile: la lettura dei limiti non lo usa.
/// Skipped senza BPROJECTS_TEST_POSTGRES.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentAdminAuthorizationTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private const string Limits = "/api/admin/document-limits";
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
        // Il database è condiviso dalla classe: ogni prova riparte dai default della configurazione server.
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"DocumentPolicies\"");
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

    [Fact]
    public async Task Provisioning_twice_neither_duplicates_nor_alters_accounts_or_passwords()
    {
        var created = await AccountBootstrap.RunAsync(App.Services, App.Services.GetRequiredService<IConfiguration>());

        Assert.Equal(0, created);
        using var scope = App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        Assert.Single(await users.GetUsersInRoleAsync(AccountBootstrap.AdminRole), u => u.UserName == TestApp.AdminUser);
        var admin = (await users.FindByNameAsync(TestApp.AdminUser))!;
        Assert.Equal([AccountBootstrap.AdminRole], await users.GetRolesAsync(admin));   // un Admin nuovo non è anche PM
        Assert.True(await users.CheckPasswordAsync(admin, TestApp.AdminPassword));
        var pm = (await users.FindByNameAsync(TestApp.PmUser))!;
        Assert.True(await users.CheckPasswordAsync(pm, TestApp.PmPassword));
        Assert.DoesNotContain(AccountBootstrap.AdminRole, await users.GetRolesAsync(pm));   // nessuna elevazione implicita

        using var client = await LoginAsync(TestApp.PmUser, TestApp.PmPassword);   // il PM esistente entra ancora con la vecchia password
        Assert.NotNull(client);
    }

    [Fact]
    public async Task Elevating_an_existing_pm_requires_explicit_configuration_and_keeps_their_password()
    {
        using var scope = App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "pm.da.promuovere" };
        Assert.True((await users.CreateAsync(user, "Test-Password-Quattro-4!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, AccountBootstrap.PmRole)).Succeeded);

        // Senza elencarlo in Bootstrap:Admins il bootstrap non lo tocca.
        await AccountBootstrap.RunAsync(App.Services, App.Services.GetRequiredService<IConfiguration>());
        Assert.DoesNotContain(AccountBootstrap.AdminRole, await users.GetRolesAsync(user));

        // Con la configurazione esplicita riceve Admin (anche senza password nel file), mantiene PM e password.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bootstrap:Admins:0:UserName"] = "pm.da.promuovere",
        }).Build();
        Assert.Equal(1, await AccountBootstrap.RunAsync(App.Services, configuration));
        Assert.Equal(0, await AccountBootstrap.RunAsync(App.Services, configuration));
        var roles = await users.GetRolesAsync((await users.FindByIdAsync(user.Id.ToString()))!);
        Assert.Contains(AccountBootstrap.AdminRole, roles);
        Assert.Contains(AccountBootstrap.PmRole, roles);
        Assert.True(await users.CheckPasswordAsync(user, "Test-Password-Quattro-4!"));
    }

    [Fact]
    public async Task Admin_logs_in_and_refreshes_with_the_admin_claim_only()
    {
        using var client = NewClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = TestApp.AdminPassword }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal([AccountBootstrap.AdminRole], RoleClaims(await Token(login)));

        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refresh.Headers.Add("Cookie", $"bp_refresh={CookieValue(login, "bp_refresh")}; XSRF-TOKEN={CookieValue(login, "XSRF-TOKEN")}");
        refresh.Headers.Add("X-XSRF-TOKEN", CookieValue(login, "XSRF-TOKEN"));
        var refreshed = await client.SendAsync(refresh, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal([AccountBootstrap.AdminRole], RoleClaims(await Token(refreshed)));
    }

    [Fact]
    public async Task Wrong_credentials_and_a_missing_role_are_rejected_at_login()
    {
        using var client = NewClient();
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = "password-sbagliata-1" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using (var scope = App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            Assert.True((await users.CreateAsync(new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "senza.ruolo.admin" }, "Test-Password-Cinque-5!")).Succeeded);
        }

        var noRole = await client.PostAsJsonAsync("/api/auth/login", new { username = "senza.ruolo.admin", password = "Test-Password-Cinque-5!" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, noRole.StatusCode);
    }

    [Fact]
    public async Task Anonymous_gets_401_and_a_pm_gets_403_on_both_methods()
    {
        using var anonymous = NewClient();
        using var pm = await LoginAsync(TestApp.PmUser, TestApp.PmPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Limits, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(anonymous, "\"1\"", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.GetAsync(Limits, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(pm, "\"1\"", "{}")).StatusCode);
    }

    [Fact]
    public async Task An_admin_does_not_gain_project_access_and_pm_access_is_unchanged()
    {
        using var admin = await LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);
        using var pm = await LoginAsync(TestApp.PmUser, TestApp.PmPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/projects", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pm.GetAsync("/api/projects", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Admin_reads_the_default_limits_without_the_model_and_gets_an_etag()
    {
        using var admin = await LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);

        var response = await admin.GetAsync(Limits, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<DocumentLimitEndpoints.PolicyView>(TestContext.Current.CancellationToken))!;
        Assert.Equal(new DocumentOptions().ToLimits(), view.Limits);
        Assert.Equal(view.Limits.Hash(), view.PolicyHash);
        Assert.Equal($"\"{view.Revision}\"", response.Headers.ETag!.Tag);
        Assert.DoesNotContain("password", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Put_replaces_the_limits_atomically_and_a_stale_if_match_is_a_409()
    {
        using var admin = await LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);
        var current = await ReadView(admin);
        var changed = current.Limits with { MaxPages = 2_000, ExtendedDeadlineSeconds = 7_200 };

        var ok = await Put(admin, $"\"{current.Revision}\"", changed);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = (await ok.Content.ReadFromJsonAsync<DocumentLimitEndpoints.PolicyView>(TestContext.Current.CancellationToken))!;
        Assert.Equal(current.Revision + 1, updated.Revision);
        Assert.Equal(changed, updated.Limits);
        Assert.NotEqual(current.PolicyHash, updated.PolicyHash);
        Assert.NotNull(updated.UpdatedBy);

        var stale = await Put(admin, $"\"{current.Revision}\"", changed with { MaxPages = 3_000 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(changed, (await ReadView(admin)).Limits);   // il conflitto non ha modificato nulla
    }

    [Fact]
    public async Task Concurrent_puts_with_the_same_revision_have_a_single_winner()
    {
        using var admin = await LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);
        var current = await ReadView(admin);

        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            Put(admin, $"\"{current.Revision}\"", current.Limits with { MaxBlocks = 1_000 + i })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(current.Revision + 1, (await ReadView(admin)).Revision);
    }

    [Fact]
    public async Task The_server_rejects_invalid_malformed_and_unknown_limits_without_changing_the_policy()
    {
        using var admin = await LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);
        var current = await ReadView(admin);
        var etag = $"\"{current.Revision}\"";

        var tooShort = await Put(admin, etag, current.Limits with { ExtendedDeadlineSeconds = 100 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);
        Assert.Contains("ExtendedDeadlineSeconds", await tooShort.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Put(admin, etag, current.Limits with { MaxFileBytes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Put(admin, etag, current.Limits with { ProjectQuotaBytes = -5 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Put(admin, etag, current.Limits with { ExtendedDeadlineSeconds = int.MaxValue })).StatusCode);

        var json = current.Limits.ToJson();
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, etag, json.TrimEnd('}') + ",\"promotionAfterSeconds\":120}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, etag, json.Replace("\"maxPages\":1000,", string.Empty, StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, etag, "non json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "\"x\"", json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, null, json)).StatusCode);

        Assert.Equal(current.Revision, (await ReadView(admin)).Revision);
    }

    private HttpClient NewClient() => App.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });

    private async Task<HttpClient> LoginAsync(string user, string password)
    {
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = user, password }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Token(response));
        return client;
    }

    private static async Task<string> Token(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<AuthEndpoints.TokenResponse>(TestContext.Current.CancellationToken))!.AccessToken;

    private static string[] RoleClaims(string token) =>
        [.. new JsonWebTokenHandler().ReadJsonWebToken(token).Claims.Where(c => c.Type == "role").Select(c => c.Value)];

    private static async Task<DocumentLimitEndpoints.PolicyView> ReadView(HttpClient admin) =>
        (await (await admin.GetAsync(Limits, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<DocumentLimitEndpoints.PolicyView>(TestContext.Current.CancellationToken))!;

    private static Task<HttpResponseMessage> Put(HttpClient client, string? ifMatch, DocumentLimits limits) => Put(client, ifMatch, limits.ToJson());

    private static Task<HttpResponseMessage> Put(HttpClient client, string? ifMatch, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Limits) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string CookieValue(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(name + "=", StringComparison.Ordinal)).Split(';')[0][(name.Length + 1)..];
}
