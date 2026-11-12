using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>Supporto condiviso dei test di riconciliazione gerarchica (T078/T079/T080): modello finto reconcile-1, documenti con evidenza ledger e esecuzione del grafo.</summary>
public abstract class ReconcileRig : ArchiveTestBase
{
    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 100_000;
    }

    // ================================================================ modello finto reconcile-1

    internal sealed record Parsed(string BatchId, string InputHash, string Stage, ReconcileView View, int Level, string[] Entries, string[] Nodes, (string Entry, Guid Evidence, string Hash, string Statement, Guid Source)[] Voci, string SourceHash, string? FocusId, string Text);

    internal static Parsed Parse(string text)
    {
        string Line(string key) => Regex.Match(text, $"^{key}: (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        string[] List(string key) => JsonSerializer.Deserialize<string[]>(Regex.Match(text, $@"^{key} \(\d+ elementi[^)]*\): (\[.*\])$", RegexOptions.Multiline).Groups[1].Value)!;
        var voci = Regex.Matches(text, @"<<<VOCE entryId=(\w+) evidenza=([0-9a-f-]{36}) hash=(\w+) [^>]*>>>\n(.*)\n<<<FONTE fonte:([0-9a-f-]{36})")
            .Select(m => (m.Groups[1].Value, Guid.Parse(m.Groups[2].Value), m.Groups[3].Value, m.Groups[4].Value, Guid.Parse(m.Groups[5].Value))).ToArray();
        var focus = Regex.Match(text, "\"candidateId\":\"(\\w+)\"");
        return new(
            Line("batchId"), Line("inputHash"), Line("stage"), Enum.Parse<ReconcileView>(Line("view")), int.Parse(Line("level")), List("visitedEntryIds"), List("visitedNodeIds"), voci,
            Line("sourceHash"), focus.Success ? focus.Groups[1].Value : null, text);
    }

    /// <summary>Risposta reconcile-1 valida: visite identiche all'input, descriptor dove si costruiscono nodi, una proposta per voce nelle foglie della vista A.</summary>
    internal static string Respond(DocumentChatWorkflowTests.ChatRequest request, Func<Parsed, ReconcileResult, ReconcileResult>? change = null)
    {
        var p = Parse(request.AllText);
        var proposals = p.Stage == "leaf" && p.View == ReconcileView.A
            ? p.Voci.Select(v => new ItemProposal
            {
                LocalKey = $"k-{v.Evidence:N}", Text = v.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [v.Evidence], SourceIds = [v.Source],
                // (v.Hash serve solo ai candidati)
            }).ToList()
            : [];
        var result = new ReconcileResult
        {
            BatchId = p.BatchId, InputHash = p.InputHash, View = p.View, Level = p.Level, VisitedEntryIds = p.Entries, VisitedNodeIds = p.Nodes, Candidates = [], Dispositions = [],
            ItemProposals = proposals, Patches = [], Descriptor = p.Stage is "leaf" or "reduce" ? $"gruppo {p.BatchId[..8]}" : null,
            NoFindingsReason = proposals.Count > 0 ? null : "nessun collegamento emerso",
        };
        return ReconciliationCodec.Encode(change?.Invoke(p, result) ?? result);
    }

    protected static ReconcileCandidate Link((Guid Evidence, string Hash) from, (Guid Evidence, string Hash) to, string parent, string batch, string sourceHash)
    {
        var l = ReconciliationFixtures.Endpoint(from.Evidence, from.Hash);
        var r = ReconciliationFixtures.Endpoint(to.Evidence, to.Hash);
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(CandidateKind.Conflict, l, r, ReconcileProposedType.Contradicts, sourceHash), Kind = CandidateKind.Conflict, Left = l, Right = r,
            ProposedType = ReconcileProposedType.Contradicts, Rationale = "30 giorni nel documento contro 45 giorni dichiarati", Basis = EvidenceRelationBasis.Inferred,
            OriginBatchIds = [batch], ParentCandidateId = parent,
        };
    }

    protected async Task<string> Message(Guid run) => await Scalar<string>("SELECT coalesce(\"ErrorCode\",'') || ': ' || coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

    protected sealed record Doc(Guid Document, Guid Version, Guid SourceId);

    protected async Task<Doc> NewDocument(HttpClient client, Guid project, string name, string statement, string? subject = null, string? scopeKey = null, Guid? evidenceId = null, EvidenceKind kind = EvidenceKind.Requirement)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var doc = new Doc(created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid(), Guid.NewGuid());
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == doc.Version, Ct);
        var now = DateTimeOffset.UtcNow;
        var (job, version) = (Guid.NewGuid(), Guid.NewGuid());
        db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
        {
            Id = job, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
            Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        });
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string('1', 64),
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 1, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var content = new DocumentEvidenceContent
        {
            SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
            KindRationale = null,
            Structure = new DocumentEvidenceStructure { Subject = subject, Action = null, Object = null, Scope = scopeKey, Purpose = null, Instrument = null, Channel = null, Recipient = null },
            Qualifiers = [],
            Sources =
            [
                new PersistedSource
                {
                    SourceId = doc.SourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                    MessageId = null, Sequence = null, DocumentId = doc.Document, DocumentVersionId = doc.Version, PreparationId = preparation.Id,
                    ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = statement.Length, Quote = statement,
                },
            ],
        };
        db.Evidences.Add(new EvidenceItem
        {
            Id = evidenceId ?? Guid.NewGuid(), VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = 1,
            Key = "e1", Kind = kind, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement,
            Content = DocumentEvidenceCodec.EncodeContent(content),
        });
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = doc.Version, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = version, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = version;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == doc.Document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, doc.Version), Ct);
        return doc;
    }

    protected async Task<Guid> StartRunAsync(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        // La riconciliazione gerarchica è del contratto v2 (definizione e prompt v2 congelati alla richiesta).
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-analysis-documents-v2', \"PromptVersions\" = '{\"analysis\":\"document-analysis-v2\",\"analysisSchema\":\"1.1.0\"}'::jsonb WHERE \"Id\" = @r",
            ("r", run));
        return run;
    }

    protected async Task<AnalysisRunClaim> ClaimAsync(Guid run)
    {
        await Execute(
            """
            UPDATE "ProcessingRuns" SET "Status" = 'Running', "LeaseOwner" = 'test-owner', "FencingToken" = 1, "AttemptCount" = 1, "StartedAt" = now(),
                   "LeaseUntil" = now() + interval '10 minutes'
            WHERE "Id" = @r AND "Status" = 'Queued'
            """,
            ("r", run));
        return new AnalysisRunClaim(run, "test-owner", 1, 1);
    }

    protected async Task<DocumentAnalysisInput> PrepareAsync(AnalysisRunClaim claim)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var frozen = await new EvidenceLedgerStore(db).FreezeDocumentSelectionAsync(claim, Ct);
        Assert.True(frozen.Outcome is SelectionOutcome.Frozen or SelectionOutcome.Existing, frozen.Outcome.ToString());
        return await DocumentAnalysisSources.LoadAsync(db, claim.RunId, Ct);
    }

    internal async Task<AnalysisRunResult> RunAsync(AnalysisRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model, DocumentOptions? documentOptions = null, int? contextWindow = null, int? maxOutputTokens = null)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = contextWindow ?? DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = maxOutputTokens ?? 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentAnalysisWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(documentOptions ?? new DocumentOptions()));
        return await workflow.ExecuteAsync(claim, Ct);
    }
}

/// <summary>
/// Definizione e versioni congelate dalle nuove richieste di Analizza v4 (T131) fino a prima di T141. Dal T141 il POST reale congela la v5: i test che caratterizzano il
/// comportamento STORICO v4 (single-call alias-1 o gerarchia senza policy Gap) riportano il run creato dall'endpoint a queste chiavi, come un run richiesto allora.
/// Solo fixture: nessun codice di produzione e nessuna asserzione dei test sul comportamento cambia.
/// </summary>
internal static class HistoricalAnalysisV4
{
    public const string Definition = "project-analysis-documents-v4";

    public const string Versions =
        """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v2","validation":"document-analysis-validation-v2","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1"}""";

    public static string Sql => "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = '" + Definition + "', \"PromptVersions\" = @v::jsonb WHERE \"Id\" = @r";
}
