using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T079 (GOLD DET, PG + modello FINTO) — la riconciliazione globale sul corpus T002: fatti distanti (txt-long) e conflitto fra documenti (txt-conflict-a/b)
/// oltre il contesto, chiavi letterali condivise e non primarie, evidenze senza chiavi (unclassified in coda), candidati espansi/confrontati fino a Open. La
/// QUALITÀ semantica del gold è valutata a parte dalla coverage: una fixture che perde un conflitto gold produce FAIL semantico anche con ogni EvidenceId
/// mappato e il run Completed. Il modello finto è deterministico: nessuna recall assoluta è pretesa, solo che ciò che registra venga preservato.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentGlobalReconciliationTests : ReconcileRig
{
    private static readonly JsonElement Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(DocumentTestEnvironment.FixturesDirectory, "manifest.json"))).RootElement;

    internal sealed record Fact(string Id, string Quote, string? Subject, string? Scope);

    internal sealed record GoldConflict(string Id, string[] FactIds);

    private static string Quote(string factId) => Manifest.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("id").GetString() == factId).GetProperty("quote").GetString()!;

    internal static IReadOnlyList<GoldConflict> Gold(params string[] conflictIds) =>
        [.. Manifest.GetProperty("conflicts").EnumerateArray().Where(c => conflictIds.Contains(c.GetProperty("id").GetString()))
            .Select(c => new GoldConflict(c.GetProperty("id").GetString()!, [.. c.GetProperty("factIds").EnumerateArray().Select(x => x.GetString()!)]))];

    internal static readonly string[] AllGold = ["conflict-users", "conflict-payment"];

    /// <summary>Corpus: i fatti di txt-long (inizio/metà/fine, tra cui il conflitto distante F01/F13) e la coppia 30/60 giorni dei due file; ciascun fatto in un documento con padding.</summary>
    internal static List<Fact> Corpus()
    {
        var longFacts = Manifest.GetProperty("facts").EnumerateArray().Where(f => f.GetProperty("fileId").GetString() == "txt-long").Select(f => f.GetProperty("id").GetString()!).ToList();
        var ids = longFacts.Concat(["conflict-pay-a", "conflict-pay-b"]).ToList();
        string? Subject(string id, int n) => id switch { "txt-long-F01" => "a1", "conflict-pay-a" => "a2", "txt-long-F13" => "z1", "conflict-pay-b" => "z2", _ => $"m{n:D2}" };
        string? Scope(string id) => id switch { "txt-long-F01" or "txt-long-F13" => "utenti", "conflict-pay-a" or "conflict-pay-b" => "pagamento", _ => null };
        return [.. ids.Select((id, n) => new Fact(id, Quote(id), Subject(id, n), Scope(id)))];
    }

    internal static string Statement(Fact fact, int n) => fact.Quote + " " + string.Join(' ', Enumerable.Range(0, 100).Select(i => $"p{n}x{i}"));

    // ================================================================ qualità semantica separata dalla coverage

    /// <summary>Valutatore gold di test: un conflitto gold è presente se un conflitto dell'analisi riporta, nelle sue alternative, le fonti di tutti i fatti gold coinvolti.</summary>
    internal static IReadOnlyList<string> MissingGoldConflicts(DocumentAnalysisContent content, IEnumerable<GoldConflict> gold)
    {
        var quotes = content.Sources.ToDictionary(s => s.Id, s => s.Quote ?? string.Empty);
        return [.. gold.Where(g => !content.Conflicts.Any(c =>
        {
            var cited = c.Alternatives.SelectMany(a => a.EvidenceRefs).Select(id => quotes[id]).ToList();
            return g.FactIds.All(f => cited.Any(q => q.Contains(Quote(f), StringComparison.Ordinal)));
        })).Select(g => g.Id)];
    }

    internal sealed class Gate(IEnumerable<Fact> corpus, bool dropPayment = false, Func<Parsed, ReconcileResult, ReconcileResult>? tweak = null)
    {
        public readonly Dictionary<string, (Guid Evidence, string Hash)> Known = [];
        private readonly List<Fact> _corpus = [.. corpus];

        private (string A, string B, string Rationale)[] Pairs => dropPayment
            ? [("txt-long-F01", "txt-long-F13", "500 utenti simultanei contro almeno 2000")]
            : [("txt-long-F01", "txt-long-F13", "500 utenti simultanei contro almeno 2000"), ("conflict-pay-a", "conflict-pay-b", "pagamento a 30 giorni contro 60 giorni")];

        public string Respond(DocumentChatWorkflowTests.ChatRequest request) => ReconcileRig.Respond(request, (p, result0) =>
        {
            foreach (var v in p.Voci)
            {
                foreach (var f in _corpus.Where(f => v.Statement.StartsWith(f.Quote, StringComparison.Ordinal)))
                {
                    Known[f.Id] = (v.Evidence, v.Hash);
                }
            }

            bool Reopens(string id) => p.Voci.Any(v => v.Statement.StartsWith(Quote(id), StringComparison.Ordinal));
            var result = result0;
            var produced = Stage(p, result, Reopens);
            return tweak is null ? produced : tweak(p, produced);
        });

        private ReconcileResult Stage(Parsed p, ReconcileResult result, Func<string, bool> Reopens)
        {
            switch (p.Stage)
            {
                case "expand":
                    var found = Pairs.Where(x => (Reopens(x.A) || Reopens(x.B)) && Known.ContainsKey(x.A) && Known.ContainsKey(x.B)).Select(x => Candidate(Known[x.A], Known[x.B], x.Rationale, p)).ToList();
                    return found.Count == 0 ? result : result with { Candidates = found, NoFindingsReason = null };
                case "compare":
                    return result with
                    {
                        Dispositions = [new CandidateDisposition { CandidateId = p.FocusId!, State = CandidateState.Open, ChildCandidateIds = [], ComparisonOutputHashes = [p.InputHash], Reason = "alternative integrali da sottoporre al PM" }],
                    };
                default:
                    return result;
            }
        }

        private static ReconcileCandidate Candidate((Guid Evidence, string Hash) from, (Guid Evidence, string Hash) to, string rationale, Parsed p)
        {
            var l = ReconciliationFixtures.Endpoint(from.Evidence, from.Hash);
            var r = ReconciliationFixtures.Endpoint(to.Evidence, to.Hash);
            return new()
            {
                CandidateId = ReconciliationIds.CandidateId(CandidateKind.Conflict, l, r, ReconcileProposedType.Contradicts, p.SourceHash), Kind = CandidateKind.Conflict, Left = l, Right = r,
                ProposedType = ReconcileProposedType.Contradicts, Rationale = rationale, Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = [p.BatchId], ParentCandidateId = p.FocusId,
            };
        }
    }

    // ================================================================ scenari

    [Fact]
    public async Task Distant_and_inter_document_gold_conflicts_beyond_the_context_are_preserved_with_every_fact_mapped_and_the_gold_quality_passes()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var parsed = model.Requests.Select(r => Parse(r.AllText)).ToList();
        Assert.True(parsed.Count(p => p.Stage == "leaf" && p.View == ReconcileView.A) > 1);   // l'intero catalogo A non entra in una chiamata
        Assert.True(parsed.Count(p => p.Stage == "leaf" && p.View == ReconcileView.B) > 1);   // né il B
        Assert.Contains(parsed, p => p.Stage == "expand");
        Assert.Contains(parsed, p => p.Stage == "compare");
        Assert.All(model.Requests, r => Assert.Single(r.Messages));

        // Coverage: ogni evidenza attesa è mappata e ha un collegamento di analisi.
        Assert.Equal(corpus.Count, await Count("SELECT count(DISTINCT \"EvidenceId\") FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(2 * corpus.Count, await Count("SELECT count(*) FROM \"RunCatalogMembers\" WHERE \"RunId\" = @r", ("r", run)));

        var content = await ContentAsync(run);
        Assert.Equal(corpus.Count, content.Requirements.Count);
        Assert.Equal(2, content.Conflicts.Count);
        Assert.All(content.Conflicts, c =>
        {
            Assert.Equal(AnalysisConflictStatus.Open, c.Status);
            Assert.Null(c.Resolution);
            Assert.Equal(2, c.Alternatives.Count);
        });

        // Qualità: i due conflitti gold (distante entro txt-long e fra file) sono presenti con tutte le fonti;.
        Assert.Empty(MissingGoldConflicts(content, Gold(AllGold)));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"RunCandidateDispositions\" WHERE \"RunId\" = @r AND \"State\" = 'Open'", ("r", run)));
    }

    [Fact]
    public async Task A_gold_conflict_deliberately_lost_by_the_fake_is_a_semantic_FAIL_even_with_every_evidence_id_mapped_and_the_run_completed()
    {
        var corpus = Corpus();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus, dropPayment: true);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };

        var result = await RunAsync(claim, model);

        // Strutturalmente tutto PASS: la coverage per ID è al 100% e il run termina.
        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(corpus.Count, await Count("SELECT count(DISTINCT \"EvidenceId\") FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        var content = await ContentAsync(run);
        Assert.Equal(corpus.Count, content.Requirements.Count);

        // Ma il gold semantico è perso: FAIL del valutatore di qualità, non della coverage (e il conflitto che il fake ha registrato resta).
        Assert.Equal(["conflict-payment"], MissingGoldConflicts(content, Gold(AllGold)));
        Assert.Empty(MissingGoldConflicts(content, Gold("conflict-users")));
        Assert.Single(content.Conflicts);
    }

    [Fact]
    public async Task Evidences_without_literal_keys_stay_in_the_tail_of_both_catalogs_and_are_all_visited_without_becoming_orphans()
    {
        var corpus = Corpus().Select(f => f.Id is "txt-long-F03" or "txt-long-F04" or "txt-long-F05" ? f with { Subject = null, Scope = null } : f).ToList();
        var keyless = corpus.Where(f => f.Subject is null).Select(f => f.Quote).ToList();
        var (run, claim) = await ArrangeAsync(corpus);
        var gate = new Gate(corpus);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => gate.Respond(r) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var parsed = model.Requests.Select(r => Parse(r.AllText)).ToList();
        foreach (var view in new[] { ReconcileView.A, ReconcileView.B })
        {
            var order = parsed.Where(p => p.Stage == "leaf" && p.View == view).SelectMany(p => p.Voci).Select(v => v.Statement).ToList();
            Assert.Equal(corpus.Count, order.Distinct().Count());   // ogni evidenza visitata: nessun orfano
            if (view == ReconcileView.A)
            {
                Assert.Equal(keyless.Order(StringComparer.Ordinal), order.TakeLast(keyless.Count).Select(s => keyless.Single(q => s.StartsWith(q, StringComparison.Ordinal))).Order(StringComparer.Ordinal));   // senza chiavi: in coda
            }
            else
            {
                Assert.Equal(keyless.Count, order.Count(s => keyless.Any(q => s.StartsWith(q, StringComparison.Ordinal))));   // in B quasi tutte sono unclassified ma restano visitate
            }
        }

        Assert.Equal(2 * corpus.Count, await Count("SELECT count(*) FROM \"RunCatalogMembers\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Empty(MissingGoldConflicts(await ContentAsync(run), Gold(AllGold)));
    }

    // ================================================================ supporto

    private async Task<DocumentAnalysisContent> ContentAsync(Guid run) =>
        DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

    private async Task<(Guid Run, AnalysisRunClaim Claim)> ArrangeAsync(List<Fact> corpus)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 0; n < corpus.Count; n++)
        {
            await NewDocument(client, project, $"doc{n}", Statement(corpus[n], n), corpus[n].Subject, corpus[n].Scope);
        }

        var run = await StartRunAsync(client, project);
        return (run, await ClaimAsync(run));
    }
}
