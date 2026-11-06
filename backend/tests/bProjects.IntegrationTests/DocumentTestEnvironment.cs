namespace bProjects.IntegrationTests;

/// <summary>Trait "Category" aggiuntivi della 002; Integration e MafReal restano in <see cref="TestCategories"/>.</summary>
public static class DocumentTestCategories
{
    public const string Parser = "Parser";
}

/// <summary>Livelli di prova della slice 002 (tasks.md, «Formato, percorsi e regole di esecuzione»).</summary>
public enum DocumentTestLevel
{
    /// <summary>DET/GOLD: logica, codec, fixture e fake senza LLM reale né servizi: sempre disponibile.</summary>
    Det,

    /// <summary>PG: PostgreSQL e filesystem reali, modello finto o assente.</summary>
    Pg,

    /// <summary>PARSER: conversione/container/pesi reali offline, senza DGX.</summary>
    Parser,

    /// <summary>MAF-FAKE: SDK MAF reale con modello finto (nessun servizio esterno).</summary>
    MafFake,

    /// <summary>MAF-PG: SDK MAF reale, modello finto e PostgreSQL reale.</summary>
    MafPg,

    /// <summary>DGX: modello interno reale.</summary>
    Dgx,

    /// <summary>E2E-OFFLINE: browser con fake-stack oppure stack reale con LLM finto (Playwright, fuori da dotnet test).</summary>
    Browser,
}

/// <summary>
/// Prerequisiti dei livelli 002 (T003), separati da <see cref="TestEnvironment"/> che resta l'unica fonte per PostgreSQL e DGX.
/// Come per la 001 un prerequisito mancante produce Skipped con il motivo, mai un PASS: il livello non verificato resta dichiarato tale.
/// Nessun segreto o endpoint nel repository: solo variabili d'ambiente.
/// </summary>
public static class DocumentTestEnvironment
{
    /// <summary>Base URL del parser interno document-parse-1 per le prove PARSER (container locale, nessun DGX).</summary>
    public const string ParserEndpointVariable = "BPROJECTS_TEST_PARSER_ENDPOINT";

    /// <summary>Stessa variabile di Playwright (tests/e2e/playwright.config.ts): stack già avviato per il livello Browser.</summary>
    public const string BrowserBaseUrlVariable = "E2E_BASE_URL";

    public static string? ParserEndpoint => Read(ParserEndpointVariable);

    public static string? BrowserBaseUrl => Read(BrowserBaseUrlVariable);

    public static Uri RequireParser()
    {
        Assert.SkipWhen(ParserEndpoint is null, $"{ParserEndpointVariable} non impostata: parser documentale non disponibile.");
        return new Uri(ParserEndpoint!.TrimEnd('/') + "/");
    }

    /// <summary>PostgreSQL per i livelli PG e MAF-PG (stesso prerequisito della 001).</summary>
    public static string RequirePostgres() => TestEnvironment.RequirePostgres();

    /// <summary>DGX per le prove DGX/PM-DGX (stesso prerequisito della 001).</summary>
    public static Uri RequireDgx() => TestEnvironment.RequireDgx();

    /// <summary>Motivo per cui un livello non è eseguibile in questo ambiente, oppure null se lo è. Det e MafFake non hanno prerequisiti.</summary>
    public static string? MissingPrerequisite(DocumentTestLevel level) => level switch
    {
        DocumentTestLevel.Pg or DocumentTestLevel.MafPg when TestEnvironment.PostgresAdminConnectionString is null => $"{TestEnvironment.PostgresVariable} non impostata",
        DocumentTestLevel.Parser when ParserEndpoint is null => $"{ParserEndpointVariable} non impostata",
        DocumentTestLevel.Dgx when TestEnvironment.DgxUnavailableReason() is { } reason => reason,
        DocumentTestLevel.Browser when BrowserBaseUrl is null => $"{BrowserBaseUrlVariable} non impostata (Playwright avvia il dev server con fake-stack)",
        _ => null,
    };

    /// <summary>Cartella del corpus sintetico (tests/fixtures/documents), copiata accanto ai test dal csproj.</summary>
    public static string FixturesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", "documents");

    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
