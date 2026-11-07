using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T084 (MAF-PG, modello FINTO) — chat documentale oltre contesto (contracts/ai-workflow.md §6): scansione di TUTTE le evidenze dello snapshot del turno in
/// batch misurati (Pack T069), supporti citati o no_support motivato, riduzione gerarchica delle note con unione di visite/conflitti/sourceIds e riapertura
/// degli originali pertinenti, risposta finale validata e commit unico. Grafo fisso T043, port T044, PostgreSQL reale, SDK MAF 1.26.0; nessun DGX.
/// La finestra del modello finto è di 4000 token (budget di input ~2480): ogni scenario sta sotto o sopra quel confine in modo verificabile.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentLongChatTests : ArchiveTestBase
{
    private const string Alpha = "ZONAALFA";
    private const string Beta = "ZONABETA";
    private const string Gamma = "ZONAGAMMA";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 500_000;
    }

    // ================================================================ scenari

    [Fact]
    public async Task Three_far_zones_are_all_visited_reduced_hierarchically_and_answered_with_the_reopened_originals()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var corpus = await NewCorpusAsync(client, project, 20, (Alpha, 10), (Beta, 30), (Gamma, 50));
        var turn = await PostAsync(client, project, "Cosa prevedono le tre zone: alfa, beta e gamma?");
        var model = new LongChatModel { NoteWords = 800 };

        var result = await RunAsync(turn, model);

        Assert.Equal(ChatTurnCommitOutcome.Committed, result.Outcome);
        // Scansione: ogni evidenza dello snapshot compare in esattamente un batch, mai due, e nessuna manca.
        var scans = model.Requests.Where(LongChatModel.IsScan).ToList();
        Assert.True(scans.Count >= 3, "il corpus supera la finestra: servono piu' batch di scansione");
        var seen = scans.SelectMany(r => EvidenceMarkers(r.Messages[^1].Text)).ToList();
        Assert.Equal(corpus.Markers.Order(), seen.Order());
        Assert.Equal(seen.Count, seen.Distinct().Count());
        // Ogni invio e' isolato: istruzioni di fase + dialogo + proprio batch, mai il batch di un altro invio.
        Assert.All(scans, r => Assert.Single(r.Messages));
        // Riduzione gerarchica: le note delle zone non entrano insieme nella risposta finale, quindi esiste almeno un passaggio di riduzione.
        var reduces = model.Requests.Where(LongChatModel.IsReduce).ToList();
        Assert.NotEmpty(reduces);
        // Finale: gli originali delle tre zone sono stati riaperti (citazioni letterali), non solo le note.
        var final = Assert.Single(model.Requests, LongChatModel.IsFinal);
        foreach (var zone in new[] { Alpha, Beta, Gamma })
        {
            Assert.Contains($"{zone} originale", final.Messages[^1].Text);
        }

        var message = await AssistantOfAsync(turn);
        foreach (var zone in new[] { Alpha, Beta, Gamma })
        {
            Assert.Contains($"[fonte:{corpus.SourceOf(zone)}]", message);   // citazioni canoniche UUID, risolte dagli alias del turno
        }

        // Registro di copertura persistito: l'unione delle visite delle scansioni e' esattamente l'insieme delle evidenze dello snapshot.
        var visited = await VisitedByScansAsync(turn);
        Assert.Equal(corpus.EvidenceIds.Order(), visited.Order());
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));   // nessuna revisione formale, Evidence o run
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\""));
    }

    [Fact]
    public async Task Reduction_unions_visits_conflicts_and_source_ids_and_never_drops_an_original_of_a_cited_source()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var corpus = await NewCorpusAsync(client, project, 20, (Alpha, 10), (Beta, 30), (Gamma, 50));
        var turn = await PostAsync(client, project, "Quali vincoli valgono?");
        var model = new LongChatModel { NoteWords = 800, ConflictZone = "*" };

        await RunAsync(turn, model);

        var merged = await ReduceNotesAsync(turn);
        var note = Assert.Single(merged);
        var reduceRequest = Assert.Single(model.Requests, LongChatModel.IsReduce);
        var merging = new[] { Alpha, Beta, Gamma }.Where(z => reduceRequest.Messages[^1].Text.Contains($"{z} originale", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, merging.Count);                                                      // due note unite, la terza inoltrata senza AI; solo gli originali pertinenti riaperti
        Assert.Equal("conflict", note.Kind);                                                 // il conflitto di una nota in ingresso si conserva nell'unione
        Assert.Equal(merging.Select(corpus.SourceOf).Order(), note.SourceIds.Order());       // unione server delle fonti, non quella dichiarata dal modello
        Assert.True(note.Visited.Count >= 2);
        Assert.Equal(1, model.Requests.Count(LongChatModel.IsFinal));
        var final = Assert.Single(model.Requests, LongChatModel.IsFinal).Messages[^1].Text;
        foreach (var zone in new[] { Alpha, Beta, Gamma })
        {
            Assert.Contains($"{zone} originale", final);                                     // nessun originale perso nel passaggio finale
        }
    }

    [Fact]
    public async Task A_batch_without_support_gives_a_reasoned_no_support_and_still_counts_as_visited()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var corpus = await NewCorpusAsync(client, project, 20, (Alpha, 10));
        var turn = await PostAsync(client, project, "Cosa dice la zona alfa?");
        var model = new LongChatModel { NoteWords = 10 };

        await RunAsync(turn, model);

        var payloads = await ScanPayloadsAsync(turn);
        Assert.True(payloads.Count >= 2);
        var unsupported = payloads.Where(p => p.GetProperty("outcome").GetString() == "no_support").ToList();
        Assert.NotEmpty(unsupported);
        Assert.All(unsupported, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("reason").GetString()));   // no_support motivato
            Assert.Equal(0, p.GetProperty("notes").GetArrayLength());
            Assert.True(p.GetProperty("visited").GetArrayLength() > 0);                        // visitato anche senza supporto
        });
        Assert.Equal(corpus.EvidenceIds.Order(), (await VisitedByScansAsync(turn)).Order());
        Assert.Single(model.Requests, LongChatModel.IsFinal);
    }

    [Fact]
    public async Task When_no_batch_supports_the_question_the_final_call_still_runs_with_the_coverage_and_no_notes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var corpus = await NewCorpusAsync(client, project, 20);
        var turn = await PostAsync(client, project, "Cosa dice il documento?");
        var model = new LongChatModel();

        await RunAsync(turn, model);

        var final = Assert.Single(model.Requests, LongChatModel.IsFinal);
        Assert.Contains($"{corpus.EvidenceIds.Count} evidenze", final.Messages[^1].Text);   // registro di copertura dichiarato al modello
        Assert.DoesNotContain("<<<NOTA", final.Messages[^1].Text);
        Assert.Equal("no_document_use", JsonDocument.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @t AND \"NodeId\" = 'InvokeBatch' ORDER BY \"CommittedAt\" DESC LIMIT 1", ("t", turn))).RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task A_batch_that_cannot_be_split_further_is_irreducible_with_its_id_before_any_model_call()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocumentAsync(client, project, "grande", [string.Join(' ', Enumerable.Range(0, 3500).Select(i => $"parola{i}"))]);
        var turn = await PostAsync(client, project, "Cosa dice?");
        var model = new LongChatModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        var classified = ModelFailure.Classify(Unwrap(failure));
        Assert.Equal(DocumentWorkException.DocumentIncomplete, classified.Code);
        Assert.False(classified.IsTransient);
        Assert.Empty(model.Requests);                                                         // nessun invio parziale, nessuna unita' saltata
        Assert.DoesNotContain("parola17", Unwrap(failure).Message);                            // ID e motivo, mai testo
        Assert.Matches("[0-9a-f]{8}-[0-9a-f]{4}-", Unwrap(failure).Message);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task Two_notes_that_do_not_fit_together_with_their_originals_are_an_explicit_irreducible_limit()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewCorpusAsync(client, project, 20, (Alpha, 10), (Beta, 30));
        var turn = await PostAsync(client, project, "Cosa prevedono alfa e beta?");
        var model = new LongChatModel { NoteWords = 1500 };                                    // una nota entra da sola nel finale, due no

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.ReconciliationIrreducible, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.DoesNotContain(model.Requests, LongChatModel.IsReduce);                            // nessuna riduzione tentata oltre il limite
        Assert.DoesNotContain(model.Requests, LongChatModel.IsFinal);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task A_note_citing_a_source_outside_its_batch_is_rejected_and_nothing_is_committed()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewCorpusAsync(client, project, 20, (Alpha, 10));
        var turn = await PostAsync(client, project, "Cosa dice alfa?");
        var model = new LongChatModel { ForeignSource = true };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.DoesNotContain(model.Requests, LongChatModel.IsFinal);                                       // nessuna risposta completa dopo un batch respinto
        Assert.Equal(model.Requests.Count - 1, await Count("SELECT count(*) FROM \"WorkOutputs\""));          // solo i batch precedenti validi sono Committed, mai quello respinto
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task A_missing_visit_in_the_coverage_registry_blocks_the_result_and_never_yields_a_complete_answer()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var corpus = await NewCorpusAsync(client, project, 20, (Alpha, 10));
        var turn = await PostAsync(client, project, "Cosa dice alfa?");
        var broken = new LongChatModel { FailFinal = true };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, broken));                     // scansioni Committed, finale respinto
        var dropped = corpus.EvidenceIds[0];
        var scanBatch = Assert.Single(await Strings(
            "SELECT w.\"BatchId\" FROM \"WorkOutputs\" w WHERE w.\"OwnerId\" = @t AND w.\"CoveredIdsJson\" @> to_jsonb(@e::text)", ("t", turn), ("e", dropped.ToString("D"))));
        await Execute(   // iniezione di guasto: il registro di copertura persistito perde una visita (WorkOutputs e' append-only: trigger sospeso solo per questa riga)
            "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; UPDATE \"WorkOutputs\" SET \"CoveredIdsJson\" = \"CoveredIdsJson\" - @e WHERE \"OwnerId\" = @t AND \"BatchId\" = @b; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;",
            ("e", dropped.ToString("D")), ("t", turn), ("b", scanBatch));
        var model = new LongChatModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(await ClaimAsync(turn), model));

        Assert.Equal(DocumentWorkException.CoverageIncomplete, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task A_retry_reuses_the_committed_scans_and_only_re_sends_the_missing_final_call()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var corpus = await NewCorpusAsync(client, project, 20, (Alpha, 10));
        var turn = await PostAsync(client, project, "Cosa dice alfa?");
        var first = new LongChatModel { FailFinal = true };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, first));
        var scanCalls = first.Requests.Count(LongChatModel.IsScan);
        var ids = await ScanBatchIdsAsync(turn);
        var second = new LongChatModel();

        var result = await RunAsync(await ClaimAsync(turn), second);

        Assert.Equal(ChatTurnCommitOutcome.Committed, result.Outcome);
        Assert.DoesNotContain(second.Requests, LongChatModel.IsScan);                              // nessuna reinferenza di output gia' Committed
        Assert.Single(second.Requests, LongChatModel.IsFinal);
        Assert.Equal(ids, await ScanBatchIdsAsync(turn));                                      // stessi ID/hash
        Assert.Equal(scanCalls, ids.Count);
        Assert.Contains($"[fonte:{corpus.SourceOf(Alpha)}]", await AssistantOfAsync(turn));
    }

    [Fact]
    public async Task Excluded_sources_and_old_assistants_are_not_reachable_indirectly_through_the_scan_or_the_notes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var old = await NewDocumentAsync(client, project, "vecchio", ["Contenuto del documento escluso vecchio."]);
        var corpus = await NewCorpusAsync(client, project, 20, (Alpha, 10));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, project, Item(old.Document), Item(corpus.Documents[0], included: false), Item(corpus.Documents[1], included: false), Item(corpus.Documents[2], included: false))).StatusCode);
        var t1 = await PostAsync(client, project, "U1 prima domanda");
        await RunAsync(t1, new LongChatModel { SmallAnswer = true });
        var a1 = await AssistantOfAsync(t1);
        Assert.Contains("[fonte:", a1);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, project, Item(old.Document, included: false), Item(corpus.Documents[0]), Item(corpus.Documents[1]), Item(corpus.Documents[2]))).StatusCode);
        var t2 = await PostAsync(client, project, "U2 seconda domanda su alfa");
        var model = new LongChatModel();

        await RunAsync(t2, model);

        Assert.NotEmpty(model.Requests);
        Assert.All(model.Requests, r =>
        {
            Assert.DoesNotContain(a1, r.AllText);                                              // il vecchio assistant (snapshot diverso) non entra in nessun invio
            Assert.DoesNotContain("Contenuto del documento escluso vecchio.", r.AllText);
            Assert.DoesNotContain(old.SourceId.ToString("D"), r.AllText);
            Assert.Contains("U1 prima domanda", r.AllText);                                    // ma l'originale PM resta
        });
        var committed = await AssistantOfAsync(t2);
        Assert.DoesNotContain(old.SourceId.ToString("D"), committed);
    }

    [Fact]
    public async Task The_historical_uuid_contract_follows_the_same_scan_and_commits_canonical_citations()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var corpus = await NewCorpusAsync(client, project, 20, (Alpha, 10));
        var turn = await PostAsync(client, project, "Cosa dice alfa?");
        await Execute("UPDATE \"ChatTurns\" SET \"PromptVersion\" = NULL, \"AgentConfig\" = NULL WHERE \"Id\" = @t", ("t", turn));
        var model = new LongChatModel();

        await RunAsync(turn, model);

        Assert.Contains(model.Requests.Where(LongChatModel.IsScan), r => r.AllText.Contains($"fonte:{corpus.SourceOf(Alpha)}", StringComparison.Ordinal));   // etichetta = UUID nel contratto storico
        Assert.Contains($"[fonte:{corpus.SourceOf(Alpha)}]", await AssistantOfAsync(turn));
        Assert.Equal(corpus.EvidenceIds.Order(), (await VisitedByScansAsync(turn)).Order());
    }

    [Fact]
    public async Task The_scan_and_reduce_prompts_state_the_boundaries_without_retrieval_or_ranking()
    {
        var scan = DocumentLongChat.ScanInstructions;
        var reduce = DocumentLongChat.ReduceInstructions;

        Assert.Contains("no_support", scan);
        Assert.Contains("TUTTE le evidenze", scan);
        Assert.Contains("# Scansione documentale", scan);
        Assert.Contains("# Riduzione delle note", reduce);
        Assert.Contains("Non scartare", reduce);
        Assert.DoesNotContain("top-k", scan + reduce, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    // ================================================================ supporto

    private sealed record Doc(Guid Document, Guid Version, Guid SourceId, IReadOnlyList<Guid> Evidences, IReadOnlyList<Guid> Sources);

    private sealed class Corpus(List<Doc> documents, Dictionary<string, Guid> zoneSources, List<string> markers)
    {
        public IReadOnlyList<Guid> Documents => documents.Select(d => d.Document).ToList();

        public IReadOnlyList<Guid> EvidenceIds => documents.SelectMany(d => d.Evidences).ToList();

        public IReadOnlyList<string> Markers => markers;

        public Guid SourceOf(string zone) => zoneSources[zone];
    }

    private static object Item(Guid document, bool included = true) => new { documentId = document, included, mode = "LatestReady", versionId = (Guid?)null };

    private async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid project, params object[] items)
    {
        var revision = (await Json(await client.GetAsync($"/api/projects/{project}/source-selection", Ct))).GetProperty("revision").GetInt64();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection") { Content = JsonContent.Create(new { items }) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Nuova richiesta di chat: congela la definizione corrente (v3 con alias); nessun ripristino alla v2.</summary>
    private async Task<Guid> PostAsync(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("chatTurnId").GetGuid();
    }

    /// <summary>Tre documenti con <paramref name="perDocument"/> evidenze ciascuno, ~70 parole l'una; le zone sono evidenze con un marcatore nell'enunciato.</summary>
    private async Task<Corpus> NewCorpusAsync(HttpClient client, Guid project, int perDocument, params (string Zone, int Index)[] zones)
    {
        var docs = new List<Doc>();
        var zoneSources = new Dictionary<string, Guid>();
        var markers = new List<string>();
        for (var d = 0; d < 3; d++)
        {
            var statements = new List<string>();
            for (var i = 0; i < perDocument; i++)
            {
                var global = d * perDocument + i;
                var zone = zones.FirstOrDefault(z => z.Index == global).Zone;
                var marker = $"EV{global:D3}";
                markers.Add(marker);
                var filler = string.Join(' ', Enumerable.Range(0, 60).Select(w => $"testo{global}x{w}"));
                statements.Add(zone is null ? $"{marker} {filler}" : $"{marker} {zone} originale {filler}");
            }

            var doc = await NewDocumentAsync(client, project, $"doc{d}", statements);
            docs.Add(doc);
            foreach (var (zone, index) in zones.Where(z => z.Index / perDocument == d))
            {
                zoneSources[zone] = doc.Sources[index % perDocument];
            }
        }

        return new Corpus(docs, zoneSources, markers);
    }

    private async Task<Doc> NewDocumentAsync(HttpClient client, Guid project, string name, IReadOnlyList<string> statements)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var (documentId, versionId) = (created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == versionId, Ct);
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
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = statements.Count, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var evidences = new List<Guid>();
        var sources = new List<Guid>();
        for (var i = 0; i < statements.Count; i++)
        {
            var (evidenceId, sourceId) = (Guid.NewGuid(), Guid.NewGuid());
            evidences.Add(evidenceId);
            sources.Add(sourceId);
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
                        SourceId = sourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                        MessageId = null, Sequence = null, DocumentId = documentId, DocumentVersionId = versionId, PreparationId = preparation.Id,
                        ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = statements[i].Length, Quote = statements[i],
                    },
                ],
            };
            db.Evidences.Add(new EvidenceItem
            {
                Id = evidenceId, VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = i + 1,
                Key = $"e{i + 1}", Kind = EvidenceKind.Requirement, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statements[i],
                Content = DocumentEvidenceCodec.EncodeContent(content),
            });
        }

        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = versionId, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = version, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = version;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == documentId).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, versionId), Ct);
        return new Doc(documentId, versionId, sources[0], evidences, sources);
    }

    private async Task<ChatTurnClaim> ClaimAsync(Guid turn)
    {
        await Execute(
            """
            UPDATE "ChatTurns" SET "Status" = 'Running', "LeaseOwner" = 'test-owner', "FencingToken" = "FencingToken" + 1, "AttemptCount" = "AttemptCount" + 1, "StartedAt" = now(),
                   "LeaseUntil" = now() + interval '10 minutes', "SessionRevisionAtStart" = (SELECT "ChatSessionRevision" FROM "Conversations" c WHERE c."Id" = "ConversationId")
            WHERE "Id" = @t
            """,
            ("t", turn));
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t = await db.ChatTurns.AsNoTracking().SingleAsync(x => x.Id == turn, Ct);
        return new ChatTurnClaim(t.Id, t.ConversationId, t.TriggerMessageId, t.SourceCutoffSequence, "test-owner", t.FencingToken, t.AttemptCount, t.LeaseUntil!.Value, t.DeadlineAt);
    }

    private async Task<ChatTurnCommitResult> RunAsync(Guid turn, LongChatModel model) => await RunAsync(await ClaimAsync(turn), model);

    private async Task<ChatTurnCommitResult> RunAsync(ChatTurnClaim claim, LongChatModel model)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model.Handler);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentChatWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions()));
        return await workflow.ExecuteAsync(claim, null, Ct);
    }

    private async Task<string> AssistantOfAsync(Guid turn) =>
        await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t", ("t", turn));

    private static IEnumerable<string> EvidenceMarkers(string text) => Regex.Matches(text, @"<<<EVIDENZA[^\n]*>>>\n(EV\d{3})\b").Select(m => m.Groups[1].Value);

    private async Task<List<JsonElement>> ScanPayloadsAsync(Guid turn)
    {
        var rows = await Strings("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" w JOIN \"DerivedBatches\" b ON b.\"BatchId\" = w.\"BatchId\" AND b.\"OwnerId\" = w.\"OwnerId\" WHERE w.\"OwnerId\" = @t AND b.\"Stage\" = 'scan' ORDER BY b.\"Ordinal\"", ("t", turn));
        return rows.Select(r => JsonDocument.Parse(r).RootElement.Clone()).ToList();
    }

    private async Task<List<Guid>> VisitedByScansAsync(Guid turn) =>
        (await ScanPayloadsAsync(turn)).SelectMany(p => p.GetProperty("visited").EnumerateArray().Select(e => e.GetGuid())).ToList();

    private async Task<List<string>> ScanBatchIdsAsync(Guid turn) =>
        await Strings("SELECT w.\"BatchId\" || ':' || w.\"OutputHash\" FROM \"WorkOutputs\" w JOIN \"DerivedBatches\" b ON b.\"BatchId\" = w.\"BatchId\" AND b.\"OwnerId\" = w.\"OwnerId\" WHERE w.\"OwnerId\" = @t AND b.\"Stage\" = 'scan' ORDER BY b.\"Ordinal\"", ("t", turn));

    private sealed record MergedNote(string Kind, List<Guid> SourceIds, List<Guid> Visited);

    private async Task<List<MergedNote>> ReduceNotesAsync(Guid turn)
    {
        var rows = await Strings("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" w JOIN \"DerivedBatches\" b ON b.\"BatchId\" = w.\"BatchId\" AND b.\"OwnerId\" = w.\"OwnerId\" WHERE w.\"OwnerId\" = @t AND b.\"Stage\" = 'reduce' ORDER BY b.\"Ordinal\"", ("t", turn));
        return rows.Select(r =>
        {
            var root = JsonDocument.Parse(r).RootElement;
            return new MergedNote(
                root.GetProperty("kind").GetString()!, [.. root.GetProperty("sourceIds").EnumerateArray().Select(e => e.GetGuid())], [.. root.GetProperty("visited").EnumerateArray().Select(e => e.GetGuid())]);
        }).ToList();
    }

    private static Exception Unwrap(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException!)
        {
            if (current is DocumentWorkException or RuntimeUnverifiedException or TaskCanceledException or LeaseLostException)
            {
                return current;
            }

            if (current.InnerException is null)
            {
                break;
            }
        }

        return e;
    }

    private async Task<List<string>> Strings(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>
    /// Modello finto per la chat oltre contesto (sopra il gestore HTTP della chat piccola): riconosce la fase dal prompt di sistema. La scansione risponde con una nota
    /// per ogni evidenza che contiene un marcatore di zona, citando l'etichetta della sua fonte, e con no_support motivato negli altri batch; la riduzione restituisce una
    /// nota unita breve; il finale cita le zone trovate nelle evidenze riaperte (o la prima fonte per la chat piccola).
    /// </summary>
    private sealed class LongChatModel
    {
        private static readonly string[] Zones = [Alpha, Beta, Gamma];

        public LongChatModel() => Handler = new DocumentChatWorkflowTests.FakeChatModel { Responder = Respond, OnRequest = Guard };

        // Guasto di rete sul solo invio finale: gli output di scansione gia' Committed restano e il retry riparte da li'.
        private Task Guard(DocumentChatWorkflowTests.ChatRequest request) =>
            FailFinal && IsFinal(request) ? throw new HttpRequestException("guasto di rete simulato") : Task.CompletedTask;

        public DocumentChatWorkflowTests.FakeChatModel Handler { get; }

        public List<DocumentChatWorkflowTests.ChatRequest> Requests => Handler.Requests;

        public int NoteWords { get; init; } = 10;

        public string? ConflictZone { get; init; }

        public bool ForeignSource { get; init; }

        public bool FailFinal { get; init; }

        public bool SmallAnswer { get; init; }

        public static bool IsScan(DocumentChatWorkflowTests.ChatRequest r) => r.System.Contains("# Scansione documentale", StringComparison.Ordinal);

        public static bool IsReduce(DocumentChatWorkflowTests.ChatRequest r) => r.System.Contains("# Riduzione delle note", StringComparison.Ordinal);

        public static bool IsFinal(DocumentChatWorkflowTests.ChatRequest r) => !IsScan(r) && !IsReduce(r);

        private const string LabelPattern = @"fonte:(F[1-9][0-9]*|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})";

        private static List<(string Zone, string Label)> ZoneLabels(string text) =>
        [
            .. Regex.Matches(text, @"<<<EVIDENZA[^\n]*>>>\n(.*?)<<<FINE EVIDENZA>>>", RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value)
                .SelectMany(block => Zones.Where(z => block.Contains($"{z} originale", StringComparison.Ordinal))
                    .Select(z => (z, Regex.Match(block, LabelPattern).Groups[1].Value))),
        ];

        private string Respond(DocumentChatWorkflowTests.ChatRequest request)
        {
            var text = request.Messages[^1].Text;
            if (IsScan(request))
            {
                var found = ZoneLabels(text);
                if (found.Count == 0)
                {
                    return JsonSerializer.Serialize(new { outcome = "no_support", reason = "Nessuna evidenza di questo gruppo riguarda la domanda del project manager.", notes = Array.Empty<object>() });
                }

                var notes = found.Select(f => new
                {
                    kind = ConflictZone is "*" || f.Zone == ConflictZone ? "conflict" : "support",
                    text = $"{f.Zone} nota " + string.Join(' ', Enumerable.Range(0, NoteWords).Select(i => $"riempitivo{i}")),
                    sources = new[] { ForeignSource ? "F9999" : f.Label },
                });
                return JsonSerializer.Serialize(new { outcome = "support", reason = "", notes });
            }

            if (IsReduce(request))
            {
                var names = Zones.Where(z => Regex.IsMatch(text, $@"<<<NOTA[^\n]*>>>\n{z} nota")).ToList();
                return JsonSerializer.Serialize(new { text = "Sintesi unita di " + string.Join(", ", names), conflict = false });
            }

            var zones = ZoneLabels(text);
            if (zones.Count > 0)
            {
                return JsonSerializer.Serialize(new { mode = "documented", answer = "Zone: " + string.Join("; ", zones.Select(z => $"{z.Zone} [fonte:{z.Label}]")) });
            }

            var first = Regex.Match(text, LabelPattern);
            return SmallAnswer && first.Success
                ? JsonSerializer.Serialize(new { mode = "documented", answer = $"Dice questo [fonte:{first.Groups[1].Value}]." })
                : JsonSerializer.Serialize(new { mode = "no_document_use", answer = "Nei documenti selezionati non risulta nulla di pertinente." });
        }
    }
}
