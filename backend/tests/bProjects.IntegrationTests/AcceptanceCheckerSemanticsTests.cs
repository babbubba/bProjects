using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// Regressioni del verificatore di accettazione emerse dall'audit V7 (Qwen3.8-Flash-Next, commit 2a2ef50): prove positive, negative e
/// controfattuali che variano soggetto, predicato, oggetto, negazione e responsabilita'. Provano le REGOLE deterministiche, non il modello.
/// Confine dichiarato: il verificatore distingue polarita' per proposizione, attribuzione a un sistema esterno annotata dal PM e
/// scomposizioni atomiche annotate; non decide se «il portale mostra i ratei» sia lecito (fallisce e va alla revisione umana).
/// </summary>
public sealed class AcceptanceCheckerSemanticsTests
{
    private const string N2 = "N2-negazioni";
    private const string N3 = "N3-lacune";
    private const string N5 = "N5-progetti-distinti";

    // ------------------------------------------------------------------------------------------------ strumenti

    private static JsonArray Array(JsonObject root, Sec section) => (section switch
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
    })!.AsArray();

    /// <summary>Analisi ideale del progetto modificata voce per voce, poi corretta e confermata come nel percorso nominale.</summary>
    private sealed class Run(ProjectScript script)
    {
        private readonly AcceptanceFixtureTests.Ids ids = AcceptanceFixtureTests.NewIds(script);
        private JsonObject? json;

        public ProjectScript Script { get; } = script;

        private JsonObject Json => json ??= AcceptanceOracle.BuildAnalysis(Script, ids.Project, ids.Analysis, ids.Users.Length, ids.Users);

        /// <summary>Id della fonte del messaggio PM <paramref name="n"/> (da 1), aggiunta se l'analisi ideale non la cita.</summary>
        private string Source(int n)
        {
            var message = ids.Users[n - 1].ToString();
            var existing = Json["sources"]!.AsArray().FirstOrDefault(x => x!["messageId"]!.GetValue<string>() == message);
            if (existing is not null)
            {
                return existing["id"]!.GetValue<string>();
            }

            var id = Guid.NewGuid().ToString();
            Json["sources"]!.AsArray().Add(new JsonObject { ["id"] = id, ["kind"] = "Message", ["messageId"] = message, ["messageRole"] = "user", ["revisionId"] = null });
            return id;
        }

        public Run Remove(string key)
        {
            var text = Script.Facts.Single(f => f.Key == key).Text;
            foreach (var section in Enum.GetValues<Sec>())
            {
                var array = Array(Json, section);
                foreach (var item in array.Where(i => i!["text"]!.GetValue<string>() == text).ToList())
                {
                    array.Remove(item);
                }
            }

            return this;
        }

        public Run Add(Sec section, string text, int from = 1)
        {
            Array(Json, section).Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["text"] = text,
                ["classification"] = "Declared",
                ["evidenceRefs"] = new JsonArray(JsonValue.Create(Source(from))),
            });
            return this;
        }

        public AnalysisContent Content => AnalysisContentJson.Deserialize(Json.ToJsonString());

        public AcceptanceFindings Analysis() => AcceptanceChecker.CheckAnalysis(Script, Content, ids.Index, ids.All);

        /// <summary>Correzione PM come la applica il percorso (prima voce che soddisfa l'attesa da modificare), conferma, bozza fedele.</summary>
        public (AnalysisContent Confirmed, string Markdown) Confirm(string extra = "")
        {
            var confirmed = AcceptanceFixtureTests.Corrected(Content, Script, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
            return (confirmed, AcceptanceOracle.RenderMarkdown(confirmed, Script.Name) + extra);
        }

        public AcceptanceFindings Document(string extra = "", bool withAi = false)
        {
            var (confirmed, markdown) = Confirm(extra);
            return AcceptanceChecker.CheckDocument(Script, markdown, confirmed, correctionApplied: true, withAi ? Content : null);
        }
    }

    private static Run Of(string caseId, string key) => new(AcceptanceFixtureTests.Script(caseId, key));

    private static bool Flags(AcceptanceFindings f, string key) => f.Failures.Any(x => x.Contains($"«{key}»", StringComparison.Ordinal));

    // ------------------------------------------------------------------------------------------------ (a) paghe e ratei: chi calcola?

    public static TheoryData<string, string, string, string> PayrollStatements() => new()
    {
        // sezione, testo, divieti attesi (vuoto = nessuna violazione), cosa varia
        { "Dependencies", "Il calcolo di buste paga e ratei di ferie resta nel software paghe", "", "V7 proxy 56: responsabilita' esterna" },
        { "Constraints", "Il software paghe calcola le buste paga e i ratei", "", "soggetto esterno, predicato calcolare" },
        { "Requirements", "Le buste paga restano di competenza del software paghe", "", "responsabilita' esterna senza verbo di calcolo" },
        { "Requirements", "Il portale non calcola le buste paga né i ratei", "", "nostro sistema, predicato negato" },
        { "Requirements", "Le buste paga, come i ratei, restano fuori dal portale", "", "negazione dopo un inciso fra virgole" },
        { "Requirements", "Il portale calcola le buste paga", "paghe", "nostro sistema, affermato" },
        { "Requirements", "Il PORTALE gestisce i ratei di ferie maturati", "ratei", "nostro sistema, altro predicato" },
        { "Requirements", "Le buste paga restano nel portale", "paghe", "stessa forma, responsabilita' al nostro sistema" },
        { "Dependencies", "Il portale calcola le buste paga, che restano nel software paghe", "paghe", "sistema esterno nominato ma il soggetto e' il portale" },
        { "Requirements", "Il portale calcola le buste paga e non le trasferte", "paghe", "negazione in un'altra proposizione" },
        { "Requirements", "Il portale non gestisce le trasferte ma calcola i ratei", "ratei", "negazione prima dell'avversativa" },
        { "Requirements", "Il software paghe gestisce i ratei mentre il portale calcola le buste paga", "paghe", "due soggetti contrapposti" },
        { "Dependencies", "Integrazione con il software paghe per importare i dati", "integrazione-paghe,paghe", "integrazione inventata col sistema esterno: e' anche una menzione non attribuita" },
    };

    [Theory]
    [MemberData(nameof(PayrollStatements))]
    public void Payroll_statements_fail_only_when_our_system_is_the_affirmed_subject(string section, string text, string expected, string why)
    {
        var findings = Of(N2, "ferie").Add(Enum.Parse<Sec>(section), text, from: 2).Analysis();

        foreach (var key in new[] { "paghe", "ratei", "integrazione-paghe" })
        {
            var wanted = expected.Split(',').Contains(key);
            Assert.True(Flags(findings, key) == wanted, $"{why}: «{key}» {(wanted ? "non rilevato" : "segnalato a torto")}: {string.Join("; ", findings.Failures)}");
        }
    }

    [Fact]
    public void An_external_responsibility_in_dependencies_is_noted_for_review_not_failed()
    {
        var findings = Of(N2, "ferie").Add(Sec.Dependencies, "Il calcolo di buste paga e ratei di ferie resta nel software paghe", from: 2).Analysis();

        Assert.True(findings.Passed, string.Join("; ", findings.Failures));
        Assert.Contains(findings.Notes, n => n.Contains("«paghe» attribuita a un sistema esterno in Dependencies", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Il calcolo di buste paga e ratei di ferie resta nel software paghe", false)]
    [InlineData("Il portale calcola le buste paga", true)]
    [InlineData("Il portale gestisce i ratei di ferie maturati", true)]
    public void The_draft_applies_the_same_subject_rule(string line, bool fails)
    {
        var findings = Of(N2, "ferie").Document($"\n## Dipendenze\n\n- {line}\n");

        Assert.Equal(fails, Flags(findings, "paghe") || Flags(findings, "ratei"));
    }

    // ------------------------------------------------------------------------------------------------ (b) richiesta e approvazione in voci distinte

    [Fact]
    public void Request_and_approval_in_separate_items_with_their_own_subjects_cover_the_expectation()
    {
        var findings = Of(N2, "ferie").Remove("richiesta")
            .Add(Sec.Requirements, "Il dipendente invia la richiesta di ferie o permessi")
            .Add(Sec.Requirements, "Il responsabile diretto approva o rifiuta")
            .Analysis();

        Assert.False(Flags(findings, "richiesta"), string.Join("; ", findings.Failures));
    }

    [Theory]
    [InlineData("Il sistema approva o rifiuta automaticamente", "soggetto diverso: le parti non si sommano")]
    [InlineData("Il responsabile diretto approva la richiesta", "predicato «rifiuta» perso")]
    public void Request_parts_with_a_wrong_subject_or_a_lost_predicate_are_an_omission(string approval, string why)
    {
        var findings = Of(N2, "ferie").Remove("richiesta")
            .Add(Sec.Requirements, "Il dipendente invia la richiesta di ferie o permessi")
            .Add(Sec.Requirements, approval)
            .Analysis();

        Assert.True(findings.Failures.Any(f => f.StartsWith("omissione: «richiesta»", StringComparison.Ordinal)), why);
    }

    [Fact]
    public void Every_atomic_part_must_cite_the_pm_message_that_states_it()
    {
        var findings = Of(N2, "ferie").Remove("richiesta")
            .Add(Sec.Requirements, "Il dipendente invia la richiesta di ferie o permessi", from: 1)
            .Add(Sec.Requirements, "Il responsabile diretto approva o rifiuta", from: 4)
            .Analysis();

        Assert.Contains(findings.Failures, f => f.StartsWith("fonte errata: «richiesta»", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fact_removed_by_the_pm_correction_is_noted_while_a_fact_lost_by_the_generator_still_fails()
    {
        // V7 ferie: l'unica voce con «responsabile diretto» dice anche «approva o rifiuta». La correzione simulata non la tocca piu'
        // (PmCorrection, sessione 1C); qui la riscrive un PM in modo deliberato. La bozza segue la revisione confermata: non e' una
        // perdita del generatore.
        const string approves = "Il responsabile diretto approva o rifiuta la richiesta";
        var run = Of(N2, "ferie").Remove("richiesta").Remove("solo-responsabile")
            .Add(Sec.Requirements, "Il dipendente invia la richiesta di ferie o permessi")
            .Add(Sec.Requirements, approves);
        Assert.False(Flags(run.Analysis(), "richiesta"));

        var (simulated, _) = run.Confirm();
        Assert.Contains(simulated.Requirements, i => i.Text == approves);
        var rewritten = simulated with { Requirements = [.. simulated.Requirements.Select(i => i.Text == approves ? i with { Text = run.Script.Correction.EditedText } : i)] };
        var rendered = AcceptanceOracle.RenderMarkdown(rewritten, run.Script.Name);

        var withAi = AcceptanceChecker.CheckDocument(run.Script, rendered, rewritten, true, run.Content);
        Assert.False(Flags(withAi, "richiesta"), string.Join("; ", withAi.Failures));
        Assert.Contains(withAi.Notes, n => n.Contains("«richiesta» presente nell'analisi AI ma non nella revisione confermata", StringComparison.Ordinal));

        Assert.Contains(AcceptanceChecker.CheckDocument(run.Script, rendered, rewritten, true).Failures,
            f => f.Contains("«richiesta» (assente gia' dalla revisione confermata", StringComparison.Ordinal));

        var ideal = Of(N2, "ferie");
        var (confirmed, markdown) = ideal.Confirm();
        var dropped = string.Join('\n', markdown.Split('\n').Where(l => !l.Contains("approva o la rifiuta", StringComparison.Ordinal)));
        Assert.Contains(AcceptanceChecker.CheckDocument(ideal.Script, dropped, confirmed, true, confirmed).Failures,
            f => f.Contains("«richiesta» (presente nella revisione confermata: persa nella generazione)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ (c) ordini e spedizione in voci distinte

    [Fact]
    public void Orders_and_shipment_in_separate_items_cover_the_expectation_in_analysis_and_draft()
    {
        var run = Of(N5, "boreale").Remove("ordini")
            .Add(Sec.Requirements, "I clienti inseriscono gli ordini")
            .Add(Sec.Requirements, "I clienti seguono la spedizione");

        Assert.False(Flags(run.Analysis(), "ordini"));
        Assert.False(Flags(run.Document(withAi: true), "ordini"));
    }

    [Fact]
    public void Shipment_followed_by_someone_else_does_not_complete_the_customer_fact()
    {
        var run = Of(N5, "boreale").Remove("ordini")
            .Add(Sec.Requirements, "I clienti inseriscono gli ordini")
            .Add(Sec.Requirements, "Il corriere gestisce la spedizione");

        Assert.True(Flags(run.Analysis(), "ordini"));
        Assert.True(Flags(run.Document(withAi: true), "ordini"));
    }

    // ------------------------------------------------------------------------------------------------ Colorweb: qualificatori omessi o contraddetti

    [Theory]
    [InlineData("Integrazione con il gestionale ordini Colorweb", "/tempo reale/ assente ovunque", "/obbligatori/ assente ovunque")]
    [InlineData("Integrazione obbligatoria con il gestionale ordini Colorweb", "/tempo reale/ assente ovunque", null)]
    [InlineData("Integrazione in tempo reale con il gestionale ordini Colorweb", "/obbligatori/ assente ovunque", null)]
    public void A_colorweb_integration_without_its_qualifiers_is_a_qualifier_omission(string text, string missing, string? alsoMissing)
    {
        var findings = Of(N5, "boreale").Remove("integrazione").Add(Sec.Dependencies, text, from: 3).Analysis();

        var omission = Assert.Single(findings.Failures, f => f.StartsWith("omissione: «integrazione»", StringComparison.Ordinal));
        Assert.Contains("qualificatore o dettaglio assente", omission, StringComparison.Ordinal);
        Assert.DoesNotContain("legame perso", omission, StringComparison.Ordinal);
        Assert.Contains(missing, omission, StringComparison.Ordinal);
        if (alsoMissing is not null)
        {
            Assert.Contains(alsoMissing, omission, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Integrazione obbligatoria in tempo reale con il gestionale ordini Colorweb", false)]
    [InlineData("Gli ordini arrivano in tempo reale al gestionale Colorweb, integrazione obbligatoria", false)]
    [InlineData("Integrazione facoltativa con il gestionale ordini Colorweb", true)]
    [InlineData("L'integrazione con Colorweb non è obbligatoria", true)]
    [InlineData("Integrazione obbligatoria con Colorweb, ma non in tempo reale", true)]
    [InlineData("Integrazione con Colorweb con invio notturno degli ordini", true)]
    [InlineData("Gli ordini sono inviati a Colorweb in modo differito", true)]
    public void A_colorweb_integration_weakened_against_the_pm_fails_even_when_phrased_as_a_negation(string text, bool weakened)
    {
        var run = Of(N5, "boreale").Add(Sec.Dependencies, text, from: 3);

        Assert.Equal(weakened, Flags(run.Analysis(), "colorweb-indebolita"));
        Assert.Equal(weakened, Flags(run.Document($"\n## Dipendenze\n\n- {text}\n"), "colorweb-indebolita"));
    }

    // ------------------------------------------------------------------------------------------------ barcode: legame perso, distinto da Colorweb

    [Fact]
    public void A_barcode_reader_separated_from_loading_and_unloading_is_a_lost_link_not_a_missing_qualifier()
    {
        // V7 proxy 291: lo strumento resta negli obiettivi, l'operazione perde lo strumento.
        var findings = Of(N3, "magazzino").Remove("barcode")
            .Add(Sec.Goals, "Gestire le scorte del magazzino ricambi con un lettore di codici a barre")
            .Add(Sec.Requirements, "I magazzinieri registrano carichi e scarichi")
            .Analysis();

        var omission = Assert.Single(findings.Failures, f => f.StartsWith("omissione: «barcode»", StringComparison.Ordinal));
        Assert.Contains("legame perso fra voci", omission, StringComparison.Ordinal);
        Assert.Contains("/barre/ solo in un'altra voce (Goals)", omission, StringComparison.Ordinal);
        Assert.DoesNotContain("qualificatore", omission, StringComparison.Ordinal);
    }

    [Fact]
    public void The_barcode_reader_bound_to_the_operation_satisfies_the_expectation()
    {
        var findings = Of(N3, "magazzino").Remove("barcode")
            .Add(Sec.Requirements, "I magazzinieri registrano carichi e scarichi con il lettore di codici a barre")
            .Analysis();

        Assert.False(Flags(findings, "barcode"));
    }

    // ------------------------------------------------------------------------------------------------ errori veri che restano errori

    [Fact]
    public void An_open_conflict_turned_into_a_requirement_still_fails_in_analysis_and_draft()
    {
        var run = Of("N4-conflitti", "fornitori").Add(Sec.Requirements, "La qualifica va approvata entro 5 giorni lavorativi dall'invio", from: 2);

        Assert.True(Flags(run.Analysis(), "tempi-5"));
        Assert.True(Flags(run.Document("\n## Requisiti aggiuntivi\n\n- I fornitori accedono con SSO aziendale\n"), "accesso-scelto"));
    }

    [Fact]
    public void An_invented_citation_still_fails_in_the_draft()
    {
        var findings = Of(N2, "ferie").Document($"\n## Note\n\n- Il software paghe resta esterno [fonte:{Guid.NewGuid()}]\n");

        Assert.Contains(findings.Failures, f => f.Contains("non e' una fonte della revisione confermata", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ riesecuzione sugli output V7 catturati

    private const string TraceVariable = "BPROJECTS_ACCEPTANCE_TRACE";

    /// <summary>
    /// Riesegue il verificatore sugli output reali V7 (analisi AI, revisione confermata, bozza) letti dalla traccia del proxy. La traccia
    /// non e' versionata (README di accettazione: i contenuti restano in locale): senza file la prova risulta ignorata, mai superata.
    /// Attese fissate dall'audit V2 prima di questa modifica: restano solo le omissioni reali di barcode (legame) e Colorweb (qualificatori).
    /// </summary>
    [Fact]
    public void V7_captured_outputs_keep_only_the_real_omissions()
    {
        var path = Environment.GetEnvironmentVariable(TraceVariable)
            ?? Path.Combine(AcceptanceFixtureTests.RepoRoot(), "docs", "acceptance-trace", "trace-qwen3.8-flash-next-smart-v7.md");
        Assert.SkipUnless(File.Exists(path), $"{TraceVariable} non impostata e traccia V7 assente: {path}");

        var runs = V7Trace.Read(path);
        Assert.Equal(6, runs.Count);
        var results = runs.ToDictionary(r => r.Script.Key, r =>
        {
            var findings = AcceptanceChecker.CheckAnalysis(r.Script, r.Ai, r.UserIndex, r.MessageIds);
            findings.Merge(AcceptanceChecker.CheckDocument(r.Script, r.Markdown, r.Confirmed, correctionApplied: true, r.Ai));
            return findings;
        });
        string Report() => string.Join("\n", results.Select(r => $"{r.Key}: {string.Join(" | ", r.Value.Failures)}"));

        foreach (var key in new[] { "officina", "ferie", "fornitori", "aurora" })
        {
            Assert.True(results[key].Passed, Report());
        }

        Assert.Contains(results["ferie"].Notes, n => n.Contains("«richiesta» presente nell'analisi AI", StringComparison.Ordinal));
        Assert.Contains(results["ferie"].Notes, n => n.Contains("attribuita a un sistema esterno in Dependencies", StringComparison.Ordinal));

        Assert.True(results["magazzino"].Failures.Count == 2, Report());
        Assert.Contains("omissione: «barcode»", results["magazzino"].Failures[0], StringComparison.Ordinal);
        Assert.Contains("legame perso fra voci", results["magazzino"].Failures[0], StringComparison.Ordinal);
        Assert.StartsWith("omissione nella bozza: «barcode» (assente gia'", results["magazzino"].Failures[1], StringComparison.Ordinal);

        Assert.True(results["boreale"].Failures.Count == 2, Report());
        Assert.Contains("omissione: «integrazione»", results["boreale"].Failures[0], StringComparison.Ordinal);
        Assert.Contains("qualificatore o dettaglio assente", results["boreale"].Failures[0], StringComparison.Ordinal);
        Assert.StartsWith("omissione nella bozza: «integrazione» (assente gia'", results["boreale"].Failures[1], StringComparison.Ordinal);
    }
}

/// <summary>Lettura minima della traccia Markdown del proxy V7: per progetto, messaggi con id, analisi AI, revisione confermata, bozza.</summary>
internal static class V7Trace
{
    internal sealed record Run(ProjectScript Script, AnalysisContent Ai, AnalysisContent Confirmed, string Markdown, Dictionary<Guid, int> UserIndex, HashSet<Guid> MessageIds);

    private static readonly Regex ProjectHeading = new(@"^# (N\d-[\w-]+) / (\w+)$");
    private static readonly Regex MessageHeader = new(@"^\[messaggio id=([0-9a-f-]{36}) sequenza=\d+\]$");

    public static IReadOnlyList<Run> Read(string path)
    {
        var runs = new List<Run>();
        var lines = File.ReadAllLines(path);
        var fence = false;
        var sections = new List<(string Case, string Key, int Start)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
            }
            else if (!fence && ProjectHeading.Match(lines[i]) is { Success: true } m)
            {
                sections.Add((m.Groups[1].Value, m.Groups[2].Value, i));
            }
        }

        for (var n = 0; n < sections.Count; n++)
        {
            var (caseId, key, start) = sections[n];
            var end = n + 1 < sections.Count ? sections[n + 1].Start : lines.Length;
            var script = AcceptanceFixtureTests.Script(caseId, key);
            var blocks = Blocks(lines, start, end);

            var userIndex = new Dictionary<Guid, int>();
            var messageIds = new HashSet<Guid>();
            foreach (var block in blocks.Select(b => b.Text.Split('\n')))
            {
                if (block.Length > 1 && MessageHeader.Match(block[0]) is { Success: true } header)
                {
                    var id = Guid.Parse(header.Groups[1].Value);
                    messageIds.Add(id);
                    var index = System.Array.IndexOf(script.Messages, string.Join('\n', block.Skip(1)).Trim());
                    if (index >= 0)
                    {
                        userIndex[id] = index + 1;
                    }
                }
            }

            var responses = blocks.Where(b => b.Response).Select(b => b.Text).ToList();
            var ai = responses.Last(r => r.TrimStart().StartsWith('{') && r.Contains("\"schemaVersion\"", StringComparison.Ordinal));
            var confirmed = blocks.SelectMany(b => b.Text.Split('\n')).Last(l => l.StartsWith('{') && l.Contains("\"validationState\":\"PmConfirmed\"", StringComparison.Ordinal));
            runs.Add(new(script, AnalysisContentJson.Deserialize(ai), AnalysisContentJson.Deserialize(confirmed), responses[^1], userIndex, messageIds));
        }

        return runs;
    }

    /// <summary>Blocchi recintati della sezione; <c>Response</c> se seguono «### Risposta del modello».</summary>
    private static List<(string Text, bool Response)> Blocks(string[] lines, int start, int end)
    {
        var blocks = new List<(string, bool)>();
        var response = false;
        for (var i = start; i < end; i++)
        {
            if (lines[i] == "### Risposta del modello")
            {
                response = true;
            }
            else if (lines[i].StartsWith("```", StringComparison.Ordinal))
            {
                var close = System.Array.FindIndex(lines, i + 1, l => l.StartsWith("```", StringComparison.Ordinal));
                blocks.Add((string.Join('\n', lines[(i + 1)..close]), response));
                response = false;
                i = close;
            }
            else if (lines[i].StartsWith("### ", StringComparison.Ordinal))
            {
                response = false;
            }
        }

        return blocks;
    }
}
