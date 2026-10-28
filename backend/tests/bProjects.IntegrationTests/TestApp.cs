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
        builder.UseSetting("ConnectionStrings:AppDb", connectionString ?? "Host=unused;Database=unused");
        // Modello interno fittizio e non raggiungibile: l'avvio non deve dipendere dal DGX.
        builder.UseSetting("Ai:Endpoint", "http://127.0.0.1:9/v1/");
        builder.UseSetting("Ai:ModelId", "test-model");
        builder.UseSetting("Ai:ApiKey", "test-key");
        builder.UseSetting("Bootstrap:Accounts:0:UserName", PmUser);
        builder.UseSetting("Bootstrap:Accounts:0:Password", PmPassword);
        builder.UseSetting("Bootstrap:Accounts:1:UserName", SecondPmUser);
        builder.UseSetting("Bootstrap:Accounts:1:Password", SecondPmPassword);
    }
}
