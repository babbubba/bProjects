using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T065 — rianalisi, promozione e backfill del registro delle evidenze su PostgreSQL reale (parti non coperte da <see cref="EvidenceApiTests"/>):
/// impatto della promozione sulle relazioni (rimappate se l'abbinamento è univoco, stale altrimenti, mai riscritte), artefatti storici
/// (selezioni congelate, mappe, revisioni, bozze, versioni vecchie) invariati da rianalisi e promozione, concorrenza ottimistica e
/// idempotenza della promozione, backfill interrotto e ripreso senza versioni né lavori duplicati, analisi e bozze confermate dal PM
/// non modificate a posteriori. Estrazioni con claim, lease, fencing e commit reali del registro e uscite deterministiche; nessun modello.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class EvidenceReanalysisTests : IAsyncLifetime
{
    private const string Message1 = "Il portale invia un promemoria via email. Le scadenze restano settimanali.";
    private const string Message2 = "Intendevo dire che il promemoria vale solo per le scadenze.";
    private const string QuoteA = "Il portale invia un promemoria via email.";
    private const string QuoteB = "Le scadenze restano settimanali.";

    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private ApiHarness App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    // ---------------------------------------------------------------- (1) impatto sulle relazioni

    [Fact]
    public async Task Promoting_a_candidate_reports_which_relations_are_remapped_or_stale_and_never_rewrites_a_relation()
    {
        using var client = await App.LoginAsync();
        var project = await SeededAnalysisFactory.CreateProjectAsync(client, Message1, Message2);
        var candidate = false;
        ExtractionOutput Output(ExtractionContext c) => c.Target.Sequence switch
        {
            1 when !candidate => Out(Ev(c, "e1", QuoteA), Ev(c, "e2", QuoteB)),
            // Candidato: A ha lo stesso passo (stesso tipo cambiato), B è ridotto a una parte troppo piccola per un abbinamento.
            1 => Out(Ev(c, "e1", QuoteA, EvidenceKind.Constraint, "Promemoria via email"), Ev(c, "e2", "Le scadenze")),
            _ => Out(Ev(c, "e1", Message2, relations:
            [
                new ExtractedRelation { Type = EvidenceRelationType.Corrects, Target = "P1", Basis = EvidenceRelationBasis.Declared, Rationale = "il promemoria" },
                new ExtractedRelation { Type = EvidenceRelationType.DependsOn, Target = "P2", Basis = EvidenceRelationBasis.Inferred, Rationale = null },
            ])),
        };

        Assert.Equal(2, (await ExtractionDriver.RunAsync(App.Services, Output)).Count);
        var m1 = await MessageId(project, 1);
        var m2 = await MessageId(project, 2);
        var v1 = await Scalar<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", m1));
        var oldA = await Scalar<Guid>("""SELECT "Id" FROM "Evidences" WHERE "VersionId" = @v AND "Ordinal" = 1""", ("v", v1));
        var oldB = await Scalar<Guid>("""SELECT "Id" FROM "Evidences" WHERE "VersionId" = @v AND "Ordinal" = 2""", ("v", v1));
        var fromEvidence = await Scalar<Guid>("""SELECT "Id" FROM "Evidences" WHERE "MessageId" = @m""", ("m", m2));
        var relationOf = async (EvidenceRelationType type) => await Scalar<Guid>("""SELECT "Id" FROM "EvidenceRelations" WHERE "ToEvidenceId" = ANY(@t) AND "Type" = @type""",
            ("t", new[] { oldA, oldB }), ("type", type.ToString()));
        var (corrects, dependsOn) = (await relationOf(EvidenceRelationType.Corrects), await relationOf(EvidenceRelationType.DependsOn));
        var relationsBefore = await Digest("EvidenceRelations");
        var m2VersionsBefore = await Digest("EvidenceExtractionVersions", "\"MessageId\" = @m", ("m", m2));

        // Rianalisi del messaggio 1: la sola nuova versione, candidata (la corrente resta quella di prima).
        candidate = true;
        Assert.Equal(HttpStatusCode.Accepted, (await Command(client, $"/api/projects/{project}/messages/{m1}/evidence/reanalyses")).StatusCode);
        Assert.Single(await ExtractionDriver.RunAsync(App.Services, Output));
        var versions = await Versions(client, project, m1);
        var candidateVersion = versions.GetProperty("versions").EnumerateArray().Single(v => !v.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();
        Assert.Equal(v1, versions.GetProperty("currentVersionId").GetGuid());
        var newA = await Scalar<Guid>("""SELECT "Id" FROM "Evidences" WHERE "VersionId" = @v AND "Ordinal" = 1""", ("v", candidateVersion));

        var promoted = await Promote(client, project, m1, candidateVersion, versions.GetProperty("stateRevision").GetInt32(), "ripasso");
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        var body = await Json(promoted);
        Assert.Equal(2, body.GetProperty("stateRevision").GetInt32());
        Assert.Equal(candidateVersion, body.GetProperty("currentVersionId").GetGuid());
        var promotion = body.GetProperty("promotion");
        Assert.Equal(v1, promotion.GetProperty("fromVersionId").GetGuid());
        Assert.Equal("pm.uno", promotion.GetProperty("promotedBy").GetString());

        // Impatto: A abbinata in modo univoco (Corrects → rimappata), B senza abbinamento (DependsOn → stale).
        var impact = promotion.GetProperty("relationImpact");
        Assert.Equal((1, 1), (impact.GetProperty("remapped").GetInt32(), impact.GetProperty("stale").GetInt32()));
        var rows = impact.GetProperty("relations").EnumerateArray().ToDictionary(r => r.GetProperty("type").GetString()!);
        Assert.Equal(2, rows.Count);
        Assert.Equal(corrects, rows["Corrects"].GetProperty("relationId").GetGuid());
        Assert.Equal((fromEvidence, oldA, newA), (rows["Corrects"].GetProperty("fromEvidenceId").GetGuid(), rows["Corrects"].GetProperty("toEvidenceId").GetGuid(), rows["Corrects"].GetProperty("remappedTo").GetGuid()));
        Assert.Equal(dependsOn, rows["DependsOn"].GetProperty("relationId").GetGuid());
        Assert.Equal(oldB, rows["DependsOn"].GetProperty("toEvidenceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, rows["DependsOn"].GetProperty("remappedTo").ValueKind);

        // Lo stesso impatto è persistito con la promozione (append-only) e il suo esito è leggibile dalla cronologia.
        var stored = JsonNode.Parse(await Scalar<string>("""SELECT "RelationImpact"::text FROM "EvidencePromotions" WHERE "MessageId" = @m AND "ToVersionId" = @v""", ("m", m1), ("v", candidateVersion)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(impact.GetRawText()), stored));
        Assert.Equal(2, (await Versions(client, project, m1)).GetProperty("promotions").GetArrayLength());

        // Nessuna relazione riscritta: stesse righe, stessi bersagli (le evidenze della versione uscente); il messaggio 2 non cambia.
        Assert.Equal(relationsBefore, await Digest("EvidenceRelations"));
        Assert.Equal(m2VersionsBefore, await Digest("EvidenceExtractionVersions", "\"MessageId\" = @m", ("m", m2)));
        Assert.Equal(oldA, await Scalar<Guid>("""SELECT "ToEvidenceId" FROM "EvidenceRelations" WHERE "Id" = @r""", ("r", corrects)));
        Assert.Equal(oldB, await Scalar<Guid>("""SELECT "ToEvidenceId" FROM "EvidenceRelations" WHERE "Id" = @r""", ("r", dependsOn)));

        // Una selezione costruita dopo la promozione risolve le stesse relazioni nello stesso modo: rimappata e stale dichiarata.
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var current = await db.EvidenceMessageStates.AsNoTracking().Where(s => s.ProjectId == project).Select(s => s.CurrentVersionId).ToListAsync(Ct);
        var items = (await db.EvidenceExtractionVersions.AsNoTracking().Where(v => current.Contains(v.Id)).OrderBy(v => v.MessageSequence).ToListAsync(Ct))
            .Select(v => new SelectionItem(v.MessageId!.Value, v.MessageSequence!.Value, v.Id, v.Version, v.EvidenceCount, v.NoEvidenceReason)).ToList();
        var view = await new EvidenceLedgerStore(db).BuildViewAsync(items, Ct);
        var remapped = Assert.Single(view.Relations);
        Assert.True(remapped.Remapped);
        Assert.Equal(EvidenceRelationType.Corrects, remapped.Type);
        Assert.Equal(newA, view.Evidences.Single(e => e.Alias == remapped.ToAlias).EvidenceId);
        var stale = Assert.Single(view.StaleRelations);
        Assert.Equal((dependsOn, EvidenceRelationType.DependsOn), (stale.RelationId, stale.Type));
        Assert.Contains("senza abbinamento univoco", stale.Reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- (2) artefatti storici

    [Fact]
    public async Task Reanalysis_and_promotion_leave_every_historical_artifact_intact_and_a_new_run_freezes_the_new_version()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var edited = await SeededAnalysisFactory.AddPmRevisionAsync(App.Services, w, confirmed: false, SeededAnalysisFactory.DropItem2AndRewriteItem1);
        var draft = await SeededAnalysisFactory.AddDraftAsync(App.Services, w, edited, "# Documento\n\nIl portale invia un promemoria entro 48 ore.");
        var coverageUrl = $"/api/projects/{w.Project}/analyses/{w.Analysis}/coverage-reports";
        var report = await Json(await client.PostAsJsonAsync(coverageUrl, new { draftId = draft }, Ct));
        var selectionUrl = $"/api/projects/{w.Project}/analyses/{w.Analysis}/evidence-selection";
        var selectionBefore = await client.GetStringAsync(selectionUrl, Ct);
        var oldVersions = new[] { w.Version1, w.Version2 };
        var oldEvidences = new[] { w.Evidence1, w.Evidence2 };
        var frozen = await Snapshot(oldVersions);
        var oldSelectionRow = await Digest("AnalysisEvidenceSelections", "\"RunId\" = @r", ("r", w.Analysis));

        // Rianalisi dell'intero progetto con un'estrazione diversa e promozione esplicita di entrambe le nuove versioni.
        var batch = await Json(await Command(client, $"/api/projects/{w.Project}/evidence/reanalyses"));
        Assert.Equal(2, batch.GetProperty("jobCount").GetInt32());
        Assert.Equal(2, (await ExtractionDriver.RunAsync(App.Services, Different)).Count);
        var promoted = new List<Guid>();
        foreach (var message in new[] { w.Message1, w.Message2 })
        {
            promoted.Add((await PromoteCandidate(client, w.Project, message)).GetProperty("currentVersionId").GetGuid());
        }

        Assert.DoesNotContain(promoted, oldVersions.Contains);
        Assert.Equal(promoted.Order(), (await Current(w.Project)).Order());

        // Selezione congelata, mappa, revisioni, run, bozza: byte per byte come prima.
        Assert.Equal(selectionBefore, await client.GetStringAsync(selectionUrl, Ct));
        Assert.Equal(frozen, await Snapshot(oldVersions));
        var view = EvidenceSelectionView.Deserialize(await Scalar<string>("""SELECT "View"::text FROM "AnalysisEvidenceSelections" WHERE "RunId" = @r""", ("r", w.Analysis)));
        Assert.Equal(oldEvidences.Order(), view.Evidences.Select(e => e.EvidenceId).Order());
        Assert.Equal(oldVersions.Order(), view.Items.Select(i => i.VersionId).Order());

        // Gli ID delle evidenze continuano a risolversi: mappa → evidenze → versioni vecchie, ancora con le stesse evidenze.
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "AnalysisEvidenceLinks" l WHERE NOT EXISTS (SELECT 1 FROM "Evidences" e WHERE e."Id" = l."EvidenceId")"""));
        Assert.Equal(2L, await Scalar<long>("""SELECT count(*) FROM "Evidences" WHERE "VersionId" = ANY(@v)""", ("v", oldVersions)));
        Assert.Equal(4L, await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionVersions" WHERE "ProjectId" = @p""", ("p", w.Project)));
        Assert.Equal("Il portale invia un promemoria via email entro 24 ore.", await Scalar<string>("""SELECT "Statement" FROM "Evidences" WHERE "Id" = @e""", ("e", w.Evidence1)));

        // Anche il report di copertura del run storico è lo stesso (stessi input ⇒ stesso report, 200) con lo stesso contenuto.
        var again = await client.PostAsJsonAsync(coverageUrl, new { draftId = draft }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(report.GetRawText(), (await Json(again)).GetRawText());

        // Un nuovo run congela le versioni correnti, non quelle storiche; la selezione del run storico non cambia.
        var claim = await SeededAnalysisFactory.InsertRunningAnalysisAsync(App.Services, w);
        using (var scope = App.Services.CreateScope())
        {
            var result = await new EvidenceLedgerStore(scope.ServiceProvider.GetRequiredService<AppDbContext>()).FreezeSelectionAsync(claim, Ct);
            Assert.Equal(SelectionOutcome.Frozen, result.Outcome);
            Assert.Equal(promoted.Order(), result.View!.Items.Select(i => i.VersionId).Order());
            Assert.Equal(["Riformulata: " + Message1Default, "Riformulata: " + Message2Default], result.View.Evidences.Select(e => e.Statement));
        }

        Assert.Equal(promoted.Order(), (await ScalarList<Guid>("""SELECT "VersionId" FROM "AnalysisEvidenceSelectionItems" WHERE "RunId" = @r""", ("r", claim.RunId))).Order());
        Assert.NotEqual(
            await Scalar<string>("""SELECT "ViewHash" FROM "AnalysisEvidenceSelections" WHERE "RunId" = @r""", ("r", claim.RunId)),
            await Scalar<string>("""SELECT "ViewHash" FROM "AnalysisEvidenceSelections" WHERE "RunId" = @r""", ("r", w.Analysis)));
        Assert.Equal(oldSelectionRow, await Digest("AnalysisEvidenceSelections", "\"RunId\" = @r", ("r", w.Analysis)));
        Assert.Equal(selectionBefore, await client.GetStringAsync(selectionUrl, Ct));
    }

    // ---------------------------------------------------------------- (3) concorrenza ottimistica e idempotenza

    [Fact]
    public async Task A_promotion_is_idempotent_on_the_current_version_and_a_stale_expected_revision_is_a_409_that_changes_nothing()
    {
        using var client = await App.LoginAsync();
        var project = await SeededAnalysisFactory.CreateProjectAsync(client, "Il magazzino lavora su due turni.");
        await ExtractionDriver.RunAsync(App.Services, EvidenceTestSeed.Output);
        var message = await MessageId(project, 1);
        var v1 = await Scalar<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message));
        Assert.Equal(HttpStatusCode.Accepted, (await Command(client, $"/api/projects/{project}/messages/{message}/evidence/reanalyses")).StatusCode);
        await ExtractionDriver.RunAsync(App.Services, Different);
        var v2 = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "MessageId" = @m AND "Version" = 2""", ("m", message));

        var first = await Promote(client, project, message, v2, 1);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var promoted = await Json(first);
        Assert.Equal((2, v2), (promoted.GetProperty("stateRevision").GetInt32(), promoted.GetProperty("currentVersionId").GetGuid()));
        Assert.Equal(2L, await Count("EvidencePromotions", message));

        // Stessa versione, revisione attesa aggiornata: già corrente, 200, nessuna nuova promozione né nuova revisione.
        var settled = await Digests("EvidenceMessageStates", "EvidencePromotions", "EvidenceExtractionVersions", "Evidences");
        var replay = await Promote(client, project, message, v2, 2);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await Json(replay);
        Assert.Equal((2, v2, JsonValueKind.Null), (replayBody.GetProperty("stateRevision").GetInt32(), replayBody.GetProperty("currentVersionId").GetGuid(), replayBody.GetProperty("promotion").ValueKind));
        Assert.Equal(settled, await Digests("EvidenceMessageStates", "EvidencePromotions", "EvidenceExtractionVersions", "Evidences"));

        // La richiesta identica con la revisione attesa di prima non è più valida: conflitto esplicito con lo stato corrente, nessuna modifica.
        foreach (var (target, expected) in new[] { (v2, 1), (v1, 1), (v1, 0), (v1, 99) })
        {
            var stale = await Promote(client, project, message, target, expected);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var conflict = await Json(stale);
            Assert.Equal("promotion_conflict", conflict.GetProperty("code").GetString());
            Assert.Equal((v2, 2), (conflict.GetProperty("currentVersionId").GetGuid(), conflict.GetProperty("stateRevision").GetInt32()));
            Assert.Equal(settled, await Digests("EvidenceMessageStates", "EvidencePromotions", "EvidenceExtractionVersions", "Evidences"));
        }

        // Con la revisione corretta la stessa promozione è ammessa (anche all'indietro): le versioni non si perdono mai.
        var back = await Promote(client, project, message, v1, 2);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal((3, v1), ((await Json(back)).GetProperty("stateRevision").GetInt32(), v1));
        Assert.Equal(3L, await Count("EvidencePromotions", message));
        Assert.Equal(2L, await Count("EvidenceExtractionVersions", message));
        Assert.Equal(1L, await Scalar<long>("""SELECT count(*) FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));
    }

    [Fact]
    public async Task Concurrent_promotions_with_the_same_expected_revision_produce_exactly_one_winner()
    {
        using var client = await App.LoginAsync();
        var project = await SeededAnalysisFactory.CreateProjectAsync(client, "Il magazzino lavora su due turni.");
        await ExtractionDriver.RunAsync(App.Services, EvidenceTestSeed.Output);
        var message = await MessageId(project, 1);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Command(client, $"/api/projects/{project}/messages/{message}/evidence/reanalyses")).StatusCode);
            await ExtractionDriver.RunAsync(App.Services, Different);
        }

        var v2 = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "MessageId" = @m AND "Version" = 2""", ("m", message));
        var v3 = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "MessageId" = @m AND "Version" = 3""", ("m", message));

        var attempts = new[] { v2, v3, v2, v3 };
        var responses = await Task.WhenAll(attempts.Select(v => Promote(client, project, message, v, 1)));
        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Conflict));
        var winner = await Json(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
        var winnerVersion = winner.GetProperty("currentVersionId").GetGuid();
        Assert.Contains(winnerVersion, new[] { v2, v3 });
        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var body = await Json(loser);
            Assert.Equal("promotion_conflict", body.GetProperty("code").GetString());
            Assert.Equal((winnerVersion, 2), (body.GetProperty("currentVersionId").GetGuid(), body.GetProperty("stateRevision").GetInt32()));
        }

        Assert.Equal(winnerVersion, await Scalar<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));
        Assert.Equal(2, await Scalar<int>("""SELECT "Revision" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));
        Assert.Equal(2L, await Count("EvidencePromotions", message));         // la prima automatica e una sola esplicita
        Assert.Equal(3L, await Count("EvidenceExtractionVersions", message));  // nessuna versione perduta o duplicata
    }

    // ---------------------------------------------------------------- (4) backfill interrotto e ripreso

    [Fact]
    public async Task An_interrupted_backfill_resumes_without_duplicate_versions_or_jobs_and_reports_the_real_progress()
    {
        using var client = await App.LoginAsync();
        var project = await SeededAnalysisFactory.CreateProjectAsync(client, "Requisito uno del portale.", "Requisito due del portale.", "Requisito tre del portale.",
            "Requisito quattro del portale.", "Requisito cinque del portale.");
        // Cinque messaggi senza versione: i lavori iniziali sono falliti (come dopo un'installazione precedente al registro).
        await Execute("""UPDATE "EvidenceExtractionJobs" SET "Status" = 'Failed', "ErrorCode" = 'test', "CompletedAt" = now()""");

        var key = Guid.NewGuid();
        var started = await Command(client, $"/api/projects/{project}/evidence/backfills", key);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var batch = await Json(started);
        var batchId = batch.GetProperty("id").GetGuid();
        Assert.Equal((5, 0), (batch.GetProperty("jobCount").GetInt32(), batch.GetProperty("skippedCount").GetInt32()));

        // Due messaggi completati, il terzo preso in carico da un worker che poi muore (lease scaduto, nessun commit).
        var done = await ExtractionDriver.RunAsync(App.Services, EvidenceTestSeed.Output, max: 2);
        Assert.Equal(2, done.Count);
        var crashed = (await ExtractionDriver.ClaimAsync(App.Services, "crashed"))!;
        Assert.Equal(3L, await Scalar<long>("""SELECT "MessageSequence" FROM "EvidenceExtractionJobs" WHERE "Id" = @j""", ("j", crashed.JobId)));
        Assert.Equal((2, 1, 2, 0), Progress(await Batch(client, project, batchId)));    // queued, running, completed, failed
        await Execute("""UPDATE "EvidenceExtractionJobs" SET "LeaseUntil" = clock_timestamp() - interval '1 second' WHERE "Id" = @j""", ("j", crashed.JobId));

        // Ripetere il backfill (nuova chiave o stessa chiave) non accoda nulla: ogni messaggio ha una versione o un lavoro aperto.
        var second = await Command(client, $"/api/projects/{project}/evidence/backfills");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await Json(second);
        Assert.Equal((0, 5), (secondBody.GetProperty("jobCount").GetInt32(), secondBody.GetProperty("skippedCount").GetInt32()));
        var replay = await Json(await Command(client, $"/api/projects/{project}/evidence/backfills", key));
        Assert.Equal(batchId, replay.GetProperty("id").GetGuid());
        Assert.Equal(5, replay.GetProperty("jobCount").GetInt32());

        // Ripresa: il lavoro abbandonato è ripreso con un nuovo fencing; il vecchio worker non può più né rinnovare né committare.
        var resumed = (await ExtractionDriver.ClaimAsync(App.Services, "recovered"))!;
        Assert.Equal(crashed.JobId, resumed.JobId);
        Assert.Equal(2, resumed.AttemptCount);
        Assert.True(resumed.FencingToken > crashed.FencingToken);
        using (var scope = App.Services.CreateScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>();
            var ledger = new EvidenceLedgerStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            var context = await ledger.LoadContextAsync(crashed.JobId, Ct);
            var validation = EvidenceExtractionValidator.Validate(EvidenceTestSeed.Output(context), context);
            Assert.False(await leases.RenewAsync(crashed, Ct));
            Assert.Equal(ExtractionCommitOutcome.Rejected, (await ledger.CommitAsync(crashed, context, validation, "stale", "{}", Ct)).Outcome);
            Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionVersions" WHERE "MessageSequence" = 3"""));

            // Il worker ripreso rinuncia (lavoro fallito): il messaggio resta senza versione e un retry autorizzato lo riprende nello stesso batch.
            Assert.True(await ledger.FailAsync(resumed, "abandoned", "worker interrotto", Ct));
        }

        Assert.Equal((2, 0, 2, 1), Progress(await Batch(client, project, batchId)));
        var retry = await Command(client, $"/api/projects/{project}/evidence/jobs/{crashed.JobId}/retry");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(batchId, (await Json(retry)).GetProperty("batchId").GetGuid());
        Assert.Equal((3, 0, 2, 0), Progress(await Batch(client, project, batchId)));    // il retry sostituisce il lavoro fallito nel conteggio

        // Completamento: i tre messaggi rimasti, nessuno ripetuto.
        Assert.Equal(3, (await ExtractionDriver.RunAsync(App.Services, EvidenceTestSeed.Output)).Count);
        Assert.Equal((0, 0, 5, 0), Progress(await Batch(client, project, batchId)));
        Assert.Empty(await ExtractionDriver.RunAsync(App.Services, EvidenceTestSeed.Output));

        var again = await Json(await Command(client, $"/api/projects/{project}/evidence/backfills"));
        Assert.Equal((0, 5), (again.GetProperty("jobCount").GetInt32(), again.GetProperty("skippedCount").GetInt32()));
        Assert.Equal(5L, await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionVersions" WHERE "ProjectId" = @p""", ("p", project)));
        Assert.Equal(5L, await Scalar<long>("""SELECT count(DISTINCT "MessageId") FROM "EvidenceExtractionVersions" WHERE "ProjectId" = @p""", ("p", project)));
        Assert.Equal(1L, await Scalar<long>("""SELECT max(c) FROM (SELECT count(*) c FROM "EvidenceExtractionVersions" GROUP BY "MessageId") x"""));
        Assert.Equal(5L, await Scalar<long>("""SELECT count(*) FROM "EvidenceMessageStates" WHERE "ProjectId" = @p AND "Revision" = 1""", ("p", project)));
        Assert.Equal(5L, await Scalar<long>("""SELECT count(*) FROM "EvidencePromotions" WHERE "ProjectId" = @p AND "PromotedBy" IS NULL""", ("p", project)));
        Assert.Equal(1L, await Scalar<long>("""SELECT max(c) FROM (SELECT count(*) c FROM "EvidenceExtractionJobs" WHERE "Status" = 'Completed' GROUP BY "MessageId") x"""));
        Assert.Equal(5L, await Scalar<long>("""SELECT count(DISTINCT "MessageId") FROM "EvidenceExtractionJobs" WHERE "Status" = 'Completed'"""));
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionJobs" WHERE "Status" IN ('Queued', 'Running')"""));
        Assert.Equal(1L, await Scalar<long>("""SELECT max(c) FROM (SELECT count(*) c FROM "EvidenceExtractionJobs" WHERE "Reason" = 'Backfill' AND "BatchId" = @b GROUP BY "MessageId") x""", ("b", batchId)));
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/evidence/status", Ct);
        Assert.All(status.GetProperty("messages").EnumerateArray(), m => Assert.Equal(1, m.GetProperty("stateRevision").GetInt32()));
    }

    // ---------------------------------------------------------------- (5) analisi e bozze confermate

    [Fact]
    public async Task A_confirmed_analysis_and_its_draft_are_not_modified_by_reanalysis_or_promotion()
    {
        using var client = await App.LoginAsync();
        var w = await SeededAnalysisFactory.CreateAsync(client, App.Services);
        var confirmed = await SeededAnalysisFactory.AddPmRevisionAsync(App.Services, w, confirmed: true, SeededAnalysisFactory.DropItem2AndRewriteItem1);
        var draft = await SeededAnalysisFactory.AddDraftAsync(App.Services, w, confirmed, "# Documento\n\nIl portale invia un promemoria entro 48 ore. [fonte:" + w.Message1 + "]");
        await Execute("""UPDATE "Projects" SET "CurrentAnalysisId" = @a, "LatestDraftId" = @d WHERE "Id" = @p""", ("a", w.Analysis), ("d", draft), ("p", w.Project));
        var coverageUrl = $"/api/projects/{w.Project}/analyses/{w.Analysis}/coverage-reports";
        var report = await Json(await client.PostAsJsonAsync(coverageUrl, new { draftId = draft }, Ct));
        var confirmedAt = await Scalar<DateTime>("""SELECT "ConfirmedAt" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", confirmed));
        string[] tables =
            ["Projects", "ProcessingRuns", "AnalysisRevisions", "Drafts", "AnalysisEvidenceSelections", "AnalysisEvidenceSelectionItems", "AnalysisEvidenceLinks", "EvidenceCoverageReports", "ConversationMessages"];
        var before = await Digests(tables);

        // Rianalisi dell'intero progetto, estrazioni diverse e promozione di tutte le nuove versioni.
        Assert.Equal(HttpStatusCode.Accepted, (await Command(client, $"/api/projects/{w.Project}/evidence/reanalyses")).StatusCode);
        Assert.Equal(2, (await ExtractionDriver.RunAsync(App.Services, Different)).Count);
        foreach (var message in new[] { w.Message1, w.Message2 })
        {
            await PromoteCandidate(client, w.Project, message);
        }

        Assert.Equal(4L, await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionVersions" WHERE "ProjectId" = @p""", ("p", w.Project)));
        Assert.DoesNotContain(w.Version1, await Current(w.Project));
        Assert.Equal(before, await Digests(tables));
        Assert.Equal("PmConfirmed", await Scalar<string>("""SELECT "ValidationState" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", confirmed)));
        Assert.Equal(confirmedAt, await Scalar<DateTime>("""SELECT "ConfirmedAt" FROM "AnalysisRevisions" WHERE "Id" = @r""", ("r", confirmed)));
        Assert.Equal((w.Analysis, draft), (await Scalar<Guid>("""SELECT "CurrentAnalysisId" FROM "Projects" WHERE "Id" = @p""", ("p", w.Project)), await Scalar<Guid>("""SELECT "LatestDraftId" FROM "Projects" WHERE "Id" = @p""", ("p", w.Project))));

        // Il report di copertura della revisione confermata e della sua bozza non cambia: nuovi input richiederebbero una nuova analisi.
        var again = await client.PostAsJsonAsync(coverageUrl, new { draftId = draft }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(report.GetRawText(), (await Json(again)).GetRawText());
    }

    // ---------------------------------------------------------------- supporto

    private const string Message1Default = SeededAnalysisFactory.Text1;
    private const string Message2Default = SeededAnalysisFactory.Text2;

    /// <summary>Estrazione alternativa: stessa citazione, tipo e enunciato diversi (la nuova versione differisce dalla precedente).</summary>
    private static ExtractionOutput Different(ExtractionContext context)
    {
        var output = EvidenceTestSeed.Output(context);
        return output with { Evidences = [output.Evidences[0] with { Kind = EvidenceKind.Constraint, Statement = "Riformulata: " + output.Evidences[0].Statement }] };
    }

    private static ExtractionOutput Out(params ExtractedEvidence[] evidences) => new() { NoEvidenceReason = null, Evidences = evidences };

    private static ExtractedEvidence Ev(
        ExtractionContext context, string key, string quote, EvidenceKind kind = EvidenceKind.Requirement, string? statement = null,
        IReadOnlyList<ExtractedRelation>? relations = null) => new()
    {
        Key = key,
        Sources = [new ExtractedSource { MessageId = context.Target.Id, Quote = quote, Occurrence = 1, Role = EvidenceSourceRole.Primary }],
        Kind = kind,
        KindRationale = null,
        Statement = statement ?? quote,
        Subject = null,
        Action = null,
        Object = null,
        Scope = null,
        Purpose = null,
        Instrument = null,
        Channel = null,
        Recipient = null,
        Qualifiers = [],
        Polarity = EvidencePolarity.Affirmed,
        Certainty = EvidenceCertainty.Declared,
        Relations = relations ?? [],
    };

    private static (int Queued, int Running, int Completed, int Failed) Progress(JsonElement batch)
    {
        var p = batch.GetProperty("progress");
        return (p.GetProperty("queued").GetInt32(), p.GetProperty("running").GetInt32(), p.GetProperty("completed").GetInt32(), p.GetProperty("failed").GetInt32());
    }

    private static Task<JsonElement> Batch(HttpClient client, Guid project, Guid batch) =>
        client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/evidence/batches/{batch}", Ct);

    private static Task<JsonElement> Versions(HttpClient client, Guid project, Guid message) =>
        client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/messages/{message}/evidence/versions", Ct);

    private static Task<HttpResponseMessage> Promote(HttpClient client, Guid project, Guid message, Guid version, int expectedRevision, string? reason = null) =>
        client.PostAsJsonAsync($"/api/projects/{project}/messages/{message}/evidence/promotions", new { versionId = version, expectedStateRevision = expectedRevision, reason }, Ct);

    /// <summary>Promuove la versione candidata più recente del messaggio con la revisione di stato appena letta.</summary>
    private static async Task<JsonElement> PromoteCandidate(HttpClient client, Guid project, Guid message)
    {
        var versions = await Versions(client, project, message);
        var candidate = versions.GetProperty("versions").EnumerateArray().Where(v => !v.GetProperty("isCurrent").GetBoolean())
            .OrderByDescending(v => v.GetProperty("version").GetInt32()).First().GetProperty("id").GetGuid();
        var response = await Promote(client, project, message, candidate, versions.GetProperty("stateRevision").GetInt32(), "ripasso");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static Task<HttpResponseMessage> Command(HttpClient client, string url, Guid? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private Task<Guid> MessageId(Guid project, long sequence) => Scalar<Guid>("""
        SELECT m."Id" FROM "ConversationMessages" m JOIN "Conversations" c ON c."Id" = m."ConversationId"
        WHERE c."ProjectId" = @p AND m."Sequence" = @s AND m."Role" = 'User'
        """, ("p", project), ("s", sequence));

    private async Task<List<Guid>> Current(Guid project) =>
        await ScalarList<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "ProjectId" = @p""", ("p", project));

    private Task<long> Count(string table, Guid message) =>
        Scalar<long>($"""SELECT count(*) FROM "{table}" WHERE "MessageId" = @m""", ("m", message));

    private Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters) => SqlHelper.ScalarAsync<T>(_database.ConnectionString, sql, parameters);

    private Task Execute(string sql, params (string Name, object Value)[] parameters) => SqlHelper.ExecuteAsync(_database.ConnectionString, sql, parameters);

    private Task<string> Digest(string table, string? where = null, params (string Name, object Value)[] parameters) =>
        SqlHelper.DigestAsync(_database.ConnectionString, table, where, parameters);

    private async Task<string> Digests(params string[] tables) => string.Join('\n', await Task.WhenAll(tables.Select(t => Digest(t))));

    private async Task<List<T>> ScalarList<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new Npgsql.NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var list = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            list.Add(reader.GetFieldValue<T>(0));
        }

        return list;
    }

    /// <summary>Impronta delle righe storiche che la rianalisi e la promozione non devono toccare: selezione, mappa, revisioni, run, bozze, versioni e evidenze vecchie.</summary>
    private async Task<string> Snapshot(Guid[] oldVersions) => string.Join('\n', await Task.WhenAll(
        Digest("AnalysisEvidenceSelections"), Digest("AnalysisEvidenceSelectionItems"), Digest("AnalysisEvidenceLinks"), Digest("AnalysisRevisions"),
        Digest("ProcessingRuns"), Digest("Drafts"), Digest("EvidenceRelations"),
        Digest("EvidenceExtractionVersions", "\"Id\" = ANY(@v)", ("v", oldVersions)),
        Digest("Evidences", "\"VersionId\" = ANY(@v)", ("v", oldVersions)),
        Digest("EvidenceExtractionJobs", "\"ResultVersionId\" = ANY(@v)", ("v", oldVersions))));
}

/// <summary>Esecutore di estrazioni dei test con uscita scelta dal test: stessi claim, lease, fencing, validazione e commit del dispatcher reale.</summary>
internal static class ExtractionDriver
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ExtractionJobClaim?> ClaimAsync(IServiceProvider services, string owner)
    {
        using var scope = services.CreateScope();
        var work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextWorkAsync(owner, turns: false, runs: false, documents: false, extractions: true, Ct);
        return work?.Extraction;
    }

    /// <summary>Esegue i lavori di estrazione aperti nell'ordine del claim (al più <paramref name="max"/>); ritorna lavoro e versione di ciascun commit.</summary>
    public static async Task<List<(Guid JobId, Guid VersionId)>> RunAsync(IServiceProvider services, Func<ExtractionContext, ExtractionOutput> output, int max = int.MaxValue)
    {
        var done = new List<(Guid, Guid)>();
        while (done.Count < max)
        {
            using var scope = services.CreateScope();
            var work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
                .ClaimNextWorkAsync($"driver:{Guid.NewGuid():N}", turns: false, runs: false, documents: false, extractions: true, Ct);
            if (work?.Extraction is not { } claim)
            {
                break;
            }

            var ledger = new EvidenceLedgerStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            var context = await ledger.LoadContextAsync(claim.JobId, Ct);
            var validation = EvidenceExtractionValidator.Validate(output(context), context);
            Assert.True(validation.Accepted, "Uscita di prova non valida: " + string.Join(" | ", validation.Violations));
            var result = await ledger.CommitAsync(claim, context, validation, "test", """{"test":true}""", Ct);
            Assert.Equal(ExtractionCommitOutcome.Committed, result.Outcome);
            done.Add((claim.JobId, result.VersionId!.Value));
        }

        return done;
    }
}
