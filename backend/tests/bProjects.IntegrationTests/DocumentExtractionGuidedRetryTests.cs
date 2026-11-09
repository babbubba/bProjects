using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T134 (MAF-PG, modello finto a livello HTTP, nessun DGX) — retry guidato unico dell'unità di estrazione quando TUTTE le violazioni del validatore sono
/// <c>kind Other</c> senza <c>kindRationale</c> (contracts/ai-workflow.md §2.3). Il primo output resta invalido e non committato; il secondo invio ripete il primo e
/// chiede l'estrazione COMPLETA rigenerata (nessun merge); il secondo output passa per l'INTERO validatore originale. Un solo retry per unità, che consuma il budget
/// chiamate, non sposta la deadline e non si ripete dopo restart/recovery/fencing. Ogni altra violazione, o violazioni miste, restano permanenti.
/// </summary>
public sealed partial class DocumentEvidenceAgentTests
{
    private const string RetryStage = "extract_retry";

    /// <summary>Evidenza JSON valida su un range; la citazione è fra le parole <c>q0…q29</c> del documento di prova.</summary>
    private static object Ev(string key, string range, int word, string kind = "Requirement", string? rationale = null, string? statement = null, string? quote = null) => new
    {
        key,
        sources = new[] { new { kind = "DocumentPassage", rangeKey = range, quote = quote ?? $"q{word} q{word + 1} q{word + 2}", occurrence = 1, role = "Primary", citationGroupKey = $"g{key}", part = 0 } },
        kind, kindRationale = rationale, statement = statement ?? $"Fatto {key}", subject = (string?)null, action = (string?)null, @object = (string?)null,
        scope = (string?)null, purpose = (string?)null, instrument = (string?)null, channel = (string?)null, recipient = (string?)null,
        qualifiers = Array.Empty<object>(), polarity = "Affirmed", certainty = "Declared", relations = Array.Empty<object>(),
    };

    private static string Out(params object[] evidences) =>
        JsonSerializer.Serialize(new { schemaVersion = "evidence-1.1.0", evidences, noEvidenceReason = (string?)null });

    private static string OutNone(string reason) =>
        JsonSerializer.Serialize(new { schemaVersion = "evidence-1.1.0", evidences = Array.Empty<object>(), noEvidenceReason = reason });

    private static FakeUnitModel Seq(params Func<ModelRequest, string>[] replies)
    {
        var index = 0;
        return new FakeUnitModel { Responder = r => replies[Math.Min(Interlocked.Increment(ref index) - 1, replies.Length - 1)](r) };
    }

    private static Func<ModelRequest, string> Reply(Func<string, string> build) => r => build(r.Keys[0]);

    private static Func<ModelRequest, string> Reply(string raw) => _ => raw;

    private static bool IsRetry(ModelRequest r) => r.UserText.Contains("CORREZIONE", StringComparison.Ordinal);

    private static Task<long> RetryMarkers(UnitRig rig) =>
        rig.Db.ScalarAsync<long>($"""SELECT count(*) FROM "DerivedBatches" WHERE "OwnerId" = '{rig.Job}' AND "Stage" = '{RetryStage}'""");

    private static Task<long> CommittedOutputs(UnitRig rig) => rig.Db.ScalarAsync<long>("""SELECT count(*) FROM "WorkOutputs" WHERE "Status" = 'Committed'""");

    private static string Correction(ModelRequest r) => r.UserText[r.UserText.IndexOf("CORREZIONE", StringComparison.Ordinal)..];

    private static Func<string, string> OtherNoRationale => k => Out(Ev("e1", k, 0), Ev("e2", k, 3, "Other"));

    private static Func<string, string> Fixed => k => Out(Ev("e1", k, 0), Ev("e2", k, 3, "Other", "Clausola di manleva non classificabile altrove."), Ev("e3", k, 6, "Constraint"));

    // ================================================================ nessun retry

    [Fact]
    public async Task A_legitimate_other_with_a_specific_rationale_is_accepted_at_the_first_call_with_no_retry()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var model = Seq(Reply(Fixed));

        await rig.RunAsync(model);

        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkers(rig));
        Assert.Equal(1, await rig.CallCountAsync());
        Assert.Equal(1L, await CommittedOutputs(rig));
    }

    [Fact]
    public async Task Narrative_that_produces_no_evidence_with_a_reason_is_accepted_at_the_first_call_with_no_retry()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var model = Seq(Reply(OutNone("Solo narrativa non pertinente al progetto.")));

        await rig.RunAsync(model);

        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkers(rig));
        Assert.Equal(1, await rig.CallCountAsync());
        Assert.Equal("Solo narrativa non pertinente al progetto.", await rig.Db.ScalarAsync<string>("""SELECT "NoEvidenceReason" FROM "DocumentPortions" LIMIT 1"""));
    }

    public static IEnumerable<object[]> NonRetryable() =>
    [
        ["invalid_quote", (Func<string, string>)(k => Out(Ev("e1", k, 0, quote: "testo che non esiste")))],
        ["invented_source", (Func<string, string>)(k => Out(Ev("e1", "r99", 0)))],
        ["not_json", (Func<string, string>)(_ => "non json")],
        ["truncated_json", (Func<string, string>)(_ => "{\"schemaVersion\":\"evidence-1.1.0\",\"evidences\":[")],
        ["zero_evidences_without_reason", (Func<string, string>)(_ => """{"schemaVersion":"evidence-1.1.0","evidences":[],"noEvidenceReason":null}""")],
        ["blank_statement", (Func<string, string>)(k => Out(Ev("e1", k, 0, statement: " ")))],
        ["other_without_rationale_plus_invalid_quote", (Func<string, string>)(k => Out(Ev("e1", k, 0, "Other"), Ev("e2", k, 3, quote: "citazione inventata")))],
        ["other_without_rationale_plus_invented_source", (Func<string, string>)(k => Out(Ev("e1", k, 0, "Other"), Ev("e2", "r99", 3)))],
        ["other_without_rationale_plus_blank_statement_same_evidence", (Func<string, string>)(k => Out(Ev("e1", k, 0, "Other", statement: " ")))],
        ["other_without_rationale_plus_duplicate_key", (Func<string, string>)(k => Out(Ev("e1", k, 0, "Other"), Ev("e1", k, 3)))],
        ["other_without_rationale_plus_zero_info_noEvidenceReason", (Func<string, string>)(k => """{"schemaVersion":"evidence-1.1.0","evidences":[{"key":"e1","sources":[],"kind":"Other","kindRationale":null,"statement":"x","qualifiers":[],"polarity":"Affirmed","certainty":"Declared","relations":[]}],"noEvidenceReason":"mix"}""")],
    ];

    [Theory]
    [MemberData(nameof(NonRetryable))]
    public async Task Every_other_violation_and_every_mixed_one_is_permanent_with_no_retry_no_marker_and_no_second_call(string kind, Func<string, string> build)
    {
        _ = kind;
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var model = Seq(Reply(build), Reply(Fixed));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Single(model.Requests);
        Assert.Equal(1, await rig.CallCountAsync());
        Assert.Equal(0, await RetryMarkers(rig));
        Assert.Equal(0L, await CommittedOutputs(rig));
    }

    [Fact]
    public async Task A_truncated_by_length_output_is_never_retried_even_if_it_only_contains_other_without_rationale()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var model = new FakeUnitModel { Responder = r => OtherNoRationale(r.Keys[0]), FinishReason = _ => "length" };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Single(model.Requests);
        Assert.Equal(0, await RetryMarkers(rig));
        Assert.Equal(0L, await CommittedOutputs(rig));
    }

    // ================================================================ il retry riuscito

    [Fact]
    public async Task Other_without_rationale_gets_exactly_one_guided_retry_whose_second_message_repeats_the_first_and_carries_the_four_instructions()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var model = Seq(Reply(OtherNoRationale), Reply(Fixed));
        var before = await rig.Db.ScalarAsync<string>($"""SELECT "DeadlineAt"::text FROM "DocumentJobs" WHERE "Id" = '{rig.Job}'""");

        await rig.RunAsync(model);

        Assert.Equal(2, model.Requests.Count);
        Assert.False(IsRetry(model.Requests[0]));
        Assert.True(IsRetry(model.Requests[1]));
        Assert.StartsWith(model.Requests[0].UserText.Replace("\r\n", "\n"), model.Requests[1].UserText.Replace("\r\n", "\n"));   // il primo messaggio, intatto
        var correction = Correction(model.Requests[1]);
        Assert.Contains("e2", correction);                                       // l'evidenza con Other senza motivazione
        Assert.DoesNotContain("e1", correction);                                 // e solo quella
        // 1. la narrativa non pertinente al progetto non deve diventare evidenza
        Assert.Contains("non pertinenti al progetto", correction);
        Assert.Contains("non devono diventare evidenze", correction);
        // 2. Other solo per informazioni pertinenti non classificabili altrove
        Assert.Contains("Other", correction);
        Assert.Contains("classificabili in nessun altro kind", correction);
        // 3. ogni Other legittimo richiede una kindRationale specifica
        Assert.Contains("kindRationale specifica", correction);
        // 4. conservare evidenze valide, condizioni, negazioni, citazioni; estrazione completa rigenerata
        Assert.Contains("evidenze valide", correction);
        Assert.Contains("condizioni", correction);
        Assert.Contains("negazioni", correction);
        Assert.Contains("citazioni", correction);
        Assert.Contains("COMPLETA", correction);
        Assert.Equal(model.Requests[0].Keys, model.Requests[1].Keys);

        Assert.Equal(2, await rig.CallCountAsync());                              // il retry consuma il budget, nessun azzeramento
        Assert.Equal(before, await rig.Db.ScalarAsync<string>($"""SELECT "DeadlineAt"::text FROM "DocumentJobs" WHERE "Id" = '{rig.Job}'"""));
        Assert.Equal(1, await RetryMarkers(rig));
        Assert.Equal(1L, await CommittedOutputs(rig));                            // solo il secondo output: il primo non è committato
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>($"""SELECT count(*) FROM "WorkOutputs" w JOIN "DerivedBatches" b ON b."BatchId" = w."BatchId" WHERE b."Stage" = '{RetryStage}'"""));
    }

    [Fact]
    public async Task The_retried_output_keeps_every_valid_evidence_it_contains_with_nothing_merged_from_the_first_output()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var first = Reply(k => Out(Ev("e1", k, 0, statement: "SOLO-NEL-PRIMO-OUTPUT"), Ev("e2", k, 3, "Other")));
        var model = Seq(first, Reply(Fixed));

        await rig.RunAsync(model);

        Assert.DoesNotContain("SOLO-NEL-PRIMO-OUTPUT", model.Requests[1].UserText);
        var payload = await rig.Db.ScalarAsync<string>("""SELECT "PayloadJson"::text FROM "WorkOutputs" LIMIT 1""");
        var output = JsonSerializer.Deserialize<DocumentUnitOutput>(payload, DocumentEvidenceCodec.Options)!;
        Assert.Equal(["e1", "e2", "e3"], output.Evidences.Select(e => e.Key).ToArray());   // tutte le evidenze valide del secondo output
        Assert.Equal(EvidenceKind.Other, output.Evidences[1].Kind);
        Assert.Equal("Clausola di manleva non classificabile altrove.", output.Evidences[1].Content.KindRationale);
        Assert.DoesNotContain("SOLO-NEL-PRIMO-OUTPUT", payload);
    }

    [Fact]
    public async Task A_first_output_with_irrelevant_narrative_turned_into_other_is_recovered_when_the_retry_drops_it()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var model = Seq(Reply(OtherNoRationale), Reply(k => Out(Ev("e1", k, 0))));   // il secondo output rimuove la narrativa irrilevante

        await rig.RunAsync(model);

        Assert.Equal(2, model.Requests.Count);
        var payload = await rig.Db.ScalarAsync<string>("""SELECT "PayloadJson"::text FROM "WorkOutputs" LIMIT 1""");
        Assert.Single(JsonSerializer.Deserialize<DocumentUnitOutput>(payload, DocumentEvidenceCodec.Options)!.Evidences);
    }

    [Fact]
    public async Task A_second_output_that_is_zero_evidences_with_a_reason_is_accepted_by_the_full_validator()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var model = Seq(Reply(OtherNoRationale), Reply(OutNone("Solo narrativa non pertinente.")));

        await rig.RunAsync(model);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("Solo narrativa non pertinente.", await rig.Db.ScalarAsync<string>("""SELECT "NoEvidenceReason" FROM "DocumentPortions" LIMIT 1"""));
    }

    // ================================================================ il retry fallito

    [Fact]
    public async Task A_persistent_other_without_rationale_after_the_retry_fails_the_unit_with_no_third_call_and_no_output()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")], queued: true);
        var model = Seq(Reply(OtherNoRationale), Reply(OtherNoRationale));

        Assert.True(await rig.DispatchOnceAsync(model));

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("Failed", await rig.JobAsync("Status"));
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, await rig.JobAsync("ErrorCode"));
        Assert.Equal("Failed", await rig.Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentPreparations" WHERE "Id" = '{rig.Seed.Preparation}'"""));
        Assert.Equal(2, await rig.CallCountAsync());
        Assert.Equal(0L, await CommittedOutputs(rig));
        Assert.Equal(1, await RetryMarkers(rig));
    }

    [Theory]
    [InlineData("invalid_quote")]
    [InlineData("invented_source")]
    [InlineData("not_json")]
    [InlineData("mixed")]
    [InlineData("zero_evidences_without_reason")]
    public async Task The_whole_original_validator_applies_to_the_second_output_and_any_violation_fails_the_unit_without_a_third_call(string kind)
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        Func<string, string> second = kind switch
        {
            "invalid_quote" => k => Out(Ev("e1", k, 0), Ev("e2", k, 3, "Other", "Motivo specifico.", quote: "citazione inventata")),
            "invented_source" => k => Out(Ev("e1", "r99", 0)),
            "not_json" => _ => "non json",
            "mixed" => k => Out(Ev("e1", k, 0, "Other"), Ev("e2", k, 3, quote: "citazione inventata")),
            _ => _ => """{"schemaVersion":"evidence-1.1.0","evidences":[],"noEvidenceReason":null}""",
        };
        var model = Seq(Reply(OtherNoRationale), Reply(second), Reply(Fixed));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, await rig.CallCountAsync());
        Assert.Equal(0L, await CommittedOutputs(rig));
        Assert.Equal(1, await RetryMarkers(rig));
    }

    [Fact]
    public async Task A_second_output_truncated_by_length_fails_the_unit_with_the_code_of_the_second_error()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var index = 0;
        var model = new FakeUnitModel { Responder = r => IsRetry(r) ? Fixed(r.Keys[0]) : OtherNoRationale(r.Keys[0]), FinishReason = _ => Interlocked.Increment(ref index) == 2 ? "length" : "stop" };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(0L, await CommittedOutputs(rig));
    }

    // ================================================================ budget, deadline

    [Fact]
    public async Task When_the_call_budget_allows_exactly_one_more_call_the_retry_runs_and_call_count_reaches_the_limit()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")], configJson: """{"MaxAiCallsPerPreparation":2}""");
        var model = Seq(Reply(OtherNoRationale), Reply(Fixed));

        await rig.RunAsync(model);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, await rig.CallCountAsync());
        Assert.Equal(1L, await CommittedOutputs(rig));
    }

    [Fact]
    public async Task When_the_call_budget_is_exhausted_the_retry_is_not_sent_and_the_original_work_output_invalid_stands()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")], configJson: """{"MaxAiCallsPerPreparation":1}""");
        var model = Seq(Reply(OtherNoRationale), Reply(Fixed));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);   // non document_limit_exceeded
        Assert.Single(model.Requests);
        Assert.Equal(1, await rig.CallCountAsync());
        Assert.Equal(0L, await CommittedOutputs(rig));
    }

    [Fact]
    public async Task With_less_deadline_left_than_one_full_call_the_retry_is_not_attempted_and_the_original_reason_stands()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        await rig.Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "DeadlineAt" = clock_timestamp() + interval '40 seconds' WHERE "Id" = '{rig.Job}'""");
        var model = Seq(Reply(OtherNoRationale), Reply(Fixed));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync(model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Single(model.Requests);
        Assert.Equal(1, await rig.CallCountAsync());
        Assert.Equal(0, await RetryMarkers(rig));      // niente consumato: la rinuncia precede il marcatore
        Assert.Equal(0L, await CommittedOutputs(rig));
    }

    [Fact]
    public async Task A_deadline_that_expires_while_the_first_call_runs_leaves_no_retry_marker_and_no_second_call()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var expired = false;
        var timed = new FakeUnitModel { Responder = r => { if (!expired) { expired = true; rig.Db.ExecuteAsync($"""UPDATE "DocumentJobs" SET "DeadlineAt" = clock_timestamp() - interval '1 second' WHERE "Id" = '{rig.Job}'""").GetAwaiter().GetResult(); } return OtherNoRationale(r.Keys[0]); } };

        await Record.ExceptionAsync(() => rig.RunAsync(timed));

        Assert.Single(timed.Requests);
        Assert.Equal(0, await RetryMarkers(rig));
        Assert.Equal(0L, await CommittedOutputs(rig));
    }

    // ================================================================ restart, recovery, fencing, concorrenza

    [Fact]
    public async Task A_restart_after_the_retry_was_consumed_but_not_completed_never_authorizes_another_retry_nor_resends_it()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var crashing = new FakeUnitModel
        {
            Responder = r => OtherNoRationale(r.Keys[0]),
            Fail = index => index == 1 ? System.Net.HttpStatusCode.ServiceUnavailable : null,     // errore transitorio nel secondo invio: il lavoro non è completato
        };
        await Record.ExceptionAsync(() => rig.RunAsync(crashing));
        Assert.Equal(1, await RetryMarkers(rig));
        var callsBefore = await rig.CallCountAsync();

        var recovery = Seq(Reply(Fixed));
        var failure = await Record.ExceptionAsync(() => rig.RunAsync(recovery, attempt: 2));

        Assert.DoesNotContain(recovery.Requests, IsRetry);                       // nessun secondo invio del retry, né un secondo retry
        Assert.Equal(1, await RetryMarkers(rig));
        Assert.Equal(1, crashing.Requests.Count(IsRetry));
        Assert.Empty(recovery.Requests);                                         // la risposta salvata nel checkpoint torna alla validazione: nessun nuovo invio
        Assert.Equal(callsBefore, await rig.CallCountAsync());
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure!)).Code);
        Assert.True(await CommittedOutputs(rig) <= 1);
    }

    [Fact]
    public async Task A_stale_worker_loses_the_retry_response_to_the_fence_and_the_new_owner_cannot_retry_again()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var stale = new FakeUnitModel { Responder = r => IsRetry(r) ? Fixed(r.Keys[0]) : OtherNoRationale(r.Keys[0]) };
        var taken = false;
        var model = new FakeUnitModel
        {
            Responder = r =>
            {
                if (IsRetry(r) && !taken)
                {
                    taken = true;   // durante il retry un altro worker prende il lease con un fence piu nuovo
                    rig.TakeOverAsync(fence: 6).GetAwaiter().GetResult();
                }

                return stale.Responder!(r);
            },
        };

        await Record.ExceptionAsync(() => rig.RunAsync(model));

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(0L, await CommittedOutputs(rig));                            // il worker con fence vecchio non committa nulla
        var owner = Seq(Reply(Fixed));
        await Record.ExceptionAsync(() => rig.RunAsync(owner, fence: 6, attempt: 2, lease: "worker-b"));

        Assert.DoesNotContain(owner.Requests, IsRetry);                           // il retry era già consumato: un solo retry in totale
        Assert.Equal(1, await RetryMarkers(rig));
        Assert.Equal(1, model.Requests.Count(IsRetry) + owner.Requests.Count(IsRetry));
    }

    [Fact]
    public async Task Concurrent_workers_on_the_same_unit_never_send_more_than_one_guided_retry_or_commit_more_than_one_output()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        var a = Seq(Reply(OtherNoRationale), Reply(Fixed));
        var b = Seq(Reply(OtherNoRationale), Reply(Fixed));

        await Task.WhenAll(Record.ExceptionAsync(() => rig.RunAsync(a)).AsTask(), Record.ExceptionAsync(() => rig.RunAsync(b)).AsTask());

        Assert.InRange(a.Requests.Concat(b.Requests).Count(IsRetry), 0, 1);
        Assert.True(await RetryMarkers(rig) <= 1);
        Assert.True(await CommittedOutputs(rig) <= 1);
    }

    [Fact]
    public async Task Replaying_a_unit_whose_second_output_was_already_committed_reuses_it_without_any_new_call_or_retry()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(30, "q")]);
        await rig.RunAsync(Seq(Reply(OtherNoRationale), Reply(Fixed)));
        var payloadBefore = await rig.Db.ScalarAsync<string>("""SELECT "PayloadJson"::text FROM "WorkOutputs" LIMIT 1""");
        var callsBefore = await rig.CallCountAsync();
        // crash dopo il commit del batch e prima della chiusura dell'unità: porzioni e job tornano riprendibili, gli output Committed restano
        await rig.Db.ExecuteAsync($"""UPDATE "DocumentPortions" SET "Status" = 'Pending', "ResultHash" = NULL, "NoEvidenceReason" = NULL; UPDATE "DocumentJobs" SET "Status" = 'Running', "ResultHash" = NULL, "CompletedAt" = NULL, "LeaseOwner" = 'worker-a', "LeaseUntil" = now() + interval '30 minutes' WHERE "Id" = '{rig.Job}'""");

        var silent = Seq(Reply("{}"));
        await rig.RunAsync(silent);

        Assert.Empty(silent.Requests);
        Assert.Equal(callsBefore, await rig.CallCountAsync());
        Assert.Equal(payloadBefore, await rig.Db.ScalarAsync<string>("""SELECT "PayloadJson"::text FROM "WorkOutputs" LIMIT 1"""));
        Assert.Equal(1, await RetryMarkers(rig));
        Assert.Equal(1L, await CommittedOutputs(rig));
    }

    // ================================================================ più batch: il retry è per batch, mai fra batch

    [Fact]
    public async Task In_a_multi_batch_unit_each_batch_has_its_own_single_retry_and_other_batches_are_untouched()
    {
        TestEnvironment.RequirePostgres();
        await using var rig = await UnitRig.CreateAsync([Words(100, "q"), Words(100, "q")]);   // due batch (una porzione ciascuno)
        var model = new FakeUnitModel
        {
            Responder = r => r.Keys[0] == "r0" && !IsRetry(r)
                ? Out(Ev("e1", "r0", 0), Ev("e2", "r0", 3, "Other"))
                : Out(Ev("e1", r.Keys[0], 0), Ev("e2", r.Keys[0], 3, "Other", "Motivo specifico.")),
        };

        await rig.RunAsync(model);

        Assert.Equal(3, model.Requests.Count);                                    // b0, b0 retry, b1
        Assert.Equal(1, model.Requests.Count(IsRetry));
        Assert.Equal(1, await RetryMarkers(rig));
        Assert.Equal(2L, await CommittedOutputs(rig));
        Assert.Equal(3, await rig.CallCountAsync());
    }
}
