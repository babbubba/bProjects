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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-041 (M4) — workflow v11 COMPLETO con PostgreSQL e MAF 1.26.0, modello FINTO e fixture ERP/EDI rappresentativa (RH-040), profilo W=32768, O=16384, M=1024. È una prova
/// STRUTTURALE: dimostra che la gerarchia v11 arriva a <c>Completed</c> (o a un limite tipizzato) senza perdere evidenze, senza duplicare effetti e con ripresa deterministica; NON dimostra la correttezza
/// semantica del ragionamento generativo (conflitti e Gap sono dichiarati da uno script scritto dal test, non scoperti da un modello): quella si misura solo in M5 con il modello reale. Il tokenizer del modello
/// finto di <see cref="DocumentChatWorkflowTests.FakeChatModel"/> conta parole, non caratteri/3,5 come il runtime di RH-004: i budget misurati qui sono quelli del rig, dichiarati nel rapporto. Servono
/// BPROJECTS_TEST_POSTGRES; senza, Skipped.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentReconcileV11FlowTests : ReconcileRig
{
    private const int Window = 32768;
    private const int Output = 16384;
    private const int Margin = 1024;

    // ================================================================ script del modello finto

    /// <summary>Coppie in conflitto dello script, per enunciato: i due conflitti di versione del gold e la coppia con un Gap estremo (R-D2b-1: PM-04 contro FI-03).</summary>
    internal static IReadOnlyList<(string Left, string Right, bool GapEndpoint)> ScriptedPairs()
    {
        var pairs = RepresentativeLedger.Gold.Conflicts.Select(c => (RepresentativeLedger.Row(c.Older).Statement, RepresentativeLedger.Row(c.Newer).Statement, false)).ToList();
        pairs.Add((RepresentativeLedger.Row("PM-04").Statement, RepresentativeLedger.Row("FI-03").Statement, true));
        return pairs;
    }

    private static JsonObject Link(string key, string left, string right) => new()
    {
        ["key"] = key, ["left"] = left, ["right"] = right, ["type"] = "Contradicts", ["basis"] = "Inferred", ["rationale"] = "le due versioni stabiliscono valori diversi per la stessa regola",
    };

    /// <summary>
    /// Gerarchia: foglie A senza item (li compone il server) e solo descriptor; le foglie B, il cross e l'espansione dichiarano le coppie dello script quando entrambi gli estremi sono visibili; il confronto è
    /// <c>Open</c> per le coppie dello script e <c>Compared</c> altrimenti. Con <paramref name="perBatch"/> &gt; 0 ogni batch con candidati ne emette quel numero (inflazione).
    /// </summary>
    internal static string Hierarchy(DocumentChatWorkflowTests.ChatRequest r, IReadOnlyList<(string Left, string Right, bool GapEndpoint)> script, int perBatch = 0)
    {
        var p = DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText);
        var obj = ReconcileContractMatrixTests.Dto(
            noFindings: "nessun collegamento emerso", descriptor: p.Stage is "leaf" or "reduce" ? $"gruppo {p.Stage} {p.View}: {p.Voci.Length} voci e {p.Nodes.Length} nodi" : null);
        var offersCandidates = (p is { Stage: "leaf", View: "B" }) || p.Stage is "cross" or "expand";
        if (offersCandidates)
        {
            var aliasOf = p.Voci.GroupBy(v => v.Statement).ToDictionary(g => g.Key, g => g.First().Alias, StringComparer.Ordinal);
            var candidates = new JsonArray();
            var n = 0;
            if (perBatch > 0)
            {
                var aliases = p.Voci.Select(v => v.Alias).Distinct().ToList();
                for (var i = 0; i < aliases.Count && candidates.Count < perBatch; i++)
                {
                    for (var j = i + 1; j < aliases.Count && candidates.Count < perBatch; j++)
                    {
                        candidates.Add(Link($"f{++n}", aliases[i], aliases[j]));
                    }
                }
            }
            else
            {
                foreach (var (left, right, _) in script)
                {
                    if (aliasOf.TryGetValue(left, out var a) && aliasOf.TryGetValue(right, out var b))
                    {
                        candidates.Add(Link($"f{++n}", a, b));
                    }
                }
            }

            if (candidates.Count > 0)
            {
                obj["candidates"] = candidates;
                obj["noFindings"] = null;
            }
        }
        else if (p.Stage == "compare")
        {
            var open = script.Any(s => r.AllText.Contains(s.Left, StringComparison.Ordinal) && r.AllText.Contains(s.Right, StringComparison.Ordinal));
            obj["focus"] = JsonNode.Parse(open
                ? """{"outcome":"Open","reason":"le due versioni stabiliscono valori diversi: alternative integrali da sottoporre al PM"}"""
                : """{"outcome":"Compared","reason":null}""");
            obj["noFindings"] = null;
        }

        return obj.ToJsonString();
    }

    /// <summary>Stadio Gap: ogni Gap è Open/NoAnswer sulla propria domanda canonica.</summary>
    internal static string GapStage(DocumentChatWorkflowTests.ChatRequest r)
    {
        var view = DocumentGapStageWorkflowTests.ParseStage(r.AllText);
        var records = view.Gaps.Select(g => (JsonNode)new JsonObject
        {
            ["gap"] = g.Alias, ["outcome"] = "Open", ["reason"] = "NoAnswer", ["rationale"] = "Nessuna evidenza risponde.", ["supportingEvidence"] = new JsonArray(),
            ["openItem"] = g.Question, ["openConflict"] = null,
        });
        return new JsonObject { ["contract"] = "gap-stage-1", ["gapAssessments"] = new JsonArray([.. records]) }.ToJsonString();
    }

    private static DocumentChatWorkflowTests.FakeChatModel Model(IReadOnlyList<(string Left, string Right, bool GapEndpoint)> script, int perBatch = 0, Func<DocumentChatWorkflowTests.ChatRequest, string?>? fault = null) =>
        new()
        {
            ContextWindow = Window,
            Responder = r => fault?.Invoke(r) ?? (DocumentGapStageWorkflowTests.IsStage(r) ? GapStage(r) : Hierarchy(r, script, perBatch)),
        };

    // ================================================================ seminagione

    private sealed record DocSeed(string Key, Guid Document, Guid Version, Guid Preparation, Guid Artifact, IReadOnlyList<Guid> Blocks, Guid ExtractionVersion);

    private sealed record EvSeed(
        string Ref, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement, string DocKey, int Block, int Start, int End, string Quote,
        string? Subject, string? Scope, (string DocKey, int Block, int Start, int End, string Quote)? Extra);

    private async Task<DocSeed> CreateDocumentAsync(HttpClient client, Guid project, string key, IReadOnlyList<string> blockTexts, int evidenceCount)
    {
        var created = await Json(await Upload(client, project, Text(key), key + ".txt", displayName: key));
        var (documentId, versionId) = (created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == versionId, Ct);
        var (artifact, job, version) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var blocks = blockTexts.Select(_ => Guid.NewGuid()).ToList();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync(Ct);
            await using var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            seed.ParsedArtifacts.Add(new ParsedArtifact
            {
                Id = artifact, ProjectId = preparation.ProjectId, PreparationId = preparation.Id, JobId = Guid.NewGuid(), FencingToken = 1, SchemaVersion = "parsed-1.0.0",
                StorageKey = "k/" + Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64), ByteLength = 10, ParserConfigHash = new string('b', 64), BlockCount = blockTexts.Count,
            });
            for (var i = 0; i < blockTexts.Count; i++)
            {
                seed.DocumentBlocks.Add(new DocumentBlock
                {
                    Id = blocks[i], ProjectId = preparation.ProjectId, ArtifactId = artifact, Ordinal = i, Ref = $"b{i}", Type = BlockType.Paragraph, CanonicalText = blockTexts[i],
                    TextHash = DocumentTestFixtures.Sha256(blockTexts[i]), HeadingPathJson = JsonSerializer.Serialize(new[] { key }),
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
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = evidenceCount, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        return new DocSeed(key, documentId, versionId, preparation.Id, artifact, blocks, version);
    }

    private async Task AddEvidenceAsync(Guid project, IReadOnlyDictionary<string, DocSeed> docs, IReadOnlyList<EvSeed> items, IReadOnlyDictionary<string, Guid> ids)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ordinals = new Dictionary<string, int>();
        PersistedSource SourceOf(string tag, string docKey, int block, int start, int end, string quote)
        {
            var d = docs[docKey];
            return new PersistedSource
            {
                SourceId = MeasurementLedger.Id($"flow|src|{project}|{tag}"), Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = MeasurementLedger.Id($"flow|grp|{project}|{tag}"), Part = 0,
                MessageId = null, Sequence = null, DocumentId = d.Document, DocumentVersionId = d.Version, PreparationId = d.Preparation, ArtifactId = d.Artifact, BlockId = d.Blocks[block],
                Start = start, End = end, Quote = quote,
            };
        }

        foreach (var e in items)
        {
            var d = docs[e.DocKey];
            ordinals[e.DocKey] = ordinals.GetValueOrDefault(e.DocKey) + 1;
            var sources = new List<PersistedSource> { SourceOf(e.Ref + "|0", e.DocKey, e.Block, e.Start, e.End, e.Quote) };
            if (e.Extra is { } x)
            {
                sources.Add(SourceOf(e.Ref + "|x", x.DocKey, x.Block, x.Start, x.End, x.Quote));
            }

            var content = new DocumentEvidenceContent
            {
                SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
                KindRationale = null,
                Structure = new DocumentEvidenceStructure { Subject = e.Subject, Action = null, Object = null, Scope = e.Scope, Purpose = null, Instrument = null, Channel = null, Recipient = null },
                Qualifiers = [],
                Sources = sources,
            };
            db.Evidences.Add(new EvidenceItem
            {
                Id = ids[e.Ref], VersionId = d.ExtractionVersion, ProjectId = project, OriginKind = EvidenceOriginKind.Document,
                PreparationId = d.Preparation, Ordinal = ordinals[e.DocKey], Key = "e" + ordinals[e.DocKey], Kind = e.Kind, Polarity = e.Polarity, Certainty = e.Certainty, Statement = e.Statement,
                Content = DocumentEvidenceCodec.EncodeContent(content),
            });
        }

        await db.SaveChangesAsync(Ct);
        foreach (var d in docs.Values)
        {
            var preparation = await db.DocumentPreparations.SingleAsync(p => p.Id == d.Preparation, Ct);
            db.EvidenceDocumentStates.Add(new EvidenceDocumentState
            {
                DocumentVersionId = d.Version, ProjectId = preparation.ProjectId, CurrentPreparationId = d.Preparation, CurrentEvidenceVersionId = d.ExtractionVersion, Revision = 1, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
            preparation.Status = PreparationStatus.Ready;
            preparation.ParserVersion = "parser-1";
            preparation.CurrentEvidenceVersionId = d.ExtractionVersion;
            await db.SaveChangesAsync(Ct);
            await db.ProjectDocuments.Where(x => x.Id == d.Document).ExecuteUpdateAsync(x => x.SetProperty(y => y.LatestReadyVersionId, d.Version), Ct);
        }
    }

    /// <summary>Come <see cref="EvidenceTestSeed.CompleteAsync(IServiceProvider, CancellationToken, bool)"/>, ma l'uscita di estrazione di ogni messaggio PM è quella annotata dal ledger (tipo, certezza, citazione).</summary>
    private async Task CompletePmExtractionAsync(IReadOnlyList<RepresentativeRow> pmRows)
    {
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var policy = scope.ServiceProvider.GetService<IOptions<ExecutionOptions>>()?.Value ?? new ExecutionOptions();
            var ai = scope.ServiceProvider.GetService<IOptions<AiOptions>>()?.Value ?? new AiOptions { ModelId = "seed" };
            var conversations = await db.Conversations.AsNoTracking().Select(c => new { c.Id, c.ProjectId }).ToListAsync(Ct);
            var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(Ct)).Single();
            foreach (var c in conversations)
            {
                await new EvidenceLedgerStore(db).EnqueueAsync(c.ProjectId, c.Id, long.MaxValue, onlyMissing: true, ExtractionJobReason.Backfill, null, null, now, policy, EvidenceLedgerStore.ExtractorConfig(ai, policy), Ct);
            }

            await db.SaveChangesAsync(Ct);
        }

        while (true)
        {
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextWorkAsync($"seed:{Guid.NewGuid():N}", turns: false, runs: false, documents: false, extractions: true, Ct);
            if (work?.Extraction is not { } claim)
            {
                return;
            }

            var ledger = new EvidenceLedgerStore(db);
            var context = await ledger.LoadContextAsync(claim.JobId, Ct);
            var body = context.Target.Body;
            var mine = pmRows.Where(r => body.Contains(r.Quote, StringComparison.Ordinal)).ToList();
            var evidences = mine.Select((r, i) => new ExtractedEvidence
            {
                Key = "e" + (i + 1), Sources = [new ExtractedSource { MessageId = context.Target.Id, Quote = r.Quote, Occurrence = 1, Role = EvidenceSourceRole.Primary }], Kind = r.Kind, KindRationale = null,
                Statement = r.Statement, Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null, Qualifiers = [],
                Polarity = r.Polarity, Certainty = r.Certainty, Relations = [],
            }).ToList();
            var output = new ExtractionOutput { NoEvidenceReason = evidences.Count == 0 ? "nessuna" : null, Evidences = evidences };
            var validation = EvidenceExtractionValidator.Validate(output, context);
            Assert.True(validation.Accepted, string.Join(" | ", validation.Violations));
            var result = await ledger.CommitAsync(claim, context, validation, "seed", """{"seed":true}""", Ct);
            Assert.Equal(ExtractionCommitOutcome.Committed, result.Outcome);
        }
    }

    private sealed record Arranged(Guid Run, AnalysisRunClaim Claim, Guid Project, HttpClient Client, IReadOnlyDictionary<string, Guid> Ids);

    private static (string? Subject, string? Scope) StructureOf(string reference)
    {
        var pairs = ScriptedPairs();
        for (var i = 0; i < pairs.Count; i++)
        {
            var (left, right, _) = pairs[i];
            if (RepresentativeLedger.Row(reference).Statement == left || RepresentativeLedger.Row(reference).Statement == right)
            {
                return ($"coppia-{i + 1}", $"regola-{i + 1}");
            }
        }

        return (null, null);
    }

    /// <summary>Documenti af e verbale (un blocco per file, evidenze con offset reali nel file), tre messaggi PM con evidenze estratte dal percorso reale e run v11 congelato esplicitamente (default invariato, v6).</summary>
    private async Task<Arranged> ArrangeRepresentativeAsync(Func<RepresentativeRow, bool>? keep = null)
    {
        var client = await App.LoginAsync();
        var project = await NewProject(client);
        var rows = RepresentativeLedger.Rows.Where(r => keep?.Invoke(r) ?? true).ToList();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var docs = new Dictionary<string, DocSeed>(StringComparer.Ordinal);
        foreach (var (key, file) in new[] { ("af", RepresentativeLedger.Af), ("verbale", RepresentativeLedger.Verbale) })
        {
            docs[key] = await CreateDocumentAsync(client, project, key, [RepresentativeLedger.Read(file)], rows.Count(r => r.Doc == key));
        }

        var items = new List<EvSeed>();
        foreach (var r in rows.Where(r => r.Doc != "pm"))
        {
            ids[r.Ref] = MeasurementLedger.Id($"flow|ev|{project}|{r.Ref}");
            var (subject, scope) = StructureOf(r.Ref);
            (string, int, int, int, string)? extra = null;
            if (r.ExtraRef is { } er && rows.Any(x => x.Ref == er))
            {
                var x = RepresentativeLedger.Row(er);
                extra = (x.Doc, 0, x.Start, x.End, x.Quote);
            }

            items.Add(new EvSeed(r.Ref, r.Kind, r.Polarity, r.Certainty, r.Statement, r.Doc, 0, r.Start, r.End, r.Quote, subject, scope, extra));
        }

        await AddEvidenceAsync(project, docs, items, ids);
        foreach (var m in RepresentativeLedger.Messages)
        {
            var post = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text = m.Text }, Ct);
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        }

        await CompletePmExtractionAsync([.. rows.Where(r => r.Doc == "pm")]);

        var (run, claim) = await StartV11Async(client, project);
        using var scope2 = App.Services.CreateScope();
        var all = await scope2.ServiceProvider.GetRequiredService<AppDbContext>().Evidences.AsNoTracking().Where(e => e.ProjectId == project).Select(e => new { e.Id, e.Statement }).ToListAsync(Ct);
        var byStatement = all.ToDictionary(e => e.Statement, e => e.Id, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            ids[r.Ref] = byStatement[r.Statement];
        }

        return new Arranged(run, claim, project, client, ids);
    }

    private async Task<(Guid Run, AnalysisRunClaim Claim)> StartV11Async(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        Assert.Equal(SourceSnapshotStore.AnalysisDefinitionV6, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));   // default invariato
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = CAST(@p AS jsonb) WHERE \"Id\" = @r",
            ("d", DocumentAnalysisWorkflow.DefinitionVersionV11), ("p", SourceSnapshotStore.AnalysisPromptVersionsV11()), ("r", run));
        return (run, await ClaimAsync(run));
    }

    // ================================================================ utilità di asserzione

    private Task<AnalysisRunResult> RunV11Async(AnalysisRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model) =>
        RunAsync(claim, model, contextWindow: Window, maxOutputTokens: Output, safetyMarginTokens: Margin);

    private async Task<DocumentAnalysisContent> ContentAsync(Guid run) =>
        DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

    private Task<long> Committed(Guid run, string? schema = null) => Count(
        "SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'" + (schema is null ? "" : " AND \"SchemaVersion\" = '" + schema + "'"), ("r", run));

    private static IEnumerable<DocumentAnalysisItem> SectionItems(DocumentAnalysisContent c) =>
        c.Goals.Concat(c.ContextAndScope.Overview).Concat(c.ContextAndScope.Included).Concat(c.ContextAndScope.Excluded).Concat(c.Requirements).Concat(c.OperationalFlows).Concat(c.Constraints)
            .Concat(c.Dependencies).Concat(c.Assumptions).Concat(c.OpenQuestions);

    /// <summary>Ogni evidenza del ledger: esattamente un item proiettato oppure solo alternativa di un conflitto (D2b); un Gap o ToClarify estremo di un conflitto Open compare come domanda E come alternativa (R-D2b-1).</summary>
    private static void AssertNoEvidenceLost(DocumentAnalysisContent content, IReadOnlyDictionary<string, string> quoteOf, IReadOnlySet<string> allowedBoth)
    {
        // Gli `EvidenceRefs` degli item sono id di FONTE: ogni evidenza si riconosce dalla fonte primaria (citazione letterale).
        var unresolved = quoteOf.Where(kv => content.Sources.All(s => s.Quote != kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(unresolved.Count == 0, $"{unresolved.Count} evidenze senza fonte nel contenuto ({content.Sources.Count} fonti): {string.Join(", ", unresolved.Take(20))}");
        var sourceOf = quoteOf.ToDictionary(kv => kv.Key, kv => content.Sources.First(s => s.Quote == kv.Value).Id);
        var projectedBySource = SectionItems(content).SelectMany(i => i.EvidenceRefs).GroupBy(g => g).ToDictionary(g => g.Key, g => g.Count());
        var alternatives = content.Conflicts.SelectMany(c => c.Alternatives).SelectMany(a => a.EvidenceRefs).ToHashSet();
        var ids = sourceOf;
        var lost = ids.Where(kv => projectedBySource.GetValueOrDefault(kv.Value) + (alternatives.Contains(kv.Value) ? 1 : 0) == 0).Select(kv => kv.Key).ToList();
        Assert.True(lost.Count == 0, $"{lost.Count} evidenze perse silenziosamente su {ids.Count}: {string.Join(", ", lost.Take(15))}; sezioni: req {content.Requirements.Count}, ass {content.Assumptions.Count}, oq {content.OpenQuestions.Count}, ctx {content.ContextAndScope.Overview.Count}/{content.ContextAndScope.Included.Count}/{content.ContextAndScope.Excluded.Count}, goals {content.Goals.Count}, flows {content.OperationalFlows.Count}, vincoli {content.Constraints.Count}, dip {content.Dependencies.Count}");
        foreach (var (reference, source) in ids)
        {
            var inSections = projectedBySource.GetValueOrDefault(source);
            var inAlternative = alternatives.Contains(source);
            Assert.True(inSections <= 1, $"evidenza {reference} proiettata {inSections} volte");
            if (inSections == 1 && inAlternative)
            {
                Assert.Contains(reference, allowedBoth);
            }
        }
    }

    /// <summary>Evidenze numeriche della prova (non è un'asserzione): file in %TEMP%/bprojects-reconcile-measurement/rh041-evidence.md.</summary>
    private static void Record(string line)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bprojects-reconcile-measurement");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "rh041-evidence.md"), line + Environment.NewLine);
    }

    private static string StageCounts(DocumentChatWorkflowTests.FakeChatModel model) =>
        string.Join(", ", model.Requests.GroupBy(r => DocumentGapStageWorkflowTests.IsStage(r) ? "gap" : DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText).Stage).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

    // ================================================================ A — gerarchia + stadio Gap sul ledger rappresentativo

    [Fact]
    public async Task A_representative_ledger_with_a_gap_endpoint_of_an_open_conflict_completes_through_hierarchy_and_gap_stage_and_replays_without_inference()
    {
        var a = await ArrangeRepresentativeAsync();
        using var _ = a.Client;
        var script = ScriptedPairs();
        var model = Model(script);

        var result = await RunV11Async(a.Claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(a.Run));
        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersionV11, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", a.Run)));

        // Ramo di instradamento: gerarchia (prima foglia A) e stadio Gap come ultima chiamata; mai il single-call.
        var hierarchy = model.Requests.Where(r => !DocumentGapStageWorkflowTests.IsStage(r)).ToList();
        Assert.Matches(new Regex(@"^Tipo di batch: foglia\n", RegexOptions.Multiline), hierarchy[0].AllText);
        Assert.Single(model.Requests, DocumentGapStageWorkflowTests.IsStage);
        Assert.True(DocumentGapStageWorkflowTests.IsStage(model.Requests[^1]));
        Assert.Equal("gap", await Scalar<string>("SELECT \"Stage\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r ORDER BY \"Ordinal\" DESC LIMIT 1", ("r", a.Run)));
        Assert.Contains(hierarchy, r => DocumentReconcileAliasWorkflowTests.ParseAlias(r.AllText).Stage == "compare");
        Assert.InRange(model.Requests.Count, 2, 512);
        Record($"| A | {RepresentativeLedger.Rows.Count} evidenze | Completed | chiamate {model.Requests.Count} ({StageCounts(model)}) | batch committati {await Committed(a.Run)} |");

        var content = await ContentAsync(a.Run);

        // Ogni Gap ha la sua domanda canonica con il testo dell'enunciato.
        foreach (var gap in RepresentativeLedger.Gold.Gaps)
        {
            Assert.Single(content.OpenQuestions, q => q.Text == RepresentativeLedger.Row(gap.Ref).Statement);
        }

        // Rischi semantici di M2 (D2b-2, leaf-projection-1) sul ledger rappresentativo: ToClarify e Alternative restano domande aperte (mai decisioni acquisite), le ipotesi sono assunzioni `Assumed`,
        // le deduzioni mantengono la classificazione `Inferred`.
        foreach (var row in RepresentativeLedger.Rows.Where(r => r.Certainty == EvidenceCertainty.ToClarify || r.Kind == EvidenceKind.Alternative && r.Certainty is not (EvidenceCertainty.Hypothesized or EvidenceCertainty.ToClarify)))
        {
            Assert.Contains(content.OpenQuestions, q => q.Text == row.Statement);
            Assert.DoesNotContain(content.Requirements, i => i.Text == row.Statement);
        }

        foreach (var row in RepresentativeLedger.Rows.Where(r => r.Certainty == EvidenceCertainty.Hypothesized && r.Kind != EvidenceKind.Gap))
        {
            Assert.Contains(content.Assumptions, i => i.Text == row.Statement && i.Classification == DocumentAnalysisClassification.Assumed);
        }

        foreach (var row in RepresentativeLedger.Rows.Where(r => r.Certainty == EvidenceCertainty.Inferred))
        {
            var item = Assert.Single(SectionItems(content), i => i.Text == row.Statement);
            Assert.Equal(DocumentAnalysisClassification.Inferred, item.Classification);
        }

        // Conflitti non risolti rappresentati come tali: Open, senza risoluzione, con le due alternative integrali.
        Assert.Equal(script.Count, content.Conflicts.Count);
        Assert.All(content.Conflicts, c => Assert.Equal((AnalysisConflictStatus.Open, null), (c.Status, c.Resolution)));
        foreach (var (left, right, _) in script)
        {
            Assert.Contains(content.Conflicts, c => c.Alternatives.Select(x => x.Text).Order(StringComparer.Ordinal).SequenceEqual(new[] { left, right }.Order(StringComparer.Ordinal)));
        }

        // Nessuna evidenza persa, nessuna proiettata due volte salvo il Gap estremo di un conflitto Open (R-D2b-1).
        var gapEndpoint = a.Ids["PM-04"];
        AssertNoEvidenceLost(
            content, RepresentativeLedger.Rows.Where(r => r.Doc != "pm").ToDictionary(r => r.Ref, r => r.Quote),
            new HashSet<string> { "PM-04" }.Concat(RepresentativeLedger.Rows.Where(r => r.Certainty == EvidenceCertainty.ToClarify).Select(r => r.Ref)).ToHashSet());

        // Le evidenze del PM (fonte di messaggio, senza citazione): per testo. PM-01 vincolo e PM-02 decisione sono proiettati; i due Gap sono le domande già asserite.
        foreach (var pm in new[] { "PM-01", "PM-02" })
        {
            Assert.Single(SectionItems(content), i => i.Text == RepresentativeLedger.Row(pm).Statement);
        }

        // Stadio Gap: un record per Gap, nessuna valutazione sintetizzata.
        var payload = JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'gap-stage-alias-1'", ("r", a.Run)));
        Assert.Equal("Complete", payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(RepresentativeLedger.Gold.Gaps.Count, payload.RootElement.GetProperty("gapAssessments").GetArrayLength());

        // EvidenceCoverage di produzione: nessuna evidenza non mappata e, per le evidenze non Gap, nessun avviso D3; per il Gap estremo l'avviso è atteso e non bloccante.
        var report = await Json(await a.Client.PostAsJsonAsync($"/api/projects/{a.Project}/analyses/{a.Run}/coverage-reports", new { }, Ct));
        var findings = report.GetProperty("findings").EnumerateArray().ToList();
        Assert.DoesNotContain(findings, f => f.GetProperty("code").GetString() is "evidence_not_mapped" or "gap_reference_missing");
        var warnings = findings.Where(f => f.GetProperty("code").GetString() == "item_repeats_conflict_alternative").ToList();
        var warning = Assert.Single(warnings);
        Assert.False(warning.GetProperty("blocking").GetBoolean());
        Assert.Equal(RepresentativeLedger.Row("PM-04").Statement, content.OpenQuestions.Single(q => q.Id == warning.GetProperty("itemId").GetGuid()).Text);
        Assert.DoesNotContain(findings, f => f.GetProperty("blocking").GetBoolean());

        // Nessun WorkOutput duplicato (un output per batch) e ripresa da zero in un processo nuovo senza inferenza, con la stessa revisione.
        var committed = await Committed(a.Run);
        Assert.Equal(committed, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", a.Run)));
        Assert.Equal(committed, await Count("SELECT count(DISTINCT \"BatchId\") FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", a.Run)));
        var again = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = Window, Responder = _ => throw new InvalidOperationException("nessuna inferenza al replay") };
        var replay = await RunV11Async(a.Claim, again);
        Assert.Equal(result.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));
        Assert.Equal(committed, await Committed(a.Run));
    }

    // ================================================================ C — inflazione dei candidati

    [Fact]
    public async Task C_a_model_that_emits_thirty_candidates_per_batch_completes_or_stops_with_a_typed_limit_never_with_a_partial_success()
    {
        var a = await ArrangeRepresentativeAsync();
        using var _ = a.Client;
        var model = Model(ScriptedPairs(), perBatch: 30);

        var result = await RunV11Async(a.Claim, model);

        var calls = await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", a.Run));
        Assert.InRange(calls, 1, 512);
        if (result.Status == ProcessingRunStatus.Completed)
        {
            Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));
        }
        else
        {
            Assert.Equal(ProcessingRunStatus.Failed, result.Status);
            Assert.Contains(result.ErrorCode, new[] { "coverage_incomplete", "document_limit_exceeded", "call_limit_exceeded" });
            Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));   // fail-closed: nessuna analisi parziale
        }

        Record($"| C | {RepresentativeLedger.Rows.Count} evidenze, 30 candidati per batch | {result.Status} {result.ErrorCode} | chiamate {calls} ({StageCounts(model)}) | revisioni {await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run))} |");
        TestContext.Current.SendDiagnosticMessage($"C: esito {result.Status} {result.ErrorCode}, chiamate {calls}, richieste del modello {model.Requests.Count}");
    }

    // ================================================================ B — gerarchia senza Gap (ledger T040-single senza evidenze Gap)

    [Fact]
    public async Task B_the_single_ledger_without_gap_evidence_completes_through_the_hierarchy_without_the_gap_stage()
    {
        var client = await App.LoginAsync();
        using var _ = client;
        var project = await NewProject(client);
        var ledger = RetrievalIsolatedFixture.Build(RetrievalGapCampaign.Single);
        var docs = new Dictionary<string, DocSeed>(StringComparer.Ordinal);
        var items = new List<EvSeed>();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var document in ledger.Documents.Where(d => !d.ExcludedByPm))
        {
            var evidences = document.Evidences.Where(e => e.Kind != EvidenceKind.Gap).ToList();
            if (evidences.Count == 0)
            {
                continue;   // il vincolo del registro vieta una versione senza evidenze e senza motivo
            }

            docs[document.Key] = await CreateDocumentAsync(client, project, document.Key, [.. document.Blocks.OrderBy(b => b.Ordinal).Select(b => b.Text)], evidences.Count);
            for (var i = 0; i < evidences.Count; i++)
            {
                var e = evidences[i];
                var key = $"{document.Key}|{i}";
                ids[key] = Guid.NewGuid();
                items.Add(new EvSeed(key, e.Kind, e.Polarity, EvidenceCertainty.Declared, e.Statement, document.Key, e.BlockOrdinal, e.Start, e.End, e.Statement, null, null, null));
            }
        }

        await AddEvidenceAsync(project, docs, items, ids);
        var (run, claim) = await StartV11Async(client, project);
        var model = Model([]);

        var result = await RunV11Async(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.DoesNotContain(model.Requests, DocumentGapStageWorkflowTests.IsStage);                  // nessuno stadio Gap
        Assert.Matches(new Regex(@"^Tipo di batch: foglia\n", RegexOptions.Multiline), model.Requests[0].AllText);
        Assert.NotEqual("gap", await Scalar<string>("SELECT \"Stage\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r ORDER BY \"Ordinal\" DESC LIMIT 1", ("r", run)));
        Assert.InRange(model.Requests.Count, 2, 512);
        Record($"| B | {ids.Count} evidenze (senza Gap) | Completed | chiamate {model.Requests.Count} ({StageCounts(model)}) | batch committati {await Committed(run)} |");
        var content = await ContentAsync(run);
        AssertNoEvidenceLost(content, items.ToDictionary(i => i.Ref, i => i.Quote), new HashSet<string>());
        Assert.Empty(content.Conflicts);
        var committed = await Committed(run);
        Assert.Equal(committed, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", run)));
        var again = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = Window, Responder = _ => throw new InvalidOperationException("nessuna inferenza al replay") };
        var replay = await RunV11Async(claim, again);
        Assert.Equal(result.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
    }

    // ================================================================ RH-042 — fault injection (estensione di RH-041, ledger rappresentativo, scenario A)

    private static string Respond(DocumentChatWorkflowTests.ChatRequest r) =>
        DocumentGapStageWorkflowTests.IsStage(r) ? GapStage(r) : Hierarchy(r, ScriptedPairs());

    /// <summary>Contenuto confrontabile fra due run dello stesso ledger: domande aperte, alternative dei conflitti e sezioni (testi ordinati).</summary>
    private static string Fingerprint(DocumentAnalysisContent c) => string.Join(
        "\n", new[]
        {
            "Q:" + string.Join('|', c.OpenQuestions.Select(i => i.Text).Order(StringComparer.Ordinal)),
            "C:" + string.Join('|', c.Conflicts.Select(x => string.Join('+', x.Alternatives.Select(a => a.Text).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal)),
            "R:" + string.Join('|', c.Requirements.Select(i => i.Text).Order(StringComparer.Ordinal)),
            "A:" + string.Join('|', c.Assumptions.Select(i => i.Text).Order(StringComparer.Ordinal)),
        });

    private async Task<string> CleanFingerprintAsync()
    {
        var clean = await ArrangeRepresentativeAsync();
        using var _ = clean.Client;
        var result = await RunV11Async(clean.Claim, Model(ScriptedPairs()));
        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(clean.Run));
        return Fingerprint(await ContentAsync(clean.Run));
    }

    /// <summary>Richieste in ordine: 1 foglia A, 2 foglia B, 3 cross, 4–6 confronti, 7 stadio Gap (ordine osservato in scenario A; la riduzione e l'espansione non si generano perché il ledger entra in due foglie).</summary>
    [Theory]
    [InlineData(2, "dopo il commit della foglia A")]
    [InlineData(3, "prima del commit del cross")]
    [InlineData(4, "prima del commit del primo confronto")]
    [InlineData(6, "prima del commit dell'ultimo confronto")]
    [InlineData(7, "durante lo stadio Gap")]
    public async Task A_crash_resumes_in_a_new_process_without_reinferring_committed_batches_without_duplicates_and_with_the_same_analysis_as_a_clean_run(int crashAt, string label)
    {
        var expected = await CleanFingerprintAsync();
        var a = await ArrangeRepresentativeAsync();
        using var _ = a.Client;
        var seen = 0;
        var failing = new DocumentChatWorkflowTests.FakeChatModel
        {
            ContextWindow = Window,
            Responder = r => Interlocked.Increment(ref seen) == crashAt ? throw new InvalidOperationException("rete caduta") : Respond(r),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => RunV11Async(a.Claim, failing));
        var before = await Committed(a.Run);
        Assert.Equal(crashAt - 1, before);                                                          // {label}: esattamente i batch precedenti sono durevoli
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));
        var hashesBefore = await Scalar<string>("SELECT string_agg(\"OutputHash\", ',' ORDER BY \"BatchId\") FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", a.Run));

        var healthy = Model(ScriptedPairs());
        var resumed = await RunV11Async(a.Claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, label + ": " + await Message(a.Run));
        var all = before + healthy.Requests.Count;
        Assert.Equal(all, await Committed(a.Run));
        Assert.Equal(all, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", a.Run)));   // WorkOutput = batch, nessun duplicato
        Assert.Empty(healthy.Requests.Select(r => r.AllText).Intersect(failing.Requests.Take(crashAt - 1).Select(r => r.AllText)));                                // nessun batch durevole è reinviato
        var hashesAfter = await Scalar<string>("SELECT string_agg(\"OutputHash\", ',' ORDER BY \"BatchId\") FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", a.Run));
        Assert.True(hashesBefore is null || hashesBefore.Split(',').All(h => hashesAfter.Split(',').Contains(h)), "gli output già durevoli restano identici");
        Assert.Equal(expected, Fingerprint(await ContentAsync(a.Run)));                             // stessa analisi di un run senza guasti

        var again = new DocumentChatWorkflowTests.FakeChatModel { ContextWindow = Window, Responder = _ => throw new InvalidOperationException("nessuna inferenza al replay") };
        var replay = await RunV11Async(a.Claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));
        Record($"| RH-042 | crash {label} | ripresa Completed | durevoli prima {before}, richieste dopo {healthy.Requests.Count}, totale {all} | stessa analisi del run pulito |");
    }

    [Fact]
    public async Task A_takeover_with_a_new_lease_completes_once_and_the_stale_owner_cannot_commit_or_duplicate_effects()
    {
        var a = await ArrangeRepresentativeAsync();
        using var _ = a.Client;
        var seen = 0;
        var failing = new DocumentChatWorkflowTests.FakeChatModel
        {
            ContextWindow = Window,
            Responder = r => Interlocked.Increment(ref seen) == 4 ? throw new InvalidOperationException("processo morto") : Respond(r),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => RunV11Async(a.Claim, failing));
        var before = await Committed(a.Run);

        // Nuovo lease: un altro proprietario con fence maggiore.
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"Status\" = 'Running', \"LeaseOwner\" = 'owner-2', \"FencingToken\" = 2, \"AttemptCount\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", a.Run));
        var takeover = new AnalysisRunClaim(a.Run, "owner-2", 2, 2);

        // Il proprietario vecchio (fence 1) non può né completare né aggiungere effetti.
        var stale = Model(ScriptedPairs());
        try
        {
            var staleResult = await RunV11Async(a.Claim, stale);
            Assert.NotEqual(ProcessingRunStatus.Completed, staleResult.Status);
        }
        catch (Exception)
        {
            // un'eccezione di fence è un esito ammesso: l'invariante è l'assenza di effetti
        }

        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));
        Assert.Equal(before, await Committed(a.Run));

        var healthy = Model(ScriptedPairs());
        var result = await RunV11Async(takeover, healthy);
        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(a.Run));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));
        var total = await Committed(a.Run);
        Assert.Equal(total, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", a.Run)));
        Assert.Equal(7, total);
        Record($"| RH-042 | takeover con nuovo lease (fence 2) | Completed | proprietario vecchio senza effetti | batch {total} |");
    }

    [Fact]
    public async Task A_call_limit_exhausted_mid_hierarchy_fails_closed_with_no_partial_analysis_and_the_committed_batches_preserved()
    {
        // I limiti si congelano nello snapshot del run alla richiesta: il tetto va impostato PRIMA di crearlo (come in DocumentGapStageWorkflowTests).
        using (var scope = App.Services.CreateScope())
        {
            var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
            var limits = snapshot.Limits with { MaxAiCallsPerRun = 4 };
            Assert.Empty(limits.Validate());
            await Execute("UPDATE \"DocumentPolicies\" SET \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
        }

        var a = await ArrangeRepresentativeAsync();
        using var _ = a.Client;
        var limited = Model(ScriptedPairs());

        var result = await RunV11Async(a.Claim, limited);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("document_limit_exceeded", result.ErrorCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));    // fail-closed: nessuna analisi parziale
        Assert.Equal(4, limited.Requests.Count);                                                                                // nessuna chiamata oltre il tetto
        Assert.Equal(4, await Committed(a.Run));                                                                               // i batch già validati restano durevoli
        Record($"| RH-042 | limite chiamate 4 | {result.Status} {result.ErrorCode} | richieste {limited.Requests.Count} | nessuna revisione, 4 batch durevoli |");
    }

    [Fact]
    public async Task A_deadline_expired_in_the_middle_of_the_hierarchy_stops_without_a_partial_analysis_and_keeps_the_committed_batches()
    {
        var a = await ArrangeRepresentativeAsync();
        using var _ = a.Client;
        var seen = 0;
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            ContextWindow = Window,
            Responder = Respond,
            OnRequest = async _ =>
            {
                if (Interlocked.Increment(ref seen) == 3)
                {
                    // Scadenze coerenti con i vincoli del registro (come in DocumentReconcileAliasWorkflowTests): richiesta due ore fa, scadenze a 180 s e 3600 s dalla richiesta, quindi già trascorse.
                    await Execute(
                        """
                        UPDATE "ProcessingRuns" SET "RequestedAt" = now() - interval '2 hours', "OrdinaryDeadlineAt" = now() - interval '2 hours' + interval '180 seconds',
                               "EffectiveDeadlineAt" = now() - interval '2 hours' + interval '180 seconds', "DeadlineAt" = now() - interval '2 hours' + interval '180 seconds',
                               "HardDeadlineAt" = now() - interval '2 hours' + interval '3600 seconds'
                        WHERE "Id" = @r
                        """,
                        ("r", a.Run));
                }
            },
        };

        AnalysisRunResult? result = null;
        Exception? thrown = null;
        try
        {
            result = await RunV11Async(a.Claim, model);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.False(thrown is PostgresException, "la scadenza non è stata impostata: " + thrown?.Message);             // il vincolo del registro rifiuterebbe l'UPDATE e il test sarebbe vacuo
        Assert.True(thrown is not null || result!.Status != ProcessingRunStatus.Completed, "una scadenza a metà gerarchia non può chiudere con successo");
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", a.Run)));
        Assert.True(await Committed(a.Run) < 7);
        Record($"| RH-042 | scadenza a metà gerarchia | {(result is null ? "eccezione " + thrown!.GetType().Name : result.Status + " " + result.ErrorCode)} | batch durevoli {await Committed(a.Run)} | nessuna revisione |");
    }
}
