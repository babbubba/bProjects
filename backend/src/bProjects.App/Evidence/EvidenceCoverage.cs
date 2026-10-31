using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
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
    private static partial Regex Negation();

    [GeneratedRegex(@"\[fonte:([^\]\s]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceMarker();

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

    /// <summary>Radici (prime cinque lettere) delle parole significative: almeno quattro lettere, senza parole funzionali.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        [.. Token().Matches(Normalize(text)).Select(m => m.Value).Where(w => w.Length >= 4 && !StopWords.Contains(w) && !w.All(char.IsDigit))
            .Select(w => w.Length > 5 ? w[..5] : w).Distinct(StringComparer.Ordinal)];

    private static bool Contains(string text, IReadOnlyList<string> stems)
    {
        var normalized = Normalize(text);
        return stems.Any(s => normalized.Contains(s, StringComparison.Ordinal));
    }

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Short(string text) => text.Length <= 90 ? text : text[..90] + "…";

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

    private static async Task<CoverageReportView> ToViewAsync(AppDbContext db, EvidenceCoverageReport report, CancellationToken ct)
    {
        var content = EvidenceJson.Deserialize<CoverageReportContent>(report.Report);
        var names = await EvidenceEndpoints.UserNames(db, [report.CreatedBy], ct);
        return new(report.Id, report.AnalysisId, report.RevisionId, report.DraftId, report.CheckerVersion, report.InputHash, report.CreatedAt,
            names.GetValueOrDefault(report.CreatedBy), new(report.DeterministicErrors, report.SemanticRisks, report.PmAssessments),
            [.. content.Findings.Select(f => new CoverageFindingView(f.Category.ToString(), f.Code, f.Message, f.EvidenceId, f.Alias, f.ItemId, f.Section, f.Confidence, f.Details))],
            content.Limits);
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = "coverage_input_invalid" });
}
