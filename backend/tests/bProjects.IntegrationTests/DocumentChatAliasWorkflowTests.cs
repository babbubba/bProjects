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
/// T128 (MAF-PG) — chat documentale con alias: il turno percorre il grafo nativo con il port T044 su PostgreSQL reale, SDK MAF 1.26.0, preflight/AgentFactory
/// reali e modello FINTO a livello HTTP (nessun DGX). Il modello vede alias `F&lt;n&gt;` e cita `[fonte:F&lt;n&gt;]`; il server risolve contro il catalogo
/// dello snapshot congelato, rifiuta alias sconosciuti/fuori snapshot/malformati/UUID, persiste e restituisce solo il formato canonico `[fonte:UUID]`.
/// Replay, recovery e compatibilità dei turni storici v2 sono provati sullo stesso percorso.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentChatAliasWorkflowTests : ArchiveTestBase
{
    private static readonly Guid S1 = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid S2 = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid S3 = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid S4 = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private const string Statement = "Il cliente A puo emettere al massimo 5 ordini al giorno.";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    private static string Answer(string mode, string answer) => JsonSerializer.Serialize(new { mode, answer });

    private static string FirstAlias(DocumentChatWorkflowTests.ChatRequest r) => Regex.Match(r.Messages[^1].Text, @"fonte:(F\d+) ").Groups[1].Value;

    // ================================================================ congelamento alla richiesta

    [Fact]
    public async Task A_new_documentary_turn_freezes_the_v3_definition_and_a_turn_without_documents_freezes_nothing()
    {
        using var client = await App.LoginAsync();
        var plain = await NewProject(client);
        var plainTurn = await PostAsync(client, plain, "Buongiorno, vorrei un portale clienti.");
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");

        Assert.Null(await ScalarOrNull("SELECT \"PromptVersion\" FROM \"ChatTurns\" WHERE \"Id\" = @t", plainTurn));
        Assert.Null(await ScalarOrNull("SELECT \"AgentConfig\"::text FROM \"ChatTurns\" WHERE \"Id\" = @t", plainTurn));
        Assert.Equal(DocumentChatWorkflow.PromptVersionV3, await ScalarOrNull("SELECT \"PromptVersion\" FROM \"ChatTurns\" WHERE \"Id\" = @t", turn));
        using var frozen = JsonDocument.Parse((await ScalarOrNull("SELECT \"AgentConfig\"::text FROM \"ChatTurns\" WHERE \"Id\" = @t", turn))!);
        Assert.Equal(DocumentChatWorkflow.DefinitionVersionV3, frozen.RootElement.GetProperty("chatDefinition").GetString());
        Assert.Equal(ChatAliasCatalog.Version, frozen.RootElement.GetProperty("chatCatalog").GetString());
    }

    [Fact]
    public async Task A_turn_whose_documents_are_not_ready_stays_on_the_001_dialogue_without_any_chat_freeze()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "attesa");                                   // incluso ma non pronto
        var turn = await PostAsync(client, project, "Cosa dicono i documenti?");

        Assert.Null(await ScalarOrNull("SELECT \"PromptVersion\" FROM \"ChatTurns\" WHERE \"Id\" = @t", turn));
        Assert.Null(await ScalarOrNull("SELECT \"AgentConfig\"::text FROM \"ChatTurns\" WHERE \"Id\" = @t", turn));
    }

    // ================================================================ percorso nominale

    [Fact]
    public async Task The_model_sees_aliases_and_the_server_commits_and_returns_only_the_canonical_marker()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini al giorno puo emettere il cliente A?");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Answer("documented", $"Massimo 5 ordini al giorno [fonte:{FirstAlias(r)}].") };
        var deltas = new List<string>();

        var result = await RunAsync(turn, model, (d, _) => { deltas.Add(d); return default; });

        Assert.Equal(ChatTurnCommitOutcome.Committed, result.Outcome);
        var request = Assert.Single(model.Requests);
        var text = request.Messages[^1].Text;
        Assert.Contains("fonte:F1 ", text);
        Assert.Contains(Statement, text);
        Assert.DoesNotMatch("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", request.AllText);   // nessun UUID nel messaggio
        Assert.DoesNotContain(await Scalar<string>("SELECT \"Hash\" FROM \"SourceSnapshots\" LIMIT 1"), request.AllText);
        Assert.DoesNotContain("[fonte:<UUID>]", request.System);
        Assert.Contains(DocumentChatWorkflow.InstructionsV3.Replace("\r\n", "\n"), request.System.Replace("\r\n", "\n"));

        var message = await AssistantOfAsync(turn);
        Assert.Equal($"Massimo 5 ordini al giorno [fonte:{S1}].", message);
        Assert.DoesNotMatch(@"\[fonte:F\d", message);
        Assert.Equal([message], deltas);                                                  // il delta provvisorio e' gia' canonico
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn)));
        Assert.Equal([DocumentChatWorkflow.DefinitionVersionV3], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ChatTurnId\" = @o", ("o", turn)));
        Assert.StartsWith("document-chat-v3:", await Scalar<string>("SELECT \"ChatConfigVersion\" FROM \"Conversations\""));
        Assert.NotEqual(Guid.Empty, doc.Version);

        // WorkOutput: solo canonico + hash del catalogo; nessun alias e nessun testo del modello.
        var payload = await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @t", ("t", turn));
        Assert.Contains(DocumentChatWorkflow.SchemaVersionV3, payload);
        Assert.Contains("catalogHash", payload);
        Assert.Contains(S1.ToString(), payload);
        Assert.DoesNotMatch(@"fonte:F\d", payload);
    }

    [Fact]
    public async Task A_source_shared_by_two_evidences_has_one_alias_and_multi_block_parts_keep_their_own_alias_group_and_order()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var group = Guid.NewGuid();
        await NewDocument(
            client, project, "regole",
            ("Massimo 5 ordini al giorno.", [new Spec(S1, Guid.NewGuid(), 0, "Il cliente A puo emettere 5 ordini.")]),
            ("Il termine e 30 giorni.", [new Spec(S2, group, 0, "Pagamento a 30 giorni", 100), new Spec(S3, group, 1, "dal ricevimento della fattura", 130), new Spec(S1, Guid.NewGuid(), 0, "Il cliente A puo emettere 5 ordini.")]));
        var turn = await PostAsync(client, project, "Termini?");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Parte 2 [fonte:F3] poi parte 1 [fonte:F2] e ordini [fonte:F1].") };

        await RunAsync(turn, model);

        var text = Assert.Single(model.Requests).Messages[^1].Text;
        Assert.Equal(2, Regex.Matches(text, @"fonte:F1 ").Count);                       // S1 condiviso: un alias, citato nelle due evidenze
        Assert.Single(Regex.Matches(text, @"fonte:F2 "));
        Assert.Single(Regex.Matches(text, @"fonte:F3 "));
        Assert.DoesNotContain("fonte:F4", text);
        Assert.True(text.IndexOf("fonte:F2 ", StringComparison.Ordinal) < text.IndexOf("fonte:F3 ", StringComparison.Ordinal));   // ordine delle parti invariato
        Assert.Equal($"Parte 2 [fonte:{S3}] poi parte 1 [fonte:{S2}] e ordini [fonte:{S1}].", await AssistantOfAsync(turn));
        var payload = await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @t", ("t", turn));
        using var json = JsonDocument.Parse(payload);
        Assert.Equal([S3.ToString(), S2.ToString(), S1.ToString()], json.RootElement.GetProperty("sourceIds").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Only_the_source_the_model_chose_is_attributed_never_another_even_if_plausible()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement), new Spec(S2, Guid.NewGuid(), 0, "Pagamento a 30 giorni", 100)]));
        var turn = await PostAsync(client, project, "Termini?");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "A 30 giorni [fonte:F2].") };

        await RunAsync(turn, model);

        var message = await AssistantOfAsync(turn);
        Assert.Equal($"A 30 giorni [fonte:{S2}].", message);
        Assert.DoesNotContain(S1.ToString(), message);
    }

    [Fact]
    public async Task A_non_documentary_answer_without_markers_is_committed_unchanged()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Buongiorno, vorrei un portale clienti.");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("no_document_use", "Buongiorno, quali utenti userebbero il portale?") };

        await RunAsync(turn, model);

        Assert.Equal("Buongiorno, quali utenti userebbero il portale?", await AssistantOfAsync(turn));
    }

    // ================================================================ rifiuti

    [Theory]
    [InlineData("Dice X [fonte:F9].")]                                                    // alias sconosciuto
    [InlineData("Dice X [fonte:f1].")]
    [InlineData("Dice X [fonte:F01].")]
    [InlineData("Dice X [fonte: F1].")]
    [InlineData("Dice X [fonte:F1,F2].")]
    [InlineData("Dice X [fonte:F1")]
    [InlineData("Dice X [fonte:S1].")]
    public async Task An_unknown_or_malformed_alias_is_a_permanent_work_output_invalid_with_one_call_and_nothing_committed(string answer)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Cosa dice il documento?");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", answer) };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.False(ModelFailure.Classify(Unwrap(failure)).IsTransient);
        Assert.DoesNotContain("Dice X", Unwrap(failure).Message);
        await AssertNothingCommitted(turn);
        Assert.Single(model.Requests);                                                     // nessuna riparazione ne' secondo invio
    }

    [Fact]
    public async Task A_canonical_uuid_instead_of_the_alias_is_rejected_even_when_it_belongs_to_the_snapshot()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Cosa dice il documento?");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", $"Dice X [fonte:{S1}].") };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        await AssertNothingCommitted(turn);
    }

    [Fact]
    public async Task An_alias_of_a_source_outside_the_turn_snapshot_does_not_exist_and_is_rejected()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var keep = await NewDocument(client, project, "tenuto", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var drop = await NewDocument(client, project, "escluso", ("Contenuto escluso.", [new Spec(S2, Guid.NewGuid(), 0, "Contenuto escluso.")]));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, project, Item(keep), Item(drop, included: false))).StatusCode);
        var turn = await PostAsync(client, project, "Riassumi.");
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Usa il documento escluso [fonte:F2].") };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.DoesNotContain("Contenuto escluso.", Assert.Single(model.Requests).AllText);
        Assert.DoesNotContain("fonte:F2", model.Requests[0].AllText);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        await AssertNothingCommitted(turn);
    }

    [Fact]
    public async Task The_catalog_comes_from_the_frozen_snapshot_a_selection_change_or_new_ready_document_after_the_post_does_not_enter()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var a = await NewDocument(client, project, "uno", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var b = await NewDocument(client, project, "due", ("Secondo documento.", [new Spec(S2, Guid.NewGuid(), 0, "Secondo documento.")]));
        var pending = await NewDocument(client, project, "attesa");
        var turn = await PostAsync(client, project, "Cosa dicono?");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, project, Item(a), Item(b, included: false), Item(pending, included: false))).StatusCode);   // selezione cambiata DOPO il POST
        await MakeReady(pending, ("Comparso dopo la richiesta.", [new Spec(S4, Guid.NewGuid(), 0, "Comparso dopo la richiesta.")]));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Uno [fonte:F1] due [fonte:F2].") };

        await RunAsync(turn, model);

        var text = Assert.Single(model.Requests).AllText;
        Assert.Contains("Secondo documento.", text);                                       // ancora nello snapshot del turno
        Assert.DoesNotContain("Comparso dopo la richiesta.", text);
        Assert.DoesNotContain("fonte:F3", text);
        Assert.StartsWith($"Uno [fonte:{S1}] due [fonte:{S2}].", await AssistantOfAsync(turn));
        Assert.Contains("Nota sulle fonti: 1 documento incluso nel progetto non è ancora disponibile", await AssistantOfAsync(turn));   // lo snapshot dichiara la fonte non pronta, mai la usa
    }

    [Fact]
    public async Task A_truncated_output_is_never_accepted_even_if_the_json_looks_closed()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Cosa dice il documento?");
        var model = new DocumentChatWorkflowTests.FakeChatModel { FinishReason = "length", Responder = _ => Answer("documented", "X [fonte:F1].") };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        await AssertNothingCommitted(turn);
    }

    // ================================================================ replay, retry e recovery

    [Fact]
    public async Task A_replay_after_the_commit_returns_the_same_result_without_a_second_call_or_answer()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");
        var claim = await ClaimAsync(turn);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Cinque [fonte:F1].") };
        var first = await RunAsync(claim, model);
        var again = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Diverso [fonte:F1].") };

        var second = await RunAsync(claim, again);

        Assert.Equal(ChatTurnCommitOutcome.Committed, first.Outcome);
        Assert.Equal(first.ResultMessageId, second.ResultMessageId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal($"Cinque [fonte:{S1}].", await AssistantOfAsync(turn));
    }

    [Fact]
    public async Task After_a_crash_between_the_committed_work_output_and_the_turn_commit_the_retry_reuses_the_same_mapping_without_inferring_again()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement), new Spec(S2, Guid.NewGuid(), 0, "Pagamento a 30 giorni", 100)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");
        var claim = await ClaimAsync(turn);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Cinque [fonte:F1] e trenta [fonte:F2].") };

        // Il delta provvisorio fallisce DOPO il commit del WorkOutput e PRIMA del commit del turno (crash simulato).
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, model, (_, _) => throw new InvalidOperationException("crash simulato")));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        var retry = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Risposta diversa [fonte:F2].") };

        var result = await RunAsync(claim, retry);

        Assert.Equal(ChatTurnCommitOutcome.Committed, result.Outcome);
        Assert.Empty(retry.Requests);                                                      // nessuna reinferenza: stesso WorkOutput, stesso mapping
        Assert.Equal($"Cinque [fonte:{S1}] e trenta [fonte:{S2}].", await AssistantOfAsync(turn));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task A_committed_work_output_with_a_different_catalog_hash_is_checkpoint_incompatible_and_never_reinterpreted()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");
        var claim = await ClaimAsync(turn);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Cinque [fonte:F1].") };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, model, (_, _) => throw new InvalidOperationException("crash simulato")));
        await TamperPayloadAsync(turn, p => Regex.Replace(p, "\"catalogHash\":\"[0-9a-f]{64}\"", $"\"catalogHash\":\"{new string('0', 64)}\""));
        var retry = new DocumentChatWorkflowTests.FakeChatModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, retry));

        Assert.Equal(DocumentWorkException.CheckpointIncompatible, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Empty(retry.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task A_committed_work_output_whose_canonical_text_was_altered_is_an_integrity_conflict()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");
        var claim = await ClaimAsync(turn);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Cinque [fonte:F1].") };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, model, (_, _) => throw new InvalidOperationException("crash simulato")));
        await TamperPayloadAsync(turn, p => p.Replace("Cinque", "Sei"));
        var retry = new DocumentChatWorkflowTests.FakeChatModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, retry));

        Assert.Equal(DocumentWorkException.IntegrityConflict, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Empty(retry.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    // ================================================================ compatibilita storica e definizioni ignote

    [Fact]
    public async Task A_historic_v2_turn_without_frozen_versions_keeps_its_prompt_uuid_message_decoder_and_definition()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");
        await PinToV2Async(turn);
        var model = new DocumentChatWorkflowTests.FakeChatModel();                          // risposta UUID di default (v2)

        await RunAsync(turn, model);

        var request = Assert.Single(model.Requests);
        Assert.Contains($"fonte:{S1}", request.Messages[^1].Text);
        Assert.Contains("[fonte:<UUID>]", request.System);
        Assert.DoesNotContain("F<n>", request.System);
        Assert.Equal($"Massimo 5 ordini al giorno [fonte:{S1}].", await AssistantOfAsync(turn));
        Assert.Equal([DocumentChatWorkflow.DefinitionVersion], await Strings("SELECT DISTINCT \"WorkflowDefinitionVersion\" FROM \"WorkflowCheckpoints\" WHERE \"ChatTurnId\" = @o", ("o", turn)));
        Assert.StartsWith("document-chat-v2:", await Scalar<string>("SELECT \"ChatConfigVersion\" FROM \"Conversations\""));
    }

    [Fact]
    public async Task A_historic_v2_turn_never_accepts_an_alias_marker()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");
        await PinToV2Async(turn);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => Answer("documented", "Cinque [fonte:F1].") };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        await AssertNothingCommitted(turn);
    }

    [Theory]
    [InlineData("document-chat-v9", null)]
    [InlineData("document-chat-v3", "{\"chatDefinition\":\"project-chat-documents-v9\"}")]
    [InlineData("document-chat-v2", null)]
    public async Task An_unknown_definition_prompt_or_version_is_workflow_incompatible_before_any_model_call(string prompt, string? config)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var turn = await PostAsync(client, project, "Quanti ordini?");
        await Execute("UPDATE \"ChatTurns\" SET \"PromptVersion\" = @p, \"AgentConfig\" = @c::jsonb WHERE \"Id\" = @t", ("p", prompt), ("c", (object?)config ?? DBNull.Value), ("t", turn));
        var model = new DocumentChatWorkflowTests.FakeChatModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal("workflow_incompatible", ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.False(ModelFailure.Classify(Unwrap(failure)).IsTransient);
        Assert.Empty(model.Requests);
        await AssertNothingCommitted(turn);
    }

    [Fact]
    public async Task The_regime_probe_payload_follows_the_frozen_contract_alias_messages_for_v3_and_uuid_messages_for_v2()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", (Statement, [new Spec(S1, Guid.NewGuid(), 0, Statement)]));
        var v3 = await PostAsync(client, project, "Quanti ordini?");
        var v2 = await PostAsync(client, project, "Quanti ordini davvero?");
        await PinToV2Async(v2);

        var payloadV3 = (await BuildRegimePayloadAsync(v3)).Payload!;
        var payloadV2 = (await BuildRegimePayloadAsync(v2)).Payload!;

        Assert.Contains("fonte:F1 ", payloadV3.Messages[^1].Text);
        Assert.DoesNotContain(S1.ToString(), payloadV3.Messages[^1].Text);
        Assert.Equal(DocumentChatWorkflow.InstructionsV3, payloadV3.Instructions);
        Assert.Contains($"fonte:{S1}", payloadV2.Messages[^1].Text);
        Assert.Equal(DocumentChatWorkflow.Instructions, payloadV2.Instructions);
    }

    // ================================================================ supporto

    private sealed record Spec(Guid SourceId, Guid Group, int Part, string Quote, int Start = 0);

    private sealed record Doc(Guid Document, Guid Version);

    private static object Item(Doc doc, bool included = true) => new { documentId = doc.Document, included, mode = "LatestReady", versionId = (Guid?)null };

    private async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid project, params object[] items)
    {
        var revision = (await Json(await client.GetAsync($"/api/projects/{project}/source-selection", Ct))).GetProperty("revision").GetInt64();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}/source-selection") { Content = JsonContent.Create(new { items }) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return await client.SendAsync(request, Ct);
    }

    private static async Task<Guid> PostAsync(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("chatTurnId").GetGuid();
    }

    private async Task<string?> ScalarOrNull(string sql, Guid turn)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", turn);
        return await command.ExecuteScalarAsync(Ct) is string value ? value : null;
    }

    private async Task<string[]> Strings(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = await OpenConnection();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            list.Add(reader.GetString(0));
        }

        return [.. list];
    }

    /// <summary>Simula un turno richiesto prima di T128: nessuna versione congelata sul turno (definizione storica v2).</summary>
    private Task PinToV2Async(Guid turn) => Execute("UPDATE \"ChatTurns\" SET \"PromptVersion\" = NULL, \"AgentConfig\" = NULL WHERE \"Id\" = @t", ("t", turn));

    private async Task AssertNothingCommitted(Guid turn)
    {
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn)));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));    // l'esito lo decide il dispatcher
    }

    /// <summary>Altera il payload del WorkOutput Committed mantenendo coerente l'OutputHash (l'alterazione non e' un guasto di storage ma un cambio di contenuto).</summary>
    private async Task TamperPayloadAsync(Guid turn, Func<string, string> change)
    {
        var payload = await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn));
        var canonical = CanonicalJson.Canonicalize(payload);
        var changed = CanonicalJson.Canonicalize(change(canonical));
        Assert.NotEqual(canonical, changed);
        // WorkOutputs e' append-only per trigger: l'alterazione e' un intervento di test come amministratore, con il trigger sospeso solo per questa istruzione.
        await Execute("ALTER TABLE \"WorkOutputs\" DISABLE TRIGGER USER");
        try
        {
            await Execute(
                "UPDATE \"WorkOutputs\" SET \"PayloadJson\" = @p::jsonb, \"OutputHash\" = @h WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'",
                ("p", changed), ("h", CanonicalJson.Sha256Hex(changed)), ("t", turn));
        }
        finally
        {
            await Execute("ALTER TABLE \"WorkOutputs\" ENABLE TRIGGER USER");
        }
    }

    private async Task<Doc> NewDocument(HttpClient client, Guid project, string name, params (string Statement, Spec[] Sources)[] evidences)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var doc = new Doc(created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid());
        return evidences.Length == 0 ? doc : await MakeReady(doc, evidences);
    }

    private async Task<Doc> MakeReady(Doc doc, params (string Statement, Spec[] Sources)[] evidences)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == doc.Version, Ct);
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
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = evidences.Length, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var ordinal = 0;
        foreach (var (statement, specs) in evidences)
        {
            ordinal++;
            var content = new DocumentEvidenceContent
            {
                SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
                KindRationale = null,
                Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
                Qualifiers = [],
                Sources =
                [
                    .. specs.Select(s => new PersistedSource
                    {
                        SourceId = s.SourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = s.Group, Part = s.Part,
                        MessageId = null, Sequence = null, DocumentId = doc.Document, DocumentVersionId = doc.Version, PreparationId = preparation.Id,
                        ArtifactId = Guid.Parse("a0000000-0000-4000-8000-000000000001"), BlockId = Guid.Parse("b0000000-0000-4000-8000-000000000001"),
                        Start = s.Start, End = s.Start + s.Quote.Length, Quote = s.Quote,
                    }),
                ],
            };
            db.Evidences.Add(new EvidenceItem
            {
                Id = Guid.NewGuid(), VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = ordinal,
                Key = "e" + ordinal, Kind = EvidenceKind.Requirement, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement,
                Content = DocumentEvidenceCodec.EncodeContent(content),
            });
        }

        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = doc.Version, ProjectId = preparation.ProjectId, CurrentPreparationId = preparation.Id, CurrentEvidenceVersionId = version, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        preparation.Status = PreparationStatus.Ready;
        preparation.CurrentEvidenceVersionId = version;
        await db.SaveChangesAsync(Ct);
        await db.ProjectDocuments.Where(d => d.Id == doc.Document).ExecuteUpdateAsync(x => x.SetProperty(d => d.LatestReadyVersionId, doc.Version), Ct);
        return doc;
    }

    /// <summary>Presa in carico come il dispatcher: Running con owner, fence e lease validi.</summary>
    private async Task<ChatTurnClaim> ClaimAsync(Guid turn)
    {
        await Execute(
            """
            UPDATE "ChatTurns" SET "Status" = 'Running', "LeaseOwner" = 'test-owner', "FencingToken" = 1, "AttemptCount" = 1, "StartedAt" = now(),
                   "LeaseUntil" = now() + interval '10 minutes', "SessionRevisionAtStart" = (SELECT "ChatSessionRevision" FROM "Conversations" c WHERE c."Id" = "ConversationId")
            WHERE "Id" = @t
            """,
            ("t", turn));
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t = await db.ChatTurns.AsNoTracking().SingleAsync(x => x.Id == turn, Ct);
        return new ChatTurnClaim(t.Id, t.ConversationId, t.TriggerMessageId, t.SourceCutoffSequence, "test-owner", 1, 1, t.LeaseUntil!.Value, t.DeadlineAt);
    }

    private async Task<ChatTurnCommitResult> RunAsync(Guid turn, DocumentChatWorkflowTests.FakeChatModel model, Func<string, CancellationToken, ValueTask>? onDelta = null) =>
        await RunAsync(await ClaimAsync(turn), model, onDelta);

    private AgentFactoryScope Build(DocumentChatWorkflowTests.FakeChatModel model)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        return new AgentFactoryScope(client, new DocumentChatWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions())));
    }

    private sealed record AgentFactoryScope(InternalModelClient Client, DocumentChatWorkflow Workflow) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private async Task<ChatTurnCommitResult> RunAsync(ChatTurnClaim claim, DocumentChatWorkflowTests.FakeChatModel model, Func<string, CancellationToken, ValueTask>? onDelta = null)
    {
        using var built = Build(model);
        return await built.Workflow.ExecuteAsync(claim, onDelta, Ct);
    }

    private async Task<RegimePayload> BuildRegimePayloadAsync(Guid turn)
    {
        using var built = Build(new DocumentChatWorkflowTests.FakeChatModel());
        return await built.Workflow.BuildRegimePayloadAsync(turn, Ct);
    }

    private async Task<string> AssistantOfAsync(Guid turn) =>
        await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t", ("t", turn));

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
}
