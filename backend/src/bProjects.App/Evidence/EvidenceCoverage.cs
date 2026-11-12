using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.App.Evidence;

public enum FindingCategory
{
    /// <summary>Verificabile formalmente: ID, fonte, span, relazione, regola.</summary>
    DeterministicError,

    /// <summary>Stima lessicale con incertezza dichiarata: richiede interpretazione del linguaggio naturale.</summary>
    SemanticRisk,

    /// <summary>Effetto di una decisione del PM nella revisione: da valutare, non un errore automatico.</summary>
    PmAssessment,
}

public sealed record CoverageFinding(
    FindingCategory Category, string Code, string Message, Guid? EvidenceId, string? Alias, Guid? ItemId, string? Section, string? Confidence, string? Details);

public sealed record CoverageReportContent(
    string CheckerVersion, Guid AnalysisId, Guid AiRevisionId, Guid RevisionId, Guid? DraftId, string ViewHash, IReadOnlyList<CoverageFinding> Findings,
    IReadOnlyList<string> Limits);

/// <summary>Elemento di AnalysisContent con la sezione in cui si trova.</summary>
public sealed record SectionItem(string Section, AnalysisItem Item, Guid? ConflictId = null, AnalysisConflictStatus? ConflictStatus = null);

/// <summary>
/// Verifica di copertura e provenienza `coverage-v1` (contracts/ai-workflow.md §10.8): confronta la selezione congelata del run, la mappa
/// elementi→evidenze, la revisione AI, la revisione target e l'eventuale bozza. Riutilizzabile, senza regole di casi specifici; separa
/// errori deterministici, rischi semantici stimati (euristiche lessicali, mai giudizi certi) e valutazioni PM. Non modifica nulla.
/// </summary>
public static partial class EvidenceCoverage
{
    public const string CheckerVersion = "coverage-v1";

    public static IReadOnlyList<string> Limits { get; } =
    [
        "La mappa elementi→evidenze è dichiarata dal modello nel consolidamento: un collegamento non dimostra che il testo dell'elemento sia fedele all'evidenza.",
        "I rischi semantici sono euristiche lessicali (numeri, marcatori di negazione, parole significative): sinonimi, pronomi, soggetti impliciti e riformulazioni non sono riconosciuti.",
        "Nessun modello linguistico valuta la copertura in questa versione: la verifica semantica resta della revisione PM.",
        "Le evidenze sono interpretazioni del testo PM: un'estrazione incompleta non è rilevata da questo report.",
    ];

    private static readonly HashSet<string> Structural = ["goals", "contextAndScope.included", "requirements", "operationalFlows", "constraints", "dependencies"];

    public static IReadOnlyList<SectionItem> Items(AnalysisContent content)
    {
        var items = new List<SectionItem>();
        void Add(string section, IEnumerable<AnalysisItem> list) => items.AddRange(list.Select(i => new SectionItem(section, i)));
        Add("goals", content.Goals);
        Add("contextAndScope.overview", content.ContextAndScope.Overview);
        Add("contextAndScope.included", content.ContextAndScope.Included);
        Add("contextAndScope.excluded", content.ContextAndScope.Excluded);
        Add("requirements", content.Requirements);
        Add("operationalFlows", content.OperationalFlows);
        Add("constraints", content.Constraints);
        Add("dependencies", content.Dependencies);
        Add("assumptions", content.Assumptions);
        Add("openQuestions", content.OpenQuestions);
        foreach (var conflict in content.Conflicts)
        {
            items.AddRange(conflict.Alternatives.Select(a => new SectionItem("conflicts", a, conflict.Id, conflict.Status)));
        }

        return items;
    }

    public static IReadOnlyList<CoverageFinding> Check(
        EvidenceSelectionView view,
        IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> links,
        AnalysisContent ai,
        AnalysisContent target,
        string? draftMarkdown,
        IReadOnlyDictionary<Guid, string> messageBodies)
    {
        var findings = new List<CoverageFinding>();
        var aiItems = Items(ai).ToDictionary(i => i.Item.Id);
        var targetItems = Items(target).ToDictionary(i => i.Item.Id);
        var byEvidence = links.GroupBy(l => l.EvidenceId).ToDictionary(g => g.Key, g => g.Select(l => l.ItemId).Distinct().ToList());
        var pmSources = target.Sources.Where(s => s.Kind == AnalysisSourceKind.PmRevision).Select(s => s.Id).ToHashSet();
        var openAliases = view.Relations.Where(r => r.Open).SelectMany(r => new[] { r.FromAlias, r.ToAlias }).ToHashSet();
        var settled = view.Relations
            .Where(r => r.Basis == EvidenceRelationBasis.Declared && r.Type is EvidenceRelationType.Corrects or EvidenceRelationType.Supersedes)
            .Select(r => r.ToAlias).ToHashSet();

        foreach (var e in view.Evidences)
        {
            var linked = byEvidence.GetValueOrDefault(e.EvidenceId) ?? [];
            var inAi = linked.Where(aiItems.ContainsKey).Select(id => aiItems[id]).ToList();
            var inTarget = linked.Where(targetItems.ContainsKey).Select(id => targetItems[id]).ToList();

            foreach (var span in e.Content.Sources)
            {
                if (!messageBodies.TryGetValue(span.MessageId, out var body) || span.Start < 0 || span.End > body.Length || span.Start >= span.End
                    || !string.Equals(body[span.Start..span.End], span.Quote, StringComparison.Ordinal))
                {
                    findings.Add(new(FindingCategory.DeterministicError, "evidence_span_invalid",
                        $"{e.Alias}: la citazione [{span.Start},{span.End}) non corrisponde al testo originale del messaggio di sequenza {span.Sequence}.",
                        e.EvidenceId, e.Alias, null, null, null, null));
                }
            }

            if (inAi.Count == 0)
            {
                findings.Add(new(FindingCategory.DeterministicError, "evidence_unrepresented",
                    $"{e.Alias} ({e.Kind}, sequenza {e.Sequence}) non è collegata a nessun elemento dell'analisi: «{Short(e.Statement)}».",
                    e.EvidenceId, e.Alias, null, null, null, null));
                continue;
            }

            foreach (var item in inAi.Where(i => !i.Item.EvidenceRefs.Contains(e.MessageId)))
            {
                findings.Add(new(FindingCategory.DeterministicError, "evidence_source_mismatch",
                    $"L'elemento collegato a {e.Alias} non cita il messaggio di sequenza {e.Sequence} da cui l'evidenza proviene.",
                    e.EvidenceId, e.Alias, item.Item.Id, item.Section, null, null));
            }

            foreach (var item in inAi.Where(i => !targetItems.ContainsKey(i.Item.Id)))
            {
                findings.Add(new(FindingCategory.PmAssessment, "item_removed_by_pm",
                    $"L'elemento che rappresentava {e.Alias} non è più nella revisione verificata: rimosso dal PM.",
                    e.EvidenceId, e.Alias, item.Item.Id, item.Section, null, null));
            }

            foreach (var item in inTarget.Where(i => aiItems.TryGetValue(i.Item.Id, out var original) && !string.Equals(original.Item.Text, i.Item.Text, StringComparison.Ordinal)))
            {
                findings.Add(new(FindingCategory.PmAssessment, "item_rewritten_by_pm",
                    $"L'elemento che rappresentava {e.Alias} è stato riscritto dal PM.",
                    e.EvidenceId, e.Alias, item.Item.Id, item.Section, null, $"prima: «{Short(aiItems[item.Item.Id].Item.Text)}» · dopo: «{Short(item.Item.Text)}»"));
            }

            var openAlternative = (openAliases.Contains(e.Alias) || e.Kind == EvidenceKind.Alternative) && !settled.Contains(e.Alias);
            if (openAlternative)
            {
                foreach (var item in inTarget.Where(i => Structural.Contains(i.Section)))
                {
                    var byPm = item.Item.EvidenceRefs.Any(pmSources.Contains);
                    findings.Add(byPm
                        ? new(FindingCategory.PmAssessment, "pm_decided_alternative",
                            $"Il PM ha riportato l'alternativa aperta {e.Alias} in {item.Section}: decisione da confermare.", e.EvidenceId, e.Alias, item.Item.Id, item.Section, null, null)
                        : new(FindingCategory.DeterministicError, "open_conflict_as_decision",
                            $"{e.Alias} è un'alternativa non decisa ma compare come scelta in {item.Section}.", e.EvidenceId, e.Alias, item.Item.Id, item.Section, null, null));
                }
            }

            if (inTarget.Count == 0)
            {
                continue;
            }

            var texts = inTarget.Select(i => i.Item.Text).ToList();
            foreach (var q in e.Content.Qualifiers)
            {
                var numbers = Numbers(q.Text);
                var missing = numbers.Where(n => !texts.Any(t => Numbers(t).Contains(n))).ToList();
                if (missing.Count > 0)
                {
                    findings.Add(new(FindingCategory.SemanticRisk, "qualifier_changed",
                        $"{e.Alias}: il qualificatore «{q.Text}» ({q.Type}) non ritrova {string.Join(", ", missing)} negli elementi collegati.",
                        e.EvidenceId, e.Alias, inTarget[0].Item.Id, inTarget[0].Section, "medium", null));
                }
            }

            var negative = e.Polarity is EvidencePolarity.Negated or EvidencePolarity.Excluded or EvidencePolarity.Prohibited;
            if (negative && !inTarget.Any(i => i.Section == "contextAndScope.excluded" || Negation().IsMatch(i.Item.Text)))
            {
                findings.Add(new(FindingCategory.SemanticRisk, "negation_inverted",
                    $"{e.Alias} è {e.Polarity} ma nessun elemento collegato è un'esclusione o contiene una negazione.",
                    e.EvidenceId, e.Alias, inTarget[0].Item.Id, inTarget[0].Section, "medium", null));
            }
            else if (e.Polarity == EvidencePolarity.Affirmed && inTarget.All(i => i.Section == "contextAndScope.excluded"))
            {
                findings.Add(new(FindingCategory.SemanticRisk, "negation_inverted",
                    $"{e.Alias} è un'affermazione ma è rappresentata solo fra le esclusioni.",
                    e.EvidenceId, e.Alias, inTarget[0].Item.Id, inTarget[0].Section, "low", null));
            }

            if (e.Content.Structure is { Instrument: { } instrument, Action: { } action })
            {
                var tool = Words(instrument);
                var act = Words(action);
                if (tool.Count > 0 && act.Count > 0 && !inTarget.Any(i => Contains(i.Item.Text, tool) && Contains(i.Item.Text, act)))
                {
                    findings.Add(new(FindingCategory.SemanticRisk, "instrument_detached",
                        $"{e.Alias}: lo strumento «{instrument}» e l'azione «{action}» non compaiono insieme in nessun elemento collegato.",
                        e.EvidenceId, e.Alias, inTarget[0].Item.Id, inTarget[0].Section, "medium", null));
                }
            }
        }

        foreach (var stale in view.StaleRelations)
        {
            findings.Add(new(FindingCategory.DeterministicError, "relation_stale",
                $"Relazione {stale.Type} non più risolvibile nella selezione: {stale.Reason}.", stale.FromEvidenceId, null, null, null, null, stale.RelationId.ToString()));
        }

        if (draftMarkdown is not null)
        {
            var cited = SourceMarker().Matches(draftMarkdown).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var document = Normalize(draftMarkdown);
            foreach (var item in targetItems.Values)
            {
                if (item.Item.EvidenceRefs.Count > 0 && !item.Item.EvidenceRefs.Any(r => cited.Contains(r.ToString())))
                {
                    findings.Add(new(FindingCategory.DeterministicError, "revision_source_not_cited",
                        $"Nessuna fonte dell'elemento confermato in {item.Section} è citata nella bozza: «{Short(item.Item.Text)}».",
                        null, null, item.Item.Id, item.Section, null, null));
                }

                var words = Words(item.Item.Text);
                if (words.Count >= 3)
                {
                    var present = words.Count(w => document.Contains(w, StringComparison.Ordinal));
                    var ratio = (double)present / words.Count;
                    if (ratio < 0.5)
                    {
                        findings.Add(new(FindingCategory.SemanticRisk, "revision_item_missing_in_document",
                            $"L'elemento confermato in {item.Section} sembra assente dalla bozza ({present}/{words.Count} parole significative): «{Short(item.Item.Text)}».",
                            null, null, item.Item.Id, item.Section, ratio < 0.3 ? "medium" : "low", null));
                    }
                }
            }
        }

        return findings;
    }

    [GeneratedRegex(@"\b(non|niente|nessun\w*|senza|mai|vietat\w*|esclus\w*|né|no)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex Negation();

    [GeneratedRegex(@"\[fonte:([^\]\s]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex SourceMarker();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.Ordinal)
    {
        ["due"] = "2", ["tre"] = "3", ["quattro"] = "4", ["cinque"] = "5", ["sei"] = "6", ["sette"] = "7",
        ["otto"] = "8", ["nove"] = "9", ["dieci"] = "10", ["undici"] = "11", ["dodici"] = "12", ["quindici"] = "15", ["venti"] = "20",
        ["trenta"] = "30", ["quaranta"] = "40", ["cinquanta"] = "50", ["sessanta"] = "60", ["cento"] = "100", ["mille"] = "1000",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "alla", "alle", "agli", "dalla", "dalle", "dagli", "della", "delle", "degli", "nella", "nelle", "negli", "sulla", "sulle", "sugli",
        "come", "anche", "sono", "deve", "devono", "essere", "viene", "vengono", "questo", "questa", "quello", "quella", "tutti", "tutte",
        "ogni", "loro", "dove", "quando", "mentre", "perche", "oppure", "ancora", "sistema", "portale", "progetto",
    };

    /// <summary>Numeri del testo, in cifre o in parole (normalizzati in cifre).</summary>
    public static IReadOnlySet<string> Numbers(string text)
    {
        var result = Digits().Matches(text).Select(m => m.Value.Replace(',', '.')).ToHashSet(StringComparer.Ordinal);
        foreach (Match token in Token().Matches(text.ToLowerInvariant()))
        {
            if (NumberWords.TryGetValue(token.Value, out var digits))
            {
                result.Add(digits);
            }
        }

        return result;
    }

    /// <summary>Token numerici del testo come MULTIINSIEME ordinato (cifre con la virgola normalizzata a punto, più i numeri in parole in cifre): D3, confronto strutturale di ripetizione.</summary>
    internal static IReadOnlyList<string> NumberTokens(string text)
    {
        var tokens = Digits().Matches(text).Select(m => m.Value.Replace(',', '.')).ToList();
        tokens.AddRange(Token().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(NumberWords.ContainsKey).Select(w => NumberWords[w]));
        return [.. tokens.Order(StringComparer.Ordinal)];
    }

    /// <summary>Radici (prime cinque lettere) delle parole significative: almeno quattro lettere, senza parole funzionali.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        [.. Token().Matches(Normalize(text)).Select(m => m.Value).Where(w => w.Length >= 4 && !StopWords.Contains(w) && !w.All(char.IsDigit))
            .Select(w => w.Length > 5 ? w[..5] : w).Distinct(StringComparer.Ordinal)];

    internal static bool Contains(string text, IReadOnlyList<string> stems)
    {
        var normalized = Normalize(text);
        return stems.Any(s => normalized.Contains(s, StringComparison.Ordinal));
    }

    internal static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(c);
        }

        return builder.ToString();
    }

    internal static string Short(string text) => text.Length <= 90 ? text : text[..90] + "…";

    public static string InputHash(string viewHash, IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> links, Guid aiRevisionId, Guid revisionId, Guid? draftId, string revisionJson, string? draftMarkdown)
    {
        var text = new StringBuilder()
            .Append(CheckerVersion).Append('|').Append(viewHash).Append('|').Append(aiRevisionId).Append('|').Append(revisionId).Append('|').Append(draftId)
            .Append('|').Append(string.Join(",", links.OrderBy(l => l.ItemId).ThenBy(l => l.EvidenceId).Select(l => $"{l.ItemId}:{l.EvidenceId}")))
            .Append('|').Append(revisionJson).Append('|').Append(draftMarkdown);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

public sealed record CoverageRequest(Guid? RevisionId, Guid? DraftId);

public sealed record CoverageCountsView(int DeterministicErrors, int SemanticRisks, int PmAssessments);

public sealed record CoverageReportSummary(Guid Id, Guid AnalysisId, Guid? RevisionId, Guid? DraftId, string CheckerVersion, DateTimeOffset CreatedAt, CoverageCountsView Counts);

public sealed record CoverageFindingView(string Category, string Code, string Message, Guid? EvidenceId, string? Alias, Guid? ItemId, string? Section, string? Confidence, string? Details);

public sealed record CoverageReportView(
    Guid Id, Guid AnalysisId, Guid? RevisionId, Guid? DraftId, string CheckerVersion, string InputHash, DateTimeOffset CreatedAt, string? CreatedBy,
    CoverageCountsView Counts, IReadOnlyList<CoverageFindingView> Findings, IReadOnlyList<string> Limits);

/// <summary>Endpoint dei report di copertura (contracts/http-api.md §Registro): calcolo su richiesta esplicita, persistito e versionato.</summary>
public static class CoverageEndpoints
{
    public static RouteGroupBuilder MapCoverageEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/analyses/{analysisId:guid}/coverage-reports", Create);
        group.MapGet("/analyses/{analysisId:guid}/coverage-reports", List);
        group.MapGet("/analyses/{analysisId:guid}/coverage-reports/{reportId:guid}", Get);
        return group;
    }

    private static async Task<IResult> Create(Guid projectId, Guid analysisId, CoverageRequest? request, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var createdBy))
        {
            return Results.Unauthorized();
        }

        var run = await db.ProcessingRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct);
        if (run is null)
        {
            return Results.NotFound();
        }

        var selection = await db.AnalysisEvidenceSelections.AsNoTracking().SingleOrDefaultAsync(s => s.RunId == analysisId, ct);
        var revisions = await db.AnalysisRevisions.AsNoTracking().Where(r => r.AnalysisId == analysisId).OrderBy(r => r.Version).ToListAsync(ct);
        var aiRevision = revisions.FirstOrDefault(r => r.Origin == AnalysisRevisionOrigin.Ai);
        if (selection is null || aiRevision is null)
        {
            return EvidenceEndpoints.Problem(StatusCodes.Status409Conflict, "Selezione delle evidenze o revisione AI non disponibili per questo run", "selection_not_available");
        }

        Draft? draft = null;
        if (request?.DraftId is { } draftId)
        {
            draft = await db.Drafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
            if (draft is null)
            {
                return Invalid("draftId", "Bozza inesistente nel progetto.");
            }
        }

        var revisionId = request?.RevisionId ?? draft?.AnalysisRevisionId ?? revisions[^1].Id;
        var revision = revisions.SingleOrDefault(r => r.Id == revisionId);
        if (revision is null)
        {
            return Invalid("revisionId", "La revisione non appartiene a questa analisi.");
        }

        if (draft is not null && draft.AnalysisRevisionId != revision.Id)
        {
            return Invalid("draftId", "La bozza non deriva dalla revisione indicata.");
        }

        // Analisi con revisione 1.1: coverage-2. Le analisi 001 (1.0) restano al checker coverage-v1, invariato.
        if (aiRevision.SchemaVersion == AnalysisRevision.DocumentSchemaVersion)
        {
            return await CreateDocumentAsync(db, run, selection, aiRevision, revision, draft, createdBy, ct);
        }

        var existing = await db.EvidenceCoverageReports.AsNoTracking().SingleOrDefaultAsync(r =>
            r.AnalysisId == analysisId && r.RevisionId == revision.Id && r.DraftId == (draft == null ? null : draft.Id) && r.CheckerVersion == EvidenceCoverage.CheckerVersion, ct);
        if (existing is not null)
        {
            return Results.Ok(await ToViewAsync(db, existing, ct));
        }

        var view = EvidenceSelectionView.Deserialize(selection.View);
        var links = (await db.AnalysisEvidenceLinks.AsNoTracking().Where(l => l.RunId == analysisId).Select(l => new { l.ItemId, l.EvidenceId }).ToListAsync(ct))
            .Select(l => (l.ItemId, l.EvidenceId)).ToList();
        var messageIds = view.Evidences.SelectMany(e => e.Content.Sources.Select(s => s.MessageId)).Distinct().ToList();
        var bodies = await db.ConversationMessages.AsNoTracking().Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Body, ct);
        var findings = EvidenceCoverage.Check(
            view, links, AnalysisContentJson.Deserialize(aiRevision.ContentJson), AnalysisContentJson.Deserialize(revision.ContentJson), draft?.Markdown, bodies);
        var content = new CoverageReportContent(EvidenceCoverage.CheckerVersion, analysisId, aiRevision.Id, revision.Id, draft?.Id, selection.ViewHash, findings, EvidenceCoverage.Limits);
        var report = new EvidenceCoverageReport
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            AnalysisId = analysisId,
            RevisionId = revision.Id,
            DraftId = draft?.Id,
            CheckerVersion = EvidenceCoverage.CheckerVersion,
            InputHash = EvidenceCoverage.InputHash(selection.ViewHash, links, aiRevision.Id, revision.Id, draft?.Id, revision.ContentJson, draft?.Markdown),
            CreatedBy = createdBy,
            CreatedAt = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single(),
            DeterministicErrors = findings.Count(f => f.Category == FindingCategory.DeterministicError),
            SemanticRisks = findings.Count(f => f.Category == FindingCategory.SemanticRisk),
            PmAssessments = findings.Count(f => f.Category == FindingCategory.PmAssessment),
            Report = EvidenceJson.Serialize(content),
        };
        db.EvidenceCoverageReports.Add(report);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Richiesta concorrente con gli stessi input: vale il report già scritto.
            db.ChangeTracker.Clear();
            var raced = await db.EvidenceCoverageReports.AsNoTracking().SingleAsync(r =>
                r.AnalysisId == analysisId && r.RevisionId == revision.Id && r.DraftId == report.DraftId && r.CheckerVersion == EvidenceCoverage.CheckerVersion, ct);
            return Results.Ok(await ToViewAsync(db, raced, ct));
        }

        return Results.Created($"/api/projects/{projectId}/analyses/{analysisId}/coverage-reports/{report.Id}", await ToViewAsync(db, report, ct));
    }

    private static async Task<IResult> List(Guid projectId, Guid analysisId, AppDbContext db, CancellationToken ct)
    {
        if (!await db.ProcessingRuns.AsNoTracking().AnyAsync(r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct))
        {
            return Results.NotFound();
        }

        var reports = await db.EvidenceCoverageReports.AsNoTracking().Where(r => r.AnalysisId == analysisId && r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return Results.Ok(new
        {
            items = reports.Select(r => new CoverageReportSummary(r.Id, r.AnalysisId, r.RevisionId, r.DraftId, r.CheckerVersion, r.CreatedAt,
                new(r.DeterministicErrors, r.SemanticRisks, r.PmAssessments))).ToList(),
        });
    }

    private static async Task<IResult> Get(Guid projectId, Guid analysisId, Guid reportId, AppDbContext db, CancellationToken ct)
    {
        var report = await db.EvidenceCoverageReports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reportId && r.AnalysisId == analysisId && r.ProjectId == projectId, ct);
        return report is null ? Results.NotFound() : Results.Ok(await ToViewAsync(db, report, ct));
    }

    private static async Task<IResult> CreateDocumentAsync(
        AppDbContext db, ProcessingRun run, AnalysisEvidenceSelection selection, AnalysisRevision aiRevision, AnalysisRevision revision, Draft? draft, Guid createdBy, CancellationToken ct)
    {
        var draftId = draft?.Id;

        // La versione si sceglie dal dato discriminante dell'origine della revisione AI (algoritmo del WorkManifest e schema dei WorkOutput), mai dalla sola definizione v5 né dal solo
        // SchemaVersion di AnalysisContent 1.1.0. Origini discordanti sono un conflitto d'integrità: nessun report e mai un ripiego sul checker più vecchio.
        CoverageOrigin origin;
        try
        {
            origin = await DocumentCoverageLoader.OriginAsync(db, run.Id, ct);
        }
        catch (DocumentWorkException ex)
        {
            return EvidenceEndpoints.Problem(StatusCodes.Status409Conflict, ex.Message, ex.Code);
        }

        var checker = origin.CheckerVersion;
        Task<EvidenceCoverageReport?> Find() => db.EvidenceCoverageReports.AsNoTracking().SingleOrDefaultAsync(r =>
            r.AnalysisId == run.Id && r.RevisionId == revision.Id && r.DraftId == draftId && r.CheckerVersion == checker, ct);
        if (await Find() is { } existing)
        {
            return Results.Ok(await ToViewAsync(db, existing, ct));
        }

        var loaded = await DocumentCoverageLoader.LoadAsync(db, run, selection, aiRevision, revision, draft, ct);
        if (DocumentCoverage.AppliesStructuralRepetition(run.WorkflowDefinitionVersion))
        {
            // D3: avviso strutturale non bloccante solo per le revisioni v6 (AI §13.8); v5 e precedenti restano identici.
            loaded = loaded with { Input = loaded.Input with { StructuralRepetition = true } };
        }

        if (origin.Output is not null)
        {
            return await CreateWithGapsAsync(db, run, revision, aiRevision, draftId, createdBy, loaded, origin.Output, origin.Stage, origin.Retrieval, Find, ct);
        }

        var findings = DocumentCoverage.Check(loaded.Input);
        var summary = DocumentCoverage.Summarize(loaded.Input, findings);
        var content = new DocumentCoverageContent(
            DocumentCoverage.CheckerVersion, run.Id, aiRevision.Id, revision.Id, draftId, loaded.Hashes, summary, loaded.Preparations, findings, DocumentCoverage.Limits, loaded.Input.Reconciliation);
        var report = new EvidenceCoverageReport
        {
            Id = Guid.NewGuid(),
            ProjectId = run.ProjectId,
            AnalysisId = run.Id,
            RevisionId = revision.Id,
            DraftId = draftId,
            CheckerVersion = DocumentCoverage.CheckerVersion,
            InputHash = DocumentCoverage.InputHash(loaded.Hashes, loaded.Input.Links),
            CreatedBy = createdBy,
            CreatedAt = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single(),
            DeterministicErrors = summary.DeterministicErrors,
            SemanticRisks = summary.SemanticRisks,
            PmAssessments = summary.PmAssessments,
            Report = EvidenceJson.Serialize(content),
        };
        db.EvidenceCoverageReports.Add(report);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return Results.Ok(await ToViewAsync(db, (await Find())!, ct));
        }

        return Results.Created($"/api/projects/{run.ProjectId}/analyses/{run.Id}/coverage-reports/{report.Id}", await ToViewAsync(db, report, ct));
    }

    /// <summary>coverage-3 (T139): solo revisioni single-call v5. Le valutazioni sono lette dal WorkOutput committato e verificate (hash, forma, riferimenti): un'incoerenza impedisce il report.</summary>
    private static async Task<IResult> CreateWithGapsAsync(
        AppDbContext db, ProcessingRun run, AnalysisRevision revision, AnalysisRevision aiRevision, Guid? draftId, Guid createdBy, LoadedDocumentCoverage loaded, WorkOutput output,
        bool stage, WorkOutput? retrieval, Func<Task<EvidenceCoverageReport?>> find, CancellationToken ct)
    {
        GapAssessmentSet gaps;
        try
        {
            gaps = stage ? GapAssessmentProjection.ProjectStage(output, loaded.Input, retrieval) : GapAssessmentProjection.Project(output, loaded.Input);
        }
        catch (DocumentWorkException ex)
        {
            return EvidenceEndpoints.Problem(StatusCodes.Status409Conflict, ex.Message, ex.Code);
        }

        var input = loaded.Input with { Gaps = gaps.Records, GapStage = stage };
        var findings = DocumentCoverage.Check(input);
        var summary = DocumentCoverage.SummarizeGaps(input, findings);
        var content = new DocumentCoverage3Content(
            DocumentCoverage.CheckerVersionV3, run.Id, aiRevision.Id, revision.Id, draftId, loaded.Hashes, summary, gaps.Hash, gaps.Records, loaded.Preparations, findings,
            gaps.Retrieval is not null ? DocumentCoverage.LimitsRetrieval : stage ? DocumentCoverage.LimitsStage : DocumentCoverage.LimitsV3, input.Reconciliation, RetrievalCoverageSection.From(gaps.Retrieval));
        var report = new EvidenceCoverageReport
        {
            Id = Guid.NewGuid(),
            ProjectId = run.ProjectId,
            AnalysisId = run.Id,
            RevisionId = revision.Id,
            DraftId = draftId,
            CheckerVersion = DocumentCoverage.CheckerVersionV3,
            InputHash = DocumentCoverage.InputHash(loaded.Hashes, loaded.Input.Links, gaps.Hash),
            CreatedBy = createdBy,
            CreatedAt = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single(),
            DeterministicErrors = summary.DeterministicErrors,
            SemanticRisks = summary.SemanticRisks,
            PmAssessments = summary.PmAssessments,
            Report = EvidenceJson.Serialize(content),
        };
        db.EvidenceCoverageReports.Add(report);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return Results.Ok(await ToViewAsync(db, (await find())!, ct));
        }

        return Results.Created($"/api/projects/{run.ProjectId}/analyses/{run.Id}/coverage-reports/{report.Id}", await ToViewAsync(db, report, ct));
    }

    private static async Task<object> ToViewAsync(AppDbContext db, EvidenceCoverageReport report, CancellationToken ct)
    {
        var names = await EvidenceEndpoints.UserNames(db, [report.CreatedBy], ct);
        if (report.CheckerVersion == DocumentCoverage.CheckerVersionV3)
        {
            var v3 = EvidenceJson.Deserialize<DocumentCoverage3Content>(report.Report);
            return new DocumentCoverage3ReportView(
                report.Id, report.AnalysisId, report.RevisionId, v3.AiRevisionId, report.DraftId, report.CheckerVersion, report.InputHash, report.CreatedAt,
                names.GetValueOrDefault(report.CreatedBy), new(report.DeterministicErrors, report.SemanticRisks, report.PmAssessments), v3.Summary, v3.Hashes, v3.GapAssessmentsHash,
                [.. v3.GapAssessments.Select(g => new GapAssessmentView(
                    g.GapEvidenceId, g.GapEvidenceVersionId, g.SourceSnapshotHash, g.Outcome.ToString(), g.Reason.ToString(), g.Rationale, g.SupportingEvidenceIds, g.OpenItemId,
                    g.OpenConflictId, g.OriginBatchId, g.SearchBasis, g.RetrievalComplete, g.AbsenceProven, g.AnsweredWithOpenConflict))],
                v3.Preparations, [.. v3.Findings.Select(DocumentCoverageLoader.ToView)], v3.Limits, v3.Reconciliation, v3.Retrieval);
        }

        if (report.CheckerVersion == DocumentCoverage.CheckerVersion)
        {
            var document = EvidenceJson.Deserialize<DocumentCoverageContent>(report.Report);
            return new DocumentCoverageReportView(
                report.Id, report.AnalysisId, report.RevisionId, report.DraftId, report.CheckerVersion, report.InputHash, report.CreatedAt, names.GetValueOrDefault(report.CreatedBy),
                new(report.DeterministicErrors, report.SemanticRisks, report.PmAssessments), document.Summary, document.Hashes, document.Preparations,
                [.. document.Findings.Select(DocumentCoverageLoader.ToView)], document.Limits, document.Reconciliation);
        }

        var content = EvidenceJson.Deserialize<CoverageReportContent>(report.Report);
        return new CoverageReportView(report.Id, report.AnalysisId, report.RevisionId, report.DraftId, report.CheckerVersion, report.InputHash, report.CreatedAt,
            names.GetValueOrDefault(report.CreatedBy), new(report.DeterministicErrors, report.SemanticRisks, report.PmAssessments),
            [.. content.Findings.Select(f => new CoverageFindingView(f.Category.ToString(), f.Code, f.Message, f.EvidenceId, f.Alias, f.ItemId, f.Section, f.Confidence, f.Details))],
            content.Limits);
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = "coverage_input_invalid" });
}

// ---------------------------------------------------------------- coverage-2 (002 T062)

public sealed record CoverageQualifier(string Type, string Text);

public sealed record CoverageSource(Guid SourceId, Guid? MessageId, Guid? BlockId, int Start, int End, string Quote);

public sealed record DocumentCoverageEvidence(
    Guid EvidenceId, string Alias, EvidenceOriginKind Origin, Guid? MessageId, EvidenceKind Kind, EvidencePolarity Polarity, string Statement,
    IReadOnlyList<CoverageQualifier> Qualifiers, string? Instrument, string? Action, IReadOnlyList<CoverageSource> Sources);

public sealed record DocumentCoveragePortion(Guid PortionId, Guid PreparationId, int Ordinal, PortionStatus Status, string? NoEvidenceReason);

public sealed record DocumentCoverageRelation(Guid FromEvidenceId, Guid ToEvidenceId, EvidenceRelationType Type);

/// <summary>
/// Valutazione `gap-assessment-1` di un Gap, risolta dal WorkOutput single-call v5 della revisione AI originaria (002 T139): alias risolti negli id del catalogo congelato,
/// punto aperto negli id canonici (`canon-ids-1`). Interpretazione AI nello snapshot: non è un supporto di requisito né una conferma del PM, e non viene mai riscritta.
/// I quattro campi finali sono DERIVATI dal server (mai dichiarati dal modello) e presenti solo per le revisioni dello stadio Gap (definizione v6, AI §13.7): assenti (null, non serializzati) nei report v5.
/// </summary>
public sealed record GapAssessmentRecord(
    Guid GapEvidenceId, Guid GapEvidenceVersionId, string SourceSnapshotHash, GapOutcome Outcome, GapReason Reason, string Rationale, IReadOnlyList<Guid> SupportingEvidenceIds,
    Guid? OpenItemId, Guid? OpenConflictId, string OriginBatchId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SearchBasis = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RetrievalComplete = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AbsenceProven = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AnsweredWithOpenConflict = null);

/// <summary>`Gaps` è null per coverage-2 (valutazione Gap «non disponibile», mai «zero Gap») e una lista, anche vuota, solo per coverage-3.</summary>
public sealed record DocumentCoverageInput(
    IReadOnlyList<DocumentCoverageEvidence> Evidence, IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> Links, DocumentAnalysisContent Ai,
    DocumentAnalysisContent Target, string? DraftMarkdown, IReadOnlyDictionary<Guid, string> OriginalTexts, IReadOnlyList<DocumentCoveragePortion> Portions,
    IReadOnlyList<DocumentCoverageRelation> Relations, ReconciliationCoverageSection? Reconciliation = null, IReadOnlyList<GapAssessmentRecord>? Gaps = null, bool GapStage = false, bool StructuralRepetition = false);

public sealed record DocumentCoverageFinding(
    FindingCategory Category, string Code, string Message, Guid? EvidenceId, string? Alias, Guid? ItemId, string? Section, string? Confidence, string? Details, bool Blocking);

public sealed record DocumentCoverageSummary(
    int ExpectedEvidence, int RepresentedEvidence, int PortionsExpected, int PortionsProcessed, int PortionsNoEvidence, int PortionsMissing,
    int DeterministicErrors, int SemanticRisks, int PmAssessments, int BlockingFindings);

/// <summary>
/// Summary di coverage-3: i campi di coverage-2 più i cinque contatori dei Gap (non negativi; expected = assessed + unassessed, assessed = answered + open). Distinti dal supporto item:
/// `RepresentedEvidence` conta ancora solo le evidenze collegate a elementi della revisione AI e i Gap rendicontati non vi si sommano.
/// </summary>
public sealed record DocumentCoverageSummary3(
    int ExpectedEvidence, int RepresentedEvidence, int PortionsExpected, int PortionsProcessed, int PortionsNoEvidence, int PortionsMissing,
    int DeterministicErrors, int SemanticRisks, int PmAssessments, int BlockingFindings,
    int ExpectedGaps, int AssessedGaps, int AnsweredGaps, int OpenGaps, int UnassessedGaps,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AbsenceProvenGaps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AbsenceUnprovenGaps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AnsweredWithOpenConflictGaps = null);

public sealed record DocumentCoverageHashes(string SourceSnapshotHash, string ViewHash, IReadOnlyList<string> ParseHashes, IReadOnlyList<string> ExtractionHashes, string RevisionHash, string? DraftHash);

public sealed record DocumentCoverageContent(
    string CheckerVersion, Guid AnalysisId, Guid AiRevisionId, Guid RevisionId, Guid? DraftId, DocumentCoverageHashes Hashes, DocumentCoverageSummary Summary,
    IReadOnlyList<PreparationCoverageRecord> Preparations, IReadOnlyList<DocumentCoverageFinding> Findings, IReadOnlyList<string> Limits, ReconciliationCoverageSection? Reconciliation = null);

/// <summary>
/// Sintesi del registro del recupero nelle sole revisioni v7 con sorgente `retrieval` (003 T032, additiva e opzionale: assente in v5/v6, che restano byte-identici). Solo conteggi e dichiarazioni
/// di metodo: nessun identificativo tecnico e nessuna affermazione di completezza o di assenza (<c>retrievalComplete</c> non è prova di completezza semantica).
/// </summary>
public sealed record RetrievalCoverageSection(
    string SearchBasis, bool RetrievalComplete, bool Degraded, string PolicyVersion, int Gaps, int Candidates, int InPackage, int ExcludedForBudget, int NoEvidenceForBlock,
    int AlwaysIncluded, int AlwaysIncludedInPackage)
{
    public static RetrievalCoverageSection? From(RetrievalRegistry? registry)
    {
        if (registry is null)
        {
            return null;
        }

        var candidates = registry.Gaps.SelectMany(g => g.Candidates).ToList();
        return new(
            registry.SearchBasis, registry.RetrievalComplete, registry.Degraded, registry.PolicyVersion, registry.Gaps.Count,
            candidates.Select(c => c.BlockId).Distinct().Count(), registry.PackageEvidence.Count,
            registry.Excluded.Count(e => e.Reason == CandidateRegistry.ReasonBudget),
            candidates.Where(c => c.ExcludedReason == CandidateRegistry.ReasonNoEvidence).Select(c => c.BlockId).Distinct().Count(),
            registry.AlwaysIncluded.Count, registry.AlwaysIncluded.Count(a => a.InPackage));
    }
}

/// <summary>Contenuto persistito di coverage-3 (JSONB esistente): come coverage-2 più le valutazioni Gap della revisione AI originaria e il loro hash.</summary>
public sealed record DocumentCoverage3Content(
    string CheckerVersion, Guid AnalysisId, Guid AiRevisionId, Guid RevisionId, Guid? DraftId, DocumentCoverageHashes Hashes, DocumentCoverageSummary3 Summary,
    string GapAssessmentsHash, IReadOnlyList<GapAssessmentRecord> GapAssessments, IReadOnlyList<PreparationCoverageRecord> Preparations,
    IReadOnlyList<DocumentCoverageFinding> Findings, IReadOnlyList<string> Limits, ReconciliationCoverageSection? Reconciliation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RetrievalCoverageSection? Retrieval = null);

/// <summary>Elemento di AnalysisContent 1.1 con la sezione in cui si trova.</summary>
public sealed record DocumentSectionItem(string Section, DocumentAnalysisItem Item, Guid? ConflictId = null);

/// <summary>
/// Verifica di copertura `coverage-2` (002 T062; contracts/ai-workflow.md §errori): stessa disciplina di coverage-v1 sul contenuto 1.1.
/// Errori deterministici (ID, fonti, range, porzioni, relazioni note), rischi semantici stimati (euristiche lessicali con Confidence) e
/// giudizi PM restano categorie separate; un rischio non è mai una diagnosi certa e non esiste un booleano «complete» autorevole: la copertura
/// per ID al 100% non certifica la correttezza semantica. Non modifica nulla e non invoca modelli.
/// </summary>
public static class DocumentCoverage
{
    public const string CheckerVersion = "coverage-2";

    /// <summary>Verifica con le valutazioni dei Gap (002 T139): solo revisioni single-call v5 (`analysis-single-v3`, WorkOutput `document-analysis-alias-2`).</summary>
    public const string CheckerVersionV3 = "coverage-3";

    /// <summary>
    /// Summary di coverage-3: i contatori coverage-2 invariati più i cinque dei Gap. Attesi = evidenze Gap della selezione; valutati = Gap con un record (un record su un'evidenza non Gap è ignorato);
    /// expected = assessed + unassessed e assessed = answered + open per costruzione. Mai sommati alle evidenze rappresentate da elementi.
    /// </summary>
    public static DocumentCoverageSummary3 SummarizeGaps(DocumentCoverageInput input, IReadOnlyList<DocumentCoverageFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Gaps is null)
        {
            throw new ArgumentException("coverage-3 richiede la lista delle valutazioni Gap, anche vuota.", nameof(input));
        }

        var b = Summarize(input, findings);
        var expected = input.Evidence.Where(e => e.Kind == EvidenceKind.Gap).Select(e => e.EvidenceId).ToHashSet();
        var assessed = input.Gaps.Where(g => expected.Contains(g.GapEvidenceId)).GroupBy(g => g.GapEvidenceId).Select(g => g.First()).ToList();
        var answered = assessed.Count(g => g.Outcome == GapOutcome.AnsweredInSnapshot);
        var summary = new DocumentCoverageSummary3(
            b.ExpectedEvidence, b.RepresentedEvidence, b.PortionsExpected, b.PortionsProcessed, b.PortionsNoEvidence, b.PortionsMissing, b.DeterministicErrors, b.SemanticRisks,
            b.PmAssessments, b.BlockingFindings, expected.Count, assessed.Count, answered, assessed.Count - answered, expected.Count - assessed.Count);
        if (!input.GapStage)
        {
            return summary;
        }

        // Revisioni dello stadio Gap (v6): campi additivi calcolati dal server. absenceProven + absenceUnproven = Gap NoAnswer; la guardia answeredWithOpenConflict non cambia alcun esito.
        var noAnswer = assessed.Where(g => g.Reason == GapReason.NoAnswer).ToList();
        return summary with
        {
            AbsenceProvenGaps = noAnswer.Count(g => g.AbsenceProven == true),
            AbsenceUnprovenGaps = noAnswer.Count(g => g.AbsenceProven != true),
            AnsweredWithOpenConflictGaps = assessed.Count(g => g.AnsweredWithOpenConflict == true),
        };
    }

    /// <summary>Identità input di coverage-3: come coverage-2, con il suo checker e con l'hash delle valutazioni Gap (§ai-workflow 13.5); mai uguale a quella di coverage-2.</summary>
    public static string InputHash(DocumentCoverageHashes hashes, IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> links, string gapAssessmentsHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(gapAssessmentsHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(HashText(CheckerVersionV3, hashes, links) + "|gapAssessments:" + gapAssessmentsHash)));
    }

    public static IReadOnlyList<string> Limits { get; } =
    [
        .. EvidenceCoverage.Limits,
        "La copertura per porzioni misura il trattamento strutturale dell'estrazione: una porzione elaborata senza evidenze dichiara un motivo ma non prova che non vi fosse nulla da estrarre.",
        "Gli ID collegati a un elemento non dimostrano che il testo sia fedele alla fonte: anche con tutte le evidenze rappresentate un gold semanticamente errato è segnalato solo come rischio stimato.",
    ];

    public static IReadOnlyList<string> LimitsV3 { get; } =
    [
        .. Limits,
        "Le valutazioni dei Gap sono interpretazioni AI nello snapshot congelato: il server verifica forma, riferimenti e hash, non la pertinenza; non sono supporti di requisito né conferme del PM.",
    ];

    /// <summary>Limiti delle revisioni dello stadio Gap (v6): quelli di coverage-3 più la natura dell'assenza provata e della guardia sul conflitto aperto.</summary>
    public static IReadOnlyList<string> LimitsStage { get; } =
    [
        .. LimitsV3,
        "«Assenza provata» significa che il ledger completo dello snapshot (searchBasis ledger_exhaustive, retrievalComplete true) e l'estrazione completa delle porzioni non contengono alcuna evidenza pertinente secondo la valutazione AI: non prova l'assenza dell'informazione nel mondo né la correttezza della valutazione.",
        "La segnalazione «risposta documentale con conflitto aperto» è una guardia non bloccante calcolata dai riferimenti strutturali (conflitti Open e relazioni note): non cambia l'esito della valutazione e non arbitra fra le fonti.",
    ];

    /// <summary>
    /// Limiti delle revisioni con sorgente `retrieval` (v7, 003): quelli di coverage-3 e dello stadio, con la natura del recupero. Nessuna «assenza provata» (il recupero non esamina l'intero ledger) e
    /// nessuna attestazione di completezza dei conflitti.
    /// </summary>
    public static IReadOnlyList<string> LimitsRetrieval { get; } =
    [
        .. LimitsV3,
        "La valutazione dei Gap si basa su un sottoinsieme di passaggi recuperati per pertinenza (searchBasis retrieval), non sull'intero ledger: un Gap senza risposta è sempre «assenza non provata», mai provata.",
        "retrievalComplete indica soltanto che la procedura di recupero dichiarata è stata eseguita per intero: non è una prova di completezza semantica né di assenza dell'informazione nelle fonti.",
        "La segnalazione «risposta documentale con conflitto aperto» è una guardia non bloccante calcolata dai riferimenti strutturali (conflitti Open e relazioni note): non cambia l'esito della valutazione, non arbitra fra le fonti e non attesta la completezza dei conflitti.",
    ];

    public static IReadOnlyList<DocumentSectionItem> Items(DocumentAnalysisContent content)
    {
        var items = new List<DocumentSectionItem>();
        void Add(string section, IEnumerable<DocumentAnalysisItem> list) => items.AddRange(list.Select(i => new DocumentSectionItem(section, i)));
        Add("goals", content.Goals);
        Add("contextAndScope.overview", content.ContextAndScope.Overview);
        Add("contextAndScope.included", content.ContextAndScope.Included);
        Add("contextAndScope.excluded", content.ContextAndScope.Excluded);
        Add("requirements", content.Requirements);
        Add("operationalFlows", content.OperationalFlows);
        Add("constraints", content.Constraints);
        Add("dependencies", content.Dependencies);
        Add("assumptions", content.Assumptions);
        Add("openQuestions", content.OpenQuestions);
        foreach (var conflict in content.Conflicts)
        {
            items.AddRange(conflict.Alternatives.Select(a => new DocumentSectionItem("conflicts", a, conflict.Id)));
        }

        return items;
    }

    private static DocumentCoverageFinding Doc(
        FindingCategory category, string code, string message, Guid? evidenceId = null, string? alias = null, Guid? itemId = null, string? section = null,
        string? confidence = null, string? details = null) =>
        new(category, code, message, evidenceId, alias, itemId, section, confidence, details, category == FindingCategory.DeterministicError);

    private static Dictionary<Guid, DocumentSectionItem> ById(IEnumerable<DocumentSectionItem> items) =>
        items.GroupBy(i => i.Item.Id).ToDictionary(g => g.Key, g => g.First());

    public static IReadOnlyList<DocumentCoverageFinding> Check(DocumentCoverageInput input)
    {
        var findings = new List<DocumentCoverageFinding>();
        var aiItems = ById(Items(input.Ai));
        var targetItems = ById(Items(input.Target));
        var targetSources = input.Target.Sources.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var byEvidence = input.Links.GroupBy(l => l.EvidenceId).ToDictionary(g => g.Key, g => g.Select(l => l.ItemId).Distinct().ToList());
        // coverage-3 (T139): un Gap con valutazione AI valida è rendicontato anche senza un elemento sostantivo; la regola resta per i Gap senza record e per ogni altro tipo.
        var reportedGaps = (input.Gaps ?? []).Select(g => g.GapEvidenceId).ToHashSet();

        foreach (var e in input.Evidence)
        {
            foreach (var span in e.Sources)
            {
                var key = span.BlockId ?? span.MessageId;
                if (key is null || !input.OriginalTexts.TryGetValue(key.Value, out var text) || span.Start < 0 || span.End > text.Length || span.Start >= span.End
                    || !string.Equals(text[span.Start..span.End], span.Quote, StringComparison.Ordinal))
                {
                    findings.Add(Doc(FindingCategory.DeterministicError, "source_range_invalid",
                        $"{e.Alias}: la citazione [{span.Start},{span.End}) non corrisponde al testo originale della fonte.", e.EvidenceId, e.Alias));
                }
            }

            var linked = byEvidence.GetValueOrDefault(e.EvidenceId) ?? [];
            var inAi = linked.Where(aiItems.ContainsKey).Select(id => aiItems[id]).ToList();
            var inTarget = linked.Where(targetItems.ContainsKey).Select(id => targetItems[id]).ToList();
            if (inAi.Count == 0)
            {
                if (!(e.Kind == EvidenceKind.Gap && reportedGaps.Contains(e.EvidenceId)))
                {
                    findings.Add(Doc(FindingCategory.DeterministicError, "evidence_unrepresented",
                        $"{e.Alias} ({e.Kind}) non è collegata a nessun elemento dell'analisi: «{EvidenceCoverage.Short(e.Statement)}».", e.EvidenceId, e.Alias));
                }

                continue;
            }

            foreach (var item in inTarget.Where(i => i.Item.EvidenceRefs.Count > 0 && !i.Item.EvidenceRefs.Any(r => CitesEvidence(e, r, targetSources))))
            {
                findings.Add(Doc(FindingCategory.DeterministicError, "evidence_source_mismatch",
                    $"L'elemento collegato a {e.Alias} non cita nessuna fonte di quell'evidenza.", e.EvidenceId, e.Alias, item.Item.Id, item.Section));
            }

            // La rimozione della domanda aperta indicata dalla valutazione AI ha il proprio giudizio PM (GapReferenceFindings): qui non si duplica.
            var openItems = (input.Gaps ?? []).Where(g => g.GapEvidenceId == e.EvidenceId && g.OpenItemId is not null).Select(g => g.OpenItemId!.Value).ToHashSet();
            foreach (var item in inAi.Where(i => !targetItems.ContainsKey(i.Item.Id) && !openItems.Contains(i.Item.Id)))
            {
                findings.Add(Doc(FindingCategory.PmAssessment, "item_removed_by_pm",
                    $"L'elemento che rappresentava {e.Alias} non è più nella revisione verificata: rimosso dal PM.", e.EvidenceId, e.Alias, item.Item.Id, item.Section));
            }

            foreach (var item in inTarget.Where(i => aiItems.TryGetValue(i.Item.Id, out var original) && !string.Equals(original.Item.Text, i.Item.Text, StringComparison.Ordinal)))
            {
                findings.Add(Doc(FindingCategory.PmAssessment, "item_rewritten_by_pm", $"L'elemento che rappresentava {e.Alias} è stato riscritto dal PM.",
                    e.EvidenceId, e.Alias, item.Item.Id, item.Section, null,
                    $"prima: «{EvidenceCoverage.Short(aiItems[item.Item.Id].Item.Text)}» · dopo: «{EvidenceCoverage.Short(item.Item.Text)}»"));
            }

            if (inTarget.Count == 0)
            {
                continue;
            }

            var texts = inTarget.Select(i => i.Item.Text).ToList();
            foreach (var q in e.Qualifiers)
            {
                var missing = EvidenceCoverage.Numbers(q.Text).Where(n => !texts.Any(t => EvidenceCoverage.Numbers(t).Contains(n))).ToList();
                if (missing.Count > 0)
                {
                    findings.Add(Doc(FindingCategory.SemanticRisk, "qualifier_changed",
                        $"{e.Alias}: il qualificatore «{q.Text}» ({q.Type}) non ritrova {string.Join(", ", missing)} negli elementi collegati.",
                        e.EvidenceId, e.Alias, inTarget[0].Item.Id, inTarget[0].Section, "medium"));
                }
            }

            var negative = e.Polarity is EvidencePolarity.Negated or EvidencePolarity.Excluded or EvidencePolarity.Prohibited;
            if (negative && !inTarget.Any(i => i.Section == "contextAndScope.excluded" || EvidenceCoverage.Negation().IsMatch(i.Item.Text)))
            {
                findings.Add(Doc(FindingCategory.SemanticRisk, "negation_inverted",
                    $"{e.Alias} è {e.Polarity} ma nessun elemento collegato è un'esclusione o contiene una negazione.", e.EvidenceId, e.Alias,
                    inTarget[0].Item.Id, inTarget[0].Section, "medium"));
            }
            else if (e.Polarity == EvidencePolarity.Affirmed && inTarget.All(i => i.Section == "contextAndScope.excluded"))
            {
                findings.Add(Doc(FindingCategory.SemanticRisk, "negation_inverted",
                    $"{e.Alias} è un'affermazione ma è rappresentata solo fra le esclusioni.", e.EvidenceId, e.Alias, inTarget[0].Item.Id, inTarget[0].Section, "low"));
            }

            if (e.Instrument is { } instrument && e.Action is { } action)
            {
                var tool = EvidenceCoverage.Words(instrument);
                var act = EvidenceCoverage.Words(action);
                if (tool.Count > 0 && act.Count > 0 && !inTarget.Any(i => EvidenceCoverage.Contains(i.Item.Text, tool) && EvidenceCoverage.Contains(i.Item.Text, act)))
                {
                    findings.Add(Doc(FindingCategory.SemanticRisk, "instrument_detached",
                        $"{e.Alias}: lo strumento «{instrument}» e l'azione «{action}» non compaiono insieme in nessun elemento collegato.",
                        e.EvidenceId, e.Alias, inTarget[0].Item.Id, inTarget[0].Section, "medium"));
                }
            }
        }

        foreach (var item in targetItems.Values.Where(i => i.Item.EvidenceRefs.Count == 0 && i.Item.Classification is DocumentAnalysisClassification.Declared or DocumentAnalysisClassification.Documented))
        {
            findings.Add(Doc(FindingCategory.DeterministicError, "item_without_fragment",
                $"L'elemento {item.Item.Classification} in {item.Section} non cita nessun frammento: «{EvidenceCoverage.Short(item.Item.Text)}».", null, null, item.Item.Id, item.Section));
        }

        foreach (var item in targetItems.Values)
        {
            var unresolved = item.Item.EvidenceRefs.Where(r => !targetSources.ContainsKey(r)).ToList();
            if (unresolved.Count > 0)
            {
                findings.Add(Doc(FindingCategory.DeterministicError, "item_source_unresolved",
                    $"L'elemento in {item.Section} cita fonti che non appartengono alla revisione.", null, null, item.Item.Id, item.Section, null, string.Join(",", unresolved)));
            }
        }

        foreach (var relation in input.Relations.Where(r => r.Type == EvidenceRelationType.Contradicts))
        {
            var from = ItemsOf(relation.FromEvidenceId, input, targetItems);
            var to = ItemsOf(relation.ToEvidenceId, input, targetItems);
            if (from.Count == 0 || to.Count == 0)
            {
                continue;
            }

            var shared = input.Target.Conflicts.Any(c => c.Alternatives.Any(a => from.Contains(a.Id)) && c.Alternatives.Any(a => to.Contains(a.Id)));
            if (!shared)
            {
                findings.Add(Doc(FindingCategory.DeterministicError, "known_relation_unrepresented",
                    "Una contraddizione nota fra due evidenze non è rappresentata in un conflitto con entrambe le posizioni.", relation.FromEvidenceId, null, null, null, null,
                    $"{relation.FromEvidenceId} -> {relation.ToEvidenceId}"));
            }
        }

        findings.AddRange(PortionFindings(input.Portions));
        findings.AddRange(ReconciliationFindings(input.Reconciliation));
        findings.AddRange(GapReferenceFindings(input, targetItems));
        if (input.StructuralRepetition)
        {
            findings.AddRange(StructuralRepetitionFindings(input, targetItems));
        }

        foreach (var conflict in input.Target.Conflicts.Where(c => c.Status == AnalysisConflictStatus.Resolved))
        {
            if (input.Ai.Conflicts.Any(c => c.Id == conflict.Id && c.Status == AnalysisConflictStatus.Open))
            {
                findings.Add(Doc(FindingCategory.PmAssessment, "conflict_resolved_by_pm",
                    $"Il PM ha risolto il conflitto «{EvidenceCoverage.Short(conflict.Description)}»: decisione da confermare.", null, null, null, "conflicts"));
            }
        }

        if (input.DraftMarkdown is not null)
        {
            var cited = EvidenceCoverage.SourceMarker().Matches(input.DraftMarkdown).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var document = EvidenceCoverage.Normalize(input.DraftMarkdown);
            foreach (var item in targetItems.Values)
            {
                if (item.Item.EvidenceRefs.Count > 0 && !item.Item.EvidenceRefs.Any(r => cited.Contains(r.ToString())))
                {
                    findings.Add(Doc(FindingCategory.DeterministicError, "revision_source_not_cited",
                        $"Nessuna fonte dell'elemento confermato in {item.Section} è citata nella bozza: «{EvidenceCoverage.Short(item.Item.Text)}».", null, null, item.Item.Id, item.Section));
                }

                var words = EvidenceCoverage.Words(item.Item.Text);
                if (words.Count >= 3)
                {
                    var present = words.Count(w => document.Contains(w, StringComparison.Ordinal));
                    var ratio = (double)present / words.Count;
                    if (ratio < 0.5)
                    {
                        findings.Add(Doc(FindingCategory.SemanticRisk, "revision_item_missing_in_document",
                            $"L'elemento confermato in {item.Section} sembra assente dalla bozza ({present}/{words.Count} parole significative): «{EvidenceCoverage.Short(item.Item.Text)}».",
                            null, null, item.Item.Id, item.Section, ratio < 0.3 ? "medium" : "low"));
                    }
                }
            }
        }

        return findings;
    }

    /// <summary>Porzioni non elaborate (Pending o Failed): errore deterministico bloccante. Una porzione senza evidenze ma con motivo è un esito, non un errore.</summary>
    public static IReadOnlyList<DocumentCoverageFinding> PortionFindings(IReadOnlyList<DocumentCoveragePortion> portions) =>
        [.. portions.Where(p => p.Status != PortionStatus.Completed).Select(portion => Doc(FindingCategory.DeterministicError, "portion_not_processed",
            $"La porzione {portion.Ordinal} non è stata elaborata dall'estrazione ({portion.Status}).", null, null, null, null, null, portion.PortionId.ToString()))];

    /// <summary>
    /// Residui della riconciliazione gerarchica proiettati dal run (T081): candidati non terminali e patch Stale/Invalid/NotProcessed sono errori deterministici
    /// bloccanti con ID e motivo. Un candidato Open e un conflitto aperto non sono errori: sono dubbi preservati, mostrati nella sezione e rivisti dal PM.
    /// </summary>
    public static IReadOnlyList<DocumentCoverageFinding> ReconciliationFindings(ReconciliationCoverageSection? section)
    {
        if (section is null)
        {
            return [];
        }

        return
        [
            .. section.Candidates.Where(c => !c.Terminal).Select(c => Doc(FindingCategory.DeterministicError, "candidate_not_terminal",
                $"Il candidato {c.CandidateId} non è terminale ({c.State}{(c.Reason is null ? string.Empty : ": " + c.Reason)}).", details: c.CandidateId)),
            .. section.Patches.Where(p => p.Blocking).Select(p => Doc(FindingCategory.DeterministicError, "patch_blocking",
                $"La patch {p.PatchId} non è applicabile ({p.State}{(p.Reason is null ? string.Empty : ": " + p.Reason)}).", details: p.PatchId)),
        ];
    }

    /// <summary>
    /// Un edit del PM che rimuove il punto aperto (domanda o conflitto) indicato da una valutazione AI è un giudizio PM distinto: la valutazione della revisione AI originaria
    /// non è mai riscritta né rivalutata. Un conflitto ancora presente ma risolto è già «conflict_resolved_by_pm».
    /// </summary>
    private static IEnumerable<DocumentCoverageFinding> GapReferenceFindings(DocumentCoverageInput input, Dictionary<Guid, DocumentSectionItem> targetItems)
    {
        foreach (var record in input.Gaps ?? [])
        {
            var alias = input.Evidence.FirstOrDefault(e => e.EvidenceId == record.GapEvidenceId)?.Alias;
            if (record.OpenItemId is { } item && !targetItems.ContainsKey(item))
            {
                yield return Doc(FindingCategory.PmAssessment, "gap_open_reference_removed_by_pm",
                    $"{alias}: la domanda aperta indicata dalla valutazione AI non è più nella revisione verificata: rimossa dal PM.", record.GapEvidenceId, alias, item, "openQuestions", null, item.ToString());
            }

            if (record.OpenConflictId is { } conflict && input.Target.Conflicts.All(c => c.Id != conflict))
            {
                yield return Doc(FindingCategory.PmAssessment, "gap_open_reference_removed_by_pm",
                    $"{alias}: il conflitto aperto indicato dalla valutazione AI non è più nella revisione verificata: rimosso dal PM.", record.GapEvidenceId, alias, null, "conflicts", null, conflict.ToString());
            }
        }
    }

    /// <summary>D3 (AI §13.8): l'avviso di ripetizione strutturale si applica solo alle revisioni della definizione v6; i report v5 e precedenti restano identici.</summary>
    public static bool AppliesStructuralRepetition(string? workflowDefinition) =>
        workflowDefinition is DocumentAnalysisWorkflow.DefinitionVersionV6 or DocumentAnalysisWorkflow.DefinitionVersionV7 or DocumentAnalysisWorkflow.DefinitionVersionV8
            or DocumentAnalysisWorkflow.DefinitionVersionV9 or DocumentAnalysisWorkflow.DefinitionVersionV10;

    /// <summary>
    /// D3 — avviso STRUTTURALE (semantic risk, confidenza media, mai bloccante) per un item ordinario che ripete l'alternativa di un conflitto: stesso insieme NON vuoto di evidenze collegate, stessa
    /// classificazione, stesso multiinsieme di token numerici e stesso numero di marcatori di negazione (controllo di polarità: «entro 30» ≠ «non entro 30»). Nessun testo è modificato, rifiutato o
    /// rimosso e non c'è alcuna deduplicazione semantica/probabilistica: il PM decide. La Coherence esatta del composer resta invariata.
    /// </summary>
    private static IEnumerable<DocumentCoverageFinding> StructuralRepetitionFindings(DocumentCoverageInput input, Dictionary<Guid, DocumentSectionItem> targetItems)
    {
        HashSet<Guid> EvidenceOf(Guid itemId) => input.Links.Where(l => l.ItemId == itemId).Select(l => l.EvidenceId).ToHashSet();

        var alternatives = targetItems.Values.Where(i => i.ConflictId is not null).ToList();
        foreach (var ordinary in targetItems.Values.Where(i => i.ConflictId is null))
        {
            var evidence = EvidenceOf(ordinary.Item.Id);
            if (evidence.Count == 0)
            {
                continue;
            }

            var numbers = EvidenceCoverage.NumberTokens(ordinary.Item.Text);
            var negations = EvidenceCoverage.Negation().Matches(ordinary.Item.Text).Count;
            foreach (var alternative in alternatives.Where(a => a.Item.Classification == ordinary.Item.Classification))
            {
                if (EvidenceCoverage.NumberTokens(alternative.Item.Text).SequenceEqual(numbers, StringComparer.Ordinal)
                    && EvidenceCoverage.Negation().Matches(alternative.Item.Text).Count == negations
                    && evidence.SetEquals(EvidenceOf(alternative.Item.Id)))
                {
                    var first = evidence.Order().First();
                    var alias = input.Evidence.FirstOrDefault(e => e.EvidenceId == first)?.Alias;
                    yield return Doc(FindingCategory.SemanticRisk, "item_repeats_conflict_alternative",
                        $"L'elemento in {ordinary.Section} ripete un'alternativa del conflitto (stesse evidenze, classificazione e numeri): «{EvidenceCoverage.Short(ordinary.Item.Text)}». Avviso strutturale, nulla è stato modificato.",
                        first, alias, ordinary.Item.Id, ordinary.Section, "medium", alternative.Item.Id.ToString());
                }
            }
        }
    }

    private static HashSet<Guid> ItemsOf(Guid evidenceId, DocumentCoverageInput input, Dictionary<Guid, DocumentSectionItem> targetItems) =>
        input.Links.Where(l => l.EvidenceId == evidenceId && targetItems.ContainsKey(l.ItemId)).Select(l => l.ItemId).ToHashSet();

    /// <summary>Una fonte della revisione appartiene all'evidenza: per id di fonte (documento) oppure per messaggio di origine.</summary>
    private static bool CitesEvidence(DocumentCoverageEvidence e, Guid sourceId, IReadOnlyDictionary<Guid, DocumentAnalysisSource> sources) =>
        e.Sources.Any(s => s.SourceId == sourceId)
        || (e.MessageId is { } message && sources.TryGetValue(sourceId, out var source) && source.MessageId == message);

    public static DocumentCoverageSummary Summarize(DocumentCoverageInput input, IReadOnlyList<DocumentCoverageFinding> findings)
    {
        var aiItems = Items(input.Ai).Select(i => i.Item.Id).ToHashSet();
        var represented = input.Evidence.Count(e => input.Links.Any(l => l.EvidenceId == e.EvidenceId && aiItems.Contains(l.ItemId)));
        var completed = input.Portions.Where(p => p.Status == PortionStatus.Completed).ToList();
        return new(
            input.Evidence.Count, represented, input.Portions.Count, completed.Count, completed.Count(p => p.NoEvidenceReason is not null),
            input.Portions.Count - completed.Count,
            findings.Count(f => f.Category == FindingCategory.DeterministicError), findings.Count(f => f.Category == FindingCategory.SemanticRisk),
            findings.Count(f => f.Category == FindingCategory.PmAssessment), findings.Count(f => f.Blocking));
    }

    public static string InputHash(DocumentCoverageHashes hashes, IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> links) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(HashText(CheckerVersion, hashes, links))));

    private static string HashText(string checker, DocumentCoverageHashes hashes, IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> links) =>
        new StringBuilder()
            .Append(checker).Append('|').Append(hashes.SourceSnapshotHash).Append('|').Append(hashes.ViewHash)
            .Append('|').Append(string.Join(",", hashes.ParseHashes.Order(StringComparer.Ordinal)))
            .Append('|').Append(string.Join(",", hashes.ExtractionHashes.Order(StringComparer.Ordinal)))
            .Append('|').Append(hashes.RevisionHash).Append('|').Append(hashes.DraftHash)
            .Append('|').Append(string.Join(",", links.OrderBy(l => l.ItemId).ThenBy(l => l.EvidenceId).Select(l => $"{l.ItemId}:{l.EvidenceId}")))
            .ToString();
}

// ---- viste e caricamento di coverage-2 (002 T062)

public sealed record PortionCountsView(int Expected, int Processed, int NoEvidence, int Missing, IReadOnlyList<Guid> MissingPortionIds);

public sealed record PreparationCoverageHashes(string? ManifestHash, string? ParseSha256, Guid? EvidenceVersionId, string? ExtractionHash);

public sealed record PreparationCoverageRecord(
    Guid PreparationId, Guid DocumentId, Guid DocumentVersionId, int Generation, string Status, PreparationCoverageHashes Hashes, PortionCountsView Portions);

public sealed record DocumentCoverageFindingView(
    string Category, string Code, string Message, Guid? EvidenceId, string? Alias, Guid? ItemId, string? Section, string? Confidence, string? Details, bool Blocking);

public sealed record DocumentCoverageReportView(
    Guid Id, Guid AnalysisId, Guid? RevisionId, Guid? DraftId, string CheckerVersion, string InputHash, DateTimeOffset CreatedAt, string? CreatedBy,
    CoverageCountsView Counts, DocumentCoverageSummary Summary, DocumentCoverageHashes Hashes, IReadOnlyList<PreparationCoverageRecord> Preparations,
    IReadOnlyList<DocumentCoverageFindingView> Findings, IReadOnlyList<string> Limits, ReconciliationCoverageSection? Reconciliation = null);

public sealed record GapAssessmentView(
    Guid GapEvidenceId, Guid GapEvidenceVersionId, string SourceSnapshotHash, string Outcome, string Reason, string Rationale, IReadOnlyList<Guid> SupportingEvidenceIds,
    Guid? OpenItemId, Guid? OpenConflictId, string OriginBatchId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SearchBasis = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RetrievalComplete = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AbsenceProven = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AnsweredWithOpenConflict = null);

/// <summary>
/// Vista di coverage-3 (T139): i campi di coverage-2 più `aiRevisionId` (la revisione AI originaria cui appartengono le valutazioni, distinta da `revisionId` corrente),
/// `gapAssessments` e `gapAssessmentsHash`. Un client discrimina dalla versione del report, mai dalla presenza dei campi.
/// </summary>
public sealed record DocumentCoverage3ReportView(
    Guid Id, Guid AnalysisId, Guid? RevisionId, Guid AiRevisionId, Guid? DraftId, string CheckerVersion, string InputHash, DateTimeOffset CreatedAt, string? CreatedBy,
    CoverageCountsView Counts, DocumentCoverageSummary3 Summary, DocumentCoverageHashes Hashes, string GapAssessmentsHash, IReadOnlyList<GapAssessmentView> GapAssessments,
    IReadOnlyList<PreparationCoverageRecord> Preparations, IReadOnlyList<DocumentCoverageFindingView> Findings, IReadOnlyList<string> Limits, ReconciliationCoverageSection? Reconciliation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RetrievalCoverageSection? Retrieval = null);

/// <summary>GET di preparazione/coverage: stato strutturale della preparazione prima dell'analisi. Non avvia lavori e non persiste report.</summary>
public sealed record PreparationCoverageView(
    string CheckerVersion, Guid PreparationId, Guid DocumentId, Guid DocumentVersionId, int Generation, string Status, PreparationCoverageHashes Hashes,
    PortionCountsView Portions, IReadOnlyList<DocumentCoverageFindingView> Findings, IReadOnlyList<string> Limits);

// ---- riconciliazione gerarchica nel report (002 T081): lettura delle proiezioni T122, nessun nuovo calcolo e nessun booleano di completezza

public sealed record ReconciliationLevelRecord(string View, int Level, int Nodes, int Pages);

public sealed record ReconciliationCandidateRecord(string CandidateId, string Kind, string ProposedType, string State, string? Reason, IReadOnlyList<string> ChildCandidateIds, bool Terminal);

public sealed record ReconciliationPatchRecord(string PatchId, string TargetKind, string TargetId, string State, string? Reason, bool Blocking);

public sealed record ReconciliationFrontierRecord(string CandidateId, string Reason);

public sealed record ReconciliationAlternativeRecord(Guid ItemId, string Text, string Classification, IReadOnlyList<Guid> EvidenceRefs);

/// <summary>Punto aperto della revisione verificata: conflitto Open con tutte le alternative integrali, oppure domanda aperta (senza alternative).</summary>
public sealed record ReconciliationOpenPoint(string Kind, Guid Id, string Description, IReadOnlyList<ReconciliationAlternativeRecord> Alternatives);

/// <summary>`Projected` è falso se per il run non esistono proiezioni gerarchiche (analisi non gerarchica): non significa «completo», solo «nulla da elencare».</summary>
public sealed record ReconciliationCoverageSection(
    bool Projected, IReadOnlyList<ReconciliationLevelRecord> Levels, IReadOnlyList<ReconciliationCandidateRecord> Candidates, IReadOnlyList<ReconciliationPatchRecord> Patches,
    IReadOnlyList<ReconciliationFrontierRecord> Frontier, IReadOnlyList<ReconciliationOpenPoint> OpenPoints);

internal sealed record LoadedDocumentCoverage(DocumentCoverageInput Input, DocumentCoverageHashes Hashes, IReadOnlyList<PreparationCoverageRecord> Preparations);

/// <summary>Versione del report scelta dall'origine della revisione AI; <see cref="Output"/> è il WorkOutput single-call v5 solo per coverage-3.</summary>
internal sealed record CoverageOrigin(string CheckerVersion, WorkOutput? Output, bool Stage = false, WorkOutput? Retrieval = null);

internal sealed record GapAssessmentSet(IReadOnlyList<GapAssessmentRecord> Records, string Hash, RetrievalRegistry? Retrieval = null);

/// <summary>
/// Proiezione read-only delle valutazioni `gap-assessment-1` dal WorkOutput single-call v5 committato (T139). Verifica hash del payload, del catalogo, del DTO e delle valutazioni, forma e riferimenti
/// (alias nel catalogo congelato, Gap come tipo Gap, supporti non Gap, punti aperti presenti nel DTO e nella revisione AI con gli id canonici `canon-ids-1`). Nessuna valutazione è sintetizzata
/// dal server e nessuna è corretta: un'incoerenza è <c>work_output_invalid</c> e impedisce il report, mai una lista vuota apparentemente valida.
/// </summary>
/// <summary>
/// Campi derivati dal server per le valutazioni dello stadio Gap (AI §13.7), mai dichiarati dal modello. <c>absenceProven</c>: <c>NoAnswer</c> con <c>searchBasis = ledger_exhaustive</c>, <c>retrievalComplete = true</c> (in 002 sempre)
/// E estrazione completa (tutte le porzioni <c>Completed</c>); altrimenti «assenza non provata». <c>answeredWithOpenConflict</c> (guardia non bloccante, nessun matching lessicale, nessun arbitrato):
/// per una valutazione <c>AnsweredInSnapshot</c> almeno una evidenza di supporto e endpoint di un conflitto canonico Open della revisione AI originaria, oppure di una relazione nota
/// <c>Contradicts</c>/<c>AlternativeTo</c> con un'evidenza di origine diversa (Message contro Document). Non cambia alcun esito.
/// </summary>
public sealed class GapStageDerivation
{
    private readonly bool _extractionComplete;
    private readonly HashSet<Guid> _inOpenConflict;
    private readonly IReadOnlyDictionary<Guid, EvidenceOriginKind> _originOf;
    private readonly IReadOnlyList<DocumentCoverageRelation> _relations;

    public GapStageDerivation(DocumentCoverageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _originOf = input.Evidence.ToDictionary(e => e.EvidenceId, e => e.Origin);
        _extractionComplete = input.Portions.All(p => p.Status == PortionStatus.Completed);
        _relations = input.Relations;
        var itemEvidence = input.Links.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Select(l => l.EvidenceId).ToHashSet());
        _inOpenConflict = input.Ai.Conflicts.Where(c => c.Status == AnalysisConflictStatus.Open)
            .SelectMany(c => c.Alternatives).SelectMany(a => itemEvidence.GetValueOrDefault(a.Id) ?? []).ToHashSet();
    }

    public (bool AbsenceProven, bool AnsweredWithOpenConflict) For(GapOutcome outcome, GapReason reason, IReadOnlyList<Guid> support) =>
        (reason == GapReason.NoAnswer && _extractionComplete, outcome == GapOutcome.AnsweredInSnapshot && support.Any(s => _inOpenConflict.Contains(s) || CrossOriginRelation(s)));

    private bool CrossOriginRelation(Guid evidence) => _relations.Any(r => r.Type is EvidenceRelationType.Contradicts or EvidenceRelationType.AlternativeTo
        && ((r.FromEvidenceId == evidence && _originOf.TryGetValue(r.ToEvidenceId, out var to) && _originOf.TryGetValue(evidence, out var own) && to != own)
            || (r.ToEvidenceId == evidence && _originOf.TryGetValue(r.FromEvidenceId, out var from) && _originOf.TryGetValue(evidence, out var own2) && from != own2)));
}

internal static class GapAssessmentProjection
{
    private static DocumentWorkException Invalid(string message) => new(DocumentWorkException.WorkOutputInvalid, message);

    public static GapAssessmentSet Project(WorkOutput output, DocumentCoverageInput input)
    {
        try
        {
            return ProjectCore(output, input);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw Invalid("Il payload del WorkOutput single-call v5 non è nel formato con valutazioni dei Gap.");
        }
    }

    private static GapAssessmentSet ProjectCore(WorkOutput output, DocumentCoverageInput input)
    {
        var payload = output.PayloadJson ?? throw Invalid("Payload inline del WorkOutput mancante.");
        if (CanonicalJson.Sha256Hex(payload) != output.OutputHash)
        {
            throw Invalid("Il payload del WorkOutput ha un hash diverso da OutputHash.");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != DocumentAnalysisWorkflow.SchemaVersionV5
            || root.GetProperty("gapAssessmentsVersion").GetString() != GapAssessmentRules.Version)
        {
            throw Invalid("Il WorkOutput non è del contratto con valutazioni dei Gap.");
        }

        var catalog = AnalysisAliasCatalog.Decode(root.GetProperty("catalog").GetRawText(), root.GetProperty("catalogHash").GetString()!);
        if (catalog.Data.ContractVersion != DocumentAnalysisWorkflow.SchemaVersionV5 || catalog.Data.RunId != output.OwnerId)
        {
            throw Invalid("Il catalogo del WorkOutput non è del contratto o del run della revisione.");
        }

        var dto = AnalysisAliasContracts.DecodeV2(root.GetProperty("ai").GetRawText());
        if (AnalysisAliasContracts.Hash(dto) != root.GetProperty("aiOutputHash").GetString())
        {
            throw Invalid("Il DTO committato ha un hash diverso da aiOutputHash.");
        }

        var recordsRaw = root.GetProperty("gapAssessments").GetRawText();
        var records = JsonSerializer.Deserialize<List<AiGapAssessment>>(recordsRaw, AnalysisAliasContracts.Options) ?? throw Invalid("Valutazioni dei Gap nulle.");
        GapAssessmentRules.ValidateShape(records);
        var canonical = GapAssessmentRules.Canonical(records);
        var hash = root.GetProperty("gapAssessmentsHash").GetString();
        if (hash != GapAssessmentRules.Hash(records) || CanonicalJson.Canonicalize(recordsRaw) != canonical || GapAssessmentRules.Canonical(dto.GapAssessments) != canonical)
        {
            throw Invalid("Le valutazioni dei Gap committate hanno un hash o una forma diversi da gapAssessmentsHash o dal DTO.");
        }

        var gapKinds = input.Evidence.Where(e => e.Kind == EvidenceKind.Gap).Select(e => e.EvidenceId).ToHashSet();
        var questionKeys = dto.Items.OpenQuestions.Select(q => q.Key).ToHashSet(StringComparer.Ordinal);
        var conflictKeys = dto.Conflicts.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var questionIds = input.Ai.OpenQuestions.Select(q => q.Id).ToHashSet();
        var conflictIds = input.Ai.Conflicts.Select(c => c.Id).ToHashSet();
        var projected = new List<GapAssessmentRecord>(records.Count);
        foreach (var r in records.OrderBy(r => AnalysisAliasPattern.Number(r.Gap)))
        {
            var gap = catalog.Evidence(r.Gap);
            if (gap is null || gap.Kind != EvidenceKind.Gap || !gapKinds.Contains(gap.EvidenceId))
            {
                throw Invalid($"La valutazione {r.Gap} non riguarda un Gap dello snapshot congelato.");
            }

            var support = new List<Guid>(r.SupportingEvidence.Count);
            foreach (var alias in r.SupportingEvidence)
            {
                var entry = catalog.Evidence(alias);
                if (entry is null || entry.Kind == EvidenceKind.Gap)
                {
                    throw Invalid($"Il supporto {alias} della valutazione {r.Gap} non è un'evidenza non Gap dello snapshot.");
                }

                support.Add(entry.EvidenceId);
            }

            Guid? openItem = null;
            if (r.OpenItem is not null)
            {
                openItem = AnalysisAliasComposer.CanonicalId("item", catalog.Data.RunId, catalog.Data.SourceHash, r.OpenItem);
                if (!questionKeys.Contains(r.OpenItem) || !questionIds.Contains(openItem.Value))
                {
                    throw Invalid($"La domanda aperta {r.OpenItem} della valutazione {r.Gap} non è nel DTO e nella revisione AI.");
                }
            }

            Guid? openConflict = null;
            if (r.OpenConflict is not null)
            {
                openConflict = AnalysisAliasComposer.CanonicalId("conflict", catalog.Data.RunId, catalog.Data.SourceHash, r.OpenConflict);
                if (!conflictKeys.Contains(r.OpenConflict) || !conflictIds.Contains(openConflict.Value))
                {
                    throw Invalid($"Il conflitto aperto {r.OpenConflict} della valutazione {r.Gap} non è nel DTO e nella revisione AI.");
                }
            }

            projected.Add(new GapAssessmentRecord(
                gap.EvidenceId, gap.EvidenceVersionId, catalog.Data.SourceHash, r.Outcome, r.Reason, r.Rationale, support, openItem, openConflict, output.BatchId));
        }

        return new(projected, hash!);
    }

    /// <summary>
    /// Proiezione read-only delle valutazioni dal WorkOutput dello STADIO Gap (v6, AI §13.7). Verifica hash, DTO strict, record e catalogo (<see cref="GapStagePayload.Read"/>), ricostruisce i riferimenti Open
    /// dall'analisi della revisione AI originaria e dalla sua mappa item→evidenze (identità <c>refsHash</c> verificata) e ne risolve gli id canonici. I campi derivati
    /// (<c>searchBasis</c>, <c>retrievalComplete</c>, <c>absenceProven</c>, <c>answeredWithOpenConflict</c>) sono calcolati qui, mai dichiarati dal modello. Nessuna valutazione è sintetizzata o corretta.
    /// </summary>
    public static GapAssessmentSet ProjectStage(WorkOutput output, DocumentCoverageInput input, WorkOutput? retrieval = null)
    {
        try
        {
            return ProjectStageCore(output, input, retrieval);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw Invalid("Il payload del WorkOutput dello stadio Gap non è nel formato atteso.");
        }
    }

    private static GapAssessmentSet ProjectStageCore(WorkOutput output, DocumentCoverageInput input, WorkOutput? retrievalOutput)
    {
        var payload = output.PayloadJson ?? throw Invalid("Payload inline del WorkOutput dello stadio Gap mancante.");
        if (CanonicalJson.Sha256Hex(payload) != output.OutputHash)
        {
            throw Invalid("Il payload del WorkOutput dello stadio Gap ha un hash diverso da OutputHash.");
        }

        // Sorgente `retrieval` (003 T032): il registro è letto dal WorkOutput del recupero con schema e hash verificati; deve essere consegnabile (`Package`), altrimenti il run non avrebbe prodotto uno stadio.
        RetrievalRegistry? registry = null;
        if (retrievalOutput is not null)
        {
            if (retrievalOutput.OwnerId != output.OwnerId || retrievalOutput.PayloadJson is null || CanonicalJson.Sha256Hex(retrievalOutput.PayloadJson) != retrievalOutput.OutputHash)
            {
                throw Invalid("Il WorkOutput del recupero non è del run della revisione o ha un hash diverso da OutputHash.");
            }

            registry = RetrievalOutput.Read(retrievalOutput.PayloadJson);
            if (registry.Outcome != CandidateRegistry.OutcomePackage)
            {
                throw Invalid("Il registro del recupero non è consegnabile: nessun report su uno stadio senza candidati validi.");
            }
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var catalog = AnalysisAliasCatalog.Decode(root.GetProperty("catalog").GetRawText(), root.GetProperty("catalogHash").GetString()!);
        if (catalog.Data.RunId != output.OwnerId)
        {
            throw Invalid("Il catalogo del WorkOutput dello stadio Gap non è del run della revisione.");
        }

        var map = input.Links.GroupBy(l => l.ItemId).Select(g => new EvidenceMapEntry { ItemId = g.Key, EvidenceIds = [.. g.Select(l => l.EvidenceId).Distinct()] }).ToList();
        var references = GapStageReferences.From(input.Ai, map, catalog);
        var read = GapStagePayload.Read(payload, catalog, references);
        if (read.MissingGaps.Count > 0)
        {
            throw Invalid("Lo stadio Gap committato ha Gap senza valutazione: nessun report su una revisione incompleta.");
        }

        var gapIds = input.Evidence.Where(e => e.Kind == EvidenceKind.Gap).Select(e => e.EvidenceId).ToHashSet();
        var derivation = new GapStageDerivation(input);
        var projected = new List<GapAssessmentRecord>(read.Records.Count);
        foreach (var r in read.Records.OrderBy(r => AnalysisAliasPattern.Number(r.Gap)))
        {
            var gap = catalog.Evidence(r.Gap);
            if (gap is null || gap.Kind != EvidenceKind.Gap || !gapIds.Contains(gap.EvidenceId))
            {
                throw Invalid($"La valutazione {r.Gap} non riguarda un Gap dello snapshot congelato.");
            }

            var support = new List<Guid>(r.SupportingEvidence.Count);
            foreach (var alias in r.SupportingEvidence)
            {
                var entry = catalog.Evidence(alias);
                if (entry is null || entry.Kind == EvidenceKind.Gap)
                {
                    throw Invalid($"Il supporto {alias} della valutazione {r.Gap} non è un'evidenza non Gap dello snapshot.");
                }

                support.Add(entry.EvidenceId);
            }

            var openItem = r.OpenItem is null ? (Guid?)null : references.Questions.FirstOrDefault(q => q.Key == r.OpenItem)?.ItemId
                ?? throw Invalid($"La domanda aperta {r.OpenItem} della valutazione {r.Gap} non è nella revisione AI.");
            var openConflict = r.OpenConflict is null ? (Guid?)null : references.Conflicts.FirstOrDefault(c => c.Key == r.OpenConflict)?.ConflictId
                ?? throw Invalid($"Il conflitto aperto {r.OpenConflict} della valutazione {r.Gap} non è nella revisione AI.");

            var (absenceProven, answeredWithConflict) = derivation.For(r.Outcome, r.Reason, support);
            // Sorgente `retrieval`: un NoAnswer è «assenza non provata», MAI provata (FR-017–FR-019): la ricerca ha visto solo i passaggi recuperati, e `retrievalComplete` non è prova di completezza.
            projected.Add(new GapAssessmentRecord(
                gap.EvidenceId, gap.EvidenceVersionId, catalog.Data.SourceHash, r.Outcome, r.Reason, r.Rationale, support, openItem, openConflict, output.BatchId,
                registry is null ? GapStage.SearchBasisLedgerExhaustive : CandidateRegistry.SearchBasis, registry?.RetrievalComplete ?? true,
                registry is null && absenceProven, answeredWithConflict));
        }

        return new(projected, read.GapAssessmentsHash, registry);
    }
}

internal static class DocumentCoverageLoader
{
    /// <summary>
    /// Versione del report dal dato discriminante dell'origine della revisione AI (AI §13.6): algoritmo del WorkManifest e schema dei WorkOutput.
    /// `analysis-single-v3` con un solo WorkOutput `document-analysis-alias-2` ⇒ coverage-3; ogni altra origine coerente (gerarchica `global-reconcile-v1`/`reconcile-alias-1`, storica, senza dati di lavoro) ⇒ coverage-2.
    /// Origine discordante (algoritmo e schema non concordano) è <c>integrity_conflict</c>: nessun ripiego.
    /// </summary>
    public static async Task<CoverageOrigin> OriginAsync(AppDbContext db, Guid runId, CancellationToken ct)
    {
        var algorithm = await db.WorkManifests.AsNoTracking()
            .Where(m => m.OwnerKind == WorkOwnerKind.ProcessingRun && m.OwnerId == runId).Select(m => m.AlgorithmVersion).SingleOrDefaultAsync(ct);
        var outputs = await db.WorkOutputs.AsNoTracking()
            .Where(o => o.OwnerKind == WorkOwnerKind.ProcessingRun && o.OwnerId == runId).Select(o => new { o.Id, o.SchemaVersion }).ToListAsync(ct);
        var singleCallOutputs = outputs.Where(o => o.SchemaVersion == DocumentAnalysisWorkflow.SchemaVersionV5).ToList();
        var stageOutputs = outputs.Where(o => o.SchemaVersion == GapStage.AiSchema).ToList();
        if (algorithm == GapStage.Algorithm)
        {
            // Revisione della definizione v6 con stadio Gap (AI §13.7): `gap-stage-1` sul manifest ⇒ coverage-3 estesa; serve ESATTAMENTE un WorkOutput dello stadio accanto a quelli della gerarchia.
            if (stageOutputs.Count != 1 || singleCallOutputs.Count > 0 || outputs.Count < 2)
            {
                throw new DocumentWorkException(
                    DocumentWorkException.IntegrityConflict, "L'algoritmo gap-stage-1 della revisione AI non corrisponde a un solo WorkOutput dello stadio Gap accanto a quelli della gerarchia.");
            }

            // Definizione v7 (003 T032): al più UN WorkOutput `retrieval-output-1` (il batch deterministico del recupero) accanto alla gerarchia e allo stadio; la sua presenza decide la sorgente `retrieval`.
            var retrievalOutputs = outputs.Where(o => o.SchemaVersion == RetrievalStage.OutputSchema).ToList();
            if (retrievalOutputs.Count > 1)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "La revisione AI ha più di un WorkOutput del recupero.");
            }

            return new(
                DocumentCoverage.CheckerVersionV3, await db.WorkOutputs.AsNoTracking().SingleAsync(o => o.Id == stageOutputs[0].Id, ct), Stage: true,
                Retrieval: retrievalOutputs.Count == 0 ? null : await db.WorkOutputs.AsNoTracking().SingleAsync(o => o.Id == retrievalOutputs[0].Id, ct));
        }

        if (stageOutputs.Count > 0 || outputs.Any(o => o.SchemaVersion == RetrievalStage.OutputSchema))
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Un WorkOutput dello stadio Gap o del recupero appartiene a una revisione AI di un altro algoritmo.");
        }

        if (algorithm == DocumentAnalysisWorkflow.AlgorithmVersionV5)
        {
            if (outputs.Count != 1 || singleCallOutputs.Count != 1)
            {
                throw new DocumentWorkException(
                    DocumentWorkException.IntegrityConflict, "L'algoritmo single-call v5 della revisione AI non corrisponde a un solo WorkOutput document-analysis-alias-2.");
            }

            return new(DocumentCoverage.CheckerVersionV3, await db.WorkOutputs.AsNoTracking().SingleAsync(o => o.Id == singleCallOutputs[0].Id, ct));
        }

        return singleCallOutputs.Count > 0
            ? throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Un WorkOutput single-call v5 appartiene a una revisione AI di un altro algoritmo.")
            : new(DocumentCoverage.CheckerVersion, null);
    }

    public static DocumentCoverageFindingView ToView(DocumentCoverageFinding f) =>
        new(f.Category.ToString(), f.Code, f.Message, f.EvidenceId, f.Alias, f.ItemId, f.Section, f.Confidence, f.Details, f.Blocking);

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Stato strutturale di una preparazione: porzioni attese/elaborate/senza evidenza/mancanti e hash di manifest, parse ed estrazione.</summary>
    public static async Task<(PreparationCoverageRecord Record, IReadOnlyList<DocumentCoveragePortion> Portions)> PreparationAsync(
        AppDbContext db, Guid projectId, Guid preparationId, CancellationToken ct)
    {
        var p = await db.DocumentPreparations.AsNoTracking().SingleAsync(x => x.Id == preparationId && x.ProjectId == projectId, ct);
        var documentId = await db.DocumentVersions.AsNoTracking().Where(v => v.Id == p.DocumentVersionId).Select(v => v.DocumentId).SingleAsync(ct);
        var parse = await db.ParsedArtifacts.AsNoTracking().Where(a => a.PreparationId == p.Id).Select(a => a.Sha256).SingleOrDefaultAsync(ct);
        var extraction = p.CurrentEvidenceVersionId is { } versionId
            ? await db.EvidenceExtractionVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.ManifestHash).SingleOrDefaultAsync(ct)
            : null;
        var portions = (await db.DocumentPortions.AsNoTracking().Where(x => x.PreparationId == p.Id).OrderBy(x => x.Ordinal).ToListAsync(ct))
            .Select(x => new DocumentCoveragePortion(x.Id, x.PreparationId, x.Ordinal, x.Status, x.NoEvidenceReason)).ToList();
        var completed = portions.Where(x => x.Status == PortionStatus.Completed).ToList();
        var missing = portions.Where(x => x.Status != PortionStatus.Completed).Select(x => x.PortionId).ToList();
        var record = new PreparationCoverageRecord(
            p.Id, documentId, p.DocumentVersionId, p.Generation, p.Status.ToString(),
            new(p.ManifestHash, parse, p.CurrentEvidenceVersionId, p.CurrentEvidenceVersionId is null ? null : $"{p.CurrentEvidenceVersionId:N}:{extraction}"),
            new(portions.Count, completed.Count, completed.Count(x => x.NoEvidenceReason is not null), missing.Count, missing));
        return (record, portions);
    }

    /// <summary>
    /// Stato della riconciliazione del run dalle proiezioni append-only: livelli (nodi/pagine), candidati con l'ultima disposition, patch con l'ultimo esito, frontiera
    /// (candidati Pending/NotProcessed con motivo) e punti aperti della revisione. Sola lettura.
    /// </summary>
    public static async Task<ReconciliationCoverageSection> ReconciliationAsync(AppDbContext db, Guid runId, DocumentAnalysisContent target, CancellationToken ct)
    {
        var nodes = await db.RunCatalogNodes.AsNoTracking().Where(n => n.RunId == runId).Select(n => new { n.NodeId, n.View, n.Level }).ToListAsync(ct);
        var pageNodes = await db.RunCatalogPages.AsNoTracking().Where(p => p.RunId == runId).Select(p => p.NodeId).ToListAsync(ct);
        var levels = nodes.GroupBy(n => (n.View, n.Level)).OrderBy(g => g.Key.View).ThenBy(g => g.Key.Level)
            .Select(g => new ReconciliationLevelRecord(g.Key.View.ToString(), g.Key.Level, g.Count(), pageNodes.Count(id => g.Any(n => n.NodeId == id)))).ToList();

        var candidates = await db.RunCandidates.AsNoTracking().Where(c => c.RunId == runId).OrderBy(c => c.CandidateId).Select(c => new { c.CandidateId, c.Kind, c.ProposedType }).ToListAsync(ct);
        var latest = (await db.RunCandidateDispositions.AsNoTracking().Where(d => d.RunId == runId).ToListAsync(ct))
            .GroupBy(d => d.CandidateId).ToDictionary(g => g.Key, g => g.MaxBy(d => d.Version)!, StringComparer.Ordinal);
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Terminal(string id, HashSet<string> path)
        {
            if (memo.TryGetValue(id, out var known))
            {
                return known;
            }

            if (!latest.TryGetValue(id, out var d) || !path.Add(id))
            {
                return false;
            }

            var children = ReconciliationCodec.Decode<List<string>>(d.ChildCandidateIdsJson);
            var result = d.State switch
            {
                CandidateState.Compared or CandidateState.Open => true,
                CandidateState.Expanded => children.Count > 0 && children.All(c => Terminal(c, path)),
                _ => false,
            };
            path.Remove(id);
            return memo[id] = result;
        }

        var candidateRecords = candidates.Select(c =>
        {
            latest.TryGetValue(c.CandidateId, out var d);
            return new ReconciliationCandidateRecord(
                c.CandidateId, c.Kind.ToString(), c.ProposedType.ToString(), d?.State.ToString() ?? "NoDisposition", d?.Reason,
                d is null ? [] : ReconciliationCodec.Decode<List<string>>(d.ChildCandidateIdsJson), Terminal(c.CandidateId, []));
        }).ToList();
        var frontier = candidateRecords.Where(c => c.State is "Pending" or "NotProcessed" or "NoDisposition")
            .Select(c => new ReconciliationFrontierRecord(c.CandidateId, c.Reason ?? c.State)).ToList();

        var patches = await db.RunPatches.AsNoTracking().Where(p => p.RunId == runId).OrderBy(p => p.PatchId).Select(p => new { p.PatchId, p.TargetKind, p.TargetId }).ToListAsync(ct);
        var patchLatest = (await db.RunPatchDispositions.AsNoTracking().Where(d => d.RunId == runId).ToListAsync(ct))
            .GroupBy(d => d.PatchId).ToDictionary(g => g.Key, g => g.MaxBy(d => d.Version)!, StringComparer.Ordinal);
        var patchRecords = patches.Select(p =>
        {
            patchLatest.TryGetValue(p.PatchId, out var d);
            var kind = d?.Kind;
            var terminal = kind is RunPatchDispositionKind.Applied or RunPatchDispositionKind.Duplicate;
            return new ReconciliationPatchRecord(p.PatchId, p.TargetKind.ToString(), p.TargetId, kind?.ToString() ?? "NoDisposition", d?.Reason,
                !(terminal || kind == RunPatchDispositionKind.PreservedAsConflict));
        }).Where(p => p.State is not ("Applied" or "Duplicate")).ToList();

        var openPoints = new List<ReconciliationOpenPoint>();
        openPoints.AddRange(target.Conflicts.Where(c => c.Status == AnalysisConflictStatus.Open).Select(c => new ReconciliationOpenPoint(
            "conflict", c.Id, c.Description, [.. c.Alternatives.Select(a => new ReconciliationAlternativeRecord(a.Id, a.Text, a.Classification.ToString(), a.EvidenceRefs))])));
        openPoints.AddRange(target.OpenQuestions.Select(q => new ReconciliationOpenPoint("question", q.Id, q.Text, [])));
        return new(nodes.Count + candidates.Count + patches.Count > 0, levels, candidateRecords, patchRecords, frontier, openPoints);
    }

    public static async Task<LoadedDocumentCoverage> LoadAsync(
        AppDbContext db, ProcessingRun run, AnalysisEvidenceSelection selection, AnalysisRevision aiRevision, AnalysisRevision revision, Draft? draft, CancellationToken ct)
    {
        var view = EvidenceSelectionView.Deserialize(selection.View);
        var selected = await db.AnalysisEvidenceSelectionItems.AsNoTracking()
            .Where(i => i.RunId == run.Id && i.OriginKind == EvidenceOriginKind.Document && i.PreparationId != null).OrderBy(i => i.PreparationId).ToListAsync(ct);
        var versionIds = selected.Select(i => i.VersionId).ToList();
        var preparationIds = selected.Select(i => i.PreparationId!.Value).Distinct().ToList();

        var evidence = new List<DocumentCoverageEvidence>();
        foreach (var e in view.Evidences)
        {
            evidence.Add(new(e.EvidenceId, e.Alias, EvidenceOriginKind.Message, e.MessageId, e.Kind, e.Polarity, e.Statement,
                [.. e.Content.Qualifiers.Select(q => new CoverageQualifier(q.Type.ToString(), q.Text))], e.Content.Structure?.Instrument, e.Content.Structure?.Action,
                [.. e.Content.Sources.Select(s => new CoverageSource(Guid.Empty, s.MessageId, null, s.Start, s.End, s.Quote))]));
        }

        var rows = await db.Evidences.AsNoTracking().Where(e => versionIds.Contains(e.VersionId)).OrderBy(e => e.PreparationId).ThenBy(e => e.Ordinal).ToListAsync(ct);
        foreach (var row in rows)
        {
            var content = DocumentEvidenceCodec.DecodeContent(row.Content);
            evidence.Add(new(row.Id, $"D{evidence.Count(x => x.Origin == EvidenceOriginKind.Document) + 1}", EvidenceOriginKind.Document, null, row.Kind, row.Polarity, row.Statement,
                [.. content.Qualifiers.Select(q => new CoverageQualifier(q.Type.ToString(), q.Text))], content.Structure.Instrument, content.Structure.Action,
                [.. content.Sources.Select(s => new CoverageSource(s.SourceId, s.MessageId, s.BlockId, s.Start, s.End, s.Quote))]));
        }

        var blockIds = evidence.SelectMany(e => e.Sources).Where(s => s.BlockId is not null).Select(s => s.BlockId!.Value).Distinct().ToList();
        var messageIds = evidence.SelectMany(e => e.Sources).Where(s => s.BlockId is null && s.MessageId is not null).Select(s => s.MessageId!.Value).Distinct().ToList();
        var texts = await db.DocumentBlocks.AsNoTracking().Where(b => blockIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.CanonicalText, ct);
        foreach (var (id, body) in await db.ConversationMessages.AsNoTracking().Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Body, ct))
        {
            texts[id] = body;
        }

        var links = (await db.AnalysisEvidenceLinks.AsNoTracking().Where(l => l.RunId == run.Id).Select(l => new { l.ItemId, l.EvidenceId }).ToListAsync(ct))
            .Select(l => (l.ItemId, l.EvidenceId)).ToList();
        var relations = (await db.AnalysisEvidenceRelations.AsNoTracking().Where(r => r.RunId == run.Id).ToListAsync(ct))
            .Select(r => new DocumentCoverageRelation(r.FromEvidenceId, r.ToEvidenceId, r.Type))
            .Concat((await db.EvidenceRelations.AsNoTracking().Where(r => versionIds.Contains(r.VersionId)).ToListAsync(ct))
                .Select(r => new DocumentCoverageRelation(r.FromEvidenceId, r.ToEvidenceId, r.Type)))
            .Distinct().ToList();

        var preparations = new List<PreparationCoverageRecord>();
        var portions = new List<DocumentCoveragePortion>();
        foreach (var id in preparationIds)
        {
            var (record, list) = await PreparationAsync(db, run.ProjectId, id, ct);
            preparations.Add(record);
            portions.AddRange(list);
        }

        var snapshotHash = run.SourceSnapshotId is { } snapshotId
            ? await db.SourceSnapshots.AsNoTracking().Where(s => s.Id == snapshotId).Select(s => s.Hash).SingleOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;
        var hashes = new DocumentCoverageHashes(
            snapshotHash, selection.ViewHash, [.. preparations.Select(p => p.Hashes.ParseSha256 ?? string.Empty)],
            [.. preparations.Select(p => p.Hashes.ExtractionHash ?? string.Empty)], Sha256(revision.ContentJson), draft is null ? null : Sha256(draft.Markdown));
        var input = new DocumentCoverageInput(
            evidence, links, DocumentAnalysisContentJson.Deserialize(aiRevision.ContentJson), DocumentAnalysisContentJson.Deserialize(revision.ContentJson), draft?.Markdown,
            texts, portions, relations, await ReconciliationAsync(db, run.Id, DocumentAnalysisContentJson.Deserialize(revision.ContentJson), ct));
        return new(input, hashes, preparations);
    }
}
