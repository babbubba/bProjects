using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// T052 - prove di coerenza delle fixture di accettazione e del verificatore (senza PostgreSQL ne' DGX): le annotazioni sono
/// auto-consistenti, l'analisi/bozza "ideali" passano validatori e verificatore, e ogni difetto tipico (omissione, negazione invertita,
/// lacuna trasformata in fatto, conflitto risolto o scelto in silenzio, fonte errata o di altro progetto, invenzione, ripetizione,
/// bozza presentata come approvata) viene rilevato. Provano le regole di verifica, NON la qualita' di un modello.
/// </summary>
public sealed class AcceptanceFixtureTests
{
    public static IEnumerable<object[]> Projects() =>
        AcceptanceCases.Nominal.SelectMany(c => c.Projects.Select(p => new object[] { c.Id, p.Key }));

    internal static ProjectScript Script(string caseId, string key) =>
        AcceptanceCases.Nominal.Single(c => c.Id == caseId).Projects.Single(p => p.Key == key);

    internal sealed record Ids(Guid Project, Guid Analysis, Guid[] Users, Dictionary<Guid, int> Index, HashSet<Guid> All, AnalysisValidationContext Context);

    internal static Ids NewIds(ProjectScript script)
    {
        var project = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var users = script.Messages.Select(_ => Guid.NewGuid()).ToArray();
        var context = new AnalysisValidationContext(
            project, analysis, users.Length,
            [.. users.Select((id, i) => new AnalysisMessageRef(id, project, AnalysisMessageRole.User, i + 1))],
            new HashSet<Guid>());
        return new(project, analysis, users, users.Select((id, i) => (id, i + 1)).ToDictionary(x => x.id, x => x.Item2), [.. users], context);
    }

    internal static (JsonObject Json, AnalysisContent Content, Ids Ids) Ideal(ProjectScript script, OracleOptions? options = null)
    {
        var ids = NewIds(script);
        var json = AcceptanceOracle.BuildAnalysis(script, ids.Project, ids.Analysis, ids.Users.Length, ids.Users, options);
        return (json, AnalysisContentJson.Deserialize(json.ToJsonString()), ids);
    }

    /// <summary>Revisione dopo la correzione del PM (modifica, integrazione, assunzione, risoluzione): ripete cio' che fa il server con la fonte PmRevision.</summary>
    internal static AnalysisContent Corrected(AnalysisContent ideal, ProjectScript script, Guid pmRevision, AnalysisContentState state)
    {
        var pm = Guid.NewGuid();
        var c = ideal;
        var correction = script.Correction;
        var edit = script.Facts.Single(f => f.Key == correction.EditKey);
        foreach (var section in edit.Accept)
        {
            var items = AcceptanceChecker.Items(c, section);
            if (items.Any(i => AcceptanceChecker.Matches(AcceptanceChecker.Norm(i.Text), edit.Match)))
            {
                c = With(c, section, [.. items.Select(i => AcceptanceChecker.Matches(AcceptanceChecker.Norm(i.Text), edit.Match)
                    ? i with { Text = correction.EditedText, EvidenceRefs = [.. i.EvidenceRefs, pm] } : i)]);
                break;
            }
        }

        foreach (var add in correction.Additions)
        {
            c = With(c, add.Section, [.. AcceptanceChecker.Items(c, add.Section), new AnalysisItem { Id = Guid.NewGuid(), Text = add.Text, Classification = add.Class, EvidenceRefs = [pm] }]);
        }

        if (correction.Resolve is { } resolve)
        {
            var spec = script.Conflicts.Single(k => k.Key == resolve.ConflictKey);
            c = c with
            {
                Conflicts = [.. c.Conflicts.Select(k => k.Alternatives.Any(a => AcceptanceChecker.Matches(AcceptanceChecker.Norm(a.Text), [spec.AltMatch[0]]))
                    ? k with
                    {
                        Status = AnalysisConflictStatus.Resolved,
                        Resolution = new AnalysisConflictResolution
                        {
                            SelectedAlternativeIds = [k.Alternatives.First(a => AcceptanceChecker.Matches(AcceptanceChecker.Norm(a.Text), [spec.AltMatch[resolve.Alternative]])).Id],
                            Reason = resolve.Reason,
                            EvidenceRefs = [pm],
                        },
                    }
                    : k)],
            };
        }

        return c with
        {
            ValidationState = state,
            Sources = [.. c.Sources, new AnalysisSource { Id = pm, Kind = AnalysisSourceKind.PmRevision, MessageId = null, MessageRole = null, RevisionId = pmRevision }],
        };
    }

    private static AnalysisContent With(AnalysisContent c, Sec section, IReadOnlyList<AnalysisItem> items) => section switch
    {
        Sec.Goals => c with { Goals = items },
        Sec.Overview => c with { ContextAndScope = new() { Overview = items, Included = c.ContextAndScope.Included, Excluded = c.ContextAndScope.Excluded } },
        Sec.Included => c with { ContextAndScope = new() { Overview = c.ContextAndScope.Overview, Included = items, Excluded = c.ContextAndScope.Excluded } },
        Sec.Excluded => c with { ContextAndScope = new() { Overview = c.ContextAndScope.Overview, Included = c.ContextAndScope.Included, Excluded = items } },
        Sec.Requirements => c with { Requirements = items },
        Sec.Flows => c with { OperationalFlows = items },
        Sec.Constraints => c with { Constraints = items },
        Sec.Dependencies => c with { Dependencies = items },
        Sec.Assumptions => c with { Assumptions = items },
        Sec.Open => c with { OpenQuestions = items },
        _ => c,
    };

    // ------------------------------------------------------------------------------------------------ struttura dei casi

    [Fact]
    public void There_are_five_nominal_cases_of_five_pm_messages_and_a_separate_large_context_case()
    {
        Assert.Equal(
            ["N1-frammenti", "N2-negazioni", "N3-lacune", "N4-conflitti", "N5-progetti-distinti"],
            AcceptanceCases.Nominal.Select(c => c.Id));
        Assert.All(AcceptanceCases.Nominal, c => Assert.All(c.Projects, p => Assert.Equal(5, p.Messages.Length))); // SC-001: cinque messaggi preparati
        Assert.Equal([1, 1, 1, 1, 2], AcceptanceCases.Nominal.Select(c => c.Projects.Length));
        Assert.Equal(2, AcceptanceCases.Nominal[4].Projects.Select(p => p.Name).Distinct().Count());
        Assert.Contains(AcceptanceCases.Nominal, c => c.Projects.Any(p => p.Conflicts.Length >= 2));                      // conflitti
        Assert.Contains(AcceptanceCases.Nominal, c => c.Projects.Any(p => p.Facts.Count(f => f.Section == Sec.Open) >= 3)); // lacune
        Assert.Contains(AcceptanceCases.Nominal, c => c.Projects.Any(p => p.Facts.Count(f => f.Section == Sec.Excluded) >= 4)); // esclusioni

        var exploratory = AcceptanceCases.Exploratory;
        Assert.True(exploratory.Exploratory);
        Assert.DoesNotContain(exploratory, AcceptanceCases.Nominal);
        var messages = Assert.Single(exploratory.Projects).Messages;
        Assert.Equal(AcceptanceCases.ExploratoryMessageCount, messages.Length);
        Assert.InRange(messages.Sum(m => m.Length), 170_000, 200_000); // ~185.000 caratteri, come nel primo test manuale
        Assert.All(messages, m => Assert.InRange(m.Length, 17_000, 20_000));
        Assert.Contains("Aurora", messages[0]);
        Assert.Contains("a marzo", messages[2]);
        Assert.Contains("a settembre", messages[6]);
        Assert.Contains("restare in Italia", messages[9]);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Annotations_are_consistent_with_the_messages_and_with_each_other(string caseId, string key)
    {
        var s = Script(caseId, key);
        Assert.Equal(s.Facts.Length, s.Facts.Select(f => f.Key).Distinct().Count());
        Assert.All(s.Facts, f =>
        {
            Assert.NotEmpty(f.Match);
            Assert.All(f.From, i => Assert.InRange(i, 1, s.Messages.Length));
            Assert.True(AcceptanceChecker.Matches(AcceptanceChecker.Norm(f.Text), f.Match), $"{f.Key}: il testo ideale non soddisfa le proprie espressioni");
            Assert.True(f.Section != Sec.Excluded || Regex.IsMatch(AcceptanceChecker.Norm(f.Text), @"\b(non|esclus\w*|fuori|resta fuori)\b"), $"{f.Key}: l'esclusione ideale deve essere negata");
        });
        Assert.Contains(s.Facts, f => f.Key == s.Correction.EditKey);
        var edit = s.Facts.Single(f => f.Key == s.Correction.EditKey);
        Assert.True(AcceptanceChecker.Matches(AcceptanceChecker.Norm(s.Correction.EditedText), edit.Match), "la correzione deve restare compatibile con l'attesa");
        Assert.All(s.Correction.Additions, a => Assert.True(AcceptanceChecker.Matches(AcceptanceChecker.Norm(a.Text), a.Match), $"{a.Key}: testo e attese incoerenti"));
        Assert.All(s.Correction.Additions, a => Assert.Equal(a.Section == Sec.Assumptions, a.Class == AnalysisClassification.Assumed));
        if (s.Correction.Resolve is { } r)
        {
            var spec = Assert.Single(s.Conflicts, k => k.Key == r.ConflictKey);
            Assert.InRange(r.Alternative, 0, spec.AltText.Length - 1);
            Assert.True(AcceptanceChecker.Matches(AcceptanceChecker.Norm(r.Reason), [r.ReasonMatch]));
        }

        Assert.All(s.Conflicts, k =>
        {
            Assert.Equal(2, k.AltText.Length);
            for (var n = 0; n < 2; n++)
            {
                Assert.True(AcceptanceChecker.Matches(AcceptanceChecker.Norm(k.AltText[n]), [k.AltMatch[n]]), $"{k.Key}: alternativa {n + 1}");
                Assert.All(k.AltFrom[n], i => Assert.InRange(i, 1, s.Messages.Length));
            }
        });
        Assert.All(s.Forbids.Where(f => f.ConflictKey is not null), f => Assert.Contains(s.Conflicts, k => k.Key == f.ConflictKey));

        // Il dialogo non contiene stime ne' le "invenzioni" vietate: sono le attese a doverle poter rilevare, non i messaggi a contenerle.
        Assert.All(s.Messages, m => Assert.False(AcceptanceChecker.HasEstimate(m)));
        foreach (var forbid in s.Forbids.Where(f => f.Mode == ForbidMode.Anywhere))
        {
            Assert.All(s.Messages, m => Assert.False(AcceptanceChecker.Matches(AcceptanceChecker.Norm(m), [forbid.Pattern]), $"{forbid.Key}: il dialogo contiene il testo vietato"));
            Assert.True(AcceptanceChecker.Matches(AcceptanceChecker.Norm(forbid.Sample), [forbid.Pattern]), $"{forbid.Key}: l'esempio non viola il divieto");
        }

        foreach (var forbid in s.Forbids.Where(f => f.Mode == ForbidMode.Affirmed))
        {
            var sample = AcceptanceChecker.Norm(forbid.Sample);
            Assert.True(AcceptanceChecker.Matches(sample, [forbid.Pattern]), $"{forbid.Key}: l'esempio non viola il divieto");
            Assert.False(Regex.IsMatch(sample, @"\b(non|niente|nessun\w*|senza|esclus\w*|fuori|no|neanche)\b"), $"{forbid.Key}: l'esempio contiene una negazione");
        }
    }

    [Fact]
    public void In_the_distinct_projects_case_each_projects_forbidden_markers_belong_to_the_other_project()
    {
        var aurora = Script("N5-progetti-distinti", "aurora");
        var boreale = Script("N5-progetti-distinti", "boreale");
        var toA = aurora.Forbids.Single(f => f.Key == "progetto-boreale").Pattern;
        var toB = boreale.Forbids.Single(f => f.Key == "progetto-aurora").Pattern;
        Assert.Contains(boreale.Messages, m => AcceptanceChecker.Matches(AcceptanceChecker.Norm(m), [toA]));
        Assert.Contains(aurora.Messages, m => AcceptanceChecker.Matches(AcceptanceChecker.Norm(m), [toB]));
        // decisioni opposte sugli stessi temi: SMS, integrazione, accesso
        Assert.Contains(aurora.Facts, f => f.Key == "no-sms" && f.Section == Sec.Excluded);
        Assert.Contains(boreale.Facts, f => f.Key == "notifiche" && f.Section == Sec.Requirements);
        Assert.Contains(aurora.Facts, f => f.Key == "no-integrazione" && f.Section == Sec.Excluded);
        Assert.Contains(boreale.Facts, f => f.Key == "integrazione" && f.Section == Sec.Dependencies);
    }

    [Fact]
    public void The_e2e_data_file_is_the_serialization_of_the_fixtures_so_that_the_browser_spec_and_the_backend_share_one_set_of_cases()
    {
        var path = Path.Combine(RepoRoot(), "tests", "e2e", "support", "acceptance-cases.json");
        var expected = AcceptanceE2eData.Serialize();
        if (Environment.GetEnvironmentVariable("BPROJECTS_ACCEPTANCE_REGENERATE") == "1")
        {
            File.WriteAllText(path, expected, new System.Text.UTF8Encoding(false));
        }

        Assert.True(File.Exists(path), $"{path} manca: rigenerare con BPROJECTS_ACCEPTANCE_REGENERATE=1");
        Assert.Equal(expected.ReplaceLineEndings("\n"), File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")) && Directory.Exists(Path.Combine(dir.FullName, "specs")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Radice del repository non trovata.");
    }

    // ------------------------------------------------------------------------------------------------ analisi e bozza ideali

    [Theory]
    [MemberData(nameof(Projects))]
    public void The_ideal_analysis_is_accepted_by_the_server_validator_and_meets_every_annotated_expectation(string caseId, string key)
    {
        var s = Script(caseId, key);
        var (json, content, ids) = Ideal(s);

        var validated = AnalysisContentValidator.ValidateText(json.ToJsonString(), ids.Context, AnalysisActorKind.Ai);
        Assert.True(validated.Accepted, string.Join("; ", validated.Violations));

        var findings = AcceptanceChecker.CheckAnalysis(s, content, ids.Index, ids.All);
        Assert.True(findings.Passed, string.Join("; ", findings.Failures));
    }

    [Fact]
    public void The_large_context_expectations_hold_for_the_ideal_analysis()
    {
        var s = Assert.Single(AcceptanceCases.Exploratory.Projects);
        var (json, content, ids) = Ideal(s);

        Assert.True(AnalysisContentValidator.ValidateText(json.ToJsonString(), ids.Context, AnalysisActorKind.Ai).Accepted);
        var findings = AcceptanceChecker.CheckAnalysis(s, content, ids.Index, ids.All);
        Assert.True(findings.Passed, string.Join("; ", findings.Failures));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void The_pm_correction_is_a_valid_edit_and_the_ideal_draft_passes_the_format_validator_and_the_checker_before_and_after_it(string caseId, string key)
    {
        var s = Script(caseId, key);
        var (_, ideal, ids) = Ideal(s);

        // Prima della correzione.
        var before = AcceptanceOracle.RenderMarkdown(ideal, s.Name);
        Assert.True(DocumentDraftValidator.Validate(before, new DocumentValidationContext(ideal.Sources.Select(x => x.Id).ToHashSet())).Accepted);
        var findingsBefore = AcceptanceChecker.CheckDocument(s, before, ideal, correctionApplied: false);
        Assert.True(findingsBefore.Passed, string.Join("; ", findingsBefore.Failures));

        // La correzione e' una modifica PM valida, poi confermata: e' l'input della generazione.
        var revision = Guid.NewGuid();
        var edited = Corrected(ideal, s, revision, AnalysisContentState.Unvalidated);
        var context = ids.Context with { PmRevisionIds = new HashSet<Guid> { revision } };
        var accepted = AnalysisContentValidator.Validate(edited, context, AnalysisActorKind.PmEdit, ideal);
        Assert.True(accepted.Accepted, string.Join("; ", accepted.Violations));
        var confirmed = edited with { ValidationState = AnalysisContentState.PmConfirmed };
        Assert.True(AnalysisContentValidator.Validate(confirmed, context, AnalysisActorKind.PmConfirm, edited).Accepted);

        var after = AcceptanceOracle.RenderMarkdown(confirmed, s.Name);
        Assert.True(DocumentDraftValidator.Validate(after, new DocumentValidationContext(confirmed.Sources.Select(x => x.Id).ToHashSet())).Accepted);
        var findingsAfter = AcceptanceChecker.CheckDocument(s, after, confirmed, correctionApplied: true);
        Assert.True(findingsAfter.Passed, string.Join("; ", findingsAfter.Failures));
    }

    // ------------------------------------------------------------------------------------------------ il verificatore rileva i difetti

    private static void Insert(JsonObject root, Sec section, JsonObject item)
    {
        var array = section switch
        {
            Sec.Goals => root["goals"],
            Sec.Overview => root["contextAndScope"]!["overview"],
            Sec.Included => root["contextAndScope"]!["included"],
            Sec.Excluded => root["contextAndScope"]!["excluded"],
            Sec.Requirements => root["requirements"],
            Sec.Flows => root["operationalFlows"],
            Sec.Constraints => root["constraints"],
            Sec.Dependencies => root["dependencies"],
            Sec.Assumptions => root["assumptions"],
            _ => root["openQuestions"],
        };
        array!.AsArray().Add(item);
    }

    private static JsonObject NewItem(string text, string cls, IEnumerable<string> refs) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["text"] = text,
        ["classification"] = cls,
        ["evidenceRefs"] = new JsonArray(refs.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
    };

    private static void RemoveByText(JsonNode root, string text)
    {
        var arrays = new[] { "goals", "requirements", "operationalFlows", "constraints", "dependencies", "assumptions", "openQuestions" }
            .Select(n => root[n]!.AsArray())
            .Concat(new[] { "overview", "included", "excluded" }.Select(n => root["contextAndScope"]![n]!.AsArray()));
        foreach (var array in arrays)
        {
            foreach (var item in array.Where(i => i!["text"]!.GetValue<string>() == text).ToList())
            {
                array.Remove(item);
            }
        }
    }

    internal static readonly (string Name, Func<ProjectScript, bool> Applies, Action<JsonObject, ProjectScript, Ids> Mutate, string Expect)[] AnalysisDefects =
    [
        ("omette-un-requisito", _ => true, (r, s, _) => RemoveByText(r, Untouched(s, Sec.Requirements)), "omissione"),
        ("omette-una-esclusione", s => s.Facts.Any(f => f.Section == Sec.Excluded), (r, s, _) => RemoveByText(r, Untouched(s, Sec.Excluded)), "omissione"),
        ("omette-una-lacuna", s => s.Facts.Any(f => f.Section == Sec.Open), (r, s, _) => RemoveByText(r, s.Facts.First(f => f.Section == Sec.Open).Text), "omissione"),
        ("esclusione-trasformata-in-requisito", s => s.Facts.Any(f => f.Section == Sec.Excluded), (r, s, _) =>
        {
            var text = Untouched(s, Sec.Excluded);
            RemoveByText(r, text);
            Insert(r, Sec.Requirements, NewItem(text, "Declared", [r["sources"]![0]!["id"]!.GetValue<string>()]));
        }, "sezione sbagliata"),
        ("negazione-invertita", s => s.Forbids.Any(f => f.Mode == ForbidMode.Affirmed && f.ConflictKey is null), (r, s, _) =>
            Insert(r, Sec.Requirements, NewItem(s.Forbids.First(f => f.Mode == ForbidMode.Affirmed && f.ConflictKey is null).Sample, "Declared", [r["sources"]![0]!["id"]!.GetValue<string>()])),
            "affermata"),
        ("informazione-inventata", s => s.Forbids.Any(f => f.Mode == ForbidMode.Anywhere), (r, s, _) =>
            Insert(r, Sec.Requirements, NewItem(s.Forbids.First(f => f.Mode == ForbidMode.Anywhere).Sample, "Declared", [r["sources"]![0]!["id"]!.GetValue<string>()])),
            "presente nell'analisi"),
        ("fonte-sbagliata", s => s.Facts.Any(f => f.Class == AnalysisClassification.Declared && f.From.Length < s.Messages.Length), (r, s, ids) =>
        {
            var fact = s.Facts.First(f => f.Class == AnalysisClassification.Declared && f.From.Length < s.Messages.Length);
            var other = Enumerable.Range(1, s.Messages.Length).First(i => !fact.From.Contains(i));
            var source = Guid.NewGuid().ToString();
            r["sources"]!.AsArray().Add(new JsonObject { ["id"] = source, ["kind"] = "Message", ["messageId"] = ids.Users[other - 1].ToString(), ["messageRole"] = "user", ["revisionId"] = null });
            foreach (var array in new[] { "goals", "requirements", "constraints", "dependencies", "operationalFlows" }.Select(n => r[n]!.AsArray()))
            {
                foreach (var item in array.Where(i => i!["text"]!.GetValue<string>() == fact.Text))
                {
                    item!["evidenceRefs"] = new JsonArray(JsonValue.Create(source));
                }
            }

            foreach (var item in new[] { "overview", "included", "excluded" }.SelectMany(n => r["contextAndScope"]![n]!.AsArray()).Where(i => i!["text"]!.GetValue<string>() == fact.Text))
            {
                item!["evidenceRefs"] = new JsonArray(JsonValue.Create(source));
            }
        }, "fonte errata"),
        ("fonte-di-un-altro-progetto", _ => true, (r, _, _) =>
        {
            var foreign = Guid.NewGuid().ToString();
            r["sources"]!.AsArray().Add(new JsonObject { ["id"] = foreign, ["kind"] = "Message", ["messageId"] = Guid.NewGuid().ToString(), ["messageRole"] = "user", ["revisionId"] = null });
        }, "non appartiene al progetto"),
        ("conflitto-risolto-dall-ai", s => s.Conflicts.Length > 0, (r, _, _) =>
        {
            var conflict = r["conflicts"]![0]!;
            conflict["status"] = "Resolved";
            conflict["resolution"] = new JsonObject
            {
                ["selectedAlternativeIds"] = new JsonArray(JsonValue.Create(conflict["alternatives"]![0]!["id"]!.GetValue<string>())),
                ["reason"] = "Scelta automatica",
                ["evidenceRefs"] = new JsonArray(),
            };
        }, "risolto dall'AI"),
        ("conflitto-scartato", s => s.Conflicts.Length > 0, (r, _, _) => r["conflicts"] = new JsonArray(), "alternative non riportate"),
        ("voce-ripetuta", _ => true, (r, s, _) =>
        {
            var requirements = r["requirements"]!.AsArray();
            var copy = requirements[0]!.DeepClone();
            copy["id"] = Guid.NewGuid().ToString();
            requirements.Add(copy);
        }, "ripetizione"),
        ("stima-inventata", _ => true, (r, _, _) =>
            Insert(r, Sec.Constraints, NewItem("Impegno stimato: 120 ore uomo", "Declared", [r["sources"]![0]!["id"]!.GetValue<string>()])), "stima"),
    ];

    public static IEnumerable<object[]> AnalysisDefectCases() =>
        AnalysisDefects.SelectMany(d => AcceptanceCases.Nominal.SelectMany(c => c.Projects.Where(p => d.Applies(p)).Select(p => new object[] { d.Name, c.Id, p.Key })));

    [Theory]
    [MemberData(nameof(AnalysisDefectCases))]
    public void The_checker_rejects_a_defective_analysis(string defect, string caseId, string key)
    {
        var s = Script(caseId, key);
        var (json, _, ids) = Ideal(s);
        var d = AnalysisDefects.Single(x => x.Name == defect);
        d.Mutate(json, s, ids);

        var content = AnalysisContentJson.Deserialize(json.ToJsonString());
        var findings = AcceptanceChecker.CheckAnalysis(s, content, ids.Index, ids.All);

        Assert.False(findings.Passed, $"{defect}: il difetto non e' stato rilevato");
        Assert.Contains(findings.Failures, f => f.Contains(d.Expect, StringComparison.Ordinal));
    }

    [Fact]
    public void Cross_project_contamination_is_rejected_by_the_checker()
    {
        var a = Script("N5-progetti-distinti", "aurora");
        var (json, _, ids) = Ideal(a, new OracleOptions { Contaminate = "integrazione Colorweb per gli ordini" });
        var findings = AcceptanceChecker.CheckAnalysis(a, AnalysisContentJson.Deserialize(json.ToJsonString()), ids.Index, ids.All);

        Assert.False(findings.Passed);
        Assert.Contains(findings.Failures, f => f.Contains("progetto-boreale", StringComparison.Ordinal));
    }

    internal static readonly (string Name, Func<ProjectScript, bool> Applies, Func<string, ProjectScript, AnalysisContent, string> Mutate, string Expect)[] DocumentDefects =
    [
        ("omette-un-requisito", _ => true, (md, s, _) => DropLines(md, Untouched(s, Sec.Requirements)), "omissione nella bozza"),
        ("omette-un-vincolo-o-una-esclusione", s => s.Facts.Any(f => f.Section == Sec.Excluded), (md, s, _) => DropLines(md, Untouched(s, Sec.Excluded)), "omissione nella bozza"),
        ("esclusione-presentata-come-requisito", s => s.Facts.Any(f => f.Section == Sec.Excluded), (md, s, _) =>
            DropLines(md, Untouched(s, Sec.Excluded)) + "\n\n## Requisiti aggiuntivi\n\n- " + Affirm(Untouched(s, Sec.Excluded)) + "\n",
            "omissione nella bozza"),
        ("lacuna-trasformata-in-fatto", s => s.Facts.Any(f => f.Section == Sec.Open), (md, s, _) =>
            DropLines(md, s.Facts.First(f => f.Section == Sec.Open).Text) + "\n\n## Requisiti aggiuntivi\n\n- " + s.Facts.First(f => f.Section == Sec.Open).Text.Replace("?", ".").Replace("non sono disponibili", "sono definiti").Replace("non sono definiti", "sono definiti") + "\n",
            "omissione nella bozza"),
        ("negazione-invertita", s => s.Forbids.Any(f => f.Mode == ForbidMode.Affirmed && f.ConflictKey is null), (md, s, _) =>
            md + "\n## Requisiti aggiuntivi\n\n- " + s.Forbids.First(f => f.Mode == ForbidMode.Affirmed && f.ConflictKey is null).Sample + "\n", "affermata nella bozza"),
        ("informazione-inventata", s => s.Forbids.Any(f => f.Mode == ForbidMode.Anywhere), (md, s, _) =>
            md + "\n## Requisiti aggiuntivi\n\n- " + s.Forbids.First(f => f.Mode == ForbidMode.Anywhere).Sample + "\n", "presente nella bozza"),
        ("conflitto-aperto-scelto-in-silenzio", s => s.Conflicts.Any(k => k.Key != s.Correction.Resolve?.ConflictKey), (md, _, _) =>
            string.Join('\n', md.Split('\n').Where(l => !l.Contains("Conflitto aperto", StringComparison.Ordinal))), "conflitto"),
        ("bozza-presentata-come-approvata", _ => true, (md, _, _) => md + "\n## Stato\n\n- Il documento è approvato dal cliente\n", "approvata"),
        ("punto-ripetuto", _ => true, (md, s, _) =>
        {
            var line = md.Split('\n').First(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Length > 60);
            return md + "\n\n## Riepilogo\n\n" + line + "\n";
        }, "ripetizione"),
        ("citazione-inventata", _ => true, (md, _, _) =>
            md + "\n## Note\n\n- Un fatto non verificabile [fonte:" + Guid.NewGuid() + "]\n", "non e' una fonte"),
        ("stima-inventata", _ => true, (md, _, _) => md + "\n## Impegno\n\n- Impegno stimato: 120 ore uomo\n", "stima"),
    ];

    /// <summary>Toglie le parole di negazione/esclusione: l'esclusione diventa un'affermazione.</summary>
    private static string Affirm(string text) =>
        Regex.Replace(text, @"\b(non|esclus\w*|fuori|altrove|niente|senza)\b", "", RegexOptions.IgnoreCase);

    /// <summary>Testo ideale della prima voce della sezione che la correzione del PM non riscrive.</summary>
    private static string Untouched(ProjectScript s, Sec section) =>
        s.Facts.First(f => f.Section == section && f.Key != s.Correction.EditKey).Text;

    private static string DropLines(string markdown, string text) =>
        string.Join('\n', markdown.Split('\n').Where(l => !l.Contains(text, StringComparison.Ordinal)));

    public static IEnumerable<object[]> DocumentDefectCases() =>
        DocumentDefects.SelectMany(d => AcceptanceCases.Nominal.SelectMany(c => c.Projects.Where(p => d.Applies(p)).Select(p => new object[] { d.Name, c.Id, p.Key })));

    [Theory]
    [MemberData(nameof(DocumentDefectCases))]
    public void The_checker_rejects_a_defective_draft(string defect, string caseId, string key)
    {
        var s = Script(caseId, key);
        var (_, ideal, _) = Ideal(s);
        var confirmed = Corrected(ideal, s, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
        var d = DocumentDefects.Single(x => x.Name == defect);
        var markdown = d.Mutate(AcceptanceOracle.RenderMarkdown(confirmed, s.Name), s, confirmed);

        var findings = AcceptanceChecker.CheckDocument(s, markdown, confirmed, correctionApplied: true);

        Assert.False(findings.Passed, $"{defect}: il difetto non e' stato rilevato");
        Assert.Contains(findings.Failures, f => f.Contains(d.Expect, StringComparison.Ordinal));
    }

    [Fact]
    public void A_foreign_project_marker_in_the_draft_is_rejected_by_the_checker()
    {
        var b = Script("N5-progetti-distinti", "boreale");
        var (_, ideal, _) = Ideal(b);
        var confirmed = Corrected(ideal, b, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
        var markdown = AcceptanceOracle.RenderMarkdown(confirmed, b.Name) + "\n## Note\n\n- I ticket di assistenza di Aurora [fonte:" + confirmed.Sources[0].Id + "]\n";

        var findings = AcceptanceChecker.CheckDocument(b, markdown, confirmed, correctionApplied: true);

        Assert.Contains(findings.Failures, f => f.Contains("progetto-aurora", StringComparison.Ordinal));
    }
}
