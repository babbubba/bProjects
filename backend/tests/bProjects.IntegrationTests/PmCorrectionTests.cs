using System.Text.Json.Nodes;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// Correzione PM simulata dal percorso di accettazione (<see cref="PmCorrection"/>, <see cref="AcceptanceRunner.ApplyCorrection"/>): la
/// modifica colpisce solo la voce che esprime il fatto da modificare. Voce assente o ambigua: correzione non applicata e segnalata, nessuna
/// altra voce riscritta. Provano l'harness, non il modello ne' il verificatore (le cui regole restano quelle di 66205ea).
/// </summary>
public sealed class PmCorrectionTests
{
    private const string Approves = "Il responsabile diretto approva o rifiuta la richiesta";
    private const string Sends = "Il dipendente invia la richiesta di ferie o permessi";

    private static ProjectScript Ferie => AcceptanceFixtureTests.Script("N2-negazioni", "ferie");

    /// <summary>Analisi ideale del progetto senza le voci ideali dei fatti <paramref name="remove"/> e con le voci <paramref name="add"/> inserite.</summary>
    private static (JsonObject Json, AcceptanceFixtureTests.Ids Ids) Analysis(ProjectScript script, string[] remove, params (Sec Section, int At, string Text)[] add)
    {
        var (json, _, ids) = AcceptanceFixtureTests.Ideal(script);
        foreach (var text in script.Facts.Where(f => remove.Contains(f.Key)).Select(f => f.Text))
        {
            foreach (var section in Enum.GetValues<Sec>())
            {
                var items = PmCorrection.Items(json, section);
                foreach (var item in items.Where(i => i!["text"]!.GetValue<string>() == text).ToList())
                {
                    items.Remove(item);
                }
            }
        }

        var source = json["sources"]!.AsArray().First(s => s!["messageId"]!.GetValue<string>() == ids.Users[0].ToString())!["id"]!.GetValue<string>();
        foreach (var (section, at, text) in add)
        {
            var items = PmCorrection.Items(json, section);
            items.Insert(Math.Min(at, items.Count), new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["text"] = text,
                ["classification"] = "Declared",
                ["evidenceRefs"] = new JsonArray(JsonValue.Create(source)),
            });
        }

        return (json, ids);
    }

    private static AnalysisContent Content(JsonNode json) => AnalysisContentJson.Deserialize(json.ToJsonString());

    private static List<string> Texts(JsonNode json) =>
        Enum.GetValues<Sec>().SelectMany(s => PmCorrection.Items(json, s).Select(i => i!["text"]!.GetValue<string>())).ToList();

    /// <summary>Il PUT del percorso: corpo corretto (o null) e fallimenti registrati.</summary>
    private static (JsonNode? Body, ProjectRun Run) Apply(ProjectScript script, JsonNode json)
    {
        var run = new ProjectRun(script);
        return (AcceptanceRunner.ApplyCorrection(json.DeepClone(), script, run), run);
    }

    /// <summary>Le voci originali, tolte le aggiunte del PM, sono identiche salvo il testo della voce indicata.</summary>
    private static void OnlyTextChanged(JsonNode before, JsonNode after, Sec? section, int index)
    {
        foreach (var s in Enum.GetValues<Sec>())
        {
            var original = PmCorrection.Items(before, s);
            var edited = PmCorrection.Items(after, s);
            for (var n = 0; n < original.Count; n++)
            {
                var expected = original[n]!.DeepClone();
                if (s == section && n == index)
                {
                    expected["text"] = edited[n]!["text"]!.DeepClone();
                }

                Assert.True(JsonNode.DeepEquals(expected, edited[n]), $"{s}[{n}] modificata: {edited[n]!.ToJsonString()}");
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ voce presente

    [Fact]
    public void A_present_target_is_found_and_only_its_text_changes()
    {
        var s = Ferie;
        var (json, _) = Analysis(s, []);
        var target = PmCorrection.Locate(s, json);

        Assert.Equal(EditTargetKind.Found, target.Kind);
        Assert.Equal(Sec.Requirements, target.Section);
        Assert.Equal(s.Facts.Single(f => f.Key == "solo-responsabile").Text, PmCorrection.Items(json, Sec.Requirements)[target.Index]!["text"]!.GetValue<string>());
        Assert.Equal(target, PmCorrection.Locate(s, Content(json)));

        var (body, run) = Apply(s, json);
        Assert.Empty(run.Findings.Failures);
        Assert.Equal(s.Correction.EditedText, PmCorrection.Items(body!, Sec.Requirements)[target.Index]!["text"]!.GetValue<string>());
        OnlyTextChanged(json, body!, Sec.Requirements, target.Index);

        // Aggiunte del PM: nuove voci con la classificazione annotata e senza fonti di messaggio (la fonte PmRevision la assegna il server).
        foreach (var add in s.Correction.Additions)
        {
            var item = PmCorrection.Items(body!, add.Section).Last(i => i!["text"]!.GetValue<string>() == add.Text)!;
            Assert.Equal(add.Class.ToString(), item["classification"]!.GetValue<string>());
            Assert.Empty(item["evidenceRefs"]!.AsArray());
        }
    }

    [Fact]
    public void The_simulated_revision_keeps_ids_classification_and_sources_and_passes_the_pm_validators()
    {
        var s = Ferie;
        var (json, ids) = Analysis(s, [], (Sec.Requirements, 0, Approves));
        var ai = Content(json);
        var target = PmCorrection.Locate(s, ai);
        var revision = Guid.NewGuid();

        var edited = AcceptanceFixtureTests.Corrected(ai, s, revision, AnalysisContentState.Unvalidated);

        var before = AcceptanceChecker.Items(ai, target.Section)[target.Index];
        var after = AcceptanceChecker.Items(edited, target.Section)[target.Index];
        var pm = edited.Sources.Single(x => x.Kind == AnalysisSourceKind.PmRevision);
        Assert.Equal(revision, pm.RevisionId);
        Assert.Equal((before.Id, before.Classification), (after.Id, after.Classification));
        Assert.Equal([.. before.EvidenceRefs, pm.Id], after.EvidenceRefs);
        Assert.Equal(s.Correction.EditedText, after.Text);
        Assert.Equal(Approves, AcceptanceChecker.Items(edited, Sec.Requirements)[0].Text);

        var context = ids.Context with { PmRevisionIds = new HashSet<Guid> { revision } };
        var accepted = AnalysisContentValidator.Validate(edited, context, AnalysisActorKind.PmEdit, ai);
        Assert.True(accepted.Accepted, string.Join("; ", accepted.Violations));
        var confirmed = edited with { ValidationState = AnalysisContentState.PmConfirmed };
        Assert.True(AnalysisContentValidator.Validate(confirmed, context, AnalysisActorKind.PmConfirm, edited).Accepted);
        var document = AcceptanceChecker.CheckDocument(s, AcceptanceOracle.RenderMarkdown(confirmed, s.Name), confirmed, correctionApplied: true, ai);
        Assert.True(document.Passed, string.Join("; ", document.Failures));
    }

    [Fact]
    public void Two_items_with_similar_words_do_not_redirect_the_edit_to_the_wrong_one()
    {
        // La voce sull'approvazione viene prima e contiene anche «responsabile diretto»: la vecchia regola (prima voce che combacia) la riscriveva.
        var s = Ferie;
        var (json, _) = Analysis(s, ["richiesta"], (Sec.Requirements, 0, Sends), (Sec.Requirements, 1, Approves));

        var target = PmCorrection.Locate(s, json);

        Assert.Equal(EditTargetKind.Found, target.Kind);
        Assert.Equal(s.Facts.Single(f => f.Key == "solo-responsabile").Text, PmCorrection.Items(json, target.Section)[target.Index]!["text"]!.GetValue<string>());
        var (body, run) = Apply(s, json);
        Assert.Empty(run.Findings.Failures);
        Assert.Contains(Approves, Texts(body!));
        OnlyTextChanged(json, body!, target.Section, target.Index);
    }

    [Fact]
    public void An_edit_on_approval_exclusivity_does_not_remove_the_right_to_reject()
    {
        var s = Ferie;
        var richiesta = s.Facts.Single(f => f.Key == "richiesta");
        foreach (var json in new[]
        {
            Analysis(s, ["richiesta"], (Sec.Requirements, 0, Sends), (Sec.Requirements, 1, Approves)).Json,
            Analysis(s, ["richiesta", "solo-responsabile"], (Sec.Requirements, 0, Sends), (Sec.Requirements, 1, Approves)).Json,
        })
        {
            var (body, _) = Apply(s, json);
            var confirmed = AcceptanceFixtureTests.Corrected(Content(json), s, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
            foreach (var texts in new[] { Texts(body!), AcceptanceChecker.AllTexts(confirmed).ToList() })
            {
                Assert.Contains(texts, t => AcceptanceChecker.Norm(t).Contains("rifiut", StringComparison.Ordinal));
                Assert.True(AcceptanceChecker.Covers(richiesta.Match, richiesta.Parts, texts.Select(AcceptanceChecker.Norm).ToList()));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ voce assente o ambigua

    [Fact]
    public void An_absent_target_is_reported_and_no_other_item_is_rewritten()
    {
        var s = Ferie;
        var (json, _) = Analysis(s, ["solo-responsabile"]);

        var target = PmCorrection.Locate(s, json);

        Assert.Equal(EditTargetKind.Absent, target.Kind);
        Assert.Empty(target.Lost);
        var (body, run) = Apply(s, json);
        Assert.Equal(["correzione non applicabile: la voce «solo-responsabile» da modificare non e' nell'anteprima (Requirements/Constraints)"], run.Findings.Failures);
        Assert.DoesNotContain(s.Correction.EditedText, Texts(body!));
        OnlyTextChanged(json, body!, null, -1);
    }

    [Fact]
    public void An_ambiguous_target_is_not_chosen_silently()
    {
        var s = Ferie;
        var (json, _) = Analysis(s, [], (Sec.Constraints, 0, "Solo il responsabile diretto approva le richieste di ferie"));

        var target = PmCorrection.Locate(s, json);

        Assert.Equal(EditTargetKind.Ambiguous, target.Kind);
        Assert.Contains("2 voci candidate", target.Message, StringComparison.Ordinal);
        var (body, run) = Apply(s, json);
        Assert.Equal([target.Message], run.Findings.Failures);
        Assert.DoesNotContain(s.Correction.EditedText, Texts(body!));
        OnlyTextChanged(json, body!, null, -1);
        Assert.DoesNotContain(s.Correction.EditedText, AcceptanceChecker.AllTexts(AcceptanceFixtureTests.Corrected(Content(json), s, Guid.NewGuid(), AnalysisContentState.PmConfirmed)));
    }

    [Fact]
    public void A_multi_fact_item_is_rewritten_keeping_the_independent_facts()
    {
        // V7 proxy 56: nessuna voce sull'esclusivita'; l'unica con «responsabile diretto» porta anche «approva o rifiuta» (fatto «richiesta»).
        var s = Ferie;
        var (json, _) = Analysis(s, ["richiesta", "solo-responsabile"], (Sec.Requirements, 0, Sends), (Sec.Requirements, 1, Approves));
        var ai = Content(json);

        var target = PmCorrection.Locate(s, json);

        Assert.Equal(EditTargetKind.Found, target.Kind);
        Assert.Equal(["richiesta"], target.Preserved);
        Assert.Equal(PmCorrection.Preserving(s.Correction.EditedText, Approves), target.NewText);
        Assert.StartsWith(s.Correction.EditedText, target.NewText, StringComparison.Ordinal);
        Assert.Contains("rifiuta la richiesta", target.NewText, StringComparison.Ordinal);
        var again = PmCorrection.Locate(s, ai);
        Assert.Equal((target.Kind, target.Section, target.Index, target.NewText), (again.Kind, again.Section, again.Index, again.NewText));

        var (body, run) = Apply(s, json);
        Assert.Empty(run.Findings.Failures);
        Assert.Equal(target.NewText, PmCorrection.Items(body!, target.Section)[target.Index]!["text"]!.GetValue<string>());
        OnlyTextChanged(json, body!, target.Section, target.Index);

        // Nella revisione confermata «rifiuta» e il fatto «richiesta» sono conservati, l'esclusivita' e' espressa e nessuna nota li maschera.
        var confirmed = AcceptanceFixtureTests.Corrected(ai, s, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
        var texts = AcceptanceChecker.AllTexts(confirmed).Select(AcceptanceChecker.Norm).ToList();
        foreach (var key in new[] { "richiesta", "solo-responsabile" })
        {
            var fact = s.Facts.Single(f => f.Key == key);
            Assert.True(AcceptanceChecker.Covers(fact.Match, fact.Parts, texts), $"«{key}» non coperto dopo la correzione");
        }

        var document = AcceptanceChecker.CheckDocument(s, AcceptanceOracle.RenderMarkdown(confirmed, s.Name), confirmed, correctionApplied: true, ai);
        Assert.DoesNotContain(document.Failures, f => f.Contains("«richiesta»", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Notes, n => n.Contains("«richiesta»", StringComparison.Ordinal));
    }

    [Fact]
    public void A_multi_fact_item_with_two_equivalent_candidates_is_reported_not_guessed()
    {
        // Due voci distinte portano ciascuna «responsabile diretto» e un fatto proprio non coperto altrove: nessuna scelta silenziosa.
        var s = Ferie;
        var (json, _) = Analysis(s, ["richiesta", "solo-responsabile", "ufficio-riepilogo"],
            (Sec.Requirements, 0, Approves), (Sec.Requirements, 1, "L'ufficio personale vede soltanto il riepilogo, non il responsabile diretto"));

        var target = PmCorrection.Locate(s, json);

        Assert.Equal(EditTargetKind.Ambiguous, target.Kind);
        Assert.Contains("voci candidate a piu' fatti", target.Message, StringComparison.Ordinal);
        Assert.Null(target.NewText);
        var (body, run) = Apply(s, json);
        Assert.Equal([target.Message], run.Findings.Failures);
        Assert.DoesNotContain(Texts(body!), t => t.StartsWith(s.Correction.EditedText, StringComparison.Ordinal));
        OnlyTextChanged(json, body!, null, -1);
    }

    [Fact]
    public void A_simple_replacement_is_kept_when_no_other_fact_is_at_stake()
    {
        // Nessun fatto indipendente da conservare: il testo resta quello della correzione, senza accodare l'originale.
        var s = Ferie;
        var (json, _) = Analysis(s, []);
        var target = PmCorrection.Locate(s, json);
        Assert.Equal(EditTargetKind.Found, target.Kind);
        Assert.Empty(target.Preserved!);
        Assert.Equal(s.Correction.EditedText, target.NewText);
    }

    [Fact]
    public void A_real_omission_before_the_correction_is_still_detected_in_analysis_and_draft()
    {
        // Il modello non dice mai «rifiuta»: la correzione colpisce la voce sull'esclusivita' e il verificatore segnala l'omissione ereditata.
        var s = Ferie;
        var (json, ids) = Analysis(s, ["richiesta"], (Sec.Requirements, 0, Sends));
        var ai = Content(json);
        var located = PmCorrection.Locate(s, json);
        Assert.Equal(EditTargetKind.Found, located.Kind);
        Assert.Empty(located.Preserved!);
        Assert.Equal(s.Correction.EditedText, located.NewText);

        Assert.Contains(AcceptanceChecker.CheckAnalysis(s, ai, ids.Index, ids.All).Failures, f => f.StartsWith("omissione: «richiesta»", StringComparison.Ordinal));
        var confirmed = AcceptanceFixtureTests.Corrected(ai, s, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
        var document = AcceptanceChecker.CheckDocument(s, AcceptanceOracle.RenderMarkdown(confirmed, s.Name), confirmed, correctionApplied: true, ai);
        Assert.Contains(document.Failures, f => f.Contains("«richiesta» (assente gia' dalla revisione confermata", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ casi nominali

    [Theory]
    [MemberData(nameof(AcceptanceFixtureTests.Projects), MemberType = typeof(AcceptanceFixtureTests))]
    public void Every_nominal_correction_finds_its_item_and_preserves_the_other_facts(string caseId, string key)
    {
        var s = AcceptanceFixtureTests.Script(caseId, key);
        var edit = s.Facts.Single(f => f.Key == s.Correction.EditKey);
        Assert.True(AcceptanceChecker.Matches(AcceptanceChecker.Norm(s.Correction.EditedText), edit.Match), "il testo corretto deve esprimere ancora il fatto modificato");
        var (json, _) = Analysis(s, []);

        var target = PmCorrection.Locate(s, json);

        Assert.True(target.Kind == EditTargetKind.Found, target.Message);
        Assert.Equal(edit.Text, PmCorrection.Items(json, target.Section)[target.Index]!["text"]!.GetValue<string>());
        var (body, run) = Apply(s, json);
        Assert.Empty(run.Findings.Failures);
        Assert.Equal(s.Correction.EditedText, PmCorrection.Items(body!, target.Section)[target.Index]!["text"]!.GetValue<string>());
        OnlyTextChanged(json, body!, target.Section, target.Index);
        foreach (var fact in s.Facts)
        {
            var texts = fact.Accept.SelectMany(x => PmCorrection.Items(body!, x)).Select(i => AcceptanceChecker.Norm(i!["text"]!.GetValue<string>())).ToList();
            Assert.True(AcceptanceChecker.Covers(fact.Match, fact.Parts, texts), $"«{fact.Key}» perso dopo la correzione");
        }
    }

    /// <summary>
    /// Scelta della voce sulle analisi AI reali V7 (traccia locale non versionata, ignorata se assente): nei cinque progetti con la voce
    /// la scelta coincide con quella registrata nella revisione confermata V7; in ferie la voce a piu' fatti e' riscritta conservando «richiesta».
    /// </summary>
    [Fact]
    public void V7_captured_analyses_select_the_same_items_and_ferie_keeps_the_independent_fact()
    {
        var path = Environment.GetEnvironmentVariable("BPROJECTS_ACCEPTANCE_TRACE")
            ?? Path.Combine(AcceptanceFixtureTests.RepoRoot(), "docs", "acceptance-trace", "trace-qwen3.8-flash-next-smart-v7.md");
        Assert.SkipUnless(File.Exists(path), $"BPROJECTS_ACCEPTANCE_TRACE non impostata e traccia V7 assente: {path}");

        var runs = V7Trace.Read(path).ToDictionary(r => r.Script.Key);
        Assert.Equal(6, runs.Count);
        foreach (var key in new[] { "officina", "magazzino", "fornitori", "aurora", "boreale" })
        {
            var r = runs[key];
            var target = PmCorrection.Locate(r.Script, r.Ai);
            Assert.True(target.Kind == EditTargetKind.Found, $"{key}: {target.Message}");
            Assert.Equal(r.Script.Correction.EditedText, AcceptanceChecker.Items(r.Confirmed, target.Section)[target.Index].Text);
        }

        var ferie = PmCorrection.Locate(runs["ferie"].Script, runs["ferie"].Ai);
        Assert.Equal(EditTargetKind.Found, ferie.Kind);
        Assert.Equal(["richiesta"], ferie.Preserved);
        var confirmed = AcceptanceFixtureTests.Corrected(runs["ferie"].Ai, runs["ferie"].Script, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
        Assert.Contains(AcceptanceChecker.AllTexts(confirmed), t => AcceptanceChecker.Norm(t).Contains("rifiut", StringComparison.Ordinal));
    }
}
