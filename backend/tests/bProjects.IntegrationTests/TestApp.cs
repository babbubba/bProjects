using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace bProjects.IntegrationTests;

/// <summary>Applicazione reale in processo con configurazione di test (chiave e credenziali fittizie, solo per i test).</summary>
public sealed class TestApp(string? connectionString = null) : WebApplicationFactory<Program>
{
    public const string PmUser = "pm.uno";
    public const string PmPassword = "Test-Password-Uno-1!";
    public const string SecondPmUser = "pm.due";
    public const string SecondPmPassword = "Test-Password-Due-2!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:SigningKey", "test-signing-key-0123456789-abcdefghij");
        builder.UseSetting("ConnectionStrings:Default", connectionString ?? "Host=unused;Database=unused");
        builder.UseSetting("Bootstrap:Accounts:0:UserName", PmUser);
        builder.UseSetting("Bootstrap:Accounts:0:Password", PmPassword);
        builder.UseSetting("Bootstrap:Accounts:1:UserName", SecondPmUser);
        builder.UseSetting("Bootstrap:Accounts:1:Password", SecondPmPassword);
    }
}
