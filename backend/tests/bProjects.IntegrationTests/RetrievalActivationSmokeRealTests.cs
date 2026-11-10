using System.Diagnostics;
using System.Text.Json;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// Verifica reale ISOLATA dell'attivazione on-demand (003, commit 6549e1b): esercita il codice di PRODOTTO — <c>EnsureEmbeddingReadyAsync</c> / <c>EnsureRerankerReadyAsync</c> di <see cref="RetrievalModelReadiness"/>
/// con i client reali di embedding e riordino — e mai chiamate di attivazione scritte dal test. Nessun database, nessun LLM generativo, nessun documento, nessuna campagna, nessun unload/reset del router.
/// Per ciascun modello, in ordine: (1) lettura PASSIVA (<c>Get*SnapshotAsync</c>): se il modello è <c>ModelUnloaded</c> non deve partire alcuna richiesta al modello; (2) percorso PER L'USO (<c>Ensure*ReadyAsync</c>), che può attivarlo;
/// (3) stato finale e <c>Last*Activation</c>. Se un modello è già <c>loaded</c> il percorso cold-start NON è verificato e il rapporto lo dichiara. Opt-in come ogni test DGX; senza, Skipped. Rapporto in un file NUOVO
/// <c>%TEMP%\bprojects-t040-campaign\activation-smoke-report-&lt;UTC&gt;.json</c> (mai sovrascrive quelli precedenti), scritto anche in caso di errore; nessuna chiave né testo di contenuto.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "ActivationSmoke")]
public sealed class RetrievalActivationSmokeRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Step(
        string Role,
        string InitialState,
        bool ColdStartVerified,
        string ColdStartNote,
        int PassiveModelRequests,
        string? UseResult,
        string? UseErrorCode,
        string? UseErrorMessage,
        double UseMilliseconds,
        ModelReadinessRecord? Final,
        RetrievalActivationOutcome? LastActivation);

    [Fact]
    public async Task Ensure_paths_activate_an_unloaded_model_on_demand_and_the_passive_paths_never_do()
    {
        TestEnvironment.RequireDgx();
        var options = new RetrievalOptions();
        RetrievalGapCampaign.ApplyRetrievalOptions(options, TestEnvironment.DgxEndpoint!, TestEnvironment.DgxApiKey!);
        var recorder = new RetrievalCallRecorder { Phase = "setup" };
        using var embedding = new EmbeddingClient(options, recorder.NewHandler());
        using var reranker = new RerankClient(options, recorder.NewHandler());
        using var readiness = new RetrievalModelReadiness(options, embedding, recorder.NewHandler(), reranker: reranker);
        var steps = new List<Step>();
        var path = Path.Combine(Path.GetTempPath(), "bprojects-t040-campaign", $"activation-smoke-report-{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z.json");
        try
        {
            steps.Add(await RunAsync("embedding", options.Embedding, "embedding", recorder, readiness.GetEmbeddingSnapshotAsync, readiness.EnsureEmbeddingReadyAsync, () => readiness.Current, () => readiness.LastEmbeddingActivation));
            steps.Add(await RunAsync("reranker", options.Reranker, "rerank", recorder, readiness.GetRerankerSnapshotAsync, readiness.EnsureRerankerReadyAsync, () => readiness.CurrentReranker, () => readiness.LastRerankerActivation));
        }
        finally
        {
            Persist(path, steps, recorder);
        }

        foreach (var s in steps)
        {
            if (s.ColdStartVerified)
            {
                Assert.True(s.PassiveModelRequests == 0, $"{s.Role}: la lettura passiva ha inviato {s.PassiveModelRequests} richieste al modello (rapporto in {path})");
                Assert.NotNull(s.LastActivation);
            }

            Assert.True(s.UseResult == "Ready", $"{s.Role}: il percorso per l'uso non ha reso pronto il modello: {s.UseErrorCode} (rapporto in {path})");
        }
    }

    private static async Task<Step> RunAsync(
        string role, RetrievalModelOptions model, string kind, RetrievalCallRecorder recorder,
        Func<CancellationToken, Task<RetrievalModelSnapshot>> passive, Func<CancellationToken, Task<RetrievalModelSnapshot>> ensure,
        Func<RetrievalModelSnapshot> current, Func<RetrievalActivationOutcome?> last)
    {
        recorder.Phase = $"{role}-passiva";
        var passiveSnapshot = await passive(Ct);
        var passiveRequests = recorder.Calls.Count(c => c.Phase == recorder.Phase && c.Kind == kind);
        var unloaded = passiveSnapshot.State == RetrievalModelState.ModelUnloaded;

        recorder.Phase = $"{role}-uso";
        var clock = Stopwatch.StartNew();
        string? result = null;
        string? code = null;
        string? message = null;
        try
        {
            result = (await ensure(Ct)).State.ToString();
        }
        catch (RetrievalException e)
        {
            code = e.Code;
            message = e.Message;
        }

        clock.Stop();
        var final = current();
        return new Step(
            role,
            passiveSnapshot.State.ToString(),
            unloaded,
            unloaded
                ? "cold start verificato: il modello era ModelUnloaded all'inizio"
                : $"cold start NON verificato: il modello non era scaricato all'inizio (stato {passiveSnapshot.State}); verificato solo il percorso per l'uso su un modello già pronto",
            passiveRequests,
            result,
            code,
            message,
            Math.Round(clock.Elapsed.TotalMilliseconds, 1),
            new ModelReadinessRecord("finale", role, RetrievalIndexGate.LogicalEndpoint(model), final.ModelId, final.State.ToString(), final.ExpectedArtifact, final.Artifact, final.Dimension, final.Sha256Status.ToString(), final.CheckedAt),
            last());
    }

    private static void Persist(string path, List<Step> steps, RetrievalCallRecorder recorder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var report = new { scenario = "activation-smoke", writtenAtUtc = DateTimeOffset.UtcNow, commit = "6549e1b", steps, http = RetrievalIndexGate.HttpSummary(recorder.Calls) };
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        catch (IOException)
        {
            // diagnostica best-effort: non maschera l'esito del test
        }
    }
}
