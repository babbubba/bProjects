using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>Tokenizer e template FINTI e deterministici, condivisi dal runtime finto e dai misuratori puri: <c>/tokenize</c> = lunghezza / 3,5 arrotondata per eccesso, <c>/apply-template</c> = concatenazione dei messaggi.</summary>
internal static class FakeTokenizer
{
    public const double CharsPerToken = 3.5;

    public static int Tokens(string text) => (int)Math.Ceiling(text.Length / CharsPerToken);

    public static string Render(IEnumerable<(string Role, string Text)> messages) => string.Join('\n', messages.Select(m => $"[{m.Role}] {m.Text}"));
}

/// <summary>Tipi di richiesta di misura (RH-004): il tipo di batch dal messaggio utente reso, la pagina del catalogo (JSON), l'altro (sonde).</summary>
internal static class MeasureKinds
{
    public const string Leaf = "foglia";
    public const string Reduce = "riduzione";
    public const string Cross = "cross";
    public const string Expand = "espansione";
    public const string Compare = "confronto";
    public const string Page = "pagina";
    public const string Other = "altro";

    public static readonly string[] All = [Leaf, Reduce, Cross, Expand, Compare, Page, Other];

    private static readonly Regex BatchType = new(@"^Tipo di batch: (?<t>foglia|riduzione|cross|espansione|confronto)\n", RegexOptions.CultureInvariant);

    /// <summary>Classifica dal messaggio UTENTE reso: <c>Tipo di batch: …</c> = il tipo, <c>{</c> = una pagina del catalogo, altrimenti «altro».</summary>
    public static string Classify(string userMessage)
    {
        var match = BatchType.Match(userMessage);
        if (match.Success)
        {
            return match.Groups["t"].Value;
        }

        return userMessage.TrimStart().StartsWith('{') ? Page : Other;
    }
}

/// <summary>Una richiesta ricevuta dal runtime finto, con la classificazione e (per i messaggi dei batch) voci e vista.</summary>
internal sealed record RuntimeRequest(int Sequence, string Path, string Kind, string Role, int Entries, string View, bool Repeated, int Characters);

/// <summary>
/// Runtime llama.cpp FINTO in-process (<see cref="HttpMessageHandler"/>, nessuna rete): risponde a <c>/v1/models</c>, <c>/props</c>, <c>/slots</c>, <c>/tokenize</c> (<see cref="FakeTokenizer"/>) e <c>/apply-template</c>
/// (concatenazione) e CONTA le richieste per percorso e per tipo (<see cref="MeasureKinds"/>), dal messaggio utente reso. Un <c>/tokenize</c> del testo reso eredita il tipo dell'ultimo
/// <c>/apply-template</c> (ruolo <c>rendered</c>); un <c>/tokenize</c> di altro testo (lo schema della risposta) eredita lo stesso tipo con ruolo <c>extra</c>; <c>"probe"</c> è la sonda del monitor.
/// </summary>
internal sealed class CountingLlamaRuntime(string modelId, int window) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RuntimeRequest> _log = [];
    private readonly HashSet<string> _seenTemplates = new(StringComparer.Ordinal);
    private string _lastKind = MeasureKinds.Other;
    private string _lastRendered = string.Empty;

    public IReadOnlyList<RuntimeRequest> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public int Count(string path) => Log.Count(r => r.Path == path);

    public int Count(string path, string kind) => Log.Count(r => r.Path == path && r.Kind == kind);

    public int Count(string path, string kind, string role) => Log.Count(r => r.Path == path && r.Kind == kind && r.Role == role);

    /// <summary>Contatori correnti: (percorso, tipo) → richieste. Ordine deterministico (percorso, tipo).</summary>
    public IReadOnlyList<(string Path, string Kind, int Requests)> Counters() =>
        [.. Log.GroupBy(r => (r.Path, r.Kind)).OrderBy(g => g.Key.Path, StringComparer.Ordinal).ThenBy(g => g.Key.Kind, StringComparer.Ordinal).Select(g => (g.Key.Path, g.Key.Kind, g.Count()))];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        switch (path)
        {
            case "/v1/models":
                Record(path, MeasureKinds.Other, "models", 0, string.Empty, false, 0);
                return Json("{\"object\":\"list\",\"data\":[{\"id\":\"" + modelId + "\",\"object\":\"model\",\"status\":{\"value\":\"loaded\",\"args\":[\"llama-server\"]}}]}");
            case "/props":
                Record(path, MeasureKinds.Other, "props", 0, string.Empty, false, 0);
                return Json("{\"default_generation_settings\":{\"n_ctx\":" + window + ",\"params\":{\"n_predict\":-1}},\"total_slots\":1}");
            case "/slots":
                Record(path, MeasureKinds.Other, "slots", 0, string.Empty, false, 0);
                return Json("[{\"id\":0,\"n_ctx\":" + window + "}]");
            case "/tokenize":
            {
                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement.GetProperty("content").GetString()!;
                var count = FakeTokenizer.Tokens(content);
                string kind, role;
                lock (_gate)
                {
                    if (content == "probe")
                    {
                        (kind, role) = (MeasureKinds.Other, "probe");
                    }
                    else if (content == _lastRendered)
                    {
                        (kind, role) = (_lastKind, "rendered");
                    }
                    else
                    {
                        (kind, role) = (_lastKind, "extra");
                    }
                }

                Record(path, kind, role, 0, string.Empty, false, content.Length);
                var sb = new StringBuilder("{\"tokens\":[", count * 2 + 16);
                for (var i = 0; i < count; i++)
                {
                    sb.Append(i == 0 ? "0" : ",0");
                }

                return Json(sb.Append("]}").ToString());
            }

            case "/apply-template":
            {
                using var doc = JsonDocument.Parse(body);
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (Role: m.GetProperty("role").GetString()!, Text: m.GetProperty("content").GetString()!)).ToList();
                var prompt = FakeTokenizer.Render(messages);
                var user = messages.LastOrDefault(m => m.Role == "user").Text ?? string.Empty;
                var kind = MeasureKinds.Classify(user);
                var entries = Regex.Matches(user, @"<<<VOCE \[E\d+\]").Count;
                // Campo View: la «Vista:» del messaggio di un batch; per una pagina del catalogo, il suo tipo (Members, Keys, Relations).
                var view = kind == MeasureKinds.Page
                    ? (Regex.Match(user, "\"kind\":\"(Members|Keys|Relations)\"") is { Success: true } p ? p.Groups[1].Value : string.Empty)
                    : (Regex.Match(user, @"^Vista: (\w+)$", RegexOptions.Multiline) is { Success: true } v ? v.Groups[1].Value : string.Empty);
                bool repeated;
                lock (_gate)
                {
                    repeated = !_seenTemplates.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))));
                    _lastKind = kind;
                    _lastRendered = prompt;
                }

                Record(path, kind, "rendered", entries, view, repeated, prompt.Length);
                return Json(JsonSerializer.Serialize(new { prompt }));
            }

            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private void Record(string path, string kind, string role, int entries, string view, bool repeated, int characters)
    {
        lock (_gate)
        {
            _log.Add(new RuntimeRequest(_log.Count, path, kind, role, entries, view, repeated, characters));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Runtime finto, client interno e preflight di produzione con il profilo W/O/M indicato. Nessuna rete; <c>RuntimeCheckSeconds</c> alto: una sola sonda del monitor per tutta la misura.</summary>
internal sealed class MeasurementRig : IDisposable
{
    public const string Model = "Qwen3.6-35B-Smart";

    private MeasurementRig(CountingLlamaRuntime runtime, InternalModelClient client, AiOptions options, DocumentPreflight preflight, DocumentRuntimeProfile profile)
    {
        Runtime = runtime;
        Client = client;
        Options = options;
        Preflight = preflight;
        Profile = profile;
    }

    public CountingLlamaRuntime Runtime { get; }

    public InternalModelClient Client { get; }

    public AiOptions Options { get; }

    public DocumentPreflight Preflight { get; }

    public DocumentRuntimeProfile Profile { get; }

    public static async Task<MeasurementRig> CreateAsync(CancellationToken ct, int window = 32768, int output = 16384, int margin = 1024)
    {
        var runtime = new CountingLlamaRuntime(Model, window);
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = Model, ApiKey = "test-key", ContextWindowTokens = window, MaxOutputTokens = output, SafetyMarginTokens = margin,
            RuntimeCheckSeconds = 36_000,
        };
        var client = new InternalModelClient(options, runtime);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var preflight = new DocumentPreflight(options, monitor, client, null);
        var profile = await preflight.ResolveProfileAsync(AiPhase.Analysis, ct);
        return new MeasurementRig(runtime, client, options, preflight, profile);
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Ledger della prova isolata Single (<see cref="RetrievalIsolatedFixture"/>, T040-single) tradotto in un <see cref="DocumentAnalysisInput"/> IN MEMORIA, senza database: stesse evidenze (una per frase, i Gap della
/// domanda del PM), id deterministici. Le evidenze del documento escluso dal PM non entrano nello snapshot (come nella prova). I <see cref="DirectoryFacts"/> sono vuoti (come la prova isolata) oppure
/// DETERMINISTICI: soggetto, oggetto e azione ricavati dal testo delle regole di linea (stesse tre forme di <c>LargeDocumentCorpus.BulkRule</c>); le altre frasi non hanno fatti.
/// </summary>
internal static class MeasurementLedger
{
    public static readonly Guid Project = Id("project");
    public static readonly string SourceHash = Hex("source|" + RetrievalIsolatedFixture.Id);

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static Guid Id(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes("rh|" + text))[..16]);

    public static DocumentAnalysisInput Input(IsolatedLedger ledger)
    {
        var evidence = new List<DocumentAnalysisEvidence>();
        foreach (var document in ledger.Documents.Where(d => !d.ExcludedByPm))
        {
            var documentId = Id("doc|" + document.Key);
            for (var i = 0; i < document.Evidences.Count; i++)
            {
                var e = document.Evidences[i];
                var source = new PersistedSource
                {
                    SourceId = Id($"src|{document.Key}|{i}"), Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Id($"grp|{document.Key}|{i}"), Part = 0,
                    MessageId = null, Sequence = null, DocumentId = documentId, DocumentVersionId = Id("ver|" + document.Key), PreparationId = Id("prep|" + document.Key),
                    ArtifactId = Id("art|" + document.Key), BlockId = Id($"blk|{document.Key}|{e.BlockOrdinal}"), Start = e.Start, End = e.End, Quote = e.Statement,
                };
                evidence.Add(new DocumentAnalysisEvidence(
                    Id($"ev|{document.Key}|{i}"), Id("evver|" + document.Key), EvidenceOriginKind.Document, e.Kind, e.Polarity, EvidenceCertainty.Declared, e.Statement, Hex($"{e.Kind}|{e.Polarity}|{e.Statement}"), [source]));
            }
        }

        var snapshot = new ReconciliationSnapshot(Project, SourceHash, [.. evidence.Select(e => new SnapshotEvidence(e.EvidenceId, e.VersionId, e.ContentHash, e.Sources))], []);
        return new DocumentAnalysisInput(Project, Id("run"), 1, 1, 1, 0, Hex("snapshot|" + RetrievalIsolatedFixture.Id), snapshot, evidence, []);
    }

    private static readonly Regex LotRule = new(@"^Per la linea (?<n>\p{L}+) il (?<o>lotto minimo di rifornimento) è di \d+ pezzi\.$", RegexOptions.CultureInvariant);
    private static readonly Regex PackRule = new(@"^La scheda tecnica della linea (?<n>\p{L}+) (?<a>prevede) un (?<o>imballo) da \d+ pezzi per scatola\.$", RegexOptions.CultureInvariant);
    private static readonly Regex TempRule = new(@"^Nel reparto (?<n>\p{L}+) la (?<o>temperatura di conservazione) è (?<a>fissata) a \d+ gradi\.$", RegexOptions.CultureInvariant);

    /// <summary>Fatti deterministici dal testo della regola: soggetto = «linea|reparto NOME», oggetto = il sostantivo regolato, azione = il verbo; vuoti per ogni altra frase.</summary>
    public static IReadOnlyDictionary<Guid, DirectoryFacts> DeterministicFacts(DocumentAnalysisInput input)
    {
        var facts = new Dictionary<Guid, DirectoryFacts>();
        foreach (var e in input.Evidence)
        {
            DirectoryFacts? found = null;
            if (LotRule.Match(e.Statement) is { Success: true } lot)
            {
                found = new(new EvidenceStructure("linea " + lot.Groups["n"].Value, "è di", lot.Groups["o"].Value, null, null, null, null, null), []);
            }
            else if (PackRule.Match(e.Statement) is { Success: true } pack)
            {
                found = new(new EvidenceStructure("linea " + pack.Groups["n"].Value, pack.Groups["a"].Value, pack.Groups["o"].Value, null, null, null, null, null), []);
            }
            else if (TempRule.Match(e.Statement) is { Success: true } temp)
            {
                found = new(new EvidenceStructure("reparto " + temp.Groups["n"].Value, "è " + temp.Groups["a"].Value, temp.Groups["o"].Value, null, null, null, null, null), []);
            }

            if (found is not null)
            {
                facts[e.EvidenceId] = found;
            }
        }

        return facts;
    }
}

/// <summary>
/// Misuratori PURI dei Pack della riconciliazione (nessuna rete): stesso payload di <c>DocumentReconcilePlan</c> (istruzioni come system, messaggio alias reale, schema come extra) misurato con
/// <see cref="FakeTokenizer"/>: tokens = <c>Tokens(Render(messaggi))</c> + <c>Tokens(schema)</c>. Servono a RH-004 per ricostruire in modo INDIPENDENTE le foglie e le pagine attese e a RH-005
/// per il motore puro. Gli item delle foglie A (<see cref="Items"/>) sono visibili agli stadi di espansione e confronto come nel piano.
/// </summary>
internal sealed class PureReconcileMeasure
{
    private readonly AnalysisAliasCatalog _run;
    private readonly IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> _evidence;
    private readonly string _instructions;
    private readonly string _schema;
    private readonly string? _profile;
    private readonly string _schemaVersion = ReconcileAliasContracts.SchemaVersion;
    private readonly string _promptVersion;

    public PureReconcileMeasure(DocumentAnalysisInput input, AnalysisAliasCatalog run, string definition)
    {
        _run = run;
        _evidence = input.Evidence.ToDictionary(e => e.EvidenceId);
        _promptVersion = DocumentAnalysisWorkflow.ReconcilePromptOf(definition);
        _instructions = ReconcileAliasContracts.InstructionsFor(_promptVersion);
        _schema = ReconcileAliasContracts.JsonSchema().GetRawText();
        _profile = DocumentAnalysisWorkflow.ReconcileSchemaProfileOf(definition);
    }

    /// <summary>Item già proposti dalle foglie A, nell'ordine di arrivo (come <c>ReplayState.Items</c>).</summary>
    public List<ReconcileItemRef> Items { get; } = [];

    /// <summary>Vero dopo la costruzione della gerarchia (stadio dei placeholder del Pack di voci).</summary>
    public bool CandidatePhase { get; set; }

    private static UnitMeasure Measure(string instructions, string user, string? schema)
    {
        var rendered = FakeTokenizer.Render([("system", instructions), ("user", user)]);
        var tokens = FakeTokenizer.Tokens(rendered) + (schema is null ? 0 : FakeTokenizer.Tokens(schema));
        return new UnitMeasure(tokens, TokenCountQuality.Measured, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rendered + "\u0001" + schema))), tokens);
    }

    private string Compose(ReconcileBatchRequest request) =>
        ReconcileAliasMessages.Build(_run, ReconcileAliasCatalog.Build(_run, request, Items), request, _evidence)[0].Text!;

    private ReconcileBatchRequest Placeholder(IReadOnlyList<CatalogEntry> entries, IReadOnlyList<CatalogNode> nodes) => new(
        new DerivedBatchInput(
            new string('0', 64), entries.Count > 0 ? (CandidatePhase ? "expand" : "leaf") : "reduce", null, null, null, 0, [], "{}", null, "measured", new string('0', 64), _schemaVersion, _promptVersion),
        ReconcileView.A, 0, entries, nodes);

    public ValueTask<UnitMeasure> Leaf(IReadOnlyList<CatalogEntry> members, CancellationToken ct) => new(Measure(_instructions, Compose(Placeholder(members, [])), _schema));

    public ValueTask<UnitMeasure> Node(IReadOnlyList<CatalogNode> members, CancellationToken ct) => new(Measure(_instructions, Compose(Placeholder([], members)), _schema));

    public ValueTask<UnitMeasure> Page(IReadOnlyList<PageEntry> members, CancellationToken ct)
    {
        var kind = members.FirstOrDefault() switch { KeyPageEntry => CatalogPageKind.Keys, RelationPageEntry => CatalogPageKind.Relations, _ => CatalogPageKind.Members };
        var text = ReconciliationCodec.Canonical(new CatalogPage { PageId = "x", NodeId = "x", Kind = kind, Ordinal = 0, Entries = members, PreviousPageHash = null });
        return new(Measure(_instructions, text, null));
    }

    public ValueTask<UnitMeasure> Expansion(ReconcileCandidate candidate, ReconcileView view, ExpansionGroups groups, IReadOnlyList<CatalogEntry> members, CancellationToken ct)
    {
        var request = new ReconcileBatchRequest(
            new DerivedBatchInput(new string('0', 64), "expand", null, null, null, 0, [], "{}", null, "measured", new string('0', 64), _schemaVersion, _promptVersion), view, 0, members, [], [candidate], groups);
        var (_, schema) = ReconcileAliasContracts.ProfileSchemaFor(_profile!, "expand", view);
        return new(Measure(_instructions, Compose(request), schema.GetRawText()));
    }

    public ReconciliationMeasurers Measurers => new(Leaf, Page);
}
