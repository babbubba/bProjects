using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
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

    // ---- soglie (tasks.md T040, spec SC-003/SC-004: riferimento del laboratorio «≤ 2 su 10 casi», applicato in proporzione al numero di casi misurati)
    public const int MaxInvalidCitationsPer10 = 2;
    public const int MaxLostConflictsPer10 = 2;

    /// <summary>Un conteggio rispetta «≤ 2 su 10»: bad/total ≤ 2/10, in interi.</summary>
    public static bool WithinPer10(int bad, int total, int maxPer10) => bad * 10 <= maxPer10 * Math.Max(total, 1);

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

    public static Corpus BuildCorpus(CampaignScenario scenario) =>
        scenario == Single ? LargeDocumentCorpus.Single(FillerChars, SingleBulkRules) : LargeDocumentCorpus.Multi(FillerChars, MultiBulkRulesPerDocument);

    // ================================================================ manifesto congelato

    /// <summary>Hash del manifesto congelato (SHA-256 del JSON compatto di <see cref="BuildManifestJson"/>). Cambiarlo è un atto esplicito di ri-congelamento, mai un adattamento a un risultato.</summary>
    public const string FrozenManifestSha256 = "662431340021f99eb094814adc730eca54ba9776000412ea8df0fd9c3552c035";

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
            definition = "project-analysis-documents-v7",
            config = new
            {
                window = WindowTokens, output = OutputTokens, margin = MarginTokens, budget = BudgetTokens, thinking = Thinking,
                policy = PolicyVersion, policyHash = RetrievalPolicy.V1.Hash(),
                embedding = new { id = EmbeddingModelId, artifact = EmbeddingArtifact, dimension = EmbeddingDimension },
                reranker = new { id = RerankerModelId, artifact = RerankerArtifact },
                indexTextVersion = RetrievalOptions.IndexTextVersion, fts = RetrievalOptions.FtsConfiguration,
                llm = "da ambiente: BPROJECTS_TEST_DGX_MODEL (registrato nel rapporto, non modificabile a campagna avviata)",
            },
            thresholds = new { maxInvalidCitationsPer10 = MaxInvalidCitationsPer10, maxLostConflictsPer10 = MaxLostConflictsPer10, falseAnsweredInSnapshot = 0 },
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

        if (string.IsNullOrWhiteSpace(read(TestEnvironment.DgxModelVariable)))
        {
            problems.Add($"{TestEnvironment.DgxModelVariable} non impostata: il modello LLM della campagna deve essere dichiarato");
        }

        return problems;
    }

    /// <summary>Verifica del profilo effettivo del runtime (misurato) contro la configurazione fissata. Null = compatibile.</summary>
    public static string? RejectProfile(DocumentRuntimeProfile profile, string? expectedModel)
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

        if (!string.IsNullOrWhiteSpace(expectedModel) && !string.Equals(profile.ModelId, expectedModel, StringComparison.Ordinal))
        {
            problems.Add($"modello {profile.ModelId} diverso da quello dichiarato {expectedModel}");
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
    internal static void AddRetrieval(IServiceCollection services, string endpoint, string apiKey, RetrievalCallRecorder recorder)
    {
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
        "work_output_invalid" or "gap_stage_invalid" => "STADIO GAP o RICONCILIAZIONE (output del modello non valido: vedere calls.jsonl e texts del recorder diagnostico)",
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
    public sealed record Call(string Kind, string Phase, IReadOnlyList<string> Inputs, double Milliseconds, int Status, DateTimeOffset At);

    private readonly ConcurrentQueue<Call> _calls = new();

    /// <summary>Fase corrente (setup | index | run): impostata dal test, mai dal prodotto.</summary>
    public volatile string Phase = "setup";

    public IReadOnlyList<Call> Calls => [.. _calls];

    public DelegatingHandler NewHandler() => new Tap(this) { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false } };   // stesse impostazioni del client di prodotto

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

    /// <summary>Latenza attribuita a un Gap nella fase indicata: durata delle chiamate di embedding/riordino la cui interrogazione è l'enunciato del Gap (le richieste multi-input si ripartiscono in parti uguali).</summary>
    public (int Calls, double Milliseconds) LatencyOf(string statement, string phase, Func<string, string> fold)
    {
        var key = fold(statement);
        double total = 0;
        var count = 0;
        foreach (var call in _calls.Where(c => c.Phase == phase && (c.Kind is "embedding" or "rerank")))
        {
            var matches = call.Inputs.Count(i => fold(i) == key);
            if (matches > 0)
            {
                count++;
                total += call.Milliseconds * matches / call.Inputs.Count;
            }
        }

        return (count, total);
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
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                status = (int)response.StatusCode;
                return response;
            }
            finally
            {
                recorder.Add(new Call(kind, recorder.Phase, inputs, clock.Elapsed.TotalMilliseconds, status, DateTimeOffset.UtcNow));
            }
        }
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
