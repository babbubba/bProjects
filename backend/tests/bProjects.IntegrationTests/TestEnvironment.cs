using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>Categorie (trait "Category") per filtrare i test con <c>dotnet test --filter</c>.</summary>
public static class TestCategories
{
    public const string Integration = "Integration";
    public const string MafReal = "MafReal";
}

/// <summary>
/// Servizi reali richiesti dai test di integrazione, configurati solo da variabili d'ambiente:
/// nessun segreto o endpoint nel repository. Se mancano, i test dipendenti risultano Skipped,
/// mai superati.
/// </summary>
public static class TestEnvironment
{
    public const string PostgresVariable = "BPROJECTS_TEST_POSTGRES";
    public const string DgxEndpointVariable = "BPROJECTS_TEST_DGX_ENDPOINT";

    /// <summary>Connessione amministrativa a un PostgreSQL di test (può creare database).</summary>
    public static string? PostgresAdminConnectionString => Read(PostgresVariable);

    /// <summary>Base URL OpenAI-compatibile del modello interno su DGX Spark, es. http://host:8000/v1.</summary>
    public static string? DgxEndpoint => Read(DgxEndpointVariable);

    public const string DgxApiKeyVariable = "BPROJECTS_TEST_DGX_API_KEY";
    public const string DgxModelVariable = "BPROJECTS_TEST_DGX_MODEL";

    /// <summary>Chiave del server interno: solo da variabile d'ambiente, mai stampata né salvata.</summary>
    public static string? DgxApiKey => Read(DgxApiKeyVariable);

    public static string? DgxModel => Read(DgxModelVariable);

    /// <summary>Opzioni AI reali per i test MafReal; Skipped se endpoint, chiave o modello mancano.</summary>
    public static AiOptions RequireDgxOptions()
    {
        var endpoint = RequireDgx();
        Assert.SkipWhen(DgxApiKey is null, $"{DgxApiKeyVariable} non impostata.");
        Assert.SkipWhen(DgxModel is null, $"{DgxModelVariable} non impostata.");
        return new AiOptions
        {
            Endpoint = endpoint.ToString(),
            ApiKey = DgxApiKey!,
            ModelId = DgxModel!,
        };
    }

    public static string RequirePostgres()
    {
        Assert.SkipWhen(PostgresAdminConnectionString is null, $"{PostgresVariable} non impostata: PostgreSQL di test non disponibile.");
        return PostgresAdminConnectionString!;
    }

    public static Uri RequireDgx()
    {
        Assert.SkipWhen(DgxEndpoint is null, $"{DgxEndpointVariable} non impostata: DGX Spark non disponibile.");
        return new Uri(DgxEndpoint!.TrimEnd('/') + "/");
    }

    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
