using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T130 (MAF-PG, IChatClient finto a livello HTTP) — retry guidato unico dell'analisi v3 (contracts/ai-workflow.md §10) per due sole classi di errore:
/// (a) chiavi locali duplicate e (b) evidenze non mappate su alcun item come unico gap. Il primo output resta invalido e non committato; il secondo invio
/// ripete il messaggio del primo, elenca chiavi o alias E&lt;n&gt; e chiede l'analisi COMPLETA (nessun merge). Un solo retry per operazione, che consuma il budget
/// chiamate, non sposta la deadline e non si ripete dopo restart/recovery/fencing. Ogni altra violazione resta permanente. Run con due evidenze: E1 ed E2.
/// </summary>
public sealed partial class DocumentAnalysisAliasWorkflowTests
{
    private static string Valid(string prompt) => AliasModelReplies.Reply(prompt);

    /// <summary>Due item con la stessa chiave locale (a).</summary>
    private static string DuplicateKeys(string prompt) => AliasModelReplies.Reply(prompt, d =>
        d with { Items = d.Items with { Requirements = [d.Items.Requirements[0], d.Items.Requirements[1] with { Key = d.Items.Requirements[0].Key }] } });

    /// <summary>Solo E1 è citata da un item: E2 non è mappata (b).</summary>
    private static string Unmapped(string prompt) => AliasModelReplies.Reply(prompt, d => d with { Items = d.Items with { Requirements = [d.Items.Requirements[0]] } });

    private static DocumentChatWorkflowTests.FakeChatModel Seq(params Func<string, string>[] replies)
    {
        var index = 0;
        return new DocumentChatWorkflowTests.FakeChatModel { Responder = r => replies[Math.Min(Interlocked.Increment(ref index) - 1, replies.Length - 1)](r.AllText) };
    }

    private async Task<(HttpClient Client, Guid Project, Guid Run)> SeedRunAsync()
    {
        var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        return (client, project, await StartRunAsync(client, project));
    }

    /// <summary>Tetto di chiamate dalla policy corrente: va impostato PRIMA di creare il run, che lo congela nel proprio snapshot (come in produzione).</summary>
    private async Task SetFrozenCallLimitAsync(int max)
    {
        using var scope = App.Services.CreateScope();
        var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
        var limits = snapshot.Limits with { MaxAiCallsPerRun = max };
        Assert.Empty(limits.Validate());
        await Execute("UPDATE \"DocumentPolicies\" SET \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
    }

    private async Task<long> CallsAsync(Guid run) => await Scalar<int>("SELECT COALESCE(\"CallCount\", 0) FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

    private async Task<long> RetryMarkersAsync(Guid run) =>
        await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'analysis_retry'", ("r", run));

    private async Task<long> CommittedOutputsAsync(Guid run) =>
        await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run));

    private async Task<string> RunStateAsync(Guid run) => await Scalar<string>(
        "SELECT concat_ws('|', \"DeadlineAt\", \"AttemptCount\", \"FencingToken\") FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

    private static string CorrectionOf(DocumentChatWorkflowTests.ChatRequest request) => request.AllText[request.AllText.IndexOf("CORREZIONE", StringComparison.Ordinal)..];

    // ================================================================ il retry riuscito

    [Fact]
    public async Task Duplicate_local_keys_get_exactly_one_guided_retry_that_completes_the_analysis_with_a_single_commit()
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var model = Seq(DuplicateKeys, Valid);
        var claim = await ClaimAsync(run);
        var before = await RunStateAsync(run);

        var result = await RunClaimAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(run));
        Assert.Equal(2, model.Requests.Count);
        var (first, second) = (model.Requests[0].AllText.Replace("\r\n", "\n"), model.Requests[1].AllText.Replace("\r\n", "\n"));
        Assert.DoesNotContain("CORREZIONE", first);
        Assert.StartsWith(first, second);                                                // stesso messaggio del primo, intatto
        var correction = CorrectionOf(model.Requests[1]);
        Assert.Contains("i1", correction);                                               // la chiave duplicata, esatta
        Assert.Contains("COMPLETA", correction);
        Assert.DoesNotMatch(AnyUuid, second);
        Assert.DoesNotMatch(AnyHash, second);
        Assert.Equal(DocumentAnalysisWorkflow.InstructionsV3.Replace("\r\n", "\n"), model.Requests[1].System.Replace("\r\n", "\n"));

        Assert.Equal(2, await CallsAsync(run));                                          // il retry consuma il budget: nessun azzeramento
        Assert.Equal(before, await RunStateAsync(run));                                  // deadline, attempt e fence invariati
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(1, await CommittedOutputsAsync(run));                               // solo il secondo output: nessun doppio commit
        Assert.Equal(1, await RetryMarkersAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" w JOIN \"DerivedBatches\" b ON b.\"BatchId\" = w.\"BatchId\" WHERE w.\"OwnerId\" = @r AND b.\"Stage\" = 'analysis_retry'", ("r", run)));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(2, content.Requirements.Count);
    }

    [Fact]
    public async Task An_unmapped_evidence_gets_exactly_one_guided_retry_listing_only_its_alias_and_completes_the_analysis()
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var model = Seq(Unmapped, Valid);
        var claim = await ClaimAsync(run);
        var before = await RunStateAsync(run);

        var result = await RunClaimAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode + " " + await ErrorMessage(run));
        Assert.Equal(2, model.Requests.Count);
        var (first, second) = (model.Requests[0].AllText.Replace("\r\n", "\n"), model.Requests[1].AllText.Replace("\r\n", "\n"));
        Assert.StartsWith(first, second);
        var correction = CorrectionOf(model.Requests[1]);
        Assert.Contains("[E2]", correction);
        Assert.DoesNotContain("[E1]", correction);                                       // solo le evidenze non mappate
        Assert.Contains("COMPLETA", correction);
        Assert.DoesNotMatch(AnyUuid, second);
        Assert.DoesNotMatch(AnyHash, second);

        Assert.Equal(2, await CallsAsync(run));
        Assert.Equal(before, await RunStateAsync(run));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(1, await CommittedOutputsAsync(run));                               // il primo output (ValidIncomplete) non e mai committato
        Assert.Equal(1, await RetryMarkersAsync(run));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task Several_unmapped_evidences_are_all_listed_and_nothing_else()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2, "Il cliente B puo emettere al massimo 3 ordini al giorno.");
        var run = await StartRunAsync(client, project);
        var model = Seq(p => AliasModelReplies.Reply(p, d => d with { Items = d.Items with { Requirements = [d.Items.Requirements[1]] } }), Valid);

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        var correction = CorrectionOf(model.Requests[1]);
        Assert.Contains("[E1]", correction);
        Assert.Contains("[E3]", correction);
        Assert.DoesNotContain("[E2]", correction);
        Assert.Equal(2, model.Requests.Count);
    }

    // ================================================================ il retry fallito

    [Theory]
    [InlineData("dup_dup", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("unmapped_unmapped", DocumentWorkException.CoverageIncomplete)]
    [InlineData("dup_unmapped", DocumentWorkException.CoverageIncomplete)]       // codice del SECONDO errore
    [InlineData("unmapped_dup", DocumentWorkException.WorkOutputInvalid)]        // codice del SECONDO errore
    public async Task When_the_second_output_is_also_rejected_the_run_fails_with_the_second_error_code_and_no_third_call(string kind, string expectedCode)
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var parts = kind.Split('_');
        Func<string, string> Of(string name) => name == "dup" ? DuplicateKeys : Unmapped;
        var model = Seq(Of(parts[0]), Of(parts[1]));

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, expectedCode), (result.Status, result.ErrorCode));
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, await CallsAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"CurrentAnalysisId\" IS NOT NULL"));
    }

    [Theory]
    [InlineData("second_with_unknown_alias")]
    [InlineData("second_structurally_invalid")]
    [InlineData("second_with_hostile_extra_property")]
    public async Task A_second_output_invalid_in_any_other_way_fails_the_run_without_a_third_call(string kind)
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        Func<string, string> second = kind switch
        {
            "second_with_unknown_alias" => p => AliasModelReplies.Reply(p, d => d with
            {
                Items = d.Items with { Requirements = [AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E9")] },
            }),
            "second_structurally_invalid" => _ => "non json",
            _ => p => Extra(JsonNode.Parse(Valid(p))!.AsObject()),
        };
        var model = Seq(DuplicateKeys, second);

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
        Assert.Equal(0, await CommittedOutputsAsync(run));
    }

    private static string Extra(JsonObject node)
    {
        node["sources"] = new JsonArray();
        return node.ToJsonString();
    }

    // ================================================================ errori esclusi dal retry

    [Theory]
    [InlineData("unknown_alias", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("duplicate_key_and_unknown_alias", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("uuid_instead_of_alias", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("extra_property", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("truncated_json", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("declared_without_pm_source", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("conflict_with_one_alternative", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("unmapped_and_missing_visit", DocumentWorkException.CoverageIncomplete)]
    [InlineData("missing_visit_only", DocumentWorkException.CoverageIncomplete)]
    public async Task Any_other_rejection_is_never_retried_even_when_an_error_of_the_retry_classes_is_also_present(string kind, string expectedCode)
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var model = Seq(
            p =>
            {
                var dto = AliasModelReplies.Dto(p);
                string Requirements(params AiItem[] items) => AnalysisAliasContracts.Encode(dto with { Items = dto.Items with { Requirements = items } });
                return kind switch
                {
                    "unknown_alias" => Requirements(AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E9")),
                    "duplicate_key_and_unknown_alias" => Requirements(
                        AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E1"), AliasFixtures.Item("i1", Statement2, DocumentAnalysisClassification.Documented, "E9")),
                    "uuid_instead_of_alias" => AnalysisAliasContracts.Encode(dto).Replace("\"E1\"", "\"a0000000-0000-4000-8000-000000000001\"", StringComparison.Ordinal),
                    "extra_property" => Mutate(AnalysisAliasContracts.Encode(dto), n => n["sources"] = new JsonArray()),
                    "truncated_json" => AnalysisAliasContracts.Encode(dto)[..40],
                    "declared_without_pm_source" => Requirements(AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Declared, "E1"), AliasFixtures.Item("i2", Statement2, DocumentAnalysisClassification.Documented, "E2")),
                    "conflict_with_one_alternative" => AnalysisAliasContracts.Encode(dto with
                    {
                        Items = dto.Items with { Requirements = [] },
                        Conflicts = [new AiConflict { Key = "c1", Description = "x", Alternatives = [AliasFixtures.Item("i1", Statement1, DocumentAnalysisClassification.Documented, "E1")] }],
                    }),
                    "unmapped_and_missing_visit" => AliasModelReplies.Reply(p, d => d with
                    {
                        Items = d.Items with { Requirements = [d.Items.Requirements[0]] }, Visited = new() { A = ["E1"], B = ["E1", "E2"] },
                    }),
                    _ => AliasModelReplies.Reply(p, d => d with { Visited = new() { A = ["E1"], B = ["E1", "E2"] } }),
                };
            },
            Valid);

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, expectedCode), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        Assert.Equal(1, await CallsAsync(run));
        Assert.Equal(0, await RetryMarkersAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_known_relation_without_a_verdict_alongside_an_unmapped_evidence_is_never_retried()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", Statement1, Statement2);
        await AddKnownRelationAsync(doc, EvidenceRelationType.Contradicts);
        var run = await StartRunAsync(client, project);
        var model = Seq(p => AliasModelReplies.Reply(p, d => d with { KnownRelationChecks = [], Items = d.Items with { Requirements = [d.Items.Requirements[0]] } }), Valid);

        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.CoverageIncomplete), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkersAsync(run));
    }

    [Fact]
    public async Task A_frozen_v2_run_never_gets_the_guided_retry()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", Statement1, Statement2);
        var run = await StartRunAsync(client, project);
        await PinToV2Async(run);
        var input = await PrepareAsync(run);
        var incomplete = V2Output(input) with { EvidenceMap = [V2Output(input).EvidenceMap[0]] };
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => ReconciliationCodec.Encode(incomplete) };

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkersAsync(run));
        Assert.Equal(1, await CallsAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_complete_first_output_never_creates_a_retry_marker_and_costs_a_single_call()
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var model = Seq(Valid);

        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkersAsync(run));
        Assert.Equal(1, await CallsAsync(run));
    }

    // ================================================================ limiti: deadline e budget

    [Theory]
    [InlineData("dup", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("unmapped", DocumentWorkException.CoverageIncomplete)]
    public async Task With_less_deadline_left_than_one_full_call_the_retry_is_not_attempted_and_the_original_reason_stands(string kind, string expectedCode)
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var claim = await ClaimAsync(run);
        await Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = now() + interval '40 seconds', \"EffectiveDeadlineAt\" = now() + interval '40 seconds' WHERE \"Id\" = @r", ("r", run));
        var model = Seq(kind == "dup" ? DuplicateKeys : Unmapped, Valid);

        var result = await RunClaimAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, expectedCode), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        Assert.Equal(1, await CallsAsync(run));
        Assert.Equal(0, await RetryMarkersAsync(run));      // niente consumato: la rinuncia precede il marcatore
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_deadline_that_expires_while_the_first_call_runs_leaves_no_retry_and_no_second_call()
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var claim = await ClaimAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => DuplicateKeys(r.AllText),
            OnRequest = _ => Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = now() - interval '1 second', \"EffectiveDeadlineAt\" = now() - interval '1 second' WHERE \"Id\" = @r", ("r", run)),
        };

        await Record.ExceptionAsync(() => RunClaimAsync(claim, model));

        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkersAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task When_the_call_budget_allows_exactly_one_more_call_the_retry_runs_and_call_count_reaches_the_limit()
    {
        await SetFrozenCallLimitAsync(2);   // v4 (T131): il tetto è quello della policy congelata, non le opzioni correnti
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var model = Seq(Unmapped, Valid);
        var result = await RunAsync(run, model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, await CallsAsync(run));
    }

    [Theory]
    [InlineData("dup", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("unmapped", DocumentWorkException.CoverageIncomplete)]
    public async Task When_the_call_budget_is_exhausted_the_retry_is_not_sent_and_the_original_error_stands(string kind, string expectedCode)
    {
        // Il tetto si fissa nello snapshot alla creazione del run: la policy va modificata prima della richiesta.
        await SetFrozenCallLimitAsync(1);
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var model = Seq(kind == "dup" ? DuplicateKeys : Unmapped, Valid);
        var result = await RunAsync(run, model);

        Assert.Equal((ProcessingRunStatus.Failed, expectedCode), (result.Status, result.ErrorCode));      // non document_limit_exceeded
        Assert.Single(model.Requests);
        Assert.Equal(1, await CallsAsync(run));                                                          // nessun incremento oltre il massimo
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    // ================================================================ restart, recovery, fencing, replay

    [Theory]
    [InlineData("dup", DocumentWorkException.WorkOutputInvalid)]
    [InlineData("unmapped", DocumentWorkException.CoverageIncomplete)]
    public async Task A_restart_after_the_retry_was_consumed_but_not_completed_never_authorizes_another_retry(string kind, string expectedCode)
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var claim = await ClaimAsync(run);
        var index = 0;
        var crashing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => kind == "dup" ? DuplicateKeys(r.AllText) : Unmapped(r.AllText),
            OnRequest = _ => Interlocked.Increment(ref index) == 2 ? throw new HttpRequestException("rete caduta durante il retry") : Task.CompletedTask,
        };
        await Record.ExceptionAsync(() => RunClaimAsync(claim, crashing));      // errore transitorio nel secondo invio: il run resta Running
        Assert.Equal(1, await RetryMarkersAsync(run));
        var callsBefore = await CallsAsync(run);

        var recovery = Seq(Valid);
        var result = await RunClaimAsync(claim, recovery);

        Assert.Equal((ProcessingRunStatus.Failed, expectedCode), (result.Status, result.ErrorCode));
        Assert.Empty(recovery.Requests);                                         // nessuna nuova chiamata, nemmeno un secondo retry
        Assert.Equal(callsBefore, await CallsAsync(run));
        Assert.Equal(1, await RetryMarkersAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_stale_worker_loses_the_retry_response_to_the_fence_and_the_new_owner_cannot_retry_again()
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var claim = await ClaimAsync(run);
        var index = 0;
        var stale = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Interlocked.Increment(ref index) == 1 ? DuplicateKeys(r.AllText) : Valid(r.AllText),
            // durante il retry un altro worker prende il lease con un fence piu nuovo
            OnRequest = request => request.AllText.Contains("CORREZIONE", StringComparison.Ordinal)
                ? Execute("UPDATE \"ProcessingRuns\" SET \"LeaseOwner\" = 'other', \"FencingToken\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", run))
                : Task.CompletedTask,
        };

        await Record.ExceptionAsync(() => RunClaimAsync(claim, stale));

        Assert.Equal(2, stale.Requests.Count);
        Assert.Equal(0, await CommittedOutputsAsync(run));                      // il worker con fence vecchio non committa nulla
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));

        var owner = Seq(Valid);
        var result = await RunClaimAsync(new AnalysisRunClaim(run, "other", 2, 1), owner);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Empty(owner.Requests);                                           // il retry era gia consumato: un solo retry in totale
        Assert.Equal(1, await RetryMarkersAsync(run));
        Assert.Equal(0, await CommittedOutputsAsync(run));
    }

    [Fact]
    public async Task Concurrent_workers_on_the_same_run_never_send_more_than_one_guided_retry_or_commit_more_than_one_revision()
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        var claim = await ClaimAsync(run);
        var a = Seq(Unmapped, Valid);
        var b = Seq(Unmapped, Valid);

        await Task.WhenAll(Record.ExceptionAsync(() => RunClaimAsync(claim, a)).AsTask(), Record.ExceptionAsync(() => RunClaimAsync(claim, b)).AsTask());

        var retries = a.Requests.Concat(b.Requests).Count(r => r.AllText.Contains("CORREZIONE", StringComparison.Ordinal));
        Assert.InRange(retries, 0, 1);
        Assert.True(await RetryMarkersAsync(run) <= 1);
        Assert.True(await Count("SELECT count(*) FROM \"AnalysisRevisions\"") <= 1);
        Assert.True(await CommittedOutputsAsync(run) <= 1);
    }

    [Fact]
    public async Task Replaying_a_run_whose_second_output_was_already_committed_reuses_it_without_any_new_call_or_retry()
    {
        var (client, _, run) = await SeedRunAsync();
        using var _c = client;
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(run, Seq(DuplicateKeys, Valid))).Status);
        var payloadBefore = await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run));

        var silent = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => "{}" };
        var replay = await RunAsync(run, silent);

        Assert.Equal(ProcessingRunStatus.Completed, replay.Status);
        Assert.Empty(silent.Requests);
        Assert.Equal(payloadBefore, await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(2, await CallsAsync(run));
        Assert.Equal(1, await RetryMarkersAsync(run));
        Assert.Equal(1, await CommittedOutputsAsync(run));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }
}
