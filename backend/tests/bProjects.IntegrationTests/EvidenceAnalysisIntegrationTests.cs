using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T063 — il registro dentro Analizza, su PostgreSQL reale con l'app, il workflow MAF e il client del modello veri e un server del modello
/// finto (tokenizer, limite di finestra, compattazione nativa). Registro grande oltre il budget: consolidamento incrementale senza sintesi,
/// ogni evidenza inviata intera una sola volta, ogni originale intero, ogni passo entro il budget, mappa persistita completa. Selezione
/// congelata identica fra retry anche dopo una rianalisi e una promozione concorrenti; cutoff rispettato; vista intatta nel messaggio
/// finale quando la compattazione nativa scatta. Le estrazioni sono deterministiche (esecutore dei soli test, commit reale del registro).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class EvidenceAnalysisIntegrationTests : IAsyncLifetime
{
    private const int Window = 32768;
    private const int Output = 4096;
    private const int Margin = 1024;

    private readonly PostgresDatabaseFixture _database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private static readonly Regex Alias = new(@"(?m)^(E\d+) · messaggio sequenza (\d+) \(id ([0-9a-f-]{36})\)");

    [Fact]
    public async Task A_registry_far_beyond_the_budget_is_consolidated_incrementally_with_every_evidence_once_and_the_whole_map_persisted()
    {
        TestEnvironment.RequirePostgres();
        var server = new FakeModelServer { StructuredReply = ConsolidationReply };
        await using var host = await CompactionHost.StartAsync(_database.ConnectionString, Options(), server, chat: false, analysis: true, prepare: true,
            configure: s => s.AddSingleton<IEvidenceExtractionExecutor>(sp => new RichExtractionExecutor(sp.GetRequiredService<IServiceScopeFactory>(), perMessage: 12, quoteChars: 300)));
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        for (var i = 0; i < 10; i++)
        {
            await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(i, 18_500));
        }

        var before = await CompactionHost.OriginalsAsync(_database.ConnectionString);
        var run = await CompactionHost.AnalyzeAsync(pm, project);
        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);
        Assert.True(terminal.GetProperty("status").GetString() == "Completed", $"analisi non completata: {CompactionHost.ErrorOf(terminal)}");

        var selected = await Aliases(run);
        Assert.True(selected.Count >= 100, $"il registro deve essere grande davvero: {selected.Count} evidenze");

        // Nessuna sintesi (nessuna perdita); più passi, tutti entro il budget, ciascuno con l'analisi corrente dal secondo in poi.
        Assert.Empty(host.Recorder.Summaries);
        var calls = host.Recorder.Main;
        Assert.True(calls.Count >= 2, $"atteso un consolidamento incrementale, chiamate={calls.Count}");
        foreach (var call in calls)
        {
            var sent = await host.CountSentAsync(call);
            Assert.True(sent <= Options().InputBudgetTokens, $"passo con {sent} token oltre il budget {Options().InputBudgetTokens}");
        }

        Assert.All(calls.Skip(1), c => Assert.Contains(c.Texts, t => t.Contains("Analisi corrente dopo i passi")));

        // Ogni evidenza selezionata è inviata intera esattamente una volta; ogni originale PM intero in almeno un passo.
        var sentAliases = calls.SelectMany(c => Alias.Matches(c.Messages[^1].Text).Select(m => m.Groups[1].Value)).ToList();
        Assert.Equal(selected.Order(), sentAliases.Order());
        Assert.All(before, m => Assert.Contains(calls, c => c.Texts.Contains($"[messaggio id={m.Id} sequenza={m.Sequence}]\n{m.Body}")));

        // La mappa elemento → evidenze della revisione copre tutte le evidenze selezionate.
        Assert.Equal(selected.Count, await Scalar<long>("""SELECT count(DISTINCT "EvidenceId") FROM "AnalysisEvidenceLinks" WHERE "RunId" = @r""", ("r", run)));
        Assert.Equal(0L, await Scalar<long>("""
            SELECT count(*) FROM "AnalysisEvidenceLinks" l
            WHERE l."RunId" = @r AND NOT EXISTS (
                SELECT 1 FROM "Evidences" e JOIN "AnalysisEvidenceSelectionItems" s ON s."VersionId" = e."VersionId"
                WHERE s."RunId" = l."RunId" AND e."Id" = l."EvidenceId")
            """, ("r", run)));
        Assert.Equal(before, await CompactionHost.OriginalsAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task A_step_that_drops_evidences_already_represented_fails_the_run_with_invalid_agent_output_and_no_revision()
    {
        TestEnvironment.RequirePostgres();
        // Un modello che ignora l'analisi corrente e risponde solo sul proprio intervallo: perderebbe in silenzio i passi precedenti.
        var server = new FakeModelServer { StructuredReply = request => ConsolidationReply(Regex.Replace(request,
            @"Analisi corrente dopo i passi[^\n]*\n\{.*?\}\r?\n", "", RegexOptions.Singleline)) };
        await using var host = await CompactionHost.StartAsync(_database.ConnectionString, Options(), server, chat: false, analysis: true, prepare: true,
            configure: s => s.AddSingleton<IEvidenceExtractionExecutor>(sp => new RichExtractionExecutor(sp.GetRequiredService<IServiceScopeFactory>(), perMessage: 12, quoteChars: 300)));
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        for (var i = 0; i < 10; i++)
        {
            await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(i, 18_500));
        }

        var run = await CompactionHost.AnalyzeAsync(pm, project);
        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);

        Assert.Equal("Failed", terminal.GetProperty("status").GetString());
        Assert.Equal("invalid_agent_output", terminal.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("evidenze già rappresentate", terminal.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(2, host.Recorder.Main.Count);                        // si ferma al primo passo che perde evidenze, senza riparazioni
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "AnalysisRevisions" """));
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "AnalysisEvidenceLinks" """));
    }

    [Fact]
    public async Task The_frozen_selection_is_sent_again_identical_on_retry_after_a_concurrent_reanalysis_and_promotion()
    {
        TestEnvironment.RequirePostgres();
        var server = new FakeModelServer { StructuredReply = ConsolidationReply, FailStructured = 1 };
        await using var host = await CompactionHost.StartAsync(_database.ConnectionString, Options(), server, chat: false, analysis: true, prepare: true,
            configure: s =>
            {
                s.AddSingleton<IEvidenceExtractionExecutor>(sp => new RichExtractionExecutor(sp.GetRequiredService<IServiceScopeFactory>(), perMessage: 1, quoteChars: 120));
                s.Configure<ExecutionOptions>(o => { o.RetryBaseDelaySeconds = 10; o.RetryMaxDelaySeconds = 10; }); // tempo per la rianalisi
            });
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(0, 1_200));
        await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(1, 1_200));
        var run = await CompactionHost.AnalyzeAsync(pm, project);

        // Primo tentativo: 503 dopo il congelamento della selezione; il run torna Queued in backoff.
        await Until(async () => await Scalar<int>("""SELECT "AttemptCount" FROM "ProcessingRuns" WHERE "Id" = @r AND "Status" = 'Queued'""", ("r", run)) == 1);
        var frozenHash = await Scalar<string>("""SELECT "ViewHash" FROM "AnalysisEvidenceSelections" WHERE "RunId" = @r""", ("r", run));
        var message = await Scalar<Guid>("""SELECT "Id" FROM "ConversationMessages" WHERE "Sequence" = 1""");
        var frozenVersion = await Scalar<Guid>("""SELECT "VersionId" FROM "AnalysisEvidenceSelectionItems" WHERE "RunId" = @r AND "MessageId" = @m""", ("r", run), ("m", message));

        // Rianalisi del messaggio (nuova versione, con un'evidenza in più) e promozione esplicita, mentre il run attende il retry.
        var reanalysis = await Command(pm, $"/api/projects/{project}/messages/{message}/evidence/reanalyses");
        Assert.Equal(HttpStatusCode.Accepted, reanalysis.StatusCode);
        var job = (await reanalysis.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await Until(async () => await Scalar<string>("""SELECT "Status" FROM "EvidenceExtractionJobs" WHERE "Id" = @j""", ("j", job)) == "Completed");
        var versions = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/messages/{message}/evidence/versions", Ct);
        var candidate = versions.GetProperty("versions").EnumerateArray().Single(v => !v.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();
        var promoted = await pm.PostAsJsonAsync($"/api/projects/{project}/messages/{message}/evidence/promotions",
            new { versionId = candidate, expectedStateRevision = versions.GetProperty("stateRevision").GetInt32(), reason = "prova" }, Ct);
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal(candidate, await Scalar<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));

        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);
        Assert.True(terminal.GetProperty("status").GetString() == "Completed", $"analisi non completata: {CompactionHost.ErrorOf(terminal)}");
        Assert.Equal(2, terminal.GetProperty("attemptCount").GetInt32());

        // Stessa selezione e stessa vista inviata: il retry non legge il registro corrente.
        Assert.Equal(frozenHash, await Scalar<string>("""SELECT "ViewHash" FROM "AnalysisEvidenceSelections" WHERE "RunId" = @r""", ("r", run)));
        Assert.Equal(frozenVersion, await Scalar<Guid>("""SELECT "VersionId" FROM "AnalysisEvidenceSelectionItems" WHERE "RunId" = @r AND "MessageId" = @m""", ("r", run), ("m", message)));
        var attempts = host.Recorder.Main;
        Assert.Equal([503, 200], attempts.Select(c => c.Status));
        Assert.Equal(attempts[0].Messages[^1].Text, attempts[1].Messages[^1].Text);
        var selection = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/analyses/{run}/evidence-selection", Ct);
        Assert.Equal(frozenHash, selection.GetProperty("viewHash").GetString());
        Assert.NotEmpty(selection.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task With_native_compaction_the_frozen_view_reaches_the_model_intact_and_messages_beyond_the_cutoff_never_do()
    {
        TestEnvironment.RequirePostgres();
        var server = new FakeModelServer { StructuredReply = ConsolidationReply };
        var reduced = Options();
        reduced.CompactionTriggerTokens = FakeModelServer.Tokens(ConversationalAgent.ConsolidationInstructions) + 6000;
        await using var host = await CompactionHost.StartAsync(_database.ConnectionString, reduced, server, chat: false, analysis: true, prepare: true);
        var (pm, project) = await host.NewProjectAsync();
        using var _ = pm;
        for (var i = 0; i < 8; i++)
        {
            await CompactionHost.PostAsync(pm, project, CompactionDialogs.Message(i, 3_200));
        }

        var run = await CompactionHost.AnalyzeAsync(pm, project);
        await CompactionHost.PostAsync(pm, project, "Messaggio successivo al cutoff: non deve entrare in questa analisi.");
        var terminal = await CompactionHost.WaitRunAsync(pm, project, run);
        Assert.True(terminal.GetProperty("status").GetString() == "Completed", $"analisi non completata: {CompactionHost.ErrorOf(terminal)}");

        Assert.NotEmpty(host.Recorder.Summaries);                         // la compattazione nativa è scattata
        var main = Assert.Single(host.Recorder.Main);                     // passo singolo
        var view = EvidenceSelectionView.Deserialize(await Scalar<string>("""SELECT "View"::text FROM "AnalysisEvidenceSelections" WHERE "RunId" = @r""", ("r", run)));
        Assert.Contains(view.ToPromptText(), main.Messages[^1].Text);     // la vista è intera nel messaggio preservato

        var cutoff = await Scalar<long>("""SELECT "SourceCutoffSequence" FROM "ProcessingRuns" WHERE "Id" = @r""", ("r", run));
        Assert.All(view.Items, i => Assert.True(i.Sequence <= cutoff));
        var beyond = await Scalar<Guid>("""SELECT "Id" FROM "ConversationMessages" WHERE "Sequence" > @c""", ("c", cutoff));
        Assert.DoesNotContain(host.Recorder.Calls, c => c.Texts.Any(t => t.Contains(beyond.ToString())));
        Assert.DoesNotContain(view.Items, i => i.MessageId == beyond);
    }

    // ---------------------------------------------------------------- supporto

    private static AiOptions Options() => new()
    {
        Endpoint = "http://dgx.test/v1/",
        ApiKey = "chiave-di-prova",
        ModelId = "modello-finto",
        ContextWindowTokens = Window,
        MaxOutputTokens = Output,
        SafetyMarginTokens = Margin,
    };

    /// <summary>
    /// Consolidamento finto ma fedele al protocollo: riparte dall'analisi corrente (se il passo la porta) e aggiunge, per ogni messaggio
    /// del passo, una voce con evidenceKeys = gli alias delle sue evidenze inviate; output compatto come quello di un modello reale.
    /// </summary>
    private static string ConsolidationReply(string request)
    {
        string Match(string pattern) => Regex.Match(request, pattern).Groups[1].Value;
        var marker = Regex.Match(request, @"da aggiornare e restituire per intero:\r?\n(\{.*?\})\r?\nConsolidamento incrementale", RegexOptions.Singleline);
        var content = marker.Success ? JsonNode.Parse(marker.Groups[1].Value)!.AsObject() : new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["analysisId"] = Match(@"analysisId: ([0-9a-f-]{36})"),
            ["projectId"] = Match(@"projectId: ([0-9a-f-]{36})"),
            ["sourceCutoffSequence"] = long.Parse(Match(@"sourceCutoffSequence: (\d+)")),
            ["validationState"] = "Unvalidated",
            ["sources"] = new JsonArray(),
            ["goals"] = new JsonArray(),
            ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
            ["requirements"] = new JsonArray(),
            ["operationalFlows"] = new JsonArray(),
            ["constraints"] = new JsonArray(),
            ["dependencies"] = new JsonArray(),
            ["assumptions"] = new JsonArray(),
            ["openQuestions"] = new JsonArray(),
            ["conflicts"] = new JsonArray(),
        };
        var sources = content["sources"]!.AsArray();
        string SourceOf(string messageId)
        {
            if (sources.FirstOrDefault(s => s!["messageId"]!.GetValue<string>() == messageId) is { } existing)
            {
                return existing["id"]!.GetValue<string>();
            }

            var id = Guid.NewGuid().ToString();
            sources.Add(new JsonObject { ["id"] = id, ["kind"] = "Message", ["messageId"] = messageId, ["messageRole"] = "user", ["revisionId"] = null });
            return id;
        }

        JsonObject Item(string text, string source, IEnumerable<string> keys) => new()
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["text"] = text,
            ["classification"] = "Declared",
            ["evidenceRefs"] = new JsonArray(JsonValue.Create(source)),
            ["evidenceKeys"] = new JsonArray([.. keys.Select(k => (JsonNode)JsonValue.Create(k)!)]),
        };

        foreach (var group in Alias.Matches(request).GroupBy(m => m.Groups[3].Value))
        {
            content["requirements"]!.AsArray().Add(Item($"Requisiti del messaggio {group.First().Groups[2].Value}", SourceOf(group.Key), group.Select(m => m.Groups[1].Value)));
        }

        if (content["goals"]!.AsArray().Count == 0 && sources.Count > 0)
        {
            content["goals"]!.AsArray().Add(Item("Realizzare un portale clienti", sources[0]!["id"]!.GetValue<string>(), []));
        }

        return content.ToJsonString();
    }

    private async Task<List<string>> Aliases(Guid run) =>
        [.. EvidenceSelectionView.Deserialize(await Scalar<string>("""SELECT "View"::text FROM "AnalysisEvidenceSelections" WHERE "RunId" = @r""", ("r", run))).Aliases];

    private static Task<HttpResponseMessage> Command(HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request, Ct);
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condizione non raggiunta entro 60 s");
            await Task.Delay(100, Ct);
        }
    }

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    /// <summary>
    /// Estrattore deterministico dei soli test con molte evidenze per messaggio: finestre consecutive del messaggio citate alla lettera
    /// (<paramref name="quoteChars"/> caratteri ciascuna), attraverso validazione e commit reali del registro. Una rianalisi aggiunge
    /// un'evidenza, così la nuova versione differisce dalla corrente.
    /// </summary>
    private sealed class RichExtractionExecutor(IServiceScopeFactory scopes, int perMessage, int quoteChars) : IEvidenceExtractionExecutor
    {
        public async Task ExecuteAsync(ExtractionJobClaim claim, CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ledger = new EvidenceLedgerStore(db);
            var context = await ledger.LoadContextAsync(claim.JobId, ct);
            var reanalysis = db.EvidenceExtractionJobs.Single(j => j.Id == claim.JobId).Reason == ExtractionJobReason.Reanalysis;
            var body = context.Target.Body;
            var evidences = new List<ExtractedEvidence>();
            for (var k = 0; k < perMessage + (reanalysis ? 1 : 0) && k * quoteChars < body.Length; k++)
            {
                var quote = body.Substring(k * quoteChars, Math.Min(quoteChars, body.Length - k * quoteChars)).Trim();
                evidences.Add(new ExtractedEvidence
                {
                    Key = $"e{k + 1}",
                    Sources = [new ExtractedSource { MessageId = context.Target.Id, Quote = quote, Occurrence = 1, Role = EvidenceSourceRole.Primary }],
                    Kind = EvidenceKind.Requirement,
                    KindRationale = null,
                    Statement = quote,
                    Subject = null,
                    Action = null,
                    Object = null,
                    Scope = null,
                    Purpose = null,
                    Instrument = null,
                    Channel = null,
                    Recipient = null,
                    Qualifiers = [],
                    Polarity = EvidencePolarity.Affirmed,
                    Certainty = EvidenceCertainty.Declared,
                    Relations = [],
                });
            }

            var validation = EvidenceExtractionValidator.Validate(new ExtractionOutput { NoEvidenceReason = null, Evidences = evidences }, context);
            if (!validation.Accepted)
            {
                throw new InvalidOperationException("Uscita di prova non valida: " + string.Join(" | ", validation.Violations));
            }

            var result = await ledger.CommitAsync(claim, context, validation, "rich", """{"rich":true}""", ct);
            if (result.Outcome == ExtractionCommitOutcome.Rejected)
            {
                throw new LeaseLostException($"Commit di prova rifiutato: {result.Reason}");
            }
        }
    }
}
