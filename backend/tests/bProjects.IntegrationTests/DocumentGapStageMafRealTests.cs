using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// T150 (DGX) — gate «SC-004-Gap»: lo stadio Gap v6 con tutto reale (API di produzione, parser Docling, PostgreSQL, MAF 1.26.0, modello interno) sui dataset T082 (documento singolo) e T083 (tre documenti),
/// ciascuno con TRE domande del PM (un messaggio per domanda, quindi un Gap proprio). Gold dei fatti e dei conflitti invariati (<see cref="LargeDocumentCorpus"/>). Le ATTESE SONO ANNOTATE QUI, PRIMA
/// dell'esecuzione, e non si adattano alle risposte:
/// Q1 (risposta presente e univoca) = AnsweredInSnapshot/Answered, supporto documentale con la citazione del fatto, nessun riferimento Open;
/// Q2 (argomento assente dal corpus: nessun recapito telefonico esiste nei documenti) = Open/NoAnswer con absenceProven, searchBasis=ledger_exhaustive, retrievalComplete, riferimento Open valorizzato;
/// Q3 (due valori in conflitto nel corpus) = Open con Ambiguous o Partial, openConflict canonico valorizzato, supporti che includono entrambi i valori, nessuna scelta fra i termini.
/// Criteri PASS (tutti): (1) payload dello stadio con l'intero ledger (conteggio) e UNA sola chiamata dello stadio; (2) un record finale valido per ogni Gap; (3) esiti e motivi come annotati;
/// (4) citazioni dei supporti esatte e riferimenti Open esistenti; (5) conflitto preservato; (6) nessun troncamento ne valutazione parziale; (7) token e latenza registrati (nessuna ricalibrazione applicata).
/// Errore, SKIP o fail-fast per budget non sono PASS. Un solo test per volta sul DGX, mai in parallelo con T082/T083, una esecuzione per corpus, solo con autorizzazione esplicita.
/// Servono BPROJECTS_TEST_POSTGRES, BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET e BPROJECTS_TEST_DGX_* con opt-in BPROJECTS_TEST_DGX_ALLOW=1; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Collection(RealParser.CollectionName)]
public abstract class DocumentGapStageMafRealTests : LargeDocumentRealFlow
{
    private const string AbsentQuestion = "Qual è il numero di telefono dell'assistenza clienti?";

    protected override bool GapStageExpected => true;

    // Dimensionamento sul LEDGER, non sul testo grezzo (finding T150 del 2026-11-10): con testo > 2xB il ledger (~0,8x il testo, ~104 token per evidenza) supera il budget di input dello
    // stadio e il run termina, come da contratto, con gap_ledger_exceeds_input (perimetro non coperto della 002). L'instradamento (b1) richiede invece solo che la chiamata singola non
    // sia affidabile: la stima di output (single-call-output-estimate-1) supera O gia a ~100 evidenze (~10k token di ledger). Target 0,95xB di testo => ledger ~0,75xB, sotto B meno la maggiorazione.
    protected override double TargetRatio => 0.95;

    protected override double MinimumRatio => 0.5;

    protected override int MinimumHierarchyBatches => 1;

    /// <summary>Attesa annotata per una domanda: parola chiave nell'enunciato del Gap, esito, motivi ammessi e citazioni dei supporti.</summary>
    protected sealed record Expectation(string Id, string QuestionToken, string Outcome, string[] Reasons, string[][] SupportQuotes, bool ExpectOpenConflict, bool ExpectProvenAbsence);

    protected abstract IReadOnlyList<Expectation> Expectations { get; }

    protected override async Task VerifyExtraAsync(HttpClient client, Guid project, Guid run, Guid revision, Corpus corpus, Content content, Verdicts v)
    {
        const string l = "[GAP]";
        var args = ("r", (object)run);

        // (1) lo stadio: un solo batch `gap`, un solo WorkOutput committato; il payload porta l'intero ledger non-Gap.
        var stageBatches = await Scalar<long>("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Stage\" = 'gap'", args);
        var stageOutputs = await Scalar<long>("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1' AND \"Status\" = 'Committed'", args);
        v.Check($"{l} (1) esattamente un batch dello stadio Gap e un WorkOutput gap-stage-alias-1 committato", stageBatches == 1 && stageOutputs == 1, $"batch={stageBatches} output={stageOutputs}");
        var payload = await Scalar<string>("SELECT \"Payload\"::text FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1' AND \"Status\" = 'Committed' LIMIT 1", args);
        using var stage = JsonDocument.Parse(payload);
        var info = stage.RootElement.GetProperty("stage");
        var candidates = info.GetProperty("candidates").GetInt32();
        var gapCount = info.GetProperty("gaps").GetInt32();
        var selected = await Scalar<long>(RunSignalSql.SelectedEvidences, args);
        v.Note($"{l} stadio: candidati={candidates} gap={gapCount} evidenze selezionate dal run={selected} catalogHash={Short(stage.RootElement.GetProperty("catalogHash").GetString() ?? "-", 16)} refsHash={Short(info.GetProperty("refsHash").GetString() ?? "-", 16)}");
        v.Check($"{l} (1) il payload dello stadio contiene l'INTERO ledger (candidati + Gap = evidenze selezionate dal run) e tanti Gap quante le domande", candidates + gapCount == selected && gapCount == Expectations.Count,
            $"candidati={candidates} gap={gapCount} selezionate={selected} domande={Expectations.Count}");
        v.Check($"{l} (1) searchBasis=ledger_exhaustive e retrievalComplete=true", info.GetProperty("searchBasis").GetString() == "ledger_exhaustive" && info.GetProperty("retrievalComplete").GetBoolean());

        // Una sola chiamata per lo stadio: le chiamate del run sono una per batch (gerarchia + stadio), nessun retry; la chiamata dello stadio e l'ultima.
        var batches = await Scalar<long>(RunSignalSql.Batches, args);
        var calls = await Scalar<long>("SELECT count(*) FROM \"InferenceCalls\" WHERE \"ProcessingRunId\" = @r", args);
        v.Check($"{l} (1) nessuna chiamata oltre una per batch: la chiamata dello stadio e unica", calls == batches, $"chiamate={calls} batch (gerarchia + stadio)={batches}");

        // evidenze Gap del progetto e record del coverage-3
        var gapPage = await Json(await client.GetAsync($"/api/projects/{project}/evidence?kind=Gap&viewVersion=1.1.0&limit=100", Ct));
        var gaps = gapPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Select(e => (Id: e.GetProperty("id").GetGuid(), Statement: e.GetProperty("statement").GetString() ?? "")).ToList();
        v.Note($"{l} evidenze Gap: " + string.Join(" | ", gaps.Select(g => Short(g.Statement, 80))));
        var docEvidence = await DocumentEvidences(client, project);

        var coverage = await client.PostAsJsonAsync($"/api/projects/{project}/analyses/{run}/coverage-reports", new { revisionId = revision }, Ct);
        var report = await Json(coverage);
        v.Check($"{l} coverage-3 creato", (coverage.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created) && report.TryGetProperty("checkerVersion", out var cv) && cv.GetString() == "coverage-3", $"{(int)coverage.StatusCode} {Short(report.GetRawText(), 300)}");
        if (!report.TryGetProperty("gapAssessments", out var assessments) || assessments.ValueKind != JsonValueKind.Array)
        {
            v.Check($"{l} gapAssessments presente", false);
            return;
        }

        var records = assessments.EnumerateArray().ToList();
        var summary = report.GetProperty("summary");
        v.Note($"{l} summary: expected={summary.GetProperty("expectedGaps").GetInt32()} assessed={summary.GetProperty("assessedGaps").GetInt32()} answered={summary.GetProperty("answeredGaps").GetInt32()} open={summary.GetProperty("openGaps").GetInt32()} unassessed={summary.GetProperty("unassessedGaps").GetInt32()}");

        // (2) un record finale per ogni Gap, nessun Gap non valutato
        v.Check($"{l} (2) un solo record valido per ciascun Gap e nessun Gap non valutato", summary.GetProperty("unassessedGaps").GetInt32() == 0 && gaps.Count == Expectations.Count
            && records.Count == gaps.Count && gaps.All(g => records.Count(r => r.GetProperty("gapEvidenceId").GetGuid() == g.Id) == 1), $"gap={gaps.Count} record={records.Count}");

        foreach (var expected in Expectations)
        {
            var gap = gaps.Where(g => Fold(g.Statement).Contains(Fold(expected.QuestionToken), StringComparison.Ordinal)).ToList();
            v.Check($"{l} {expected.Id}: la domanda e stata estratta come UNA evidenza Gap", gap.Count == 1, $"{gap.Count}");
            if (gap.Count != 1)
            {
                continue;
            }

            var record = records.FirstOrDefault(r => r.GetProperty("gapEvidenceId").GetGuid() == gap[0].Id);
            if (record.ValueKind != JsonValueKind.Object)
            {
                v.Check($"{l} {expected.Id}: record presente", false);
                continue;
            }

            var outcome = record.GetProperty("outcome").GetString();
            var reason = record.GetProperty("reason").GetString();
            var detail = Short(record.GetRawText(), 420);

            // (3) esito e motivo come annotati
            v.Check($"{l} (3) {expected.Id}: esito {expected.Outcome} con motivo in [{string.Join("/", expected.Reasons)}]", outcome == expected.Outcome && expected.Reasons.Contains(reason), detail);
            v.Check($"{l} {expected.Id}: motivazione e snapshot hash presenti", (record.GetProperty("rationale").GetString() ?? "").Trim().Length > 0 && (record.GetProperty("sourceSnapshotHash").GetString() ?? "").Length > 0);

            var support = record.GetProperty("supportingEvidenceIds").EnumerateArray().Select(x => x.GetGuid()).ToList();
            var openItem = record.GetProperty("openItemId").ValueKind != JsonValueKind.Null;
            var openConflict = record.GetProperty("openConflictId").ValueKind != JsonValueKind.Null;

            // (4) supporti: solo documentali, citazioni esatte, contenenti i valori attesi; riferimenti Open coerenti con l'esito
            v.Check($"{l} (4) {expected.Id}: i supporti sono evidenze documentali (mai un Gap) e le citazioni sono esatte nel blocco", support.All(id => docEvidence.Any(e => e.Id == id)) && await QuotesExactAsync(client, project, docEvidence, support),
                $"supporti={support.Count}");
            foreach (var quote in expected.SupportQuotes)
            {
                v.Check($"{l} (4) {expected.Id}: tra i supporti c'e il riscontro «{quote[0]}»",
                    support.Any(id => docEvidence.Where(e => e.Id == id).SelectMany(e => e.Sources).Any(s => quote.Any(q => Fold(s.Quote).Contains(Fold(q), StringComparison.Ordinal)))));
            }

            if (expected.Outcome == "AnsweredInSnapshot")
            {
                v.Check($"{l} (4) {expected.Id}: nessun riferimento Open e almeno un supporto", !openItem && !openConflict && support.Count > 0);
            }
            else
            {
                v.Check($"{l} (4) {expected.Id}: esattamente uno fra openItem e openConflict valorizzato", openItem ^ openConflict, $"openItem={openItem} openConflict={openConflict}");
            }

            // (5) conflitto preservato, nessuna scelta automatica
            if (expected.ExpectOpenConflict)
            {
                v.Check($"{l} (5) {expected.Id}: openConflict canonico valorizzato, conflitto Open nella revisione e nessuna risoluzione automatica", openConflict
                    && content.Conflicts.Any(c => c.Status == "Open") && content.Conflicts.All(c => !c.HasResolution), detail);
            }

            if (expected.ExpectProvenAbsence)
            {
                v.Check($"{l} (3) {expected.Id}: assenza provata (ledger esaustivo, estrazione completa) e campi derivati dal server",
                    record.TryGetProperty("absenceProven", out var proven) && proven.ValueKind == JsonValueKind.True
                    && record.TryGetProperty("searchBasis", out var basis) && basis.GetString() == "ledger_exhaustive"
                    && record.TryGetProperty("retrievalComplete", out var rc) && rc.ValueKind == JsonValueKind.True, detail);
            }
        }

        // (6) nessuna valutazione parziale (unassessed = 0 sopra) ne troncamento; (7) token e latenza dell'ultima chiamata del run, cioe quella dello stadio.
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT \"PromptTokens\", \"CompletionTokens\", \"DurationMs\", \"FinishReason\" FROM \"InferenceCalls\" WHERE \"ProcessingRunId\" = @r ORDER BY \"StartedAt\" DESC LIMIT 1", connection);
        command.Parameters.AddWithValue("r", run);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (await reader.ReadAsync(Ct))
        {
            v.Note($"{l} (7) chiamata dello stadio: prompt={reader.GetValue(0)} token, output={reader.GetValue(1)} token, durata={Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) / 1000}s, finish={reader.GetValue(3)}; "
                + $"maggiorazione di ingresso stimata {GapStageBudget.ReferenceAllowance(Expectations.Count)} token e stima di uscita gap-stage-estimate-1: confronto nel rapporto, nessuna ricalibrazione applicata");
            v.Check($"{l} (6) la chiamata dello stadio non e troncata (finish != length)", !string.Equals(reader.GetValue(3)?.ToString(), "length", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            v.Check($"{l} (7) chiamata dello stadio registrata", false);
        }
    }

    private static async Task<bool> QuotesExactAsync(HttpClient client, Guid project, IReadOnlyList<Ev> evidences, IReadOnlyList<Guid> ids)
    {
        foreach (var source in evidences.Where(e => ids.Contains(e.Id)).SelectMany(e => e.Sources))
        {
            if (await PassageText(client, project, source) != source.Quote)
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- scenari

    protected static readonly Expectation[] SingleExpectations =
    [
        new("Q1", "conserva i documenti di trasporto", "AnsweredInSnapshot", ["Answered"], [["84 mesi"]], false, false),
        new("Q2", "numero di telefono", "Open", ["NoAnswer"], [], false, true),
        new("Q3", "30 o di 60 giorni", "Open", ["Ambiguous", "Partial"], [["30 giorni"], ["60 giorni"]], true, false),
    ];

    protected static readonly Expectation[] MultiExpectations =
    [
        new("Q1", "durata del contratto quadro", "AnsweredInSnapshot", ["Answered"], [["48 mesi"]], false, false),
        new("Q2", "numero di telefono", "Open", ["NoAnswer"], [], false, true),
        new("Q3", "garanzia sui ricambi", "Open", ["Ambiguous", "Partial"], [["24 mesi"], ["36 mesi"]], true, false),
    ];

    public sealed class Single : DocumentGapStageMafRealTests
    {
        protected override IReadOnlyList<string> PmQuestions =>
        [
            "Per quanti mesi il portale Aurora conserva i documenti di trasporto?",
            AbsentQuestion,
            "Il termine di pagamento delle fatture è di 30 o di 60 giorni?",
        ];

        protected override IReadOnlyList<Expectation> Expectations => SingleExpectations;

        [Fact]
        public async Task T150_the_gap_stage_assesses_three_pm_questions_over_the_large_single_document_ledger()
        {
            TestEnvironment.RequireDgx();
            await Require();
            await RunScenarioAsync("T150-single", filler => LargeDocumentCorpus.Single(filler, 90));
        }
    }

    public sealed class Multi : DocumentGapStageMafRealTests
    {
        protected override IReadOnlyList<string> PmQuestions =>
        [
            "Qual è la durata del contratto quadro Zefiro?",
            AbsentQuestion,
            "Qual è la durata della garanzia sui ricambi?",
        ];

        protected override IReadOnlyList<Expectation> Expectations => MultiExpectations;

        [Fact]
        public async Task T150_the_gap_stage_assesses_three_pm_questions_over_the_large_multi_document_ledger()
        {
            TestEnvironment.RequireDgx();
            await Require();
            await RunScenarioAsync("T150-multi", filler => LargeDocumentCorpus.Multi(filler, 30));
        }
    }
}
