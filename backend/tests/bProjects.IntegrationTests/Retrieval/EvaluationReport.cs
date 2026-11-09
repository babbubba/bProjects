using System.Text;
using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

internal sealed record MetricSet(
    int Queries, IReadOnlyDictionary<string, double> Recall, IReadOnlyDictionary<string, double> Hit, IReadOnlyDictionary<string, double> Complete,
    double? FirstRankMean, int NeverFound)
{
    public static MetricSet From(AggregateMetrics a) => new(
        a.Queries, Round(a.Recall), Round(a.Hit), Round(a.Complete), a.FirstRankMean is { } m ? Math.Round(m, 6) : null, a.NeverFound);

    private static SortedDictionary<string, double> Round(IReadOnlyDictionary<int, double> values) =>
        new(values.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => Math.Round(kv.Value, 6)), StringComparer.Ordinal);
}

internal sealed record QueryReport(string Id, string Query, int RequiredBlocks, IReadOnlyList<int?> BlockPositions, IReadOnlyList<int?> EvidencePositions, int MessageRequired);

internal sealed record ConflictReport(string Id, string Status, IReadOnlyList<string> Sides);

internal sealed record CorpusReport(
    string Name, int Documents, int Blocks, int Queries, MetricSet BlockLexical, MetricSet BlockDense, MetricSet BlockHybrid, MetricSet EvidenceHybrid,
    int MappingLoss, int InvalidCitations, int MessageRequired, int MessageRequiredOffered, int ConflictsEvaluated, int LostConflicts,
    IReadOnlyList<ConflictReport> Conflicts, IReadOnlyList<QueryReport> PerQuery);

internal sealed record ReportConfiguration(
    string IndexConfigHash, string? PolicyHash, string EmbeddingModel, int EmbeddingDimension, string Reranker, string WeightsSha256, string FtsConfiguration,
    int Pool, int RrfK, string FusionTieBreak);

internal sealed record ReportAcceptance(string Status, string Statement, bool AnonymizedCorpus, bool IndependentQueries, IReadOnlyDictionary<string, string> Upstream);

internal sealed record ReportMeasurements(double IndexSecondsCold, double SearchMillisecondsMean, string Note);

/// <summary>
/// Rapporto di valutazione <c>retrieval-evaluation-report-1</c> (contracts/evaluation.md §5): hash degli input e dei gold, configurazione, versione dei servizi, modalità,
/// provenienza dei dati, autore delle query e limiti noti. La sezione <see cref="Measurements"/> (tempi) non entra nell'hash: il resto è deterministico.
/// </summary>
internal sealed record EvaluationReport(
    string Contract, string Mode, string ModeStatement, string DataOrigin, string QueryAuthor, string GoldSourceCommit, string GoldFrozenAt,
    IReadOnlyDictionary<string, string> InputHashes, string GoldManifestHash, ReportConfiguration Configuration, IReadOnlyList<CorpusReport> Corpora,
    ReportAcceptance Acceptance, IReadOnlyList<string> KnownLimits, ReportMeasurements? Measurements, string? ReportHash)
{
    public const string ContractName = "retrieval-evaluation-report-1";
    public const string Simulated = "Simulated";
    public const string Real = "Real";

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
    private static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    /// <summary>Hash del contenuto deterministico (senza tempi e senza l'hash stesso).</summary>
    public string ComputeHash() => CanonicalJson.Sha256Hex(JsonSerializer.Serialize(this with { Measurements = null, ReportHash = null }, Compact));

    public EvaluationReport Sealed() => this with { ReportHash = ComputeHash() };

    public string ToJson() => JsonSerializer.Serialize(this, Indented);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Rapporto di valutazione del recupero (003)");
        sb.AppendLine();
        sb.AppendLine($"- Modalità: **{Mode}**. {ModeStatement}");
        sb.AppendLine($"- Provenienza dei dati: {DataOrigin}; autore delle query: {QueryAuthor}; gold congelato il {GoldFrozenAt} (commit del laboratorio {GoldSourceCommit}).");
        sb.AppendLine($"- Configurazione: `IndexConfigHash` {Configuration.IndexConfigHash}; politica: {Configuration.PolicyHash ?? "non applicabile in M1 (T022)"}; embedding {Configuration.EmbeddingModel} ({Configuration.EmbeddingDimension}); riordino: {Configuration.Reranker}; SHA-256 dei pesi: {Configuration.WeightsSha256}; FTS {Configuration.FtsConfiguration}; pool {Configuration.Pool}; RRF k={Configuration.RrfK}.");
        sb.AppendLine($"- Hash gold (PROVENANCE.json): {GoldManifestHash}; hash del rapporto: {ReportHash}.");
        sb.AppendLine($"- Esito: **{Acceptance.Status}**. {Acceptance.Statement}");
        foreach (var (key, value) in Acceptance.Upstream)
        {
            sb.AppendLine($"  - {key}: {value}");
        }

        sb.AppendLine();
        foreach (var c in Corpora)
        {
            sb.AppendLine($"## {c.Name} ({c.Documents} documenti, {c.Blocks} blocchi, {c.Queries} query)");
            sb.AppendLine();
            sb.AppendLine("| Livello / segnale | Query | Recall@1 | @5 | @10 | @20 | @50 | Hit@1 | Hit@5 | Hit@10 | Tutti i riscontri @10 | Primo riscontro (media) | Mai trovato |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            Row(sb, "Blocco, lessicale", c.BlockLexical);
            Row(sb, "Blocco, denso", c.BlockDense);
            Row(sb, "Blocco, RRF", c.BlockHybrid);
            Row(sb, "Evidenza, RRF (dopo mappatura)", c.EvidenceHybrid);
            sb.AppendLine();
            sb.AppendLine($"Perdita di mappatura blocco→evidenza: {c.MappingLoss}. Citazioni invalide: {c.InvalidCitations}. Riscontri Message sempre inclusi: {c.MessageRequiredOffered}/{c.MessageRequired}. " +
                          $"Conflitti valutati: {c.ConflictsEvaluated}; persi (almeno un lato fuori dai primi {EvaluationMetrics.ConflictK}): {c.LostConflicts}.");
            sb.AppendLine();
        }

        sb.AppendLine("## Limiti noti");
        sb.AppendLine();
        foreach (var limit in KnownLimits)
        {
            sb.AppendLine($"- {limit}");
        }

        if (Measurements is { } m)
        {
            sb.AppendLine();
            sb.AppendLine($"Misure non deterministiche (fuori dall'hash): indicizzazione a freddo {m.IndexSecondsCold:F1} s; ricerca per query (media) {m.SearchMillisecondsMean:F1} ms. {m.Note}");
        }

        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, MetricSet m)
    {
        string P(double v) => (v * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%";
        sb.AppendLine($"| {label} | {m.Queries} | {P(m.Recall["1"])} | {P(m.Recall["5"])} | {P(m.Recall["10"])} | {P(m.Recall["20"])} | {P(m.Recall["50"])} | " +
                      $"{P(m.Hit["1"])} | {P(m.Hit["5"])} | {P(m.Hit["10"])} | {P(m.Complete["10"])} | {(m.FirstRankMean is { } f ? f.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "n/d")} | {m.NeverFound} |");
    }
}

/// <summary>Esito di accettazione (contracts/evaluation.md §6, D-2): le soglie da sole non bastano; mai «accettata» senza corpus anonimizzato e query indipendenti (SC-012).</summary>
internal static class AcceptanceDecision
{
    public const string Accepted = "Accettata";
    public const string AcceptedWithReservation = "Accettata con riserva";
    public const string NotAccepted = "Non accettata";
    public const string NotAssessable = "Non valutabile";
    public const string ReservationText = "manca la validazione su corpus anonimizzato e su query indipendenti";

    public static (string Status, string Statement) Decide(bool simulatedMode, bool thresholdsMet, bool anonymizedCorpus, bool independentQueries)
    {
        if (simulatedMode)
        {
            return (NotAssessable, "Modalità simulata: le soglie di qualità non si applicano (embedding simulati, nessuna semantica). Nessuna accettazione è possibile da questo rapporto.");
        }

        if (!thresholdsMet)
        {
            return (NotAccepted, "Le soglie sul gold sintetico congelato non sono rispettate.");
        }

        if (anonymizedCorpus && independentQueries)
        {
            return (Accepted, "Soglie rispettate su corpus anonimizzato e con query indipendenti.");
        }

        return (AcceptedWithReservation, $"Soglie rispettate, ma {ReservationText}: non equivale all'accettazione piena.");
    }
}

/// <summary>Regressione ≤ 2 punti rispetto alla baseline dichiarata, SOLO sullo stesso insieme di query (hash coincidenti): insiemi diversi non sono confrontabili e il confronto è rifiutato.</summary>
internal static class RegressionCheck
{
    public const double Tolerance = 0.02;

    public static IReadOnlyList<string> Compare(
        IReadOnlyDictionary<string, string> baselineInputs, IReadOnlyDictionary<string, string> currentInputs,
        IReadOnlyDictionary<string, double> baselineMetrics, IReadOnlyDictionary<string, double> currentMetrics)
    {
        var problems = new List<string>();
        var sameInputs = baselineInputs.Count == currentInputs.Count && baselineInputs.All(kv => currentInputs.TryGetValue(kv.Key, out var v) && v == kv.Value);
        if (!sameInputs)
        {
            problems.Add("input_hash_mismatch: gli hash di input/gold non coincidono con la baseline; insiemi diversi non sono confrontabili e il confronto è rifiutato");
            return problems;
        }

        foreach (var (name, baseline) in baselineMetrics)
        {
            if (!currentMetrics.TryGetValue(name, out var current))
            {
                problems.Add($"{name}: metrica assente dal rapporto corrente");
            }
            else if (baseline - current > Tolerance + 1e-9)
            {
                problems.Add($"{name}: regressione di {(baseline - current) * 100:F1} punti (baseline {baseline:F4}, corrente {current:F4})");
            }
        }

        return problems;
    }
}
