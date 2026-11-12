using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Retrieval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>Un'estensione fissa del corpus T082/T083 per i Gap della 003 (documento incluso con risposta parziale, documento escluso dal PM).</summary>
internal sealed record ExtensionDocument(string Key, string FileName, string Text, bool ExcludedByPm);

/// <summary>
/// Attesa annotata PRIMA di ogni inferenza per una domanda del PM. <see cref="Kind"/>: present | partial | absent | conflict | excluded. Il <see cref="QuestionToken"/> individua l'enunciato del Gap
/// estratto dalla domanda; <see cref="SupportQuotes"/> sono le alternative (piegate) che devono comparire fra le citazioni dei supporti; <see cref="ForbiddenQuotes"/> non devono comparire mai.
/// </summary>
internal sealed record CampaignExpectation(
    string Id, string Kind, string Question, string QuestionToken, string Outcome, string[] Reasons, string[][] SupportQuotes, string[] ForbiddenQuotes, bool ExpectOpenConflict);

/// <summary>Un corpus della campagna: stessa generazione e stessi gold di T082/T083 (<see cref="LargeDocumentCorpus"/>), parametri FISSATI (nessun riempitivo dipendente dal tokenizer).</summary>
internal sealed record CampaignScenario(string Label, string Origin, IReadOnlyList<CampaignExpectation> Expectations);

/// <summary>
/// T040 (003, gate «SC-001/SC-003-retrieval») — definizione CONGELATA della campagna reale: configurazione, corpus, domande e attese. Tutto è dichiarato qui e solo qui, PRIMA di ogni inferenza, e
/// riassunto in un manifesto con hash (<see cref="FrozenManifestSha256"/>, copia versionata in <c>specs/003-retrieval-evidence-search/validation/T040-campaign-manifest.json</c>): qualunque modifica a
/// corpus, domande, attese, soglie o configurazione cambia l'hash e fa fallire i test offline finché non viene ri-congelata esplicitamente. I gold storici (<see cref="LargeDocumentCorpus"/>) NON sono
/// modificati: sono solo riferiti (il loro hash è nel manifesto). Le attese non si adattano alle risposte del modello.
/// </summary>
internal static class RetrievalGapCampaign
{
    public const string Id = "T040-retrieval-gap-1";

    // ---- configurazione fissata. Motivazione di W=32768: il recupero interviene SOLO quando il ledger completo non entra nel budget di input dello stadio Gap (B = W − O − M);
    // a W=131072 (T150) i corpora T082/T083 entrerebbero interi (sorgente esaustiva, FR-002) e T040 non eserciterebbe il recupero. W=32768 è la finestra per cui T082/T083 sono dimensionati.
    public const int WindowTokens = 32768;
    public const int OutputTokens = 16384;
    public const int MarginTokens = 1024;
    public const bool Thinking = false;
    public const int BudgetTokens = WindowTokens - OutputTokens - MarginTokens;

    // ---- corpus: regole di T082/T083, riempitivo zero (il riempitivo di T082/T083 dipende dal tokenizer misurato a runtime e non è congelabile; non aggiunge evidenze né gold).
    public const int SingleBulkRules = 260;
    public const int MultiBulkRulesPerDocument = 90;
    public const int FillerChars = 0;

    // ---- modelli e politica: valori fissati; embedding e riordino sono quelli di default del prodotto (RetrievalOptions), la politica è selezionata esplicitamente e NON promossa a default.
    public static string PolicyVersion => RetrievalPolicy.Version1;

    public const string EmbeddingModelId = "bge-m3-Q5";
    public const string EmbeddingArtifact = "bge-m3-Q5_K_M.gguf";
    public const string RerankerModelId = "bge-reranker-v2-m3";
    public const string RerankerArtifact = "bge-reranker-v2-m3-Q5_K_M.gguf";
    public const int EmbeddingDimension = 1024;

    // ---- modello LLM: FISSATO prima dell'inferenza (non registrato dopo). È l'identificativo attualmente configurato, letto senza inferenze da BPROJECTS_TEST_DGX_MODEL dell'ambiente di lavoro e da
    // infra/.env.example (AI_MODEL_ID), concordi. È un id di configurazione del runtime: pesi e quantizzazione non sono verificabili offline; il profilo misurato a runtime deve riportare QUESTO id (RejectProfile).
    public const string LlmModelId = "Qwen3.6-35B-Smart";

    // ---- soglie (tasks.md T040; spec SC-003/SC-004)
    /// <summary>Citazioni invalide: «≤ 2 su 10», applicato in modo CONSERVATIVO in proporzione al numero di citazioni misurate (<see cref="WithinPer10"/>).</summary>
    public const int MaxInvalidCitationsPer10 = 2;

    /// <summary>
    /// Conflitti persi (SC-003): il RIFERIMENTO del laboratorio è «1–2 su 10 casi» (<see cref="LabConflictLostMax"/> su <see cref="LabConflictReferenceCases"/>), una misura su un campione di 10 casi, non una soglia
    /// universale. Si applica solo a un campione osservato almeno altrettanto ampio; con meno casi di quelli del laboratorio il riferimento non è trasferibile e la tolleranza è ZERO (<see cref="AllowedLostConflicts"/>).
    /// </summary>
    public const int LabConflictLostMax = 2;

    public const int LabConflictReferenceCases = 10;

    /// <summary>Conflitti persi per esclusione a monte del recupero (un lato del conflitto fuori dal pacchetto o solo del documento escluso): sempre zero, mai scalata.</summary>
    public const int MaxUpstreamLostConflicts = 0;

    /// <summary>Un conteggio rispetta «≤ 2 su 10»: bad/total ≤ 2/10, in interi (solo per le citazioni).</summary>
    public static bool WithinPer10(int bad, int total, int maxPer10) => bad * 10 <= maxPer10 * Math.Max(total, 1);

    /// <summary>Conflitti persi ammessi dato il numero di casi OSSERVATI: il riferimento del laboratorio si scala solo da un campione ≥ a quello del laboratorio (arrotondando per difetto); sotto, zero.</summary>
    public static int AllowedLostConflicts(int observedCases) =>
        observedCases >= LabConflictReferenceCases ? LabConflictLostMax * observedCases / LabConflictReferenceCases : 0;

    private const string AbsentQuestion = "Qual è il numero di telefono dell'assistenza clienti?";

    public static readonly ExtensionDocument PartialDocument = new(
        "t040-parziale", "t040-formazione-operatori.txt",
        "Formazione degli operatori\n\nL'appaltatore deve erogare la formazione degli operatori del cliente in tre giornate presso la sede del cliente.\n", false);

    public static readonly ExtensionDocument ExcludedDocument = new(
        "t040-escluso", "t040-addendum-garanzie-reali.txt",
        "Addendum sulle garanzie reali\n\nIl pegno sulle attrezzature non deve superare cinquantamila euro.\n", true);

    public static IReadOnlyList<ExtensionDocument> ExtensionDocuments { get; } = [PartialDocument, ExcludedDocument];

    private static readonly CampaignExpectation Partial = new(
        "partial", "partial", "Quali sono la durata e il costo della formazione degli operatori del cliente?", "formazione degli operatori",
        "Open", ["Partial"], [["tre giornate"]], ["cinquantamila"], false);

    private static readonly CampaignExpectation Excluded = new(
        "excluded", "excluded", "Qual è il limite del pegno sulle attrezzature?", "pegno sulle attrezzature",
        "Open", ["NoAnswer"], [], ["cinquantamila", "pegno sulle attrezzature non deve"], false);

    private static CampaignExpectation Absent() => new("absent", "absent", AbsentQuestion, "numero di telefono", "Open", ["NoAnswer"], [], [], false);

    public static readonly CampaignScenario Single = new("T040-single", "T082 (documento singolo)",
    [
        new("present", "present", "Per quanti mesi il portale Aurora conserva i documenti di trasporto?", "conserva i documenti di trasporto", "AnsweredInSnapshot", ["Answered"], [["84 mesi"]], [], false),
        Partial,
        Absent(),
        new("conflict", "conflict", "Il termine di pagamento delle fatture è di 30 o di 60 giorni?", "30 o di 60 giorni", "Open", ["Ambiguous", "Partial"], [["30 giorni"], ["60 giorni"]], [], true),
        Excluded,
    ]);

    public static readonly CampaignScenario Multi = new("T040-multi", "T083 (tre documenti)",
    [
        new("present", "present", "Qual è la durata del contratto quadro Zefiro?", "durata del contratto quadro", "AnsweredInSnapshot", ["Answered"], [["48 mesi"]], [], false),
        Partial,
        Absent(),
        new("conflict", "conflict", "Qual è la durata della garanzia sui ricambi?", "garanzia sui ricambi", "Open", ["Ambiguous", "Partial"], [["24 mesi"], ["36 mesi"]], [], true),
        Excluded,
    ]);

    /// <summary>Ordine UNICO e fisso della campagna: Single poi Multi. L'esecuzione lo segue in un solo test (<see cref="CampaignSequencer"/>), mai per ordine implicito del framework di test.</summary>
    public static IReadOnlyList<CampaignScenario> Sequence { get; } = [Single, Multi];

    public static Corpus BuildCorpus(CampaignScenario scenario) =>
        scenario == Single ? LargeDocumentCorpus.Single(FillerChars, SingleBulkRules) : LargeDocumentCorpus.Multi(FillerChars, MultiBulkRulesPerDocument);

    // ================================================================ manifesto congelato

    /// <summary>
    /// Hash del manifesto congelato (SHA-256 del JSON compatto di <see cref="BuildManifestJson"/>): versione 5, preparata per il Run 4 (T040 Run 3 non superato, definizione v9 con profilo di schema
    /// <c>reconcile-alias-schema-by-batch-2</c> e prompt <c>document-reconcile-v4</c> invariato). Cambiarlo è un atto esplicito di ri-congelamento, mai un adattamento a un risultato.
    /// </summary>
    public const string FrozenManifestSha256 = "1646797463253cc9d786964e6cc056046ca3192f86adcd2973272f947e85ede1";

    /// <summary>Hash della versione 4 del manifesto: quella USATA dal Run 3 (2026-11-11, M3-real.md, esito non superato). Conservata invariata in `T040-campaign-manifest-v4-run3.json`; i risultati del Run 3 si riferiscono a questa configurazione (definizione v8).</summary>
    public const string Run3ManifestSha256 = "91a1d09676e038b5bbe4bd122638704a4b8831105d8579a5c110aa339338ebaf";

    /// <summary>Hash della versione 3 del manifesto: quella USATA dal Run 2 (2026-11-10, M3-real.md, esito non superato). Conservata invariata in `T040-campaign-manifest-v3-run2.json`; i risultati del Run 2 si riferiscono a questa configurazione (definizione v7).</summary>
    public const string Run2ManifestSha256 = "6aad99f8b78e81f421d78a38619b16ec0496ee0b7dc926e24b4a36890f293181";

    /// <summary>Hash della versione 2 del manifesto: quella USATA dal Run 1 (2026-11-10, M3-real.md, esito non superato). Conservata invariata in `T040-campaign-manifest-v2-run1.json`; i risultati del Run 1 si riferiscono a questa configurazione.</summary>
    public const string Run1ManifestSha256 = "fbc0ecb348c8568bf8f799a0c1c4b79161087e95c2f2f2621fb2f713da79eefb";

    // ---- identità della pipeline di analisi per il Run 3 (letterali: congelati qui, mai letti dal prodotto; il preflight li confronta con quello che il prodotto realmente congela e invia).

    /// <summary>Definizione di workflow del Run 4: v9 = v8 con il profilo di schema per batch <c>-2</c> (sei varianti). Il Run 3 usava la v8 (<see cref="Run3DefinitionVersion"/>), il Run 2 la v7 (<see cref="Run2DefinitionVersion"/>).</summary>
    public const string DefinitionVersion = "project-analysis-documents-v9";

    public const string Run3DefinitionVersion = "project-analysis-documents-v8";

    public const string Run2DefinitionVersion = "project-analysis-documents-v7";

    /// <summary>Prompt della gerarchia di riconciliazione della v8 e SHA-256 della risorsa incorporata (testo UTF-8, senza trasformazioni).</summary>
    public const string ReconcilePromptVersion = "document-reconcile-v4";

    public const string ReconcilePromptSha256 = "d3e46528736e856eedfa43e1fa41785b0a87ab7ddba91881f57bb9c09c0da79d";

    /// <summary>Prompt della v7 (Run 2), invariato e ancora in uso per le definizioni v4–v7.</summary>
    public const string Run2ReconcilePromptVersion = "document-reconcile-v3";

    public const string Run2ReconcilePromptSha256 = "0503d17360e21e8f72e357d1fcf0f0e32fbead294d970193af4eb61ff847c9e5";

    /// <summary>Profilo degli schemi della gerarchia del Run 4 (chiave di freeze <c>reconcileSchemaProfile</c>) e hash canonici (RFC 8785, SHA-256) delle sei varianti, nell'ordine di <see cref="ReconcileBatchSchema"/>. Copia dei golden di <c>ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes</c>, mai letta dal prodotto.</summary>
    public const string ReconcileSchemaProfile = "reconcile-alias-schema-by-batch-2";

    public static readonly IReadOnlyDictionary<string, string> ReconcileSchemaVariantHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LeafA"] = "17001aef9ed768714129aba3f5d4b861e8e7d36632e6eaf78c7cf60c1ee49f09",
        ["LeafB"] = "800f277ba1daa4d14ed0694d2dbea68add81ebb8dd1915817ab1df3a6a1aa4ce",
        ["Reduce"] = "06058502b68f2d19363b1bb408bfd613b4ccafd3f8e6453c3c32eba43ba56b6c",
        ["Cross"] = "bab4a2acd070c70569fb5b80c7ec251a1f9bff809f8f6e701d83dda6e659d5f9",
        ["Expand"] = "37e08c9aa05d07b145dbee84d0775061f9f6c8735b670265cbbff1fd874cc97b",
        ["Compare"] = "b8c471d1b076af5e9fae32761d66966315fefc1b05f5c4c667714dcf808914a3",
    };

    /// <summary>Profilo degli schemi del Run 3 (definizione v8) e hash delle sue tre varianti, nell'ordine di <see cref="ReconcileSchemaVariant"/>: invariati e ancora prodotti dal prodotto per le run congelate v8.</summary>
    public const string Run3ReconcileSchemaProfile = "reconcile-alias-schema-by-batch-1";

    public static readonly IReadOnlyDictionary<string, string> Run3ReconcileSchemaVariantHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Full"] = FullSchemaHash,
        ["NoItems"] = "7b0a15de8844ecea8b5daa15009fe838bde652e261540074b3ac563bce6952b2",
        ["Candidate"] = "facb96cb31ec0f0fc2ee8a42a0cf899a95cbe5af4d90abac4f163df6c5984f38",
    };

    /// <summary>Hash canonico dello schema completo <c>reconcile-alias-1</c> (foglia della vista A nella v8; controllo delle verifiche reali dello schema). Invariato dalla v4.</summary>
    public const string FullSchemaHash = "648915d419475bb78220e92aecebe0fd398c47a2032f2cee6e031d7be1535c54";

    /// <summary>
    /// BASELINE DEL CODICE DI PRODOTTO (<c>backend/src</c>): commit che ha introdotto la definizione v9 e il profilo <c>-2</c> (<c>feat(backend): definizione v9…</c>). Dopo di esso sono cambiati solo test e documenti.
    /// Distinto dal COMMIT DI LANCIO (HEAD effettivo del Run 4), che il manifesto non può contenere (il commit che lo versiona non può citare sé stesso) e che l'harness rileva e registra al lancio
    /// (<see cref="LaunchRecord"/>), rifiutando ogni codice di prodotto diverso da questo baseline.
    /// </summary>
    public const string CodeBaseline = "184bed5";

    /// <summary>Baseline del codice di prodotto del manifesto v4 (Run 3): la v8 e il prompt v4 erano completi.</summary>
    public const string Run3CodeBaseline = "65cdcc8";

    /// <summary>Regola registrata nel manifesto per il commit di lancio: non è un valore, perché il commit che versiona il manifesto non può citare sé stesso.</summary>
    public const string LaunchCommitRule =
        "non registrato nel manifesto: e l'HEAD effettivo al momento del lancio, rilevato dall'harness (git rev-parse HEAD) e scritto nel rapporto diagnostico di ogni scenario (sezione launch); "
        + "il preflight lo rifiuta (NO-GO) se non rilevabile, se il baseline non e suo antenato, se backend/src o backend/global.json differiscono dal baseline (anche non committati o non tracciati) "
        + "o se i file tracciati di backend e specs hanno modifiche non committate (il commit di lancio deve riprodurre harness, test e manifesto realmente eseguiti)";

    /// <summary>Modalità di output strutturato richiesta: lo schema JSON viene inviato al provider. Con <c>ValidatedTextJson</c> nessuno schema viaggia e la v8 si comporterebbe come la v7.</summary>
    public const StructuredOutputMode RequiredStructuredOutputMode = StructuredOutputMode.JsonSchema;

    /// <summary>Fattore di stima dell'output dell'estrazione fissato per il Run 2 (CANDIDATO, non validato; il Run 1 usava 8,0). Deve coincidere con <see cref="AiOptions.ExtractionOutputExpansion"/> risolta dal runtime.</summary>
    public const double ExtractionOutputExpansion = 16.0;

    /// <summary>Etichetta di calibrazione attesa (`PlannerLimits.ExtractionCalibrationVersion`): un'etichetta diversa significa un altro codice di calibrazione rispetto a quello congelato.</summary>
    public const string ExpectedExtractionCalibration = "extraction-output-expansion-2026-11-10";

    /// <summary>Hash della versione 1 del manifesto (d05e93f), mai usata da una campagna reale: sostituita PRIMA di ogni inferenza.</summary>
    public const string SupersededManifestSha256 = "662431340021f99eb094814adc730eca54ba9776000412ea8df0fd9c3552c035";

    /// <summary>Motivo del ri-congelamento (versione 2). Corpus, gold, domande, attese e testi d'estensione NON cambiano: lo verificano gli hash per scenario nel manifesto e i test sul generatore.</summary>
    public const string FreezeReasonV2 =
        "Nessuna campagna DGX eseguita con la versione 1. Correzioni dell'harness decise prima dell'inferenza: identificativo del modello LLM fissato (Qwen3.6-35B-Smart); soglia dei conflitti persi riscritta secondo SC-003 "
        + "(riferimento del laboratorio 1-2 su 10 casi, tolleranza zero sotto i 10 casi, zero perdite per esclusione a monte); configurazione delle misure di latenza per Gap; ordine Single poi Multi in un solo test. Nessun adattamento delle attese ai risultati.";

    /// <summary>Motivo del ri-congelamento (versione 3), dopo il Run 1 non superato (finish=length in estrazione). Nessun adattamento di corpus, gold, domande, attese o soglie.</summary>
    public const string FreezeReasonV3 =
        "Dopo il Run 1 (estrazione troncata a finish=length, diagnosi offline in M3-real.md) il fattore di stima dell'output dell'estrazione e stato reso esplicito e ricalibrato in via preventiva da 8,0 a 16,0 (candidato, non validato); "
        + "il manifesto lo registra e il preflight rifiuta (NO-GO) ogni configurazione risolta diversa. Corpus, gold, domande, attese, soglie, finestra, modello, prompt e schema non cambiano.";

    /// <summary>Motivo del ri-congelamento (versione 4), dopo il Run 2 non superato (work_output_invalid sulla chiamata 026, indici non costruiti). Nessun adattamento di corpus, gold, domande, attese, soglie, politica, modelli o parametri di estrazione.</summary>
    public const string FreezeReasonV4 =
        "Dopo il Run 2 (riconciliazione della foglia della vista B: items non ammessi, chiamata 026; diagnosi offline in M3-real.md) la definizione dell'analisi e passata da project-analysis-documents-v7 a v8 con il prompt document-reconcile-v4 "
        + "e il profilo degli schemi per batch reconcile-alias-schema-by-batch-1 (decisione del committente 2026-11-11). Il manifesto registra definizione, prompt con SHA-256, profilo e hash delle tre varianti dello schema; "
        + "il preflight rifiuta (NO-GO, prima di ogni inferenza) ogni configurazione risolta o identita del prodotto diversa, compresa una modalita di output strutturato diversa da JsonSchema. "
        + "Corpus, gold, domande, attese, soglie, politica, modelli, finestra e parametri di estrazione non cambiano.";

    /// <summary>Motivo del ri-congelamento (versione 5), dopo il Run 3 non superato (work_output_invalid sulla chiamata 026: descriptor null ammesso dallo schema v8 e vietato dal validatore). Nessun adattamento di corpus, gold, domande, attese, soglie, politica, modelli, parametri di estrazione o prompt.</summary>
    public const string FreezeReason =
        "Dopo il Run 3 (foglia della vista B: descriptor null, chiamata 026, discrepanza fra schema e validatore; diagnosi offline in M3-real.md) la definizione dell'analisi e passata da project-analysis-documents-v8 a v9 "
        + "con il profilo degli schemi per batch reconcile-alias-schema-by-batch-2 a sei varianti (decisione del committente 2026-11-11); il prompt document-reconcile-v4 e invariato. I vincoli strutturali della v9 (descriptor, estremi dei candidati) "
        + "sono stati verificati realmente su llama.cpp b10703 il 2026-11-11 (sei vincoli Honored, rapporto SHA-256 309D0C8A1E63C9D8569A769471FBD2E7EA5590EBEB6B9740DC8E1C8F6C0D25EE; esiti NotExercised dichiarati in M3-real.md). "
        + "Il manifesto registra definizione, profilo e hash delle sei varianti; il preflight rifiuta (NO-GO, prima di ogni inferenza) ogni identita del prodotto diversa e ogni codice di prodotto diverso dal baseline. "
        + "Corpus, gold, domande, attese, soglie, politica, modelli, finestra, parametri di estrazione, output strutturato e prompt non cambiano.";

    /// <summary>Caratteristiche del modello che richiedono una verifica REALE (non verificabili offline) e che la campagna registra senza dichiararle verificate.</summary>
    public static readonly string[] LlmUnverifiedCharacteristics = ["pesi effettivamente caricati", "quantizzazione", "artefatto/file del modello", "finestra di contesto effettiva (misurata dal profilo di runtime)", "comportamento del ragionamento (thinking=0 richiesto)"];

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static string BuildManifestJson()
    {
        static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        static string Hash(object value) => Sha(JsonSerializer.Serialize(value, Compact));

        object Scenario(CampaignScenario s)
        {
            var corpus = BuildCorpus(s);
            return new
            {
                s.Label,
                s.Origin,
                corpus = corpus.Name,
                bulkRules = corpus.Documents.Sum(d => d.BulkRules.Count),
                fillerChars = FillerChars,
                pmStatement = corpus.PmStatement,
                documents = corpus.Documents.Select(d => new { d.Key, d.FileName, format = d.Format.ToString(), d.PlainTextSha256, chars = d.PlainText.Length, rules = d.BulkRules.Count }).ToArray(),
                goldSha256 = Hash(new { corpus.Gold, corpus.Conflicts, corpus.Equivalents }),
                questions = s.Expectations.Select(e => e.Question).ToArray(),
                expectations = s.Expectations,
            };
        }

        var manifest = new
        {
            campaign = Id,
            definition = DefinitionVersion,
            config = new
            {
                window = WindowTokens, output = OutputTokens, margin = MarginTokens, budget = BudgetTokens, thinking = Thinking,
                policy = PolicyVersion, policyHash = RetrievalPolicy.V1.Hash(),
                embedding = new { id = EmbeddingModelId, artifact = EmbeddingArtifact, dimension = EmbeddingDimension },
                reranker = new { id = RerankerModelId, artifact = RerankerArtifact },
                indexTextVersion = RetrievalOptions.IndexTextVersion, fts = RetrievalOptions.FtsConfiguration,
                extraction = new { outputExpansion = ExtractionOutputExpansion, calibration = ExpectedExtractionCalibration, option = "Ai:ExtractionOutputExpansion", previousRun1Value = PlannerLimits.DefaultOutputExpansion, status = "candidato, non validato da misura reale completa", preflight = "le AiOptions risolte dal runtime devono riportare questo valore, altrimenti NO-GO prima di ogni inferenza" },
                llm = new { id = LlmModelId, source = "identificativo di configurazione confermato dal committente (2026-11-10); pesi, quantizzazione e artefatto caricato NON verificati offline", unverified = LlmUnverifiedCharacteristics },
                latency = new { perGap = LatencyReport.PerGapPhases, shared = LatencyReport.SharedPhases, attribution = "per Gap: search e rerank; fasi condivise e composizione del pacchetto riportate a parte, mai divise per Gap" },
                reconcile = new
                {
                    prompt = new { version = ReconcilePromptVersion, sha256 = ReconcilePromptSha256, previousVersion = Run2ReconcilePromptVersion, previousSha256 = Run2ReconcilePromptSha256, note = "il v3 resta invariato e in uso per le definizioni v4-v7" },
                    schemaProfile = new
                    {
                        key = DocumentAnalysisWorkflow.ReconcileSchemaProfileKey, value = ReconcileSchemaProfile, variantHashes = ReconcileSchemaVariantHashes,
                        hashAlgorithm = "SHA-256 della forma canonica RFC 8785 dello schema",
                        providerFeatures = new[]
                        {
                            "descriptor type:string+minLength:1 (LeafA, LeafB, Reduce) e type:null (Cross, Expand, Compare)",
                            "estremi dei candidati pattern ^E (LeafA, LeafB, Expand, Compare) e ^N (Reduce, Cross)",
                            "maxItems:0 su items (tutte salvo LeafA) e su patches (LeafA, LeafB, Reduce, Cross); type:null su focus (LeafA, LeafB, Reduce, Cross); enum ridotto di focus.outcome (Expand, Compare)",
                        },
                        providerSupport = "Verificato realmente su llama.cpp b10703 (ReconcileSchemaV9SupportRealTests, 2026-11-11, quattro richieste, rapporto SHA-256 309D0C8A1E63C9D8569A769471FBD2E7EA5590EBEB6B9740DC8E1C8F6C0D25EE): sei vincoli Honored "
                            + "(descriptor minLength:1 su LeafB e Reduce, descriptor null su Cross, estremi ^E su LeafB, estremi ^N su Reduce e Cross). NotExercised, inferiti e NON provati: descriptor type:string non null, LeafA, Expand e Compare "
                            + "(sottoschemi identici a quelli esercitati); maxItems:0 su items e patches e focus null misurati sulla v8, non ripetuti. Compatibilita dello schema, non validazione end-to-end: il validatore della gerarchia resta l'unica garanzia "
                            + "sulle regole che la grammatica non esprime (D9-D12, soli spazi, lunghezza del descriptor)",
                    },
                    freezeKeys = new { reconcile = ReconcilePromptVersion, reconcileSchemaProfile = ReconcileSchemaProfile },
                    preflight = "definizione, prompt (versione e SHA-256), profilo, hash delle varianti dello schema e chiavi di freeze del prodotto devono coincidere con questi valori, altrimenti NO-GO prima di ogni inferenza",
                },
                structuredOutput = new
                {
                    mode = RequiredStructuredOutputMode.ToString(),
                    scope = "Ai:StructuredOutputMode e globale: vale anche per la fase Analysis",
                    reason = "con ValidatedTextJson nessuno schema viene inviato e la v8 equivarrebbe alla v7",
                    preflight = "le AiOptions risolte dal runtime devono riportare questo valore, altrimenti NO-GO prima di ogni inferenza",
                },
            },
            thresholds = new
            {
                maxInvalidCitationsPer10 = MaxInvalidCitationsPer10,
                lostConflicts = new { labReference = $"{LabConflictLostMax} su {LabConflictReferenceCases} casi", scaledOnlyFromCases = LabConflictReferenceCases, belowThatTolerance = 0, upstream = MaxUpstreamLostConflicts },
                falseAnsweredInSnapshot = 0,
            },
            freeze = new
            {
                version = 5,
                supersedes = Run3ManifestSha256,
                codeBaseline = CodeBaseline,
                launchCommit = LaunchCommitRule,
                history = new object[]
                {
                    new { version = 1, sha256 = SupersededManifestSha256, usedByRun = "nessuno (sostituita prima di ogni inferenza)" },
                    new { version = 2, sha256 = Run1ManifestSha256, usedByRun = "Run 1 (2026-11-10): non superato in estrazione, risultati invariati in M3-real.md; copia in T040-campaign-manifest-v2-run1.json" },
                    new { version = 3, sha256 = Run2ManifestSha256, usedByRun = "Run 2 (2026-11-10): non superato (work_output_invalid sulla riconciliazione, definizione v7), risultati invariati in M3-real.md; copia in T040-campaign-manifest-v3-run2.json" },
                    new { version = 4, sha256 = Run3ManifestSha256, usedByRun = "Run 3 (2026-11-11): non superato (work_output_invalid sulla riconciliazione, descriptor null alla chiamata 026, definizione v8), risultati invariati in M3-real.md; copia in T040-campaign-manifest-v4-run3.json" },
                },
                reason = FreezeReason,
                configDifferenceFromRun3 = "differenze rispetto al manifesto v4 (Run 3), uniche: definition project-analysis-documents-v8 -> project-analysis-documents-v9; config.reconcile.schemaProfile (profilo reconcile-alias-schema-by-batch-1 -> reconcile-alias-schema-by-batch-2, "
                    + "hash delle varianti: tre -> sei, providerFeatures e providerSupport con l'esito della verifica reale) e config.reconcile.freezeKeys.reconcileSchemaProfile; freeze v5 (version, supersedes, codeBaseline, launchCommit, history, reason, differenza). "
                    + "Corpus, gold, domande, attese, soglie, politica, modelli, finestra, parametri di estrazione (ExtractionOutputExpansion 16.0), output strutturato e prompt document-reconcile-v4 (stesso SHA-256) invariati.",
            },
            seed = LargeDocumentCorpus.Seed,
            extension = ExtensionDocuments.Select(d => new { d.Key, d.FileName, d.ExcludedByPm, textSha256 = Sha(d.Text) }).ToArray(),
            scenarios = new[] { Scenario(Single), Scenario(Multi) },
        };
        return JsonSerializer.Serialize(manifest, Compact);
    }

    public static string ManifestSha256() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BuildManifestJson())));

    // ================================================================ rifiuto di configurazioni incompatibili (PRIMA di upload e inferenze)

    /// <summary>
    /// Verifica pura (nessuna rete) di manifesto e ambiente. Restituisce i problemi trovati: vuoto = configurazione compatibile. Le variabili dell'ambiente di test che modificherebbero finestra, output,
    /// margine o ragionamento sono rifiutate se diverse dal valore fissato; il riordino non è sovrascrivibile (nessun <c>BPROJECTS_EVAL_RERANKER_MODEL_ID</c> diverso dal fissato).
    /// </summary>
    public static IReadOnlyList<string> ValidateEnvironment(Func<string, string?> read)
    {
        var problems = new List<string>();
        if (!string.Equals(ManifestSha256(), FrozenManifestSha256, StringComparison.Ordinal))
        {
            problems.Add($"il manifesto della campagna ({ManifestSha256()}) non coincide con l'hash congelato ({FrozenManifestSha256}): corpus, domande, attese o configurazione sono cambiati dopo il congelamento");
        }

        void Fixed(string variable, string expected)
        {
            if (read(variable) is { Length: > 0 } actual && actual != expected)
            {
                problems.Add($"{variable}={actual} diverso dal valore fissato {expected}");
            }
        }

        Fixed("BPROJECTS_TEST_CTX_WINDOW", WindowTokens.ToString(CultureInfo.InvariantCulture));
        Fixed("BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT", OutputTokens.ToString(CultureInfo.InvariantCulture));
        Fixed("BPROJECTS_TEST_SAFETY_MARGIN", MarginTokens.ToString(CultureInfo.InvariantCulture));
        Fixed("BPROJECTS_TEST_ANALYSIS_THINKING", "0");
        Fixed("BPROJECTS_EVAL_RERANKER_MODEL_ID", RerankerModelId);
        if (read(ExtractionExpansionVariable) is { Length: > 0 } expansion
            && !(double.TryParse(expansion, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed == ExtractionOutputExpansion))
        {
            problems.Add($"{ExtractionExpansionVariable}={expansion} diverso dal valore fissato {ExtractionOutputExpansion.ToString(CultureInfo.InvariantCulture)}");
        }

        var model = read(TestEnvironment.DgxModelVariable);
        if (string.IsNullOrWhiteSpace(model))
        {
            problems.Add($"{TestEnvironment.DgxModelVariable} non impostata: il modello LLM della campagna è {LlmModelId} e deve essere dichiarato");
        }
        else if (!string.Equals(model, LlmModelId, StringComparison.Ordinal))
        {
            problems.Add($"{TestEnvironment.DgxModelVariable}={model} diverso dal modello fissato {LlmModelId}");
        }

        return problems;
    }

    public const string ExtractionExpansionVariable = "BPROJECTS_TEST_EXTRACTION_OUTPUT_EXPANSION";

    /// <summary>
    /// Verifica delle <see cref="AiOptions"/> RISOLTE dal runtime (quelle che il planner di estrazione legge davvero) contro il fattore e l'etichetta di calibrazione congelati. Null = compatibile; altrimenti NO-GO PRIMA di ogni upload e inferenza.
    /// </summary>
    public static string? RejectOptions(AiOptions options)
    {
        var problems = new List<string>();
        if (options.ExtractionOutputExpansion != ExtractionOutputExpansion)
        {
            problems.Add($"ExtractionOutputExpansion risolta {options.ExtractionOutputExpansion.ToString(CultureInfo.InvariantCulture)} invece di {ExtractionOutputExpansion.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.Equals(PlannerLimits.ExtractionCalibrationVersion, ExpectedExtractionCalibration, StringComparison.Ordinal))
        {
            problems.Add($"etichetta di calibrazione {PlannerLimits.ExtractionCalibrationVersion} invece di {ExpectedExtractionCalibration}");
        }

        // Output strutturato: Ai:StructuredOutputMode e globale e quindi vale per la fase Analysis. Qualunque valore diverso da JsonSchema (anche un valore numerico non definito) e NO-GO: con
        // ValidatedTextJson InternalModelClient non invia alcuno schema e la v8 equivarrebbe alla v7 senza che nulla lo segnali.
        if (options.StructuredOutputMode != RequiredStructuredOutputMode)
        {
            problems.Add($"StructuredOutputMode risolta {options.StructuredOutputMode} invece di {RequiredStructuredOutputMode}: nessuno schema sarebbe inviato al provider");
        }

        // Fase Analysis come risolta dal runtime (quella che usano estrazione di gerarchia e stadio Gap): output, ragionamento e modello fissati dalla campagna.
        var analysis = options.ForPhase(AiPhase.Analysis);
        if (analysis.MaxOutputTokens != OutputTokens)
        {
            problems.Add($"output della fase Analysis risolto {analysis.MaxOutputTokens} invece di {OutputTokens}");
        }

        if (analysis.EnableThinking != Thinking)
        {
            problems.Add($"ragionamento della fase Analysis risolto {analysis.EnableThinking} invece di {Thinking}");
        }

        if (!string.Equals(analysis.ModelId, LlmModelId, StringComparison.Ordinal))
        {
            problems.Add($"modello della fase Analysis risolto «{analysis.ModelId}» invece di {LlmModelId}");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>
    /// Identità della pipeline di analisi come la vedrebbe una NUOVA richiesta di Analizza con il recupero abilitato: definizione congelata, prompt di riconciliazione (versione dichiarata nel freeze, versione
    /// effettivamente instradata e SHA-256 della risorsa incorporata), profilo degli schemi con hash canonico delle varianti e contratto riconosciuto da <c>ContractOf</c>. Letta dal prodotto, mai da file.
    /// </summary>
    internal sealed record AnalysisIdentity(
        string Definition, int? Contract, string FrozenReconcilePrompt, string DispatchedReconcilePrompt, string ReconcilePromptSha256, string? FrozenSchemaProfile,
        IReadOnlyDictionary<string, string> SchemaVariantHashes)
    {
        /// <summary>
        /// Identità del prodotto per una NUOVA richiesta di Analizza con il recupero, letta dallo stesso freeze dell'endpoint (<see cref="SourceSnapshotStore.DocumentaryAnalysisFreeze"/>), mai da costanti
        /// della prova; con <paramref name="retrieval"/> RISOLTE dall'applicazione in prova (altrimenti opzioni sintetiche con la politica 1, solo per i test). Dalla v9 il prodotto congela la v9: un
        /// manifesto legato alla v8 (T040 v4) è NO-GO prima di ogni upload e inferenza.
        /// </summary>
        public static AnalysisIdentity FromProduct(RetrievalOptions? retrieval = null)
        {
            var (definition, json) = SourceSnapshotStore.DocumentaryAnalysisFreeze(useRetrieval: true, retrieval ?? new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 });
            return ForFreeze(definition, json);
        }

        /// <summary>Identità di un freeze dato (definizione e versioni congelate): contratto, prompt instradato, SHA della risorsa e hash delle varianti del profilo congelato, come li calcola il prodotto.</summary>
        public static AnalysisIdentity ForFreeze(string definition, string promptVersions)
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(promptVersions)!;
            var run = new ProcessingRun { WorkflowDefinitionVersion = definition, MafVersion = Conversation.CurrentMafVersion, PromptVersions = promptVersions };
            var frozenPrompt = keys.GetValueOrDefault("reconcile") ?? string.Empty;
            var profile = keys.GetValueOrDefault(DocumentAnalysisWorkflow.ReconcileSchemaProfileKey);
            var variants = profile is ReconcileAliasContracts.SchemaProfileByBatch or ReconcileAliasContracts.SchemaProfileByBatchV2 or ReconcileAliasContracts.SchemaProfileByBatchV3
                ? ReconcileAliasContracts.ProfileVariants(profile).ToDictionary(v => v.Name, v => CanonicalJson.HashText(CanonicalJson.Canonicalize(v.Schema.GetRawText())), StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return new(
                definition, DocumentAnalysisWorkflow.ContractOf(run), frozenPrompt, DocumentAnalysisWorkflow.ReconcilePromptOf(definition), EmbeddedPromptSha256(frozenPrompt), profile, variants);
        }

        /// <summary>
        /// Identità della v9 come la congelava il prodotto fino alla v10 (quella del manifesto T040 v5 e del Run 4). Dalla v10 il prodotto congela la v10 tramite il normale percorso: un Run con il manifesto v5
        /// (definizione v9) è NO-GO finché non esiste un manifesto v6 (non ancora congelato). Questa identità serve solo a provare che il manifesto v5 archiviato resta riproducibile.
        /// </summary>
        public static AnalysisIdentity V9(RetrievalOptions? retrieval = null) =>
            ForFreeze(SourceSnapshotStore.AnalysisDefinitionV9, SourceSnapshotStore.AnalysisPromptVersionsV9(retrieval ?? new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 }));

        /// <summary>Identità della v8 come la congelava il prodotto fino alla v9 (quella del manifesto T040 v4 e del Run 3).</summary>
        public static AnalysisIdentity V8(RetrievalOptions? retrieval = null) =>
            ForFreeze(SourceSnapshotStore.AnalysisDefinitionV8, SourceSnapshotStore.AnalysisPromptVersionsV8(retrieval ?? new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 }));
    }

    /// <summary>
    /// Verifica delle <see cref="RetrievalOptions"/> RISOLTE dall'applicazione in prova: il recupero deve essere abilitato (altrimenti una nuova richiesta congelerebbe la v6, senza recupero), con la politica, i modelli e
    /// gli artefatti fissati. Null = compatibile; altrimenti NO-GO PRIMA di ogni upload e inferenza.
    /// </summary>
    public static string? RejectRetrievalOptions(RetrievalOptions options)
    {
        var problems = new List<string>();
        if (!options.Enabled)
        {
            problems.Add("recupero non abilitato: una nuova richiesta di Analizza congelerebbe la v6, senza recupero");
        }

        void Same(string what, object? found, object expected)
        {
            if (!Equals(found, expected))
            {
                problems.Add($"{what} risolto «{found}» invece di «{expected}»");
            }
        }

        Same("politica di recupero", options.PolicyVersion, PolicyVersion);
        Same("modello di embedding", options.Embedding.ModelId, EmbeddingModelId);
        Same("artefatto di embedding", options.Embedding.Artifact, EmbeddingArtifact);
        Same("dimensione dell'embedding", options.Embedding.Dimension, EmbeddingDimension);
        Same("modello di riordino", options.Reranker.ModelId, RerankerModelId);
        Same("artefatto di riordino", options.Reranker.Artifact, RerankerArtifact);
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>SHA-256 (UTF-8, senza trasformazioni) della risorsa incorporata di un prompt della riconciliazione; stringa vuota se la risorsa non esiste.</summary>
    internal static string EmbeddedPromptSha256(string version)
    {
        using var stream = typeof(ReconcileAliasContracts).Assembly.GetManifestResourceStream($"Prompts.{version}.md");
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reader.ReadToEnd())));
    }

    /// <summary>
    /// Verifica PURA (nessuna rete, nessun database) che definizione, prompt, schemi e chiavi di freeze realmente prodotti dal prodotto coincidano con quelli congelati nel manifesto e con la definizione attesa
    /// dalla prova (<paramref name="expectedDefinition"/>, quella che il controllo strutturale confronterebbe, ma solo a run concluso). Vuoto = coerente; altrimenti NO-GO PRIMA di ogni upload e inferenza.
    /// </summary>
    internal static IReadOnlyList<string> RejectIdentity(AnalysisIdentity actual, string expectedDefinition)
    {
        var problems = new List<string>();
        void Same(string what, string? found, string expected)
        {
            if (!string.Equals(found, expected, StringComparison.Ordinal))
            {
                problems.Add($"{what}: il prodotto riporta «{found}» invece di «{expected}»");
            }
        }

        Same("definizione congelata da una nuova richiesta di Analizza", actual.Definition, DefinitionVersion);
        Same("definizione attesa dalla prova (controllo strutturale)", expectedDefinition, DefinitionVersion);
        Same("chiave di freeze reconcile (prompt)", actual.FrozenReconcilePrompt, ReconcilePromptVersion);
        Same("prompt di riconciliazione realmente instradato per la definizione", actual.DispatchedReconcilePrompt, ReconcilePromptVersion);
        Same($"SHA-256 della risorsa incorporata {ReconcilePromptVersion}", actual.ReconcilePromptSha256, ReconcilePromptSha256);
        Same("chiave di freeze reconcileSchemaProfile", actual.FrozenSchemaProfile, ReconcileSchemaProfile);
        Same("profilo degli schemi supportato dal prodotto", ReconcileAliasContracts.SchemaProfileByBatchV2, ReconcileSchemaProfile);
        if (actual.Contract != 9)
        {
            problems.Add($"contratto riconosciuto da ContractOf per il freeze v9: {actual.Contract?.ToString(CultureInfo.InvariantCulture) ?? "nessuno (workflow_incompatible)"} invece di 9");
        }

        foreach (var (variant, expected) in ReconcileSchemaVariantHashes)
        {
            Same($"hash canonico dello schema {variant}", actual.SchemaVariantHashes.GetValueOrDefault(variant), expected);
        }

        foreach (var extra in actual.SchemaVariantHashes.Keys.Except(ReconcileSchemaVariantHashes.Keys, StringComparer.Ordinal))
        {
            problems.Add($"variante dello schema non congelata nel manifesto: {extra}");
        }

        return problems;
    }

    /// <summary>
    /// Identità di LANCIO, distinta dal baseline del codice di prodotto del manifesto (<see cref="CodeBaseline"/>): il commit effettivo (HEAD) con cui parte il Run, se il baseline ne è antenato e se il codice di prodotto
    /// (<see cref="ProductPaths"/>, anche non committato o non tracciato) è identico al baseline e se i file tracciati di <c>backend</c> e <c>specs</c> (harness, test, manifesto) coincidono con HEAD, così che il commit di lancio
    /// riproduca ciò che gira davvero. Un valore non rilevabile (<c>null</c>) è NO-GO, mai «presumibilmente uguale». I file NON tracciati fuori da <c>backend/src</c> (es. <c>docs/acceptance-trace</c>) sono ignorati.
    /// </summary>
    internal sealed record LaunchRecord(string? LaunchCommit, bool? BaselineIsAncestor, bool? ProductCodeUnchanged, bool? TrackedTreeClean, string? Error)
    {
        /// <summary>Percorsi del codice di prodotto e della toolchain che ne determina il comportamento: devono coincidere con il baseline.</summary>
        public static readonly IReadOnlyList<string> ProductPaths = ["backend/src", "backend/global.json"];

        /// <summary>Percorsi i cui file tracciati devono essere senza modifiche non committate al lancio: il commit di lancio deve riprodurre harness, test e manifesto realmente eseguiti.</summary>
        public static readonly IReadOnlyList<string> TrackedPaths = ["backend", "specs"];

        /// <summary>
        /// Rileva l'identità di lancio con <c>git</c> (nessuna rete, sola lettura: <c>GIT_OPTIONAL_LOCKS=0</c>). Qualunque difficoltà (git assente, albero non versionato) produce campi <c>null</c> e un messaggio, mai un'eccezione.
        /// <paramref name="baseline"/> è il baseline del manifesto (<see cref="CodeBaseline"/>); il parametro serve solo ai test su repository temporanei.
        /// </summary>
        public static LaunchRecord Read(string? repositoryRoot = null, string baseline = CodeBaseline)
        {
            try
            {
                var root = repositoryRoot ?? FindRepositoryRoot(AppContext.BaseDirectory);
                if (root is null)
                {
                    return new(null, null, null, null, "repository git non trovato a partire da " + AppContext.BaseDirectory);
                }

                var (headExit, head) = Git(root, ["rev-parse", "HEAD"]);
                var (ancestorExit, _) = Git(root, ["merge-base", "--is-ancestor", baseline, "HEAD"]);
                var (diffExit, _) = Git(root, ["diff", "--quiet", baseline, "--", .. ProductPaths]);
                var (untrackedExit, untracked) = Git(root, ["ls-files", "--others", "--exclude-standard", "--", .. ProductPaths]);
                var (statusExit, status) = Git(root, ["status", "--porcelain", "--untracked-files=no", "--", .. TrackedPaths]);
                return new(
                    headExit == 0 && head.Trim().Length == 40 ? head.Trim() : null,
                    ancestorExit switch { 0 => true, 1 => false, _ => null },
                    diffExit == 0 && untrackedExit == 0 ? untracked.Trim().Length == 0 : diffExit == 1 ? false : null,
                    statusExit == 0 ? status.Trim().Length == 0 : null,
                    null);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or TimeoutException)
            {
                return new(null, null, null, null, e.GetType().Name);
            }
        }

        private static string? FindRepositoryRoot(string start)
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }
            }

            return null;
        }

        private static (int Exit, string Output) Git(string root, string[] args)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.Environment["GIT_OPTIONAL_LOCKS"] = "0";   // sola lettura: nessun aggiornamento opportunistico dell'indice
            info.ArgumentList.Add("--no-pager");
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info) ?? throw new InvalidOperationException("git non avviato");
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("git oltre 30 s");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult());
        }

        public System.Text.Json.Nodes.JsonObject ToJson() => new()
        {
            ["launchCommit"] = LaunchCommit,
            ["codeBaseline"] = CodeBaseline,
            ["baselineIsAncestorOfLaunchCommit"] = BaselineIsAncestor,
            ["productCodeUnchangedSinceBaseline"] = ProductCodeUnchanged,
            ["productPaths"] = new System.Text.Json.Nodes.JsonArray([.. ProductPaths.Select(p => (System.Text.Json.Nodes.JsonNode?)p)]),
            ["trackedTreeCleanOutsideUntracked"] = TrackedTreeClean,
            ["manifestSha256"] = ManifestSha256(),
            ["frozenManifestSha256"] = FrozenManifestSha256,
            ["error"] = Error,
        };
    }

    /// <summary>
    /// Verifica PURA dell'identità di lancio (<see cref="LaunchRecord"/>): commit di lancio rilevato, baseline del codice di prodotto antenato di esso, codice di prodotto identico al baseline e file tracciati senza
    /// modifiche non committate. Vuoto = coerente; altrimenti NO-GO PRIMA di ogni upload e inferenza. I test e i documenti possono cambiare DOPO il baseline purché committati (il commit di lancio li versiona), il codice di prodotto no.
    /// </summary>
    internal static IReadOnlyList<string> RejectLaunch(LaunchRecord launch)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(launch.LaunchCommit))
        {
            problems.Add($"commit di lancio non rilevabile ({launch.Error ?? "git non ha restituito HEAD"}): il Run non sarebbe riproducibile");
        }

        if (launch.BaselineIsAncestor != true)
        {
            problems.Add($"il baseline del codice di prodotto {CodeBaseline} non e un antenato del commit di lancio o non e verificabile");
        }

        if (launch.ProductCodeUnchanged != true)
        {
            problems.Add($"{string.Join(", ", LaunchRecord.ProductPaths)} differisce dal baseline {CodeBaseline} (anche non committato o non tracciato) o non e verificabile: il codice di prodotto del lancio non e quello del manifesto");
        }

        if (launch.TrackedTreeClean != true)
        {
            problems.Add($"file tracciati di {string.Join(", ", LaunchRecord.TrackedPaths)} con modifiche non committate o stato non verificabile: il commit di lancio non riprodurrebbe harness, test e manifesto realmente eseguiti");
        }

        return problems;
    }

    /// <summary>Verifica del profilo effettivo del runtime (misurato) contro la configurazione fissata, modello compreso (<see cref="LlmModelId"/>, mai un altro). Null = compatibile.</summary>
    public static string? RejectProfile(DocumentRuntimeProfile profile)
    {
        var problems = new List<string>();
        if (profile.WindowTokens != WindowTokens)
        {
            problems.Add($"finestra misurata {profile.WindowTokens} invece di {WindowTokens}");
        }

        if (profile.OutputTokens != OutputTokens)
        {
            problems.Add($"output {profile.OutputTokens} invece di {OutputTokens}");
        }

        if (profile.MarginTokens != MarginTokens)
        {
            problems.Add($"margine {profile.MarginTokens} invece di {MarginTokens}");
        }

        if (profile.Thinking != Thinking)
        {
            problems.Add($"ragionamento {profile.Thinking} invece di {Thinking}");
        }

        if (!string.Equals(profile.ModelId, LlmModelId, StringComparison.Ordinal))
        {
            problems.Add($"modello {profile.ModelId} diverso da quello fissato {LlmModelId}");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>
    /// Applica la configurazione fissata del recupero a un'opzione del prodotto (stessa funzione per la prova reale e per la verifica offline contro <see cref="RetrievalOptionsValidator"/>): recupero abilitato SOLO qui,
    /// politica selezionata esplicitamente, modelli e artefatti fissati, host del DGX ammesso esplicitamente. Nessun default di prodotto cambia.
    /// </summary>
    public static void ApplyRetrievalOptions(RetrievalOptions o, string endpoint, string apiKey)
    {
        var host = new Uri(endpoint.TrimEnd('/') + "/").Host;
        o.Enabled = true;
        o.PolicyVersion = PolicyVersion;
        o.TimeoutSeconds = 120;
        o.RuntimeCheckSeconds = 600;
        o.AllowedHosts = [.. o.AllowedHosts, host];
        o.Embedding.Endpoint = endpoint;
        o.Embedding.ApiKey = apiKey;
        o.Embedding.ModelId = EmbeddingModelId;
        o.Embedding.Artifact = EmbeddingArtifact;
        o.Embedding.Dimension = EmbeddingDimension;
        o.Reranker.Endpoint = endpoint;
        o.Reranker.ApiKey = apiKey;
        o.Reranker.ModelId = RerankerModelId;
        o.Reranker.Artifact = RerankerArtifact;
    }

    /// <summary>Registra opzioni e client del recupero con l'osservatore delle chiamate HTTP (solo durata/esito). I client sostituiscono quelli di prodotto SOLO nell'host di test.</summary>
    internal static void AddRetrieval(IServiceCollection services, string endpoint, string apiKey, RetrievalCallRecorder recorder, RetrievalTimingLog? timing = null)
    {
        if (timing is not null)
        {
            services.AddSingleton(timing);   // misura opzionale dei tempi per fase: solo osservazione, la pipeline di prodotto la usa se presente
        }

        services.PostConfigure<RetrievalOptions>(o => ApplyRetrievalOptions(o, endpoint, apiKey));
        services.AddSingleton(sp => new EmbeddingClient(sp.GetRequiredService<IOptions<RetrievalOptions>>().Value, recorder.NewHandler()));
        services.AddSingleton(sp => new RerankClient(sp.GetRequiredService<IOptions<RetrievalOptions>>().Value, recorder.NewHandler()));
        services.AddSingleton(sp => new RetrievalModelReadiness(
            sp.GetRequiredService<IOptions<RetrievalOptions>>().Value, sp.GetRequiredService<EmbeddingClient>(), recorder.NewHandler(), reranker: sp.GetRequiredService<RerankClient>()));
    }

    /// <summary>Strato a cui attribuire un run fallito, dal codice tipizzato (estrazione/riconciliazione, recupero, stadio Gap).</summary>
    public static string Classify(string errorCode) => errorCode switch
    {
        RetrievalException.EmbeddingUnavailable or RetrievalException.IndexNotReady or RetrievalException.RerankerUnavailable => "RECUPERO (servizi o indice del retrieval)",
        "gap_ledger_exceeds_input" or "gap_assessment_exceeds_output" => "ROUTING DELLO STADIO GAP (budget: il recupero non ha potuto sostituire l'intero ledger)",
        "work_output_invalid" or "gap_stage_invalid" => "STADIO GAP o RICONCILIAZIONE (output del modello non valido o troncato: le risposte grezze NNN-assistant.txt sono nell'archivio MafDiagnostics, cartella `analysisCallsSession` del rapporto diagnostico; la cartella texts del recorder di estrazione NON le contiene)",
        "" => "codice assente",
        _ => "ESTRAZIONE/RICONCILIAZIONE o altro (codice non specifico del recupero: vedere summary.json del recorder diagnostico)",
    };
}

/// <summary>
/// Registratore SOLO-TEST delle chiamate HTTP del recupero (embedding, riordino, sonde) con durata ed esito, per misurare la latenza del recupero per Gap. Non registra intestazioni né chiavi; conserva in
/// memoria il testo dell'interrogazione (enunciato del Gap, dato di test). Non altera richieste né risposte.
/// </summary>
internal sealed class RetrievalCallRecorder
{
    public sealed record Call(string Kind, string Phase, IReadOnlyList<string> Inputs, double Milliseconds, int Status, DateTimeOffset At, string Path = "", string? Exception = null);

    private readonly ConcurrentQueue<Call> _calls = new();

    /// <summary>Fase corrente (setup | index | run): impostata dal test, mai dal prodotto.</summary>
    public volatile string Phase = "setup";

    public IReadOnlyList<Call> Calls => [.. _calls];

    public DelegatingHandler NewHandler(HttpMessageHandler? inner = null) => new Tap(this) { InnerHandler = inner ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false } };   // stesse impostazioni del client di prodotto

    private void Add(Call call) => _calls.Enqueue(call);

    internal static (string Kind, IReadOnlyList<string> Inputs) Describe(string path, string body)
    {
        var kind = path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase) ? "embedding" : path.Contains("rerank", StringComparison.OrdinalIgnoreCase) ? "rerank" : "other";
        var inputs = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (kind == "rerank" && root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
            {
                inputs.Add(q.GetString()!);
            }
            else if (kind == "embedding" && root.TryGetProperty("input", out var input))
            {
                if (input.ValueKind == JsonValueKind.String)
                {
                    inputs.Add(input.GetString()!);
                }
                else if (input.ValueKind == JsonValueKind.Array)
                {
                    inputs.AddRange(input.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
            }
        }
        catch (JsonException)
        {
            // corpo non JSON (sonde): nessun input registrato
        }

        return (kind, inputs);
    }

    private sealed class Tap(RetrievalCallRecorder recorder) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = string.Empty;
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken);
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var (kind, inputs) = Describe(request.RequestUri?.AbsolutePath ?? string.Empty, body);
            var clock = Stopwatch.StartNew();
            var status = 0;
            string? exception = null;
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                return response;
            }
            catch (Exception e)
            {
                exception = e.GetType().Name;   // solo il tipo: nessun messaggio (potrebbe contenere URL con credenziali)
                throw;
            }
            finally
            {
                recorder.Add(new Call(kind, recorder.Phase, inputs, clock.Elapsed.TotalMilliseconds, status, DateTimeOffset.UtcNow, request.RequestUri?.AbsolutePath ?? string.Empty, exception));
            }
        }
    }
}

/// <summary>Latenza di UN Gap (ms): fasi proprie <c>search</c> e <c>rerank</c> della pipeline, con le componenti HTTP osservate nella finestra temporale di ciascuna.</summary>
internal sealed record GapLatency(
    Guid GapEvidenceId, double SearchMs, double RerankMs, double EmbeddingHttpMs, int EmbeddingCalls, double RerankHttpMs, int RerankCalls, bool Incomplete)
{
    /// <summary>Latenza end-to-end attribuibile al Gap: ricerca + riordino.</summary>
    public double EndToEndMs => SearchMs + RerankMs;

    /// <summary>Ricerca meno embedding della query: ricerca PostgreSQL (lessicale + vettoriale) e fusione RRF; non separabili senza alterare la pipeline, quindi riportate insieme.</summary>
    public double PostgresAndFusionMs => Math.Max(0, SearchMs - EmbeddingHttpMs);

    /// <summary>Riordino meno chiamata HTTP al reranker: costo applicativo proprio del Gap (selezione dei candidati, composizione delle richieste, ordinamento).</summary>
    public double RerankApplicationMs => Math.Max(0, RerankMs - RerankHttpMs);

    /// <summary>
    /// Misura completa del Gap: fasi ricerca e riordino chiuse, una chiamata di embedding (la query si incorpora sempre) e, SOLO se la ricerca ha prodotto candidati, almeno una chiamata al reranker.
    /// Un Gap senza candidati non invia nulla al reranker (<c>CandidateReranker</c>: nessuna fusione ⇒ nessuna richiesta): è un caso legittimo, non un fallimento del recupero.
    /// </summary>
    public bool IsMeasuredCompletely(int candidateCount) => !Incomplete && EmbeddingCalls > 0 && EndToEndMs > 0 && (candidateCount == 0 || RerankCalls > 0);
}

/// <summary>Rapporto di latenza del recupero: per Gap, le fasi condivise (una sola volta, MAI divise per Gap) e il totale.</summary>
internal sealed record LatencyReport(IReadOnlyList<GapLatency> PerGap, IReadOnlyDictionary<string, double> SharedMs, double TotalMs)
{
    public static readonly string[] PerGapPhases = [RetrievalTimingLog.PhaseSearch, RetrievalTimingLog.PhaseRerank];

    public static readonly string[] SharedPhases =
        [RetrievalTimingLog.PhaseReadiness, RetrievalTimingLog.PhaseIndexReady, RetrievalTimingLog.PhaseRerankerReady, RetrievalTimingLog.PhaseProvenance, RetrievalTimingLog.PhaseRegistry];

    /// <summary>Composizione condivisa del pacchetto (unione fra i Gap, per rango, fino al budget): lavoro di tutti i Gap insieme, non di uno.</summary>
    public double SharedPackageCompositionMs => SharedMs.GetValueOrDefault(RetrievalTimingLog.PhaseRegistry);

    /// <summary>
    /// Funzione pura: nessun effetto, nessuna rete. Le chiamate HTTP del recupero (fase «run») si attribuiscono a un Gap solo se terminano DENTRO la finestra [inizio, fine] della sua fase di ricerca (embedding)
    /// o di riordino (reranker); le sonde e le chiamate delle fasi condivise non sono attribuite. I Gap sono eseguiti in sequenza dalla pipeline: le finestre non si sovrappongono.
    /// </summary>
    public static LatencyReport Build(IReadOnlyList<RetrievalTimingLog.Entry> entries, IReadOnlyList<RetrievalCallRecorder.Call> calls, IReadOnlyList<Guid> gapIds)
    {
        var runCalls = calls.Where(c => c.Phase == "run").ToList();
        var perGap = new List<GapLatency>();
        foreach (var gap in gapIds)
        {
            var search = entries.Where(e => e.Phase == RetrievalTimingLog.PhaseSearch && e.GapEvidenceId == gap).ToList();
            var rerank = entries.Where(e => e.Phase == RetrievalTimingLog.PhaseRerank && e.GapEvidenceId == gap).ToList();
            var embedding = runCalls.Where(c => c.Kind == "embedding" && search.Any(w => Inside(c, w))).ToList();
            var rerankCalls = runCalls.Where(c => c.Kind == "rerank" && rerank.Any(w => Inside(c, w))).ToList();
            perGap.Add(new GapLatency(gap, search.Sum(e => e.Milliseconds), rerank.Sum(e => e.Milliseconds), embedding.Sum(c => c.Milliseconds), embedding.Count,
                rerankCalls.Sum(c => c.Milliseconds), rerankCalls.Count, search.Concat(rerank).Any(e => e.Faulted) || search.Count == 0 || rerank.Count == 0));
        }

        var shared = SharedPhases.ToDictionary(p => p, p => entries.Where(e => e.Phase == p && e.GapEvidenceId is null).Sum(e => e.Milliseconds));
        return new LatencyReport(perGap, shared, entries.Where(e => e.Phase == RetrievalTimingLog.PhaseTotal).Sum(e => e.Milliseconds));
    }

    private static bool Inside(RetrievalCallRecorder.Call call, RetrievalTimingLog.Entry window) => call.At >= window.StartedAt && call.At <= window.EndedAt;

    /// <summary>Righe di rapporto: una per Gap (componenti distinte) e una per le fasi condivise con il totale; la composizione condivisa non è mai attribuita ai Gap.</summary>
    public IEnumerable<string> Describe(Func<Guid, string> label)
    {
        foreach (var g in PerGap)
        {
            yield return $"Gap «{label(g.GapEvidenceId)}»: end-to-end {g.EndToEndMs:F0} ms = ricerca {g.SearchMs:F0} (embedding query {g.EmbeddingHttpMs:F0} in {g.EmbeddingCalls} chiamate + PostgreSQL/fusione RRF {g.PostgresAndFusionMs:F0}) "
                + $"+ riordino {g.RerankMs:F0} (reranker {g.RerankHttpMs:F0} in {g.RerankCalls} chiamate + applicativo {g.RerankApplicationMs:F0})";
        }

        yield return "fasi CONDIVISE (non attribuite ai Gap): " + string.Join(", ", SharedMs.Select(kv => $"{kv.Key} {kv.Value:F0} ms")) + $"; composizione del pacchetto {SharedPackageCompositionMs:F0} ms; totale pipeline {TotalMs:F0} ms";
    }
}

/// <summary>
/// Interrogazioni SQL della prova T040, in un posto solo perché <see cref="RetrievalGapCampaignSqlTests"/> ne verifica nomi di tabelle e colonne su uno schema migrato reale (senza dati e senza DGX):
/// un nome errato emerge offline e non dopo ore di campagna.
/// </summary>
internal static class CampaignSql
{
    public const string PreparationsReady = "SELECT count(*) FROM \"DocumentPreparations\" WHERE \"ProjectId\" = @p AND \"Status\"::text = 'Ready'";
    public const string IndexReady = "SELECT count(*) FROM \"RetrievalIndexStates\" WHERE \"ProjectId\" = @p AND \"Status\"::text = 'Ready'";
    public const string RunError = "SELECT coalesce(\"ErrorCode\", '') FROM \"ProcessingRuns\" WHERE \"Id\" = @r";
    public const string RunMessage = "SELECT coalesce(\"ErrorMessage\", '') FROM \"ProcessingRuns\" WHERE \"Id\" = @r";
    public const string StageBatches = "SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = @s";
    public const string StageOrdinal = "SELECT \"Ordinal\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = @s LIMIT 1";
    public const string CommittedOutputs = "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = @s AND \"Status\" = 'Committed'";
    public const string InferenceCalls = "SELECT count(*) FROM \"InferenceCalls\" WHERE \"ProcessingRunId\" = @r";
    public const string RegistryPayload = "SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'retrieval-output-1' AND \"Status\" = 'Committed'";
    public const string StagePayload = "SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1' AND \"Status\" = 'Committed' LIMIT 1";
    public const string IndexedExcluded = "SELECT count(*) FROM \"RetrievalIndexStates\" s JOIN \"DocumentVersions\" v ON v.\"Id\" = s.\"DocumentVersionId\" WHERE s.\"ProjectId\" = @p AND s.\"Status\"::text = 'Ready' AND v.\"DocumentId\" = ANY(@d)";
    public const string RetrievalCommitted = "SELECT coalesce(min(\"CommittedAt\"), now()) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'retrieval-output-1'";
    public const string HierarchyCommitted = "SELECT coalesce(max(\"CommittedAt\"), now()) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed' AND \"SchemaVersion\" NOT IN ('retrieval-output-1', 'gap-stage-alias-1')";
    public const string LastCall = "SELECT \"PromptTokens\", \"CompletionTokens\", \"DurationMs\", \"FinishReason\" FROM \"InferenceCalls\" WHERE \"ProcessingRunId\" = @r ORDER BY \"StartedAt\" DESC LIMIT 1";

    public static IEnumerable<string> All { get; } =
    [
        PreparationsReady, IndexReady, RunError, RunMessage, StageBatches, StageOrdinal, CommittedOutputs, InferenceCalls, RegistryPayload, StagePayload, IndexedExcluded, RetrievalCommitted, HierarchyCommitted, LastCall,
    ];
}
