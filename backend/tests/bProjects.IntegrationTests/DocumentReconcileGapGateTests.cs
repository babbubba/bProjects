using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using Microsoft.Extensions.Logging.Abstractions;

namespace bProjects.IntegrationTests;

/// <summary>
/// Gate anticipato T138 (contracts/ai-workflow.md §13.4): prova OFFLINE sul percorso reale degli attuali batch leaf/reduce/Cross/espansione/confronto della riconciliazione
/// gerarchica v4 (SDK MAF reale, PostgreSQL reale, modello FINTO con output controllati). Domanda: un Gap e il suo riscontro, collocati in foglie diverse, arrivano
/// SIMULTANEAMENTE e per intero (enunciato e fonti con provenienza risolvibile) nel payload realmente inviato a un batch del grafo ESISTENTE, senza nuove fasi o chiamate?
/// Le prove ispezionano i messaggi ricevuti dal client simulato, non gli identificatori dei DTO. Nessuna implementazione v5: sono prove di fattibilità del grafo attuale.
/// Le prove «Characterization_*» documentano un LIMITE del grafo: passano solo finché il limite esiste e si rompono se il grafo cambia.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentReconcileGapGateTests : ReconcileRig
{
    private const int Documents = 8;
    private const string GapMark = "GAPMARK";
    private const string FindMark = "RISMARK";

    // Layout degli EvidenceId (artificio dichiarato, NON garantito in un run reale): Gap (rango 1) e riscontro (rango 4) sono separati da due documenti della foglia
    // centrale (ranghi 2 e 3, ids ...0002/...0003) quindi cadono in foglie B diverse (le foglie B ordinano per EvidenceId) e in foglie A diverse (soggetti aaa e zzz), ma restano
    // adiacenti nel manifesto di espansione, che contiene solo le foglie A del candidato del server e ordina per EvidenceId.
    private static readonly Guid GapId = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid NearFindId = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly Guid Find2Id = Guid.Parse("00000000-0000-4000-8000-000000000005");
    private static readonly Guid FarFindId = Guid.Parse("ffffffff-0000-4000-8000-000000000002");

    private static string Filler(int n, int words) => string.Join(' ', Enumerable.Range(0, words).Select(i => $"w{n}x{i}"));

    private static string GapStatement => $"Requisito 1: {GapMark} quanti giorni di garanzia offre il prodotto? " + Filler(1, 80);

    private static string FindStatement => $"Requisito {Documents}: {FindMark} la scheda prodotto dichiara 45 giorni di garanzia. " + Filler(Documents, 80);

    private static string Find2Statement => $"Requisito 7: {Find2Mark} il listino conferma la garanzia di 45 giorni. " + Filler(7, 80);

    private const string Find2Mark = "RIS2MARK";

    private static string StatementOf(int n, bool secondFinding) =>
        n == 1 ? GapStatement : n == Documents ? FindStatement : n == 7 && secondFinding ? Find2Statement : $"Requisito {n}: " + Filler(n, 90);

    // ================================================================ modello finto

    /// <summary>
    /// Modello finto (output controllati): risposta valida reconcile-alias-1 per ogni batch; nell'espansione che vede INSIEME Gap e riscontro propone il collegamento fra i due,
    /// nel confronto risponde Compared. Usa solo gli alias del messaggio ricevuto: mai un'informazione che il batch non ha.
    /// </summary>
    private static DocumentChatWorkflowTests.FakeChatModel LinkModel(Func<int, bool>? failOn = null)
    {
        var seen = 0;
        DocumentChatWorkflowTests.FakeChatModel? model = null;
        model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r =>
            {
                var p = DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText);
                if (failOn is not null && failOn(Interlocked.Increment(ref seen)))
                {
                    throw new InvalidOperationException("rete caduta");
                }

                var gap = p.Voci.FirstOrDefault(v => v.Statement.Contains(GapMark, StringComparison.Ordinal)).Alias;
                var finds = p.Voci.Where(v => v.Statement.Contains(FindMark, StringComparison.Ordinal) || v.Statement.Contains(Find2Mark, StringComparison.Ordinal)).Select(v => v.Alias).ToList();
                return DocumentReconcileAliasWorkflowTests.Dto(p, (_, o) =>
                {
                    if (p.Stage == "expand" && gap is not null && finds.Count > 0)
                    {
                        o["candidates"] = new JsonArray([.. finds.Select((find, i) => (JsonNode)new JsonObject
                        {
                            ["key"] = $"f{i + 1}", ["left"] = gap, ["right"] = find, ["type"] = "Contradicts", ["basis"] = "Inferred",
                            ["rationale"] = "la domanda sulla garanzia e il dato della scheda prodotto",
                        })]);
                        o["noFindings"] = null;
                    }

                    return o;
                });
            },
        };
        return model;
    }

    // ================================================================ lettura dei payload ricevuti

    private sealed record Voce(string Alias, string Origin, string Kind, string Statement, IReadOnlyList<(string Alias, string Quote)> Sources);

    private static readonly Regex VoceBlock = new(
        @"<<<VOCE \[(?<a>E[1-9][0-9]*)\] origine=(?<o>\w+) tipo=(?<k>\w+) polarita=\w+>>>\n(?<s>.*)\n(?<f>(?:<<<FONTE \[S[1-9][0-9]*\] ruolo=\w+>>>\n.*\n<<<FINE FONTE>>>\n)+)<<<FINE VOCE>>>",
        RegexOptions.Compiled);

    private static readonly Regex SourceBlock = new(@"<<<FONTE \[(?<a>S[1-9][0-9]*)\] ruolo=\w+>>>\n(?<q>.*)\n<<<FINE FONTE>>>", RegexOptions.Compiled);

    private static IReadOnlyList<Voce> Voci(string text) =>
        [.. VoceBlock.Matches(text).Select(m => new Voce(
            m.Groups["a"].Value, m.Groups["o"].Value, m.Groups["k"].Value, m.Groups["s"].Value,
            [.. SourceBlock.Matches(m.Groups["f"].Value).Select(s => (s.Groups["a"].Value, s.Groups["q"].Value))]))];

    /// <summary>Il messaggio porta INTERI e insieme l'originale del Gap (tipo=Gap) e quello del riscontro, ciascuno con almeno una fonte e il testo integrale della citazione.</summary>
    private static bool HasBothOriginals(string text)
    {
        var voci = Voci(text);
        return voci.Any(v => v.Kind == "Gap" && v.Statement == GapStatement && v.Sources.Any(s => s.Quote == GapStatement))
            && voci.Any(v => v.Kind == "Requirement" && v.Statement == FindStatement && v.Sources.Any(s => s.Quote == FindStatement));
    }

    private sealed record Observed(int Index, string Stage, string View, bool Both, string BatchId, string InputHash);

    private async Task<List<Observed>> ObserveAsync(Guid run, DocumentChatWorkflowTests.FakeChatModel model)
    {
        var list = new List<Observed>();
        var requests = model.Requests.ToList();
        for (var k = 0; k < requests.Count; k++)
        {
            var p = DocumentReconcileAliasWorkflowTests.ParseAlias(requests[k].AllText);
            var batchId = await Scalar<string>("SELECT \"BatchId\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r ORDER BY \"Ordinal\" LIMIT 1 OFFSET " + k, ("r", run));
            var inputHash = await Scalar<string>("SELECT \"InputHash\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r ORDER BY \"Ordinal\" LIMIT 1 OFFSET " + k, ("r", run));
            list.Add(new(k, p.Stage, p.View, HasBothOriginals(requests[k].AllText), batchId, inputHash));
        }

        Trace(run, list);
        return list;
    }

    /// <summary>Traccia opzionale (percorso, batch, input hash, tipi di chiamata) su file, solo se BPROJECTS_GATE_TRACE_DIR è impostata: serve a compilare il report del gate.</summary>
    private static void Trace(Guid run, IEnumerable<Observed> observed)
    {
        if (Environment.GetEnvironmentVariable("BPROJECTS_GATE_TRACE_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var name = TestContext.Current.Test?.TestDisplayName ?? "test";
        File.AppendAllLines(
            Path.Combine(dir, "gate-trace.txt"),
            [$"## {name} run={run:D}", .. observed.Select(o => $"{o.Index}\t{o.Stage}\t{o.View}\tboth={o.Both}\t{o.BatchId}\t{o.InputHash}")]);
    }

    // ================================================================ scenario

    private async Task<(Guid Run, AnalysisRunClaim Claim, Doc GapDoc, Doc FindDoc)> ArrangeAsync(bool sharedKey, Guid findId, bool secondFinding = false)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        Doc? gapDoc = null, findDoc = null;
        for (var n = 1; n <= Documents; n++)
        {
            // Soggetti distinti: Gap (aaa) e riscontro (zzz) cadono in foglie diverse della vista A; con sharedKey condividono la chiave di scope «regola».
            var subject = n == 1 ? "aaa" : n == Documents ? "zzz" : $"mmm{n}";
            var shares = sharedKey && (n is 1 or Documents || (n == 7 && secondFinding));
            Guid? evidenceId = n switch { 1 => GapId, Documents => findId, 7 when secondFinding => Find2Id, 4 => Guid.Parse("00000000-0000-4000-8000-000000000002"), 5 => Guid.Parse("00000000-0000-4000-8000-000000000003"), _ => null };
            var doc = await NewDocument(client, project, $"doc{n}", StatementOf(n, secondFinding), subject, shares ? "regola" : null, evidenceId, n == 1 ? EvidenceKind.Gap : EvidenceKind.Requirement);
            gapDoc = n == 1 ? doc : gapDoc;
            findDoc = n == Documents ? doc : findDoc;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        await Execute(HistoricalAnalysisV4.Sql, ("v", HistoricalAnalysisV4.Versions), ("r", run));   // T141: il gate caratterizza il grafo gerarchico v4 esistente; il POST ora congela la v5 (fail-fast con Gap, T143)
        return (run, await ClaimAsync(run), gapDoc!, findDoc!);
    }

    private async Task<AnalysisAliasCatalog> RunCatalogAsync(AnalysisRunClaim claim) => AnalysisAliasCatalog.Build(await PrepareAsync(claim));

    // ================================================================ prove

    [Fact]
    public async Task With_a_server_candidate_the_effective_expand_and_compare_payloads_carry_both_full_originals_with_resolvable_provenance_persisted_and_hash_consistent()
    {
        var (run, claim, gapDoc, findDoc) = await ArrangeAsync(sharedKey: true, NearFindId);
        var model = LinkModel();

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var observed = await ObserveAsync(run, model);

        // Percorso e tipi di chiamata: solo batch del grafo esistente, nessuna fase nuova.
        Assert.All(observed, o => Assert.Contains(o.Stage, new[] { "leaf", "reduce", "cross", "expand", "compare" }));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));

        // Gap e riscontro sono in foglie DIVERSE: nessuna foglia (A o B) li riceve entrambi.
        var leaves = observed.Where(o => o.Stage == "leaf").ToList();
        Assert.True(leaves.Count(o => o.View == "A") >= 2, "servono almeno due foglie A perché Gap e riscontro siano in porzioni diverse");
        Assert.DoesNotContain(leaves, o => o.Both);
        // Riduzioni e Cross ricevono solo descriptor di nodo: mai gli originali.
        Assert.All(observed.Where(o => o.Stage is "reduce" or "cross"), o =>
        {
            Assert.False(o.Both);
            Assert.Empty(Voci(model.Requests[o.Index].AllText));
        });

        // I batch che vedono entrambi gli originali integrali sono espansione e confronto (già pianificati dal grafo).
        var evaluators = observed.Where(o => o.Both).ToList();
        Assert.Contains(evaluators, o => o.Stage == "expand");
        var compare = Assert.Single(evaluators, o => o.Stage == "compare");

        // Provenienze risolvibili: alias E/S del payload → catalogo del run → evidenza/fonte/versione documento persistiti.
        var catalog = await RunCatalogAsync(claim);
        foreach (var o in evaluators)
        {
            var voci = Voci(model.Requests[o.Index].AllText);
            var gap = voci.Single(v => v.Kind == "Gap");
            var find = voci.Single(v => v.Statement == FindStatement);
            Assert.Equal(GapId, catalog.Evidence(gap.Alias)!.EvidenceId);
            Assert.Equal(NearFindId, catalog.Evidence(find.Alias)!.EvidenceId);
            Assert.Equal(EvidenceKind.Gap, catalog.Evidence(gap.Alias)!.Kind);
            Assert.Equal(gapDoc.Version, catalog.Evidence(gap.Alias)!.DocumentVersionId);
            Assert.Equal(findDoc.Version, catalog.Evidence(find.Alias)!.DocumentVersionId);
            foreach (var (voce, version) in new[] { (gap, gapDoc.Version), (find, findDoc.Version) })
            {
                Assert.NotEmpty(voce.Sources);
                foreach (var (alias, quote) in voce.Sources)
                {
                    var source = catalog.Source(alias) ?? throw new Xunit.Sdk.XunitException($"alias di fonte {alias} non risolvibile");
                    Assert.Equal(version, source.DocumentVersionId);
                    Assert.Equal(quote, source.Quote);
                }
            }
        }

        // Il payload misurato dal preflight (istruzioni, messaggio, schema) è quello inviato: l'inputHash del batch di confronto è il suo hash.
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window, MaxOutputTokens = 1500,
            SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var meterClient = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(meterClient, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var preflight = new AgentFactory(options, meterClient.ChatClient, (text, ct) => new(meterClient.CountTokensAsync(text, ct)), null, monitor, meterClient).Preflight;
        var schema = ReconcileAliasContracts.JsonSchema().GetRawText();
        foreach (var o in evaluators)
        {
            var sent = model.Requests[o.Index];
            var measured = await preflight.MeasureAsync(
                new DocumentPayload(AiPhase.Analysis, sent.System, [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, sent.AllText)], schema), Ct);
            Assert.Equal(o.InputHash, measured.PayloadHash);
            Assert.True(measured.Fits);
        }

        // WorkOutput persistito del batch valutatore: Committed, hash del batch, catalogo con ENTRAMBE le evidenze, tre hash verificabili.
        foreach (var o in evaluators)
        {
            Assert.Equal(o.InputHash, await Scalar<string>("SELECT \"InputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"BatchId\" = @b AND \"Status\" = 'Committed'", ("r", run), ("b", o.BatchId)));
            var payload = JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"BatchId\" = @b", ("r", run), ("b", o.BatchId))).RootElement;
            Assert.Equal("reconcile-alias-1", payload.GetProperty("schemaVersion").GetString());
            Assert.Equal(o.Stage, payload.GetProperty("stage").GetString());
            var ids = payload.GetProperty("catalog").GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("evidenceId").GetGuid()).ToHashSet();
            Assert.Contains(GapId, ids);
            Assert.Contains(NearFindId, ids);
            foreach (var name in new[] { "catalogHash", "aiOutputHash", "canonicalHash" })
            {
                Assert.Matches("^[0-9a-f]{64}$", payload.GetProperty(name).GetString()!);
            }
        }

        // Il confronto ha esattamente i due originali del candidato, il candidato nominato e nessun altro.
        Assert.Equal(2, Voci(model.Requests[compare.Index].AllText).Count);

        // Replay del run completato: nessuna nuova inferenza.
        var again = LinkModel();
        var replay = await RunAsync(claim, again);
        Assert.Equal(result.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task A_crash_at_the_evaluating_compare_batch_resumes_with_the_identical_payload_and_never_resends_committed_batches()
    {
        var (run, claim, _, _) = await ArrangeAsync(sharedKey: true, NearFindId);
        var failing = LinkModel(failOn: _ => false);
        var crashing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r =>
            {
                var p = DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText);
                if (p.Stage == "compare")
                {
                    throw new InvalidOperationException("processo terminato nel batch valutatore");
                }

                return failing.Responder!(r);
            },
        };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, crashing));

        var committedBefore = await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run));
        var lost = crashing.Requests.Last();
        Assert.Equal("compare", DocumentReconcileAliasWorkflowTests.ParseAlias(lost.AllText).Stage);
        Assert.True(HasBothOriginals(lost.AllText), "il batch caduto era quello che riceveva entrambi gli originali");
        Assert.Equal(crashing.Requests.Count - 1, committedBefore);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

        var healthy = LinkModel();
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        // Il primo invio dopo la ripresa è lo STESSO payload del batch caduto (stesso input hash), e nulla di già Committed è reinviato.
        Assert.Equal(lost.AllText, healthy.Requests[0].AllText);
        Assert.Empty(healthy.Requests.Select(r => r.AllText).Intersect(crashing.Requests.Take(crashing.Requests.Count - 1).Select(r => r.AllText)));
        Assert.Equal(committedBefore + healthy.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var stages = healthy.Requests.Select(r => DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText).Stage).ToList();
        Assert.Equal("compare", stages[0]);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"InputHash\" = (SELECT \"InputHash\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'compare')", ("r", run)));

        var again = LinkModel();
        var replay = await RunAsync(claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
    }

    // ---------------------------------------------------------------- LIMITI del grafo esistente (caratterizzazione)

    [Fact]
    public async Task Characterization_LIMIT_without_a_shared_literal_key_no_batch_of_the_existing_graph_receives_Gap_and_finding_together()
    {
        var (run, claim, _, _) = await ArrangeAsync(sharedKey: false, NearFindId);
        // Il modello è lo stesso del gate (propone il collegamento ovunque veda entrambi): se nessun batch li vede insieme, nessuna proposta e nessuna valutazione è possibile.
        var model = LinkModel();

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var observed = await ObserveAsync(run, model);
        Assert.True(observed.Count(o => o.Stage == "leaf" && o.View == "A") >= 2);
        Assert.Contains(observed, o => o.Stage is "reduce");
        Assert.DoesNotContain(observed, o => o.Stage is "expand" or "compare");   // nessun candidato del server: nessun batch con originali oltre le foglie
        Assert.DoesNotContain(observed, o => o.Both);                              // LIMITE: né foglie, né riduzioni, né Cross ricevono Gap+riscontro insieme
        Assert.All(observed.Where(o => o.Stage is "reduce" or "cross"), o => Assert.Empty(Voci(model.Requests[o.Index].AllText)));
        // Il run è comunque Completed: la copertura attuale non richiede alcun confronto, quindi il grafo non segnala la valutazione mancante.
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task Characterization_LIMIT_the_expand_manifest_is_partitioned_by_EvidenceId_so_a_Gap_and_a_finding_at_opposite_ends_never_share_an_expand_batch()
    {
        var (run, claim, _, _) = await ArrangeAsync(sharedKey: true, FarFindId);
        var model = LinkModel();

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var observed = await ObserveAsync(run, model);
        var expands = observed.Where(o => o.Stage == "expand").ToList();
        Assert.True(expands.Count >= 2, "il candidato del server esiste e il suo manifesto è diviso in più batch di espansione");
        // LIMITE: ciascun batch di espansione vede una sola porzione del manifesto ordinato per EvidenceId; Gap (id minimo) e riscontro (id massimo) sono in batch diversi.
        Assert.DoesNotContain(expands, o => o.Both);
        Assert.Contains(expands, o => model.Requests[o.Index].AllText.Contains(GapMark, StringComparison.Ordinal));
        Assert.Contains(expands, o => model.Requests[o.Index].AllText.Contains(FindMark, StringComparison.Ordinal));
        // Nessun figlio Evidence-Evidence emerge, quindi nessun confronto: nessun batch riceve i due originali.
        Assert.DoesNotContain(observed, o => o.Stage == "compare");
        Assert.DoesNotContain(observed, o => o.Both);
    }

    private static HashSet<string> Marks(string text) =>
        [.. Voci(text).Select(v => v.Statement.Contains(GapMark, StringComparison.Ordinal) ? "GAP" : v.Statement.Contains(Find2Mark, StringComparison.Ordinal) ? "RIS2" : v.Statement.Contains(FindMark, StringComparison.Ordinal) ? "RIS" : null).OfType<string>()];

    [Fact]
    public async Task Characterization_LIMIT_a_Gap_with_two_findings_never_has_all_of_them_in_one_batch_and_the_finding_outside_its_expand_batch_is_never_compared()
    {
        var (run, claim, _, _) = await ArrangeAsync(sharedKey: true, NearFindId, secondFinding: true);
        var model = LinkModel();

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var observed = await ObserveAsync(run, model);
        var marks = model.Requests.Select(r => Marks(r.AllText)).ToList();
        var seen = observed.Select(o => (o.Stage, Marks: string.Join('+', marks[o.Index].Order()))).ToList();
        // LIMITE (budget del rig: un batch di espansione contiene due voci del manifesto ordinato per EvidenceId): nessun batch riceve il Gap con ENTRAMBI i riscontri.
        Assert.DoesNotContain(marks, m => m.Count == 3);
        Assert.Contains(seen, s => s is { Stage: "expand", Marks: "GAP+RIS" });
        Assert.Contains(seen, s => s is { Stage: "expand" } && s.Marks.Contains("RIS2", StringComparison.Ordinal) && !s.Marks.Contains("GAP", StringComparison.Ordinal));
        // Il riscontro 2 non è mai visto insieme al Gap: nessun confronto lo riguarda, il Gap sarebbe valutato su UN solo riscontro (o nessuno per l'altro).
        Assert.DoesNotContain(marks, m => m.Contains("GAP") && m.Contains("RIS2"));
        Assert.Equal(1, observed.Count(o => o.Stage == "compare"));
    }
}
