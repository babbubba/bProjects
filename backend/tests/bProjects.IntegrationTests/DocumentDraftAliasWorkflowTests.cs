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
/// T127 (MAF-PG, IChatClient finto a livello HTTP) — generazione documentale v3 con alias sul grafo T043: il modello vede solo alias e produce il DTO
/// `document-draft-alias-1`; il server risolve contro il catalogo della revisione confermata congelata, ricostruisce id canonici e Markdown, esegue il
/// validatore invariato PRIMA del WorkOutput e ricompone a ogni lettura/commit. I run già congelati restano v2 (contracts/ai-workflow.md §7.11). Nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed partial class DocumentDraftAliasWorkflowTests : IAsyncLifetime
{
    private static readonly Regex AnyUuid = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", RegexOptions.Compiled);
    private static readonly Regex AnyHash = new("[0-9a-f]{64}", RegexOptions.Compiled);
    private const string V2Definition = "project-document-documents-v2";
    private const string V2Prompts = """{"document":"document-documents-v2","analysisSchema":"1.1.0"}""";

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

    private static string AliasReply() => DraftAliasFixtures.Valid().ToJsonString();

    private static DocumentChatWorkflowTests.FakeChatModel Model(Func<DocumentChatWorkflowTests.ChatRequest, string>? responder = null) =>
        new() { Responder = responder ?? (_ => AliasReply()) };

    // ================================================================ scenari v3

    [Fact]
    public async Task A_valid_alias_output_sends_one_call_with_aliases_only_and_commits_the_canonical_draft_with_the_three_hashes()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Model();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await Scalar<string>("SELECT COALESCE(\"ErrorMessage\", '') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
        var request = Assert.Single(model.Requests);
        Assert.Equal(DocumentSourceDraftWorkflow.InstructionsV3.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));
        Assert.Contains("[I5]", request.AllText);
        Assert.Contains("Realizzare un portale clienti", request.AllText);
        Assert.DoesNotMatch(AnyUuid, request.AllText);
        Assert.DoesNotMatch(AnyHash, request.AllText);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));

        var draft = await Row("SELECT \"Id\", \"Status\", \"ApprovalStatus\", \"AnalysisRevisionId\", \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        Assert.Equal(result.DraftId, (Guid)draft[0]);
        Assert.Equal(("Proposed", "NotApproved", s.Revision), ((string)draft[1], (string)draft[2], (Guid)draft[3]));
        var markdown = (string)draft[4];
        Assert.Contains($"[fonte:{DraftFixtures.Msg:D}]", markdown);
        Assert.Contains($"[fonte:{DraftFixtures.Pass:D}]", markdown);
        Assert.DoesNotContain("[fonte:F", markdown);
        Assert.Contains("Conflitto aperto", markdown);
        Assert.Equal(result.DraftId, await Scalar<Guid>("SELECT \"LatestDraftId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", s.Project)));
        Assert.Equal("document-draft-alias-1", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
        Assert.Equal(V3, await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", s.Run)));

        var payload = await PayloadAsync(s.Run);
        Assert.Matches("^[0-9a-f]{64}$", payload["aiOutputHash"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", payload["catalogHash"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", payload["canonicalHash"]!.GetValue<string>());
        Assert.Equal("I1", payload["ai"]!["blocks"]![0]!["items"]![0]!.GetValue<string>());   // il DTO del modello e persistito con alias
    }

    [Fact]
    public async Task Running_a_completed_alias_run_again_returns_the_committed_draft_without_a_second_call()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Model();
        var claim = await ClaimAsync(s.Run);
        var first = await RunAsync(claim, model);

        var second = await RunAsync(claim, model);

        Assert.Equal(first.DraftId, second.DraftId);
        Assert.Equal(ProcessingRunStatus.Completed, second.Status);
        Assert.Single(model.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_replay_after_the_output_was_committed_reuses_it_with_the_same_ids_without_a_new_inference()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var firstModel = Model();
        var first = await RunAsync(claim, firstModel);
        var markdownBefore = await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        var payloadBefore = await PayloadAsync(s.Run);
        await ReopenAsync(s);

        var silent = Model(_ => "{}");
        var replay = await RunAsync(claim, silent);

        Assert.Equal(ProcessingRunStatus.Completed, first.Status);
        Assert.Equal(ProcessingRunStatus.Completed, replay.Status);
        Assert.Empty(silent.Requests);
        Assert.Equal(markdownBefore, await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run)));
        Assert.Equal(payloadBefore.ToJsonString(), (await PayloadAsync(s.Run)).ToJsonString());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
    }

    [Fact]
    public async Task A_retry_with_a_new_root_on_the_same_revision_gets_the_same_canonical_ids_catalog_and_hashes()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var first = await RunAsync(await ClaimAsync(s.Run), Model());
        Assert.Equal(ProcessingRunStatus.Completed, first.Status);
        var retry = await NewGenerationAsync(client, s);
        var retryResult = await RunAsync(await ClaimAsync(retry), Model());

        Assert.Equal(ProcessingRunStatus.Completed, retryResult.Status);
        var (a, b) = (await PayloadAsync(s.Run), await PayloadAsync(retry));
        Assert.Equal(a["catalogHash"]!.GetValue<string>(), b["catalogHash"]!.GetValue<string>());
        Assert.Equal(a["aiOutputHash"]!.GetValue<string>(), b["aiOutputHash"]!.GetValue<string>());
        Assert.Equal(a["canonicalHash"]!.GetValue<string>(), b["canonicalHash"]!.GetValue<string>());
        Assert.Equal(
            await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run)),
            await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", retry)));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Theory]
    [InlineData("unknown_item_alias")]
    [InlineData("duplicate_item_alias")]
    [InlineData("unknown_source_alias")]
    [InlineData("marker_with_uuid")]
    [InlineData("uuid_instead_of_alias")]
    [InlineData("hostile_html")]
    [InlineData("v2_closed_output")]
    [InlineData("truncated_json")]
    [InlineData("fenced_json")]
    public async Task An_invalid_alias_output_fails_the_run_permanently_with_no_draft_no_reusable_output_and_no_second_call(string kind)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Model(_ =>
        {
            var output = DraftAliasFixtures.Valid();
            switch (kind)
            {
                case "unknown_item_alias":
                    output.BlockOf(2)["items"] = new JsonArray("I3", "I99");
                    break;
                case "duplicate_item_alias":
                    output.BlockOf(2)["items"] = new JsonArray("I3", "I3");
                    break;
                case "unknown_source_alias":
                    output.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F9]";
                    output.BlockOf(0)["sources"] = new JsonArray("F9");
                    break;
                case "marker_with_uuid":
                    output.BlockOf(0)["markdown"] = $"- Obiettivo [fonte:{DraftFixtures.Msg:D}]";
                    break;
                case "uuid_instead_of_alias":
                    output.BlockOf(0)["items"] = new JsonArray(DraftFixtures.Goal.ToString("D"));
                    break;
                case "hostile_html":
                    output.BlockOf(2)["markdown"] = "- Ipotesi <script>alert(1)</script>";
                    break;
                case "v2_closed_output":
                    return DraftFixtures.Valid().ToJsonString();
                case "truncated_json":
                    return output.ToJsonString()[..40];
                case "fenced_json":
                    return "```json\n" + output.ToJsonString() + "\n```";
            }

            return output.ToJsonString();
        });

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Single(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"Status\" = 'Committed'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"LatestDraftId\" IS NOT NULL", ("p", s.Project)));
    }

    [Theory]
    [InlineData("canonicalHash", DocumentWorkException.IntegrityConflict)]
    [InlineData("catalogHash", DocumentWorkException.CheckpointIncompatible)]
    [InlineData("aiOutputHash", DocumentWorkException.WorkOutputInvalid)]
    public async Task A_committed_output_whose_recomposition_diverges_is_a_permanent_error_without_inference_or_draft(string tampered, string expectedCode)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, Model())).Status);
        await ReopenAsync(s);
        await TamperPayloadAsync(s.Run, payload => payload[tampered] = new string('f', 64));

        var silent = Model(_ => "{}");
        var result = await RunAsync(claim, silent);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Empty(silent.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_committed_dto_altered_after_the_fact_is_rejected_on_recomposition_and_never_reinferred()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, Model())).Status);
        await ReopenAsync(s);
        await TamperPayloadAsync(s.Run, payload => payload["ai"]!["blocks"]![0]!["items"] = new JsonArray("I99"));

        var silent = Model(_ => "{}");
        var result = await RunAsync(claim, silent);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Empty(silent.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_confirmed_revision_with_one_irreducible_item_beyond_the_capacity_of_one_call_fails_explicitly_before_any_model_call()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, string.Join(' ', Enumerable.Repeat("parola", 3_000)));
        var model = Model();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.DocumentIncomplete, result.ErrorCode);   // T086: oltre una chiamata si pianificano unita editoriali; un item che da solo non entra e irriducibile
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    // ================================================================ compatibilita v2

    [Fact]
    public async Task A_run_already_frozen_with_v2_still_completes_with_the_v2_prompt_message_and_decoder_and_never_uses_the_alias_contract()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, definition: V2Definition, prompts: V2Prompts);
        var model = Model(_ => DraftFixtures.Valid().ToJsonString());

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        var request = Assert.Single(model.Requests);
        Assert.Equal(DocumentSourceDraftWorkflow.Instructions.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));
        Assert.Contains(DraftFixtures.Alt2.ToString("D"), request.AllText);
        Assert.Equal("document-draft-1", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
        Assert.Equal(V2Definition, await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", s.Run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run)));
    }

    [Fact]
    public async Task The_formats_never_cross_a_v2_run_rejects_the_alias_dto_and_a_v3_run_rejects_the_v2_closed_output()
    {
        using var client = await App.LoginAsync();
        var v2 = await SeedAsync(client, definition: V2Definition, prompts: V2Prompts);
        var v2Result = await RunAsync(await ClaimAsync(v2.Run), Model(_ => AliasReply()));
        var v3 = await SeedAsync(client);

        var v3Result = await RunAsync(await ClaimAsync(v3.Run), Model(_ => DraftFixtures.Valid().ToJsonString()));

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (v2Result.Status, v2Result.ErrorCode));
        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (v3Result.Status, v3Result.ErrorCode));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Theory]
    [InlineData("project-document-documents-v9", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-1","draftCatalog":"draft-alias-catalog-1","algorithm":"draft-single-v2","validation":"document-draft-validation-v2"}""")]   // definizione ignota
    [InlineData("project-document-documents-v3", """{"document":"document-documents-v2","analysisSchema":"1.1.0"}""")]                                                                                                                                               // v3 con prompt v2: mai un downgrade
    [InlineData("project-document-documents-v3", """{"document":"document-draft-v3","analysisSchema":"1.1.0"}""")]                                                                                                                                                     // v3 senza schema, catalogo, algoritmo e validazione congelati
    [InlineData("project-document-documents-v2", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-1","draftCatalog":"draft-alias-catalog-1","algorithm":"draft-single-v2","validation":"document-draft-validation-v2"}""")]   // v2 con prompt v3: mai un upgrade
    [InlineData("project-document-documents-v3", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-9","draftCatalog":"draft-alias-catalog-1","algorithm":"draft-single-v2","validation":"document-draft-validation-v2"}""")]
    [InlineData("project-document-documents-v3", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-1","draftCatalog":"draft-alias-catalog-9","algorithm":"draft-single-v2","validation":"document-draft-validation-v2"}""")]
    [InlineData("project-document-documents-v3", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-1","draftCatalog":"draft-alias-catalog-1","algorithm":"draft-single-v9","validation":"document-draft-validation-v2"}""")]
    [InlineData("project-document-documents-v3", """{"document":"document-draft-v3","analysisSchema":"1.1.0","draftAiSchema":"document-draft-alias-1","draftCatalog":"draft-alias-catalog-1","algorithm":"draft-single-v2","validation":"document-draft-validation-v9"}""")]
    public async Task An_unknown_or_incompatible_definition_is_workflow_incompatible_and_never_downgrades_or_reaches_the_model(string definition, string prompts)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, definition: definition, prompts: prompts);
        var model = Model();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, "workflow_incompatible"), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", s.Run)));
    }

    [Fact]
    public async Task The_regime_payload_of_a_v3_root_is_the_alias_message_and_dto_schema_and_of_a_v2_root_the_historical_one()
    {
        using var client = await App.LoginAsync();
        var v3 = await SeedAsync(client);
        var v2 = await SeedAsync(client, definition: V2Definition, prompts: V2Prompts);

        var (p3, p2) = (await RegimeAsync(v3.Run), await RegimeAsync(v2.Run));

        Assert.Equal(DraftAliasContracts.JsonSchema().GetRawText(), p3.SchemaJson);
        Assert.Contains("[I1]", p3.Payload!.Messages.Single().Text);
        Assert.DoesNotMatch(AnyUuid, p3.Payload.Messages.Single().Text);
        Assert.Equal(DocumentSourceDraftWorkflow.InstructionsV3, p3.Payload.Instructions);
        Assert.Equal(DocumentSourceDraftWorkflow.Instructions, p2.Payload!.Instructions);
        Assert.Contains(DraftFixtures.Alt2.ToString("D"), p2.Payload.Messages.Single().Text);
        Assert.NotEqual(p2.SchemaJson, p3.SchemaJson);
    }

    // ================================================================ supporto

    private const string V3 = "project-document-documents-v3";

    private sealed record Seeded(Guid Project, Guid Analysis, Guid Revision, Guid Run);

    /// <summary>Progetto con analisi completata, revisione 1.1.0 confermata e run Document in coda con la definizione indicata (di default la v3 congelata dalle nuove richieste).</summary>
    private async Task<Seeded> SeedAsync(HttpClient client, string goalText = "Realizzare un portale clienti", string definition = V3, string? prompts = null)
    {
        var project = (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Bozza alias {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();
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
            var content = DraftFixtures.Content(analysis, project, message.Id, goalText) with { SourceCutoffSequence = run.SourceCutoffSequence ?? 0 };
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

        var seeded = new Seeded(project, analysis, revision, Guid.Empty);
        var generation = await NewGenerationAsync(client, seeded, definition, prompts);
        return seeded with { Run = generation };
    }

    /// <summary>Nuova richiesta di Genera sulla stessa revisione; il progetto non ha documenti quindi la richiesta nasce 001 e si congela a mano come fara T123.</summary>
    private async Task<Guid> NewGenerationAsync(HttpClient client, Seeded s, string definition = V3, string? prompts = null)
    {
        var generate = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{s.Project}/analyses/{s.Analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = s.Revision }) };
        generate.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var accepted = await client.SendAsync(generate, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var generation = (await Json(accepted)).GetProperty("id").GetGuid();
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r",
            ("d", definition), ("p", prompts ?? SourceSnapshotStore.DocumentPromptVersions()), ("r", generation));
        return generation;
    }

    /// <summary>Presa in carico come il dispatcher: Running con owner, fence e lease validi.</summary>
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

    /// <summary>Riporta un run completato allo stato «WorkOutput Committed, commit non avvenuto» (come dopo un crash prima del commit di dominio): nessuna bozza, run Running, checkpoint azzerati.</summary>
    private async Task ReopenAsync(Seeded s) => await Execute(
        """
        ALTER TABLE "ProcessingRuns" DISABLE TRIGGER USER;
        ALTER TABLE "Drafts" DISABLE TRIGGER USER;
        UPDATE "Projects" SET "LatestDraftId" = NULL WHERE "Id" = @p;
        UPDATE "ProcessingRuns" SET "DraftId" = NULL, "Status" = 'Running', "CompletedAt" = NULL, "ErrorCode" = NULL, "ErrorMessage" = NULL,
               "LeaseOwner" = 'test-owner', "FencingToken" = 1, "LeaseUntil" = now() + interval '10 minutes' WHERE "Id" = @r;
        DELETE FROM "Drafts" WHERE "ProcessingRunId" = @r;
        DELETE FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = @r;
        ALTER TABLE "Drafts" ENABLE TRIGGER USER;
        ALTER TABLE "ProcessingRuns" ENABLE TRIGGER USER;
        """,
        ("p", s.Project), ("r", s.Run));

    private async Task<JsonObject> PayloadAsync(Guid run) =>
        JsonNode.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)))!.AsObject();

    /// <summary>Altera il payload di un WorkOutput mantenendone coerente l'OutputHash, cosi il lookup lo accetta e solo la ricomposizione puo accorgersene.</summary>
    private async Task TamperPayloadAsync(Guid run, Action<JsonObject> change)
    {
        var payload = await PayloadAsync(run);
        change(payload);
        var text = payload.ToJsonString();
        await Execute(
            "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; UPDATE \"WorkOutputs\" SET \"PayloadJson\" = @p::jsonb, \"OutputHash\" = @h WHERE \"OwnerId\" = @r; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;",
            ("p", text), ("h", CanonicalJson.Sha256Hex(text)), ("r", run));
    }

    private (InternalModelClient Client, DocumentSourceDraftWorkflow Workflow) Build(DocumentChatWorkflowTests.FakeChatModel model, DocumentOptions? documents = null)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        return (client, new DocumentSourceDraftWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(documents ?? new DocumentOptions())));
    }

    private async Task<DocumentRunResult> RunAsync(DocumentRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model, DocumentOptions? documents = null)
    {
        var (client, workflow) = Build(model, documents);
        using (client)
        {
            return await workflow.ExecuteAsync(claim, Ct);
        }
    }

    private async Task<RegimePayload> RegimeAsync(Guid run)
    {
        var (client, workflow) = Build(Model());
        using (client)
        {
            return await workflow.BuildRegimePayloadAsync(run, Ct);
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();

    private async Task<long> Count(string sql, params (string Name, object Value)[] parameters) => await Scalar<long>(sql, parameters);

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    private async Task<object[]> Row(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        return values;
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
