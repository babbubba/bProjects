using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T048 - concorrenza e idempotenza end-to-end di anteprima, conferma e generazione su PostgreSQL reale. L'applicazione e' completa
/// (endpoint, dispatcher con esecutore di chat, workflow di Analysis e di Document, store e commit reali) e l'unico punto di
/// iniezione e' il server del modello, un <see cref="HttpMessageHandler"/> dietro il vero <see cref="InternalModelClient"/>.
/// Il dispatcher e' pilotato a mano; le chiamate HTTP concorrenti partono insieme da un cancello comune. Gli esiti di una gara
/// possono essere piu' di uno (chi prende il lock per primo): le prove verificano gli invarianti che valgono per ogni ordine, e le
/// gare che hanno un solo esito corretto sono rese deterministiche con un <see cref="DbCommandInterceptor"/> dell'harness.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RevisionConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private readonly FakeDgx _dgx = new();
    private readonly QueryHook _hook = new();
    private ApiHarness? _app;
    private AiDispatchService? _dispatcher;
    private int _texts;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString, prepare: true, Configure);
            Assert.DoesNotContain(_app.Services.GetServices<IHostedService>(), s => s is AiDispatchService); // il dispatcher e' pilotato dalla prova
            _dispatcher = ActivatorUtilities.CreateInstance<AiDispatchService>(_app.Services);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _dgx.Dispose();
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

    /// <summary>Come Program.cs: stesso <see cref="InternalModelClient"/> e stesso cablaggio di agenti e workflow, sul solo server finto.</summary>
    private void Configure(IServiceCollection services)
    {
        foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AiDispatchService)).ToList())
        {
            services.Remove(hosted);
        }

        services.RemoveAll<InternalModelClient>();
        services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, _dgx));
        services.AddSingleton(sp =>
        {
            var model = sp.GetRequiredService<InternalModelClient>();
            return new ConversationalAgent(AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model), model.CreateChatOptions(temperature: 0.2f));
        });
        services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
        services.AddSingleton(sp => new AnalysisWorkflow(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ConversationalAgent>(),
            sp.GetRequiredService<InternalModelClient>().CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0)));
        services.AddSingleton(sp =>
        {
            var model = sp.GetRequiredService<InternalModelClient>();
            return new DocumentAgent(AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model), model.CreateChatOptions(temperature: 0.2f));
        });
        services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>()));
        services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(_hook));
    }

    // ================================================================ PUT / confirm / generate simultanei

    [Fact]
    public async Task Simultaneous_put_confirm_and_generate_on_one_revision_serialize_into_a_single_history_and_at_most_one_formal_phase()
    {
        using var pm = await App.LoginAsync();
        for (var round = 0; round < 8; round++)
        {
            var s = await SeedAnalysis(pm);
            var before = await Revisions(s.Analysis);
            var edit = await Content(pm, s);
            edit["goals"]![0]!["text"] = $"Obiettivo concorrente {round}";
            var body = edit.ToJsonString();

            var calls = new List<Func<Task<(string Name, HttpResponseMessage Response)>>>();
            for (var i = 0; i < 3; i++)
            {
                calls.Add(async () => ("put", await PutRaw(pm, s, s.Revision, body)));
                calls.Add(async () => ("confirm", await Confirm(pm, s, s.Revision)));
                calls.Add(async () => ("generate", await Generate(pm, s, Guid.NewGuid(), s.Revision)));
            }

            var results = await Together(calls.ToArray());
            var puts = results.Where(r => r.Name == "put").Select(r => r.Response).ToList();
            var confirms = results.Where(r => r.Name == "confirm").Select(r => r.Response).ToList();
            var generates = results.Where(r => r.Name == "generate").Select(r => r.Response).ToList();

            foreach (var response in puts.Concat(confirms).Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                Assert.Equal("revision_conflict", (await AssertProblem(response, HttpStatusCode.Conflict)).GetProperty("code").GetString());
            }

            foreach (var response in generates.Where(r => r.StatusCode != HttpStatusCode.Accepted))
            {
                Assert.Contains((await AssertProblem(response, HttpStatusCode.Conflict)).GetProperty("code").GetString(),
                    new[] { "analysis_not_confirmed", "revision_conflict", "processing_active" });
            }

            var applied = puts.Count(r => r.StatusCode == HttpStatusCode.OK);
            var accepted = generates.Count(r => r.StatusCode == HttpStatusCode.Accepted);
            Assert.True(applied <= 1, $"round {round}: {applied} PUT applicati sulla stessa base");
            Assert.True(accepted <= 1, $"round {round}: {accepted} fasi formali accettate");

            var after = await Revisions(s.Analysis);
            Assert.Equal(1 + applied, after.Count);
            AssertAppendOnly(before, after);
            Assert.Equal(accepted, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Kind" = 'Document'""", ("p", s.Project)));
            Assert.Equal(accepted, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Status" IN ('Queued','Running') AND "Id" <> @a""", ("p", s.Project), ("a", s.Analysis)));

            var confirmedViews = new List<JsonElement>();
            foreach (var response in confirms.Where(r => r.StatusCode == HttpStatusCode.OK))
            {
                confirmedViews.Add(await Json(response));
            }

            if (confirmedViews.Count > 0)
            {
                // Una sola conferma e' stata scritta: tutte le risposte 200 mostrano la stessa revisione e lo stesso istante.
                Assert.Equal("PmConfirmed", after[0].State);
                Assert.All(confirmedViews, v => Assert.Equal(s.Revision, v.GetProperty("currentRevisionId").GetGuid()));
                Assert.Single(confirmedViews.Select(v => v.GetProperty("confirmedAt").GetDateTimeOffset()).Distinct());
                Assert.True(Math.Abs((confirmedViews[0].GetProperty("confirmedAt").GetDateTimeOffset() - after[0].ConfirmedAt!.Value).TotalMilliseconds) < 1);
            }
            else
            {
                // Nessuna conferma e' riuscita: il PUT ha preceduto tutte; la v1 e' rimasta Unvalidated e nulla ha potuto generare.
                Assert.Equal(("Unvalidated", 1, 0), (after[0].State, applied, accepted));
            }

            if (accepted == 1)
            {
                var run = await Scalar<Guid>("""SELECT "InputRevisionId" FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Kind" = 'Document'""", ("p", s.Project));
                Assert.Equal(s.Revision, run);
                Assert.Equal("PmConfirmed", after[0].State); // mai un input non confermato, anche se un edit e' seguito
            }

            await Drain();
            Assert.Equal(accepted, await Count("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p AND "AnalysisRevisionId" = @r""", ("p", s.Project), ("r", s.Revision)));
            Assert.Equal(accepted, await Count("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p""", ("p", s.Project)));
            var final = await Revisions(s.Analysis);
            Assert.Equal(after, final); // il dispatcher non tocca le revisioni

            var view = await Json(await pm.GetAsync(Url(s), Ct));
            Assert.Equal(final[^1].Id, view.GetProperty("currentRevisionId").GetGuid());
            Assert.Equal(final[^1].State == "PmConfirmed", view.GetProperty("canGenerate").GetBoolean());
            Assert.True(view.GetProperty("canEdit").GetBoolean());
        }
    }

    [Fact]
    public async Task Simultaneous_confirms_stamp_the_confirmation_once_and_simultaneous_edits_on_the_confirmed_base_yield_one_new_revision()
    {
        using var pm = await App.LoginAsync();
        var s = await SeedAnalysis(pm);
        var original = await Revisions(s.Analysis);

        var confirms = await Together(Enumerable.Range(0, 8).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => Confirm(pm, s, s.Revision)).ToArray());

        Assert.All(confirms, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var views = await Task.WhenAll(confirms.Select(Json));
        Assert.Single(views.Select(v => v.GetProperty("confirmedAt").GetDateTimeOffset()).Distinct());
        Assert.All(confirms, r => Assert.Equal($"\"{s.Revision}\"", r.Headers.ETag!.Tag));
        var confirmed = await Revisions(s.Analysis);
        Assert.Single(confirmed);
        Assert.Equal("PmConfirmed", confirmed[0].State);
        AssertAppendOnly(original, confirmed);

        // Una conferma ripetuta dopo le altre non riscrive nulla.
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, s.Revision)).StatusCode);
        Assert.Equal(confirmed, await Revisions(s.Analysis));

        // Edit dopo conferma ammesso, ma con la stessa base ne vince uno solo; gli altri vedono la revisione vincente.
        var edit = await Content(pm, s);
        edit["goals"]![0]!["text"] = "Modifica dopo la conferma";
        var body = edit.ToJsonString();
        var puts = await Together(Enumerable.Range(0, 8).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => PutRaw(pm, s, s.Revision, body)).ToArray());

        var winner = Assert.Single(puts, r => r.StatusCode == HttpStatusCode.OK);
        var newRevision = (await Json(winner)).GetProperty("currentRevisionId").GetGuid();
        foreach (var loser in puts.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            var problem = await AssertProblem(loser, HttpStatusCode.Conflict);
            Assert.Equal("revision_conflict", problem.GetProperty("code").GetString());
            Assert.Equal(newRevision, problem.GetProperty("currentRevisionId").GetGuid());
        }

        var after = await Revisions(s.Analysis);
        Assert.Equal(2, after.Count);
        AssertAppendOnly(confirmed, after);
        Assert.Equal("Unvalidated", after[1].State);
        Assert.Null(after[1].ConfirmedBy);
    }

    [Fact]
    public async Task Only_one_formal_phase_is_accepted_among_simultaneous_analyses_and_generations_and_the_losers_point_to_it()
    {
        using var pm = await App.LoginAsync();
        for (var round = 0; round < 4; round++)
        {
            var s = await SeedAnalysis(pm);
            Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, s.Revision)).StatusCode);
            var before = await Revisions(s.Analysis);

            var calls = new List<Func<Task<(string Name, HttpResponseMessage Response)>>>();
            for (var i = 0; i < 3; i++)
            {
                calls.Add(async () => ("analysis", await Analyze(pm, s.Project, Guid.NewGuid())));
                calls.Add(async () => ("document", await Generate(pm, s, Guid.NewGuid(), s.Revision)));
            }

            var results = await Together(calls.ToArray());

            var winner = Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.Accepted);
            var winnerRun = (await Json(winner.Response)).GetProperty("id").GetGuid();
            foreach (var loser in results.Where(r => r.Response.StatusCode != HttpStatusCode.Accepted))
            {
                var problem = await AssertProblem(loser.Response, HttpStatusCode.Conflict);
                Assert.Equal("processing_active", problem.GetProperty("code").GetString());
                Assert.Equal(winnerRun, problem.GetProperty("processingRunId").GetGuid());
            }

            Assert.Equal(1, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Status" IN ('Queued','Running')""", ("p", s.Project)));
            Assert.Equal(2, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p""", ("p", s.Project))); // l'analisi di partenza e la vincitrice

            await Drain();

            AssertAppendOnly(before, await Revisions(s.Analysis));
            if (winner.Name == "document")
            {
                Assert.Equal(1, await Count("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p AND "AnalysisRevisionId" = @r""", ("p", s.Project), ("r", s.Revision)));
                Assert.Equal(s.Analysis, await Scalar<Guid>("""SELECT "CurrentAnalysisId" FROM "Projects" WHERE "Id" = @p""", ("p", s.Project)));
            }
            else
            {
                Assert.Equal(0, await Count("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p""", ("p", s.Project)));
                Assert.Equal(winnerRun, await Scalar<Guid>("""SELECT "CurrentAnalysisId" FROM "Projects" WHERE "Id" = @p""", ("p", s.Project)));
                var fresh = await Revisions(winnerRun);
                Assert.Equal(("Ai", "Unvalidated", 1), (fresh[0].Origin, fresh[0].State, fresh.Count)); // la nuova anteprima richiede la propria conferma
            }
        }
    }

    [Fact]
    public async Task An_edit_or_confirmation_racing_a_new_analysis_is_applied_strictly_before_it_or_refused_never_during_it()
    {
        using var pm = await App.LoginAsync();
        for (var round = 0; round < 6; round++)
        {
            var s = await SeedAnalysis(pm);
            var before = await Revisions(s.Analysis);
            var edit = await Content(pm, s);
            edit["goals"]![0]!["text"] = $"Modifica in gara {round}";
            var body = edit.ToJsonString();
            var key = Guid.NewGuid();

            var results = await Together<(string Name, HttpResponseMessage Response)>(
                async () => ("analysis", await Analyze(pm, s.Project, key)),
                async () => ("put", await PutRaw(pm, s, s.Revision, body)),
                async () => ("confirm", await Confirm(pm, s, s.Revision)));

            var analysis = results.Single(r => r.Name == "analysis").Response;
            Assert.Equal(HttpStatusCode.Accepted, analysis.StatusCode); // Analizza non dipende dalla revisione: parte sempre
            var run = (await Json(analysis)).GetProperty("id").GetGuid();
            foreach (var (name, response) in results.Where(r => r.Name != "analysis" && r.Response.StatusCode != HttpStatusCode.OK))
            {
                var problem = await AssertProblem(response, HttpStatusCode.Conflict);
                var code = problem.GetProperty("code").GetString();
                Assert.Contains(code, name == "put" ? new[] { "processing_active" } : new[] { "processing_active", "revision_conflict" });
                if (code == "processing_active")
                {
                    Assert.Equal(run, problem.GetProperty("processingRunId").GetGuid());
                }
            }

            // Ogni scrittura sull'anteprima e' avvenuta sotto lock prima che la nuova Analysis fosse registrata.
            var requestedAt = await Scalar<DateTime>("""SELECT "RequestedAt" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", run));
            var after = await Revisions(s.Analysis);
            AssertAppendOnly(before, after);
            Assert.All(after.Skip(1), r => Assert.True(r.CreatedAt.UtcDateTime < requestedAt, "revisione creata durante la nuova Analysis"));
            Assert.All(after.Where(r => r.ConfirmedAt is not null), r => Assert.True(r.ConfirmedAt!.Value.UtcDateTime < requestedAt, "conferma durante la nuova Analysis"));

            await Drain();

            Assert.Equal(after, await Revisions(s.Analysis)); // la nuova analisi non riscrive la vecchia anteprima
            Assert.Equal(run, await Scalar<Guid>("""SELECT "CurrentAnalysisId" FROM "Projects" WHERE "Id" = @p""", ("p", s.Project)));
            Assert.Single(await Revisions(run));
        }
    }

    // ================================================================ edit durante Document

    [Fact]
    public async Task Edits_during_a_document_run_never_change_its_input_and_the_new_revisions_need_their_own_confirmation()
    {
        using var pm = await App.LoginAsync();
        var s = await SeedAnalysis(pm);
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, s.Revision)).StatusCode);
        var run = (await Json(await Generate(pm, s, Guid.NewGuid(), s.Revision))).GetProperty("id").GetGuid();
        var v1 = (await Revisions(s.Analysis)).Single();

        var firstEdit = await Content(pm, s);
        firstEdit["goals"]![0]!["text"] = "Obiettivo modificato durante la generazione";
        HttpResponseMessage? put1 = null, put2 = null, generateWhileActive = null, confirmV2 = null;
        Guid v2 = Guid.Empty;
        _dgx.OnCompletion = async _ =>
        {
            // L'inferenza di Document e' in corso: una seconda generazione e' rifiutata, poi il PM corregge due volte l'anteprima.
            generateWhileActive = await Generate(pm, s, Guid.NewGuid(), s.Revision);
            put1 = await Put(pm, s, s.Revision, firstEdit);
            v2 = (await Json(put1)).GetProperty("currentRevisionId").GetGuid();
            var secondEdit = await Content(pm, s);
            secondEdit["goals"]![0]!["text"] = "Obiettivo corretto una seconda volta";
            put2 = await Put(pm, s, v2, secondEdit);
            confirmV2 = await Confirm(pm, s, v2); // v2 non e' piu' la corrente
        };

        Assert.True(await _dispatcher!.RunOnceAsync(Ct));
        _dgx.OnCompletion = null;

        Assert.Equal(HttpStatusCode.OK, put1!.StatusCode);
        Assert.Equal(HttpStatusCode.OK, put2!.StatusCode);
        var v3 = (await Json(put2)).GetProperty("currentRevisionId").GetGuid();
        Assert.Equal("processing_active", (await AssertProblem(generateWhileActive!, HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal("revision_conflict", (await AssertProblem(confirmV2!, HttpStatusCode.Conflict)).GetProperty("code").GetString());

        // Il run e la bozza restano legati alla v1 confermata, il cui JSON e' quello congelato.
        Assert.Equal("Completed", await Scalar<string>("""SELECT "Status" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", run)));
        Assert.Equal(s.Revision, await Scalar<Guid>("""SELECT "InputRevisionId" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", run)));
        var draft = await Json(await pm.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        Assert.Equal(s.Revision, draft.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal(1, draft.GetProperty("analysisVersion").GetInt32());
        Assert.Contains("Realizzare un portale clienti", draft.GetProperty("markdown").GetString());
        Assert.DoesNotContain("modificato durante", draft.GetProperty("markdown").GetString());
        Assert.True(draft.GetProperty("hasNewRevision").GetBoolean());
        var revisions = await Revisions(s.Analysis);
        Assert.Equal(3, revisions.Count);
        Assert.Equal(v1, revisions[0]); // la v1 confermata e' byte per byte quella dell'avvio
        Assert.Equal(new[] { "Unvalidated", "Unvalidated" }, revisions.Skip(1).Select(r => r.State));
        AssertAppendOnly([v1], revisions);

        // Riconferma obbligatoria: ne' la v1 (non piu' corrente) ne' la v3 non confermata possono generare.
        var stale = await AssertProblem(await Generate(pm, s, Guid.NewGuid(), s.Revision), HttpStatusCode.Conflict);
        Assert.Equal("revision_conflict", stale.GetProperty("code").GetString());
        Assert.Equal(v3, stale.GetProperty("currentRevisionId").GetGuid());
        Assert.Equal("analysis_not_confirmed", (await AssertProblem(await Generate(pm, s, Guid.NewGuid(), v3), HttpStatusCode.Conflict)).GetProperty("code").GetString());
        var view = await Json(await pm.GetAsync(Url(s), Ct));
        Assert.Equal((true, true, false), (view.GetProperty("canEdit").GetBoolean(), view.GetProperty("canConfirm").GetBoolean(), view.GetProperty("canGenerate").GetBoolean()));

        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, v3)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Generate(pm, s, Guid.NewGuid(), v3)).StatusCode);
        await Drain();

        var latest = await Json(await pm.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        Assert.Equal(v3, latest.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal(3, latest.GetProperty("analysisVersion").GetInt32());
        Assert.Contains("corretto una seconda volta", latest.GetProperty("markdown").GetString());
        Assert.False(latest.GetProperty("hasNewRevision").GetBoolean());
        Assert.Equal(2, await Count("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p""", ("p", s.Project))); // la prima bozza resta consultabile
        Assert.Equal(draft.GetProperty("markdown").GetString(), await Scalar<string>("""SELECT "Markdown" FROM "Drafts" WHERE "Id" = @d""", ("d", draft.GetProperty("id").GetGuid())));
        var final = await Revisions(s.Analysis);
        AssertAppendOnly(revisions, final);
        Assert.Equal(new[] { "PmConfirmed", "Unvalidated", "PmConfirmed" }, final.Select(r => r.State));
    }

    // ================================================================ nuova Analysis fallita

    [Fact]
    public async Task A_failed_new_analysis_blocks_edits_while_active_then_leaves_the_preview_the_draft_and_the_pointers_untouched()
    {
        using var pm = await App.LoginAsync();
        var s = await SeedAnalysis(pm);
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, s.Revision)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Generate(pm, s, Guid.NewGuid(), s.Revision)).StatusCode);
        await Drain();
        var draftBefore = await Json(await pm.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        await PostMessage(pm, s.Project, "Aggiungiamo anche la fatturazione.");
        await Drain(); // risposta della chat
        var previewBefore = await pm.GetAsync(Url(s), Ct);
        var viewBefore = await Json(previewBefore);
        var snapshot = await Snapshot();
        var content = JsonNode.Parse(viewBefore.GetProperty("content").GetRawText())!;

        var key = Guid.NewGuid();
        var queued = await Analyze(pm, s.Project, key);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var failedRun = (await Json(queued)).GetProperty("id").GetGuid();

        // Finche' la nuova Analysis e' attiva la vecchia anteprima e' leggibile e non modificabile.
        foreach (var response in new[] { await Put(pm, s, s.Revision, content), await Generate(pm, s, Guid.NewGuid(), s.Revision) })
        {
            var problem = await AssertProblem(response, HttpStatusCode.Conflict);
            Assert.Equal("processing_active", problem.GetProperty("code").GetString());
            Assert.Equal(failedRun, problem.GetProperty("processingRunId").GetGuid());
        }

        // La conferma ripetuta di una revisione gia' confermata e ancora corrente e' idempotente (contratto) e non scrive nulla; una non confermata e' rifiutata.
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, s.Revision)).StatusCode);
        Assert.Equal(snapshot, await Snapshot());

        var during = await Json(await pm.GetAsync(Url(s), Ct));
        Assert.Equal((false, false, false), (during.GetProperty("canEdit").GetBoolean(), during.GetProperty("canConfirm").GetBoolean(), during.GetProperty("canGenerate").GetBoolean()));
        Assert.Equal(viewBefore.GetProperty("content").GetRawText(), during.GetProperty("content").GetRawText());

        _dgx.Faults.Enqueue(FakeDgx.Status(401));
        await Drain();

        var run = await Json(await pm.GetAsync($"/api/projects/{s.Project}/processing-runs/{failedRun}", Ct));
        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Equal("model_unauthorized", run.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(snapshot, await Snapshot()); // nessuna revisione, bozza o puntatore e' cambiato
        Assert.Equal(0, await Count("""SELECT count(*) FROM "AnalysisRevisions" WHERE "AnalysisId" = @a""", ("a", failedRun)));
        var failedPreview = await AssertProblem(await pm.GetAsync($"/api/projects/{s.Project}/analyses/{failedRun}", Ct), HttpStatusCode.NotFound);
        Assert.Equal("preview_not_available", failedPreview.GetProperty("code").GetString()); // nessun JSON riuscito inventato

        var previewAfter = await pm.GetAsync(Url(s), Ct);
        var viewAfter = await Json(previewAfter);
        Assert.Equal(previewBefore.Headers.ETag!.Tag, previewAfter.Headers.ETag!.Tag);
        Assert.Equal(viewBefore.GetProperty("content").GetRawText(), viewAfter.GetProperty("content").GetRawText());
        Assert.Equal((true, false, true), (viewAfter.GetProperty("canEdit").GetBoolean(), viewAfter.GetProperty("canConfirm").GetBoolean(), viewAfter.GetProperty("canGenerate").GetBoolean()));
        Assert.True(viewAfter.GetProperty("hasNewNotes").GetBoolean()); // il messaggio PM oltre il cutoff resta segnalato
        var draftAfter = await Json(await pm.GetAsync($"/api/projects/{s.Project}/draft", Ct));
        Assert.Equal(draftBefore.GetProperty("id").GetGuid(), draftAfter.GetProperty("id").GetGuid());
        Assert.Equal(draftBefore.GetProperty("markdown").GetString(), draftAfter.GetProperty("markdown").GetString());

        // La stessa chiave non riparte; un nuovo comando esplicito e' un'altra richiesta; l'anteprima torna modificabile.
        var replay = await Analyze(pm, s.Project, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(failedRun, (await Json(replay)).GetProperty("id").GetGuid());
        Assert.False(await _dispatcher!.RunOnceAsync(Ct));
        var edit = await Put(pm, s, s.Revision, content);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(2, (await Revisions(s.Analysis)).Count);
        var fresh = await Analyze(pm, s.Project, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
        Assert.NotEqual(failedRun, (await Json(fresh)).GetProperty("id").GetGuid());
    }

    // ================================================================ chiavi ripetute, mismatch e risposta unica

    [Fact]
    public async Task A_repeated_analysis_key_returns_the_same_run_even_simultaneously_and_never_a_second_inference()
    {
        using var pm = await App.LoginAsync();
        var project = await CreateProject(pm);
        await PostMessage(pm, project, "Vogliamo un portale clienti.");
        await Drain();
        _dgx.ResetCount();
        var key = Guid.NewGuid();

        var first = await Together(Enumerable.Range(0, 6).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => Analyze(pm, project, key)).ToArray());

        Assert.All(first, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var views = await Task.WhenAll(first.Select(Json));
        var run = Assert.Single(views.Select(v => v.GetProperty("id").GetGuid()).Distinct());
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p""", ("p", project)));
        var cutoff = views[0].GetProperty("sourceCutoffSequence").GetInt64();

        await Drain();
        Assert.Equal(1, _dgx.Completions); // una sola inferenza per sei richieste
        await PostMessage(pm, project, "Un altro messaggio dopo la richiesta."); // il cutoff avanza, la chiave resta quella

        var replay = await Analyze(pm, project, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await Json(replay);
        Assert.Equal(run, replayed.GetProperty("id").GetGuid());
        Assert.Equal("Completed", replayed.GetProperty("status").GetString());
        Assert.Equal(cutoff, replayed.GetProperty("sourceCutoffSequence").GetInt64()); // l'input originale, non il cutoff attuale
        Assert.Equal(1, await Count("""SELECT count(*) FROM "AnalysisRevisions" WHERE "AnalysisId" = @a""", ("a", run)));
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p""", ("p", project)));
        Assert.Equal(1, _dgx.Completions); // il turno chat del messaggio nuovo non e' stato eseguito: nessun'altra inferenza di analisi
        var other = await Analyze(pm, project, Guid.NewGuid()); // un nuovo comando esplicito con un'altra chiave
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        Assert.NotEqual(run, (await Json(other)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_repeated_generation_key_replays_the_same_run_and_a_mismatching_input_is_an_idempotency_conflict()
    {
        using var pm = await App.LoginAsync();
        var s = await SeedAnalysis(pm);
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, s.Revision)).StatusCode);
        _dgx.ResetCount();
        var key = Guid.NewGuid();

        var first = await Together(Enumerable.Range(0, 6).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => Generate(pm, s, key, s.Revision)).ToArray());

        Assert.All(first, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var run = Assert.Single((await Task.WhenAll(first.Select(Json))).Select(v => v.GetProperty("id").GetGuid()).Distinct());
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Kind" = 'Document'""", ("p", s.Project)));
        await Drain();
        Assert.Equal(1, _dgx.Completions);
        var draft = await Scalar<Guid>("""SELECT "DraftId" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", run));

        var replay = await Generate(pm, s, key, s.Revision);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await Json(replay);
        Assert.Equal((run, "Completed", draft), (replayed.GetProperty("id").GetGuid(), replayed.GetProperty("status").GetString(), replayed.GetProperty("draftId").GetGuid()));

        // Input diverso con la stessa chiave: revisione corrente differente, poi altra analisi.
        var edit = await Content(pm, s);
        edit["goals"]![0]!["text"] = "Obiettivo rivisto";
        var v2 = (await Json(await Put(pm, s, s.Revision, edit))).GetProperty("currentRevisionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, v2)).StatusCode);
        var differentRevision = await AssertProblem(await Generate(pm, s, key, v2), HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", differentRevision.GetProperty("code").GetString());
        Assert.Equal(run, differentRevision.GetProperty("processingRunId").GetGuid());
        var oldReplay = await Generate(pm, s, key, s.Revision); // stesso input, v1 non piu' corrente: ancora lo stesso run
        Assert.Equal(HttpStatusCode.OK, oldReplay.StatusCode);
        Assert.Equal(run, (await Json(oldReplay)).GetProperty("id").GetGuid());

        var second = await SeedAnalysisFromExisting(pm, s.Project); // la seconda analisi diventa la corrente
        var differentAnalysis = await AssertProblem(await Generate(pm, second, key, second.Revision), HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", differentAnalysis.GetProperty("code").GetString());

        Assert.Equal(1, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Kind" = 'Document'""", ("p", s.Project)));
        Assert.Equal(1, await Count("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p""", ("p", s.Project)));
    }

    [Fact]
    public async Task A_repeated_client_message_id_creates_one_message_one_turn_and_a_single_reply_even_with_two_dispatchers()
    {
        using var pm = await App.LoginAsync();
        var project = await CreateProject(pm);
        var clientId = Guid.NewGuid();
        const string text = "Il portale si chiama Aurora.";

        var posts = await Together(Enumerable.Range(0, 6).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => PostMessage(pm, project, text, clientId, expected: null)).ToArray());

        Assert.Equal(1, posts.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, posts.Count(r => r.StatusCode == HttpStatusCode.OK));
        var turns = (await Task.WhenAll(posts.Select(Json))).Select(v => v.GetProperty("chatTurnId").GetGuid()).Distinct().ToList();
        var turn = Assert.Single(turns);
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ConversationMessages" WHERE "ClientMessageId" = @c""", ("c", clientId)));
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ChatTurns" """));
        var mismatch = await PostMessage(pm, project, text + " Modificato.", clientId, expected: null);
        Assert.Equal("idempotency_conflict", (await AssertProblem(mismatch, HttpStatusCode.Conflict)).GetProperty("code").GetString());

        // Due processi (owner distinti) si contendono lo stesso turno: il claim e il limite AI globale ne lasciano correre uno.
        var other = ActivatorUtilities.CreateInstance<AiDispatchService>(App.Services);
        _dgx.ResetCount();
        var ran = await Task.WhenAll(_dispatcher!.RunOnceAsync(Ct), other.RunOnceAsync(Ct));

        Assert.Single(ran, r => r);
        Assert.Equal(1, _dgx.Completions);
        Assert.Equal("Completed", await Scalar<string>("""SELECT "Status" FROM "ChatTurns" WHERE "Id" = @t""", ("t", turn)));
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ConversationMessages" WHERE "Role" = 'Assistant'"""));
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ConversationMessages" WHERE "ChatTurnId" = @t""", ("t", turn)));

        var replay = await PostMessage(pm, project, text, clientId, expected: null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(turn, (await Json(replay)).GetProperty("chatTurnId").GetGuid());
        Assert.False(await _dispatcher!.RunOnceAsync(Ct));
        Assert.False(await other.RunOnceAsync(Ct));
        Assert.Equal(1, _dgx.Completions);
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ConversationMessages" WHERE "Role" = 'Assistant'"""));
    }

    // ================================================================ hasNewNotes e riconferma

    [Fact]
    public async Task HasNewNotes_counts_only_pm_messages_beyond_the_cutoff_and_every_new_revision_or_analysis_needs_its_own_confirmation()
    {
        using var pm = await App.LoginAsync();
        var a = await SeedAnalysis(pm); // cutoff = messaggio PM 1; la risposta della chat e' oltre il cutoff
        var cutoff = (await Json(await pm.GetAsync(Url(a), Ct))).GetProperty("sourceCutoffSequence").GetInt64();
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ConversationMessages" WHERE "Role" = 'Assistant' AND "Sequence" > @c""", ("c", cutoff)));
        Assert.False(await HasNewNotes(pm, a));

        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, a, a.Revision)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Generate(pm, a, Guid.NewGuid(), a.Revision)).StatusCode);
        await Drain();
        var first = await Json(await pm.GetAsync($"/api/projects/{a.Project}/draft", Ct));
        Assert.Equal((false, false), (first.GetProperty("hasNewNotes").GetBoolean(), first.GetProperty("hasNewRevision").GetBoolean())); // solo una risposta dell'assistente oltre il cutoff

        await PostMessage(pm, a.Project, "Il portale si chiama Aurora.");
        Assert.True(await HasNewNotes(pm, a));
        await Drain(); // la risposta dell'assistente al secondo messaggio non cambia nulla
        var second = await Json(await pm.GetAsync($"/api/projects/{a.Project}/draft", Ct));
        Assert.Equal((true, false), (second.GetProperty("hasNewNotes").GetBoolean(), second.GetProperty("hasNewRevision").GetBoolean()));

        // Un edit crea una revisione non confermata: la bozza e' "vecchia" e serve una nuova conferma prima di generare.
        var edit = await Content(pm, a);
        edit["goals"]![0]!["text"] = "Il portale Aurora riduce i tempi di risposta";
        var v2 = (await Json(await Put(pm, a, a.Revision, edit))).GetProperty("currentRevisionId").GetGuid();
        var viewV2 = await Json(await pm.GetAsync(Url(a), Ct));
        Assert.Equal((true, true, false, true), (viewV2.GetProperty("canEdit").GetBoolean(), viewV2.GetProperty("canConfirm").GetBoolean(), viewV2.GetProperty("canGenerate").GetBoolean(), viewV2.GetProperty("hasNewNotes").GetBoolean()));
        Assert.True((await Json(await pm.GetAsync($"/api/projects/{a.Project}/draft", Ct))).GetProperty("hasNewRevision").GetBoolean());
        Assert.Equal("revision_conflict", (await AssertProblem(await Generate(pm, a, Guid.NewGuid(), a.Revision), HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal("analysis_not_confirmed", (await AssertProblem(await Generate(pm, a, Guid.NewGuid(), v2), HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, a, v2)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Generate(pm, a, Guid.NewGuid(), v2)).StatusCode);
        await Drain();
        var third = await Json(await pm.GetAsync($"/api/projects/{a.Project}/draft", Ct));
        Assert.Equal(v2, third.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal((true, false), (third.GetProperty("hasNewNotes").GetBoolean(), third.GetProperty("hasNewRevision").GetBoolean())); // il cutoff dell'analisi e' sempre il primo

        // Una nuova Analysis include il secondo messaggio ma e' una nuova anteprima: Unvalidated, da confermare da capo.
        var b = await SeedAnalysisFromExisting(pm, a.Project);
        var viewB = await Json(await pm.GetAsync(Url(b), Ct));
        Assert.Equal("Unvalidated", viewB.GetProperty("validationState").GetString());
        Assert.Equal((false, true, true, false), (viewB.GetProperty("hasNewNotes").GetBoolean(), viewB.GetProperty("canEdit").GetBoolean(), viewB.GetProperty("canConfirm").GetBoolean(), viewB.GetProperty("canGenerate").GetBoolean()));
        Assert.True(viewB.GetProperty("sourceCutoffSequence").GetInt64() > cutoff);
        Assert.Equal("analysis_not_confirmed", (await AssertProblem(await Generate(pm, b, Guid.NewGuid(), b.Revision), HttpStatusCode.Conflict)).GetProperty("code").GetString());

        // La vecchia analisi non e' piu' modificabile ne' utilizzabile, ma le sue revisioni e la sua bozza restano leggibili.
        var oldViewContent = await Content(pm, a);
        Assert.Equal("analysis_not_current", (await AssertProblem(await Put(pm, a, v2, oldViewContent), HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal("analysis_not_current", (await AssertProblem(await Generate(pm, a, Guid.NewGuid(), v2), HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await pm.GetAsync($"{Url(a)}/revisions/1", Ct)).StatusCode);
        var stale = await Json(await pm.GetAsync($"/api/projects/{a.Project}/draft", Ct));
        Assert.Equal((true, true), (stale.GetProperty("hasNewNotes").GetBoolean(), stale.GetProperty("hasNewRevision").GetBoolean()));

        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, b, b.Revision)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Generate(pm, b, Guid.NewGuid(), b.Revision)).StatusCode);
        await Drain();
        var last = await Json(await pm.GetAsync($"/api/projects/{a.Project}/draft", Ct));
        Assert.Equal(b.Revision, last.GetProperty("analysisRevisionId").GetGuid());
        Assert.Equal((false, false), (last.GetProperty("hasNewNotes").GetBoolean(), last.GetProperty("hasNewRevision").GetBoolean()));
        Assert.Equal(3, await Count("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p""", ("p", a.Project)));
    }

    // ================================================================ immutabilita' delle revisioni

    [Fact]
    public async Task Historical_revisions_stay_readable_and_identical_through_edits_confirmations_generations_and_a_new_analysis()
    {
        using var pm = await App.LoginAsync();
        var s = await SeedAnalysis(pm);
        var original = await RevisionView(pm, s, 1);
        Assert.Equal(("Ai", "Unvalidated"), (original.GetProperty("origin").GetString(), original.GetProperty("validationState").GetString()));
        Assert.DoesNotContain(original.GetProperty("content").GetProperty("sources").EnumerateArray(), x => x.GetProperty("kind").GetString() == "PmRevision");

        var edit = await Content(pm, s);
        edit["goals"]![0]!["text"] = "Prima correzione";
        var v2 = (await Json(await Put(pm, s, s.Revision, edit))).GetProperty("currentRevisionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, v2)).StatusCode);
        var confirmedV2 = await RevisionView(pm, s, 2);
        edit = await Content(pm, s);
        edit["goals"]![0]!["text"] = "Seconda correzione dopo la conferma";
        var v3 = (await Json(await Put(pm, s, v2, edit))).GetProperty("currentRevisionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, s, v3)).StatusCode);
        var confirmedV3 = await RevisionView(pm, s, 3);
        Assert.Equal(HttpStatusCode.Accepted, (await Generate(pm, s, Guid.NewGuid(), v3)).StatusCode);
        await Drain();
        var b = await SeedAnalysisFromExisting(pm, s.Project);

        // Prima conferma e stato corrente: tutte le revisioni storiche restano quelle lette allora, salvo il solo campo isCurrent.
        Assert.Equal(Plain(original), Plain(await RevisionView(pm, s, 1)));
        Assert.Equal(Plain(confirmedV2), Plain(await RevisionView(pm, s, 2)));
        Assert.Equal(Plain(confirmedV3), Plain(await RevisionView(pm, s, 3)));
        Assert.Equal(new[] { "Ai", "PmEdit", "PmEdit" }, new[] { original, confirmedV2, confirmedV3 }.Select(v => v.GetProperty("origin").GetString()));
        Assert.Equal(v2, confirmedV3.GetProperty("parentRevisionId").GetGuid());
        Assert.Equal(s.Revision, confirmedV2.GetProperty("parentRevisionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, original.GetProperty("parentRevisionId").ValueKind);
        Assert.False((await RevisionView(pm, s, 1)).GetProperty("isCurrent").GetBoolean());
        Assert.True((await RevisionView(pm, s, 3)).GetProperty("isCurrent").GetBoolean()); // la corrente della propria analisi, ormai superata

        // Le fonti storiche e le citazioni delle versioni precedenti sono preservate; ogni edit aggiunge una sola fonte PmRevision.
        var sources1 = SourcesOf(original);
        var sources2 = SourcesOf(confirmedV2);
        var sources3 = SourcesOf(confirmedV3);
        Assert.Equal(sources1, sources2.Take(sources1.Count).ToList());
        Assert.Equal(sources2, sources3.Take(sources2.Count).ToList());
        Assert.Equal(1, sources2.Count - sources1.Count);
        Assert.Equal(1, sources3.Count - sources2.Count);
        Assert.Equal(v2, JsonNode.Parse(sources2[^1])!["revisionId"]!.GetValue<Guid>());
        Assert.Equal(v3, JsonNode.Parse(sources3[^1])!["revisionId"]!.GetValue<Guid>());
        AssertAppendOnly(await Revisions(s.Analysis), await Revisions(s.Analysis));
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(pm, s, s.Revision)).StatusCode); // una revisione storica non si conferma piu'
        Assert.Single(await Revisions(b.Analysis));
    }

    // ================================================================ gara con il completamento di una nuova Analysis

    [Theory]
    [InlineData("put")]
    [InlineData("confirm")]
    [InlineData("generate")]
    public async Task A_new_analysis_completing_between_the_guards_cannot_let_a_command_through_on_the_superseded_analysis(string command)
    {
        using var pm = await App.LoginAsync();
        var a = await SeedAnalysis(pm);
        if (command == "generate")
        {
            Assert.Equal(HttpStatusCode.OK, (await Confirm(pm, a, a.Revision)).StatusCode);
        }

        var content = await Content(pm, a);
        var before = await Revisions(a.Analysis);
        var queued = await Analyze(pm, a.Project, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var b = (await Json(queued)).GetProperty("id").GetGuid();

        // La nuova Analysis termina (commit reale con CurrentAnalysisId) subito dopo che il comando ha letto l'analisi corrente.
        _hook.After = Drain;
        var response = command switch
        {
            "put" => await Put(pm, a, a.Revision, content),
            "confirm" => await Confirm(pm, a, a.Revision),
            _ => await Generate(pm, a, Guid.NewGuid(), a.Revision),
        };

        Assert.True(_hook.Fired, "la gara non e' stata innescata");
        Assert.Equal(b, await Scalar<Guid>("""SELECT "CurrentAnalysisId" FROM "Projects" WHERE "Id" = @p""", ("p", a.Project)));
        var problem = await AssertProblem(response, HttpStatusCode.Conflict);
        Assert.Contains(problem.GetProperty("code").GetString(), new[] { "processing_active", "analysis_not_current" });
        Assert.Equal(before, await Revisions(a.Analysis)); // nessuna scrittura sulla anteprima superata
        Assert.Equal(0, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = @p AND "Kind" = 'Document'""", ("p", a.Project)));
    }

    // ================================================================ supporto

    private sealed record Seeded(Guid Project, Guid Analysis, Guid Revision);

    private sealed record Rev(
        Guid Id, int Version, Guid? Parent, string Origin, Guid CreatedBy, DateTimeOffset CreatedAt, string Content, string State, Guid? ConfirmedBy, DateTimeOffset? ConfirmedAt);

    private static string Url(Seeded s, string suffix = "") => $"/api/projects/{s.Project}/analyses/{s.Analysis}{suffix}";

    /// <summary>Esegue il dispatcher finche' non ha piu' lavoro ammissibile: turni di chat, Analysis e Document in ordine FIFO.</summary>
    private async Task Drain()
    {
        for (var i = 0; i < 30 && await _dispatcher!.RunOnceAsync(Ct); i++)
        {
        }
    }

    private static async Task<T[]> Together<T>(params Func<Task<T>>[] calls)
    {
        var gate = new TaskCompletionSource();
        var tasks = calls.Select(call => Task.Run(async () =>
        {
            await gate.Task;
            return await call();
        }, Ct)).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private async Task<Guid> CreateProject(HttpClient pm)
    {
        var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Concorrenza {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Json(created)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> PostMessage(HttpClient pm, Guid project, string text, Guid? clientId = null, HttpStatusCode? expected = HttpStatusCode.Created)
    {
        var response = await pm.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = clientId ?? Guid.NewGuid(), text }, Ct);
        if (expected is { } status)
        {
            Assert.Equal(status, response.StatusCode);
        }

        return response;
    }

    private static Task<HttpResponseMessage> Analyze(HttpClient pm, Guid project, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", key.ToString());
        return pm.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Generate(HttpClient pm, Seeded s, Guid key, Guid revisionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/document-generations")) { Content = JsonContent.Create(new { revisionId }) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return pm.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Put(HttpClient pm, Seeded s, Guid ifMatch, JsonNode content) => PutRaw(pm, s, ifMatch, content.ToJsonString());

    private static Task<HttpResponseMessage> PutRaw(HttpClient pm, Seeded s, Guid ifMatch, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Url(s, "/preview")) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        return pm.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient pm, Seeded s, Guid ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(s, "/confirm"));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        return pm.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> Content(HttpClient pm, Seeded s) =>
        JsonNode.Parse((await Json(await pm.GetAsync(Url(s), Ct))).GetProperty("content").GetRawText())!;

    private static async Task<JsonElement> RevisionView(HttpClient pm, Seeded s, int version)
    {
        var response = await pm.GetAsync(Url(s, $"/revisions/{version}"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    /// <summary>La revisione letta senza il solo campo calcolato isCurrent, che cambia quando ne arrivano di nuove.</summary>
    private static string Plain(JsonElement view)
    {
        var node = JsonNode.Parse(view.GetRawText())!.AsObject();
        node.Remove("isCurrent");
        return node.ToJsonString();
    }

    private static List<string> SourcesOf(JsonElement view) =>
        [.. view.GetProperty("content").GetProperty("sources").EnumerateArray().Select(x => x.GetRawText())];

    private static async Task<bool> HasNewNotes(HttpClient pm, Seeded s) =>
        (await Json(await pm.GetAsync(Url(s), Ct))).GetProperty("hasNewNotes").GetBoolean();

    /// <summary>Progetto nuovo con un messaggio PM e un'analisi completata dal workflow reale (revisione AI v1 Unvalidated, chat gia' risposta).</summary>
    private async Task<Seeded> SeedAnalysis(HttpClient pm)
    {
        var project = await CreateProject(pm);
        await PostMessage(pm, project, $"Vogliamo un portale clienti per ridurre i tempi di risposta ai ticket ({++_texts}).");
        return await AnalyzeAndComplete(pm, project);
    }

    /// <summary>Una nuova Analysis sul progetto esistente, completata dal workflow reale; diventa l'analisi corrente.</summary>
    private Task<Seeded> SeedAnalysisFromExisting(HttpClient pm, Guid project) => AnalyzeAndComplete(pm, project);

    private async Task<Seeded> AnalyzeAndComplete(HttpClient pm, Guid project)
    {
        var queued = await Analyze(pm, project, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var run = (await Json(queued)).GetProperty("id").GetGuid();
        await Drain();
        Assert.Equal("Completed", await Scalar<string>("""SELECT "Status" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", run)));
        Assert.Equal(run, await Scalar<Guid>("""SELECT "CurrentAnalysisId" FROM "Projects" WHERE "Id" = @p""", ("p", project)));
        return new Seeded(project, run, await Scalar<Guid>("""SELECT "ResultRevisionId" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", run)));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await Json(response);
    }

    // ---------------------------------------------------------------- database

    private async Task<List<Rev>> Revisions(Guid analysis)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT "Id", "Version", "ParentRevisionId", "Origin", "CreatedBy", "CreatedAt", "ContentJson"::text, "ValidationState", "ConfirmedBy", "ConfirmedAt"
            FROM "AnalysisRevisions" WHERE "AnalysisId" = @a ORDER BY "Version"
            """, connection);
        command.Parameters.AddWithValue("a", analysis);
        await using var r = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Rev>();
        while (await r.ReadAsync(Ct))
        {
            rows.Add(new Rev(
                r.GetGuid(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5),
                r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9)));
        }

        return rows;
    }

    /// <summary>
    /// Storia append-only: stessa catena (versioni contigue, parent, Origin Ai solo per la v1), nessuna revisione cambia salvo il
    /// passaggio Unvalidated -> PmConfirmed dell'ultima (solo stato, metadati di conferma e validationState del JSON), e una revisione
    /// confermata non cambia piu' in nessun campo. Il JSON e la colonna di stato sono sempre coerenti.
    /// </summary>
    private static void AssertAppendOnly(IReadOnlyList<Rev> before, IReadOnlyList<Rev> after)
    {
        Assert.True(after.Count >= before.Count);
        for (var i = 0; i < after.Count; i++)
        {
            Assert.Equal(i + 1, after[i].Version);
            Assert.Equal(i == 0 ? null : after[i - 1].Id, after[i].Parent);
            Assert.Equal(i == 0 ? "Ai" : "PmEdit", after[i].Origin);
            Assert.Equal(after[i].State, JsonNode.Parse(after[i].Content)!["validationState"]!.GetValue<string>());
            Assert.Equal(after[i].State == "PmConfirmed", after[i].ConfirmedBy is not null && after[i].ConfirmedAt is not null);
        }

        for (var i = 0; i < before.Count; i++)
        {
            var (b, a) = (before[i], after[i]);
            Assert.Equal((b.Id, b.Version, b.Parent, b.Origin, b.CreatedBy, b.CreatedAt), (a.Id, a.Version, a.Parent, a.Origin, a.CreatedBy, a.CreatedAt));
            if (b.State == "PmConfirmed" || a.State == b.State)
            {
                Assert.Equal(b, a);
                continue;
            }

            Assert.Equal(("Unvalidated", "PmConfirmed", before.Count - 1), (b.State, a.State, i)); // solo la corrente, solo verso la conferma
            Assert.Equal(WithoutState(b.Content), WithoutState(a.Content));
        }
    }

    private static string WithoutState(string content)
    {
        var node = JsonNode.Parse(content)!.AsObject();
        node.Remove("validationState");
        return node.ToJsonString();
    }

    /// <summary>Messaggi, revisioni, bozze e puntatori del progetto come JSON dei record veri: cio' che un fallimento non deve toccare.</summary>
    private Task<string> Snapshot() => Scalar<string>(
        """
        SELECT (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY m."Sequence"), '[]') FROM "ConversationMessages" m)::text
            || (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r."AnalysisId", r."Version"), '[]') FROM "AnalysisRevisions" r)::text
            || (SELECT coalesce(jsonb_agg(to_jsonb(d) ORDER BY d."CreatedAt", d."Id"), '[]') FROM "Drafts" d)::text
            || (SELECT coalesce(jsonb_agg(jsonb_build_array(p."CurrentAnalysisId", p."LatestDraftId") ORDER BY p."Id"), '[]') FROM "Projects" p)::text
        """);

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters) => await Scalar<long>(sql, parameters);

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    // ---------------------------------------------------------------- iniezione nel database

    /// <summary>
    /// Innesta un'azione subito dopo che il comando ha letto Project.CurrentAnalysisId (la prima lettura che la contiene dopo averla
    /// armata): e' il punto della gara tra le guardie del comando e il commit di una nuova Analysis, che non prende il lock della conversazione.
    /// </summary>
    private sealed class QueryHook : DbCommandInterceptor
    {
        public Func<Task>? After { get; set; }

        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (After is { } action && command.CommandText.Contains("FROM \"Projects\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"CurrentAnalysisId\"", StringComparison.Ordinal))
            {
                After = null;
                Fired = true;
                await action();
            }

            return result;
        }
    }

    // ---------------------------------------------------------------- server del modello finto

    /// <summary>
    /// Il server interno (llama.cpp OpenAI-compatibile) finto, dietro il vero client: risposte nominali costruite dalla richiesta
    /// (chat, analisi e documento), errori HTTP in coda e conteggio delle richieste di completamento realmente arrivate.
    /// </summary>
    private sealed class FakeDgx : HttpMessageHandler
    {
        private int _completions;

        public ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> Faults { get; } = new();

        /// <summary>Eseguito a ogni richiesta di completamento, prima dell'errore in coda o della risposta.</summary>
        public Func<CancellationToken, Task>? OnCompletion { get; set; }

        public int Completions => Volatile.Read(ref _completions);

        public void ResetCount() => Volatile.Write(ref _completions, 0);

        public static Func<HttpRequestMessage, HttpResponseMessage> Status(int status) => _ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{\"error\":{\"message\":\"errore\",\"type\":\"server_error\"}}", Encoding.UTF8, "application/json"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/tokenize", StringComparison.Ordinal))
            {
                return Json("""{"tokens":[1,2,3,4,5]}""");
            }

            Interlocked.Increment(ref _completions);
            if (OnCompletion is not null)
            {
                await OnCompletion(cancellationToken);
            }

            if (Faults.TryDequeue(out var fault))
            {
                return fault(request);
            }

            var user = UserText(body);
            var text = user.Contains("JSON AnalysisContent 1.0.0 completo", StringComparison.Ordinal) ? WorkflowRecoveryTests.DocumentReply(user)
                : user.Contains("sourceCutoffSequence: ", StringComparison.Ordinal) ? WorkflowRecoveryTests.AnalysisReply(user)
                : "Risposta del modello";
            return Regex.IsMatch(body, "\"stream\"\\s*:\\s*true") ? Stream(text) : Json(Completion(text));
        }

        private static string UserText(string body)
        {
            using var json = JsonDocument.Parse(body);
            var parts = new List<string>();
            foreach (var message in json.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("role").GetString() is "system" or "developer")
                {
                    continue;
                }

                var content = message.GetProperty("content");
                parts.Add(content.ValueKind == JsonValueKind.String
                    ? content.GetString()!
                    : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : "")));
            }

            return string.Join("\n", parts);
        }

        private static string Completion(string text) => JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion",
            created = 1,
            model = "test-model",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
        });

        private static HttpResponseMessage Stream(string text)
        {
            string Chunk(object delta, string? finish) => "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl-test",
                @object = "chat.completion.chunk",
                created = 1,
                model = "test-model",
                choices = new[] { new { index = 0, delta, finish_reason = finish } },
            }) + "\n\n";

            var sse = Chunk(new { role = "assistant", content = text }, null) + Chunk(new { }, "stop") + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
