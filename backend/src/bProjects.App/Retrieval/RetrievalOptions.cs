using Microsoft.Extensions.Options;
using bProjects.App.AI;

namespace bProjects.App.Retrieval;

/// <summary>
/// Configurazione del recupero (sezione <c>Retrieval:</c>, 003 T005). Inerte per default: senza <see cref="Enabled"/> nessun servizio viene contattato
/// e nessuna validazione di raggiungibilità è eseguita. Modelli solo su infrastruttura interna (Costituzione V): nessun fallback cloud, segreti solo da ambiente.
/// Nome, artefatto e dimensione sono configurazione FISSATA (D-5): un runtime che li smentisce è «non pronto».
/// </summary>
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>Dimensione dei vettori fissata da <c>vector(1024)</c> e dal modello di embedding.</summary>
    public const int FixedDimension = 1024;

    /// <summary>Versione del testo indicizzato (A-5): entra nell'<c>IndexConfigHash</c>.</summary>
    public const string IndexTextVersion = "index-text-1";

    /// <summary>Configurazione FTS dell'indice lessicale: entra nell'<c>IndexConfigHash</c>.</summary>
    public const string FtsConfiguration = "italian";

    /// <summary>Interruttore esplicito: false (default) = il recupero non parte mai da solo.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Versione della politica di recupero SELEZIONATA esplicitamente per le nuove richieste di Analizza (M3): <c>retrieval-policy-1</c> (default, baseline) o
    /// <c>retrieval-policy-2</c> (candidata, non accettata). Cambiarla non promuove nulla: il run congela la versione scelta.
    /// </summary>
    public string PolicyVersion { get; set; } = RetrievalPolicy.Version1;

    /// <summary>Host aggiuntivi ammessi oltre a loopback/reti private (nomi interni risolti da DNS aziendale).</summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Timeout di rete della singola richiesta ai modelli (sonde incluse, con tetto <see cref="ModelRuntimeMonitor.MaxProbeSeconds"/>).</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Validità in secondi dello stato di readiness in cache (una sola sonda in volo).</summary>
    public int RuntimeCheckSeconds { get; set; } = 60;

    public RetrievalModelOptions Embedding { get; set; } = new()
    {
        ModelId = "bge-m3-Q5",
        Artifact = "bge-m3-Q5_K_M.gguf",
        Dimension = FixedDimension,
    };

    public RetrievalModelOptions Reranker { get; set; } = new()
    {
        ModelId = "bge-reranker-v2-m3",
        Artifact = "bge-reranker-v2-m3-Q5_K_M.gguf",
    };
}

/// <summary>Un modello del recupero (embedding o riordino).</summary>
public sealed class RetrievalModelOptions
{
    /// <summary>Base dell'API OpenAI-compatibile (es. <c>http://dgx.internal:8081/v1/</c>); vuoto = non configurato.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    /// <summary>Nome del file dell'artefatto atteso (es. <c>bge-m3-Q5_K_M.gguf</c>): valore diverso dal runtime ⇒ <c>ArtifactMismatch</c>.</summary>
    public string Artifact { get; set; } = string.Empty;

    /// <summary>Dimensione dei vettori attesa (solo embedding): fissata a 1024.</summary>
    public int Dimension { get; set; }

    /// <summary>SHA-256 dei pesi, registrato al provisioning (T046). Vuoto ⇒ «non verificato»: dichiarato, non bloccante.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Solo da ambiente/segreti, mai nel repository.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Stato dello SHA-256 per readiness, registro e rapporti: «Verified» solo se lo SHA-256 è configurato E il runtime lo dimostra (stesso valore);
    /// assente o non dimostrabile ⇒ «NotVerified», mai un falso «Verified».
    /// </summary>
    public Sha256Status ResolveSha256Status(string? runtimeReportedSha256) =>
        !string.IsNullOrWhiteSpace(Sha256) && string.Equals(Sha256.Trim(), runtimeReportedSha256?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Sha256Status.Verified
            : Sha256Status.NotVerified;
}

public enum Sha256Status
{
    NotVerified,
    Verified,
}

/// <summary>Validazione all'avvio (come <see cref="AiOptionsValidator"/>): solo la struttura della configurazione, mai la raggiungibilità dei servizi.</summary>
public sealed class RetrievalOptionsValidator : IValidateOptions<RetrievalOptions>
{
    public ValidateOptionsResult Validate(string? name, RetrievalOptions o)
    {
        var errors = new List<string>();

        if (RetrievalPolicy.ByVersion(o.PolicyVersion) is null)
        {
            errors.Add($"Retrieval:PolicyVersion deve essere {RetrievalPolicy.Version1} o {RetrievalPolicy.Version2}.");
        }

        if (o.TimeoutSeconds <= 0)
        {
            errors.Add("Retrieval:TimeoutSeconds deve essere positivo.");
        }

        if (o.RuntimeCheckSeconds <= 0)
        {
            errors.Add("Retrieval:RuntimeCheckSeconds deve essere positivo.");
        }

        if (o.Embedding.Dimension != RetrievalOptions.FixedDimension)
        {
            errors.Add($"Retrieval:Embedding:Dimension deve essere {RetrievalOptions.FixedDimension} (colonna vector({RetrievalOptions.FixedDimension})).");
        }

        ValidateModel("Embedding", o.Embedding, o, required: o.Enabled, errors);
        ValidateModel("Reranker", o.Reranker, o, required: false, errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateModel(string section, RetrievalModelOptions m, RetrievalOptions o, bool required, List<string> errors)
    {
        var path = $"Retrieval:{section}";
        var configured = !string.IsNullOrWhiteSpace(m.Endpoint);
        if (!configured && !required)
        {
            return;   // non configurato e non richiesto: inerte
        }

        if (!Uri.TryCreate(m.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            errors.Add($"{path}:Endpoint deve essere un URL assoluto http/https.");
        }
        else
        {
            if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query))
            {
                errors.Add($"{path}:Endpoint non deve contenere credenziali o query string.");
            }

            if (!AiOptionsValidator.IsInternalHost(endpoint.Host, o.AllowedHosts))
            {
                errors.Add($"{path}:Endpoint deve puntare a un host interno (loopback, rete privata o Retrieval:AllowedHosts): nessun servizio esterno.");
            }
        }

        if (string.IsNullOrWhiteSpace(m.ModelId))
        {
            errors.Add($"{path}:ModelId è obbligatorio.");
        }

        if (string.IsNullOrWhiteSpace(m.Artifact))
        {
            errors.Add($"{path}:Artifact è obbligatorio (artefatto atteso).");
        }

        if (o.Enabled && string.IsNullOrWhiteSpace(m.ApiKey))
        {
            errors.Add($"{path}:ApiKey è obbligatoria e va fornita fuori dal repository.");
        }

        if (!string.IsNullOrWhiteSpace(m.Sha256) && !IsSha256(m.Sha256))
        {
            errors.Add($"{path}:Sha256 deve essere vuoto (non verificato) oppure 64 caratteri esadecimali.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}
