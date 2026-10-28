namespace bProjects.App.AI;

/// <summary>Come il backend chiede al modello interno una risposta strutturata (research.md §7).</summary>
public enum StructuredOutputMode
{
    /// <summary>response_format json_schema nativo del runtime (verificato su llama.cpp).</summary>
    JsonSchema,

    /// <summary>Testo JSON prodotto dal modello e validato dal backend con la stessa validazione tipizzata.</summary>
    ValidatedTextJson,
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

    /// <summary>ContextWindowTokens - MaxOutputTokens - SafetyMarginTokens (contracts/ai-workflow.md §7).</summary>
    public int InputBudgetTokens => ContextWindowTokens - MaxOutputTokens - SafetyMarginTokens;

    public int EffectiveCompactionTriggerTokens => CompactionTriggerTokens > 0 ? CompactionTriggerTokens : InputBudgetTokens;
}
