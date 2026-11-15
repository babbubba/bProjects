using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>Una riga del ledger rappresentativo: identificativo di riga del documento (o del messaggio PM), annotazione di tipo/polarità/certezza, enunciato, citazione LETTERALE e offset reali (UTF-16, fine riga LF).</summary>
internal sealed record RepresentativeRow(
    string Ref, string Doc, int Line, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement, string Quote, int Start, int End, string? ExtraRef);

internal sealed record RepresentativeFact(string Ref, string Doc, string Phrase, string Kind, string Certainty, string Section, string Classification);

internal sealed record RepresentativeConflict(string Id, string Older, string Newer, string Topic);

internal sealed record RepresentativeGap(string Ref, string Question, string ExpectedOutcome, IReadOnlyList<string> RelatedRefs);

/// <summary>Gold annotato PRIMA di ogni esecuzione (<c>gold.json</c>): fatti attesi con frase esatta, conflitti di versione attesi, Gap con esito atteso, evidenze con più fonti.</summary>
internal sealed record RepresentativeGold(
    IReadOnlyList<RepresentativeFact> Facts, IReadOnlyList<RepresentativeConflict> Conflicts, IReadOnlyList<RepresentativeGap> Gaps, IReadOnlyList<(string Ref, string ExtraRef)> MultiSource);

/// <summary>
/// 002 reconciliation-hardening RH-040 — ledger della fixture rappresentativa ERP/EDI (sintetica, scritta da zero, nessun documento cliente). Ogni riga identificata di <c>af-integrazione-edi.md</c> e
/// <c>verbale-riunione-2.md</c> (elenchi «ID. testo» e righe di tabella «| ID | … |») e ogni dichiarazione di <c>pm-messages.json</c> indicata nella tabella dei messaggi è UNA evidenza: l'enunciato è il testo
/// della riga, la citazione è la riga stessa (letterale) e gli offset sono quelli reali nel file. Tipo, polarità e certezza sono annotati qui per prefisso di identificativo, con le eccezioni dichiarate in
/// <see cref="Overrides"/>; il gold (<c>gold.json</c>) è indipendente e lo verifica. Il ledger NON è un risultato di modello.
/// </summary>
internal static class RepresentativeLedger
{
    public const string Folder = "Reconciliation/Representative";
    public const string Af = "af-integrazione-edi.md";
    public const string Verbale = "verbale-riunione-2.md";
    public const string PmFile = "pm-messages.json";
    public const string GoldFile = "gold.json";
    public const string ProvenanceFile = "PROVENANCE.json";

    public static readonly Guid Project = MeasurementLedger.Id("representative|project");

    /// <summary>Contenuto del file con fine riga normalizzata a LF: hash e offset non dipendono dal checkout (CRLF).</summary>
    public static string Read(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, Folder, file), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Read(file))));

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static Guid EvidenceId(string reference) => MeasurementLedger.Id("rep|ev|" + reference);

    public static string DocumentKey(string doc) => doc switch { "af" => Af, "verbale" => Verbale, _ => PmFile };

    // ---------------------------------------------------------------- annotazione

    private static readonly Regex Bullet = new(@"^- (?<id>[A-Z]{2,3}-\d{2})\. (?<text>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex TableRow = new(@"^\| (?<id>[A-Z]{2,3}-\d{2}) \| (?<rest>.+) \|$", RegexOptions.CultureInvariant);

    private static (EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty) Default(string prefix) => prefix switch
    {
        "OB" => (EvidenceKind.Goal, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        "CT" => (EvidenceKind.Context, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        "FO" or "FD" or "FI" => (EvidenceKind.Action, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        "RF" or "ORD" or "DES" or "INV" or "UM" or "CS" or "ER" or "MN" => (EvidenceKind.Requirement, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        "VN" => (EvidenceKind.Constraint, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        "DC" => (EvidenceKind.Decision, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        "EX" => (EvidenceKind.Exclusion, EvidencePolarity.Excluded, EvidenceCertainty.Declared),
        "DP" => (EvidenceKind.Dependency, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        "AS" => (EvidenceKind.Assumption, EvidencePolarity.Affirmed, EvidenceCertainty.Hypothesized),
        "AL" => (EvidenceKind.Alternative, EvidencePolarity.Undefined, EvidenceCertainty.Hypothesized),
        "DA" => (EvidenceKind.Decision, EvidencePolarity.Undefined, EvidenceCertainty.ToClarify),
        "VB" => (EvidenceKind.Context, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        _ => throw new InvalidOperationException("Prefisso senza annotazione: " + prefix),
    };

    /// <summary>Eccezioni al default per prefisso (tipo, polarità, certezza).</summary>
    private static readonly Dictionary<string, (EvidenceKind, EvidencePolarity, EvidenceCertainty)> Overrides = new(StringComparer.Ordinal)
    {
        ["FI-03"] = (EvidenceKind.Requirement, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["ORD-10"] = (EvidenceKind.Constraint, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["VN-03"] = (EvidenceKind.Constraint, EvidencePolarity.Prohibited, EvidenceCertainty.Declared),
        ["VN-04"] = (EvidenceKind.Constraint, EvidencePolarity.Prohibited, EvidenceCertainty.Declared),
        ["VN-09"] = (EvidenceKind.Constraint, EvidencePolarity.Prohibited, EvidenceCertainty.Declared),
        ["DC-04"] = (EvidenceKind.Decision, EvidencePolarity.Negated, EvidenceCertainty.Declared),
        ["IN-01"] = (EvidenceKind.Context, EvidencePolarity.Affirmed, EvidenceCertainty.Inferred),
        ["IN-02"] = (EvidenceKind.Requirement, EvidencePolarity.Affirmed, EvidenceCertainty.Inferred),
        ["IN-03"] = (EvidenceKind.Constraint, EvidencePolarity.Affirmed, EvidenceCertainty.Inferred),
        ["IN-04"] = (EvidenceKind.Context, EvidencePolarity.Affirmed, EvidenceCertainty.Inferred),
        ["AL-03"] = (EvidenceKind.Alternative, EvidencePolarity.Undefined, EvidenceCertainty.ToClarify),
        ["VB-03"] = (EvidenceKind.Decision, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["VB-05"] = (EvidenceKind.Decision, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["VB-06"] = (EvidenceKind.Action, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["VB-07"] = (EvidenceKind.Constraint, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["VB-08"] = (EvidenceKind.Requirement, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["VB-09"] = (EvidenceKind.Action, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ["VB-10"] = (EvidenceKind.Action, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
    };

    private static string StatementOfTableRow(string id, string rest)
    {
        var c = rest.Split(" | ");
        return id.Split('-')[0] switch
        {
            "ORD" => $"Tracciato ORDERS, campo {c[0]}: tipo {c[1]}, lunghezza {c[2]}, obbligatorio {c[3]}; {c[4]}",
            "DES" => $"Tracciato DESADV, campo {c[0]}: tipo {c[1]}, lunghezza {c[2]}, obbligatorio {c[3]}; {c[4]}",
            "INV" => $"Tracciato INVOIC, campo {c[0]}: tipo {c[1]}, lunghezza {c[2]}, obbligatorio {c[3]}; {c[4]}",
            "UM" => $"Trascodifica delle unità di misura: il codice cliente {c[0]} corrisponde a {c[1]} ({c[2]})",
            "CS" => $"Trascodifica delle causali di scarto: il codice {c[0]} di Hub Beta corrisponde a {c[1]} ({c[2]})",
            _ => throw new InvalidOperationException("Tabella senza regola di enunciato: " + id),
        };
    }

    // ---------------------------------------------------------------- righe

    private static readonly Lazy<RepresentativeGold> GoldLazy = new(LoadGold);
    private static readonly Lazy<IReadOnlyList<RepresentativeRow>> RowsLazy = new(BuildRows);

    public static RepresentativeGold Gold => GoldLazy.Value;

    public static IReadOnlyList<RepresentativeRow> Rows => RowsLazy.Value;

    public static RepresentativeRow Row(string reference) => Rows.Single(r => r.Ref == reference);

    private static IEnumerable<RepresentativeRow> ParseDocument(string doc, string file, IReadOnlyDictionary<string, string?> extra)
    {
        var text = Read(file);
        var offset = 0;
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            string? id = null, statement = null, quote = null;
            var quoteAt = 0;
            if (Bullet.Match(line) is { Success: true } b)
            {
                (id, statement, quote, quoteAt) = (b.Groups["id"].Value, b.Groups["text"].Value, line[2..], offset + 2);
            }
            else if (TableRow.Match(line) is { Success: true } t)
            {
                (id, statement, quote, quoteAt) = (t.Groups["id"].Value, StatementOfTableRow(t.Groups["id"].Value, t.Groups["rest"].Value), line[2..^2], offset + 2);
            }

            if (id is not null)
            {
                var (kind, polarity, certainty) = Overrides.TryGetValue(id, out var o) ? o : Default(id.Split('-')[0]);
                yield return new RepresentativeRow(id, doc, lineNumber, kind, polarity, certainty, statement!, quote!, quoteAt, quoteAt + quote!.Length, extra.GetValueOrDefault(id));
            }

            offset += line.Length + 1;
        }
    }

    /// <summary>Messaggi del PM: (identificativo evidenza, id messaggio, citazione, tipo, polarità, certezza). Le due domande sono Gap.</summary>
    private static readonly (string Ref, string MessageId, string Quote, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty)[] PmEvidence =
    [
        ("PM-01", "pm-1", "Gestionale Alfa resta il sistema di riferimento per le anagrafiche dei clienti.", EvidenceKind.Constraint, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ("PM-02", "pm-1", "Il go-live della prima fase è fissato a marzo.", EvidenceKind.Decision, EvidencePolarity.Affirmed, EvidenceCertainty.Declared),
        ("PM-03", "pm-2", "Chi gestisce gli scarti della DESADV?", EvidenceKind.Gap, EvidencePolarity.Undefined, EvidenceCertainty.Declared),
        ("PM-04", "pm-3", "La fattura va inviata entro 24 o 48 ore?", EvidenceKind.Gap, EvidencePolarity.Undefined, EvidenceCertainty.Declared),
    ];

    public sealed record PmMessage(string Id, long Sequence, string Text);

    public static IReadOnlyList<PmMessage> Messages { get; } = LoadMessages();

    private static IReadOnlyList<PmMessage> LoadMessages()
    {
        using var doc = JsonDocument.Parse(Read(PmFile));
        return [.. doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => new PmMessage(m.GetProperty("id").GetString()!, m.GetProperty("sequence").GetInt64(), m.GetProperty("text").GetString()!))];
    }

    private static IReadOnlyList<RepresentativeRow> BuildRows()
    {
        var extra = Gold.MultiSource.ToDictionary(m => m.Ref, m => (string?)m.ExtraRef, StringComparer.Ordinal);
        var rows = ParseDocument("af", Af, extra).Concat(ParseDocument("verbale", Verbale, extra)).ToList();
        foreach (var (reference, messageId, quote, kind, polarity, certainty) in PmEvidence)
        {
            var message = Messages.Single(m => m.Id == messageId);
            var start = message.Text.IndexOf(quote, StringComparison.Ordinal);
            rows.Add(new RepresentativeRow(reference, "pm", (int)message.Sequence, kind, polarity, certainty, quote, quote, start, start + quote.Length, null));
        }

        return rows;
    }

    private static RepresentativeGold LoadGold()
    {
        using var doc = JsonDocument.Parse(Read(GoldFile));
        var root = doc.RootElement;
        string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;
        return new RepresentativeGold(
            [.. root.GetProperty("facts").EnumerateArray().Select(f => new RepresentativeFact(S(f, "ref"), S(f, "doc"), S(f, "phrase"), S(f, "kind"), S(f, "certainty"), S(f, "section"), S(f, "classification")))],
            [.. root.GetProperty("versionConflicts").EnumerateArray().Select(c => new RepresentativeConflict(S(c, "id"), S(c, "older"), S(c, "newer"), S(c, "topic")))],
            [.. root.GetProperty("gaps").EnumerateArray().Select(g => new RepresentativeGap(S(g, "ref"), S(g, "question"), S(g, "expectedOutcome"), [.. g.GetProperty("relatedRefs").EnumerateArray().Select(r => r.GetString()!)]))],
            [.. root.GetProperty("multiSourceEvidence").GetProperty("refs").EnumerateArray().Select(r => (S(r, "ref"), S(r, "extraSourceRef")))]);
    }

    // ---------------------------------------------------------------- input del workflow

    private static PersistedSource SourceOf(RepresentativeRow row, string tag)
    {
        var message = row.Doc == "pm" ? Messages.Single(m => m.Sequence == row.Line) : null;
        return new PersistedSource
        {
            SourceId = MeasurementLedger.Id($"rep|src|{row.Ref}|{tag}"), Kind = message is null ? EvidenceSourceKind.DocumentPassage : EvidenceSourceKind.Message, Role = EvidenceSourceRole.Primary,
            CitationGroupId = MeasurementLedger.Id($"rep|grp|{row.Ref}|{tag}"), Part = 0,
            MessageId = message is null ? null : MeasurementLedger.Id("rep|msg|" + message.Id), Sequence = message?.Sequence,
            DocumentId = message is null ? MeasurementLedger.Id("rep|doc|" + row.Doc) : null, DocumentVersionId = message is null ? MeasurementLedger.Id("rep|ver|" + row.Doc) : null,
            PreparationId = message is null ? MeasurementLedger.Id("rep|prep|" + row.Doc) : null, ArtifactId = message is null ? MeasurementLedger.Id("rep|art|" + row.Doc) : null,
            BlockId = message is null ? MeasurementLedger.Id($"rep|blk|{row.Doc}|{row.Line}") : null, Start = row.Start, End = row.End, Quote = row.Quote,
        };
    }

    public static DocumentAnalysisEvidence EvidenceOf(RepresentativeRow row)
    {
        var sources = new List<PersistedSource> { SourceOf(row, "0") };
        if (row.ExtraRef is not null)
        {
            sources.Add(SourceOf(Row(row.ExtraRef), "x" + row.Ref));
        }

        return new DocumentAnalysisEvidence(
            EvidenceId(row.Ref), MeasurementLedger.Id("rep|evver|" + row.Ref), row.Doc == "pm" ? EvidenceOriginKind.Message : EvidenceOriginKind.Document, row.Kind, row.Polarity, row.Certainty, row.Statement,
            Hex($"{row.Kind}|{row.Polarity}|{row.Statement}"), sources);
    }

    public static DocumentAnalysisInput Input()
    {
        var evidence = Rows.Select(EvidenceOf).ToList();
        var sourceHash = Hex("rep|source|" + string.Join('|', Rows.Select(r => r.Ref + ":" + Hex(r.Statement))));
        var snapshot = new ReconciliationSnapshot(Project, sourceHash, [.. evidence.Select(e => new SnapshotEvidence(e.EvidenceId, e.VersionId, e.ContentHash, e.Sources))], []);
        var dialog = Messages.Select(m => new bProjects.App.Analysis.AnalysisDialogMessage(MeasurementLedger.Id("rep|msg|" + m.Id), bProjects.App.Analysis.AnalysisMessageRole.User, m.Sequence, m.Text)).ToList();
        return new DocumentAnalysisInput(Project, MeasurementLedger.Id("rep|run"), Messages.Count, Messages.Count, 2, 0, Hex("rep|snapshot|" + sourceHash), snapshot, evidence, dialog);
    }

    /// <summary>Metadati documentali (documento, versione, sezione del titolo) delle sole evidenze documentali, per lo stadio Gap (D2).</summary>
    public static IReadOnlyDictionary<Guid, GapStageEvidenceMeta> Meta()
    {
        var result = new Dictionary<Guid, GapStageEvidenceMeta>();
        foreach (var (doc, file) in new[] { ("af", Af), ("verbale", Verbale) })
        {
            string? section = null, sub = null;
            var lines = Read(file).Split('\n');
            var byLine = Rows.Where(r => r.Doc == doc).ToDictionary(r => r.Line);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("## ", StringComparison.Ordinal))
                {
                    (section, sub) = (lines[i][3..], null);
                }
                else if (lines[i].StartsWith("### ", StringComparison.Ordinal))
                {
                    sub = lines[i][4..];
                }

                if (byLine.TryGetValue(i + 1, out var row))
                {
                    result[EvidenceId(row.Ref)] = new GapStageEvidenceMeta(file, 1, sub is null ? section : $"{section} > {sub}");
                }
            }
        }

        return result;
    }
}
