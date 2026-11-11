using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T036 (MAF-PG) — percorso completo OFFLINE: documenti con blocchi e evidenze → indice (embedding finto) → run Analizza v7 dal vero POST → gerarchia → batch deterministico `retrieval`
/// (ricerca ibrida PostgreSQL + riordino finto) → UNA chiamata dello stadio Gap sul pacchetto recuperato → assemble e commit. PostgreSQL e MAF 1.26.0 reali; embedding, riordino e LLM FINTI
/// (il modello finto legge SOLO il messaggio inviato). Dimostrano il protocollo e l'integrazione, non la qualità dei modelli reali.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RetrievalGapFlowTests : ReconcileRig, IDisposable
{
    private const string GapPresent = "Serve una fideiussione bancaria per la fornitura?";
    private const string GapPartial = "Quali sono i termini e le penali del collaudo finale?";
    private const string GapConflict = "In quanti giorni viene pagata la fattura del cliente?";
    private const string GapExcluded = "Quale garanzia vale sui ricambi della sede di Torino?";

    private const string FactPresent = "La fideiussione bancaria è richiesta per importi sopra cinquantamila euro.";
    private const string FactPartial = "Il collaudo finale prevede un termine massimo di quindici giorni lavorativi.";
    private const string FactSideA = "Il contratto prevede il pagamento a trenta giorni dalla fattura.";
    private const string FactSideB = "Il cliente ha dichiarato il pagamento a quarantacinque giorni dalla fattura.";
    private const string FactExcluded = "La garanzia sui ricambi della sede di Torino dura ventiquattro mesi.";

    private readonly FakeEmbeddingServer _embedding = new();
    private readonly FakeRerankServer _rerank = new() { Scores = Overlap };
    private readonly RetrievalRig _rig;

    public RetrievalGapFlowTests()
    {
        _rig = new RetrievalRig(_embedding, _rerank);
    }

    public void Dispose() => _rig.Dispose();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.Configure<RetrievalOptions>(o =>
        {
            o.Enabled = true;
            o.AllowedHosts = ["dgx.internal"];
            o.PolicyVersion = RetrievalPolicy.Version1;
            o.Embedding.Endpoint = _rig.Options.Embedding.Endpoint;
            o.Embedding.ApiKey = _rig.Options.Embedding.ApiKey;
            o.Reranker.Endpoint = _rig.Options.Reranker.Endpoint;
            o.Reranker.ApiKey = _rig.Options.Reranker.ApiKey;
        });
        services.AddSingleton(_rig.Embedding);
        services.AddSingleton(_rig.Reranker!);
        services.AddSingleton(_rig.Readiness);
    }

    /// <summary>Riordino finto deterministico: numero di parole (≥ 5 lettere) dell'interrogazione contenute nel passaggio. Il modello reale non è in gioco.</summary>
    private static double[] Overlap(string query, string[] docs)
    {
        static HashSet<string> Words(string text) => [.. Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\d]{5,}").Select(m => m.Value)];
        var q = Words(query);
        return [.. docs.Select(d => (double)Words(d).Count(q.Contains))];
    }

    // ================================================================ modello finto: gerarchia + stadio che legge SOLO i candidati ricevuti

    private sealed record Voice(string Alias, string Kind, string? Question, string Statement);

    private static List<Voice> Voices(string text) =>
        [.. Regex.Matches(text, @"<<<VOCE \[(E\d+)\] origine=\w+ tipo=(\w+)[^>\n]*?(?: domandaCanonica=(i\d+))?>>>\n([^\n]*)\n")
            .Select(m => new Voice(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null, m.Groups[4].Value))];

    private static JsonObject Assessment(string gap, string outcome, string reason, string[] support, string? openItem) => new()
    {
        ["gap"] = gap, ["outcome"] = outcome, ["reason"] = reason, ["rationale"] = "Valutazione sui soli candidati ricevuti.", ["supportingEvidence"] = new JsonArray([.. support.Select(s => (JsonNode)JsonValue.Create(s)!)]),
        ["openItem"] = openItem, ["openConflict"] = null,
    };

    internal static string Stage(DocumentChatWorkflowTests.ChatRequest request)
    {
        var voices = Voices(request.AllText);
        var candidates = voices.Where(v => v.Kind != "Gap").ToList();
        string[] Having(string needle) => [.. candidates.Where(c => c.Statement.Contains(needle, StringComparison.Ordinal)).Select(c => c.Alias)];
        var records = new JsonArray();
        foreach (var gap in voices.Where(v => v.Kind == "Gap"))
        {
            records.Add(gap.Statement switch
            {
                GapPresent => Assessment(gap.Alias, "AnsweredInSnapshot", "Answered", Having("fideiussione"), null),
                GapPartial => Assessment(gap.Alias, "Open", "Partial", Having("collaudo"), gap.Question),
                // Conflitto: entrambi i lati sono riscontri e l'esito resta Open (nessun arbitrato).
                GapConflict => Assessment(gap.Alias, "Open", "Ambiguous", Having("giorni dalla fattura"), gap.Question),
                _ => Assessment(gap.Alias, "Open", "NoAnswer", [], gap.Question),
            });
        }

        return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = records }.ToJsonString();
    }

    /// <summary>Gerarchia v4-equivalente del modello finto (enunciati liberi: gli helper della 002 numerano «Requisito N»): una voce per evidenza nella foglia A, i Gap come domande aperte.</summary>
    internal static string Hierarchy(DocumentChatWorkflowTests.ChatRequest request)
    {
        var p = DocumentReconcileAliasWorkflowTests.ParseAlias(request.AllText);
        var gaps = Voices(request.AllText).Where(v => v.Kind == "Gap").Select(v => v.Alias).ToHashSet(StringComparer.Ordinal);
        var items = new JsonArray();
        if (p is { Stage: "leaf", View: "A" })
        {
            var n = 0;
            foreach (var (alias, statement) in p.Voci)
            {
                n++;
                items.Add(JsonNode.Parse(JsonSerializer.Serialize(new { key = $"i{n}", section = gaps.Contains(alias) ? "openQuestions" : "requirements", text = statement, classification = "Documented", evidence = new[] { alias } })));
            }
        }

        return new JsonObject
        {
            ["contract"] = "reconcile-alias-1",
            ["descriptor"] = p.Stage is "leaf" or "reduce" ? $"gruppo {p.Stage} {p.View} {string.Join('-', p.Voci.Select(v => v.Alias).Concat(p.Nodes))}" : null,
            ["noFindings"] = items.Count > 0 ? null : "nessun collegamento emerso",
            ["items"] = items,
            ["candidates"] = new JsonArray(),
            ["focus"] = p.Stage == "compare" ? JsonNode.Parse("""{"outcome":"Compared","reason":null}""") : null,
            ["patches"] = new JsonArray(),
        }.ToJsonString();
    }

    private static string Respond(DocumentChatWorkflowTests.ChatRequest request) =>
        DocumentGapStageWorkflowTests.IsStage(request) ? Stage(request) : Hierarchy(request);

    // ================================================================ corpus sintetico con blocchi, preparazioni, evidenze e indice

    private sealed record Seeded(Guid Document, Guid Version, Guid Preparation, IReadOnlyList<Guid> Blocks, IReadOnlyList<Guid> Evidence);

    private async Task<Seeded> NewRetrievalDocument(HttpClient client, Guid project, string name, params (EvidenceKind Kind, string Text)[] items)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var (documentId, versionId) = (created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == versionId, Ct);
        var (artifact, job, version) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var blocks = items.Select(_ => Guid.NewGuid()).ToList();
        var now = DateTimeOffset.UtcNow;

        // Blocchi e artefatto: la seminagione salta le FK del solo parsing (session_replication_role), come nel kit del recupero.
        await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync(Ct);
            await using var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            seed.ParsedArtifacts.Add(new ParsedArtifact
            {
                Id = artifact, ProjectId = preparation.ProjectId, PreparationId = preparation.Id, JobId = Guid.NewGuid(), FencingToken = 1, SchemaVersion = "parsed-1.0.0",
                StorageKey = "k/" + Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64), ByteLength = 10, ParserConfigHash = new string('b', 64), BlockCount = items.Length,
            });
            for (var i = 0; i < items.Length; i++)
            {
                seed.DocumentBlocks.Add(new DocumentBlock
                {
                    Id = blocks[i], ProjectId = preparation.ProjectId, ArtifactId = artifact, Ordinal = i, Ref = $"b{i}", Type = BlockType.Paragraph, CanonicalText = items[i].Text,
                    TextHash = DocumentTestFixtures.Sha256(items[i].Text), HeadingPathJson = JsonSerializer.Serialize(new[] { name }),
                });
            }

            await seed.SaveChangesAsync(Ct);
        }

        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = items.Length, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var evidence = new List<Guid>();
        for (var i = 0; i < items.Length; i++)
        {
            var content = new DocumentEvidenceContent
            {
                SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
                KindRationale = null,
                Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
                Qualifiers = [],
                Sources =
                [
                    new PersistedSource
                    {
                        SourceId = Guid.NewGuid(), Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                        MessageId = null, Sequence = null, DocumentId = documentId, DocumentVersionId = versionId, PreparationId = preparation.Id,
                        ArtifactId = artifact, BlockId = blocks[i], Start = 0, End = items[i].Text.Length, Quote = items[i].Text,
                    },
                ],
            };
            var id = Guid.NewGuid();
            evidence.Add(id);
            db.Evidences.Add(new EvidenceItem
            {
                Id = id, VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = i + 1,
                Key = "e" + (i + 1), Kind = items[i].Kind, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = items[i].Text,
                Content = DocumentEvidenceCodec.EncodeContent(content),
            });
        }

        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = versionId, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = version, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.ParserVersion = "parser-1";
        preparation.CurrentEvidenceVersionId = version;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == documentId).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, versionId), Ct);

        var built = await _rig.Builder(db).BuildAsync(preparation.Id, Ct);
        Assert.Equal(RetrievalIndexStatus.Ready, built.Status);
        return new(documentId, versionId, preparation.Id, blocks, evidence);
    }

    private static string Filler(int n, int words) => $"Voce {n}: " + string.Join(' ', Enumerable.Range(0, words).Select(i => $"q{n}z{i}"));

    private sealed record Scenario(Guid Project, Guid Run, AnalysisRunClaim Claim, Seeded Excluded, Seeded Conflict, Seeded Facts);

    /// <summary>Progetto con i quattro Gap annotati, i fatti (uno solo lato di un conflitto per documento), un documento ESCLUSO dal PM e <paramref name="fillers"/> documenti di riempimento.</summary>
    private async Task<Scenario> ArrangeAsync(int fillers = 12, int fillerWords = 90)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var gaps = await NewRetrievalDocument(client, project, "domande", (EvidenceKind.Gap, GapPresent), (EvidenceKind.Gap, GapPartial), (EvidenceKind.Gap, GapConflict), (EvidenceKind.Gap, GapExcluded));
        var facts = await NewRetrievalDocument(client, project, "contratto", (EvidenceKind.Requirement, FactPresent), (EvidenceKind.Requirement, FactPartial), (EvidenceKind.Requirement, FactSideA));
        var conflict = await NewRetrievalDocument(client, project, "email-cliente", (EvidenceKind.Requirement, FactSideB));
        var excluded = await NewRetrievalDocument(client, project, "garanzie-torino", (EvidenceKind.Requirement, FactExcluded));
        for (var n = 1; n <= fillers; n++)
        {
            await NewRetrievalDocument(client, project, $"allegato{n}", (EvidenceKind.Requirement, Filler(n, fillerWords)));
        }

        // Esclusione del PM: If-Match sulla revisione corrente della selezione (0).
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection")
        {
            Content = JsonContent.Create(new { items = new[] { new { documentId = excluded.Document, included = false, mode = "LatestReady", versionId = (Guid?)null } } }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put, Ct)).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        return new(project, run, await ClaimAsync(run), excluded, conflict, facts);
    }

    private async Task<(string Status, string Code, string Message, int Calls)> RunRow(Guid run) => (
        await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorCode\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<int>("SELECT coalesce(\"CallCount\",0) FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

    private Task<long> Outputs(Guid run, string schema) => Count(
        "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed' AND \"SchemaVersion\" = @s", ("r", run), ("s", schema));

    private async Task<RetrievalRegistry> RegistryOf(Guid run) =>
        RetrievalOutput.Read(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'retrieval-output-1'", ("r", run)));

    // ================================================================ freeze e percorso felice

    [Fact]
    public async Task The_real_analyze_endpoint_freezes_v8_when_retrieval_is_enabled()
    {
        // Correzione T040 Run 2 (decisione approvata): le NUOVE richieste con recupero congelano la v8 = v7 + `reconcileSchemaProfile`; i run già congelati v7 restano v7.
        var s = await ArrangeAsync(fillers: 1, fillerWords: 5);

        Assert.Equal("project-analysis-documents-v8", await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
        using var scope = App.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProcessingRuns.FindAsync([s.Run], Ct);
        Assert.Equal(8, DocumentAnalysisWorkflow.ContractOf(row!));
        var keys = JsonDocument.Parse(row!.PromptVersions!).RootElement;
        Assert.Equal(RetrievalPolicy.Version1, keys.GetProperty("retrievalPolicy").GetString());
        Assert.Equal(ReconcileAliasContracts.SchemaProfileByBatch, keys.GetProperty(DocumentAnalysisWorkflow.ReconcileSchemaProfileKey).GetString());
        Assert.Equal(ReconcileAliasContracts.PromptVersionV4, keys.GetProperty("reconcile").GetString());   // la v8 congela il prompt della gerarchia document-reconcile-v4
    }

    [Fact]
    public async Task A_ledger_beyond_the_stage_budget_completes_with_one_stage_call_on_the_retrieved_package_and_no_llm_call_for_the_retrieval()
    {
        var s = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(s.Claim, model);

        var row = await RunRow(s.Run);
        Assert.True(result.Status == ProcessingRunStatus.Completed, $"{row.Code}: {row.Message}");
        Assert.DoesNotContain("gap_ledger_exceeds_input", row.Message, StringComparison.Ordinal);

        // Esattamente UNA chiamata dello stadio, dopo la gerarchia; il recupero non consuma chiamate LLM: CallCount = invii reali al modello.
        var stage = Assert.Single(model.Requests, DocumentGapStageWorkflowTests.IsStage);
        Assert.True(DocumentGapStageWorkflowTests.IsStage(model.Requests[^1]));
        Assert.Equal(model.Requests.Count, row.Calls);
        Assert.Equal(1, await Outputs(s.Run, "gap-stage-alias-1"));
        Assert.Equal(1, await Outputs(s.Run, "retrieval-output-1"));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'retrieval'", ("r", s.Run)));
        var hierarchy = model.Requests.Count - 1;
        Assert.Equal(hierarchy, await Scalar<int>("SELECT \"Ordinal\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'retrieval'", ("r", s.Run)));
        Assert.Equal(hierarchy + 1, await Scalar<int>("SELECT \"Ordinal\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'gap'", ("r", s.Run)));
        Assert.Equal("project-analysis-documents-v8", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", s.Run)));

        // Registro congelato nel WorkOutput: pacchetto consegnabile, ordine, politica e hash.
        var registry = await RegistryOf(s.Run);
        Assert.Equal((CandidateRegistry.OutcomePackage, CandidateRegistry.SearchBasis, RetrievalPolicy.Version1), (registry.Outcome, registry.SearchBasis, registry.PolicyVersion));
        Assert.Equal(RetrievalPolicy.V1.Hash(), registry.PolicyHash);
        Assert.Equal(4, registry.Gaps.Count);
        Assert.NotEmpty(registry.PackageEvidence);
        Assert.Equal(registry.PackageEvidence.Count, await RegistryPackageCandidates(stage.AllText));

        // Il messaggio dichiara la sorgente: non è il ledger completo.
        Assert.Contains("searchBasis: retrieval", stage.AllText, StringComparison.Ordinal);
        Assert.Contains($"retrievalComplete: {(registry.RetrievalComplete ? "true" : "false")}", stage.AllText, StringComparison.Ordinal);
        Assert.Contains("NON il ledger completo", stage.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("tutte, senza ranking", stage.AllText, StringComparison.Ordinal);

        // Candidati: i fatti pertinenti ci sono, entrambi i lati del conflitto, MAI il documento escluso.
        foreach (var fact in new[] { FactPresent, FactPartial, FactSideA, FactSideB })
        {
            Assert.Contains(fact + "\n<<<FONTE", stage.AllText, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(FactExcluded, stage.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("garanzie-torino", stage.AllText, StringComparison.Ordinal);
        Assert.All(registry.Gaps.SelectMany(g => g.Candidates), c => Assert.NotEqual(s.Excluded.Document, c.DocumentId));
        Assert.DoesNotContain(s.Excluded.Evidence[0], registry.PackageEvidence);
        // Il recupero non è un invio: nessun UUID, hash o offset nel testo per il modello.
        Assert.DoesNotMatch("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", stage.AllText);

        // Ogni Gap ha un esito e i supporti sono passaggi originali recuperati (alias dei candidati forniti).
        var payload = JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", s.Run)));
        var records = payload.RootElement.GetProperty("gapAssessments").EnumerateArray().ToList();
        Assert.Equal(4, records.Count);
        Assert.Equal("Complete", payload.RootElement.GetProperty("outcome").GetString());
        var stageInfo = payload.RootElement.GetProperty("stage");
        Assert.Equal(("retrieval", registry.RetrievalComplete, registry.PackageEvidence.Count), (stageInfo.GetProperty("searchBasis").GetString(), stageInfo.GetProperty("retrievalComplete").GetBoolean(), stageInfo.GetProperty("candidates").GetInt32()));
        Assert.Equal(["Answered", "Partial", "Ambiguous", "NoAnswer"], records.Select(r => r.GetProperty("reason").GetString()).OrderBy(x => x == "Answered" ? 0 : x == "Partial" ? 1 : x == "Ambiguous" ? 2 : 3));
        var ambiguous = Assert.Single(records, r => r.GetProperty("reason").GetString() == "Ambiguous");
        Assert.Equal("Open", ambiguous.GetProperty("outcome").GetString());          // un conflitto resta aperto: nessun arbitrato
        Assert.Equal(2, ambiguous.GetProperty("supportingEvidence").GetArrayLength());   // entrambi i lati sono riscontri
        Assert.Single(records, r => r.GetProperty("outcome").GetString() == "AnsweredInSnapshot");
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));

        // Nessuna richiesta di embedding di contenuto oltre all'indice e alle quattro interrogazioni.
        Assert.True(_embedding.EmbeddingRequests > 0);
        Assert.True(_rerank.RerankRequests >= 4);
    }

    private static Task<int> RegistryPackageCandidates(string stageText) =>
        Task.FromResult(Voices(stageText).Count(v => v.Kind != "Gap"));

    // ================================================================ ledger entro budget: sorgente esaustiva come in 002

    [Fact]
    public async Task A_v7_run_whose_ledger_fits_the_stage_budget_keeps_the_exhaustive_source_and_never_retrieves()
    {
        // Contesto largo: lo stadio entra con l'intero ledger, ma il single-call non entra (gerarchia). Il recupero non interviene (FR-002).
        var s = await ArrangeAsync(fillers: 14, fillerWords: 8);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond, ContextWindow = 8000 };
        var searchesBefore = _embedding.EmbeddingRequests;

        var result = await RunAsync(s.Claim, model, contextWindow: 8000);

        var row = await RunRow(s.Run);
        Assert.True(result.Status == ProcessingRunStatus.Completed, $"{row.Code}: {row.Message}");
        var stages = model.Requests.Where(DocumentGapStageWorkflowTests.IsStage).ToList();
        var stage = Assert.Single(stages);   // il single-call non entra, lo stadio sì: (b1-ledger)
        Assert.True(stage.AllText.Contains("searchBasis: ledger_exhaustive", StringComparison.Ordinal), stage.AllText[..300]);
        Assert.Contains("retrievalComplete: true", stage.AllText, StringComparison.Ordinal);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'retrieval'", ("r", s.Run)));

        Assert.Equal(0, await Outputs(s.Run, "retrieval-output-1"));
        Assert.Equal(searchesBefore, _embedding.EmbeddingRequests);   // nessuna interrogazione ai servizi di recupero
    }

    // ================================================================ servizi non disponibili: errori tipizzati a zero invii LLM

    [Fact]
    public async Task An_unavailable_embedding_service_fails_the_run_with_a_typed_code_and_zero_stage_calls()
    {
        var s = await ArrangeAsync();
        _embedding.Models = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _embedding.EmbeddingsOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _rig.Readiness.Invalidate();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(s.Claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(RetrievalException.EmbeddingUnavailable, result.ErrorCode);
        Assert.DoesNotContain(model.Requests, DocumentGapStageWorkflowTests.IsStage);   // zero invii dello stadio
        var row = await RunRow(s.Run);
        Assert.Equal(model.Requests.Count, row.Calls);                                    // nessuna riserva per il recupero
        Assert.StartsWith(RetrievalException.EmbeddingUnavailable + ":", row.Message, StringComparison.Ordinal);
        Assert.Equal(0, await Outputs(s.Run, "gap-stage-alias-1"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));
        var registry = await RegistryOf(s.Run);                                           // registro d'errore conservato e consultabile
        Assert.Equal(CandidateRegistry.OutcomeFailed, registry.Outcome);
        Assert.Empty(registry.PackageEvidence);
        Assert.False(registry.RetrievalComplete);
    }

    [Fact]
    public async Task An_index_that_is_not_ready_fails_the_run_with_a_typed_code_and_zero_stage_calls()
    {
        var s = await ArrangeAsync();
        await Execute("UPDATE \"RetrievalIndexStates\" SET \"Status\" = 'Building' WHERE \"ProjectId\" = @p AND \"DocumentVersionId\" = @v", ("p", s.Project), ("v", s.Facts.Version));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(s.Claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, RetrievalException.IndexNotReady), (result.Status, result.ErrorCode));
        Assert.DoesNotContain(model.Requests, DocumentGapStageWorkflowTests.IsStage);
        Assert.Equal(0, await Outputs(s.Run, "gap-stage-alias-1"));
        Assert.Equal(CandidateRegistry.OutcomeFailed, (await RegistryOf(s.Run)).Outcome);
    }

    [Fact]
    public async Task An_unavailable_reranker_ends_the_run_with_a_typed_cause_keeps_the_diagnostic_registry_and_never_invokes_the_stage()
    {
        var s = await ArrangeAsync();
        _rerank.RerankOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _rerank.Models = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _rig.Readiness.InvalidateReranker();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(s.Claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, RetrievalException.RerankerUnavailable), (result.Status, result.ErrorCode));
        Assert.DoesNotContain(model.Requests, DocumentGapStageWorkflowTests.IsStage);   // stadio Gap mai invocato
        Assert.NotEqual(ProcessingRunStatus.Completed, result.Status);
        Assert.Equal(0, await Outputs(s.Run, "gap-stage-alias-1"));                      // nessun gapAssessments
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));
        var registry = await RegistryOf(s.Run);
        Assert.Equal(CandidateRegistry.OutcomeDiagnostic, registry.Outcome);
        Assert.True(registry.Degraded);
        Assert.False(registry.RetrievalComplete);
        Assert.Contains(RetrievalException.RerankerUnavailable, registry.Causes);
        Assert.Empty(registry.PackageEvidence);
        Assert.NotEmpty(registry.Gaps.SelectMany(g => g.Candidates));                     // ordine RRF diagnostico consultabile
    }

    // ================================================================ replay, takeover, crash

    [Fact]
    public async Task Replay_and_takeover_after_the_retrieval_output_never_call_the_retrieval_services_again_and_keep_the_same_registry()
    {
        var s = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(s.Claim, model)).Status);
        var registry = await RegistryOf(s.Run);
        var (embeddings, reranks) = (_embedding.EmbeddingRequests, _rerank.RerankRequests);
        var calls = model.Requests.Count;

        // Takeover: nuovo owner e fence sul run già Completed (terminale: nessuna rielaborazione).
        await Execute("UPDATE \"ProcessingRuns\" SET \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2 WHERE \"Id\" = @r", ("r", s.Run));
        var replay = await RunAsync(new AnalysisRunClaim(s.Run, "owner-2", 2, 2), model);

        Assert.Equal(ProcessingRunStatus.Completed, replay.Status);
        Assert.Equal((embeddings, reranks, calls), (_embedding.EmbeddingRequests, _rerank.RerankRequests, model.Requests.Count));
        Assert.Equal(registry.Hash(), (await RegistryOf(s.Run)).Hash());
        Assert.Equal(1, await Outputs(s.Run, "retrieval-output-1"));
    }

    [Fact]
    public async Task A_crash_between_the_retrieval_and_the_stage_resumes_without_repeating_the_retrieval_and_completes_with_the_same_registry()
    {
        var s = await ArrangeAsync();
        var crash = new DocumentChatWorkflowTests.FakeChatModel
        {
            // Il processo muore alla chiamata dello stadio: recupero già congelato, nessun output dello stadio.
            Responder = r => DocumentGapStageWorkflowTests.IsStage(r) ? throw new InvalidOperationException("crash simulato") : Hierarchy(r),
        };
        var first = await Record.ExceptionAsync(() => RunAsync(s.Claim, crash));
        Assert.True(first is not null || (await RunRow(s.Run)).Status != "Completed");
        Assert.Equal(1, await Outputs(s.Run, "retrieval-output-1"));
        Assert.Equal(0, await Outputs(s.Run, "gap-stage-alias-1"));
        var registry = await RegistryOf(s.Run);
        var (embeddings, reranks) = (_embedding.EmbeddingRequests, _rerank.RerankRequests);

        // Ripresa: il run torna in esecuzione con un nuovo owner (takeover) e completa sul registro congelato.
        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Running', \"ErrorCode\" = NULL, \"ErrorMessage\" = NULL, \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", s.Run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        var resumed = await RunAsync(new AnalysisRunClaim(s.Run, "owner-2", 2, 2), model);

        var row = await RunRow(s.Run);
        Assert.True(resumed.Status == ProcessingRunStatus.Completed, $"{row.Code}: {row.Message}");
        Assert.Single(model.Requests);                                                     // solo lo stadio: la gerarchia e il recupero sono Committed
        Assert.True(DocumentGapStageWorkflowTests.IsStage(model.Requests[0]));
        Assert.Equal((embeddings, reranks), (_embedding.EmbeddingRequests, _rerank.RerankRequests));   // nessuna nuova richiesta ai servizi di recupero
        Assert.Equal(registry.Hash(), (await RegistryOf(s.Run)).Hash());
        Assert.Equal(1, await Outputs(s.Run, "retrieval-output-1"));
        Assert.Equal(1, await Outputs(s.Run, "gap-stage-alias-1"));
    }

    // ================================================================ v8: schema della gerarchia per stadio e vista (correzione T040 Run 2, chiamata 026)

    private static string Canon(string json) => CanonicalJson.Canonicalize(json);

    private static ReconcileSchemaVariant VariantOf(DocumentChatWorkflowTests.ChatRequest request)
    {
        var p = DocumentReconcileAliasWorkflowTests.ParseAlias(request.AllText);
        return ReconcileAliasContracts.SchemaVariantOf(p.Stage, Enum.Parse<ReconcileView>(p.View));
    }

    private Task<string> ManifestProfile(Guid run) =>
        Scalar<string>("SELECT coalesce(\"PlanJson\"->>'reconcileSchemaProfile', '') FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run));

    /// <summary>Versioni del prompt persistite sui batch derivati della gerarchia (DTO reconcile-alias-1): <c>SameBatch</c> le confronta alla ripresa.</summary>
    private Task<string> HierarchyPromptVersions(Guid run) =>
        Scalar<string>("SELECT coalesce(string_agg(DISTINCT \"PromptVersion\", ','), '') FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = @s", ("r", run), ("s", ReconcileAliasContracts.SchemaVersion));

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Il prompt di sistema REALMENTE inviato a ogni batch della gerarchia è quello della versione indicata (lo stadio Gap conserva il proprio).</summary>
    private static void AssertHierarchyPrompt(DocumentChatWorkflowTests.FakeChatModel model, string version)
    {
        var expected = Lf(ReconcileAliasContracts.InstructionsFor(version));
        var hierarchy = model.Requests.Where(r => !DocumentGapStageWorkflowTests.IsStage(r)).ToList();
        Assert.NotEmpty(hierarchy);
        Assert.All(hierarchy, r => Assert.Equal(expected, Lf(r.System)));
        Assert.All(model.Requests.Where(DocumentGapStageWorkflowTests.IsStage), r => Assert.Equal(Lf(GapStage.Instructions), Lf(r.System)));
    }

    [Fact]
    public async Task V8_sends_each_hierarchy_batch_the_schema_variant_of_its_stage_and_view_and_freezes_the_profile_in_the_manifest()
    {
        var s = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(s.Claim, model);

        var row = await RunRow(s.Run);
        Assert.True(result.Status == ProcessingRunStatus.Completed, $"{row.Code}: {row.Message}");
        var seen = new HashSet<ReconcileSchemaVariant>();
        foreach (var request in model.Requests)
        {
            // Lo schema verificato è quello REALMENTE presente nel corpo HTTP (`response_format.json_schema.schema`), non quello dichiarato dal piano.
            Assert.NotNull(request.ResponseSchema);
            if (DocumentGapStageWorkflowTests.IsStage(request))
            {
                Assert.Equal(Canon(GapStageContracts.JsonSchema().GetRawText()), Canon(request.ResponseSchema!));   // lo stadio Gap conserva il proprio schema
                continue;
            }

            var variant = VariantOf(request);
            seen.Add(variant);
            Assert.Equal(Canon(ReconcileAliasContracts.SchemaOf(variant).GetRawText()), Canon(request.ResponseSchema!));
            if (variant != ReconcileSchemaVariant.Full)
            {
                Assert.Equal(0, JsonDocument.Parse(request.ResponseSchema!).RootElement.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
            }
        }

        // La gerarchia di questo corpus ha foglie in entrambe le viste: la foglia A riceve lo schema completo (fissato), la foglia B quello con `items` vuoto.
        Assert.Contains(ReconcileSchemaVariant.Full, seen);
        Assert.Contains(ReconcileSchemaVariant.NoItems, seen);
        Assert.Equal(ReconcileAliasContracts.SchemaProfileByBatch, await ManifestProfile(s.Run));

        // Prompt della v8: document-reconcile-v4 in ogni batch della gerarchia (foglie A e B comprese) e nella versione persistita di ogni batch derivato.
        AssertHierarchyPrompt(model, ReconcileAliasContracts.PromptVersionV4);
        Assert.Equal(ReconcileAliasContracts.PromptVersionV4, await HierarchyPromptVersions(s.Run));
    }

    [Fact]
    public async Task A_run_frozen_as_v7_before_the_change_keeps_sending_the_single_pinned_schema_and_its_manifest_plan()
    {
        var s = await ArrangeAsync();
        // Run congelato v7 (come quelli già esistenti): stessa selezione, chiavi v7 senza `reconcileSchemaProfile`.
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = CAST(@p AS jsonb) WHERE \"Id\" = @r",
            ("d", DocumentAnalysisWorkflow.DefinitionVersionV7), ("p", SourceSnapshotStore.AnalysisPromptVersionsV7(new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 })), ("r", s.Run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(s.Claim, model);

        var row = await RunRow(s.Run);
        Assert.True(result.Status == ProcessingRunStatus.Completed, $"{row.Code}: {row.Message}");
        var pinned = Canon(ReconcileAliasContracts.JsonSchema().GetRawText());
        Assert.All(model.Requests.Where(r => !DocumentGapStageWorkflowTests.IsStage(r)), r => Assert.Equal(pinned, Canon(r.ResponseSchema!)));
        Assert.Contains(model.Requests, r => !DocumentGapStageWorkflowTests.IsStage(r) && VariantOf(r) == ReconcileSchemaVariant.NoItems);   // anche la foglia B: schema unico storico
        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersionV7, await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", s.Run)));
        Assert.Equal(string.Empty, await ManifestProfile(s.Run));   // piano v7 invariato: nessuna chiave del profilo
        AssertHierarchyPrompt(model, ReconcileAliasContracts.PromptVersion);                           // prompt storico document-reconcile-v3, mai il v4
        Assert.Equal(ReconcileAliasContracts.PromptVersion, await HierarchyPromptVersions(s.Run));
    }

    [Fact]
    public async Task A_v8_run_frozen_with_the_v3_reconcile_prompt_fails_as_incompatible_before_any_call()
    {
        // Forma della v8 prima del completamento con il prompt v4: mai reinterpretata (né come v7 né promossa al v4), nessuna selezione, misura o invio.
        var s = await ArrangeAsync();
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(SourceSnapshotStore.AnalysisPromptVersionsV8(new RetrievalOptions { PolicyVersion = RetrievalPolicy.Version1 }))!;
        keys["reconcile"] = ReconcileAliasContracts.PromptVersion;
        await Execute("UPDATE \"ProcessingRuns\" SET \"PromptVersions\" = CAST(@p AS jsonb) WHERE \"Id\" = @r", ("p", JsonSerializer.Serialize(keys)), ("r", s.Run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };

        var result = await RunAsync(s.Claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, "workflow_incompatible"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r", ("r", s.Run)));
    }

    [Fact]
    public async Task Under_v8_a_leaf_B_answer_with_items_is_still_rejected_by_the_validator_with_no_commit_and_no_further_call()
    {
        // La grammatica del provider è un aiuto, non una garanzia: il modello finto ignora lo schema e risponde come la chiamata 026 (un item per voce nella foglia B, nessun candidato).
        var s = await ArrangeAsync();
        string? rejected = null;
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r =>
            {
                if (DocumentGapStageWorkflowTests.IsStage(r))
                {
                    return Stage(r);
                }

                var p = DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText);
                if (p is not { Stage: "leaf", View: "B" })
                {
                    return Hierarchy(r);
                }

                rejected = r.AllText;
                var items = new JsonArray([.. p.Voci.Select((v, i) => JsonNode.Parse(JsonSerializer.Serialize(new { key = $"i{i + 1}", section = "requirements", text = v.Statement, classification = "Documented", evidence = new[] { v.Alias } })))]);
                return new JsonObject
                {
                    ["contract"] = "reconcile-alias-1", ["descriptor"] = "gruppo B", ["noFindings"] = null, ["items"] = items, ["candidates"] = new JsonArray(), ["focus"] = null, ["patches"] = new JsonArray(),
                }.ToJsonString();
            },
        };

        var result = await RunAsync(s.Claim, model);

        Assert.NotNull(rejected);
        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        var row = await RunRow(s.Run);
        Assert.Contains("items: ammessi solo nelle foglie della vista A", row.Message, StringComparison.Ordinal);
        Assert.Equal(rejected, model.Requests[^1].AllText);                                          // nessuna chiamata dopo il rifiuto: nessun retry nascosto
        Assert.Equal(ReconcileSchemaVariant.NoItems, VariantOf(model.Requests[^1]));                  // e la richiesta rifiutata portava lo schema con `items` vuoto
        Assert.Equal(model.Requests.Count - 1, await Outputs(s.Run, ReconcileAliasContracts.SchemaVersion));   // l'output invalido non è Committed
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));
    }

    [Fact]
    public async Task A_crash_on_a_leaf_B_batch_resumes_with_the_same_input_hash_and_schema_and_never_resends_committed_batches()
    {
        var s = await ArrangeAsync();
        string? crashed = null;
        var crash = new DocumentChatWorkflowTests.FakeChatModel
        {
            // Il processo muore alla prima foglia B (batch pubblicato e riservato, nessun output): i batch precedenti sono Committed.
            Responder = r =>
            {
                if (!DocumentGapStageWorkflowTests.IsStage(r) && VariantOf(r) == ReconcileSchemaVariant.NoItems && crashed is null)
                {
                    crashed = r.AllText;
                    throw new InvalidOperationException("crash simulato");
                }

                return Respond(r);
            },
        };
        await Record.ExceptionAsync(() => RunAsync(s.Claim, crash));
        Assert.NotNull(crashed);
        var answered = crash.Requests.Where(r => r.AllText != crashed).Select(r => r.AllText).ToHashSet(StringComparer.Ordinal);
        var committedBefore = await Outputs(s.Run, ReconcileAliasContracts.SchemaVersion);
        Assert.Equal(answered.Count, committedBefore);

        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Running', \"ErrorCode\" = NULL, \"ErrorMessage\" = NULL, \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", s.Run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        var resumed = await RunAsync(new AnalysisRunClaim(s.Run, "owner-2", 2, 2), model);

        var row = await RunRow(s.Run);
        Assert.True(resumed.Status == ProcessingRunStatus.Completed, $"{row.Code}: {row.Message}");   // nessun integrity_conflict: l'inputHash ricalcolato coincide con quello pubblicato
        Assert.Equal(crashed, model.Requests[0].AllText);                                              // si riprende dal batch interrotto
        Assert.Equal(Canon(ReconcileAliasContracts.SchemaOf(ReconcileSchemaVariant.NoItems).GetRawText()), Canon(model.Requests[0].ResponseSchema!));
        Assert.Equal(Lf(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV4)), Lf(model.Requests[0].System));   // stesso prompt v4 alla ripresa
        Assert.Equal(ReconcileAliasContracts.PromptVersionV4, await HierarchyPromptVersions(s.Run));    // nessun batch con un'altra versione del prompt
        Assert.DoesNotContain(model.Requests, r => answered.Contains(r.AllText));                      // nessun batch Committed reinviato
        Assert.Equal(0, await Count(
            "SELECT count(*) FROM (SELECT \"Ordinal\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r GROUP BY \"Ordinal\" HAVING count(*) > 1) d", ("r", s.Run)));   // nessuna riga orfana per ordinale
    }

    // ================================================================ coverage (T032) e invarianti di assenza (T039, per questa configurazione)

    [Fact]
    public async Task The_coverage_of_a_retrieval_run_declares_the_retrieval_basis_and_never_proves_absence()
    {
        var s = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(s.Claim, model)).Status);
        var registry = await RegistryOf(s.Run);
        using var client = await App.LoginAsync();

        var created = await client.PostAsJsonAsync($"/api/projects/{s.Project}/analyses/{s.Run}/coverage-reports", new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var report = await Json(created);
        Assert.Equal("coverage-3", report.GetProperty("checkerVersion").GetString());

        // Sezione `retrieval`: conteggi e dichiarazioni di metodo coerenti con il registro congelato, nessun identificativo tecnico.
        var section = report.GetProperty("retrieval");
        Assert.Equal((CandidateRegistry.SearchBasis, registry.RetrievalComplete, registry.PolicyVersion, 4, registry.PackageEvidence.Count),
            (section.GetProperty("searchBasis").GetString(), section.GetProperty("retrievalComplete").GetBoolean(), section.GetProperty("policyVersion").GetString(),
                section.GetProperty("gaps").GetInt32(), section.GetProperty("inPackage").GetInt32()));
        Assert.DoesNotMatch("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", section.GetRawText());

        // Nessuna assenza provata: contatori a zero, ogni Gap con sorgente retrieval ha absenceProven = false (un NoAnswer è «assenza non provata»).
        var summary = report.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("absenceProvenGaps").GetInt32());
        Assert.Equal(summary.GetProperty("openGaps").GetInt32() + summary.GetProperty("answeredGaps").GetInt32(), summary.GetProperty("assessedGaps").GetInt32());
        var assessments = report.GetProperty("gapAssessments").EnumerateArray().ToList();
        Assert.Equal(4, assessments.Count);
        Assert.All(assessments, a =>
        {
            Assert.Equal(CandidateRegistry.SearchBasis, a.GetProperty("searchBasis").GetString());
            Assert.Equal(registry.RetrievalComplete, a.GetProperty("retrievalComplete").GetBoolean());
            Assert.False(a.GetProperty("absenceProven").GetBoolean());
        });
        Assert.Contains(assessments, a => a.GetProperty("reason").GetString() == "NoAnswer");   // il caso «assente» esiste e resta non provato

        // I limiti dichiarano la natura del recupero, non l'esaustività del ledger.
        var limits = string.Join('\n', report.GetProperty("limits").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains("assenza non provata", limits, StringComparison.Ordinal);
        Assert.Contains("retrievalComplete", limits, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ledger_exhaustive_run_has_no_retrieval_section_in_its_coverage()
    {
        // Contesto largo: ledger entro budget ⇒ sorgente esaustiva come in 002; la sezione `retrieval` è assente e i campi dei Gap restano quelli della 002.
        var s = await ArrangeAsync(fillers: 14, fillerWords: 8);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond, ContextWindow = 8000 };
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(s.Claim, model, contextWindow: 8000)).Status);
        using var client = await App.LoginAsync();

        var report = await Json(await client.PostAsJsonAsync($"/api/projects/{s.Project}/analyses/{s.Run}/coverage-reports", new { }, Ct));

        Assert.False(report.TryGetProperty("retrieval", out _));
        Assert.All(report.GetProperty("gapAssessments").EnumerateArray(), a => Assert.Equal("ledger_exhaustive", a.GetProperty("searchBasis").GetString()));
        var limits = string.Join('\n', report.GetProperty("limits").EnumerateArray().Select(l => l.GetString()));
        Assert.DoesNotContain("searchBasis retrieval", limits, StringComparison.Ordinal);
    }

    // ================================================================ invarianti di assenza e completezza su più configurazioni (T039)

    private static string AllNoAnswer(DocumentChatWorkflowTests.ChatRequest request)
    {
        var records = new JsonArray([.. Voices(request.AllText).Where(v => v.Kind == "Gap").Select(g => (JsonNode)Assessment(g.Alias, "Open", "NoAnswer", [], g.Question))]);
        return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = records }.ToJsonString();
    }

    /// <summary>
    /// Proprietà (FR-018/FR-019, SC-008): per OGNI configurazione con sorgente `retrieval` — ledger piccolo/medio/grande, pacchetto completo o troncato dal budget, modello che risponde in modo misto o
    /// «assente» ovunque — nessun Gap è mai «assenza provata» in nessuna rappresentazione (WorkOutput, coverage, API del registro); `retrievalComplete` non diventa mai «assente».
    /// </summary>
    [Theory]
    [InlineData(12, 90, "mixed")]
    [InlineData(12, 90, "noanswer")]
    [InlineData(6, 200, "mixed")]
    [InlineData(20, 60, "noanswer")]
    [InlineData(5, 130, "mixed")]
    public async Task For_every_configuration_with_the_retrieval_source_no_gap_is_ever_represented_as_proven_absent(int fillers, int fillerWords, string answers)
    {
        var s = await ArrangeAsync(fillers, fillerWords);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => answers == "noanswer" && DocumentGapStageWorkflowTests.IsStage(r) ? AllNoAnswer(r) : Respond(r) };
        var result = await RunAsync(s.Claim, model);
        var row = await RunRow(s.Run);
        Assert.True(result.Status == ProcessingRunStatus.Completed, $"{fillers}/{fillerWords}/{answers}: {row.Code}: {row.Message}");
        var registry = await RegistryOf(s.Run);
        Assert.Equal(CandidateRegistry.SearchBasis, registry.SearchBasis);
        using var client = await App.LoginAsync();

        // 1. WorkOutput dello stadio: la sorgente dichiarata è retrieval e retrievalComplete è quello del registro (mai riscritto in «assente»).
        var stage = JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", s.Run))).RootElement.GetProperty("stage");
        Assert.Equal((CandidateRegistry.SearchBasis, registry.RetrievalComplete), (stage.GetProperty("searchBasis").GetString(), stage.GetProperty("retrievalComplete").GetBoolean()));

        // 2. Coverage: nessuna assenza provata, né nel summary né per Gap; i NoAnswer restano «non provati».
        var report = await Json(await client.PostAsJsonAsync($"/api/projects/{s.Project}/analyses/{s.Run}/coverage-reports", new { }, Ct));
        Assert.Equal(0, report.GetProperty("summary").GetProperty("absenceProvenGaps").GetInt32());
        var assessments = report.GetProperty("gapAssessments").EnumerateArray().ToList();
        Assert.NotEmpty(assessments);
        Assert.All(assessments, a =>
        {
            Assert.Equal(CandidateRegistry.SearchBasis, a.GetProperty("searchBasis").GetString());
            Assert.Equal(registry.RetrievalComplete, a.GetProperty("retrievalComplete").GetBoolean());
            Assert.False(a.GetProperty("absenceProven").GetBoolean());
        });
        if (answers == "noanswer")
        {
            Assert.All(assessments, a => Assert.Equal("NoAnswer", a.GetProperty("reason").GetString()));
            Assert.Equal(assessments.Count, report.GetProperty("summary").GetProperty("absenceUnprovenGaps").GetInt32());
        }

        // 3. API del registro: dichiara la procedura eseguita, non l'assenza; nessun campo «absent/absenceProven».
        var view = await Json(await client.GetAsync(RegistryUrl(s.Project, s.Run), Ct));
        Assert.Equal(registry.RetrievalComplete, view.GetProperty("retrievalComplete").GetBoolean());
        Assert.DoesNotContain("absen", view.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"absenceProven\":true", report.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Proprietà in modalità degradata (FR-035, SC-011): per OGNI causa di mancato completamento nessuno stadio Gap, nessun gapAssessments, nessuna revisione, nessun report di coverage creabile e
    /// quindi nessun `AnsweredInSnapshot` definitivo né attestazione di completezza; il registro, quando c'è, non è mai completo.
    /// </summary>
    [Theory]
    [InlineData("reranker")]
    [InlineData("embedding")]
    [InlineData("index")]
    public async Task For_every_degraded_cause_no_definitive_result_or_completeness_claim_is_ever_produced(string cause)
    {
        var s = await ArrangeAsync();
        switch (cause)
        {
            case "reranker":
                _rerank.RerankOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                _rerank.Models = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                _rig.Readiness.InvalidateReranker();
                break;
            case "embedding":
                _embedding.Models = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                _embedding.EmbeddingsOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                _rig.Readiness.Invalidate();
                break;
            default:
                await Execute("UPDATE \"RetrievalIndexStates\" SET \"Status\" = 'Building' WHERE \"ProjectId\" = @p AND \"DocumentVersionId\" = @v", ("p", s.Project), ("v", s.Facts.Version));
                break;
        }

        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond };
        var result = await RunAsync(s.Claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.NotNull(result.ErrorCode);
        Assert.DoesNotContain(model.Requests, DocumentGapStageWorkflowTests.IsStage);
        Assert.Equal(0, await Outputs(s.Run, "gap-stage-alias-1"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"EvidenceCoverageReports\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"PayloadJson\"::text LIKE '%AnsweredInSnapshot%'", ("r", s.Run)));

        using var client = await App.LoginAsync();
        var coverage = await client.PostAsJsonAsync($"/api/projects/{s.Project}/analyses/{s.Run}/coverage-reports", new { }, Ct);
        Assert.NotEqual(HttpStatusCode.Created, coverage.StatusCode);                                  // nessuna attestazione di copertura o di conflitti per un run non completato

        var registry = await RegistryOf(s.Run);
        Assert.False(registry.RetrievalComplete);
        Assert.NotEqual(CandidateRegistry.OutcomePackage, registry.Outcome);
        Assert.Empty(registry.PackageEvidence);
        var response = await client.GetAsync(RegistryUrl(s.Project, s.Run), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.False(view.GetProperty("retrievalComplete").GetBoolean());
        Assert.NotEqual(CandidateRegistry.OutcomePackage, view.GetProperty("outcome").GetString());
        Assert.All(view.GetProperty("gaps").EnumerateArray().SelectMany(g => g.GetProperty("candidates").EnumerateArray()), c => Assert.False(c.GetProperty("inPackage").GetBoolean()));
    }

    // ================================================================ alias e citazioni su candidati recuperati (T038)

    [Fact]
    public async Task A_stage_response_citing_an_alias_that_was_not_among_the_retrieved_candidates_is_rejected_with_a_typed_error_and_no_silent_fix()
    {
        // Alias = voci del messaggio: il sottoinsieme recuperato ne offre un numero limitato; quello subito dopo l'ultimo (come un alias del ledger non incluso) NON è fornito.
        string[] scenarios = ["next-after-last", "invented", "gap-as-support"];
        foreach (var scenario in scenarios)
        {
            var s = await ArrangeAsync();
            var model = new DocumentChatWorkflowTests.FakeChatModel
            {
                Responder = r =>
                {
                    if (!DocumentGapStageWorkflowTests.IsStage(r))
                    {
                        return Hierarchy(r);
                    }

                    var voices = Voices(r.AllText);
                    var gap = voices.First(v => v.Kind == "Gap");
                    // Massimo di TUTTI gli alias [E<n>] del messaggio (non solo delle voci riconosciute da Voices): quello successivo non è fornito.
                    var last = Regex.Matches(r.AllText, @"\[E(\d+)\]").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Max();
                    var bad = scenario switch { "next-after-last" => $"E{last + 1}", "invented" => "E999", _ => voices.Where(v => v.Kind == "Gap").Skip(1).First().Alias };
                    var records = new JsonArray(voices.Where(v => v.Kind == "Gap").Select((g, i) =>
                        (JsonNode)(i == 0 ? Assessment(gap.Alias, "AnsweredInSnapshot", "Answered", [bad], null) : Assessment(g.Alias, "Open", "NoAnswer", [], g.Question))).ToArray());
                    return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = records }.ToJsonString();
                },
            };

            var result = await RunAsync(s.Claim, model);

            var row = await RunRow(s.Run);
            Assert.True((result.Status, result.ErrorCode) == (ProcessingRunStatus.Failed, "work_output_invalid"), $"{scenario}: {result.Status}/{result.ErrorCode} {row.Code} {row.Message}");
            Assert.Single(model.Requests, DocumentGapStageWorkflowTests.IsStage);              // nessun nuovo tentativo né correzione
            Assert.Equal(0, await Outputs(s.Run, "gap-stage-alias-1"));                         // nessun output dello stadio, nessuna valutazione salvata
            Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));
            Assert.Equal(1, await Outputs(s.Run, "retrieval-output-1"));                        // il registro resta consultabile
        }
    }

    // ================================================================ lettura del registro (T033)

    private static string RegistryUrl(Guid project, Guid run) => $"/api/projects/{project}/analyses/{run}/retrieval-registry";

    [Fact]
    public async Task The_registry_endpoint_lists_ordered_candidates_per_gap_with_the_excluded_document_never_among_them()
    {
        var s = await ArrangeAsync();
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(s.Claim, new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond })).Status);
        var registry = await RegistryOf(s.Run);
        using var client = await App.LoginAsync();

        var response = await client.GetAsync(RegistryUrl(s.Project, s.Run), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.Equal((s.Run, CandidateRegistry.SearchBasis, CandidateRegistry.OutcomePackage, RetrievalPolicy.Version1),
            (view.GetProperty("analysisId").GetGuid(), view.GetProperty("searchBasis").GetString(), view.GetProperty("outcome").GetString(), view.GetProperty("policyVersion").GetString()));
        Assert.Equal(registry.RetrievalComplete, view.GetProperty("retrievalComplete").GetBoolean());
        var gaps = view.GetProperty("gaps").EnumerateArray().ToList();
        Assert.Equal(4, gaps.Count);
        Assert.All(gaps, g =>
        {
            var candidates = g.GetProperty("candidates").EnumerateArray().ToList();
            Assert.NotEmpty(candidates);
            Assert.Equal(Enumerable.Range(1, candidates.Count), candidates.Select(c => c.GetProperty("rank").GetInt32()));   // ordine finale, rango 1-based consecutivo
            Assert.All(candidates, c =>
            {
                Assert.NotEqual("garanzie-torino", c.GetProperty("documentName").GetString());                                // documento escluso dal PM mai fra i candidati
                Assert.True(c.GetProperty("rangeEnd").GetInt32() >= c.GetProperty("rangeStart").GetInt32());
                Assert.Equal(1, c.GetProperty("documentVersion").GetInt32());
                Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("section").GetString()));
            });
        });
        // Sola lettura e nessun identificativo tecnico di blocco/hash: unici GUID ammessi sono l'analisi e l'evidenza-Gap.
        var guids = System.Text.RegularExpressions.Regex.Matches(view.GetRawText(), "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Subset(registry.Gaps.Select(g => g.GapEvidenceId.ToString()).Append(s.Run.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase), guids);
        Assert.DoesNotMatch("[0-9a-f]{64}", view.GetRawText());
    }

    [Fact]
    public async Task The_registry_endpoint_is_project_scoped_requires_authentication_and_reports_a_missing_registry_with_a_typed_code()
    {
        var s = await ArrangeAsync();
        using var client = await App.LoginAsync();

        // Run creato ma recupero non ancora eseguito: nessun registro.
        var missing = await client.GetAsync(RegistryUrl(s.Project, s.Run), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("retrieval_registry_not_available", (await Json(missing)).GetProperty("code").GetString());

        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(s.Claim, new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond })).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(RegistryUrl(s.Project, s.Run), Ct)).StatusCode);

        // Progetto diverso e run inesistente: 404 senza rivelare l'esistenza del registro; anonimo: 401.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(RegistryUrl(await NewProject(client), s.Run), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(RegistryUrl(s.Project, Guid.NewGuid()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.Anonymous().GetAsync(RegistryUrl(s.Project, s.Run), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_registry_of_a_diagnostic_run_is_readable_and_never_presented_as_a_valid_result()
    {
        var s = await ArrangeAsync();
        _rerank.RerankOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _rerank.Models = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _rig.Readiness.InvalidateReranker();
        Assert.Equal(ProcessingRunStatus.Failed, (await RunAsync(s.Claim, new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond })).Status);
        using var client = await App.LoginAsync();

        var response = await client.GetAsync(RegistryUrl(s.Project, s.Run), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.Equal(CandidateRegistry.OutcomeDiagnostic, view.GetProperty("outcome").GetString());
        Assert.True(view.GetProperty("degraded").GetBoolean());
        Assert.False(view.GetProperty("retrievalComplete").GetBoolean());
        Assert.Contains(RetrievalException.RerankerUnavailable, view.GetProperty("causes").EnumerateArray().Select(c => c.GetString()));
        Assert.All(view.GetProperty("gaps").EnumerateArray().SelectMany(g => g.GetProperty("candidates").EnumerateArray()), c => Assert.False(c.GetProperty("inPackage").GetBoolean()));
        // Nessun report di coverage né revisione per un run non completato: il registro non diventa mai un risultato valido.
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", s.Run)));
    }
}
