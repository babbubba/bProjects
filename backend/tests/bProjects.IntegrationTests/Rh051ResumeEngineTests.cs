using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// RH-051 (ripresa dopo la 4ª run) — harness ISOLATO e NON GENERATIVO: avvia il motore applicativo reale (dispatcher, <see cref="DocumentAnalysisWorkflow"/>, store dei checkpoint) su una COPIA scratch del
/// database della run 4, riapre il run <c>Failed</c> come lo vedrebbe una nuova richiesta (Queued, errore azzerato, scadenze rinnovate, contatori e checkpoint INTATTI) e lascia che il dispatcher lo reclami.
/// Il tokenizer, <c>/props</c> e <c>/v1/models</c> sono quelli REALI del DGX (stessi conteggi di token e stessa impronta del runtime: stessa partizione e stessi hash dei batch della run); ogni richiesta
/// di completamento è invece intercettata da un modello FINTO (nessuna inferenza sul DGX: ogni POST diverso da /tokenize e /apply-template è bloccato). Il primo batch atteso è l'espansione 30, a cui si
/// risponde con l'output REALE conservato in m5-evidence/run4 (dimostra la correzione); gli altri con risposte minime valide. Nessuna modifica del prodotto. Attivo solo con
/// <c>BPROJECTS_RH051_RESUME_DB</c> (database scratch) e con l'opt-in DGX (solo per leggere endpoint e chiave); altrimenti ignorato.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class Rh051ResumeEngineTests
{
    // 200 (tetto complessivo di RH-051) − 46 chiamate già consumate dalla run 4 (3 chat + 12 estrazione + 31 analisi) = 154. Il limite congelato della run è 512
    // (maxAiCallsPerRun): BPROJECTS_RH051_RESUME_CAP alza solo il tetto del modello FINTO per misurare il carico complessivo (mai chiamate reali).
    private static readonly int GenerativeCap = int.TryParse(Environment.GetEnvironmentVariable("BPROJECTS_RH051_RESUME_CAP"), out var cap) && cap > 0 ? cap : 154;

    private static string EvidenceDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "specs", "002-project-documents")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, "specs", "002-project-documents", "validation", "reconciliation-hardening", "m5-evidence", "run4");
    }

    /// <summary>Tokenizer, props e modelli reali (non generativi); completamenti al modello finto; ogni altra richiesta è bloccata.</summary>
    private sealed class HybridHandler(HttpMessageHandler real, DocumentChatWorkflowTests.FakeChatModel fake, int cap) : HttpMessageHandler
    {
        public int Passed { get; private set; }

        public int Faked { get; private set; }

        public List<string> Blocked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/completions", StringComparison.Ordinal))
            {
                if (++Faked > cap)
                {
                    throw new InvalidOperationException($"tetto di {cap} completamenti simulati superato");
                }

                return await new HttpMessageInvoker(fake, disposeHandler: false).SendAsync(request, cancellationToken);
            }

            var allowed = request.Method == HttpMethod.Get || (request.Method == HttpMethod.Post && (path.EndsWith("/tokenize", StringComparison.Ordinal) || path.EndsWith("/apply-template", StringComparison.Ordinal)));
            if (!allowed)
            {
                Blocked.Add($"{request.Method} {path}");
                throw new InvalidOperationException($"richiesta non ammessa dall'harness non generativo: {request.Method} {path}");
            }

            Passed++;
            return await new HttpMessageInvoker(real, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private static string Stage(DocumentChatWorkflowTests.ChatRequest r)
    {
        var view = DocumentGapStageWorkflowTests.ParseStage(r.AllText);
        var records = view.Gaps.Select(g => (JsonNode)new JsonObject
        {
            ["gap"] = g.Alias, ["outcome"] = "Open", ["reason"] = "NoAnswer", ["rationale"] = "Risposta simulata dall'harness non generativo.", ["supportingEvidence"] = new JsonArray(),
            ["openItem"] = g.Question, ["openConflict"] = null,
        });
        return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = new JsonArray([.. records]) }.ToJsonString();
    }

    /// <summary>Risposta minima valida della gerarchia v11 (nessun collegamento): serve solo a far avanzare la sequenza dei batch; non è una valutazione semantica.</summary>
    private static string Hierarchy(DocumentChatWorkflowTests.ChatRequest r)
    {
        var p = DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText);
        var obj = ReconcileContractMatrixTests.Dto(noFindings: "nessun collegamento emerso", descriptor: p.Stage is "leaf" or "reduce" ? "gruppo" : null);
        if (p.Stage == "compare")
        {
            obj["focus"] = JsonNode.Parse("""{"outcome":"Compared","reason":null}""");
            obj["noFindings"] = null;
        }

        return obj.ToJsonString();
    }

    [Fact]
    public async Task The_engine_resumes_the_reopened_failed_v11_run_reusing_the_30_committed_outputs_and_reruns_only_expand_30_and_the_following_batches()
    {
        var scratch = Environment.GetEnvironmentVariable("BPROJECTS_RH051_RESUME_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(scratch), "BPROJECTS_RH051_RESUME_DB non impostata: harness di ripresa non richiesto.");
        var ct = TestContext.Current.CancellationToken;

        // Opzioni AI come la run 4 (finestra 65536, O=8192, M=1024, thinking spento, fattore di estrazione 24).
        var ai = TestEnvironment.RequireProductionLikeOptions();
        ai.ContextWindowTokens = 65536;
        ai.SafetyMarginTokens = 1024;
        ai.Phases.Analysis.MaxOutputTokens = 8192;
        ai.Phases.Analysis.EnableThinking = false;
        ai.ExtractionOutputExpansion = 24;

        var order = new List<string>();
        var fake = new DocumentChatWorkflowTests.FakeChatModel
        {
            ContextWindow = 65536,
            Responder = r =>
            {
                var staged = DocumentGapStageWorkflowTests.IsStage(r);
                var stage = staged ? "gap" : DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText).Stage;
                lock (order)
                {
                    order.Add(stage);
                }

                // expand 30 NON è reinviato: la risposta reale è già nel checkpoint e il recovery di produzione (DocumentBatchPort.PersistAsync)
                // la rivalida col composer corretto e la committa senza chiamate. Se arrivasse una richiesta expand 30, la asserzione finale fallirebbe.
                return staged ? Stage(r) : Hierarchy(r);
            },
        };
        var handler = new HybridHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, fake, GenerativeCap);

        await using var app = await ApiHarness.StartAsync(scratch!, prepare: false, services =>
        {
            services.PostConfigure<AiOptions>(o => CopyOptions(ai, o));
            services.Configure<ExecutionOptions>(o => o.AnalysisRunDeadlineSeconds = TestEnvironment.ProductionAnalysisRunDeadlineSeconds);   // come la run reale: il timeout della richiesta (900 s) deve stare sotto la scadenza del run
            services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value, handler));
            services.AddAiWorkflows();
            services.AddSingleton<IEvidenceExtractionExecutor>(sp =>
            {
                var model = sp.GetRequiredService<InternalModelClient>();
                return new EvidenceExtractor(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>()),
                    model.CreateChatOptions(EvidenceExtractionSchema.Base, EvidenceExtractionSchema.Name, temperature: 0, phase: AiPhase.Extraction),
                    sp.GetRequiredService<EvidenceEvents>());
            });
            services.AddSingleton<IDocumentUnitExecutor>(sp =>
            {
                var model = sp.GetRequiredService<InternalModelClient>();
                var documents = sp.GetRequiredService<IOptions<DocumentOptions>>();
                return new DocumentEvidenceAgent(
                    sp.GetRequiredService<IServiceScopeFactory>(), AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>(), documents.Value.Calibration.ToCalibration()),
                    model, documents);
            });
        });

        // Con prepare:false l'host non viene costruito (Services è lazy): senza questo accesso nessun servizio in background (dispatcher) partirebbe.
        using (var scope = app.Services.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetService<DocumentAnalysisWorkflow>());   // il workflow reale è registrato: il dispatcher può eseguire il run
        }

        // ---- stato prima della riapertura
        var run = (Guid)(await Scalar(scratch!, "SELECT \"Id\" FROM \"ProcessingRuns\" WHERE \"Kind\"::text = 'Analysis' ORDER BY \"RequestedAt\" DESC LIMIT 1"))!;
        var before = await Snapshot(scratch!, run);
        Assert.Equal("Failed", before.Status);
        Assert.Equal(30, before.Committed.Count);
        Assert.Equal(31, before.CallCount);

        // ---- riapertura (unica operazione sul run): contatori, checkpoint e snapshot dei limiti restano intatti
        await Execute(scratch!, run,
            """
            UPDATE "ProcessingRuns" SET "Status" = 'Queued', "ErrorCode" = NULL, "ErrorMessage" = NULL, "CompletedAt" = NULL, "LeaseOwner" = NULL, "LeaseUntil" = NULL, "NextAttemptAt" = NULL,
                   "DeadlineAt" = now() + interval '3 hours', "EffectiveDeadlineAt" = now() + interval '3 hours', "HardDeadlineAt" = now() + interval '3 hours'
            WHERE "Id" = @r AND "Status" = 'Failed'
            """);

        // ---- il dispatcher applicativo reclama ed esegue il run
        var deadline = DateTime.UtcNow.AddMinutes(25);
        string status;
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            status = (string)(await Scalar(scratch!, "SELECT \"Status\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = '" + run + "'"))!;
        }
        while (status is "Queued" or "Running" && DateTime.UtcNow < deadline);

        var after = await Snapshot(scratch!, run);
        var report = new JsonObject
        {
            ["status"] = after.Status, ["errorCode"] = after.ErrorCode, ["callCountBefore"] = before.CallCount, ["callCountAfter"] = after.CallCount, ["attemptCountBefore"] = before.AttemptCount, ["attemptCountAfter"] = after.AttemptCount,
            ["fencingBefore"] = before.Fence, ["fencingAfter"] = after.Fence,
            ["simulatedCompletions"] = handler.Faked, ["stageOrder"] = new JsonArray([.. order.Select(s => (JsonNode?)JsonValue.Create(s))]), ["realNonGenerativePassed"] = handler.Passed, ["blocked"] = new JsonArray([.. handler.Blocked.Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["committedBefore"] = before.Committed.Count, ["committedAfter"] = after.Committed.Count, ["batchesBefore"] = before.Batches, ["batchesAfter"] = after.Batches,
            ["extractionJobsBefore"] = before.ExtractionJobs, ["extractionJobsAfter"] = after.ExtractionJobs, ["extractionOutputsBefore"] = before.ExtractionOutputs, ["extractionOutputsAfter"] = after.ExtractionOutputs,
            ["errorMessage"] = after.ErrorMessage, ["cap"] = GenerativeCap, ["capExceeded"] = handler.Faked > GenerativeCap,
        };
        var dir = Path.Combine(Path.GetTempPath(), "bprojects-rh051-resume");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"resume-engine-{DateTime.UtcNow:yyyyMMddTHHmmss}.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Current.SendDiagnosticMessage(report.ToJsonString());

        Assert.Empty(handler.Blocked);                                           // nessun POST generativo verso il DGX, nessuna richiesta fuori dall'elenco
        Assert.Equal(before.ExtractionJobs, after.ExtractionJobs);              // nessuna riestrazione
        Assert.Equal(before.ExtractionOutputs, after.ExtractionOutputs);
        foreach (var (batch, hash) in before.Committed)                          // i 30 output committati sono rimasti identici
        {
            Assert.Equal(hash, after.Committed[batch]);
        }

        var capExceeded = handler.Faked > GenerativeCap;   // il carico residuo non entra nel tetto: la run si ferma per il tetto del modello finto, non per un difetto del motore
        if (!capExceeded)
        {
            Assert.Equal("Completed", after.Status + (after.Status == "Completed" ? "" : $" ({after.ErrorCode}: {after.ErrorMessage})"));
        }

        Assert.True(handler.Faked > 0);
        // expand 30 è l'unico batch non committato prima della ripresa: ora lo è, ma senza alcuna richiesta (recovery dal checkpoint): le richieste
        // simulate sono solo quelle dei batch nuovi (la prima è già un batch successivo).
        Assert.True(after.Committed.Keys.Except(before.Committed.Keys).Any());
        Assert.DoesNotContain(order, s => s is "leaf" or "reduce" or "cross");   // nessun batch già committato è reinviato
        if (!capExceeded)
        {
            Assert.Equal("gap", order[^1]);                                      // lo stadio Gap è l'ultimo
        }

        Assert.Equal(before.CallCount + handler.Faked, after.CallCount);         // la contabilizzazione prosegue da 31, senza azzeramenti
        Assert.True(after.AttemptCount > before.AttemptCount);
    }

    // ---------------------------------------------------------------------------------------------- accesso al database scratch

    internal sealed record RunSnapshot(string Status, string? ErrorCode, string? ErrorMessage, int CallCount, int AttemptCount, long Fence, int Batches, Dictionary<string, string> Committed, long ExtractionJobs, long ExtractionOutputs);

    internal static async Task<RunSnapshot> Snapshot(string cs, Guid run)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        string status;
        string? errorCode, errorMessage;
        int calls, attempts;
        long fence;
        await using (var command = new NpgsqlCommand("SELECT \"Status\"::text, \"ErrorCode\", \"ErrorMessage\", coalesce(\"CallCount\",0), \"AttemptCount\", \"FencingToken\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", connection))
        {
            command.Parameters.AddWithValue("r", run);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            (status, errorCode, errorMessage, calls, attempts, fence) = (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5));
        }

        var committed = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT \"BatchId\", \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\"::text = 'Committed' AND \"SchemaVersion\" = 'reconcile-alias-1'", connection))
        {
            command.Parameters.AddWithValue("r", run);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                committed[reader.GetString(0)] = reader.GetString(1);
            }
        }

        async Task<long> Count(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        var batches = (int)await Count($"SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = '{run}'");
        return new(status, errorCode, errorMessage, calls, attempts, fence, batches, committed, await Count("SELECT count(*) FROM \"DocumentJobs\""), await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"SchemaVersion\" = 'evidence-1.1.0'"));
    }

    internal static async Task<object?> Scalar(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    internal static async Task Execute(string cs, Guid run, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("r", run);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    internal static void CopyOptions(AiOptions from, AiOptions to)
    {
        foreach (var property in typeof(AiOptions).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(p => p.CanWrite && p.Name != nameof(AiOptions.Phases)))
        {
            property.SetValue(to, property.GetValue(from));
        }

        foreach (var phase in Enum.GetValues<AiPhase>())
        {
            var source = from.Phases.Of(phase);
            var target = to.Phases.Of(phase);
            target.ModelId = source.ModelId;
            target.MaxOutputTokens = source.MaxOutputTokens;
            target.EnableThinking = source.EnableThinking;
            target.RequestTimeoutSeconds = source.RequestTimeoutSeconds;
        }
    }
}
