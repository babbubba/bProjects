using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T098 (PostgreSQL reale, nessun DGX né modello) — lifecycle concorrente del bundle corrente: prima preparazione corrente, candidata Ready, CAS delle
/// promozioni (un solo vincitore), candidata fallita non promovibile, nuova versione file che diventa «ultima pronta», pin ed esclusione su una terza
/// versione e scambio del bundle durante chat, Analizza e generazione: i comandi che fissano uno snapshot e la promozione condividono il lock di progetto,
/// quindi lo snapshot vede un bundle intero (preparazione + versione Evidence dello stesso bundle) e nulla di già accettato cambia dopo. Le letture non
/// scelgono mai il bundle corrente al posto del PM.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentBundleConcurrencyTests : LifecycleTestBase
{
    private async Task<HttpResponseMessage> AnalyzeAsync(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> ChatAsync(HttpClient client, Guid project) =>
        client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text = "Vorrei un portale per i clienti" }, Ct);

    private async Task<(Guid? Preparation, Guid? Evidence)> SnapshotBundle(Guid snapshot, Guid document) =>
        (await Scalar<string>("SELECT COALESCE((SELECT \"PreparationId\"::text FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"DocumentId\" = @d), '')", ("s", snapshot), ("d", document))) is { Length: > 0 } p
            ? (Guid.Parse(p), Guid.Parse(await Scalar<string>("SELECT \"EvidenceVersionId\"::text FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"DocumentId\" = @d", ("s", snapshot), ("d", document))))
            : (null, null);

    private Task<Guid> SnapshotOf(string table, Guid id) => Scalar<Guid>($"SELECT \"SourceSnapshotId\" FROM \"{table}\" WHERE \"Id\" = @i", ("i", id));

    private Task<string> SnapshotRows(Guid snapshot) => Scalar<string>(
        "SELECT COALESCE(string_agg(row_to_json(i)::text, '|' ORDER BY i.\"DocumentId\"), '') || '#' || (SELECT row_to_json(s)::text FROM \"SourceSnapshots\" s WHERE s.\"Id\" = @s) FROM \"SourceSnapshotItems\" i WHERE i.\"SnapshotId\" = @s",
        ("s", snapshot));

    // ================================================================ lifecycle completo

    [Fact]
    public async Task The_whole_lifecycle_keeps_one_applicable_bundle_per_version_with_a_single_cas_winner_and_survives_a_third_file_version()
    {
        using var client = await App.LoginAsync();
        var (t, first) = await CurrentWithCandidatesAsync(client);   // g1 Ready corrente, revisione 1
        var g1 = first[0];
        var g2 = await SeedGenerationAsync(t, 2, PreparationStatus.Ready);
        var g3 = await SeedGenerationAsync(t, 3, PreparationStatus.Ready);
        var g4 = await SeedGenerationAsync(t, 4, PreparationStatus.Failed);
        Assert.Equal(g1.Preparation, (await SelectionItemAsync(client, t)).GetProperty("preparationId").GetGuid());   // le candidate non cambiano la selezione

        // CAS: due promozioni diverse con la stessa revisione attesa, in parallelo. Un solo vincitore, l'altra riceve 409.
        var results = await Task.WhenAll(Promote(client, t, "2", Expect(1)), Promote(client, t, "3", Expect(1)));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, results.Select(r => r.StatusCode).Order().ToArray());
        var winner = results[0].StatusCode == HttpStatusCode.OK ? g2 : g3;
        Assert.Equal((winner.Preparation, winner.EvidenceVersion, 2), await State(t));
        Assert.Equal(2L, await Promotions(t));
        Assert.Equal(winner.Preparation, (await SelectionItemAsync(client, t)).GetProperty("preparationId").GetGuid());

        // La candidata fallita non è promovibile e non sposta nulla; il replay del vincitore è 200 senza un secondo evento.
        Assert.Equal((HttpStatusCode)422, (await Promote(client, t, "4", Expect(2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, winner.Number.ToString(), Expect(1))).StatusCode);
        Assert.Equal((winner.Preparation, winner.EvidenceVersion, 2), await State(t));
        Assert.Equal(2L, await Promotions(t));
        _ = g4;

        // Una v2 file pronta diventa l'ultima pronta senza promozione PM; la v1 conserva il bundle vincitore.
        var v2 = await UploadNextVersionAsync(client, t, "Seconda versione");
        var v2Ready = await SeedGenerationAsync(v2, 1, PreparationStatus.Ready);
        await MakeCurrentAsync(v2, v2Ready);
        var latest = await SelectionItemAsync(client, t);
        Assert.Equal(v2.Version, latest.GetProperty("resolvedVersionId").GetGuid());
        Assert.Equal(v2Ready.Preparation, latest.GetProperty("preparationId").GetGuid());

        // Una v3 incompleta non toglie la v2 pronta; il pin sulla v1 usa il suo bundle corrente; l'esclusione sopravvive alle versioni successive.
        var v3 = await UploadNextVersionAsync(client, t, "Terza versione");
        Assert.Equal(v2.Version, (await SelectionItemAsync(client, t)).GetProperty("resolvedVersionId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await PutSelectionAsync(client, t, new { documentId = t.Document, included = true, mode = "Pinned", versionId = t.Version })).StatusCode);
        var pinned = await SelectionItemAsync(client, t);
        Assert.Equal(t.Version, pinned.GetProperty("resolvedVersionId").GetGuid());
        Assert.Equal(winner.Preparation, pinned.GetProperty("preparationId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await PutSelectionAsync(client, t, new { documentId = t.Document, included = false, mode = "Pinned", versionId = t.Version })).StatusCode);
        await UploadNextVersionAsync(client, t, "Quarta versione");
        var excluded = await SelectionItemAsync(client, t);
        Assert.False(excluded.GetProperty("included").GetBoolean());
        Assert.Equal("Excluded", excluded.GetProperty("availability").GetString());
        _ = v3;

        // Invarianti finali: uno stato per versione con puntatori entrambi nulli o entrambi valorizzati; l'ultima pronta punta a una versione con bundle corrente.
        Assert.Equal(0L, await Count("SELECT count(*) FROM \"EvidenceDocumentStates\" WHERE (\"CurrentPreparationId\" IS NULL) <> (\"CurrentEvidenceVersionId\" IS NULL)"));
        Assert.Equal(2L, await Count("SELECT count(*) FROM \"EvidenceDocumentStates\" WHERE \"ProjectId\" = @p", ("p", t.Project)));
        Assert.Equal(v2.Version, await Scalar<Guid>("SELECT \"LatestReadyVersionId\" FROM \"ProjectDocuments\" WHERE \"Id\" = @d", ("d", t.Document)));
    }

    [Fact]
    public async Task Reads_never_choose_or_change_the_current_bundle_on_their_own()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var before = (await StateRowJson(t), await Promotions(t), await Preparations(t), await Jobs(t));

        foreach (var url in new[]
        {
            $"/api/projects/{t.Project}/documents", $"/api/projects/{t.Project}/documents/{t.Document}", $"/api/projects/{t.Project}/documents/{t.Document}/versions/1",
            $"/api/projects/{t.Project}/source-selection", $"/api/projects/{t.Project}/documents/{t.Document}/versions/1/evidence/versions?viewVersion=1.1.0",
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url, Ct)).StatusCode);
        }

        Assert.Equal(before, (await StateRowJson(t), await Promotions(t), await Preparations(t), await Jobs(t)));
        Assert.Equal(1, (await State(t)).Revision);
    }

    // ================================================================ scambio del bundle durante i comandi

    [Fact]
    public async Task A_promotion_racing_chat_and_analyze_yields_whole_snapshots_and_never_rewrites_one_already_taken()
    {
        using var client = await App.LoginAsync();
        for (var round = 0; round < 4; round++)
        {
            var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
            var promote = Promote(client, t, "2", Expect(1));
            var analyze = AnalyzeAsync(client, t.Project);
            var chat = ChatAsync(client, t.Project);
            await Task.WhenAll(promote, analyze, chat);
            Assert.Equal(HttpStatusCode.OK, promote.Result.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, analyze.Result.StatusCode);
            Assert.Equal(HttpStatusCode.Created, chat.Result.StatusCode);

            var run = (await Json(analyze.Result)).GetProperty("id").GetGuid();
            var turn = (await Json(chat.Result)).GetProperty("chatTurnId").GetGuid();
            var oldBundle = (g[0].Preparation, g[0].EvidenceVersion);
            var newBundle = (g[1].Preparation, g[1].EvidenceVersion);
            foreach (var snapshot in new[] { await SnapshotOf("ProcessingRuns", run), await SnapshotOf("ChatTurns", turn) })
            {
                var bundle = await SnapshotBundle(snapshot, t.Document);
                Assert.True(bundle == oldBundle || bundle == newBundle, "il bundle dello snapshot deve essere intero: preparazione ed evidenza dello stesso bundle");
            }

            // Riportare il corrente al bundle precedente non riscrive gli snapshot già presi, che restano identici; l'avviso riflette la differenza.
            var rows = (await SnapshotRows(await SnapshotOf("ProcessingRuns", run)), await SnapshotRows(await SnapshotOf("ChatTurns", turn)));
            Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "1", Expect(2))).StatusCode);
            Assert.Equal(rows, (await SnapshotRows(await SnapshotOf("ProcessingRuns", run)), await SnapshotRows(await SnapshotOf("ChatTurns", turn))));
            var runView = await Json(await client.GetAsync($"/api/projects/{t.Project}/processing-runs/{run}", Ct));
            var atOld = await SnapshotBundle(await SnapshotOf("ProcessingRuns", run), t.Document) == oldBundle;
            Assert.Equal(!atOld, runView.GetProperty("hasSourceChanges").GetBoolean());   // il corrente è tornato al vecchio: cambiato solo se lo snapshot ha il nuovo
        }
    }

    [Fact]
    public async Task Promotion_chat_and_analyze_share_the_project_lock_so_none_of_them_runs_while_another_writer_holds_it()
    {
        using var client = await App.LoginAsync();
        var (t, _) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        Task<HttpResponseMessage> promote, analyze, chat;
        await using (var holder = await OpenConnection())
        await using (var tx = await holder.BeginTransactionAsync(Ct))
        {
            await using (var command = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", holder, tx))
            {
                command.Parameters.AddWithValue("k", ProjectLockKey(t.Project));
                await command.ExecuteNonQueryAsync(Ct);
            }

            promote = Promote(client, t, "2", Expect(1));
            analyze = AnalyzeAsync(client, t.Project);
            chat = ChatAsync(client, t.Project);
            await Task.Delay(1_500, Ct);
            Assert.False(promote.IsCompleted || analyze.IsCompleted || chat.IsCompleted, "i tre comandi devono attendere lo stesso lock di progetto");
            Assert.Equal(1, (await State(t)).Revision);   // nulla è stato scritto nel frattempo
            await tx.RollbackAsync(Ct);
        }

        var all = await Task.WhenAll(promote, analyze, chat);
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Accepted, HttpStatusCode.Created }.Order().ToArray(), all.Select(r => r.StatusCode).Order().ToArray());
    }

    [Fact]
    public async Task A_promotion_after_a_confirmed_revision_and_its_generation_changes_neither_the_snapshot_the_confirmation_nor_the_draft()
    {
        using var client = await App.LoginAsync();
        var (t, g) = await CurrentWithCandidatesAsync(client, PreparationStatus.Ready);
        var run = (await Json(await AnalyzeAsync(client, t.Project))).GetProperty("id").GetGuid();
        Guid revisionId;
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var runRow = await db.ProcessingRuns.SingleAsync(r => r.Id == run, Ct);
            var revision = new AnalysisRevision
            {
                Id = Guid.NewGuid(), AnalysisId = run, Version = 1, Origin = AnalysisRevisionOrigin.Ai, CreatedBy = runRow.RequestedBy, CreatedAt = DateTimeOffset.UtcNow,
                SchemaVersion = "1.1.0", ContentJson = "{}", ValidationState = AnalysisValidationState.PmConfirmed, ConfirmedBy = runRow.RequestedBy, ConfirmedAt = DateTimeOffset.UtcNow,
            };
            db.AnalysisRevisions.Add(revision);
            runRow.Status = ProcessingRunStatus.Completed;
            runRow.ResultRevisionId = revision.Id;
            runRow.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
            await db.Projects.Where(p => p.Id == t.Project).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAnalysisId, run), Ct);
            revisionId = revision.Id;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{t.Project}/analyses/{run}/document-generations") { Content = JsonContent.Create(new { revisionId }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var generate = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, generate.StatusCode);
        var documentRun = (await Json(generate)).GetProperty("id").GetGuid();
        var analysisSnapshot = await SnapshotOf("ProcessingRuns", run);
        Assert.Equal(analysisSnapshot, await SnapshotOf("ProcessingRuns", documentRun));   // la generazione eredita lo snapshot dell'analisi
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion), await SnapshotBundle(analysisSnapshot, t.Document));
        var before = (await SnapshotRows(analysisSnapshot), await RowJson("AnalysisRevisions", revisionId), await RowJson("ProcessingRuns", documentRun));
        var analysisBefore = await Json(await client.GetAsync($"/api/projects/{t.Project}/analyses/{run}", Ct));

        Assert.Equal(HttpStatusCode.OK, (await Promote(client, t, "2", Expect(1))).StatusCode);

        Assert.Equal(before, (await SnapshotRows(analysisSnapshot), await RowJson("AnalysisRevisions", revisionId), await RowJson("ProcessingRuns", documentRun)));
        Assert.Equal((g[0].Preparation, g[0].EvidenceVersion), await SnapshotBundle(await SnapshotOf("ProcessingRuns", documentRun), t.Document));
        var analysisAfter = await Json(await client.GetAsync($"/api/projects/{t.Project}/analyses/{run}", Ct));
        Assert.True(analysisAfter.GetProperty("hasSourceChanges").GetBoolean());
        Assert.Equal(analysisBefore.GetProperty("validationState").GetString(), analysisAfter.GetProperty("validationState").GetString());
        Assert.Equal(analysisBefore.GetProperty("canGenerate").GetBoolean(), analysisAfter.GetProperty("canGenerate").GetBoolean());
    }
}
