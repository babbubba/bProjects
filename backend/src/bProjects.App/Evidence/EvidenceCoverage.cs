using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;
using bProjects.App.Documents;
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
        Task<EvidenceCoverageReport?> Find() => db.EvidenceCoverageReports.AsNoTracking().SingleOrDefaultAsync(r =>
            r.AnalysisId == run.Id && r.RevisionId == revision.Id && r.DraftId == draftId && r.CheckerVersion == DocumentCoverage.CheckerVersion, ct);
        if (await Find() is { } existing)
        {
            return Results.Ok(await ToViewAsync(db, existing, ct));
        }

        var loaded = await DocumentCoverageLoader.LoadAsync(db, run, selection, aiRevision, revision, draft, ct);
        var findings = DocumentCoverage.Check(loaded.Input);
        var summary = DocumentCoverage.Summarize(loaded.Input, findings);
        var content = new DocumentCoverageContent(
            DocumentCoverage.CheckerVersion, run.Id, aiRevision.Id, revision.Id, draftId, loaded.Hashes, summary, loaded.Preparations, findings, DocumentCoverage.Limits);
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

    private static async Task<object> ToViewAsync(AppDbContext db, EvidenceCoverageReport report, CancellationToken ct)
    {
        var names = await EvidenceEndpoints.UserNames(db, [report.CreatedBy], ct);
        if (report.CheckerVersion == DocumentCoverage.CheckerVersion)
        {
            var document = EvidenceJson.Deserialize<DocumentCoverageContent>(report.Report);
            return new DocumentCoverageReportView(
                report.Id, report.AnalysisId, report.RevisionId, report.DraftId, report.CheckerVersion, report.InputHash, report.CreatedAt, names.GetValueOrDefault(report.CreatedBy),
                new(report.DeterministicErrors, report.SemanticRisks, report.PmAssessments), document.Summary, document.Hashes, document.Preparations,
                [.. document.Findings.Select(DocumentCoverageLoader.ToView)], document.Limits);
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

public sealed record DocumentCoverageInput(
    IReadOnlyList<DocumentCoverageEvidence> Evidence, IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> Links, DocumentAnalysisContent Ai,
    DocumentAnalysisContent Target, string? DraftMarkdown, IReadOnlyDictionary<Guid, string> OriginalTexts, IReadOnlyList<DocumentCoveragePortion> Portions,
    IReadOnlyList<DocumentCoverageRelation> Relations);

public sealed record DocumentCoverageFinding(
    FindingCategory Category, string Code, string Message, Guid? EvidenceId, string? Alias, Guid? ItemId, string? Section, string? Confidence, string? Details, bool Blocking);

public sealed record DocumentCoverageSummary(
    int ExpectedEvidence, int RepresentedEvidence, int PortionsExpected, int PortionsProcessed, int PortionsNoEvidence, int PortionsMissing,
    int DeterministicErrors, int SemanticRisks, int PmAssessments, int BlockingFindings);

public sealed record DocumentCoverageHashes(string SourceSnapshotHash, string ViewHash, IReadOnlyList<string> ParseHashes, IReadOnlyList<string> ExtractionHashes, string RevisionHash, string? DraftHash);

public sealed record DocumentCoverageContent(
    string CheckerVersion, Guid AnalysisId, Guid AiRevisionId, Guid RevisionId, Guid? DraftId, DocumentCoverageHashes Hashes, DocumentCoverageSummary Summary,
    IReadOnlyList<PreparationCoverageRecord> Preparations, IReadOnlyList<DocumentCoverageFinding> Findings, IReadOnlyList<string> Limits);

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

    public static IReadOnlyList<string> Limits { get; } =
    [
        .. EvidenceCoverage.Limits,
        "La copertura per porzioni misura il trattamento strutturale dell'estrazione: una porzione elaborata senza evidenze dichiara un motivo ma non prova che non vi fosse nulla da estrarre.",
        "Gli ID collegati a un elemento non dimostrano che il testo sia fedele alla fonte: anche con tutte le evidenze rappresentate un gold semanticamente errato è segnalato solo come rischio stimato.",
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
                findings.Add(Doc(FindingCategory.DeterministicError, "evidence_unrepresented",
                    $"{e.Alias} ({e.Kind}) non è collegata a nessun elemento dell'analisi: «{EvidenceCoverage.Short(e.Statement)}».", e.EvidenceId, e.Alias));
                continue;
            }

            foreach (var item in inTarget.Where(i => i.Item.EvidenceRefs.Count > 0 && !i.Item.EvidenceRefs.Any(r => CitesEvidence(e, r, targetSources))))
            {
                findings.Add(Doc(FindingCategory.DeterministicError, "evidence_source_mismatch",
                    $"L'elemento collegato a {e.Alias} non cita nessuna fonte di quell'evidenza.", e.EvidenceId, e.Alias, item.Item.Id, item.Section));
            }

            foreach (var item in inAi.Where(i => !targetItems.ContainsKey(i.Item.Id)))
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

    public static string InputHash(DocumentCoverageHashes hashes, IReadOnlyCollection<(Guid ItemId, Guid EvidenceId)> links)
    {
        var text = new StringBuilder()
            .Append(CheckerVersion).Append('|').Append(hashes.SourceSnapshotHash).Append('|').Append(hashes.ViewHash)
            .Append('|').Append(string.Join(",", hashes.ParseHashes.Order(StringComparer.Ordinal)))
            .Append('|').Append(string.Join(",", hashes.ExtractionHashes.Order(StringComparer.Ordinal)))
            .Append('|').Append(hashes.RevisionHash).Append('|').Append(hashes.DraftHash)
            .Append('|').Append(string.Join(",", links.OrderBy(l => l.ItemId).ThenBy(l => l.EvidenceId).Select(l => $"{l.ItemId}:{l.EvidenceId}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
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
    IReadOnlyList<DocumentCoverageFindingView> Findings, IReadOnlyList<string> Limits);

/// <summary>GET di preparazione/coverage: stato strutturale della preparazione prima dell'analisi. Non avvia lavori e non persiste report.</summary>
public sealed record PreparationCoverageView(
    string CheckerVersion, Guid PreparationId, Guid DocumentId, Guid DocumentVersionId, int Generation, string Status, PreparationCoverageHashes Hashes,
    PortionCountsView Portions, IReadOnlyList<DocumentCoverageFindingView> Findings, IReadOnlyList<string> Limits);

internal sealed record LoadedDocumentCoverage(DocumentCoverageInput Input, DocumentCoverageHashes Hashes, IReadOnlyList<PreparationCoverageRecord> Preparations);

internal static class DocumentCoverageLoader
{
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
            texts, portions, relations);
        return new(input, hashes, preparations);
    }
}
