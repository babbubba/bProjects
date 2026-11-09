using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>
/// T129 (MAF-PG, IChatClient finto a livello HTTP) — retry guidato unico della bozza v3 per item obbligatori NON rappresentati (contracts/ai-workflow.md §9).
/// Il primo output resta invalido e non committato; il secondo invio ripete lo snapshot con alias, elenca gli alias mancanti e chiede la bozza COMPLETA
/// (nessun merge). Un solo retry per operazione, che consuma il budget chiamate, non sposta la deadline e non si ripete dopo restart/recovery/fencing.
/// Ogni altra violazione resta permanente. Alias: I1 obiettivo, I2 requisito, I3 assunzione, I4 domanda, I5/I6 alternative (blocchi 0..4, il quinto ha I5+I6).
/// </summary>
public sealed partial class DocumentDraftAliasWorkflowTests
{
    private static string Without(params int[] blocks)
    {
        var output = DraftAliasFixtures.Valid();
        foreach (var index in blocks.OrderDescending())
        {
            output["blocks"]!.AsArray().RemoveAt(index);
        }

        return output.ToJsonString();
    }

    /// <summary>Modello che risponde con le bozze indicate, in ordine; oltre l'ultima ripete l'ultima.</summary>
    private static DocumentChatWorkflowTests.FakeChatModel Sequence(params string[] replies)
    {
        var index = 0;
        return new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => replies[Math.Min(Interlocked.Increment(ref index) - 1, replies.Length - 1)] };
    }

    private async Task<long> CallsAsync(Guid run) => await Scalar<int>("SELECT COALESCE(\"CallCount\", 0) FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

    private async Task<long> RetryMarkersAsync(Guid run) =>
        await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r AND \"Stage\" = 'draft_retry'", ("r", run));

    private async Task<long> OutputsAsync(Guid run) =>
        await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run));

    private static string Correction(DocumentChatWorkflowTests.ChatRequest request) => request.AllText[request.AllText.IndexOf("CORREZIONE", StringComparison.Ordinal)..];

    // ================================================================ il retry riuscito

    [Fact]
    public async Task A_first_draft_missing_a_required_item_gets_exactly_one_guided_retry_that_completes_the_draft_as_proposed()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Sequence(Without(3), AliasReply());
        var claim = await ClaimAsync(s.Run);
        var before = await Row("SELECT \"DeadlineAt\", \"AttemptCount\", \"FencingToken\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run));

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, "Failed: " + result.ErrorCode);
        Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain("CORREZIONE", model.Requests[0].AllText);
        var (first, second) = (model.Requests[0].AllText, model.Requests[1].AllText);
        Assert.StartsWith(first.Replace("\r\n", "\n"), second.Replace("\r\n", "\n"));    // snapshot, alias e fonti canoniche del primo messaggio, intatti
        Assert.Contains("[I4]", Correction(model.Requests[1]));
        Assert.Contains("COMPLETA", Correction(model.Requests[1]));
        Assert.DoesNotMatch(AnyUuid, second);
        Assert.DoesNotMatch(AnyHash, second);
        Assert.Equal(DocumentSourceDraftWorkflow.InstructionsV3.Replace("\r\n", "\n"), model.Requests[1].System.Replace("\r\n", "\n"));

        Assert.Equal(2, await CallsAsync(s.Run));                                       // il retry consuma il budget: CallCount incrementa, nessun azzeramento
        var after = await Row("SELECT \"DeadlineAt\", \"AttemptCount\", \"FencingToken\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", s.Run));
        Assert.Equal(before[0], after[0]);                                               // deadline originale invariata
        Assert.Equal((before[1], before[2]), (after[1], after[2]));                      // attempt e fence invariati

        var draft = await Row("SELECT \"Status\", \"ApprovalStatus\", \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        Assert.Equal(("Proposed", "NotApproved"), ((string)draft[0], (string)draft[1]));
        Assert.Contains("quanti clienti useranno il portale", (string)draft[2]);       // l'item omesso e ora rappresentato dal testo del modello
        Assert.Equal(1, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(1, await OutputsAsync(s.Run));                                      // solo il secondo output e committato: nessun doppio commit
        Assert.Equal(1, await RetryMarkersAsync(s.Run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" w JOIN \"DerivedBatches\" b ON b.\"BatchId\" = w.\"BatchId\" WHERE w.\"OwnerId\" = @r AND b.\"Stage\" = 'draft_retry'", ("r", s.Run)));
    }

    [Fact]
    public async Task The_retried_draft_keeps_every_item_already_represented_without_duplicates_and_the_first_text_is_never_merged()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var firstOutput = DraftAliasFixtures.Valid();
        firstOutput.BlockOf(0)["markdown"] = "- TESTO-SOLO-DEL-PRIMO-OUTPUT [fonte:F1]";
        firstOutput["blocks"]!.AsArray().RemoveAt(3);
        var second = DraftAliasFixtures.Valid();
        second.BlockOf(0)["markdown"] = "- Obiettivo rigenerato [fonte:F1]";
        var model = Sequence(firstOutput.ToJsonString(), second.ToJsonString());

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.DoesNotContain("TESTO-SOLO-DEL-PRIMO-OUTPUT", model.Requests[1].AllText);   // il primo output non viene rimandato
        var markdown = await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        Assert.DoesNotContain("TESTO-SOLO-DEL-PRIMO-OUTPUT", markdown);                    // nessun merge: il risultato e la sola rigenerazione completa
        Assert.Contains("Obiettivo rigenerato", markdown);
        foreach (var fragment in new[] { "mostra lo stato dei ticket", "browser desktop", "quanti clienti useranno", "Conflitto aperto" })
        {
            Assert.Single(Regex.Matches(markdown, Regex.Escape(fragment)));                  // ogni item una volta sola
        }

        var payload = await PayloadAsync(s.Run);
        var items = payload["ai"]!["blocks"]!.AsArray().SelectMany(b => b!["items"]!.AsArray().Select(i => i!.GetValue<string>())).ToList();
        Assert.Equal(["I1", "I2", "I3", "I4", "I5", "I6"], items.Order().ToList());
    }

    [Fact]
    public async Task A_first_draft_missing_several_items_lists_exactly_all_of_them_and_only_them_in_the_second_message()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Sequence(Without(2, 3, 4), AliasReply());     // mancano I3, I4, I5 e I6

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        var correction = Correction(model.Requests[1]);
        foreach (var alias in new[] { "[I3]", "[I4]", "[I5]", "[I6]" })
        {
            Assert.Contains(alias, correction);
        }

        Assert.Contains("(sezione=assumptions)", correction);
        Assert.DoesNotContain("[I1]", correction);
        Assert.DoesNotContain("[I2]", correction);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, await CallsAsync(s.Run));
    }

    // ================================================================ il retry fallito o non ammesso

    [Fact]
    public async Task When_the_second_draft_is_also_incomplete_the_run_fails_with_no_third_call_no_draft_and_no_committed_output()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Sequence(Without(3), Without(2));

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, await CallsAsync(s.Run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await OutputsAsync(s.Run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Projects\" WHERE \"Id\" = @p AND \"LatestDraftId\" IS NOT NULL", ("p", s.Project)));
    }

    [Theory]
    [InlineData("second_with_unknown_source_alias")]
    [InlineData("second_structurally_invalid")]
    [InlineData("second_with_hostile_html")]
    public async Task A_second_draft_that_is_invalid_in_any_way_fails_the_run_without_a_third_call(string kind)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var second = DraftAliasFixtures.Valid();
        switch (kind)
        {
            case "second_with_unknown_source_alias":
                second.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F9]";
                second.BlockOf(0)["sources"] = new JsonArray("F9");
                break;
            case "second_with_hostile_html":
                second.BlockOf(2)["markdown"] = "- Ipotesi <script>alert(1)</script>";
                break;
        }

        var model = Sequence(Without(3), kind == "second_structurally_invalid" ? "non json" : second.ToJsonString());

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await OutputsAsync(s.Run));
    }

    [Theory]
    [InlineData("omission_with_unknown_item_alias")]
    [InlineData("omission_with_fabricated_source")]
    [InlineData("omission_with_hostile_html")]
    [InlineData("omission_with_uuid_marker")]
    [InlineData("item_present_but_its_source_not_cited")]
    [InlineData("source_not_supporting_the_block")]
    [InlineData("empty_blocks")]
    public async Task Permanent_violations_are_never_retried_even_when_an_item_is_also_missing(string kind)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var output = DraftAliasFixtures.Valid();
        switch (kind)
        {
            case "omission_with_unknown_item_alias":
                output.BlockOf(2)["items"] = new JsonArray("I3", "I99");
                output["blocks"]!.AsArray().RemoveAt(3);
                break;
            case "omission_with_fabricated_source":
                output.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F9]";
                output.BlockOf(0)["sources"] = new JsonArray("F9");
                output["blocks"]!.AsArray().RemoveAt(3);
                break;
            case "omission_with_hostile_html":
                output.BlockOf(2)["markdown"] = "- Ipotesi <script>alert(1)</script>";
                output["blocks"]!.AsArray().RemoveAt(3);
                break;
            case "omission_with_uuid_marker":
                output.BlockOf(0)["markdown"] = $"- Obiettivo [fonte:{DraftFixtures.Msg:D}]";
                output["blocks"]!.AsArray().RemoveAt(3);
                break;
            case "item_present_but_its_source_not_cited":
                output.BlockOf(0)["markdown"] = "- Obiettivo senza fonte";
                output.BlockOf(0)["sources"] = new JsonArray();
                break;
            case "source_not_supporting_the_block":
                output.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F2]";
                output.BlockOf(0)["sources"] = new JsonArray("F2");
                break;
            case "empty_blocks":
                output["blocks"] = new JsonArray();
                break;
        }

        var model = Sequence(output.ToJsonString(), AliasReply());

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        Assert.Equal(1, await CallsAsync(s.Run));
        Assert.Equal(0, await RetryMarkersAsync(s.Run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task A_frozen_v2_run_never_gets_the_guided_retry_and_fails_on_the_first_incomplete_output()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, definition: V2Definition, prompts: V2Prompts);
        var incomplete = DraftFixtures.Valid();
        incomplete["blocks"]!.AsArray().RemoveAt(3);
        var model = Sequence(incomplete.ToJsonString(), DraftFixtures.Valid().ToJsonString());

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkersAsync(s.Run));
        Assert.Equal(1, await CallsAsync(s.Run));
    }

    // ================================================================ limiti: deadline, budget

    [Fact]
    public async Task With_less_deadline_left_than_one_full_call_the_retry_is_not_attempted_and_the_original_reason_stands()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        await Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = now() + interval '40 seconds' WHERE \"Id\" = @r", ("r", s.Run));
        var model = Sequence(Without(3), AliasReply());

        var result = await RunAsync(claim, model);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Single(model.Requests);
        Assert.Equal(1, await CallsAsync(s.Run));
        Assert.Equal(0, await RetryMarkersAsync(s.Run));      // niente consumato: la rinuncia precede il marcatore
    }

    [Fact]
    public async Task A_deadline_that_expires_while_the_first_call_runs_leaves_no_retry_and_no_second_call()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => Without(3),
            OnRequest = _ => Execute("UPDATE \"ProcessingRuns\" SET \"DeadlineAt\" = now() - interval '1 second' WHERE \"Id\" = @r", ("r", s.Run)),
        };

        await Record.ExceptionAsync(() => RunAsync(claim, model));

        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkersAsync(s.Run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    [Fact]
    public async Task When_the_call_budget_allows_exactly_one_more_call_the_retry_runs_and_call_count_reaches_the_limit()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Sequence(Without(3), AliasReply());

        var result = await RunAsync(await ClaimAsync(s.Run), model, new DocumentOptions { MaxAiCallsPerRun = 2 });

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, await CallsAsync(s.Run));
    }

    [Fact]
    public async Task When_the_call_budget_is_exhausted_the_retry_is_not_sent_and_the_original_work_output_invalid_stands()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Sequence(Without(3), AliasReply());

        var result = await RunAsync(await ClaimAsync(s.Run), model, new DocumentOptions { MaxAiCallsPerRun = 1 });

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));   // non document_limit_exceeded
        Assert.Single(model.Requests);
        Assert.Equal(1, await CallsAsync(s.Run));      // nessun incremento oltre il massimo
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
    }

    // ================================================================ restart, recovery, fencing, replay

    [Fact]
    public async Task A_restart_after_the_retry_was_consumed_but_not_completed_never_authorizes_another_retry()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var index = 0;
        var crashing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => Without(3),
            OnRequest = _ => Interlocked.Increment(ref index) == 2 ? throw new HttpRequestException("rete caduta durante il retry") : Task.CompletedTask,
        };
        await Record.ExceptionAsync(() => RunAsync(claim, crashing));      // errore transitorio nel secondo invio: il run resta Running
        Assert.Equal(1, await RetryMarkersAsync(s.Run));
        var callsBefore = await CallsAsync(s.Run);

        var recovery = Sequence(AliasReply());
        var result = await RunAsync(claim, recovery);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Empty(recovery.Requests);                                   // nessuna nuova chiamata, nemmeno un secondo retry
        Assert.Equal(callsBefore, await CallsAsync(s.Run));
        Assert.Equal(1, await RetryMarkersAsync(s.Run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));
        Assert.Equal(0, await OutputsAsync(s.Run));
    }

    [Fact]
    public async Task A_stale_worker_loses_the_retry_response_to_the_fence_and_the_new_owner_cannot_retry_again()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var index = 0;
        var stale = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = _ => Interlocked.Increment(ref index) == 1 ? Without(3) : AliasReply(),
            // durante il retry un altro worker prende il lease con un fence piu nuovo
            OnRequest = request => request.AllText.Contains("CORREZIONE", StringComparison.Ordinal)
                ? Execute("UPDATE \"ProcessingRuns\" SET \"LeaseOwner\" = 'other', \"FencingToken\" = 2, \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", s.Run))
                : Task.CompletedTask,
        };

        await Record.ExceptionAsync(() => RunAsync(claim, stale));

        Assert.Equal(2, stale.Requests.Count);
        Assert.Equal(0, await OutputsAsync(s.Run));                        // il worker con fence vecchio non committa nulla
        Assert.Equal(0, await Count("SELECT count(*) FROM \"Drafts\""));

        var owner = Sequence(AliasReply());
        var result = await RunAsync(new DocumentRunClaim(s.Run, "other", 2, 1), owner);

        Assert.Equal((ProcessingRunStatus.Failed, DocumentWorkException.WorkOutputInvalid), (result.Status, result.ErrorCode));
        Assert.Empty(owner.Requests);                                      // il retry era gia consumato: un solo retry in totale
        Assert.Equal(1, await RetryMarkersAsync(s.Run));
        Assert.Equal(0, await OutputsAsync(s.Run));
    }

    [Fact]
    public async Task Concurrent_workers_on_the_same_run_never_send_more_than_one_guided_retry_or_commit_more_than_one_draft()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        var a = Sequence(Without(3), AliasReply());
        var b = Sequence(Without(3), AliasReply());

        await Task.WhenAll(Record.ExceptionAsync(() => RunAsync(claim, a)).AsTask(), Record.ExceptionAsync(() => RunAsync(claim, b)).AsTask());

        var retries = a.Requests.Concat(b.Requests).Count(r => r.AllText.Contains("CORREZIONE", StringComparison.Ordinal));
        Assert.InRange(retries, 0, 1);
        Assert.True(await RetryMarkersAsync(s.Run) <= 1);
        Assert.True(await Count("SELECT count(*) FROM \"Drafts\"") <= 1);
        Assert.True(await OutputsAsync(s.Run) <= 1);
    }

    [Fact]
    public async Task Replaying_a_run_whose_second_output_was_already_committed_reuses_it_without_any_new_call_or_retry()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var claim = await ClaimAsync(s.Run);
        Assert.Equal(ProcessingRunStatus.Completed, (await RunAsync(claim, Sequence(Without(3), AliasReply()))).Status);
        var markdownBefore = await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run));
        var payloadBefore = (await PayloadAsync(s.Run)).ToJsonString();
        await ReopenAsync(s);

        var silent = Model(_ => "{}");
        var replay = await RunAsync(claim, silent);

        Assert.Equal(ProcessingRunStatus.Completed, replay.Status);
        Assert.Empty(silent.Requests);
        Assert.Equal(markdownBefore, await Scalar<string>("SELECT \"Markdown\" FROM \"Drafts\" WHERE \"ProcessingRunId\" = @r", ("r", s.Run)));
        Assert.Equal(payloadBefore, (await PayloadAsync(s.Run)).ToJsonString());
        Assert.Equal(2, await CallsAsync(s.Run));
        Assert.Equal(1, await RetryMarkersAsync(s.Run));
        Assert.Equal(1, await OutputsAsync(s.Run));
    }

    [Fact]
    public async Task A_complete_first_draft_never_creates_a_retry_marker_and_costs_a_single_call()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var model = Sequence(AliasReply());

        var result = await RunAsync(await ClaimAsync(s.Run), model);

        Assert.Equal(ProcessingRunStatus.Completed, result.Status);
        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkersAsync(s.Run));
        Assert.Equal(1, await CallsAsync(s.Run));
    }
}
