using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>Revisione confermata 1.1 e output valido del modello per i test della generazione documentale piccola (T061).</summary>
internal static class DraftFixtures
{
    public static readonly Guid Msg = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    public static readonly Guid Pass = Guid.Parse("a1000000-0000-4000-8000-000000000002");
    public static readonly Guid Goal = Guid.Parse("b1000000-0000-4000-8000-000000000001");
    public static readonly Guid Req = Guid.Parse("b1000000-0000-4000-8000-000000000002");
    public static readonly Guid Assump = Guid.Parse("b1000000-0000-4000-8000-000000000003");
    public static readonly Guid Question = Guid.Parse("b1000000-0000-4000-8000-000000000004");
    public static readonly Guid Alt1 = Guid.Parse("b1000000-0000-4000-8000-000000000005");
    public static readonly Guid Alt2 = Guid.Parse("b1000000-0000-4000-8000-000000000006");
    public static readonly Guid ConflictId = Guid.Parse("b1000000-0000-4000-8000-0000000000c1");

    private static DocumentAnalysisItem Item(Guid id, string text, DocumentAnalysisClassification classification, params Guid[] refs) =>
        new() { Id = id, Text = text, Classification = classification, EvidenceRefs = refs };

    public static DocumentAnalysisContent Content(Guid analysisId, Guid projectId, Guid messageId, string goalText = "Realizzare un portale clienti") =>
        new()
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = analysisId, ProjectId = projectId, SourceCutoffSequence = 1,
            ValidationState = AnalysisContentState.PmConfirmed,
            Sources =
            [
                new DocumentAnalysisSource
                {
                    Id = Msg, Kind = DocumentAnalysisSourceKind.Message, MessageId = messageId, MessageRole = AnalysisMessageRole.User, RevisionId = null, DocumentId = null,
                    DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
                },
                new DocumentAnalysisSource
                {
                    Id = Pass, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = Guid.NewGuid(),
                    DocumentVersionId = Guid.NewGuid(), PreparationId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = 14, Quote = "quattordici ch",
                },
            ],
            Goals = [Item(Goal, goalText, DocumentAnalysisClassification.Declared, Msg)],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = [Item(Req, "Il portale mostra lo stato dei ticket", DocumentAnalysisClassification.Documented, Pass)],
            OperationalFlows = [], Constraints = [], Dependencies = [],
            Assumptions = [Item(Assump, "I clienti usano un browser desktop", DocumentAnalysisClassification.Assumed)],
            OpenQuestions = [Item(Question, "Quanti clienti useranno il portale?", DocumentAnalysisClassification.Inferred)],
            Conflicts =
            [
                new DocumentAnalysisConflict
                {
                    Id = ConflictId, Description = "La fatturazione e dentro o fuori dal perimetro?", Status = AnalysisConflictStatus.Open, Resolution = null,
                    Alternatives =
                    [
                        Item(Alt1, "Fatturazione nel portale", DocumentAnalysisClassification.Declared, Msg),
                        Item(Alt2, "Fatturazione fuori perimetro", DocumentAnalysisClassification.Documented, Pass),
                    ],
                },
            ],
        };

    public static JsonObject Block(Guid[] items, string markdown, Guid[] sources) => new()
    {
        ["itemIds"] = new JsonArray([.. items.Select(i => (JsonNode)JsonValue.Create(i.ToString("D"))!)]),
        ["markdown"] = markdown,
        ["sourceIds"] = new JsonArray([.. sources.Select(i => (JsonNode)JsonValue.Create(i.ToString("D"))!)]),
    };

    public static JsonObject Valid() => new()
    {
        ["sectionKey"] = "documento",
        ["blocks"] = new JsonArray(
            Block([Goal], $"- Obiettivo: realizzare un portale clienti [fonte:{Msg:D}]", [Msg]),
            Block([Req], $"- Requisito: il portale mostra lo stato dei ticket [fonte:{Pass:D}]", [Pass]),
            Block([Assump], "- Ipotesi da verificare: i clienti usano un browser desktop", []),
            Block([Question], "- Da definire: quanti clienti useranno il portale?", []),
            Block([Alt1, Alt2], $"Conflitto aperto. Alternativa 1: fatturazione nel portale [fonte:{Msg:D}]. Alternativa 2: fatturazione fuori perimetro [fonte:{Pass:D}].", [Msg, Pass])),
    };

    public static JsonObject Blocks(this JsonObject o, int index) => o["blocks"]!.AsArray()[index]!.AsObject();
}

/// <summary>
/// T061 (DET) — contratto chiuso dell'output della generazione documentale piccola e validazione contro il pacchetto fidato della revisione
/// confermata 1.1: item del pacchetto tutti rappresentati, marcatori <c>[fonte:UUID]</c> atomici coerenti con sourceIds e con le fonti degli item,
/// Markdown senza HTML/immagini/schemi pericolosi, nessuna riparazione.
/// </summary>
public sealed class DocumentDraftContractTests
{
    private static readonly DocumentDraftPackage Package =
        DocumentDraftPackage.From(DraftFixtures.Content(Guid.Parse("c0000000-0000-4000-8000-000000000001"), Guid.Parse("c0000000-0000-4000-8000-000000000002"), Guid.NewGuid()));

    private static DraftValidation Validate(JsonObject output) => DocumentDraftValidator.ValidateBlocks(output.ToJsonString(), Package);

    [Fact]
    public void A_complete_closed_output_is_accepted_and_the_markdown_is_the_blocks_in_order_without_llm_post_processing()
    {
        var output = DraftFixtures.Valid();

        var result = Validate(output);

        Assert.True(result.Accepted, string.Join(" | ", result.Violations));
        Assert.Equal("documento", result.Output!.SectionKey);
        Assert.Equal(5, result.Output.Blocks.Count);
        Assert.Equal(string.Join("\n\n", output["blocks"]!.AsArray().Select(b => b!["markdown"]!.GetValue<string>().Trim())), result.Markdown);
        Assert.Equal([DraftFixtures.Alt1, DraftFixtures.Alt2], result.Output.Blocks[4].ItemIds);
    }

    [Fact]
    public void The_package_holds_every_item_including_conflict_alternatives_with_their_sources()
    {
        Assert.Equal(6, Package.ItemSources.Count);
        Assert.Equal([DraftFixtures.Pass], Package.ItemSources[DraftFixtures.Alt2]);
        Assert.Empty(Package.ItemSources[DraftFixtures.Assump]);
        Assert.Equal([DraftFixtures.Msg, DraftFixtures.Pass], Package.SourceIds.Order());
    }

    public static TheoryData<string, string> Rejected() => new()
    {
        { "invented_source", "fonti:" },
        { "html", "markdown:" },
        { "image", "markdown:" },
        { "javascript_link", "markdown:" },
        { "missing_alternative", "itemIds:" },
        { "unknown_item", "itemIds:" },
        { "source_not_of_items", "fonti:" },
        { "marker_without_declared_source", "fonti:" },
        { "declared_source_without_marker", "fonti:" },
        { "item_with_sources_never_cited", "fonti:" },
        { "multi_id_marker", "fonti:" },
        { "extra_property", "struttura:" },
        { "block_extra_property", "struttura:" },
        { "section_key_invalid", "struttura:" },
        { "empty_blocks", "struttura:" },
        { "blank_markdown", "markdown:" },
        { "uppercase_uuid", "struttura:" },
        { "item_repeated_in_block", "itemIds:" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void An_invalid_output_is_rejected_with_a_specific_violation_and_exposes_nothing(string kind, string prefix)
    {
        var output = DraftFixtures.Valid();
        var other = Guid.NewGuid();
        switch (kind)
        {
            case "invented_source":
                output.Blocks(0)["markdown"] = $"- Obiettivo [fonte:{other:D}]";
                output.Blocks(0)["sourceIds"] = new JsonArray(other.ToString("D"));
                break;
            case "html":
                output.Blocks(2)["markdown"] = "- Ipotesi <script>alert(1)</script>";
                break;
            case "image":
                output.Blocks(2)["markdown"] = "- Ipotesi ![x](http://example.test/x.png)";
                break;
            case "javascript_link":
                output.Blocks(2)["markdown"] = "- Ipotesi [clic](javascript:alert(1))";
                break;
            case "missing_alternative":
                output["blocks"]!.AsArray()[4] = DraftFixtures.Block([DraftFixtures.Alt1], $"Alternativa 1 [fonte:{DraftFixtures.Msg:D}]", [DraftFixtures.Msg]);
                break;
            case "unknown_item":
                output.Blocks(2)["itemIds"] = new JsonArray(DraftFixtures.Assump.ToString("D"), other.ToString("D"));
                break;
            case "source_not_of_items":
                // La fonte esiste nella revisione, ma non e una fonte dell'obiettivo.
                output.Blocks(0)["markdown"] = $"- Obiettivo [fonte:{DraftFixtures.Pass:D}]";
                output.Blocks(0)["sourceIds"] = new JsonArray(DraftFixtures.Pass.ToString("D"));
                break;
            case "marker_without_declared_source":
                output.Blocks(0)["sourceIds"] = new JsonArray();
                break;
            case "declared_source_without_marker":
                output.Blocks(2)["sourceIds"] = new JsonArray(DraftFixtures.Msg.ToString("D"));
                break;
            case "item_with_sources_never_cited":
                output.Blocks(0)["markdown"] = "- Obiettivo senza fonte";
                output.Blocks(0)["sourceIds"] = new JsonArray();
                break;
            case "multi_id_marker":
                output.Blocks(0)["markdown"] = $"- Obiettivo [fonte:{DraftFixtures.Msg:D},{DraftFixtures.Pass:D}]";
                break;
            case "extra_property":
                output["extra"] = 1;
                break;
            case "block_extra_property":
                output.Blocks(0)["note"] = "x";
                break;
            case "section_key_invalid":
                output["sectionKey"] = "Sezione con spazi";
                break;
            case "empty_blocks":
                output["blocks"] = new JsonArray();
                break;
            case "blank_markdown":
                output.Blocks(2)["markdown"] = "   ";
                break;
            case "uppercase_uuid":
                output.Blocks(0)["itemIds"] = new JsonArray(DraftFixtures.Goal.ToString("D").ToUpperInvariant());
                break;
            case "item_repeated_in_block":
                output.Blocks(2)["itemIds"] = new JsonArray(DraftFixtures.Assump.ToString("D"), DraftFixtures.Assump.ToString("D"));
                break;
            default:
                throw new InvalidOperationException(kind);
        }

        var result = Validate(output);

        Assert.False(result.Accepted);
        Assert.Null(result.Output);
        Assert.Null(result.Markdown);
        Assert.Contains(result.Violations, v => v.StartsWith(prefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"sectionKey\":\"documento\",\"blocks\":")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"testo\"")]
    public void Text_that_is_not_the_closed_object_is_rejected(string text)
    {
        var result = DocumentDraftValidator.ValidateBlocks(text, Package);

        Assert.False(result.Accepted);
        Assert.Null(result.Output);
    }

    [Fact]
    public void A_fenced_or_prefixed_json_and_duplicate_properties_are_rejected_without_repair()
    {
        var json = DraftFixtures.Valid().ToJsonString();

        Assert.False(DocumentDraftValidator.ValidateBlocks("```json\n" + json + "\n```", Package).Accepted);
        Assert.False(DocumentDraftValidator.ValidateBlocks("Ecco il documento: " + json, Package).Accepted);
        Assert.False(DocumentDraftValidator.ValidateBlocks(json.Replace("\"sectionKey\":\"documento\"", "\"sectionKey\":\"documento\",\"sectionKey\":\"altro\""), Package).Accepted);
    }

    [Fact]
    public void An_item_without_sources_needs_no_marker_but_an_item_with_sources_needs_one_of_its_own_in_a_block_that_contains_it()
    {
        // Obiettivo in due blocchi: basta che uno citi la sua fonte.
        var output = DraftFixtures.Valid();
        var blocks = output["blocks"]!.AsArray();
        blocks.Add(DraftFixtures.Block([DraftFixtures.Goal], "- Ripresa senza fonte", []));

        Assert.True(Validate(output).Accepted);
    }
}

/// <summary>
/// T061 (PG, IChatClient finto a livello HTTP) — generazione documentale piccola sul grafo T043: una chiamata sulla revisione confermata 1.1 congelata,
/// output chiuso validato contro il pacchetto fidato PRIMA del WorkOutput, un solo Draft Proposed/NotApproved + Completed + LatestDraftId nella stessa
/// transazione fenced. Nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentSourceDraftWorkflowTests : IAsyncLifetime
{
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
    public async Task A_valid_output_sends_one_call_and_commits_draft_completed_and_latest_draft_together()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => DraftFixtures.Valid().ToJsonString() };

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        var request = Assert.Single(model.Requests);
        Assert.Contains(DraftFixtures.Alt2.ToString("D"), request.AllText);
        Assert.Contains("Realizzare un portale clienti", request.AllText);
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));

        var draft = await Row("SELECT \"Id\", \"Status\", \"ApprovalStatus\", \"AnalysisRevisionId\", \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        Assert.Equal(result.DraftId, (Guid)draft[0]);
        Assert.Equal("Proposed", draft[1]);
        Assert.Equal("NotApproved", draft[2]);
        Assert.Equal(s.Revision, (Guid)draft[3]);
        var markdown = (string)draft[4];
        Assert.Contains($"[fonte:{DraftFixtures.Msg:D}]", markdown);
        Assert.Contains($"[fonte:{DraftFixtures.Pass:D}]", markdown);
        Assert.Contains("Conflitto aperto", markdown);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(result.DraftId, await Scalar<Guid>("SELECT \"LatestDraftId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", s.Project)));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
    }

    [Theory]
    [InlineData("invented_source")]
    [InlineData("hostile_html")]
    [InlineData("missing_alternative")]
    [InlineData("truncated_json")]
    [InlineData("fenced_json")]
    public async Task An_invalid_output_fails_the_run_permanently_with_no_draft_no_reusable_output_and_no_second_call(string kind)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var other = Guid.NewGuid();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ =>
            {
                var output = DraftFixtures.Valid();
                switch (kind)
                {
                    case "invented_source":
                        output.Blocks(0)["markdown"] = $"- Obiettivo [fonte:{other:D}]";
                        output.Blocks(0)["sourceIds"] = new JsonArray(other.ToString("D"));
                        break;
                    case "hostile_html":
                        output.Blocks(2)["markdown"] = "- Ipotesi <script>alert(1)</script>";
                        break;
                    case "missing_alternative":
                        output["blocks"]!.AsArray()[4] = DraftFixtures.Block([DraftFixtures.Alt1], $"Alternativa 1 [fonte:{DraftFixtures.Msg:D}]", [DraftFixtures.Msg]);
                        break;
                    case "truncated_json":
                        return output.ToJsonString()[..40];
                    case "fenced_json":
                        return "```json\n" + output.ToJsonString() + "\n```";
                }

                return output.ToJsonString();
            },
        };

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.Single(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"Status\" = 'Committed'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"LatestDraftId\" IS NOT NULL", ("p", s.Project)));
    }

    [Fact]
    public async Task A_newer_revision_created_during_the_call_does_not_change_the_frozen_input_and_the_draft_binds_to_the_confirmed_revision()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => DraftFixtures.Valid().ToJsonString(),
            OnRequest = async _ => await Execute(
                """
                INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","Origin","ParentRevisionId","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState")
                SELECT gen_random_uuid(), "AnalysisId", 2, 'PmEdit', "Id", "CreatedBy", now(), "SchemaVersion", "ContentJson", 'Unvalidated'
                FROM "AnalysisRevisions" WHERE "Id" = @rev
                """,
                ("rev", s.Revision)),
        };

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @a", ("a", s.Analysis)));
        Assert.Equal(s.Revision, await Scalar<Guid>("SELECT \"AnalysisRevisionId\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run)));
    }

    [Fact]
    public async Task A_lease_taken_over_during_the_call_publishes_no_draft_and_does_not_complete_the_run()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => DraftFixtures.Valid().ToJsonString(),
            OnRequest = async _ => await Execute("UPDATE \"ProcessingRuns\" SET \"FencingToken\" = \"FencingToken\" + 1, \"LeaseOwner\" = 'altro-owner' WHERE \"Id\" = @r", ("r", s.Run)),
        };

        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(await ClaimAsync(s.Run), model));

        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"LatestDraftId\" IS NOT NULL", ("p", s.Project)));
    }

    [Fact]
    public async Task Running_the_same_run_again_after_completion_returns_the_committed_draft_without_a_second_call()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => DraftFixtures.Valid().ToJsonString() };
        var claim = await ClaimAsync(s.Run);
        var first = await RunAsync(claim, model);

        var second = await RunAsync(claim, model);

        Assert.Equal(first.DraftId, second.DraftId);
        Assert.Equal(ProcessingRunStatus.Completed, second.Status);
        Assert.Single(model.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_stale_fencing_token_touches_nothing_and_never_publishes_a_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => DraftFixtures.Valid().ToJsonString() };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim with { FencingToken = claim.FencingToken + 5 }, model));

        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\""));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
    }

    [Fact]
    public async Task A_run_with_the_001_definition_is_incompatible_and_never_reaches_the_model()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-document-v1' WHERE \"Id\" = @r", ("r", s.Run));
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("workflow_incompatible", result.ErrorCode);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_confirmed_revision_beyond_the_capacity_of_one_call_fails_explicitly_without_summarizing_and_before_any_model_call()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, string.Join(' ', Enumerable.Repeat("parola", 3_000)));
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CallLimitExceeded, result.ErrorCode);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task The_run_loader_dispatches_on_the_revision_schema_and_never_misreads_1_1_as_1_0_or_the_reverse()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        using var scope = App.Services.CreateScope();
        var store = new DocumentRunCompletion(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        var input = await store.LoadDocumentInputAsync(s.Run, Ct);

        Assert.Equal(s.Revision, input.RevisionId);
        Assert.Equal(DocumentAnalysisContent.SchemaVersionValue, input.Content.SchemaVersion);
        Assert.Equal(6, DocumentDraftPackage.From(input.Content).ItemSources.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadInputAsync(s.Run, Ct));
    }

    // ================================================================ supporto

    private sealed record Seeded(Guid Project, Guid Analysis, Guid Revision, Guid Run);

    /// <summary>Progetto con analisi completata, revisione 1.1.0 confermata e run Document in coda sulla definizione documentale (scritta come farà T123).</summary>
    private async Task<Seeded> SeedAsync(HttpClient client, string goalText = "Realizzare un portale clienti")
    {
        var project = (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Bozza doc {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();
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

        var generate = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses/{analysis}/document-generations") { Content = JsonContent.Create(new { revisionId = revision }) };
        generate.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var accepted = await client.SendAsync(generate, Ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var generation = (await Json(accepted)).GetProperty("id").GetGuid();
        await Execute(
            "UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = 'project-document-documents-v2', \"PromptVersions\" = @p::jsonb WHERE \"Id\" = @r",
            ("p", "{\"document\":\"document-documents-v2\",\"analysisSchema\":\"1.1.0\"}"), ("r", generation));
        return new Seeded(project, analysis, revision, generation);
    }

    /// <summary>Presa in carico come il dispatcher: Running con owner, fence e lease validi (il claim reale non e parte di T061).</summary>
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
