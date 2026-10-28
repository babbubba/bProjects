using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T045 - restore nativo dei due grafi MAF (project-analysis-v2 e project-document-v1) in un nuovo processo, con
/// PostgreSQL reale e piu' "processi" applicativi distinti. Come in <see cref="ChatRecoveryTests"/> ogni processo e' un'applicazione
/// completa (dispatcher ospitato, workflow, checkpoint store e commit reali, owner di lease proprio) che condivide soltanto il
/// database; il crash e' lo smaltimento dell'applicazione a meta' esecuzione, quindi il nuovo processo puo' leggere solo PostgreSQL.
/// L'unico punto di iniezione e' il modello (fake scriptabile) piu' un decoratore dello store del run, registrato solo nell'harness,
/// che osserva le fasi e ferma il lavoro ai confini di commit.
/// Distinzione che le prove rendono esplicita: la RIPRESA DA CHECKPOINT MAF e' un fatto del grafo (le fasi di caricamento non
/// rieseguono, il testimone e' lo store); la RIPETIZIONE dell'inferenza e' un fatto del modello (il checkpoint e' a confine di
/// superstep, mai nel token: una chiamata interrotta si ripete una volta, mai due). Non si pretende ripresa dal token.
/// Nessuna prova MafReal: il task non la prevede e la ripresa dipende dal grafo, non dal modello.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class WorkflowRecoveryTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly PostgresDatabaseFixture _database = new();
    private readonly List<ApiHarness> _processes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Graph
    {
        Analysis,
        Document,
    }

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            // Migrazioni e account una sola volta; i "processi" delle prove partono poi senza preparare nulla.
            await using var setup = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            await process.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    // ---------------------------------------------------------------- crash durante l'inferenza (dopo il checkpoint di caricamento)

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task A_crash_during_inference_resumes_from_the_MAF_checkpoint_and_repeats_only_the_interrupted_inference(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var s = await Prepare(graph);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dying = HoldingModel(graph, entered);
        var probe1 = new Probe();
        var first = await StartProcess(For(graph, dying), probe1);
        var run = await Trigger(first, s);
        await entered.Task.WaitAsync(Patience, Ct);

        var running = await RunOf(run);
        Assert.Equal(("Running", 1, 1L), (running.Status, running.Attempts, running.Fence));
        var checkpointsBefore = await Checkpoints(run);
        Assert.NotEmpty(checkpointsBefore); // il confine di superstep dopo il caricamento e' gia' su PostgreSQL
        Assert.NotNull(running.Session);
        var session = running.Session;

        await Kill(first);

        // Il processo e' morto con il lease: nulla e' stato prodotto e lo stato e' fermo.
        Assert.Equal(0, await ResultCount(s, run));
        Assert.Equal(running.Owner, (await RunOf(run)).Owner);
        await ExpireLease(run);

        var recovering = new Model(Reply(graph));
        var probe2 = new Probe();
        await StartProcess(For(graph, recovering), probe2);
        await WaitCompleted(run);

        var done = await RunOf(run);
        Assert.Equal((2, 2L), (done.Attempts, done.Fence)); // reclaim: nuovo attempt e nuovo fence
        Assert.Null(done.ErrorCode);
        Assert.Equal(session, done.Session);               // stessa sessione MAF: e' lo stesso grafo che riprende

        // RIPRESA DA CHECKPOINT: la fase di caricamento non e' rieseguita dal nuovo processo e nessun checkpoint e' stato riscritto.
        Assert.DoesNotContain(LoadStage(graph), probe2.Stages);
        var after = await Checkpoints(run);
        Assert.Equal(checkpointsBefore.Select(c => c.Key), after.Take(checkpointsBefore.Count).Select(c => c.Key));
        Assert.Equal(checkpointsBefore.Select(c => c.Payload), after.Take(checkpointsBefore.Count).Select(c => c.Payload));

        // RIPETIZIONE CONTROLLATA DELL'INFERENZA: la chiamata interrotta si ripete una sola volta; non e' ripresa dal token.
        Assert.Equal(1, dying.Calls);
        Assert.Equal(1, recovering.Calls);
        Assert.Equal(1, await ResultCount(s, run));
        Assert.Equal(done.Result, await ProjectPointer(s));
    }

    // ---------------------------------------------------------------- crash dopo il checkpoint dell'agente, prima del commit

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task A_crash_after_the_agent_checkpoint_and_before_the_commit_resumes_without_any_inference(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var s = await Prepare(graph);
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe1 = new Probe
        {
            BeforeCommit = async ct =>
            {
                atCommit.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // il processo muore con la risposta valida in mano, prima del commit
            },
        };
        var model1 = new Model(Reply(graph));
        var first = await StartProcess(For(graph, model1), probe1);
        var run = await Trigger(first, s);
        await atCommit.Task.WaitAsync(Patience, Ct);
        var checkpointsBefore = await Checkpoints(run);
        Assert.True(checkpointsBefore.Count >= 2, "il checkpoint dopo l'agente e' gia' su PostgreSQL"); // caricamento + agente

        await Kill(first);

        Assert.Equal(0, await ResultCount(s, run)); // nulla e' stato pubblicato
        await ExpireLease(run);
        var model2 = new Model(Reply(graph));
        var probe2 = new Probe();
        await StartProcess(For(graph, model2), probe2);
        await WaitCompleted(run);

        // RIPRESA PURA DA CHECKPOINT: la risposta dell'agente vive nello stato MAF persistito, quindi nessuna inferenza si ripete.
        Assert.Equal(1, model1.Calls);
        Assert.Equal(0, model2.Calls);
        Assert.DoesNotContain(LoadStage(graph), probe2.Stages);
        var done = await RunOf(run);
        Assert.Equal((2, 2L), (done.Attempts, done.Fence));
        Assert.Null(done.ErrorCode);
        Assert.Equal(1, await ResultCount(s, run));
        Assert.Equal(done.Result, await ProjectPointer(s));
        Assert.Equal(checkpointsBefore.Select(c => c.Key), (await Checkpoints(run)).Take(checkpointsBefore.Count).Select(c => c.Key));
    }

    // ---------------------------------------------------------------- crash dopo il commit del risultato

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task A_crash_after_the_result_commit_resumes_nothing_and_a_repeated_commit_reconciles_the_same_result(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var s = await Prepare(graph);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe1 = new Probe
        {
            AfterCommit = async ct =>
            {
                committed.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // il processo muore prima di notificare l'esito o di rientrare nel dispatcher
            },
        };
        var model1 = new Model(Reply(graph));
        var first = await StartProcess(For(graph, model1), probe1);
        var run = await Trigger(first, s);
        await committed.Task.WaitAsync(Patience, Ct);
        var published = await RunOf(run);
        Assert.Equal("Completed", published.Status);
        Assert.NotNull(published.Result);
        Assert.Equal(("Committed", (string?)null), Assert.Single(probe1.Commits));

        await Kill(first);

        var model2 = new Model(Reply(graph));
        var probe2 = new Probe();
        var second = await StartProcess(For(graph, model2), probe2);
        Assert.False(await Dispatcher(second).RunOnceAsync(Ct)); // nessun lavoro da riprendere
        var done = await RunOf(run);
        Assert.Equal(("Completed", 1, published.Result), (done.Status, done.Attempts, done.Result));
        Assert.Equal(0, model2.Calls);
        Assert.Empty(probe2.Stages);
        Assert.Equal(1, await ResultCount(s, run));
        Assert.Equal(published.Result, await ProjectPointer(s));

        // Il nuovo processo riconcilia dal solo database: un commit ripetuto con il claim del morto e' idempotente.
        var replay = await Replay(second, graph, probe1);
        Assert.Equal(("AlreadyCompleted", (string?)null), replay);
        Assert.Equal(1, await ResultCount(s, run));
        Assert.Equal(published.Result, await ProjectPointer(s));
    }

    // ---------------------------------------------------------------- errore del checkpoint finale

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task A_post_commit_checkpoint_failure_does_not_degrade_the_Completed_result(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var s = await Prepare(graph);
        long atCommit = -1;
        var runKnown = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe
        {
            // Subito dopo il commit il lease e' gia' rilasciato: il checkpoint di fine superstep verra' rifiutato dal fencing.
            AfterCommit = async _ => atCommit = await CheckpointCount(await runKnown.Task),
        };
        var model = new Model(Reply(graph));
        var process = await StartProcess(For(graph, model), probe);
        var run = await Trigger(process, s);
        runKnown.SetResult(run);
        await WaitCompleted(run);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // il dispatcher conclude il ciclo: un degrado sarebbe visibile qui

        var done = await RunOf(run);
        Assert.Equal(("Completed", 1, (string?)null), (done.Status, done.Attempts, done.ErrorCode));
        Assert.Null(done.Owner);
        Assert.NotNull(done.Result);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, await ResultCount(s, run));
        Assert.Equal(done.Result, await ProjectPointer(s));
        Assert.True(atCommit > 0);
        Assert.Equal(atCommit, await CheckpointCount(run)); // dopo il commit nessun checkpoint puo' piu' essere scritto
        Assert.Equal(0, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "Status" IN ('Queued','Running','Failed')"""));
    }

    // ---------------------------------------------------------------- worker con lease perso

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task A_worker_whose_lease_was_reclaimed_cannot_commit_and_the_resumed_run_publishes_a_single_result(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var s = await Prepare(graph);
        var oldReachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldMayCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldProbe = new Probe
        {
            // Il vecchio worker ha gia' la risposta ma e' fermo prima del commit; non onora la cancellazione di proposito:
            // la chiamata remota puo' finire comunque dopo la perdita del lease.
            BeforeCommit = async _ =>
            {
                oldReachedCommit.TrySetResult();
                await oldMayCommit.Task;
            },
            CommitIgnoresCancellation = true,
        };
        var oldModel = new Model(Reply(graph));
        var old = await StartProcess(For(graph, oldModel), oldProbe);
        var run = await Trigger(old, s);
        await oldReachedCommit.Task.WaitAsync(Patience, Ct);
        var oldRow = await RunOf(run);

        await ExpireLease(run); // il lease del primo owner scade: un secondo processo lo riacquisisce
        var newReachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newMayCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newModel = new Model(Reply(graph));
        var newProbe = new Probe
        {
            BeforeCommit = async ct =>
            {
                newReachedCommit.TrySetResult();
                await newMayCommit.Task.WaitAsync(ct);
            },
        };
        await StartProcess(For(graph, newModel), newProbe);
        await newReachedCommit.Task.WaitAsync(Patience, Ct);

        var newRow = await RunOf(run);
        Assert.NotEqual(oldRow.Owner, newRow.Owner);
        Assert.Equal((oldRow.Fence + 1, oldRow.Attempts + 1), (newRow.Fence, newRow.Attempts));
        Assert.Equal(0, newModel.Calls); // il nuovo owner e' ripartito dal checkpoint con la risposta dell'agente

        // Il vecchio worker termina dopo il reclaim con il vecchio fence: il commit e' rifiutato e non scrive nulla.
        oldMayCommit.SetResult();
        await Eventually(() => Task.FromResult(!oldProbe.Commits.IsEmpty), "esito del commit del vecchio worker");
        Assert.Equal(("Rejected", (string?)"lease_lost"), Assert.Single(oldProbe.Commits));
        Assert.Equal(0, await ResultCount(s, run));
        var stillRunning = await RunOf(run);
        Assert.Equal(("Running", newRow.Owner, newRow.Fence), (stillRunning.Status, stillRunning.Owner, stillRunning.Fence));

        // Il worker corrente committa: un solo risultato, quello del fence valido.
        newMayCommit.SetResult();
        await WaitCompleted(run);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct); // il vecchio dispatcher non deve poter cambiare l'esito
        var done = await RunOf(run);
        Assert.Equal(("Completed", (string?)null), (done.Status, done.ErrorCode));
        Assert.Equal(1, await ResultCount(s, run));
        Assert.Equal(done.Result, await ProjectPointer(s));
        Assert.Equal(("Committed", (string?)null), Assert.Single(newProbe.Commits));
    }

    // ---------------------------------------------------------------- attesa del PM e fasi separate

    [Fact]
    public async Task Waiting_for_the_PM_survives_restarts_with_no_running_workflow_and_each_graph_resumes_only_its_own_checkpoints()
    {
        TestEnvironment.RequirePostgres();
        var s = await Prepare(Graph.Analysis);

        // Processo 1: l'analisi si completa; il processo muore subito dopo.
        var analysisModel1 = new Model(Reply(Graph.Analysis));
        var first = await StartProcess(For(Graph.Analysis, analysisModel1), new Probe());
        var analysis = await Trigger(first, s);
        await WaitCompleted(analysis);
        await Kill(first);
        var finished = await RunOf(analysis);
        var analysisCheckpoints = await Checkpoints(analysis);

        // Processo 2: l'attesa del PM e' solo la revisione Unvalidated persistita; nessun workflow Running, nessun lavoro in coda.
        var analysisModel2 = new Model(Reply(Graph.Analysis));
        var documentHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var documentModel2 = HoldingModel(Graph.Document, documentHold);
        var probe2 = new Probe();
        var second = await StartProcess(new Models(analysisModel2, documentModel2), probe2);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // un secondo dispatcher che sceglie da solo avrebbe agito entro il ciclo
        Assert.Equal(0, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "Status" IN ('Queued','Running')"""));
        Assert.Equal((0, 0), (analysisModel2.Calls, documentModel2.Calls));
        Assert.Empty(probe2.Stages);
        Assert.Equal(1, await Count("""SELECT count(*) FROM "ProcessingRuns" """));
        using var pm = await second.LoginAsync();
        var view = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Project}", Ct);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("activeProcessingRunId").ValueKind);
        Assert.Equal(analysis, view.GetProperty("currentAnalysisId").GetGuid());
        Assert.Equal(finished.Result, view.GetProperty("currentRevisionId").GetGuid());
        Assert.Equal("Unvalidated", view.GetProperty("validationState").GetString());
        var preview = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Project}/analyses/{analysis}", Ct);
        Assert.True(preview.GetProperty("canConfirm").GetBoolean());
        Assert.False(preview.GetProperty("canGenerate").GetBoolean());

        // La conferma del PM (giorni dopo, in un altro processo) non crea alcun run e non invoca il modello.
        var revision = finished.Result!.Value;
        using (var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{s.Project}/analyses/{analysis}/confirm"))
        {
            confirm.Headers.Add("If-Match", $"\"{revision}\"");
            Assert.Equal(HttpStatusCode.OK, (await pm.SendAsync(confirm, Ct)).StatusCode);
        }

        Assert.Equal(1, await Count("""SELECT count(*) FROM "ProcessingRuns" """));
        Assert.Equal((0, 0), (analysisModel2.Calls, documentModel2.Calls));

        // Il comando Genera avvia una NUOVA fase MAF; il processo muore durante l'inferenza del documento.
        var generate = await Generate(second, s.Project, analysis, revision);
        await documentHold.Task.WaitAsync(Patience, Ct);
        var documentRunning = await RunOf(generate);
        Assert.Equal(("Running", 1, 1L), (documentRunning.Status, documentRunning.Attempts, documentRunning.Fence));
        var documentCheckpointsBefore = await Checkpoints(generate);
        Assert.NotEmpty(documentCheckpointsBefore);
        await Kill(second);
        await ExpireLease(generate);

        // Processo 3: riprende dal checkpoint del solo run Document; la bozza e' una sola e l'analisi non e' toccata.
        var documentModel3 = new Model(Reply(Graph.Document));
        var analysisModel3 = new Model(Reply(Graph.Analysis));
        var probe3 = new Probe();
        await StartProcess(new Models(analysisModel3, documentModel3), probe3);
        await WaitCompleted(generate);

        var draft = await RunOf(generate);
        Assert.Equal(("Completed", 2), (draft.Status, draft.Attempts));
        Assert.Equal(1, documentModel3.Calls);   // inferenza interrotta ripetuta una volta
        Assert.Equal(0, analysisModel3.Calls);   // il grafo dell'analisi non riparte
        Assert.DoesNotContain(DocumentWorkflow.LoadId, probe3.Stages);
        Assert.Equal(1, await Count("""SELECT count(*) FROM "Drafts" """));
        Assert.Equal(draft.Result, await ProjectPointer(new Scenario(Graph.Document, s.Project, analysis, revision)));
        Assert.Equal(1, await Count($"""SELECT count(*) FROM "AnalysisRevisions" WHERE "AnalysisId" = '{analysis}' """));
        Assert.Equal(analysisCheckpoints.Select(c => c.Key), (await Checkpoints(analysis)).Select(c => c.Key)); // checkpoint dell'analisi intatti
        Assert.NotEqual(finished.Session, draft.Session); // sessioni e checkpoint distinti fra i due grafi
        Assert.Equal(0, await Count("""SELECT count(*) FROM "ProcessingRuns" WHERE "Status" IN ('Queued','Running')"""));
    }

    // ---------------------------------------------------------------- topologia e identita' nel checkpoint

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task The_checkpoint_of_the_dead_process_carries_the_contractual_topology_and_identity_and_the_new_process_restores_it(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var (s, run, _) = await CrashedDuringInference(graph);
        var checkpoints = await Checkpoints(run);
        var stages = graph == Graph.Analysis ? AnalysisWorkflow.Stages : DocumentWorkflow.Stages;
        var agentId = stages[1]; // «{Name}_{Id}» assegnato da MAF: id e nome dell'agente stabili per il resume

        // Il checkpoint persistito descrive il grafo contrattuale: tre executor in catena, avvio e output stabili, agente come host nativo.
        var workflow = JsonDocument.Parse(checkpoints[0].Payload).RootElement.GetProperty("workflow");
        Assert.Equal(stages.Order(), workflow.GetProperty("executors").EnumerateObject().Select(e => e.Name).Order());
        Assert.Equal(stages[0], workflow.GetProperty("startExecutorId").GetString());
        Assert.Equal([stages[2]], workflow.GetProperty("outputExecutorIds").EnumerateObject().Select(e => e.Name));
        Assert.Equal("Microsoft.Agents.AI.Workflows.Specialized.AIAgentHostExecutor",
            workflow.GetProperty("executors").GetProperty(agentId).GetProperty("executorType").GetProperty("typeName").GetString());
        var edges = workflow.GetProperty("edges").EnumerateObject()
            .SelectMany(e => e.Value.EnumerateArray().Select(edge => (From: e.Name, To: edge.GetProperty("connection").GetProperty("sinkIds")[0].GetString()!)))
            .OrderBy(e => e.From, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { (stages[0], stages[1]), (stages[1], stages[2]) }.OrderBy(e => e.Item1, StringComparer.Ordinal).ToList(),
            edges);
        Assert.Single(checkpoints.Select(c => c.Session).Distinct()); // una sola sessione MAF per il run

        // Il nuovo processo ricostruisce la stessa topologia e ripristina: il run si completa, con le stesse fasi e la stessa sessione.
        var model = new Model(Reply(graph));
        var probe = new Probe();
        await StartProcess(For(graph, model), probe);
        await WaitCompleted(run);
        var done = await RunOf(run);
        Assert.Equal(checkpoints[0].Session, done.Session);
        Assert.Equal(stages[2], done.Stage);
        Assert.DoesNotContain(stages[0], probe.Stages);
        Assert.Equal(1, await ResultCount(s, run));
        var after = await Checkpoints(run);
        Assert.Equal(checkpoints.Select(c => c.Payload), after.Take(checkpoints.Count).Select(c => c.Payload)); // lo store e' solo accodato
        Assert.All(after, c => Assert.Equal(done.Session, c.Session));
    }

    // ---------------------------------------------------------------- incompatibilita' e deadline esplicite

    [Theory]
    [InlineData(Graph.Analysis, "definition")]
    [InlineData(Graph.Analysis, "maf_version")]
    [InlineData(Graph.Document, "definition")]
    [InlineData(Graph.Document, "maf_version")]
    public async Task A_run_whose_definition_or_MAF_version_no_longer_matches_fails_explicitly_without_inference_or_a_result(Graph graph, string defect)
    {
        TestEnvironment.RequirePostgres();
        var (s, run, _) = await CrashedDuringInference(graph);
        var checkpoints = await Checkpoints(run);
        await Exec(defect == "definition"
            ? """UPDATE "ProcessingRuns" SET "WorkflowDefinitionVersion" = 'project-x-v0' WHERE "Id" = @r"""
            : """UPDATE "ProcessingRuns" SET "MafVersion" = '0.0.1' WHERE "Id" = @r""", ("r", run));

        var model = new Model(Reply(graph));
        var probe = new Probe();
        await StartProcess(For(graph, model), probe);
        await Eventually(async () => (await RunOf(run)).Status == "Failed", "run Failed");

        // Nessuna migrazione implicita: errore esplicito e permanente, nessun checkpoint usato, nessuna inferenza, nessun risultato.
        var failed = await RunOf(run);
        Assert.Equal(("Failed", "workflow_incompatible", (string?)null), (failed.Status, failed.ErrorCode, failed.Owner));
        Assert.Equal(2, failed.Attempts);
        Assert.Equal(0, model.Calls);
        Assert.Empty(probe.Stages);
        Assert.Equal(0, await ResultCount(s, run));
        Assert.Null(await ProjectPointer(s));
        Assert.Equal(checkpoints.Select(c => c.Key), (await Checkpoints(run)).Select(c => c.Key));
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // non e' un errore transitorio: nessun retry
        var later = await RunOf(run);
        Assert.Equal(("Failed", 2), (later.Status, later.Attempts));
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task A_run_past_its_deadline_fails_as_deadline_exceeded_instead_of_resuming_from_its_checkpoint(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var (s, run, _) = await CrashedDuringInference(graph);
        var checkpoints = await Checkpoints(run);
        await Exec("""UPDATE "ProcessingRuns" SET "DeadlineAt" = clock_timestamp() - interval '1 second' WHERE "Id" = @r""", ("r", run));

        var model = new Model(Reply(graph));
        var probe = new Probe();
        await StartProcess(For(graph, model), probe);
        await Eventually(async () => (await RunOf(run)).Status == "Failed", "run Failed");

        var failed = await RunOf(run);
        Assert.Equal(("Failed", "deadline_exceeded", (string?)null), (failed.Status, failed.ErrorCode, failed.Owner));
        Assert.Equal(1, failed.Attempts); // fallito dallo sweep, mai preso in carico
        Assert.Equal(0, model.Calls);
        Assert.Empty(probe.Stages);
        Assert.Equal(0, await ResultCount(s, run));
        Assert.Null(await ProjectPointer(s));
        Assert.Equal(checkpoints.Select(c => c.Key), (await Checkpoints(run)).Select(c => c.Key));
    }

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task A_checkpoint_that_no_longer_matches_the_graph_is_never_restored_silently(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var (s, run, _) = await CrashedDuringInference(graph);
        var stages = graph == Graph.Analysis ? AnalysisWorkflow.Stages : DocumentWorkflow.Stages;
        // Identita' dell'agente cambiata rispetto a quella con cui il checkpoint e' stato scritto.
        await Exec(
            """UPDATE "WorkflowCheckpoints" SET "Payload" = replace("Payload"::text, @from, @to)::jsonb WHERE "ProcessingRunId" = @r""",
            ("from", stages[1]), ("to", stages[1] + "_altro"), ("r", run));

        var model = new Model(Reply(graph));
        var probe = new Probe();
        await StartProcess(For(graph, model), probe);
        await Eventually(async () => (await RunOf(run)).Status == "Failed", "run Failed");

        // Il ripristino e' rifiutato dal runtime nativo e il run fallisce in modo esplicito: mai Completed, mai un riavvio silenzioso
        // da zero (nessuna inferenza, nessuna fase rieseguita) e mai un risultato.
        var row = await RunOf(run);
        Assert.Equal(("Failed", (string?)null), (row.Status, row.Owner));
        Assert.NotNull(row.ErrorCode);
        Assert.Equal(0, model.Calls);
        Assert.Empty(probe.Stages);
        Assert.Equal(0, await ResultCount(s, run));
        Assert.Null(await ProjectPointer(s));
    }


    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task The_frozen_configuration_survives_the_restart_and_a_prompt_version_that_no_longer_matches_is_refused_explicitly(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var (s, run, _) = await CrashedDuringInference(graph);
        var frozen = await Frozen(run);
        Assert.NotNull(frozen.Config);   // congelati al comando, mai segreti
        Assert.NotNull(frozen.Prompts);
        Assert.DoesNotContain("test-key", frozen.Config);

        // Compatibile: configurazione e versioni dei prompt invariate, il run riprende e non le modifica.
        var key = graph == Graph.Analysis ? "analysis" : "document";
        Assert.Contains($"\"{key}\"", frozen.Prompts);

        // Incompatibile: il run e' stato congelato con un'altra versione del prompt di questa definizione.
        await Exec(
            """UPDATE "ProcessingRuns" SET "PromptVersions" = jsonb_build_object(@k, 'versione-obsoleta') WHERE "Id" = @r""", ("k", key), ("r", run));
        var model = new Model(Reply(graph));
        var probe = new Probe();
        await StartProcess(For(graph, model), probe);
        await Eventually(async () => (await RunOf(run)).Status is "Failed" or "Completed", "esito del run");

        var row = await RunOf(run);
        Assert.Equal(("Failed", "workflow_incompatible"), (row.Status, row.ErrorCode));
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, await ResultCount(s, run));
        Assert.Null(await ProjectPointer(s));
    }

    [Theory]
    [InlineData(Graph.Analysis)]
    [InlineData(Graph.Document)]
    public async Task Recovery_never_rewrites_the_frozen_configuration_or_the_prompt_versions(Graph graph)
    {
        TestEnvironment.RequirePostgres();
        var (s, run, _) = await CrashedDuringInference(graph);
        var frozen = await Frozen(run);

        var model = new Model(Reply(graph));
        await StartProcess(For(graph, model), new Probe());
        await WaitCompleted(run);

        Assert.Equal(frozen, await Frozen(run));
        Assert.Equal(1, await ResultCount(s, run));
    }
    // ================================================================ supporto

    /// <summary>Il run e' stato avviato da un processo morto durante l'inferenza: lease scaduto, checkpoint di caricamento su PostgreSQL.</summary>
    private async Task<(Scenario S, Guid Run, Model Dead)> CrashedDuringInference(Graph graph)
    {
        var s = await Prepare(graph);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dying = HoldingModel(graph, entered);
        var first = await StartProcess(For(graph, dying), new Probe());
        var run = await Trigger(first, s);
        await entered.Task.WaitAsync(Patience, Ct);
        Assert.NotEmpty(await Checkpoints(run));
        await Kill(first);
        await ExpireLease(run);
        return (s, run, dying);
    }

    private sealed record Scenario(Graph Graph, Guid Project, Guid? Analysis, Guid? Revision);

    private sealed record Models(Model Analysis, Model Document);

    private sealed class Probe
    {
        public Func<CancellationToken, Task>? BeforeCommit { get; init; }

        public Func<CancellationToken, Task>? AfterCommit { get; init; }

        public bool CommitIgnoresCancellation { get; init; }

        public ConcurrentQueue<string> Stages { get; } = new();

        public ConcurrentQueue<(string Outcome, string? Reason)> Commits { get; } = new();

        public object? Claim { get; set; }

        public object? Payload { get; set; }
    }

    private sealed class HookedAnalysisStore(AnalysisRunCompletion inner, Probe probe) : IAnalysisRunStore
    {
        public Task<AnalysisRunInput> LoadInputAsync(Guid runId, CancellationToken ct) => inner.LoadInputAsync(runId, ct);

        public async Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content, CancellationToken ct)
        {
            (probe.Claim, probe.Payload) = (claim, content);
            if (probe.BeforeCommit is not null)
            {
                await probe.BeforeCommit(ct);
            }

            var result = await inner.CommitAsync(claim, content, probe.CommitIgnoresCancellation ? CancellationToken.None : ct);
            probe.Commits.Enqueue((result.Outcome.ToString(), result.Reason));
            if (probe.AfterCommit is not null)
            {
                await probe.AfterCommit(ct);
            }

            return result;
        }

        public Task<bool> FailAsync(AnalysisRunClaim claim, string errorCode, string errorMessage, CancellationToken ct) => inner.FailAsync(claim, errorCode, errorMessage, ct);

        public Task<bool> RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct)
        {
            probe.Stages.Enqueue(stage);
            return inner.RecordStageAsync(claim, stage, ct);
        }
    }

    private sealed class HookedDocumentStore(DocumentRunCompletion inner, Probe probe) : IDocumentRunStore
    {
        public Task<DocumentRunInput> LoadInputAsync(Guid runId, CancellationToken ct) => inner.LoadInputAsync(runId, ct);

        public async Task<DocumentCommitResult> CommitAsync(DocumentRunClaim claim, string markdown, CancellationToken ct)
        {
            (probe.Claim, probe.Payload) = (claim, markdown);
            if (probe.BeforeCommit is not null)
            {
                await probe.BeforeCommit(ct);
            }

            var result = await inner.CommitAsync(claim, markdown, probe.CommitIgnoresCancellation ? CancellationToken.None : ct);
            probe.Commits.Enqueue((result.Outcome.ToString(), result.Reason));
            if (probe.AfterCommit is not null)
            {
                await probe.AfterCommit(ct);
            }

            return result;
        }

        public Task<bool> FailAsync(DocumentRunClaim claim, string errorCode, string errorMessage, CancellationToken ct) => inner.FailAsync(claim, errorCode, errorMessage, ct);

        public Task<bool> RecordStageAsync(DocumentRunClaim claim, string stage, CancellationToken ct)
        {
            probe.Stages.Enqueue(stage);
            return inner.RecordStageAsync(claim, stage, ct);
        }
    }

    private static string LoadStage(Graph graph) => graph == Graph.Analysis ? AnalysisWorkflow.LoadDialogId : DocumentWorkflow.LoadId;

    private static Models For(Graph graph, Model model) =>
        graph == Graph.Analysis ? new Models(model, new Model(Reply(Graph.Document))) : new Models(new Model(Reply(Graph.Analysis)), model);

    private static Model HoldingModel(Graph graph, TaskCompletionSource entered) => new(Reply(graph))
    {
        Before = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // inferenza in corso quando il processo muore
        },
    };


    private async Task<ApiHarness> StartProcess(Models models, Probe? probe, bool workflows = true)
    {
        var process = await ApiHarness.StartAsync(_database.ConnectionString, prepare: false, configure: services =>
        {
            services.Configure<ExecutionOptions>(o => o.RenewIntervalSeconds = 1); // il rinnovo reale e' piu' frequente solo per accorciare le prove
            if (!workflows)
            {
                return;
            }

            probe ??= new Probe();
            services.AddScoped<AnalysisRunCompletion>();
            services.RemoveAll<IAnalysisRunStore>();
            services.AddScoped<IAnalysisRunStore>(sp => new HookedAnalysisStore(sp.GetRequiredService<AnalysisRunCompletion>(), probe));
            services.AddScoped<DocumentRunCompletion>();
            services.RemoveAll<IDocumentRunStore>();
            services.AddScoped<IDocumentRunStore>(sp => new HookedDocumentStore(sp.GetRequiredService<DocumentRunCompletion>(), probe));
            services.AddSingleton(sp =>
            {
                using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = "fake", ApiKey = "non-usata" });
                return new AnalysisWorkflow(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    ConversationalAgentTests.Agent(models.Analysis),
                    client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0));
            });
            services.AddSingleton(new DocumentAgent(
                new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, models.Document, (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)),
                new ChatOptions { ModelId = "fake" }));
            services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>()));
        });
        _processes.Add(process);
        _ = process.Services; // avvia il dispatcher ospitato
        return process;
    }

    /// <summary>Il "crash": l'applicazione sparisce a meta' esecuzione senza rilasciare nulla; il database conserva lo stato.</summary>
    private async Task Kill(ApiHarness process)
    {
        _processes.Remove(process);
        await process.DisposeAsync();
    }

    private static AiDispatchService Dispatcher(ApiHarness process) =>
        process.Services.GetServices<IHostedService>().OfType<AiDispatchService>().Single();

    // ---------------------------------------------------------------- modello finto

    /// <summary>Costruisce una risposta valida dai soli dati della richiesta, come farebbe il modello (id fidati e fonti del dialogo).</summary>
    private static Func<string, string> Reply(Graph graph) => graph == Graph.Analysis ? AnalysisReply : DocumentReply;

    private static string AnalysisReply(string request)
    {
        string Match(string pattern) => Regex.Match(request, pattern).Groups[1].Value;
        var message = Regex.Match(request, @"\[messaggio id=([0-9a-f-]{36}) sequenza=(\d+)\]");
        var source = Guid.NewGuid().ToString();
        return new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["analysisId"] = Match(@"analysisId: ([0-9a-f-]{36})"),
            ["projectId"] = Match(@"projectId: ([0-9a-f-]{36})"),
            ["sourceCutoffSequence"] = long.Parse(Match(@"sourceCutoffSequence: (\d+)")),
            ["validationState"] = "Unvalidated",
            ["sources"] = new JsonArray(new JsonObject
            {
                ["id"] = source,
                ["kind"] = "Message",
                ["messageId"] = message.Groups[1].Value,
                ["messageRole"] = "user",
                ["revisionId"] = null,
            }),
            ["goals"] = new JsonArray(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["text"] = "Realizzare un portale clienti",
                ["classification"] = "Declared",
                ["evidenceRefs"] = new JsonArray(JsonValue.Create(source)),
            }),
            ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
            ["requirements"] = new JsonArray(),
            ["operationalFlows"] = new JsonArray(),
            ["constraints"] = new JsonArray(),
            ["dependencies"] = new JsonArray(),
            ["assumptions"] = new JsonArray(),
            ["openQuestions"] = new JsonArray(),
            ["conflicts"] = new JsonArray(),
        }.ToJsonString();
    }

    private static string DocumentReply(string request)
    {
        var json = DocumentContractTests.DocumentModel.EmbeddedJson(request);
        var source = json["sources"]![0]!["id"]!.GetValue<string>();
        return $"# Documento di progetto\n\n## Obiettivi\n\n- {json["goals"]![0]!["text"]!.GetValue<string>()} [fonte:{source}]\n";
    }

    /// <summary>Modello scriptabile: conta le chiamate e puo' fermarsi in inferenza (<see cref="Before"/>).</summary>
    private sealed class Model(Func<string, string> reply) : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Func<CancellationToken, Task>? Before { get; init; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            new(new ChatMessage(ChatRole.Assistant, await ReplyAsync(messages, cancellationToken)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, await ReplyAsync(messages, cancellationToken));
        }

        private async Task<string> ReplyAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Before is not null)
            {
                await Before(ct);
            }

            return reply(string.Join("\n", messages.Where(m => m.Role != ChatRole.System).Select(m => m.Text)));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- scenari e HTTP

    /// <summary>
    /// Analysis: progetto con due note PM. Document: in piu' un'analisi completata e confermata dal PM (processo di preparazione,
    /// poi smaltito): il run Document parte cosi' da una revisione confermata persistita.
    /// </summary>
    private async Task<Scenario> Prepare(Graph graph)
    {
        var setup = await StartProcess(For(Graph.Analysis, new Model(Reply(Graph.Analysis))), null, workflows: graph == Graph.Document);
        Guid project;
        using (var pm = await setup.LoginAsync())
        {
            var created = await pm.PostAsJsonAsync("/api/projects", new { name = $"Recovery {Guid.NewGuid():N}" }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            project = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            foreach (var text in new[] { "Vogliamo un portale clienti per ridurre i tempi di risposta ai ticket.", "Lo useranno gli agenti del supporto." })
            {
                var posted = await pm.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
                Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
            }
        }

        if (graph == Graph.Analysis)
        {
            await Kill(setup);
            return new Scenario(graph, project, null, null);
        }

        var analysis = await Trigger(setup, new Scenario(Graph.Analysis, project, null, null));
        await WaitCompleted(analysis);
        var revision = (await RunOf(analysis)).Result!.Value;
        using (var pm = await setup.LoginAsync())
        using (var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/confirm"))
        {
            confirm.Headers.Add("If-Match", $"\"{revision}\"");
            Assert.Equal(HttpStatusCode.OK, (await pm.SendAsync(confirm, Ct)).StatusCode);
        }

        await Kill(setup);
        return new Scenario(graph, project, analysis, revision);
    }

    private async Task<Guid> Trigger(ApiHarness process, Scenario s)
    {
        if (s.Graph == Graph.Document)
        {
            return await Generate(process, s.Project, s.Analysis!.Value, s.Revision!.Value);
        }

        using var pm = await process.LoginAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{s.Project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await pm.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> Generate(ApiHarness process, Guid project, Guid analysis, Guid revision)
    {
        using var pm = await process.LoginAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations")
        {
            Content = JsonContent.Create(new { revisionId = revision }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await pm.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>Commit ripetuto con il claim e il contenuto del processo morto, da un processo nuovo.</summary>
    private static async Task<(string Outcome, string? Reason)> Replay(ApiHarness process, Graph graph, Probe dead)
    {
        using var scope = process.Services.CreateScope();
        if (graph == Graph.Analysis)
        {
            var store = scope.ServiceProvider.GetRequiredService<AnalysisRunCompletion>();
            var result = await store.CommitAsync((AnalysisRunClaim)dead.Claim!, (AnalysisContent)dead.Payload!, Ct);
            return (result.Outcome.ToString(), result.Reason);
        }

        var documents = scope.ServiceProvider.GetRequiredService<DocumentRunCompletion>();
        var committed = await documents.CommitAsync((DocumentRunClaim)dead.Claim!, (string)dead.Payload!, Ct);
        return (committed.Outcome.ToString(), committed.Reason);
    }

    // ---------------------------------------------------------------- database

    private sealed record RunRow(
        string Status, int Attempts, long Fence, string? Owner, string? Session, string? ErrorCode, Guid? Result,
        string? Stage, DateTimeOffset DeadlineAt);

    private sealed record CheckpointRow(long Sequence, string Key, string Session, string Payload);

    private async Task<RunRow> RunOf(Guid run)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Status", "AttemptCount", "FencingToken", "LeaseOwner", "WorkflowSessionId", "ErrorCode",
                   COALESCE("ResultRevisionId", "DraftId"), "LastObservedStage", "DeadlineAt"
            FROM "ProcessingRuns" WHERE "Id" = @r
            """, connection);
        command.Parameters.AddWithValue("r", run);
        await using var r = await command.ExecuteReaderAsync(Ct);
        Assert.True(await r.ReadAsync(Ct));
        return new(
            r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7),
            r.GetFieldValue<DateTimeOffset>(8));
    }

    private async Task<List<CheckpointRow>> Checkpoints(Guid run)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(
            """
            SELECT "CommitSequence", "CheckpointKey", "WorkflowSessionId", "Payload"::text
            FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = @r ORDER BY "CommitSequence"
            """, connection);
        command.Parameters.AddWithValue("r", run);
        var rows = new List<CheckpointRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    private Task<long> CheckpointCount(Guid run) => Scalar<long>("""SELECT count(*) FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = @r""", ("r", run));

    /// <summary>Risultati del run: revisioni AI dell'analisi, oppure bozze dell'intero progetto (nessuna bozza duplicata).</summary>
    private Task<long> ResultCount(Scenario s, Guid run) => s.Graph == Graph.Analysis
        ? Scalar<long>("""SELECT count(*) FROM "AnalysisRevisions" WHERE "AnalysisId" = @r""", ("r", run))
        : Scalar<long>("""SELECT count(*) FROM "Drafts" WHERE "ProjectId" = @p""", ("p", s.Project));

    private async Task<Guid?> ProjectPointer(Scenario s)
    {
        var column = s.Graph == Graph.Analysis ? "CurrentAnalysisId" : "LatestDraftId";
        var value = await Scalar<object?>($"""SELECT "{column}" FROM "Projects" WHERE "Id" = @p""", ("p", s.Project));
        // Per l'analisi il puntatore e' l'id del run, e il risultato e' la revisione: si confronta il risultato del run puntato.
        if (s.Graph == Graph.Analysis)
        {
            return value is Guid analysis
                ? await Scalar<Guid?>("""SELECT "ResultRevisionId" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", analysis))
                : null;
        }

        return value as Guid?;
    }

    private async Task<(string? Config, string? Prompts)> Frozen(Guid run)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand("""SELECT "AgentConfig"::text, "PromptVersions"::text FROM "ProcessingRuns" WHERE "Id" = @r""", connection);
        command.Parameters.AddWithValue("r", run);
        await using var r = await command.ExecuteReaderAsync(Ct);
        Assert.True(await r.ReadAsync(Ct));
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private Task ExpireLease(Guid run) =>
        Exec("""UPDATE "ProcessingRuns" SET "LeaseUntil" = clock_timestamp() - interval '1 second' WHERE "Id" = @r""", ("r", run));

    private async Task WaitCompleted(Guid run)
    {
        await Eventually(async () =>
        {
            var row = await RunOf(run);
            Assert.NotEqual("Failed", row.Status);
            return row.Status == "Completed";
        }, "run Completed");
    }

    private static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200, Ct);
        }

        Assert.Fail($"Condizione non raggiunta entro il tempo previsto: {what}.");
    }

    private async Task<NpgsqlConnection> Open()
    {
        var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task Exec(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> Count(string sql) => await Scalar<long>(sql);

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }
}
