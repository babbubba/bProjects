using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Evidence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 D1/T138 - MISURA OFFLINE e deterministica del payload di una futura valutazione Gap su ledger completo (docs/roadmap/slice-002-003-transition.md §C, D1).
/// Nessun database, nessun DGX, nessuna inferenza: ledger sintetico da 297 evidenze (come l'ottava esecuzione di I5) + N Gap, compositi nel formato VOCE del piano
/// alias (DocumentAnalysisAliasPlan.BuildMessages) arricchito dai metadati di D2 (documento, versione, sezione, certezza). Si misurano con gli stimatori ESISTENTI:
/// <see cref="DocumentPreflight"/> (profilo W/O/M/B, payload completo con istruzioni, schema e framing) per l'input e <see cref="AnalysisOutputEstimate"/> per l'output.
/// Limiti: nessun tokenizer reale offline - i token sono derivati dal rapporto caratteri/token (4,48 osservato in I5: 1323985 caratteri = 295725 token; 3,5 pessimistico;
/// 2,5 = limite superiore prudente <see cref="AnalysisOutputEstimate.CharsPerToken"/>). Le lunghezze di enunciato e citazione per evidenza NON sono misurate in I5
/// (DB di test eliminato): sono scenari dichiarati. Il test non e un gate: stampa la tabella (console + file temporaneo) e fissa solo invarianti deterministici.
/// </summary>
[Trait("Category", "Offline")]
public sealed class DocumentGapStagePayloadMeasureTests
{
    private const int Window = 131_072;
    private const int OutputCap = 16_384;
    private const int Margin = 1_024;
    private const int Budget = Window - OutputCap - Margin;   // 113664, come I5
    private const int Evidences = 297;
    private const string Model = "Qwen3.6-35B-Smart";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Scenari di dimensione per evidenza (caratteri). Non misurati in I5: dichiarati.</summary>
    private sealed record Scenario(string Name, int Statement, int Quote, int SourcesPerEvidence);

    private static readonly Scenario[] Scenarios =
    [
        new("corto (come i test dell'estimatore: enunciato 90, citazione 120)", 90, 120, 1),
        new("centrale (enunciato 180, citazione 300)", 180, 300, 1),
        new("verboso (contratto reale: enunciato 300, 2 citazioni da 400)", 300, 400, 2),
    ];

    /// <summary>Rapporto caratteri/token: etichetta, valore, true = limite superiore calibrato (nessun tokenizer), false = tokenizer finto a quel rapporto.</summary>
    private static readonly (string Label, double CharsPerToken, bool Calibrated)[] Ratios =
    [
        ("4,48 c/t (osservato I5)", 4.48, false),
        ("3,5 c/t (pessimistico)", 3.5, false),
        ("2,5 c/t (limite sup. prudente)", 2.5, true),
    ];

    private static readonly int[] GapCounts = [1, 5, 10, 20];

    // ---- runtime finto: finestra 131072, tokenizer a rapporto fisso (o assente) -------------------------------------------------------------------------------------

    private sealed class FakeRuntime(double? charsPerToken) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/v1/models":
                    return Json("{\"object\":\"list\",\"data\":[{\"id\":\"" + Model + "\",\"object\":\"model\",\"status\":{\"value\":\"loaded\",\"args\":[\"llama-server\"]}}]}");
                case "/props":
                    return Json("{\"default_generation_settings\":{\"n_ctx\":" + Window + ",\"params\":{\"n_predict\":-1}},\"total_slots\":1}");
                case "/tokenize" when charsPerToken is { } ratio:
                {
                    using var doc = JsonDocument.Parse(body);
                    var length = doc.RootElement.GetProperty("content").GetString()!.Length;
                    var count = (int)Math.Ceiling(length / ratio);
                    var sb = new StringBuilder("{\"tokens\":[", count * 2 + 16);
                    for (var i = 0; i < count; i++)
                    {
                        sb.Append(i == 0 ? "0" : ",0");
                    }

                    return Json(sb.Append("]}").ToString());
                }

                case "/apply-template" when charsPerToken is not null:
                {
                    using var doc = JsonDocument.Parse(body);
                    var prompt = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray().Select(m =>
                        $"[{m.GetProperty("role").GetString()}] {m.GetProperty("content").GetString()}"));
                    return Json(JsonSerializer.Serialize(new { prompt }));
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static AiOptions Options() => new()
    {
        Endpoint = "http://dgx.internal:8080/v1/",
        ModelId = Model,
        ApiKey = "test-key",
        ContextWindowTokens = Window,
        MaxOutputTokens = OutputCap,
        SafetyMarginTokens = Margin,
        RuntimeCheckSeconds = 60,
    };

    private sealed class Meter : IDisposable
    {
        private readonly InternalModelClient _client;

        public Meter(double charsPerToken, bool calibrated)
        {
            var options = Options();
            _client = new InternalModelClient(options, new FakeRuntime(calibrated ? null : charsPerToken));
            var monitor = new ModelRuntimeMonitor(_client, options, NullLogger<ModelRuntimeMonitor>.Instance);
            // Con tokenizer (finto) il conteggio e "Measured"; senza, "CalibratedUpperBound" con il minimo caratteri/token e il framing della calibrazione.
            Preflight = new DocumentPreflight(
                options, monitor, _client, calibrated ? new PayloadCalibration("d1-measure", PerMessageFramingTokens: 8, FixedOverheadTokens: 16, MinCharsPerToken: charsPerToken) : null);
        }

        public DocumentPreflight Preflight { get; }

        public void Dispose() => _client.Dispose();
    }

    // ---- ledger sintetico deterministico ---------------------------------------------------------------------------------------------------------------------------

    private static readonly string[] Vocabulary =
    [
        "il", "fornitore", "deve", "garantire", "la", "disponibilita", "del", "servizio", "entro", "i", "termini", "contrattuali", "previsti", "dal", "capitolato",
        "per", "ogni", "lotto", "consegnato", "presso", "sede", "cliente", "con", "collaudo", "finale", "e", "verbale", "di", "accettazione", "sottoscritto",
        "dalle", "parti", "l'importo", "massimo", "non", "puo", "superare", "soglia", "indicata", "nell'allegato", "tecnico", "salvo", "variazioni", "approvate",
    ];

    private static string Fill(int length, int seed)
    {
        if (length <= 0)
        {
            return "";
        }

        var sb = new StringBuilder(length + 16);
        var state = (uint)seed * 2654435761u + 12345u;
        while (sb.Length < length)
        {
            state = state * 1664525u + 1013904223u;
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Vocabulary[(int)((state >> 16) % (uint)Vocabulary.Length)]);
        }

        sb.Length = length;
        return sb.ToString().TrimEnd();
    }

    private static readonly EvidenceKind[] NonGapKinds =
    [
        EvidenceKind.Requirement, EvidenceKind.Constraint, EvidenceKind.Action, EvidenceKind.Context, EvidenceKind.Decision, EvidenceKind.Assumption,
        EvidenceKind.Dependency, EvidenceKind.Exclusion,
    ];

    private static readonly EvidenceCertainty[] Certainties = [EvidenceCertainty.Declared, EvidenceCertainty.Inferred, EvidenceCertainty.Declared, EvidenceCertainty.ToClarify];

    private static readonly EvidencePolarity[] Polarities = [EvidencePolarity.Affirmed, EvidencePolarity.Affirmed, EvidencePolarity.Negated, EvidencePolarity.Undefined];

    /// <summary>
    /// Elenco VOCE nel formato del piano alias con i metadati D2 (documento, versione, sezione, certezza) come testo-dato. Le prime <paramref name="evidences"/> sono
    /// evidenze non Gap, le ultime <paramref name="gaps"/> sono Gap (domande del PM). Deterministico: stesso argomento, stesso testo.
    /// </summary>
    private static string BuildLedger(Scenario s, int evidences, int gaps)
    {
        var text = new StringBuilder();
        var source = 0;
        for (var i = 1; i <= evidences + gaps; i++)
        {
            var isGap = i > evidences;
            var kind = isGap ? EvidenceKind.Gap : NonGapKinds[i % NonGapKinds.Length];
            var origin = !isGap && i % 20 == 0 ? EvidenceOriginKind.Message : EvidenceOriginKind.Document;
            var statement = isGap ? Fill(Math.Min(s.Statement, 110), i) + "?" : Fill(s.Statement, i);
            text.Append($"<<<VOCE [E{i}] origine={origin} tipo={kind} polarita={Polarities[i % Polarities.Length]}");
            if (origin == EvidenceOriginKind.Document)
            {
                var section = i % 7 == 0 ? "" : $" sezione=\"{1 + (i / 40)}.{1 + (i % 9)} > {Fill(24, i + 7)}\"";
                text.Append($" documento=\"capitolato-aurora.txt\" versione=1{section}");
            }

            text.Append($" certezza={Certainties[i % Certainties.Length]}>>>\n").Append(statement).Append('\n');
            for (var k = 0; k < (isGap ? 0 : s.SourcesPerEvidence); k++)
            {
                source++;
                text.Append($"<<<FONTE [S{source}] ruolo={(k == 0 ? "Primary" : "Context")}>>>\n").Append(Fill(s.Quote, 10_000 + source)).Append("\n<<<FINE FONTE>>>\n");
            }

            text.Append("<<<FINE VOCE>>>\n");
        }

        return text.ToString();
    }

    /// <summary>Elenco compatto di conflitti/domande aperti canonici (12 conflitti da 220 caratteri, 8 domande da 140) e 30 relazioni note (D1, punto 2).</summary>
    private static string BuildOpenItems()
    {
        var text = new StringBuilder("\nConflitti aperti canonici:\n");
        for (var c = 1; c <= 12; c++)
        {
            text.Append($"[C{c}] E{c * 7} Contradicts E{c * 11 + 3}: ").Append(Fill(220, 500 + c)).Append('\n');
        }

        text.Append("\nDomande aperte canoniche:\n");
        for (var q = 1; q <= 8; q++)
        {
            text.Append($"[Q{q}] ").Append(Fill(140, 700 + q)).Append('\n');
        }

        text.Append("\nRelazioni note fra evidenze:\n");
        for (var r = 1; r <= 30; r++)
        {
            text.Append($"[R{r}] E{r * 5} AlternativeTo E{r * 6 + 1} (base=Model)\n");
        }

        return text.ToString();
    }

    private static string Instructions()
    {
        // Proxy del prompt dello stadio Gap (document-gap-v1 non esiste): l'attuale prompt di analisi v6 (16134 byte) e un limite superiore per un prompt dedicato.
        using var stream = typeof(DocumentPreflight).Assembly.GetManifestResourceStream("Prompts.document-analysis-v6.md")
            ?? throw new InvalidOperationException("Prompt document-analysis-v6 non incorporato.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Schema della sola risposta dello stadio Gap: { gapAssessments: [ gap-assessment-1 ] }, costruito dal generatore di schema esistente.</summary>
    private static string GapStageSchema()
    {
        var item = (JsonNode)typeof(AnalysisAliasContracts)
            .GetMethod("GapAssessmentSchema", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("gapAssessments"),
            ["properties"] = new JsonObject { ["gapAssessments"] = new JsonObject { ["type"] = "array", ["items"] = item } },
        };
        return root.ToJsonString();
    }

    private static DocumentPayload Payload(Scenario s, int evidences, int gaps)
    {
        var message = new StringBuilder("Gap da valutare e evidenze del ledger completo (tutte, senza ranking):\n")
            .Append(BuildLedger(s, evidences, gaps)).Append(BuildOpenItems()).Append("\nRispondi ora con l'unico oggetto JSON `gap-stage-1`.");
        return new DocumentPayload(AiPhase.Analysis, Instructions(), [new ChatMessage(ChatRole.User, message.ToString())], GapStageSchema());
    }

    private static async Task<PayloadPreflight> MeasureAsync(Meter meter, Scenario s, int evidences, int gaps) =>
        await meter.Preflight.MeasureAsync(Payload(s, evidences, gaps), Ct);

    /// <summary>Massimo numero di evidenze non Gap che entrano nel budget di input con <paramref name="gaps"/> Gap (ricerca binaria sul preflight reale).</summary>
    private static async Task<int> MaxEvidencesAsync(Meter meter, Scenario s, int gaps)
    {
        int lo = 0, hi = 4_000;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if ((await MeasureAsync(meter, s, mid, gaps)).Fits)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    // ---- output: record gap-assessment-1 con motivazione ------------------------------------------------------------------------------------------------------------

    /// <summary>Caratteri JSON dei soli <c>gapAssessments</c> (record reali serializzati con il codec del contratto, motivazione di <paramref name="rationale"/> caratteri, 2 riscontri).</summary>
    private static int GapRecordsChars(int gaps, int rationale)
    {
        var records = Enumerable.Range(1, gaps).Select(g => new AiGapAssessment
        {
            Gap = $"E{Evidences + g}",
            Outcome = g % 3 == 0 ? GapOutcome.Open : GapOutcome.AnsweredInSnapshot,
            Reason = g % 3 == 0 ? GapReason.Partial : GapReason.Answered,
            Rationale = Fill(rationale, 900 + g),
            SupportingEvidence = [$"E{g * 3}", $"E{g * 3 + 1}"],
            OpenItem = null,
            OpenConflict = null,
        }).ToList();
        return JsonSerializer.Serialize(new { gapAssessments = records }, AnalysisAliasContracts.Options).Length;
    }

    private static long CeilDiv(long chars, double ratio) => (long)Math.Ceiling(chars / ratio);

    // ---- test ----------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_synthetic_ledger_is_deterministic_and_has_the_requested_shape()
    {
        var s = Scenarios[1];
        var a = BuildLedger(s, Evidences, 10);
        Assert.Equal(a, BuildLedger(s, Evidences, 10));
        Assert.Equal(Evidences + 10, a.Split("<<<VOCE [", StringSplitOptions.None).Length - 1);
        Assert.Equal(10, a.Split("tipo=Gap ", StringSplitOptions.None).Length - 1);
        Assert.Equal(Evidences * s.SourcesPerEvidence, a.Split("<<<FONTE [", StringSplitOptions.None).Length - 1);
        Assert.Contains("documento=\"capitolato-aurora.txt\" versione=1", a, StringComparison.Ordinal);
        Assert.Contains("certezza=", a, StringComparison.Ordinal);
        Assert.Equal(Budget, 113_664);   // stesso budget effettivo di I5
    }

    [Fact]
    public async Task Gap_stage_payload_size_against_the_effective_input_and_output_budgets()
    {
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);
        var inv = CultureInfo.InvariantCulture;

        Line($"== D1 misura offline - profilo effettivo: W={Window} O={OutputCap} M={Margin} B(input)={Budget}; {Evidences} evidenze non Gap + N Gap; prompt proxy = document-analysis-v6 ==");
        Line($"Istruzioni: {Instructions().Length} caratteri; schema gapAssessments: {GapStageSchema().Length} caratteri; elenco conflitti/domande/relazioni: {BuildOpenItems().Length} caratteri.");

        var central = new Dictionary<(string Ratio, int Gaps), PayloadPreflight>();
        foreach (var s in Scenarios)
        {
            Line();
            Line($"-- Scenario {s.Name}; ledger a 297 evidenze + N Gap --");
            Line("rapporto                           | N Gap | caratteri | token input | qualita              | %B     | margine (token)");
            foreach (var (label, ratio, calibrated) in Ratios)
            {
                using var meter = new Meter(ratio, calibrated);
                foreach (var n in GapCounts)
                {
                    var payload = Payload(s, Evidences, n);
                    var m = await meter.Preflight.MeasureAsync(payload, Ct);
                    var chars = payload.Instructions.Length + payload.Messages.Sum(x => x.Text.Length) + (payload.ResponseSchemaJson?.Length ?? 0);
                    Line(string.Create(inv, $"{label,-34} | {n,5} | {chars,9} | {m.Tokens,11} | {m.Quality,-20} | {100.0 * m.Tokens / Budget,5:F1}% | {Budget - m.Tokens,8}"));
                    if (s == Scenarios[1])
                    {
                        central[(label, n)] = m;
                    }
                }
            }
        }

        Line();
        Line("-- Capacita: massimo di evidenze non Gap che entra nel budget di input (ricerca binaria sul preflight), per N Gap --");
        Line("scenario                                                    | rapporto                           | N=10  | N=20");
        foreach (var s in Scenarios)
        {
            foreach (var (label, ratio, calibrated) in Ratios)
            {
                using var meter = new Meter(ratio, calibrated);
                var max10 = await MaxEvidencesAsync(meter, s, 10);
                var max20 = await MaxEvidencesAsync(meter, s, 20);
                Line(string.Create(inv, $"{s.Name,-59} | {label,-34} | {max10,5} | {max20,5}"));
            }
        }

        Line();
        Line($"-- Output atteso dei soli gapAssessments vs O={OutputCap} (stimatore esistente AnalysisOutputEstimate.Estimate([], N, 0): scheletro 600 + 214/Gap, x1,08 x1,25) --");
        Line("N Gap | Estimate (limite sup.) | %O     | record reali: caratteri (motiv. 300) | token a 4,48 | token a 2,5 | caratteri (motiv. 600) | token a 2,5");
        foreach (var n in new[] { 1, 5, 10, 20, 50, 100 })
        {
            var estimate = AnalysisOutputEstimate.Estimate([], n, 0);
            var c300 = GapRecordsChars(n, 300);
            var c600 = GapRecordsChars(n, 600);
            Line(string.Create(inv, $"{n,5} | {estimate,22} | {100.0 * estimate / OutputCap,5:F1}% | {c300,36} | {CeilDiv(c300, 4.48),11} | {CeilDiv(c300, 2.5),11} | {c600,22} | {CeilDiv(c600, 2.5),11}"));
        }

        var maxGapsByEstimate = Enumerable.Range(0, 1_000).Last(n => !AnalysisOutputEstimate.Exceeds([], n, 0, OutputCap));
        Line($"Massimo N Gap con Estimate <= O: {maxGapsByEstimate}. (Il ragionamento Qwen, se attivo, consuma lo stesso tetto O: non incluso in nessuna di queste stime.)");

        Line();
        Line("-- Partizionamento oltre il budget (scenario centrale, 4,48 c/t, N=10): chiamate = ceil(token ledger / (B - parte fissa)) --");
        using (var meter = new Meter(4.48, false))
        {
            var empty = await MeasureAsync(meter, Scenarios[1], 0, 10);
            var one = await MeasureAsync(meter, Scenarios[1], 1, 10);
            var hundred = await MeasureAsync(meter, Scenarios[1], 101, 10);
            var perEvidence = (hundred.Tokens - one.Tokens) / 100.0;
            var capacity = (Budget - empty.Tokens) / perEvidence;
            Line(string.Create(inv, $"parte fissa (prompt+schema+10 Gap+conflitti) = {empty.Tokens} token; {perEvidence:F1} token/evidenza; capacita per chiamata = {capacity:F0} evidenze"));
            foreach (var e in new[] { 297, 600, 1_000, 2_000, 5_000 })
            {
                var partitions = (int)Math.Ceiling(e / capacity);
                Line(string.Create(inv, $"ledger {e,5} evidenze -> {partitions} chiamate di valutazione (ogni Gap valutato contro tutte le partizioni); {partitions * 10} record parziali da comporre"));
            }
        }

        var text = report.ToString();
        Console.WriteLine(text);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "bprojects-d1-gap-stage-measure.txt"), text);
        TestContext.Current.SendDiagnosticMessage(text);

        // Invarianti deterministici (non sono un gate di prodotto).
        Assert.True(central[("4,48 c/t (osservato I5)", 20)].Tokens > central[("4,48 c/t (osservato I5)", 1)].Tokens);                  // piu Gap, piu input
        Assert.True(central[("2,5 c/t (limite sup. prudente)", 10)].Tokens > central[("4,48 c/t (osservato I5)", 10)].Tokens);          // limite superiore sopra il rapporto osservato
        Assert.True(AnalysisOutputEstimate.Estimate([], 20, 0) > AnalysisOutputEstimate.Estimate([], 10, 0));

        // Esiti numerici misurati (2026-11-09, scenari dichiarati): centrale a 297 evidenze entra col rapporto osservato e col pessimistico, e col limite prudente
        // 2,5 c/t con margine > 15%; il verboso entra solo col rapporto osservato. Output: l'estimatore esistente da 53 Gap al tetto O.
        Assert.True(central[("4,48 c/t (osservato I5)", 20)].Fits);
        Assert.True(central[("3,5 c/t (pessimistico)", 20)].Fits);
        Assert.True(central[("2,5 c/t (limite sup. prudente)", 20)].Tokens < Budget * 0.85);
        Assert.Equal(53, maxGapsByEstimate);
        Assert.Equal(Budget, central[("4,48 c/t (osservato I5)", 1)].InputBudgetTokens);
    }
}
