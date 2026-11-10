using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// Verifica reale MINIMA e ISOLATA dell'embedding (003, diagnosi del Run 2 di T040): nessun database, nessun LLM generativo, nessun documento, nessuna campagna. Opt-in come ogni test DGX
/// (<c>BPROJECTS_TEST_DGX_ENDPOINT/_API_KEY/_MODEL</c> e <c>BPROJECTS_TEST_DGX_ALLOW=1</c>); senza, Skipped. Sequenza fissa, una sola volta: (1) snapshot di readiness del prodotto (<c>GET /v1/models</c>,
/// <c>/props</c> se serve e, solo se il modello è già caricato, la richiesta funzionale minima del prodotto); (2) SOLO se lo stato è <c>ModelUnloaded</c>, UNA richiesta di embedding ordinaria di UN testo
/// («probe»), come l'harness M2 per il riordino; (3) nuovo snapshot. Il rapporto (stati, codici HTTP, durate, tipo di eccezione; nessuna chiave) è scritto in
/// <c>%TEMP%\bprojects-t040-campaign\embedding-smoke-report.json</c> anche in caso di errore. Esito PASS solo se alla fine l'embedding è <c>Ready</c>.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Trait("Campaign", "EmbeddingSmoke")]
public sealed class RetrievalEmbeddingSmokeRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_embedding_model_is_ready_or_wakes_up_with_one_ordinary_request_and_every_state_and_http_outcome_is_recorded()
    {
        TestEnvironment.RequireDgx();
        var options = new RetrievalOptions();
        RetrievalGapCampaign.ApplyRetrievalOptions(options, TestEnvironment.DgxEndpoint!, TestEnvironment.DgxApiKey!);
        var recorder = new RetrievalCallRecorder { Phase = "smoke-iniziale" };
        using var embedding = new EmbeddingClient(options, recorder.NewHandler());
        using var readiness = new RetrievalModelReadiness(options, embedding, recorder.NewHandler());
        var diagnostics = new IndexDiagnostics(() => "embedding-smoke");
        var path = Path.Combine(Path.GetTempPath(), "bprojects-t040-campaign", "embedding-smoke-report.json");
        RetrievalModelSnapshot after;
        try
        {
            var before = await readiness.GetEmbeddingSnapshotAsync(Ct);
            diagnostics.Readiness.Add(Snapshot("iniziale", options, before));
            if (before.State == RetrievalModelState.ModelUnloaded)
            {
                recorder.Phase = "smoke-richiesta-minima";
                try
                {
                    await embedding.EmbedAsync("probe", Ct);
                }
                catch (RetrievalException failure)
                {
                    diagnostics.ExceptionType = nameof(RetrievalException);
                    diagnostics.ExceptionMessage = failure.ToString();
                }

                readiness.Invalidate();
            }

            recorder.Phase = "smoke-finale";
            after = await readiness.GetEmbeddingSnapshotAsync(Ct);
            diagnostics.Readiness.Add(Snapshot("finale", options, after));
        }
        finally
        {
            diagnostics.Http = RetrievalIndexGate.HttpSummary(recorder.Calls);
            diagnostics.Persist(path);
        }

        Assert.True(after.IsReady, $"embedding non pronto al termine: {after.State} (rapporto in {path})");
    }

    private static ModelReadinessRecord Snapshot(string moment, RetrievalOptions options, RetrievalModelSnapshot s) =>
        new(moment, "embedding", RetrievalIndexGate.LogicalEndpoint(options.Embedding), s.ModelId, s.State.ToString(), s.ExpectedArtifact, s.Artifact, s.Dimension, s.Sha256Status.ToString(), s.CheckedAt);
}
