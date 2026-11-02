using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T062 (DET) — `coverage-2` (contracts/ai-workflow.md §errori; http-api.md §coverage): errori deterministici, rischi semantici stimati e giudizi PM
/// restano separati; l'omissione di un'evidenza nota è un errore bloccante; un gold semanticamente errato è rilevato (separatamente) anche con
/// copertura per ID del 100%; nessun booleano «complete» autorevole. Esempi in domini inventati, senza database.
/// </summary>
public sealed class DocumentCoverageCheckerTests
{
    private static readonly IReadOnlyDictionary<string, FindingCategory> Codes = new Dictionary<string, FindingCategory>
    {
        ["evidence_unrepresented"] = FindingCategory.DeterministicError,
        ["source_range_invalid"] = FindingCategory.DeterministicError,
        ["evidence_source_mismatch"] = FindingCategory.DeterministicError,
        ["item_source_unresolved"] = FindingCategory.DeterministicError,
        ["item_without_fragment"] = FindingCategory.DeterministicError,
        ["portion_not_processed"] = FindingCategory.DeterministicError,
        ["known_relation_unrepresented"] = FindingCategory.DeterministicError,
        ["revision_source_not_cited"] = FindingCategory.DeterministicError,
        ["qualifier_changed"] = FindingCategory.SemanticRisk,
        ["negation_inverted"] = FindingCategory.SemanticRisk,
        ["instrument_detached"] = FindingCategory.SemanticRisk,
        ["revision_item_missing_in_document"] = FindingCategory.SemanticRisk,
        ["item_removed_by_pm"] = FindingCategory.PmAssessment,
        ["item_rewritten_by_pm"] = FindingCategory.PmAssessment,
        ["conflict_resolved_by_pm"] = FindingCategory.PmAssessment,
    };

    // ---------------------------------------------------------------- omissione nota bloccante

    [Fact]
    public void Omitting_a_known_evidence_is_a_blocking_deterministic_error_and_the_id_coverage_shows_it()
    {
        var input = Build(new Spec("Il cliente A emette al massimo 5 ordini al giorno."), new Spec("Il magazzino B chiude alle 18.", Linked: false));

        var findings = DocumentCoverage.Check(input);

        var finding = Assert.Single(findings);
        Assert.Equal(("evidence_unrepresented", FindingCategory.DeterministicError, true), (finding.Code, finding.Category, finding.Blocking));
        Assert.Equal("E2", finding.Alias);
        Assert.Null(finding.Confidence);
        var summary = DocumentCoverage.Summarize(input, findings);
        Assert.Equal((2, 1, 1, 0, 0), (summary.ExpectedEvidence, summary.RepresentedEvidence, summary.DeterministicErrors, summary.SemanticRisks, summary.PmAssessments));
        Assert.Equal(1, summary.BlockingFindings);
    }

    [Fact]
    public void A_report_without_findings_has_no_blocking_finding_and_a_checked_input_is_not_called_complete()
    {
        var input = Build(new Spec("Il cliente A emette ordini."));

        var findings = DocumentCoverage.Check(input);

        Assert.Empty(findings);
        Assert.Equal(0, DocumentCoverage.Summarize(input, findings).BlockingFindings);
    }

    // ---------------------------------------------------------------- gold semanticamente errato con ID al 100%

    [Fact]
    public void A_semantically_wrong_gold_is_detected_as_separate_semantic_risks_even_with_100_percent_id_coverage()
    {
        var input = Build(
            new Spec("Il cliente A emette al massimo 5 ordini al giorno.", Qualifiers: ["al massimo 5 ordini"], ItemText: "Il cliente A emette al massimo 8 ordini al giorno."),
            new Spec("Il portale non invia SMS.", Polarity: EvidencePolarity.Prohibited, ItemText: "Il portale invia SMS ai clienti."));

        var findings = DocumentCoverage.Check(input);
        var summary = DocumentCoverage.Summarize(input, findings);

        Assert.Equal((2, 2), (summary.ExpectedEvidence, summary.RepresentedEvidence));
        Assert.Equal(0, summary.DeterministicErrors);
        Assert.Equal(0, summary.BlockingFindings);
        Assert.Equal(["negation_inverted", "qualifier_changed"], findings.Select(f => f.Code).Order());
        Assert.All(findings, f =>
        {
            Assert.Equal(FindingCategory.SemanticRisk, f.Category);
            Assert.False(f.Blocking);
            Assert.NotNull(f.Confidence);
        });
        Assert.Equal(2, summary.SemanticRisks);
    }

    [Fact]
    public void A_faithful_representation_raises_neither_deterministic_nor_semantic_findings()
    {
        var input = Build(
            new Spec("Il cliente A emette al massimo 5 ordini al giorno.", Qualifiers: ["al massimo 5 ordini"]),
            new Spec("Il portale non invia SMS.", Polarity: EvidencePolarity.Prohibited));

        Assert.Empty(DocumentCoverage.Check(input));
    }

    // ---------------------------------------------------------------- porzioni

    [Fact]
    public void Pending_and_failed_portions_are_deterministic_errors_while_a_no_evidence_portion_is_a_reasoned_outcome()
    {
        var prep = Guid.NewGuid();
        var (done, empty, pending, failed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var input = Build(new Spec("Il cliente A emette ordini.")) with
        {
            Portions =
            [
                new(done, prep, 1, PortionStatus.Completed, null),
                new(empty, prep, 2, PortionStatus.Completed, "solo intestazione"),
                new(pending, prep, 3, PortionStatus.Pending, null),
                new(failed, prep, 4, PortionStatus.Failed, null),
            ],
        };

        var findings = DocumentCoverage.Check(input);
        var summary = DocumentCoverage.Summarize(input, findings);

        Assert.Equal(["portion_not_processed", "portion_not_processed"], findings.Select(f => f.Code));
        Assert.All(findings, f => Assert.True(f.Blocking && f.Category == FindingCategory.DeterministicError));
        Assert.Equal((4, 2, 1, 2), (summary.PortionsExpected, summary.PortionsProcessed, summary.PortionsNoEvidence, summary.PortionsMissing));
        Assert.Equal(2, summary.BlockingFindings);
    }

    // ---------------------------------------------------------------- provenienza

    [Fact]
    public void A_source_range_that_no_longer_matches_the_original_text_or_whose_block_is_gone_is_a_deterministic_error()
    {
        var input = Build(new Spec("Il cliente A emette ordini."), new Spec("Il magazzino B chiude alle 18."));
        var first = input.Evidence[0].Sources[0];
        var second = input.Evidence[1].Sources[0];
        var texts = input.OriginalTexts.ToDictionary(t => t.Key, t => t.Value);
        texts[first.BlockId!.Value] = "Testo del blocco cambiato del tutto.";
        texts.Remove(second.BlockId!.Value);

        var findings = DocumentCoverage.Check(input with { OriginalTexts = texts });

        Assert.Equal(["source_range_invalid", "source_range_invalid"], findings.Select(f => f.Code));
        Assert.All(findings, f => Assert.True(f.Blocking));
    }

    [Fact]
    public void An_item_that_cites_nothing_of_its_evidence_or_a_source_outside_the_revision_is_a_deterministic_error()
    {
        var input = Build(new Spec("Il cliente A emette ordini."), new Spec("Il magazzino B chiude alle 18."), new Spec("Il corriere C ritira alle 9."));
        var requirements = input.Target.Requirements.ToList();
        requirements[0] = requirements[0] with { EvidenceRefs = [] };                                   // Documented senza frammento
        requirements[1] = requirements[1] with { EvidenceRefs = [Guid.NewGuid()] };                      // fonte fuori dalla revisione
        var target = input.Target with { Requirements = requirements };

        var findings = DocumentCoverage.Check(input with { Target = target, Ai = target });

        Assert.Contains(findings, f => f.Code == "item_without_fragment" && f.ItemId == requirements[0].Id);
        Assert.Contains(findings, f => f.Code == "item_source_unresolved" && f.ItemId == requirements[1].Id);
        Assert.Contains(findings, f => f.Code == "evidence_source_mismatch" && f.Alias == "E2");
        Assert.All(findings, f => Assert.Equal(FindingCategory.DeterministicError, f.Category));
    }

    [Fact]
    public void A_known_contradiction_must_be_represented_in_a_conflict_that_holds_both_items()
    {
        var input = Build(new Spec("Il cliente A emette al massimo 5 ordini."), new Spec("Il cliente A emette almeno 10 ordini.")) with
        {
            Relations = [new(Guid.Empty, Guid.Empty, EvidenceRelationType.Contradicts)],
        };
        input = input with { Relations = [new(input.Evidence[0].EvidenceId, input.Evidence[1].EvidenceId, EvidenceRelationType.Contradicts)] };

        var unrepresented = DocumentCoverage.Check(input);
        Assert.Equal("known_relation_unrepresented", Assert.Single(unrepresented).Code);
        Assert.True(unrepresented[0].Blocking);

        var items = input.Target.Requirements;
        var conflict = new DocumentAnalysisConflict
        {
            Id = Guid.NewGuid(), Description = "Ordini del cliente A", Status = AnalysisConflictStatus.Open, Resolution = null, Alternatives = items,
        };
        var target = input.Target with { Requirements = [], Conflicts = [conflict] };

        Assert.Empty(DocumentCoverage.Check(input with { Target = target, Ai = target }));
    }

    [Fact]
    public void A_draft_that_omits_the_marker_of_a_confirmed_item_source_is_a_deterministic_error_and_missing_words_are_only_a_risk()
    {
        var input = Build(new Spec("Il cliente emette ordini giornalieri verso magazzini regionali."));

        var findings = DocumentCoverage.Check(input with { DraftMarkdown = "# Titolo\n\nTesto qualsiasi non correlato." });

        Assert.Equal(FindingCategory.DeterministicError, Assert.Single(findings, f => f.Code == "revision_source_not_cited").Category);
        Assert.Equal(FindingCategory.SemanticRisk, Assert.Single(findings, f => f.Code == "revision_item_missing_in_document").Category);
        var marker = input.Target.Sources[0].Id;
        Assert.DoesNotContain(
            DocumentCoverage.Check(input with { DraftMarkdown = $"Il cliente emette ordini giornalieri verso magazzini regionali. [fonte:{marker}]" }),
            f => f.Code is "revision_source_not_cited" or "revision_item_missing_in_document");
    }

    // ---------------------------------------------------------------- giudizi PM

    [Fact]
    public void Pm_edits_are_assessments_never_errors_and_never_blocking()
    {
        var ai = Build(new Spec("Primo requisito del cliente A."), new Spec("Secondo requisito del cliente A."), new Spec("Terzo requisito del cliente A."));
        var requirements = ai.Ai.Requirements;
        var conflict = new DocumentAnalysisConflict
        {
            Id = Guid.NewGuid(), Description = "Scelta", Status = AnalysisConflictStatus.Open, Resolution = null, Alternatives = [],
        };
        var aiContent = ai.Ai with { Requirements = [requirements[0], requirements[1]], Conflicts = [conflict with { Alternatives = [requirements[2]] }] };
        var resolved = conflict with
        {
            Status = AnalysisConflictStatus.Resolved,
            Alternatives = [requirements[2]],
            Resolution = new AnalysisConflictResolution { SelectedAlternativeIds = [requirements[2].Id], Reason = "deciso dal PM", EvidenceRefs = [aiContent.Sources[0].Id] },
        };
        var target = aiContent with { Requirements = [requirements[0] with { Text = "Primo requisito riscritto dal PM." }], Conflicts = [resolved] };

        var findings = DocumentCoverage.Check(ai with { Ai = aiContent, Target = target });

        Assert.Equal(["conflict_resolved_by_pm", "item_removed_by_pm", "item_rewritten_by_pm"], findings.Select(f => f.Code).Order());
        Assert.All(findings, f =>
        {
            Assert.Equal(FindingCategory.PmAssessment, f.Category);
            Assert.False(f.Blocking);
        });
    }

    // ---------------------------------------------------------------- limiti, hash, nessun «complete»

    [Fact]
    public void The_report_declares_its_limits_and_exposes_no_authoritative_complete_boolean()
    {
        Assert.Equal("coverage-2", DocumentCoverage.CheckerVersion);
        Assert.Contains(DocumentCoverage.Limits, l => l.Contains("fedele", StringComparison.Ordinal));
        foreach (var type in new[] { typeof(DocumentCoverageSummary), typeof(DocumentCoverageFinding), typeof(DocumentCoverageContent) })
        {
            Assert.DoesNotContain(type.GetProperties(), p =>
                p.PropertyType == typeof(bool) && Regex.IsMatch(p.Name, "complet|valid|ok|pass|verified|success", RegexOptions.IgnoreCase) && p.Name != nameof(DocumentCoverageFinding.Blocking));
        }
    }

    [Fact]
    public void The_input_hash_covers_snapshot_parse_extraction_revision_and_draft_and_ignores_link_order()
    {
        var baseline = new DocumentCoverageHashes("s", "v", ["p1"], ["x1"], "r", "d");
        var links = new[] { (Guid.NewGuid(), Guid.NewGuid()), (Guid.NewGuid(), Guid.NewGuid()) };
        var hash = DocumentCoverage.InputHash(baseline, links);

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, DocumentCoverage.InputHash(baseline, [links[1], links[0]]));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { SourceSnapshotHash = "s2" }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { ParseHashes = ["p2"] }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { ExtractionHashes = ["x2"] }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { RevisionHash = "r2" }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline with { DraftHash = "d2" }, links));
        Assert.NotEqual(hash, DocumentCoverage.InputHash(baseline, [links[0]]));
    }

    [Fact]
    public void The_checker_is_deterministic_and_every_code_in_its_source_is_listed_here_with_its_category()
    {
        var input = Build(new Spec("Il cliente A emette ordini."), new Spec("Il magazzino B chiude.", Linked: false));
        Assert.Equal(DocumentCoverage.Check(input).Select(f => f.Message), DocumentCoverage.Check(input).Select(f => f.Message));

        var source = File.ReadAllText(Locate("src/bProjects.App/Evidence/EvidenceCoverage.cs"));
        var found = Regex.Matches(source, @"Doc\(FindingCategory\.(?<cat>\w+),\s*""(?<code>[a-z_]+)""");
        var fromSource = found.GroupBy(m => m.Groups["code"].Value).ToDictionary(g => g.Key, g => g.Select(m => Enum.Parse<FindingCategory>(m.Groups["cat"].Value)).Distinct().Single());
        Assert.Equal(Regex.Matches(source, @"Doc\(FindingCategory\.").Count, found.Count);
        Assert.Equal(Codes.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"), fromSource.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}"));
    }

    // ---------------------------------------------------------------- supporto

    private sealed record Spec(
        string Statement, EvidencePolarity Polarity = EvidencePolarity.Affirmed, string[]? Qualifiers = null, bool Linked = true, string? ItemText = null);

    private static DocumentCoverageInput Build(params Spec[] specs)
    {
        var evidence = new List<DocumentCoverageEvidence>();
        var texts = new Dictionary<Guid, string>();
        var sources = new List<DocumentAnalysisSource>();
        var items = new List<DocumentAnalysisItem>();
        var links = new List<(Guid, Guid)>();
        var document = Guid.NewGuid();
        var (version, preparation, artifact) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            var (evidenceId, block, source) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            texts[block] = spec.Statement;
            evidence.Add(new(evidenceId, $"E{i + 1}", EvidenceOriginKind.Document, null, EvidenceKind.Requirement, spec.Polarity, spec.Statement,
                [.. (spec.Qualifiers ?? []).Select(q => new CoverageQualifier("Maximum", q))], null, null,
                [new(source, null, block, 0, spec.Statement.Length, spec.Statement)]));
            sources.Add(new DocumentAnalysisSource
            {
                Id = source, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = document,
                DocumentVersionId = version, PreparationId = preparation, ArtifactId = artifact, BlockId = block, Start = 0, End = spec.Statement.Length, Quote = spec.Statement,
            });
            if (spec.Linked)
            {
                var item = new DocumentAnalysisItem
                {
                    Id = Guid.NewGuid(), Text = spec.ItemText ?? spec.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [source],
                };
                items.Add(item);
                links.Add((item.Id, evidenceId));
            }
        }

        var content = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), SourceCutoffSequence = 0,
            ValidationState = AnalysisContentState.Unvalidated, Sources = sources, Goals = [],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
        };
        return new DocumentCoverageInput(evidence, links, content, content, null, texts, [], []);
    }

    private static string Locate(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var backend = Path.Combine(dir.FullName, "backend", relative);
            if (File.Exists(backend))
            {
                return backend;
            }
        }

        throw new FileNotFoundException(relative);
    }
}

/// <summary>
/// T062 (PG) — API reale + PostgreSQL: coverage-2 sul report di analisi con revisione 1.1 e GET di preparazione/coverage; i report coverage-v1
/// storici restano leggibili e immutati. Dati documentali scritti direttamente (preparazione, blocchi, porzioni, evidenze, selezione, revisione):
/// nessun modello, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentCoverageEndpointTests : ArchiveTestBase
{
    private const string TextA = "Il cliente A emette al massimo 5 ordini al giorno.";
    private const string TextB = "Il magazzino B chiude alle 18.";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    // ================================================================ GET preparazione/coverage

    [Fact]
    public async Task The_preparation_coverage_reports_expected_processed_no_evidence_and_missing_portions_and_hashes_without_side_effects()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed, PortionStatus.Completed, PortionStatus.Pending], withAnalysis: false);
        var jobs = await Count("SELECT count(*) FROM \"DocumentJobs\"");
        var reports = await Count("SELECT count(*) FROM \"EvidenceCoverageReports\"");
        var url = $"/api/projects/{w.Project}/documents/{w.Document}/versions/{w.Version}/preparations/latest/coverage";

        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("coverage-2", body.GetProperty("checkerVersion").GetString());
        Assert.Equal(w.Preparation, body.GetProperty("preparationId").GetGuid());
        var portions = body.GetProperty("portions");
        Assert.Equal((3, 2, 1, 1), (portions.GetProperty("expected").GetInt32(), portions.GetProperty("processed").GetInt32(), portions.GetProperty("noEvidence").GetInt32(), portions.GetProperty("missing").GetInt32()));
        Assert.Equal(w.PendingPortion, Assert.Single(portions.GetProperty("missingPortionIds").EnumerateArray()).GetGuid());
        var hashes = body.GetProperty("hashes");
        Assert.Equal(new string('1', 64), hashes.GetProperty("manifestHash").GetString());
        Assert.Equal(w.ParseSha, hashes.GetProperty("parseSha256").GetString());
        Assert.Equal(w.EvidenceVersion, hashes.GetProperty("evidenceVersionId").GetGuid());
        var finding = Assert.Single(body.GetProperty("findings").EnumerateArray());
        Assert.Equal(("DeterministicError", "portion_not_processed", true), (finding.GetProperty("category").GetString(), finding.GetProperty("code").GetString(), finding.GetProperty("blocking").GetBoolean()));
        Assert.DoesNotMatch("(?i)\"(is)?complete", body.GetRawText());

        Assert.Equal(jobs, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Equal(reports, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\""));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{await NewProject(client)}/documents/{w.Document}/versions/{w.Version}/preparations/latest/coverage", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{w.Project}/documents/{w.Document}/versions/{w.Version}/preparations/99/coverage", Ct)).StatusCode);
    }

    // ================================================================ coverage-2 sul report di analisi

    [Fact]
    public async Task A_known_omission_is_a_blocking_deterministic_error_in_the_persisted_coverage_2_report_with_exact_input_hashes()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], linkB: false);

        var created = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-2", report.GetProperty("checkerVersion").GetString());
        var counts = report.GetProperty("counts");
        Assert.Equal((1, 0, 0), (counts.GetProperty("deterministicErrors").GetInt32(), counts.GetProperty("semanticRisks").GetInt32(), counts.GetProperty("pmAssessments").GetInt32()));
        var finding = Assert.Single(report.GetProperty("findings").EnumerateArray());
        Assert.Equal(("evidence_unrepresented", true), (finding.GetProperty("code").GetString(), finding.GetProperty("blocking").GetBoolean()));
        Assert.Equal(w.EvidenceB, finding.GetProperty("evidenceId").GetGuid());
        var summary = report.GetProperty("summary");
        Assert.Equal((2, 1, 1), (summary.GetProperty("expectedEvidence").GetInt32(), summary.GetProperty("representedEvidence").GetInt32(), summary.GetProperty("blockingFindings").GetInt32()));
        var hashes = report.GetProperty("hashes");
        Assert.Equal(await Scalar<string>("SELECT \"Hash\" FROM \"SourceSnapshots\" WHERE \"Id\" = (SELECT \"SourceSnapshotId\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r)", ("r", w.Run)), hashes.GetProperty("sourceSnapshotHash").GetString());
        Assert.Equal(w.ParseSha, Assert.Single(hashes.GetProperty("parseHashes").EnumerateArray()).GetString());
        Assert.Matches("^[0-9a-f]{64}$", hashes.GetProperty("revisionHash").GetString()!);
        Assert.Matches("^[0-9a-f]{64}$", report.GetProperty("inputHash").GetString()!);
        Assert.Single(report.GetProperty("preparations").EnumerateArray());
        Assert.DoesNotMatch("(?i)\"(is)?complete", report.GetRawText());

        var again = await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(report.GetRawText(), (await Json(again)).GetRawText());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @a", ("a", w.Run)));
        var detail = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w) + "/" + report.GetProperty("id").GetGuid(), Ct);
        Assert.Equal(report.GetRawText(), detail.GetRawText());
    }

    [Fact]
    public async Task A_semantically_wrong_gold_with_every_evidence_linked_is_reported_as_a_semantic_risk_apart_from_the_errors()
    {
        using var client = await App.LoginAsync();
        var w = await SeedAsync(client, [PortionStatus.Completed], itemTextA: "Il cliente A emette al massimo 8 ordini al giorno.");

        var report = await Json(await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct));

        var counts = report.GetProperty("counts");
        Assert.Equal((0, 1, 0), (counts.GetProperty("deterministicErrors").GetInt32(), counts.GetProperty("semanticRisks").GetInt32(), counts.GetProperty("pmAssessments").GetInt32()));
        var summary = report.GetProperty("summary");
        Assert.Equal((2, 2, 0), (summary.GetProperty("expectedEvidence").GetInt32(), summary.GetProperty("representedEvidence").GetInt32(), summary.GetProperty("blockingFindings").GetInt32()));
        var finding = Assert.Single(report.GetProperty("findings").EnumerateArray());
        Assert.Equal(("SemanticRisk", "qualifier_changed", false), (finding.GetProperty("category").GetString(), finding.GetProperty("code").GetString(), finding.GetProperty("blocking").GetBoolean()));
        Assert.Equal("medium", finding.GetProperty("confidence").GetString());
    }

    // ================================================================ storico coverage-v1

    [Fact]
    public async Task Historic_coverage_v1_reports_stay_readable_and_byte_identical_next_to_coverage_2_and_001_analyses_keep_using_v1()
    {
        using var client = await App.LoginAsync();
        var old = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var oldUrl = $"/api/projects/{old.Project}/analyses/{old.Analysis}/coverage-reports";
        var v1 = await Json(await client.PostAsJsonAsync(oldUrl, new { }, Ct));
        Assert.Equal("coverage-v1", v1.GetProperty("checkerVersion").GetString());
        Assert.False(v1.TryGetProperty("summary", out _));
        Assert.False(v1.TryGetProperty("hashes", out _));
        var digest = await SqlDigest("coverage-v1");

        var w = await SeedAsync(client, [PortionStatus.Completed]);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(ReportsUrl(w), new { }, Ct)).StatusCode);

        Assert.Equal(digest, await SqlDigest("coverage-v1"));
        var detail = await client.GetFromJsonAsync<JsonElement>(oldUrl + "/" + v1.GetProperty("id").GetGuid(), Ct);
        Assert.Equal(v1.GetRawText(), detail.GetRawText());
        var stored = await Scalar<string>("SELECT \"Report\"::text FROM \"EvidenceCoverageReports\" WHERE \"CheckerVersion\" = 'coverage-v1'");
        Assert.Equal("coverage-v1", EvidenceJson.Deserialize<CoverageReportContent>(stored).CheckerVersion);
        var list = await client.GetFromJsonAsync<JsonElement>(oldUrl, Ct);
        Assert.Equal("coverage-v1", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("checkerVersion").GetString());
        var newList = await client.GetFromJsonAsync<JsonElement>(ReportsUrl(w), Ct);
        Assert.Equal("coverage-2", Assert.Single(newList.GetProperty("items").EnumerateArray()).GetProperty("checkerVersion").GetString());
    }

    private Task<string> SqlDigest(string checker) =>
        Scalar<string>("SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '-') FROM \"EvidenceCoverageReports\" t WHERE \"CheckerVersion\" = @c", ("c", checker));

    // ================================================================ seed

    private sealed record World(
        Guid Project, Guid Document, Guid Version, Guid Preparation, Guid Run, Guid EvidenceA, Guid EvidenceB, Guid EvidenceVersion, Guid PendingPortion, string ParseSha);

    private static string ReportsUrl(World w) => $"/api/projects/{w.Project}/analyses/{w.Run}/coverage-reports";

    private async Task<World> SeedAsync(HttpClient client, PortionStatus[] portions, bool linkB = true, string? itemTextA = null, bool withAnalysis = true)
    {
        var project = await NewProject(client);
        var created = await Json(await Upload(client, project, Text("regole"), "regole.txt", displayName: "regole"));
        var (document, version) = (created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        var (artifactId, blockA, blockB, sourceA, sourceB, evidenceA, evidenceB, evidenceVersion) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var sha = new string('a', 64);
        var pending = Guid.Empty;
        Guid preparationId;
        Guid user;
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == version, Ct);
            preparationId = preparation.Id;
            user = await db.Projects.AsNoTracking().Where(p => p.Id == project).Select(p => p.CreatedBy).SingleAsync(Ct);
            var now = DateTimeOffset.UtcNow;
            db.ParsedArtifacts.Add(new ParsedArtifact
            {
                Id = artifactId, ProjectId = project, PreparationId = preparation.Id, JobId = preparation.ParseJobId!.Value, FencingToken = 1, SchemaVersion = "parsed-manifest-1",
                StorageKey = "k" + artifactId.ToString("N"), Sha256 = sha, ByteLength = 10, ParserConfigHash = sha, BlockCount = 2, IssueCount = 0, CreatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            DocumentBlock Block(Guid id, int ordinal, string text) => new()
            {
                Id = id, ProjectId = project, ArtifactId = artifactId, Ordinal = ordinal, Ref = $"b{ordinal}", Type = BlockType.Paragraph, CanonicalText = text, TextHash = sha, HeadingPathJson = "[]",
            };
            db.DocumentBlocks.AddRange(Block(blockA, 0, TextA), Block(blockB, 1, TextB));
            await db.SaveChangesAsync(Ct);
            for (var i = 0; i < portions.Length; i++)
            {
                var id = Guid.NewGuid();
                pending = portions[i] == PortionStatus.Pending ? id : pending;
                db.DocumentPortions.Add(new DocumentPortion
                {
                    Id = id, ProjectId = project, PreparationId = preparation.Id, Ordinal = i, RangesJson = "[]", ContextRangesJson = "[]", TextHash = sha, Status = portions[i],
                    ResultHash = portions[i] == PortionStatus.Completed ? sha : null, NoEvidenceReason = i == 1 && portions[i] == PortionStatus.Completed ? "solo intestazione" : null,
                });
            }

            var job = Guid.NewGuid();
            db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
            {
                Id = job, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
                Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
            });
            db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
            {
                Id = evidenceVersion, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
                Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 2, CreatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            db.Evidences.Add(Evidence(project, evidenceVersion, preparation.Id, evidenceA, 1, TextA, EvidenceKind.Requirement,
                Content(document, version, preparation.Id, artifactId, blockA, sourceA, TextA, [new DocumentEvidenceQualifier { Type = QualifierType.Maximum, Text = "al massimo 5 ordini" }])));
            db.Evidences.Add(Evidence(project, evidenceVersion, preparation.Id, evidenceB, 2, TextB, EvidenceKind.Requirement,
                Content(document, version, preparation.Id, artifactId, blockB, sourceB, TextB, [])));
            db.EvidenceDocumentStates.Add(new EvidenceDocumentState
            {
                DocumentVersionId = version, ProjectId = project, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = evidenceVersion, Revision = 1, UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            preparation.Status = PreparationStatus.Ready;
            preparation.CurrentEvidenceVersionId = evidenceVersion;
            preparation.ManifestHash = new string('1', 64);
            await db.SaveChangesAsync(Ct);
            await db.ProjectDocuments.Where(d => d.Id == document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, version), Ct);
        }

        var run = Guid.Empty;
        if (withAnalysis)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var started = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            run = (await Json(started)).GetProperty("id").GetGuid();
            await Execute(
                "UPDATE \"ProcessingRuns\" SET \"Status\" = 'Running', \"LeaseOwner\" = 'test-owner', \"FencingToken\" = 1, \"AttemptCount\" = 1, \"StartedAt\" = now(), \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r",
                ("r", run));
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var frozen = await new EvidenceLedgerStore(db).FreezeDocumentSelectionAsync(new AnalysisRunClaim(run, "test-owner", 1, 1), Ct);
            Assert.True(frozen.Outcome is SelectionOutcome.Frozen, frozen.Outcome.ToString());
            var (itemA, itemB) = (Guid.NewGuid(), Guid.NewGuid());
            DocumentAnalysisSource Source(Guid id, Guid block, string text) => new()
            {
                Id = id, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = document,
                DocumentVersionId = version, PreparationId = preparationId, ArtifactId = artifactId, BlockId = block, Start = 0, End = text.Length, Quote = text,
            };
            var items = new List<DocumentAnalysisItem>
            {
                new() { Id = itemA, Text = itemTextA ?? TextA, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [sourceA] },
            };
            if (linkB)
            {
                items.Add(new() { Id = itemB, Text = TextB, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [sourceB] });
            }

            var content = new DocumentAnalysisContent
            {
                SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = run, ProjectId = project, SourceCutoffSequence = 0, ValidationState = AnalysisContentState.Unvalidated,
                Sources = [Source(sourceA, blockA, TextA), Source(sourceB, blockB, TextB)], Goals = [],
                ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
                Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
            };
            db.AnalysisRevisions.Add(new AnalysisRevision
            {
                Id = Guid.NewGuid(), AnalysisId = run, Version = 1, Origin = AnalysisRevisionOrigin.Ai, CreatedBy = user, CreatedAt = DateTimeOffset.UtcNow,
                SchemaVersion = AnalysisRevision.DocumentSchemaVersion, ContentJson = DocumentAnalysisContentJson.Serialize(content), ValidationState = AnalysisValidationState.Unvalidated,
            });
            await db.SaveChangesAsync(Ct);
            db.AnalysisEvidenceLinks.Add(new AnalysisEvidenceLink { RunId = run, ItemId = itemA, EvidenceId = evidenceA, ProjectId = project });
            if (linkB)
            {
                db.AnalysisEvidenceLinks.Add(new AnalysisEvidenceLink { RunId = run, ItemId = itemB, EvidenceId = evidenceB, ProjectId = project });
            }

            await db.SaveChangesAsync(Ct);
        }

        return new(project, document, version, preparationId, run, evidenceA, evidenceB, evidenceVersion, pending, sha);
    }

    private static EvidenceItem Evidence(Guid project, Guid version, Guid preparation, Guid id, int ordinal, string statement, EvidenceKind kind, DocumentEvidenceContent content) => new()
    {
        Id = id, VersionId = version, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation, Ordinal = ordinal, Key = $"e{ordinal}", Kind = kind,
        Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement, Content = DocumentEvidenceCodec.EncodeContent(content),
    };

    private static DocumentEvidenceContent Content(
        Guid document, Guid version, Guid preparation, Guid artifact, Guid block, Guid source, string text, IReadOnlyList<DocumentEvidenceQualifier> qualifiers) => new()
    {
        SchemaVersion = DocumentEvidenceContent.SchemaVersionValue, KindRationale = null,
        Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
        Qualifiers = qualifiers,
        Sources =
        [
            new PersistedSource
            {
                SourceId = source, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0, MessageId = null, Sequence = null,
                DocumentId = document, DocumentVersionId = version, PreparationId = preparation, ArtifactId = artifact, BlockId = block, Start = 0, End = text.Length, Quote = text,
            },
        ],
    };
}
