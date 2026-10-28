namespace bProjects.App.AI;

/// <summary>Come il backend chiede al modello interno una risposta strutturata (research.md §7).</summary>
public enum StructuredOutputMode
{
    /// <summary>response_format json_schema nativo del runtime (verificato su llama.cpp).</summary>
    JsonSchema,

    /// <summary>Testo JSON prodotto dal modello e validato dal backend con la stessa validazione tipizzata.</summary>
    ValidatedTextJson,
}

/// <summary>Fase di lavoro del modello: ognuna può avere tetto di output e ragionamento propri (<c>Ai:Phases</c>).</summary>
public enum AiPhase
{
    /// <summary>Dialogo con il PM.</summary>
    Chat,

    /// <summary>Consolidamento/analisi strutturata.</summary>
    Analysis,

    /// <summary>Generazione del documento.</summary>
    Document,

    /// <summary>Sintesi nativa della compattazione.</summary>
    Compaction,
}

/// <summary>Override di una fase; <c>null</c> = eredita il valore globale di <c>Ai</c> (una stringa vuota nella configurazione vale null).</summary>
public sealed class AiPhaseOptions
{
    public int? MaxOutputTokens { get; set; }

    public bool? EnableThinking { get; set; }

    // ReasoningBudgetTokens non è previsto: il supporto del runtime interno (llama.cpp su DGX) non è stato verificato.
}

/// <summary>Impostazioni risolte di una fase (<see cref="AiOptions.ForPhase"/>): override di fase oppure valori globali.</summary>
public sealed record AiPhaseSettings(AiPhase Phase, int MaxOutputTokens, bool EnableThinking, int InputBudgetTokens);

/// <summary>Sezione <c>Ai:Phases</c>: un override opzionale per fase.</summary>
public sealed class AiPhasesOptions
{
    public AiPhaseOptions Chat { get; set; } = new();

    public AiPhaseOptions Analysis { get; set; } = new();

    public AiPhaseOptions Document { get; set; } = new();

    public AiPhaseOptions Compaction { get; set; } = new();

    public AiPhaseOptions Of(AiPhase phase) => phase switch
    {
        AiPhase.Chat => Chat,
        AiPhase.Analysis => Analysis,
        AiPhase.Document => Document,
        AiPhase.Compaction => Compaction,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}

/// <summary>
/// Configurazione del modello interno (sezione <c>Ai</c>, variabili <c>Ai__*</c>). Endpoint, modello e
/// credenziale restano lato server; i valori di finestra provengono da misure sul runtime reale.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Base URL OpenAI-compatibile interno, es. <c>http://host:8080/v1/</c>.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>ID esatto del modello come esposto da <c>GET /v1/models</c>.</summary>
    public string ModelId { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>Finestra di contesto effettiva allocata dal runtime (ctx-size), non il massimo teorico del modello.</summary>
    public int ContextWindowTokens { get; set; } = 32768;

    public int MaxOutputTokens { get; set; } = 4096;

    public int SafetyMarginTokens { get; set; } = 1024;

    /// <summary>Soglia di attivazione della compattazione; 0 = uguale al budget di input.</summary>
    public int CompactionTriggerTokens { get; set; }

    public StructuredOutputMode StructuredOutputMode { get; set; } = StructuredOutputMode.JsonSchema;

    /// <summary>Timeout di rete della singola chiamata, sempre inferiore alla deadline del comando.</summary>
    public int RequestTimeoutSeconds { get; set; } = 150;

    /// <summary>I modelli Qwen emettono un ragionamento nascosto che consuma output: spento di default.</summary>
    public bool EnableThinking { get; set; }

    /// <summary>Host aggiuntivi ammessi oltre a loopback/reti private (nomi interni risolti da DNS aziendale).</summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Validità in secondi dello stato verificato del runtime (<see cref="ModelRuntimeMonitor"/>).</summary>
    public int RuntimeCheckSeconds { get; set; } = 60;

    /// <summary>Override per fase (<c>Ai:Phases:{Chat,Analysis,Document,Compaction}</c>); le fasi senza override ereditano i valori globali.</summary>
    public AiPhasesOptions Phases { get; set; } = new();

    /// <summary>ContextWindowTokens - MaxOutputTokens - SafetyMarginTokens con i soli valori globali (contracts/ai-workflow.md §7); per fase usare <see cref="ForPhase"/>.</summary>
    public int InputBudgetTokens => ContextWindowTokens - MaxOutputTokens - SafetyMarginTokens;

    public int EffectiveCompactionTriggerTokens => CompactionTriggerTokens > 0 ? CompactionTriggerTokens : InputBudgetTokens;

    /// <summary>Tetto di output, ragionamento e budget di input (finestra - output di fase - margine) della fase.</summary>
    public AiPhaseSettings ForPhase(AiPhase phase)
    {
        var overrides = Phases.Of(phase);
        var maxOutput = overrides.MaxOutputTokens ?? MaxOutputTokens;
        return new(phase, maxOutput, overrides.EnableThinking ?? EnableThinking, ContextWindowTokens - maxOutput - SafetyMarginTokens);
    }
}

/// <summary>
/// Validazione all'avvio: verifica solo la configurazione, mai la raggiungibilità del DGX
/// (un modello offline non impedisce creazione/riapertura dei progetti).
/// </summary>
public sealed class AiOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<AiOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, AiOptions o)
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(o.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            errors.Add("Ai:Endpoint deve essere un URL assoluto http/https.");
        }
        else
        {
            if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query))
            {
                errors.Add("Ai:Endpoint non deve contenere credenziali o query string.");
            }

            if (!IsInternalHost(endpoint.Host, o.AllowedHosts))
            {
                errors.Add("Ai:Endpoint deve puntare a un host interno (loopback, rete privata o Ai:AllowedHosts): nessun LLM esterno.");
            }
        }

        if (string.IsNullOrWhiteSpace(o.ModelId))
        {
            errors.Add("Ai:ModelId è obbligatorio.");
        }

        if (string.IsNullOrWhiteSpace(o.ApiKey))
        {
            errors.Add("Ai:ApiKey è obbligatoria e va fornita fuori dal repository.");
        }

        if (o.ContextWindowTokens <= 0 || o.MaxOutputTokens <= 0 || o.SafetyMarginTokens <= 0)
        {
            errors.Add("Ai:ContextWindowTokens, MaxOutputTokens e SafetyMarginTokens devono essere positivi.");
        }
        else if (o.InputBudgetTokens <= 0)
        {
            errors.Add("Ai: ContextWindowTokens deve superare MaxOutputTokens + SafetyMarginTokens (budget di input non positivo).");
        }
        else
        {
            // Una fase senza override ha già il budget globale, verificato sopra: qui solo quelle con un tetto proprio.
            var phasesValid = true;
            foreach (var phase in Enum.GetValues<AiPhase>())
            {
                if (o.Phases.Of(phase).MaxOutputTokens is not { } phaseOutput)
                {
                    continue;
                }

                if (phaseOutput <= 0)
                {
                    errors.Add($"Ai:Phases:{phase}:MaxOutputTokens deve essere positivo.");
                    phasesValid = false;
                }
                else if (o.ForPhase(phase).InputBudgetTokens <= 0)
                {
                    errors.Add($"Ai:Phases:{phase}:MaxOutputTokens: ContextWindowTokens deve superare l'output di fase + SafetyMarginTokens (budget di input non positivo).");
                    phasesValid = false;
                }
            }

            // La soglia vale per chat e analisi (le sole fasi che compattano): deve stare nel budget della più stretta.
            if (phasesValid && o.CompactionTriggerTokens != 0)
            {
                var tightest = Math.Min(o.ForPhase(AiPhase.Chat).InputBudgetTokens, o.ForPhase(AiPhase.Analysis).InputBudgetTokens);
                if (o.CompactionTriggerTokens < 1 || o.CompactionTriggerTokens > tightest)
                {
                    errors.Add($"Ai:CompactionTriggerTokens deve essere fra 1 e il budget di input della fase chat/analisi più stretta ({tightest}).");
                }
            }
        }

        if (!Enum.IsDefined(o.StructuredOutputMode))
        {
            errors.Add("Ai:StructuredOutputMode deve essere JsonSchema o ValidatedTextJson.");
        }

        if (o.RequestTimeoutSeconds <= 0)
        {
            errors.Add("Ai:RequestTimeoutSeconds deve essere positivo.");
        }

        if (o.RuntimeCheckSeconds <= 0)
        {
            errors.Add("Ai:RuntimeCheckSeconds deve essere positivo.");
        }

        return errors.Count == 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Success
            : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(errors);
    }

    /// <summary>Loopback, IP privati/link-local, nomi a etichetta singola o .local/.internal/.lan, oppure host in allowlist.</summary>
    internal static bool IsInternalHost(string host, string[] allowed)
    {
        if (allowed.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }

            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        }

        return !host.Contains('.')
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }
}
