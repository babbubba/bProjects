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

/// <summary>
/// T078 (PG + modello FINTO) — la riconciliazione gerarchica collegata al grafo fisso T043, al port T044, a WorkOutput/DerivedBatch e alle proiezioni T122.
/// Un'analisi oltre la capacità di una chiamata percorre più batch, ciascuno in una sessione nuova (un solo messaggio, nessuna storia accumulata); ripresa e
/// replay riusano gli output Committed senza nodi nuovi; una promozione valida mantiene t0 e cap; non esiste un Completed parziale.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentReconcileWorkflowTests : ArchiveTestBase
{
    private const int Documents = 8;

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 100_000;
    }

    private static string Statement(int n) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, 120).Select(i => $"w{n}x{i}"));

    // ================================================================ modello finto reconcile-1

    private sealed record Parsed(string BatchId, string InputHash, string Stage, ReconcileView View, int Level, string[] Entries, string[] Nodes, (string Entry, Guid Evidence, string Hash, string Statement, Guid Source)[] Voci, string SourceHash, string? FocusId);

    private static Parsed Parse(string text)
    {
        string Line(string key) => Regex.Match(text, $"^{key}: (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        string[] List(string key) => JsonSerializer.Deserialize<string[]>(Regex.Match(text, $@"^{key} \(\d+ elementi[^)]*\): (\[.*\])$", RegexOptions.Multiline).Groups[1].Value)!;
        var voci = Regex.Matches(text, @"<<<VOCE entryId=(\w+) evidenza=([0-9a-f-]{36}) hash=(\w+) [^>]*>>>\n(.*)\n<<<FONTE fonte:([0-9a-f-]{36})")
            .Select(m => (m.Groups[1].Value, Guid.Parse(m.Groups[2].Value), m.Groups[3].Value, m.Groups[4].Value, Guid.Parse(m.Groups[5].Value))).ToArray();
        var focus = Regex.Match(text, "\"candidateId\":\"(\\w+)\"");
        return new(
            Line("batchId"), Line("inputHash"), Line("stage"), Enum.Parse<ReconcileView>(Line("view")), int.Parse(Line("level")), List("visitedEntryIds"), List("visitedNodeIds"), voci,
            Line("sourceHash"), focus.Success ? focus.Groups[1].Value : null);
    }

    /// <summary>Risposta reconcile-1 valida: visite identiche all'input, descriptor dove si costruiscono nodi, una proposta per voce nelle foglie della vista A.</summary>
    private static string Respond(DocumentChatWorkflowTests.ChatRequest request, Func<Parsed, ReconcileResult, ReconcileResult>? change = null)
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

    // ================================================================ scenari

    [Fact]
    public async Task An_analysis_above_one_call_runs_isolated_batches_and_commits_the_revision_with_projections_atomically()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.True(model.Requests.Count > 1);
        Assert.All(model.Requests, r => Assert.Single(r.Messages));   // sessione nuova per invio: nessuna storia accumulata
        var parsed = model.Requests.Select(r => Parse(r.AllText)).ToList();
        Assert.Equal(parsed.Count, parsed.Select(p => p.BatchId).Distinct().Count());
        Assert.Equal(Documents, parsed.Where(p => p.Stage == "leaf" && p.View == ReconcileView.A).SelectMany(p => p.Voci).Select(v => v.Evidence).Distinct().Count());
        Assert.Contains(parsed, p => p.Stage == "cross");
        // Nessun batch porta il testo delle voci di un altro: ogni enunciato compare solo nelle richieste che lo riaprono.
        Assert.All(parsed.Where(p => p.Stage is "reduce" or "cross"), p => Assert.Empty(p.Voci));

        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"SchemaVersion\" = '1.1.0'", ("r", run)));
        Assert.Equal(Documents, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(2 * Documents, await Count("SELECT count(*) FROM \"RunCatalogMembers\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.True(await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)) >= 3);
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(Documents, content.Requirements.Count);
        Assert.Empty(content.Conflicts);
    }

    [Fact]
    public async Task Replaying_a_completed_run_and_resuming_an_interrupted_one_send_nothing_already_committed()
    {
        var (run, claim) = await ArrangeAsync();
        var seen = 0;
        var failing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Interlocked.Increment(ref seen) == 4 ? throw new InvalidOperationException("rete caduta") : Respond(r),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        Assert.Equal(3, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

        var healthy = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        var all = healthy.Requests.Count + 3;
        Assert.Equal(all, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(all, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", run)));
        var committedBefore = failing.Requests.Take(3).Select(r => Parse(r.AllText).BatchId).ToHashSet();
        Assert.Equal(3, committedBefore.Count);
        Assert.DoesNotContain(healthy.Requests.Select(r => Parse(r.AllText).BatchId), committedBefore.Contains);   // nessun batch già Committed è reinviato

        var again = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };
        var replay = await RunAsync(claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_valid_output_that_leaves_an_evidence_unmapped_is_kept_but_never_completes_the_run()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Respond(r, (p, result) => result with { ItemProposals = [.. result.ItemProposals.Skip(1)], NoFindingsReason = result.ItemProposals.Count > 1 ? null : "nessun collegamento emerso" }),
        };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task An_invalid_batch_output_is_a_permanent_error_with_no_work_output_and_no_repair()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Respond(r, (p, result) => p.Stage == "reduce" ? result with { VisitedNodeIds = [] } : result),
        };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Equal(model.Requests.Count - 1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task N4_regression_an_open_30_vs_45_conflict_proposed_by_the_fake_model_is_kept_as_an_open_conflict_with_both_alternatives()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var known = new Dictionary<int, (Guid Evidence, string Hash)>();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Respond(r, (p, result) =>
            {
                foreach (var v in p.Voci)
                {
                    known[int.Parse(Regex.Match(v.Statement, @"Requisito (\d+):").Groups[1].Value)] = (v.Evidence, v.Hash);
                }

                return p.Stage switch
                {
                    // L'espansione che riapre il documento 1 trova il collegamento con il documento 8, che sta in un'altra foglia (documenti e posizioni lontani).
                    "expand" when p.Voci.Any(v => v.Statement.StartsWith("Requisito 1:", StringComparison.Ordinal)) && known.ContainsKey(8) =>
                        result with { Candidates = [Link(known[1], known[8], p.FocusId!, p.BatchId, p.SourceHash)], NoFindingsReason = null },
                    "compare" => result with
                    {
                        Dispositions =
                        [
                            new CandidateDisposition
                            {
                                CandidateId = p.FocusId!, State = CandidateState.Open, ChildCandidateIds = [], ComparisonOutputHashes = [p.InputHash],
                                Reason = "30 giorni nel documento contro 45 giorni dichiarati: alternative integrali da sottoporre al PM",
                            },
                        ],
                    },
                    _ => result,
                };
            }),
        };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var conflict = Assert.Single(content.Conflicts);
        Assert.Equal(AnalysisConflictStatus.Open, conflict.Status);
        Assert.Null(conflict.Resolution);
        Assert.Equal(2, conflict.Alternatives.Count);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"RunCandidateDispositions\" WHERE \"RunId\" = @r AND \"State\" = 'Open'", ("r", run)));
    }

    private static ReconcileCandidate Link((Guid Evidence, string Hash) from, (Guid Evidence, string Hash) to, string parent, string batch, string sourceHash)
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

    [Fact]
    public async Task A_valid_promotion_keeps_t0_and_the_hard_cap_and_does_not_reset_the_counters()
    {
        var (run, claim) = await ArrangeAsync(ordinary: true);
        var before = await Scalar<DateTime>("SELECT \"RequestedAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        var hardBefore = await Scalar<DateTime>("SELECT \"HardDeadlineAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Respond(r) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(("Extended", 1), (await Scalar<string>("SELECT \"Regime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), await Scalar<int>("SELECT \"PromotionCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))));
        Assert.Equal(before, await Scalar<DateTime>("SELECT \"RequestedAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(hardBefore, await Scalar<DateTime>("SELECT \"HardDeadlineAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"RegimeTransitions\" WHERE \"RootId\" = @r AND \"Reason\" = 'additional_work'", ("r", run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal("Ordinary", await Scalar<string>("SELECT \"InitialRegime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_payload_that_fits_in_one_call_still_uses_the_single_call_path_without_hierarchical_batches()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "unico", "Il cliente A puo emettere al massimo 5 ordini al giorno.");
        var run = await StartRunAsync(client, project);
        var claim = await ClaimAsync(run);
        var input = await PrepareAsync(claim);
        var single = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => ReconciliationCodec.Encode(SingleCall(input)) };

        var result = await RunAsync(claim, single);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Single(single.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)));
    }

    private static SingleCallOutput SingleCall(DocumentAnalysisInput input)
    {
        var e = input.Evidence[0];
        var s = input.Snapshot.Evidence[0].Sources[0];
        ReconcileResult Result(ReconcileView view, bool item) => new()
        {
            BatchId = input.BatchKey, InputHash = input.InputHash, View = view, Level = 0,
            VisitedEntryIds = [ReconciliationIds.EntryId(input.Snapshot.SourceHash, e.EvidenceId, e.ContentHash)], VisitedNodeIds = [], Candidates = [], Dispositions = [],
            ItemProposals = item ? [new ItemProposal { LocalKey = "r1", Text = e.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [e.EvidenceId], SourceIds = [s.SourceId] }] : [],
            Patches = [], Descriptor = null, NoFindingsReason = item ? null : "nessun collegamento",
        };
        var itemId = Guid.Parse("40000000-0000-4000-8000-000000000001");
        var analysis = new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = input.AnalysisId, ProjectId = input.ProjectId, SourceCutoffSequence = input.Cutoff,
            ValidationState = AnalysisContentState.Unvalidated,
            Sources = [new DocumentAnalysisSource
            {
                Id = s.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = s.DocumentId, DocumentVersionId = s.DocumentVersionId,
                PreparationId = s.PreparationId, ArtifactId = s.ArtifactId, BlockId = s.BlockId, Start = s.Start, End = s.End, Quote = s.Quote,
            }],
            Goals = [], ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = [new DocumentAnalysisItem { Id = itemId, Text = e.Statement, Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [s.SourceId] }],
            OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
        };
        return new SingleCallOutput
        {
            Results = [Result(ReconcileView.A, true), Result(ReconcileView.B, false), Result(ReconcileView.Cross, false)], Analysis = analysis,
            EvidenceMap = [new EvidenceMapEntry { ItemId = itemId, EvidenceIds = [e.EvidenceId] }],
        };
    }

    // ================================================================ supporto: dati e esecuzione

    private async Task<string> Message(Guid run) => await Scalar<string>("SELECT coalesce(\"ErrorCode\",'') || ': ' || coalesce(\"ErrorMessage\",'') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

    private async Task<(Guid Run, AnalysisRunClaim Claim)> ArrangeAsync(bool ordinary = false, bool sharedKey = false)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 1; n <= Documents; n++)
        {
            // Soggetti distinti ordinano le evidenze in A; con sharedKey i documenti 1 e 8 (primo e ultimo) condividono la chiave di scope e cadono in foglie diverse.
            var subject = n == 1 ? "aaa" : n == Documents ? "zzz" : $"mmm{n}";
            await NewDocument(client, project, $"doc{n}", Statement(n), subject, sharedKey && n is 1 or Documents ? "regola" : null);
        }

        var run = await StartRunAsync(client, project);
        if (ordinary)
        {
            await Execute(
                """
                UPDATE "ProcessingRuns" SET "Regime" = 'Ordinary', "InitialRegime" = 'Ordinary', "ClassificationStatus" = 'Classified', "PromotionCount" = 0,
                       "OrdinaryDeadlineAt" = "RequestedAt" + interval '180 seconds', "EffectiveDeadlineAt" = "RequestedAt" + interval '180 seconds',
                       "DeadlineAt" = "RequestedAt" + interval '180 seconds', "HardDeadlineAt" = "RequestedAt" + interval '3600 seconds', "CallCount" = 0,
                       "PolicyHash" = repeat('a', 64)
                WHERE "Id" = @r
                """,
                ("r", run));
        }

        return (run, await ClaimAsync(run));
    }

    private sealed record Doc(Guid Document, Guid Version, Guid SourceId);

    private async Task<Doc> NewDocument(HttpClient client, Guid project, string name, string statement, string? subject = null, string? scopeKey = null)
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
            Id = Guid.NewGuid(), VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = 1,
            Key = "e1", Kind = EvidenceKind.Requirement, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement,
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

    private async Task<Guid> StartRunAsync(HttpClient client, Guid project)
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

    private async Task<AnalysisRunClaim> ClaimAsync(Guid run)
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

    private async Task<DocumentAnalysisInput> PrepareAsync(AnalysisRunClaim claim)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var frozen = await new EvidenceLedgerStore(db).FreezeDocumentSelectionAsync(claim, Ct);
        Assert.True(frozen.Outcome is SelectionOutcome.Frozen or SelectionOutcome.Existing, frozen.Outcome.ToString());
        return await DocumentAnalysisSources.LoadAsync(db, claim.RunId, Ct);
    }

    private async Task<AnalysisRunResult> RunAsync(AnalysisRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentAnalysisWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions()));
        return await workflow.ExecuteAsync(claim, Ct);
    }
}
