using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.App.Retrieval;

/// <summary>
/// Politica di recupero versionata (003 T022, FR-010, FR-012, R-6): i parametri che determinano QUALI candidati arrivano allo stadio. Funzione pura e immutabile:
/// <see cref="Hash"/> è l'impronta canonica dei valori; il registro dei candidati riporta versione e impronta, così un run è riproducibile (FR-010).
/// Cambiare un valore NON modifica una versione esistente: ne richiede una nuova (<c>retrieval-policy-2</c>), perché un test fissa l'impronta della versione 1.
/// <para>
/// <b>Calibrazione.</b> I valori della versione 1 NON sono calibrati su un gold di calibrazione separato: non ne esiste uno (i corpus sintetici hanno query di un solo
/// autore, tutte usate per l'accettazione, SC-012). Sono i valori di partenza dichiarati in R-6 e usati nella baseline M1: <see cref="Pool"/> = 50 (profondità di ogni
/// segnale in T019), <see cref="Rerank"/> = 50 (il laboratorio riordinava i primi 50 della RRF), <see cref="RrfK"/> = 60 (A-2). Il loro effetto sul riordino è misurato in T026,
/// non presunto. Il budget del pacchetto non è un numero fisso: è il budget di input dello stadio (<see cref="PackageBudgetBasis"/>), misurato dal chiamante con <c>ContextBudget</c>.
/// </para>
/// </summary>
public sealed record RetrievalPolicy
{
    public const string Version1 = "retrieval-policy-1";

    /// <summary>Il budget del pacchetto è il budget di input dello stadio (<c>ContextBudget</c>), non una costante della politica.</summary>
    public const string StageInputBudget = "stage-input-budget";

    /// <summary>Politica corrente della slice: <c>retrieval-policy-1</c>.</summary>
    public static RetrievalPolicy V1 { get; } = new(Version1, pool: 50, rerank: 50, rrfK: Rrf.K, parentCharBudget: 0, StageInputBudget, RerankInput.TextOnly);

    public const string Version2 = "retrieval-policy-2";

    /// <summary>
    /// <c>retrieval-policy-2</c>: gli STESSI valori della versione 1 (pool, finestra, k RRF) più <c>rerank-input-1</c> (documento + sezione + testo nell'input del riordino).
    /// Candidata al confronto A/B di T027; NON è la politica corrente finché la misura non è autorizzata ed esaminata. La versione 1, la sua impronta e i run storici non cambiano.
    /// </summary>
    public static RetrievalPolicy V2 { get; } = new(Version2, pool: 50, rerank: 50, rrfK: Rrf.K, parentCharBudget: 0, StageInputBudget, RerankInput.Provenance1);

    public string Version { get; }

    /// <summary>Candidati per segnale (lessicale e denso) richiesti all'indice.</summary>
    public int Pool { get; }

    /// <summary>Primi candidati della fusione RRF inviati al riordino; i successivi seguono per rango RRF. Il cutoff è per numero, mai per punteggio.</summary>
    public int Rerank { get; }

    /// <summary>Costante <c>k</c> della fusione RRF.</summary>
    public int RrfK { get; }

    /// <summary>Budget in caratteri per l'espansione al padre; 0 = espansione disattivata (nessun padre fino a M4).</summary>
    public int ParentCharBudget { get; }

    public string PackageBudgetBasis { get; }

    /// <summary>Forma dell'input del riordino (<see cref="RerankInput"/>). <see cref="RerankInput.TextOnly"/> (versione 1) è il predefinito e NON entra nel JSON canonico: l'impronta della versione 1 resta quella fissata.</summary>
    public string RerankInputMode { get; }

    private RetrievalPolicy(string version, int pool, int rerank, int rrfK, int parentCharBudget, string packageBudgetBasis, string rerankInputMode)
    {
        Version = version;
        Pool = pool;
        Rerank = rerank;
        RrfK = rrfK;
        ParentCharBudget = parentCharBudget;
        PackageBudgetBasis = packageBudgetBasis;
        RerankInputMode = rerankInputMode;
    }

    /// <summary>Politica con valori espliciti: fuori range ⇒ <see cref="ArgumentOutOfRangeException"/> (nessun valore corretto in silenzio).</summary>
    public static RetrievalPolicy Create(string version, int pool, int rerank, int rrfK, int parentCharBudget, string rerankInputMode = RerankInput.TextOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(rerankInputMode);
        if (!RerankInput.IsKnown(rerankInputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(rerankInputMode), rerankInputMode, "Modalità di input del riordino sconosciuta.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pool, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rerank, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rrfK, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(parentCharBudget);
        // La fusione di due classifiche da `pool` contiene al massimo 2·pool blocchi: riordinarne di più non ha senso e nasconderebbe un errore di configurazione.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rerank, 2 * pool);
        return new(version, pool, rerank, rrfK, parentCharBudget, StageInputBudget, rerankInputMode);
    }

    /// <summary>
    /// JSON canonico (chiavi ordinate, nessuno spazio) dei soli valori che determinano il recupero. <c>rerankInput</c> compare SOLO se diverso da <see cref="RerankInput.TextOnly"/>:
    /// così la forma canonica e l'impronta di <c>retrieval-policy-1</c> restano byte per byte quelle già usate da M1/M2.
    /// </summary>
    public string ToCanonicalJson()
    {
        var values = new Dictionary<string, object>
        {
            ["version"] = Version,
            ["pool"] = Pool,
            ["rerank"] = Rerank,
            ["rrfK"] = RrfK,
            ["parentCharBudget"] = ParentCharBudget,
            ["packageBudgetBasis"] = PackageBudgetBasis,
        };
        if (RerankInputMode != RerankInput.TextOnly)
        {
            values["rerankInput"] = RerankInputMode;
        }

        return CanonicalJson.Canonicalize(JsonSerializer.Serialize(values));
    }

    /// <summary>Impronta SHA-256 (64 esadecimali minuscoli) del JSON canonico.</summary>
    public string Hash() => CanonicalJson.HashText(ToCanonicalJson());
}
