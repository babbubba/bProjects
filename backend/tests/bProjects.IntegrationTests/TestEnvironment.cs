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

    /// <summary>
    /// Opt-in esplicito per qualunque inferenza reale sul DGX: solo il valore esatto <c>1</c>. Endpoint, modello e chiave presenti
    /// nell'ambiente NON bastano; senza opt-in i getter DGX restituiscono null e i test reali risultano Skipped (mai PASS).
    /// </summary>
    public const string DgxAllowVariable = "BPROJECTS_TEST_DGX_ALLOW";

    /// <summary>Connessione amministrativa a un PostgreSQL di test (può creare database).</summary>
    public static string? PostgresAdminConnectionString => Read(PostgresVariable);

    public static bool DgxAllowed => DgxAllowedFrom(Read);

    internal static bool DgxAllowedFrom(Func<string, string?> read) => read(DgxAllowVariable) == "1";

    /// <summary>Base URL OpenAI-compatibile del modello interno su DGX Spark, es. http://host:8000/v1. Null senza opt-in.</summary>
    public static string? DgxEndpoint => DgxAllowed ? Read(DgxEndpointVariable) : null;

    public const string DgxApiKeyVariable = "BPROJECTS_TEST_DGX_API_KEY";
    public const string DgxModelVariable = "BPROJECTS_TEST_DGX_MODEL";

    /// <summary>Chiave del server interno: solo da variabile d'ambiente, mai stampata né salvata. Null senza opt-in.</summary>
    public static string? DgxApiKey => DgxAllowed ? Read(DgxApiKeyVariable) : null;

    /// <summary>Null senza opt-in.</summary>
    public static string? DgxModel => DgxAllowed ? Read(DgxModelVariable) : null;

    /// <summary>Motivo per cui il DGX non è utilizzabile (opt-in mancante o endpoint mancante), oppure null se lo è.</summary>
    public static string? DgxUnavailableReason() => DgxUnavailableReason(Read);

    internal static string? DgxUnavailableReason(Func<string, string?> read)
    {
        if (!DgxAllowedFrom(read))
        {
            return $"{DgxAllowVariable}=1 non impostata: opt-in esplicito DGX mancante, nessuna inferenza reale eseguita (non è un PASS).";
        }

        return read(DgxEndpointVariable) is null ? $"{DgxEndpointVariable} non impostata: DGX Spark non disponibile." : null;
    }

    /// <summary>Opzioni AI reali per i test MafReal; Skipped se endpoint, chiave o modello mancano.</summary>
    public static AiOptions RequireDgxOptions()
    {
        var endpoint = RequireDgx();
        Assert.SkipWhen(DgxApiKey is null, $"{DgxApiKeyVariable} non impostata.");
        Assert.SkipWhen(DgxModel is null, $"{DgxModelVariable} non impostata.");
        var baseOptions = new AiOptions
        {
            Endpoint = endpoint.ToString(),
            ApiKey = DgxApiKey!,
            ModelId = DgxModel!,
            ContextWindowTokens = 131072,   // ctx-size del profilo Qwen3.6-35B-Smart (parallel=1), come infra/compose.yaml
            Phases = { Analysis = { MaxOutputTokens = 8192 } },   // come infra/compose.yaml (AI_ANALYSIS_MAX_OUTPUT_TOKENS)
        };
        MafDiagnostics.RecordConfig(baseOptions);
        return baseOptions;
    }

    /// <summary>
    /// Opzioni AI come i default di <c>infra/compose.yaml</c> (sessioni 6-7): finestra 131072, chat/documento/compattazione con thinking OFF e
    /// 4096 token, consolidamento con thinking ON, 32768 token e timeout 900 s. Il modello viene dalla variabile d'ambiente
    /// (per T052: Qwen3.6-35B-Smart). Usata dalle prove di accettazione; gli altri MafReal restano su <see cref="RequireDgxOptions"/>.
    /// </summary>
    public static AiOptions RequireProductionLikeOptions()
    {
        var options = RequireDgxOptions();
        options.ContextWindowTokens = 131072;
        options.MaxOutputTokens = 4096;
        options.EnableThinking = false;
        options.Phases.Chat.EnableThinking = false;
        options.Phases.Document.EnableThinking = false;
        options.Phases.Compaction.EnableThinking = false;
        options.Phases.Analysis.EnableThinking = true;
        options.Phases.Analysis.MaxOutputTokens = 32768;
        options.Phases.Analysis.RequestTimeoutSeconds = 900;
        options.Phases.Extraction.EnableThinking = false;     // come infra/compose.yaml (AI_EXTRACTION_*)
        options.Phases.Extraction.MaxOutputTokens = 16384;
        options.Phases.Extraction.RequestTimeoutSeconds = 600;
        MafDiagnostics.RecordConfig(options);
        return options;
    }

    /// <summary>Deadline del run di analisi come compose (1200 s); le altre fasi restano a 180 s.</summary>
    public const int ProductionAnalysisRunDeadlineSeconds = 1200;

    public static string RequirePostgres()
    {
        Assert.SkipWhen(PostgresAdminConnectionString is null, $"{PostgresVariable} non impostata: PostgreSQL di test non disponibile.");
        return PostgresAdminConnectionString!;
    }

    public static Uri RequireDgx()
    {
        var reason = DgxUnavailableReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        return new Uri(DgxEndpoint!.TrimEnd('/') + "/");
    }

    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
