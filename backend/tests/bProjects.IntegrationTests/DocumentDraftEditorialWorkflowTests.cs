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
/// T086 (MAF-PG, IChatClient finto a livello HTTP) — generazione documentale oltre la capacità di una chiamata (contracts/ai-workflow.md §4.5): il planner editoriale (T085)
/// suddivide la revisione confermata in unità misurate; ogni unità è un batch del grafo T043 con output validato contro i SOLI suoi item principali; l'assemblaggio è
/// deterministico (nessuna chiamata finale riduttiva) e Draft + Completed sono committati insieme solo a copertura completa. Un output o un'unità non valida non lascia mai
/// un Markdown parziale presentato come bozza. Nessun DGX: il modello è finto e le finestre sono quelle del finto.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentDraftEditorialWorkflowTests : IAsyncLifetime
{
    private const string V3 = "project-document-documents-v3";
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

    // ================================================================ contenuto e modello finto

    private static string Words(int count, string prefix) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    /// <summary>
    /// Revisione confermata oltre la capacità di una chiamata (finestra 4000, uscita 1500): un obiettivo, <paramref name="requirements"/> requisiti di
    /// <paramref name="words"/> parole che citano l'intestazione condivisa e la propria pagina, e un conflitto aperto con due alternative. Alias: I1 obiettivo, I2.. requisiti,
    /// poi le alternative; F1 messaggio, F2 intestazione, F3.. pagine.
    /// </summary>
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

    private static readonly Regex Primary = ElementRegex("ELEMENTO");
    private static readonly Regex Context = ElementRegex("CONTESTO");

    private static Regex ElementRegex(string tag) =>
        new($@"<<<{tag} \[(I\d+)\][^>\n]*?(?:fonti=([^>\n]*))?>>>\n(.*?)\n<<<FINE {tag}>>>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Elementi principali e di contesto del messaggio di una unità, come li vede il modello.</summary>
    private static List<Element> Elements(DocumentChatWorkflowTests.ChatRequest request)
    {
        var text = request.AllText;
        IEnumerable<Element> Parse(Regex regex, bool primary) => regex.Matches(text).Select(m => new Element(
            m.Groups[1].Value, m.Groups[3].Value,
            m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), primary));
        return [.. Parse(Primary, true), .. Parse(Context, false)];
    }

    /// <summary>Risposta valida del modello per l'unità: un blocco per ogni elemento principale, con le sue fonti come marcatori atomici nell'ordine dato.</summary>
    private static JsonObject ValidFor(DocumentChatWorkflowTests.ChatRequest request, Func<Element, string[]>? sources = null)
    {
        var blocks = new JsonArray();
        foreach (var e in Elements(request).Where(e => e.Primary))
        {
            var cited = sources?.Invoke(e) ?? e.Sources;
            blocks.Add(DraftAliasFixtures.Block([e.Alias], $"- {string.Join(' ', e.Text.Split(' ').Take(6))} {string.Concat(cited.Select(s => $"[fonte:{s}] "))}".Trim(), cited));
        }

        return new JsonObject { ["sectionKey"] = "documento", ["blocks"] = blocks };
    }

    private static DocumentChatWorkflowTests.FakeChatModel Model(Func<DocumentChatWorkflowTests.ChatRequest, string>? responder = null, string finishReason = "stop") =>
        new() { Responder = responder ?? (r => ValidFor(r).ToJsonString()), FinishReason = finishReason };

    // ================================================================ scenari

    [Fact]
    public async Task A_revision_beyond_one_call_is_split_into_bounded_units_and_assembled_into_a_single_complete_draft_without_a_final_reducing_call()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Model();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        Assert.True(model.Requests.Count > 1, "attesa piu di una unita");
        // Ogni elemento della revisione e' principale di UNA sola unita' e le unita' sono nell'ordine stabile della revisione.
        var primaries = model.Requests.Select(r => Elements(r).Where(e => e.Primary).Select(e => e.Alias).ToList()).ToList();
        var flat = primaries.SelectMany(p => p).ToList();
        Assert.Equal(Enumerable.Range(1, 15).Select(i => "I" + i), flat);
        Assert.All(primaries, p => Assert.NotEmpty(p));
        // Nessuna chiamata finale: tante chiamate quante unita', una per output Committed, e il contatore del run le conta tutte.
        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run)));
        // Il messaggio non contiene mai UUID o hash: soli alias; il conflitto non e' mai spezzato senza stato.
        Assert.All(model.Requests, r => Assert.DoesNotMatch("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", r.AllText));
        Assert.All(model.Requests, r => Assert.DoesNotMatch("[0-9a-f]{64}", r.AllText));

        var draft = await Row("SELECT \"Id\", \"Status\", \"ApprovalStatus\", \"AnalysisRevisionId\", \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        Assert.Equal(result.DraftId, (Guid)draft[0]);
        Assert.Equal(("Proposed", "NotApproved", s.Revision), ((string)draft[1], (string)draft[2], (Guid)draft[3]));
        var markdown = (string)draft[4];
        for (var i = 0; i < 12; i++)
        {
            Assert.Contains($"Requisito {i} r{i}x0", markdown);
        }

        Assert.True(markdown.IndexOf("Requisito 0 ", StringComparison.Ordinal) < markdown.IndexOf("Requisito 11 ", StringComparison.Ordinal));
        Assert.Contains($"[fonte:{Hdr:D}]", markdown);
        Assert.DoesNotMatch(@"\[fonte:F\d", markdown);
        Assert.Equal(result.DraftId, await Scalar<Guid>("SELECT \"LatestDraftId\" FROM \"Projects\" WHERE \"Id\" = @p", ("p", s.Project)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Theory]
    [InlineData("foreign_source")]
    [InlineData("foreign_item")]
    public async Task Each_unit_output_is_validated_against_its_own_primary_items_so_a_foreign_source_or_a_foreign_item_fails_the_run_without_any_draft(string kind)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var calls = 0;
        var model = Model(r =>
        {
            calls++;
            var output = ValidFor(r);
            if (calls != 2)
            {
                return output.ToJsonString();
            }

            var first = output["blocks"]![0]!.AsObject();
            switch (kind)
            {
                case "foreign_source":
                    // F3 e' la pagina di un requisito di un'altra unita': esiste nel catalogo ma non sostiene nessun item di questa.
                    first["markdown"] = first["markdown"]!.GetValue<string>() + " [fonte:F14]";
                    first["sources"] = new JsonArray([.. first["sources"]!.AsArray().Select(x => (JsonNode)x!.GetValue<string>()), "F14"]);
                    break;
                case "foreign_item":
                    first["items"] = new JsonArray("I15");
                    break;
            }

            return output.ToJsonString();
        });

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Equal(2, model.Requests.Count);   // nessuna riparazione ne' secondo tentativo: l'unita' fallita non si reinvia
        await AssertNoDraftAsync(s);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));   // la prima unita' resta riusabile
    }

    [Fact]
    public async Task A_unit_that_omits_one_of_its_primary_items_fails_permanently_without_a_second_call_and_without_a_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var calls = 0;
        var model = Model(r =>
        {
            calls++;
            var output = ValidFor(r);
            if (calls == 3)
            {
                output["blocks"]!.AsArray().RemoveAt(0);
            }

            return output.ToJsonString();
        });

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Equal(3, model.Requests.Count);
        await AssertNoDraftAsync(s);
        Assert.Equal(2, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
    }

    [Theory]
    [InlineData("truncated_json")]
    [InlineData("fenced_json")]
    [InlineData("v2_closed_output")]
    [InlineData("hostile_html")]
    public async Task A_malformed_or_hostile_unit_output_fails_permanently_and_never_becomes_a_partial_draft(string kind)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Model(r =>
        {
            var output = ValidFor(r);
            return kind switch
            {
                "truncated_json" => output.ToJsonString()[..40],
                "fenced_json" => "```json\n" + output.ToJsonString() + "\n```",
                "v2_closed_output" => DraftFixtures.Valid().ToJsonString(),
                _ => Hostile(output),
            };
        });

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        await AssertNoDraftAsync(s);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
    }

    private static string Hostile(JsonObject output)
    {
        output["blocks"]![0]!["markdown"] = "- <script>alert(1)</script>";
        output["blocks"]![0]!["sources"] = new JsonArray();
        return output.ToJsonString();
    }

    [Fact]
    public async Task A_unit_truncated_by_the_output_limit_is_incomplete_even_if_the_json_is_closed_and_leaves_no_output_nor_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Model(finishReason: "length");

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        await AssertNoDraftAsync(s);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
    }

    [Fact]
    public async Task Two_multipage_units_keep_every_source_marker_of_every_item_in_the_requested_order_and_persist_the_historical_source_map()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, requirements: 8, words: 450);
        var model = Model(r => ValidFor(r, e => e.Sources.Reverse().ToArray()).ToJsonString());   // ordine delle parti richiesto: prima la pagina, poi l'intestazione condivisa

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        Assert.True(model.Requests.Count >= 2);
        var markdown = await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        // L'intestazione condivisa e' citata una volta per requisito, ogni volta DOPO la pagina propria (ordine richiesto), mai fusa in un'unica citazione.
        var page0 = $"[fonte:{Guid.Parse("a3000000-0000-4000-8000-000000000001"):D}] [fonte:{Hdr:D}]";
        Assert.Contains(page0, markdown);
        Assert.True(Regex.Matches(markdown, Regex.Escape($"[fonte:{Hdr:D}]")).Count >= 8);

        var maps = await Rows("SELECT \"MapsJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed' ORDER BY \"CommittedAt\"", ("r", s.Run));
        var entries = maps.Select(m => JsonNode.Parse((string)m[0]!)!["sourceMap"]!.AsArray()).SelectMany(a => a).Select(n => n!.AsObject()).ToList();
        Assert.NotEmpty(entries);
        // Ogni entry e' (itemId, evidenceId, citationGroupId, part) + sourceId: senza registro Evidence i campi del gruppo sono null, MAI inventati.
        Assert.All(entries, e =>
        {
            Assert.True(Guid.TryParse(e["itemId"]!.GetValue<string>(), out _));
            Assert.True(Guid.TryParse(e["sourceId"]!.GetValue<string>(), out _));
            Assert.Null(e["evidenceId"]);
            Assert.Null(e["citationGroupId"]);
            Assert.Null(e["part"]);
        });
        // L'intestazione condivisa compare nella mappa una volta per ogni item che la cita (due gruppi con lo stesso header restano due voci distinte).
        Assert.True(entries.Count(e => e["sourceId"]!.GetValue<string>() == Hdr.ToString("D")) >= 8);
    }

    [Fact]
    public async Task The_persisted_source_map_keeps_two_citation_groups_that_share_the_same_header_source_as_distinct_entries_from_the_evidence_ledger()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var (evA, evB, groupA, groupB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (page0, page1) = (Guid.Parse("a3000000-0000-4000-8000-000000000001"), Guid.Parse("a3000000-0000-4000-8000-000000000002"));
        var (item0, item1) = (Guid.Parse("b2000000-0000-4000-8000-000000000010"), Guid.Parse("b2000000-0000-4000-8000-000000000011"));
        await SeedLedgerAsync(s, (item0, evA, groupA, [Hdr, page0]), (item1, evB, groupB, [Hdr, page1]));

        var result = await RunAsync(await ClaimAsync(s.Run), Model());

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        var maps = await Rows("SELECT \"MapsJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run));
        var entries = maps.SelectMany(m => JsonNode.Parse((string)m[0]!)!["sourceMap"]!.AsArray()).Select(n => n!.AsObject()).ToList();
        JsonObject Entry(Guid item, Guid source) => Assert.Single(entries, e => e["itemId"]!.GetValue<string>() == item.ToString("D") && e["sourceId"]!.GetValue<string>() == source.ToString("D"));
        var (a, b) = (Entry(item0, Hdr), Entry(item1, Hdr));
        Assert.Equal((evA.ToString("D"), groupA.ToString("D"), 0), (a["evidenceId"]!.GetValue<string>(), a["citationGroupId"]!.GetValue<string>(), a["part"]!.GetValue<int>()));
        Assert.Equal((evB.ToString("D"), groupB.ToString("D"), 0), (b["evidenceId"]!.GetValue<string>(), b["citationGroupId"]!.GetValue<string>(), b["part"]!.GetValue<int>()));
        Assert.Equal(1, Entry(item0, page0)["part"]!.GetValue<int>());
        Assert.Equal(1, Entry(item1, page1)["part"]!.GetValue<int>());
        // Gli item senza evidenza nel registro (obiettivo, altri requisiti) restano mappati con i campi di gruppo null.
        Assert.All(entries.Where(e => e["itemId"]!.GetValue<string>() == "b2000000-0000-4000-8000-000000000001"), e => Assert.Null(e["citationGroupId"]));
    }

    private static readonly (Guid Item, Guid Page) Req0 = (Guid.Parse("b2000000-0000-4000-8000-000000000010"), Guid.Parse("a3000000-0000-4000-8000-000000000001"));
    private static readonly (Guid Item, Guid Page) Req1 = (Guid.Parse("b2000000-0000-4000-8000-000000000011"), Guid.Parse("a3000000-0000-4000-8000-000000000002"));

    private static string Flat(JsonObject e) =>
        string.Join("|", new[] { "itemId", "evidenceId", "citationGroupId", "part", "sourceId" }.Select(k => e[k]?.ToJsonString() ?? "null"));

    private async Task<(Seeded S, Guid EvA, Guid EvB, Guid GroupA, Guid GroupB)> CompletedWithLedgerAsync(HttpClient client)
    {
        var s = await SeedAsync(client);
        var (evA, evB, groupA, groupB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedLedgerAsync(s, (Req0.Item, evA, groupA, [Hdr, Req0.Page]), (Req1.Item, evB, groupB, [Hdr, Req1.Page]));
        var result = await RunAsync(await ClaimAsync(s.Run), Model());
        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        return (s, evA, evB, groupA, groupB);
    }

    [Fact]
    public async Task The_draft_view_exposes_the_persisted_source_map_of_its_own_run_and_revision_keeping_two_groups_that_share_the_header_distinct()
    {
        using var client = await App.LoginAsync();
        var (s, evA, evB, groupA, groupB) = await CompletedWithLedgerAsync(client);

        var response = await client.GetAsync($"/api/projects/{s.Project}/draft", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        var map = view.GetProperty("sourceMap").EnumerateArray().Select(e => e.Clone()).ToList();
        // Esattamente la mappa persistita nei WorkOutput del run, nell'ordine delle unita', senza campi aggiunti ne' dedotti.
        var persisted = (await Rows("SELECT \"PayloadJson\"->>'ordinal', (\"PayloadJson\"->'sourceMap')::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r ORDER BY (\"PayloadJson\"->>'ordinal')::int", ("r", s.Run)))
            .SelectMany(r => JsonNode.Parse((string)r[1]!)!.AsArray()).Select(n => Flat(n!.AsObject())).ToList();
        Assert.Equal(persisted, map.Select(e => Flat(JsonNode.Parse(e.GetRawText())!.AsObject())));
        Assert.All(map, e => Assert.Equal(
            ["citationGroupId", "evidenceId", "itemId", "part", "sourceId"], e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));

        JsonElement Entry(Guid item, Guid source) => Assert.Single(map, e => e.GetProperty("itemId").GetGuid() == item && e.GetProperty("sourceId").GetGuid() == source);
        var (a, b) = (Entry(Req0.Item, Hdr), Entry(Req1.Item, Hdr));
        Assert.Equal((evA, groupA, 0), (a.GetProperty("evidenceId").GetGuid(), a.GetProperty("citationGroupId").GetGuid(), a.GetProperty("part").GetInt32()));
        Assert.Equal((evB, groupB, 0), (b.GetProperty("evidenceId").GetGuid(), b.GetProperty("citationGroupId").GetGuid(), b.GetProperty("part").GetInt32()));
        Assert.Equal(1, Entry(Req0.Item, Req0.Page).GetProperty("part").GetInt32());
        // Una fonte senza evidenza nel registro e' una voce con i campi di gruppo null, mai inventati.
        var goal = Assert.Single(map, e => e.GetProperty("itemId").GetGuid() == Guid.Parse("b2000000-0000-4000-8000-000000000001"));
        Assert.Equal(JsonValueKind.Null, goal.GetProperty("citationGroupId").ValueKind);
        Assert.Equal(JsonValueKind.Null, goal.GetProperty("part").ValueKind);
    }

    [Fact]
    public async Task A_historical_draft_without_editorial_unit_outputs_has_no_source_map_and_it_is_never_reconstructed()
    {
        using var client = await App.LoginAsync();
        var (s, _, _, _, _) = await CompletedWithLedgerAsync(client);
        await Execute("ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; DELETE FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;", ("r", s.Run));

        var response = await client.GetAsync($"/api/projects/{s.Project}/draft", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Json(response);
        Assert.True(!view.TryGetProperty("sourceMap", out var map) || map.ValueKind == JsonValueKind.Null);
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("markdown").GetString()));
    }

    [Theory]
    [InlineData("missing_map")]
    [InlineData("other_revision")]
    [InlineData("foreign_item")]
    [InlineData("foreign_source")]
    [InlineData("forged_group")]
    [InlineData("dropped_entry")]
    [InlineData("missing_ordinal")]
    public async Task A_draft_whose_persisted_source_map_is_missing_or_inconsistent_is_not_valid_and_is_never_silently_accepted(string kind)
    {
        using var client = await App.LoginAsync();
        var (s, _, _, _, _) = await CompletedWithLedgerAsync(client);

        if (kind == "missing_ordinal")
        {
            await Execute(
                "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; DELETE FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND (\"PayloadJson\"->>'ordinal')::int = 1; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;",
                ("r", s.Run));
        }
        else
        {
            await TamperPayloadAsync(s.Run, first: true, payload =>
            {
                var map = payload["sourceMap"]?.AsArray();
                switch (kind)
                {
                    case "missing_map": payload.Remove("sourceMap"); break;
                    case "other_revision": payload["revisionId"] = Guid.NewGuid().ToString("D"); break;
                    case "foreign_item": map![0]!["itemId"] = Guid.NewGuid().ToString("D"); break;
                    case "foreign_source": map![0]!["sourceId"] = Guid.NewGuid().ToString("D"); break;
                    case "forged_group": map!.First(n => n!["citationGroupId"] is not null)!["citationGroupId"] = Guid.NewGuid().ToString("D"); break;
                    case "dropped_entry": map!.RemoveAt(map.Count - 1); break;
                }
            });
        }

        var response = await client.GetAsync($"/api/projects/{s.Project}/draft", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("draft_source_map_inconsistent", (await Json(response)).GetProperty("code").GetString());
    }

    private async Task SeedLedgerAsync(Seeded s, params (Guid Item, Guid Evidence, Guid Group, Guid[] Sources)[] evidences)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync(Ct);
        await using (var selection = Command(connection, "INSERT INTO \"AnalysisEvidenceSelections\" (\"RunId\", \"ProjectId\", \"CreatedAt\", \"ViewHash\", \"View\") VALUES (@r, @p, now(), @h, '{}'::jsonb)",
            [("r", s.Analysis), ("p", s.Project), ("h", new string('0', 64))]))
        {
            await selection.ExecuteNonQueryAsync(Ct);
        }

        var ordinal = 1;
        foreach (var (item, evidence, group, sources) in evidences)
        {
            var content = new JsonObject
            {
                ["sources"] = new JsonArray([.. sources.Select((source, part) => (JsonNode)new JsonObject
                {
                    ["sourceId"] = source.ToString("D"), ["citationGroupId"] = group.ToString("D"), ["part"] = part,
                })]),
            }.ToJsonString();
            await using var insert = Command(
                connection,
                """
                INSERT INTO "Evidences" ("Id", "VersionId", "ProjectId", "MessageId", "Ordinal", "Key", "Kind", "Polarity", "Certainty", "Statement", "Content", "OriginKind", "PreparationId")
                VALUES (@e, @v, @p, NULL, @o, @k, 'Requirement', 'Affirmed', 'Declared', 'enunciato', @c::jsonb, 'Document', @prep);
                INSERT INTO "AnalysisEvidenceLinks" ("RunId", "ItemId", "EvidenceId", "ProjectId") VALUES (@r, @i, @e, @p);
                """,
                [("e", evidence), ("v", Guid.NewGuid()), ("p", s.Project), ("o", ordinal++), ("k", "e" + ordinal), ("c", content), ("prep", Guid.NewGuid()), ("r", s.Analysis), ("i", item)]);
            await insert.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task A_replay_after_every_unit_was_committed_reuses_all_outputs_with_the_same_ids_without_a_new_inference()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var firstModel = Model();
        var first = await RunAsync(claim, firstModel);
        Assert.Equal(ProcessingRunStatus.Completed, first.Status);
        var markdownBefore = await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        var outputsBefore = await Rows("SELECT \"BatchId\", \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r ORDER BY \"BatchId\"", ("r", s.Run));
        await ReopenAsync(s);

        var silent = Model(_ => "{}");
        var replay = await RunAsync(claim, silent);

        Assert.Equal(ProcessingRunStatus.Completed, replay.Status);
        Assert.Empty(silent.Requests);
        Assert.Equal(markdownBefore, await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run)));
        var outputsAfter = await Rows("SELECT \"BatchId\", \"OutputHash\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r ORDER BY \"BatchId\"", ("r", s.Run));
        Assert.Equal(outputsBefore.Select(o => ((string)o[0], (string)o[1])), outputsAfter.Select(o => ((string)o[0], (string)o[1])));
        Assert.Equal(firstModel.Requests.Count, outputsAfter.Count);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task After_a_failure_on_a_later_unit_a_new_attempt_reuses_the_already_committed_units_and_sends_only_the_missing_ones()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var calls = 0;
        var failing = Model(r =>
        {
            calls++;
            return calls == 3 ? "non json" : ValidFor(r).ToJsonString();
        });
        var failed = await RunAsync(claim, failing);
        Assert.Equal(ProcessingRunStatus.Failed, failed.Status);
        Assert.Equal(3, failing.Requests.Count);
        await AssertNoDraftAsync(s);
        await ReopenFailedAsync(s);

        var model = Model();
        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(s.Run));
        var all = model.Requests.Count + 2;
        Assert.Equal(all, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", s.Run)));
        // Le due unita' gia' committate non sono state reinviate: la prima richiesta del nuovo tentativo e' la terza unita' (il suo primo elemento principale segue i precedenti).
        var done = failing.Requests.Take(2).SelectMany(r => Elements(r).Where(e => e.Primary).Select(e => e.Alias)).ToHashSet();
        Assert.All(model.Requests.SelectMany(r => Elements(r).Where(e => e.Primary).Select(e => e.Alias)), alias => Assert.DoesNotContain(alias, done));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_single_element_that_alone_exceeds_the_unit_budget_fails_explicitly_with_its_id_before_any_model_call_and_without_compacting_or_a_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, requirements: 3, words: 300, hugeFirst: 3_000);
        var model = Model();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.DocumentIncomplete), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        Assert.Contains("unità irriducibile", await ErrorMessage(s.Run));
        await AssertNoDraftAsync(s);
    }

    [Fact]
    public async Task A_run_frozen_with_the_historical_v2_contract_beyond_one_call_still_fails_explicitly_and_never_enters_the_editorial_path()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, definition: "project-document-documents-v2", prompts: """{"document":"document-documents-v2","analysisSchema":"1.1.0"}""");
        var model = Model();

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.CallLimitExceeded), (result.Status, result.ErrorCode));
        Assert.Empty(model.Requests);
        await AssertNoDraftAsync(s);
    }

    [Fact]
    public async Task A_committed_unit_output_that_was_altered_is_rejected_on_assembly_with_no_inference_and_no_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, Model())).Status);
        await ReopenAsync(s);
        await TamperLastPayloadAsync(s.Run, p => p["ai"]!["blocks"]![0]!["items"] = new JsonArray("I99"));

        var silent = Model(_ => "{}");
        var result = await RunAsync(claim, silent);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Empty(silent.Requests);
        await AssertNoDraftAsync(s);
    }

    // ================================================================ supporto

    private sealed record Seeded(Guid Project, Guid Analysis, Guid Revision, Guid Run);

    private async Task AssertNoDraftAsync(Seeded s)
    {
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"LatestDraftId\" IS NOT NULL", ("p", s.Project)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Id\" = @r AND \"DraftId\" IS NOT NULL", ("r", s.Run)));
    }

    private async Task<string> ErrorMessage(Guid run) => await Scalar<string>("SELECT COALESCE(\"ErrorMessage\", '') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)) ?? "";

    private async Task<Seeded> SeedAsync(
        HttpClient client, int requirements = 12, int words = 300, int hugeFirst = 0, string definition = V3, string? prompts = null)
    {
        var project = (await Json(await client.PostAsJsonAsync("/api/projects", new { name = $"Bozza editoriale {Guid.NewGuid():N}" }, Ct))).GetProperty("id").GetGuid();
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
            var content = Big(analysis, project, message.Id, requirements, words) with { SourceCutoffSequence = run.SourceCutoffSequence ?? 0 };
            if (hugeFirst > 0)
            {
                content = content with { Requirements = [content.Requirements[0] with { Text = Words(hugeFirst, "enorme") }, .. content.Requirements.Skip(1)] };
            }

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
            ("d", definition), ("p", prompts ?? SourceSnapshotStore.DocumentPromptVersions()), ("r", generation));
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

    /// <summary>Come dopo un crash prima del commit di dominio: nessuna bozza, run Running, checkpoint azzerati; i WorkOutput Committed restano.</summary>
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

    /// <summary>Nuovo tentativo di un run fallito (come un retry che riusa il root): lo stato torna Running, gli output Committed e il contatore chiamate restano.</summary>
    private async Task ReopenFailedAsync(Seeded s) => await Execute(
        """
        ALTER TABLE "ProcessingRuns" DISABLE TRIGGER USER;
        UPDATE "ProcessingRuns" SET "Status" = 'Running', "CompletedAt" = NULL, "ErrorCode" = NULL, "ErrorMessage" = NULL,
               "LeaseOwner" = 'test-owner', "FencingToken" = 1, "LeaseUntil" = now() + interval '10 minutes' WHERE "Id" = @r;
        DELETE FROM "WorkflowCheckpoints" WHERE "ProcessingRunId" = @r;
        ALTER TABLE "ProcessingRuns" ENABLE TRIGGER USER;
        """,
        ("r", s.Run));

    private Task TamperLastPayloadAsync(Guid run, Action<JsonObject> change) => TamperPayloadAsync(run, first: false, change);

    private async Task TamperPayloadAsync(Guid run, bool first, Action<JsonObject> change)
    {
        var batchId = await Scalar<string>(
            $"SELECT \"BatchId\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed' ORDER BY \"CommittedAt\" {(first ? "ASC" : "DESC")} LIMIT 1", ("r", run));
        var payload = JsonNode.Parse(await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"BatchId\" = @b", ("r", run), ("b", batchId)))!.AsObject();
        change(payload);
        var text = payload.ToJsonString();
        await Execute(
            "ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER; UPDATE \"WorkOutputs\" SET \"PayloadJson\" = @p::jsonb, \"OutputHash\" = @h WHERE \"OwnerId\" = @r AND \"BatchId\" = @b; ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER;",
            ("p", text), ("h", CanonicalJson.Sha256Hex(text)), ("r", run), ("b", batchId));
    }

    private (InternalModelClient Client, DocumentSourceDraftWorkflow Workflow) Build(DocumentChatWorkflowTests.FakeChatModel model)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        return (client, new DocumentSourceDraftWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions())));
    }

    private async Task<DocumentRunResult> RunAsync(DocumentRunClaim claim, DocumentChatWorkflowTests.FakeChatModel model)
    {
        var (client, workflow) = Build(model);
        using (client)
        {
            return await workflow.ExecuteAsync(claim, Ct);
        }
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

    private async Task<object[]> Row(string sql, params (string Name, object Value)[] parameters)
    {
        var rows = await Rows(sql, parameters);
        return Assert.Single(rows);
    }

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
