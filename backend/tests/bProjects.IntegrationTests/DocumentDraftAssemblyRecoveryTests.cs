using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T091 (PG, IChatClient finto a livello HTTP) — commit/assembly e ripresa della bozza editoriale oltre contesto (contracts/ai-workflow.md §4.5, §5.3): crash fra output
/// editoriali, checkpoint e bozza; fence superato e deadline scaduta al commit; ripresa con un nuovo owner e revisione incompatibile. Database reale, grafo MAF reale
/// (SDK 1.26.0), nessun DGX. Esito atteso: bozza UNICA con stessa revisione, stesse fonti e stesso hash; output già Committed riusati senza invii aggiuntivi; mai un
/// Completed parziale né il riuso di una revisione incompatibile.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentDraftAssemblyRecoveryTests : IAsyncLifetime
{
    private const string V3 = "project-document-documents-v3";
    private const string NewOwner = "owner-2";
    private static readonly Guid Hdr = Guid.Parse("a2000000-0000-4000-8000-000000000001");
    private static readonly Guid MsgSource = Guid.Parse("a2000000-0000-4000-8000-000000000002");

    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

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

    // ================================================================ scenari

    [Fact]
    public async Task A_crash_between_unit_outputs_is_resumed_by_a_new_owner_that_sends_only_the_missing_units_and_commits_one_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        DocumentChatWorkflowTests.FakeChatModel crash = null!;
        crash = Model(onRequest: _ => crash.Requests.Count >= 3 ? throw new HttpRequestException("rete caduta") : Task.CompletedTask);
        try
        {
            await RunAsync(await ClaimAsync(s.Run), crash);
        }
        catch (Exception)
        {
            // Crash del processo: nessuno stato di dominio oltre agli output gia' Committed.
        }

        await AssertNoDraftAsync(s);
        var committed = await Outputs(s.Run);
        Assert.Equal(2, committed.Count);
        await TakeOverAsync(s, keepDraft: false);

        var resumed = Model();
        var result = await RunAsync(new DocumentRunClaim(s.Run, NewOwner, 2, 2), resumed);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        var after = await Outputs(s.Run);
        Assert.Equal(committed, after.Take(2).ToList());   // gli output gia' Committed non cambiano id/hash
        Assert.Equal(resumed.Requests.Count + 2, after.Count);
        var sentBefore = crash.Requests.Take(2).SelectMany(PrimaryAliases).ToHashSet();
        Assert.All(resumed.Requests.SelectMany(PrimaryAliases), alias => Assert.DoesNotContain(alias, sentBefore));   // nessun invio aggiuntivo per le unita' gia' committate
        await AssertSingleCompleteDraftAsync(s, result);
    }

    [Fact]
    public async Task A_crash_after_every_output_but_before_the_draft_and_the_checkpoint_rebuilds_the_identical_draft_with_no_inference_for_a_new_owner()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var first = await RunAsync(claim, Model());
        Assert.Equal(ProcessingRunStatus.Completed, first.Status);
        var before = await DraftSnapshotAsync(s);
        var outputs = await Outputs(s.Run);
        await TakeOverAsync(s, keepDraft: false);   // come dopo un crash prima del commit di dominio: output Committed, nessuna bozza, nessun checkpoint

        var silent = Model(_ => "{}");
        var result = await RunAsync(new DocumentRunClaim(s.Run, NewOwner, 2, 2), silent);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Empty(silent.Requests);
        Assert.Equal(outputs, await Outputs(s.Run));
        var after = await DraftSnapshotAsync(s);
        Assert.Equal(before.Markdown, after.Markdown);                 // stesso hash del Markdown
        Assert.Equal(before.Revision, after.Revision);                  // stessa revisione
        Assert.Equal(before.Sources, after.Sources);                    // stesse fonti
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_worker_whose_fence_was_superseded_before_the_commit_leaves_no_draft_and_the_new_owner_completes_a_single_one_resending_only_the_uncommitted_unit()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var stale = Model(onRequest: r => IsLast(r)
            ? Execute("UPDATE \"ProcessingRuns\" SET \"FencingToken\" = 2, \"LeaseOwner\" = @o WHERE \"Id\" = @r", ("o", NewOwner), ("r", s.Run))
            : Task.CompletedTask);

        var oldClaim = await ClaimAsync(s.Run);
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(oldClaim, stale));

        await AssertNoDraftAsync(s);
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
        var committed = await Outputs(s.Run);
        var units = stale.Requests.Count;
        Assert.True(committed.Count < units, "il worker stale non puo' committare l'output dell'ultima unita'");

        var resumed = Model();
        var result = await RunAsync(new DocumentRunClaim(s.Run, NewOwner, 2, 2), resumed);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        Assert.Equal(units - committed.Count, resumed.Requests.Count);
        Assert.Equal(committed, (await Outputs(s.Run)).Take(committed.Count).ToList());
        await AssertSingleCompleteDraftAsync(s, result);
        // Il vecchio worker che riprova dopo il completamento non scrive nulla: il risultato terminale non si rielabora.
        var again = Model(_ => "{}");
        var replay = await RunAsync(new DocumentRunClaim(s.Run, "test-owner", 1, 1), again);
        Assert.Equal(ProcessingRunStatus.Completed, replay.Status);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_deadline_expired_before_the_commit_leaves_no_partial_completed_and_after_the_deadline_is_renewed_the_resume_completes_without_resending_committed_units()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var original = await Row("SELECT \"DeadlineAt\", \"EffectiveDeadlineAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run));
        var late = Model(onRequest: r => IsLast(r)
            ? Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = clock_timestamp() - interval '1 second', \"EffectiveDeadlineAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = @r", ("r", s.Run))
            : Task.CompletedTask);
        ProcessingRunStatus? status = null;
        try
        {
            status = (await RunAsync(await ClaimAsync(s.Run), late)).Status;
        }
        catch (Exception)
        {
            // La scadenza puo' emergere come eccezione di lease/deadline: l'invariante e' lo stato di dominio.
        }

        Assert.NotEqual(ProcessingRunStatus.Completed, status);
        await AssertNoDraftAsync(s);
        var committed = await Outputs(s.Run);
        await Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = @d, \"EffectiveDeadlineAt\" = @e WHERE \"Id\" = @r", ("d", original[0]), ("e", original[1] is DBNull ? DBNull.Value : original[1]), ("r", s.Run));
        await TakeOverAsync(s, keepDraft: false);

        var resumed = Model();
        var result = await RunAsync(new DocumentRunClaim(s.Run, NewOwner, 2, 2), resumed);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        Assert.Equal(committed, (await Outputs(s.Run)).Take(committed.Count).ToList());
        Assert.Equal(late.Requests.Count - committed.Count, resumed.Requests.Count);
        await AssertSingleCompleteDraftAsync(s, result);
    }

    [Fact]
    public async Task A_committed_output_of_an_incompatible_revision_is_never_reused_and_fails_the_resume_with_no_inference_and_no_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, Model())).Status);
        await TakeOverAsync(s, keepDraft: false);
        var batchId = await Scalar<string>("SELECT \"BatchId\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed' ORDER BY \"CommittedAt\" DESC LIMIT 1", ("r", s.Run));
        var payload = JsonNode.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"BatchId\" = @b", ("r", s.Run), ("b", batchId)))!.AsObject();
        payload["revisionId"] = Guid.NewGuid().ToString("D");   // output di un'altra revisione di input
        var text = payload.ToJsonString();
        await Execute(
            "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; UPDATE \"WorkOutputs\" SET \"PayloadJson\" = @p::jsonb, \"OutputHash\" = @h WHERE \"OwnerId\" = @r AND \"BatchId\" = @b; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;",
            ("p", text), ("h", CanonicalJson.Sha256Hex(text)), ("r", s.Run), ("b", batchId));

        var silent = Model(_ => "{}");
        var result = await RunAsync(new DocumentRunClaim(s.Run, NewOwner, 2, 2), silent);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.IntegrityConflict), (result.Status, result.ErrorCode));
        Assert.Empty(silent.Requests);   // niente reinferenza per sovrascrivere l'output esistente
        await AssertNoDraftAsync(s);
    }

    // ================================================================ asserzioni

    private async Task AssertNoDraftAsync(Seeded s)
    {
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"LatestDraftId\" IS NOT NULL", ("p", s.Project)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Id\" = @r AND (\"DraftId\" IS NOT NULL OR \"Status\" = 'Completed')", ("r", s.Run)));
    }

    private async Task AssertSingleCompleteDraftAsync(Seeded s, DocumentRunResult result)
    {
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
        var draft = await DraftSnapshotAsync(s);
        Assert.Equal(result.DraftId, draft.Id);
        Assert.Equal(s.Revision, draft.Revision);
        for (var i = 0; i < 12; i++)
        {
            Assert.Contains($"Requisito {i} r{i}x0", draft.Markdown);   // copertura completa: nessun Markdown parziale
        }

        Assert.Contains($"[fonte:{Hdr:D}]", draft.Markdown);
        Assert.Equal(result.DraftId, await Scalar<Guid>("SELECT \"LatestDraftId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", s.Project)));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
    }

    private sealed record DraftView(Guid Id, Guid Revision, string Markdown, string Sources);

    private async Task<DraftView> DraftSnapshotAsync(Seeded s)
    {
        var row = await Row("SELECT \"Id\", \"AnalysisRevisionId\", \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        var markdown = (string)row[2];
        var sources = string.Join(',', Regex.Matches(markdown, @"\[fonte:([0-9a-f-]{36})\]").Select(m => m.Groups[1].Value).Distinct().Order());
        return new DraftView((Guid)row[0], (Guid)row[1], markdown, sources);
    }

    private async Task<List<string>> Outputs(Guid run) =>
        [.. (await Rows("SELECT \"BatchId\" || ':' || \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed' ORDER BY \"CommittedAt\", \"BatchId\"", ("r", run))).Select(r => (string)r[0])];

    private async Task<string> ErrorMessage(Guid run) => await Scalar<string>("SELECT COALESCE(\"ErrorMessage\", '') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)) ?? "";

    // ================================================================ contenuto e modello finto

    private static string Words(int count, string prefix) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    private static DocumentAnalysisContent Big(Guid analysis, Guid project, Guid message, int requirements = 12, int words = 300)
    {
        var pages = Enumerable.Range(0, requirements).Select(i => Guid.Parse($"a3000000-0000-4000-8000-{i + 1:D12}")).ToList();
        DocumentAnalysisSource Passage(Guid id, string quote) => new()
        {
            Id = id, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = Guid.NewGuid(),
            DocumentVersionId = Guid.NewGuid(), PreparationId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = quote.Length, Quote = quote,
        };

        DocumentAnalysisItem Item(string id, string text, DocumentAnalysisClassification c, params Guid[] refs) => new() { Id = Guid.Parse(id), Text = text, Classification = c, EvidenceRefs = refs };
        return new DocumentAnalysisContent
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = analysis, ProjectId = project, SourceCutoffSequence = 1, ValidationState = AnalysisContentState.PmConfirmed,
            Sources =
            [
                new DocumentAnalysisSource
                {
                    Id = MsgSource, Kind = DocumentAnalysisSourceKind.Message, MessageId = message, MessageRole = AnalysisMessageRole.User, RevisionId = null, DocumentId = null,
                    DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
                },
                Passage(Hdr, "intestazione ripetuta"),
                .. pages.Select((p, i) => Passage(p, $"pagina {i}")),
            ],
            Goals = [Item("b2000000-0000-4000-8000-000000000001", "Realizzare un portale clienti", DocumentAnalysisClassification.Declared, MsgSource)],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = [.. pages.Select((p, i) => Item($"b2000000-0000-4000-8000-{i + 10:D12}", $"Requisito {i} " + Words(words, $"r{i}x"), DocumentAnalysisClassification.Documented, Hdr, p))],
            OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [],
            Conflicts =
            [
                new DocumentAnalysisConflict
                {
                    Id = Guid.Parse("b2000000-0000-4000-8000-0000000000c1"), Description = "La fatturazione e dentro o fuori dal perimetro?", Status = AnalysisConflictStatus.Open, Resolution = null,
                    Alternatives =
                    [
                        Item("b2000000-0000-4000-8000-000000000a01", "Fatturazione nel portale", DocumentAnalysisClassification.Declared, MsgSource, Hdr),
                        Item("b2000000-0000-4000-8000-000000000a02", "Fatturazione fuori perimetro", DocumentAnalysisClassification.Documented, Hdr),
                    ],
                },
            ],
        };
    }

    private sealed record Element(string Alias, string Text, string[] Sources, bool Primary);

    private static readonly Regex PrimaryRegex = ElementRegex("ELEMENTO");
    private static readonly Regex ContextRegex = ElementRegex("CONTESTO");

    private static Regex ElementRegex(string tag) =>
        new($@"<<<{tag} \[(I\d+)\][^>\n]*?(?:fonti=([^>\n]*))?>>>\n(.*?)\n<<<FINE {tag}>>>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static List<Element> Elements(DocumentChatWorkflowTests.ChatRequest request)
    {
        var text = request.AllText;
        IEnumerable<Element> Parse(Regex regex, bool primary) => regex.Matches(text).Select(m => new Element(
            m.Groups[1].Value, m.Groups[3].Value,
            m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), primary));
        return [.. Parse(PrimaryRegex, true), .. Parse(ContextRegex, false)];
    }

    private static IEnumerable<string> PrimaryAliases(DocumentChatWorkflowTests.ChatRequest request) => Elements(request).Where(e => e.Primary).Select(e => e.Alias);

    /// <summary>L'ultima unita' e' quella che ha come principale l'ultimo alias della revisione (ordine stabile: requisiti, poi le alternative del conflitto).</summary>
    private static bool IsLast(DocumentChatWorkflowTests.ChatRequest request) => PrimaryAliases(request).Contains("I15");

    private static string ValidFor(DocumentChatWorkflowTests.ChatRequest request)
    {
        var blocks = new JsonArray();
        foreach (var e in Elements(request).Where(e => e.Primary))
        {
            blocks.Add(DraftAliasFixtures.Block([e.Alias], $"- {string.Join(' ', e.Text.Split(' ').Take(6))} {string.Concat(e.Sources.Select(s => $"[fonte:{s}] "))}".Trim(), e.Sources));
        }

        return new JsonObject { ["sectionKey"] = "documento", ["blocks"] = blocks }.ToJsonString();
    }

    private static DocumentChatWorkflowTests.FakeChatModel Model(Func<DocumentChatWorkflowTests.ChatRequest, string>? responder = null, Func<DocumentChatWorkflowTests.ChatRequest, Task>? onRequest = null) =>
        new() { Responder = responder ?? ValidFor, OnRequest = onRequest };

    // ================================================================ supporto: dati e esecuzione

    private sealed record Seeded(Guid Project, Guid Analysis, Guid Revision, Guid Run);

    private async Task<Seeded> SeedAsync(HttpClient client)
    {
        var project = (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Bozza ripresa {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();
        var post = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text = "Vogliamo un portale clienti." }, Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var analyze = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        analyze.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var queued = await client.SendAsync(analyze, Ct);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var analysis = (await Json(queued)).GetProperty("id").GetGuid();

        Guid revision;
        using (var scope = App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.ProcessingRuns.SingleAsync(r => r.Id == analysis, Ct);
            var message = await db.ConversationMessages.AsNoTracking()
                .Where(m => m.ConversationId == run.ConversationId && m.Role == MessageRole.User).OrderBy(m => m.Sequence).FirstAsync(Ct);
            var content = Big(analysis, project, message.Id) with { SourceCutoffSequence = run.SourceCutoffSequence ?? 0 };
            var row = new AnalysisRevision
            {
                Id = Guid.NewGuid(), AnalysisId = analysis, Version = 1, Origin = AnalysisRevisionOrigin.Ai, CreatedBy = run.RequestedBy, CreatedAt = DateTimeOffset.UtcNow,
                SchemaVersion = AnalysisRevision.DocumentSchemaVersion, ContentJson = DocumentAnalysisContentJson.Serialize(content with { ValidationState = AnalysisContentState.Unvalidated }),
                ValidationState = AnalysisValidationState.Unvalidated,
            };
            db.AnalysisRevisions.Add(row);
            run.Status = ProcessingRunStatus.Completed;
            run.ResultRevisionId = row.Id;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
            await db.Projects.Where(p => p.Id == project).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentAnalysisId, analysis), Ct);
            revision = row.Id;
        }

        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/confirm");
        confirm.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(confirm, Ct)).StatusCode);

        var generate = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        generate.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var accepted = await client.SendAsync(generate, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var generation = (await Json(accepted)).GetProperty("id").GetGuid();
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r",
            ("d", V3), ("p", SourceSnapshotStore.DocumentPromptVersions()), ("r", generation));
        return new Seeded(project, analysis, revision, generation);
    }

    private async Task<DocumentRunClaim> ClaimAsync(Guid run)
    {
        await Execute(
            """
            UPDATE "ProcessingRuns" SET "Status" = 'Running', "LeaseOwner" = 'test-owner', "FencingToken" = 1, "AttemptCount" = 1, "StartedAt" = now(),
                   "LeaseUntil" = now() + interval '10 minutes'
            WHERE "Id" = @r AND "Status" = 'Queued'
            """,
            ("r", run));
        return new DocumentRunClaim(run, "test-owner", 1, 1);
    }

    /// <summary>
    /// Nuovo owner dopo un crash o una scadenza: stato Running con fence 2, bozza e checkpoint azzerati (la sola parte persistita sono i WorkOutput Committed); il vecchio
    /// owner/fence non e' piu' valido.
    /// </summary>
    private async Task TakeOverAsync(Seeded s, bool keepDraft) => await Execute(
        $"""
        ALTER TABLE "ProcessingRuns" DISABLE TRIGGER USER;
        ALTER TABLE "Drafts" DISABLE TRIGGER USER;
        {(keepDraft ? "" : "UPDATE \"Projects\" SET \"LatestDraftId\" = NULL WHERE \"Id\" = @p;")}
        UPDATE "ProcessingRuns" SET {(keepDraft ? "" : "\"DraftId\" = NULL,")} "Status" = 'Running', "CompletedAt" = NULL, "ErrorCode" = NULL, "ErrorMessage" = NULL,
               "LeaseOwner" = '{NewOwner}', "FencingToken" = 2, "AttemptCount" = 2, "LeaseUntil" = now() + interval '10 minutes' WHERE "Id" = @r;
        {(keepDraft ? "" : "DELETE FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r;")}
        DELETE FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = @r;
        ALTER TABLE "Drafts" ENABLE TRIGGER USER;
        ALTER TABLE "ProcessingRuns" ENABLE TRIGGER USER;
        """,
        ("p", s.Project), ("r", s.Run));

    private async Task<DocumentRunResult> RunAsync(DocumentRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentSourceDraftWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions()));
        return await workflow.ExecuteAsync(claim, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters) => await Scalar<long>(sql, parameters);

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    private async Task<object[]> Row(string sql, params (string Name, object Value)[] parameters) => Assert.Single(await Rows(sql, parameters));

    private async Task<List<object[]>> Rows(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var list = new List<object[]>();
        while (await reader.ReadAsync(Ct))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            list.Add(values);
        }

        return list;
    }

    private async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
