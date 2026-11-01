using System.Text.Json.Nodes;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

/// <summary>
/// Verifica indipendente (sessione 1B) del verificatore di accettazione dopo il commit 66205ea. Frasi scelte dal revisore, non dall'autore
/// del verificatore: i controfattuali dell'incarico e alcune sonde di sensibilita'. Le sonde che documentano un limite NOTO
/// (<see cref="Known_limits_of_the_checker"/>) asseriscono il comportamento attuale, cosi' un cambiamento futuro e' visibile.
/// </summary>
public sealed class AcceptanceCheckerIndependentReviewTests
{
    private const string N2 = "N2-negazioni";
    private const string N5 = "N5-progetti-distinti";

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

    private sealed class Run
    {
        private readonly AcceptanceFixtureTests.Ids ids;
        private readonly JsonObject json;

        public Run(string caseId, string key)
        {
            Script = AcceptanceFixtureTests.Script(caseId, key);
            ids = AcceptanceFixtureTests.NewIds(Script);
            json = AcceptanceOracle.BuildAnalysis(Script, ids.Project, ids.Analysis, ids.Users.Length, ids.Users);
        }

        public ProjectScript Script { get; }

        private string Source(int n)
        {
            var message = ids.Users[n - 1].ToString();
            var existing = json["sources"]!.AsArray().FirstOrDefault(x => x!["messageId"]!.GetValue<string>() == message);
            if (existing is not null)
            {
                return existing["id"]!.GetValue<string>();
            }

            var id = Guid.NewGuid().ToString();
            json["sources"]!.AsArray().Add(new JsonObject { ["id"] = id, ["kind"] = "Message", ["messageId"] = message, ["messageRole"] = "user", ["revisionId"] = null });
            return id;
        }

        public Run Remove(string key)
        {
            var text = Script.Facts.Single(f => f.Key == key).Text;
            foreach (var section in Enum.GetValues<Sec>())
            {
                var array = Array(json, section);
                foreach (var item in array.Where(i => i!["text"]!.GetValue<string>() == text).ToList())
                {
                    array.Remove(item);
                }
            }

            return this;
        }

        public Run Add(Sec section, string text, int from)
        {
            Array(json, section).Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["text"] = text,
                ["classification"] = "Declared",
                ["evidenceRefs"] = new JsonArray(JsonValue.Create(Source(from))),
            });
            return this;
        }

        public AnalysisContent Content => AnalysisContentJson.Deserialize(json.ToJsonString());

        public AcceptanceFindings Analysis() => AcceptanceChecker.CheckAnalysis(Script, Content, ids.Index, ids.All);

        public AcceptanceFindings Document(string extra)
        {
            var confirmed = AcceptanceFixtureTests.Corrected(Content, Script, Guid.NewGuid(), AnalysisContentState.PmConfirmed);
            return AcceptanceChecker.CheckDocument(Script, AcceptanceOracle.RenderMarkdown(confirmed, Script.Name) + extra, confirmed, correctionApplied: true, Content);
        }
    }

    private static bool Flags(AcceptanceFindings f, string key) => f.Failures.Any(x => x.Contains($"«{key}»", StringComparison.Ordinal));

    private static bool Omits(AcceptanceFindings f, string key) => f.Failures.Any(x => x.StartsWith($"omissione: «{key}»", StringComparison.Ordinal));

    // ------------------------------------------------------------------------------------------------ paghe e ratei, polarita' per proposizione

    private static bool Affirms(string forbidKey, string text)
    {
        var script = AcceptanceFixtureTests.Script(N2, "ferie");
        return AcceptanceChecker.Affirms(script.Forbids.Single(f => f.Key == forbidKey), AcceptanceChecker.Norm(text));
    }

    [Theory]
    [InlineData("ratei", "Il software paghe calcola i ratei", false)]
    [InlineData("ratei", "Il portale calcola i ratei", true)]
    [InlineData("ratei", "Il portale non calcola i ratei", false)]
    [InlineData("paghe", "Il portale calcola le paghe ma non le trasferte", true)]
    [InlineData("paghe", "Il portale non calcola le paghe ma calcola le trasferte", false)]
    [InlineData("ratei", "Il portale legge i ratei dal software paghe", true)]
    [InlineData("ratei", "Il portale usa i dati del software paghe per calcolare i ratei", true)]
    [InlineData("ratei", "Il software paghe calcola i ratei e il portale li mostra", true)]
    [InlineData("paghe", "Il portale calcola le buste paga dei dipendenti", true)]
    public void Payroll_polarity_and_subject(string forbid, string text, bool violation) =>
        Assert.Equal(violation, Affirms(forbid, text));

    [Theory]
    [InlineData("Il portale integra il software paghe")]
    [InlineData("Il portale importa i ratei dal software paghe")]
    [InlineData("Sincronizzazione del portale con il software delle paghe")]
    public void Undeclared_integration_with_the_payroll_software_is_flagged(string text)
    {
        var findings = new Run(N2, "ferie").Add(Sec.Dependencies, text, from: 2).Analysis();

        Assert.True(Flags(findings, "integrazione-paghe"), string.Join("; ", findings.Failures));
    }

    // ------------------------------------------------------------------------------------------------ Colorweb

    [Fact]
    public void Colorweb_realtime_and_mandatory_is_compliant()
    {
        var findings = new Run(N5, "boreale").Remove("integrazione")
            .Add(Sec.Dependencies, "Colorweb è integrato in tempo reale ed è obbligatorio", from: 3).Analysis();

        Assert.False(Omits(findings, "integrazione"), string.Join("; ", findings.Failures));
        Assert.False(Flags(findings, "colorweb-indebolita"), string.Join("; ", findings.Failures));
    }

    [Theory]
    [InlineData("Colorweb è integrato ma può essere sincronizzato di notte")]
    [InlineData("Colorweb è integrato in tempo reale")]
    [InlineData("Colorweb è integrato ed è obbligatorio")]
    public void Colorweb_losing_a_qualifier_is_an_omission_with_the_missing_part_named(string text)
    {
        var findings = new Run(N5, "boreale").Remove("integrazione").Add(Sec.Dependencies, text, from: 3).Analysis();

        Assert.True(Omits(findings, "integrazione"), string.Join("; ", findings.Failures));
        Assert.Contains(findings.Failures, f => f.Contains("qualificatore o dettaglio assente", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Colorweb è integrato in tempo reale, ma non è obbligatorio")]
    [InlineData("Integrazione facoltativa con Colorweb in tempo reale, obbligatoria solo in produzione")]
    public void Colorweb_weakened_but_with_the_keywords_present_is_still_caught_by_the_ban(string text)
    {
        var findings = new Run(N5, "boreale").Remove("integrazione").Add(Sec.Dependencies, text, from: 3).Analysis();

        Assert.True(Flags(findings, "colorweb-indebolita"), string.Join("; ", findings.Failures));
    }

    [Fact]
    public void Colorweb_weakened_in_the_draft_is_caught()
    {
        var findings = new Run(N5, "boreale").Document("\n## Dipendenze\n\n- Colorweb è integrato in tempo reale ed è obbligatorio, ma può essere differito\n");

        Assert.True(Flags(findings, "colorweb-indebolita"), string.Join("; ", findings.Failures));
    }

    // ------------------------------------------------------------------------------------------------ ordini e spedizioni

    [Fact]
    public void Orders_and_shipments_in_two_items_with_the_customers_as_subject_are_compliant()
    {
        var findings = new Run(N5, "boreale").Remove("ordini")
            .Add(Sec.Requirements, "Gli ordini vengono inseriti dai clienti", from: 1)
            .Add(Sec.Requirements, "I clienti monitorano le spedizioni nel portale", from: 1).Analysis();

        Assert.False(Omits(findings, "ordini"), string.Join("; ", findings.Failures));
    }

    [Fact]
    public void Orders_and_shipments_with_an_external_courier_lose_the_customer_requirement()
    {
        var findings = new Run(N5, "boreale").Remove("ordini")
            .Add(Sec.Requirements, "Gli ordini vengono inseriti dai clienti", from: 1)
            .Add(Sec.Requirements, "Le spedizioni sono monitorate dal corriere esterno", from: 1).Analysis();

        Assert.True(Omits(findings, "ordini"), string.Join("; ", findings.Failures));
    }

    /// <summary>
    /// DISCREPANZA con l'esempio dell'incarico: «le spedizioni sono monitorate dal portale» (senza «clienti») NON copre l'attesa, perche'
    /// la parte atomica richiede il soggetto cliente. E' una scelta restrittiva (nessun falso negativo), ma un potenziale falso positivo.
    /// </summary>
    [Fact]
    public void Orders_and_shipments_monitored_by_the_portal_without_customer_subject_is_flagged_today()
    {
        var findings = new Run(N5, "boreale").Remove("ordini")
            .Add(Sec.Requirements, "Gli ordini vengono inseriti dai clienti", from: 1)
            .Add(Sec.Requirements, "Le spedizioni sono monitorate dal portale", from: 1).Analysis();

        Assert.True(Omits(findings, "ordini"), string.Join("; ", findings.Failures));
    }

    // ------------------------------------------------------------------------------------------------ limiti noti (asseriti come oggi)

    [Fact]
    public void Known_limits_of_the_checker()
    {
        // FN: una parola della lista di negazione ovunque nella stessa proposizione esenta la violazione (anche «senza», «fuori»).
        Assert.False(Affirms("ratei", "Il portale calcola i ratei senza intervento manuale"));
        Assert.False(Affirms("ratei", "Il portale calcola i ratei, anche fuori orario"));
        Assert.False(Affirms("ratei", "Il portale calcola i ratei, il software paghe no"));

        // FN del divieto Colorweb: sinonimi non elencati («di notte», «sincronizzat…») con le parole chiave corrette presenti altrove.
        var extra = new Run(N5, "boreale").Add(Sec.Requirements, "Colorweb può essere sincronizzato di notte", from: 3).Analysis();
        Assert.False(Flags(extra, "colorweb-indebolita"));
        Assert.True(extra.Passed, string.Join("; ", extra.Failures));
    }

    // ------------------------------------------------------------------------------------------------ riesecuzione V7 (stampa)

    [Fact]
    public void V7_replay_dump()
    {
        var output = Environment.GetEnvironmentVariable("BPROJECTS_REVIEW_OUT");
        var trace = Environment.GetEnvironmentVariable("BPROJECTS_ACCEPTANCE_TRACE")
            ?? Path.Combine(AcceptanceFixtureTests.RepoRoot(), "docs", "acceptance-trace", "trace-qwen3.8-flash-next-smart-v7.md");
        Assert.SkipUnless(output is not null && File.Exists(trace), "BPROJECTS_REVIEW_OUT non impostata o traccia V7 assente");

        var lines = new List<string>();
        foreach (var r in V7Trace.Read(trace))
        {
            var f = AcceptanceChecker.CheckAnalysis(r.Script, r.Ai, r.UserIndex, r.MessageIds);
            var analysis = f.Failures.Count;
            f.Merge(AcceptanceChecker.CheckDocument(r.Script, r.Markdown, r.Confirmed, correctionApplied: true, r.Ai));
            lines.Add($"## {r.Script.Key}: failures={f.Failures.Count} (analisi={analysis}) passed={f.Passed}");
            lines.AddRange(f.Failures.Select(x => "  F " + x));
            lines.AddRange(f.Notes.Select(x => "  N " + x));
        }

        File.WriteAllLines(output!, lines);
    }
}
