using System.Net;
using System.Net.Http.Json;
using System.Text;
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
/// T058 (DET) — contratto della risposta finale della chat documentale piccola: oggetto JSON chiuso {mode,answer}, marcatori
/// [fonte:UUID] solo dello snapshot, documented con almeno una fonte, no_document_use senza fonti. Nessun database.
/// </summary>
public sealed class DocumentChatContractTests
{
    private static readonly Guid S1 = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid S2 = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly HashSet<Guid> Allowed = [S1, S2];

    private static string Reply(string mode, string answer) => JsonSerializer.Serialize(new { mode, answer });

    [Fact]
    public void A_documented_answer_with_snapshot_sources_is_accepted_and_returns_the_distinct_cited_ids_in_order()
    {
        var answer = DocumentChatContract.Validate(Reply("documented", $"Vale 5 [fonte:{S2}] e anche 6 [fonte:{S1}] [fonte:{S2}]."), Allowed);

        Assert.Equal("documented", answer.Mode);
        Assert.Equal([S2, S1], answer.SourceIds);
        Assert.Contains($"[fonte:{S1}]", answer.Answer);
    }

    [Fact]
    public void A_non_documentary_answer_is_no_document_use_and_cites_nothing()
    {
        var answer = DocumentChatContract.Validate(Reply("no_document_use", "Ciao, come posso aiutarti?"), Allowed);

        Assert.Equal("no_document_use", answer.Mode);
        Assert.Empty(answer.SourceIds);
    }

    [Theory]
    [InlineData("""{"mode":"documented"}""")]                                                                                                         // answer mancante
    [InlineData("""{"mode":"documented","answer":"x [fonte:11111111-1111-4111-8111-111111111111]","extra":1}""")]                                  // proprietà extra
    [InlineData("""{"mode":"documented","mode":"no_document_use","answer":"x"}""")]                                                                  // duplicato
    [InlineData("""{"mode":"Documented","answer":"x [fonte:11111111-1111-4111-8111-111111111111]"}""")]                                            // enum non esatto
    [InlineData("""{"mode":"documented","answer":"   "}""")]                                                                                         // risposta vuota
    [InlineData("""{"mode":"documented","answer":"nessuna fonte"}""")]                                                                               // documented senza fonti
    [InlineData("""{"mode":"no_document_use","answer":"x [fonte:11111111-1111-4111-8111-111111111111]"}""")]                                       // no_document_use con fonte
    [InlineData("""{"mode":"documented","answer":"x [fonte:33333333-3333-4333-8333-333333333333]"}""")]                                            // fonte fuori snapshot
    [InlineData("""{"mode":"documented","answer":"x [fonte:11111111-1111-4111-8111-11111111111]"}""")]                                             // marcatore malformato
    [InlineData("""{"mode":"documented","answer":"x [fonte:11111111-1111-4111-8111-111111111111 ]"}""")]                                           // marcatore malformato
    [InlineData("""{"mode":"documented","answer":"x [fonte:11111111-1111-4111-8111-111111111111] [fonte:ZZZ]"}""")]                                // un marcatore valido non salva uno malformato
    [InlineData("""{"mode":"documented","answer":"x [fonte:AAAAAAAA-1111-4111-8111-111111111111]"}""")]                                            // UUID maiuscolo
    [InlineData("```json\n{\"mode\":\"no_document_use\",\"answer\":\"x\"}\n```")]                                                                    // recinto: nessuna riparazione
    [InlineData("""{"mode":"no_document_use","answer":"x"} testo dopo""")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("null")]
    public void Invalid_final_answers_are_permanent_work_output_errors_that_never_echo_the_model_text(string response)
    {
        var failure = Assert.Throws<DocumentWorkException>(() => DocumentChatContract.Validate(response, Allowed));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, failure.Code);
        Assert.DoesNotContain("33333333", failure.Message);
        Assert.DoesNotContain("nessuna fonte", failure.Message);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentChatContract.Validate(null!, Allowed));
        Assert.Throws<ArgumentNullException>(() => DocumentChatContract.Validate("{}", null!));
    }

    [Fact]
    public void The_prompt_is_embedded_and_states_the_document_boundaries()
    {
        var prompt = DocumentChatWorkflow.Instructions;

        Assert.Contains("[fonte:<UUID>]", prompt);
        Assert.Contains("no_document_use", prompt);
        Assert.Contains("SOLO le evidenze", prompt);
        Assert.Contains("Non produci requisiti formali", prompt);
    }
}

/// <summary>
/// T058 (MAF-PG) — chat documentale piccola: il turno ChatTurn percorre il grafo nativo T043 con il port T044 su PostgreSQL reale, SDK MAF
/// 1.26.0, preflight/AgentFactory reali e modello FINTO a livello HTTP (nessun DGX). Fonti e dialogo congelati dallo snapshot del turno,
/// esclusione indiretta di vecchi assistant con selezione diversa, citazioni validate prima del commit, risposta unica committata sotto
/// fencing e nessuna revisione formale creata. Il raccordo nel dispatcher e nella DI è di T123: qui si esegue il workflow direttamente.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentChatWorkflowTests : ArchiveTestBase
{
    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 50_000;
    }

    // ================================================================ scenari

    [Fact]
    public async Task A_documented_question_sends_frozen_evidence_and_dialogue_in_one_call_and_commits_one_cited_answer()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", "Il cliente A puo emettere al massimo 5 ordini al giorno.");
        var turn = await PostAsync(client, project, "Quanti ordini al giorno puo emettere il cliente A?");
        var model = new FakeChatModel();
        var deltas = new List<string>();

        var result = await RunAsync(turn, model, (d, _) => { deltas.Add(d); return default; });

        Assert.Equal(ChatTurnCommitOutcome.Committed, result.Outcome);
        var request = Assert.Single(model.Requests);
        var last = request.Messages[^1];
        Assert.Equal("user", last.Role);
        Assert.Contains("Il cliente A puo emettere al massimo 5 ordini al giorno.", last.Text);
        Assert.Contains($"fonte:{doc.SourceId}", last.Text);
        Assert.Contains("Quanti ordini al giorno puo emettere il cliente A?", last.Text);
        Assert.Contains("[fonte:<UUID>]", request.System);                      // prompt documentale, non quello del dialogo 001

        var message = await AssistantOfAsync(turn);
        Assert.Equal($"Massimo 5 ordini al giorno [fonte:{doc.SourceId}].", message);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal("Completed", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(result.ResultMessageId, await Scalar<Guid>("SELECT \"ResultMessageId\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        // Un solo invio, contato dal root, e output Committed del turno con la sola risposta validata.
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ChatTurn' AND \"OwnerId\" = @t AND \"Status\" = 'Committed'", ("t", turn)));
        // Il testo candidato e' provvisorio e arriva una sola volta, prima del commit; non e' mai la risposta finale con altri testi.
        Assert.Equal([message], deltas);
    }

    [Fact]
    public async Task A_chat_turn_never_creates_formal_revisions_evidence_or_runs()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", "Vincolo di prova.");
        var turn = await PostAsync(client, project, "Cosa dice il documento?");
        var before = await FormalCountsAsync();

        await RunAsync(turn, new FakeChatModel());

        Assert.Equal(before, await FormalCountsAsync());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\""));
    }

    [Fact]
    public async Task A_citation_outside_the_snapshot_is_a_permanent_error_and_nothing_is_committed()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", "Vincolo di prova.");
        var turn = await PostAsync(client, project, "Cosa dice il documento?");
        var invented = Guid.NewGuid();
        var model = new FakeChatModel { Responder = _ => Answer("documented", $"Dice X [fonte:{invented}].") };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.False(ModelFailure.Classify(Unwrap(failure)).IsTransient);
        Assert.DoesNotContain(invented.ToString(), Unwrap(failure).Message);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\""));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));   // l'esito lo decide il dispatcher
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));            // nessuna riparazione ne' secondo invio
    }

    [Fact]
    public async Task A_truncated_output_is_never_accepted_even_if_the_json_looks_closed()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", "Vincolo di prova.");
        var turn = await PostAsync(client, project, "Cosa dice il documento?");
        var model = new FakeChatModel { FinishReason = "length", Responder = r => Answer("documented", $"X [fonte:{r.FirstSourceId}].") };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.NotEqual(Guid.Empty, doc.SourceId);
    }

    [Fact]
    public async Task A_non_documentary_question_answers_from_the_dialogue_alone_with_an_explicit_no_document_use()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", "Vincolo di prova.");
        var turn = await PostAsync(client, project, "Buongiorno, vorrei un portale clienti.");
        var model = new FakeChatModel { Responder = _ => Answer("no_document_use", "Buongiorno, quali utenti userebbero il portale?") };

        await RunAsync(turn, model);

        Assert.Equal("Buongiorno, quali utenti userebbero il portale?", await AssistantOfAsync(turn));
        var payload = await Scalar<string>("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @t", ("t", turn));
        Assert.Contains("no_document_use", payload);
    }

    [Fact]
    public async Task An_unavailable_source_is_declared_to_the_model_and_in_the_reply_and_a_source_ready_later_is_not_used()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var ready = await NewDocument(client, project, "pronto", "Informazione del documento pronto.");
        var pending = await NewDocument(client, project, "attesa", null);                                       // incluso ma non pronto
        var turn = await PostAsync(client, project, "Cosa dicono i documenti?");
        await MakeReady(pending, "Informazione comparsa dopo la richiesta.");                                   // diventa pronto DOPO lo snapshot
        var model = new FakeChatModel();

        await RunAsync(turn, model);

        var request = Assert.Single(model.Requests);
        Assert.Contains(ChatSourceNotice.ModelGuardMarker, request.System);
        Assert.Contains("Informazione del documento pronto.", request.Messages[^1].Text);
        Assert.DoesNotContain("Informazione comparsa dopo la richiesta.", request.AllText);                      // lo snapshot non si allarga: nemmeno al retry
        var message = await AssistantOfAsync(turn);
        Assert.Contains($"[fonte:{ready.SourceId}]", message);
        Assert.Contains("Nota sulle fonti: 1 documento incluso nel progetto non è ancora disponibile", message);
    }

    [Fact]
    public async Task An_excluded_source_is_not_in_the_prompt_and_its_old_citation_is_no_longer_valid()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var keep = await NewDocument(client, project, "tenuto", "Contenuto tenuto.");
        var drop = await NewDocument(client, project, "escluso", "Contenuto escluso.");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, project, Item(keep.Document), Item(drop.Document, included: false))).StatusCode);
        var turn = await PostAsync(client, project, "Riassumi.");
        var model = new FakeChatModel { Responder = _ => Answer("documented", $"Usa il documento escluso [fonte:{drop.SourceId}].") };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.DoesNotContain("Contenuto escluso.", Assert.Single(model.Requests).AllText);
        Assert.Contains("Contenuto tenuto.", model.Requests[0].AllText);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, ModelFailure.Classify(Unwrap(failure)).Code);
    }

    [Fact]
    public async Task With_the_same_selection_a_sequential_dialogue_is_U1_A1_then_the_trigger_U2()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", "Vincolo di prova.");
        var t1 = await PostAsync(client, project, "U1 prima domanda");
        await RunAsync(t1, new FakeChatModel());
        var a1 = await AssistantOfAsync(t1);
        var t2 = await PostAsync(client, project, "U2 seconda domanda");
        var m2 = new FakeChatModel();

        await RunAsync(t2, m2);

        var dialogue = m2.Requests[0].Messages;
        Assert.Equal(3, dialogue.Count);
        Assert.Equal(("user", "U1 prima domanda"), (dialogue[0].Role, dialogue[0].Text));
        Assert.Equal(("assistant", a1), (dialogue[1].Role, dialogue[1].Text));
        Assert.Equal("user", dialogue[2].Role);
        Assert.Contains("U2 seconda domanda", dialogue[2].Text);                   // il trigger arriva una sola volta, in ultima posizione
        Assert.Single(Regex.Matches(m2.Requests[0].AllText, "U2 seconda domanda"));
    }

    [Fact]
    public async Task Queued_turns_keep_the_FIFO_cutoff_U1_U2_and_the_later_A1_is_not_in_the_second_context()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", "Vincolo di prova.");
        var t1 = await PostAsync(client, project, "U1 prima domanda");
        var t2 = await PostAsync(client, project, "U2 seconda domanda");           // accodato prima che T1 completi: sequence U1=1, U2=2, A1=3
        var m1 = new FakeChatModel();
        await RunAsync(t1, m1);
        Assert.DoesNotContain("U2 seconda domanda", m1.Requests[0].AllText);       // il dialogo del turno e' limitato al suo cutoff
        var a1 = await AssistantOfAsync(t1);
        Assert.Equal(3L, await Scalar<long>("SELECT \"Sequence\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t", ("t", t1)));
        var m2 = new FakeChatModel();

        await RunAsync(t2, m2);

        var dialogue = m2.Requests[0].Messages;
        Assert.Equal(["user", "user"], dialogue.Select(m => m.Role));
        Assert.Equal("U1 prima domanda", dialogue[0].Text);
        Assert.Contains("U2 seconda domanda", dialogue[1].Text);
        Assert.DoesNotContain(a1, m2.Requests[0].AllText);                          // A1 ha Sequence oltre il cutoff di T2
    }

    [Fact]
    public async Task After_a_selection_change_the_old_assistant_is_excluded_from_the_operational_context_but_PM_messages_stay()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var a = await NewDocument(client, project, "uno", "Contenuto del primo.");
        var b = await NewDocument(client, project, "due", "Contenuto del secondo.");
        var t1 = await PostAsync(client, project, "U1 prima domanda");
        await RunAsync(t1, new FakeChatModel());
        var a1 = await AssistantOfAsync(t1);
        Assert.Contains("[fonte:", a1);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, project, Item(a.Document), Item(b.Document, included: false))).StatusCode);
        var t2 = await PostAsync(client, project, "U2 seconda domanda");
        var m2 = new FakeChatModel();

        await RunAsync(t2, m2);

        var text = m2.Requests[0].AllText;
        Assert.Contains("U1 prima domanda", text);                                  // originale PM preservato
        Assert.DoesNotContain(a1, text);                                            // A1 (snapshot diverso) non entra nel contesto operativo
        Assert.DoesNotContain($"fonte:{b.SourceId}", text);                         // nessun riuso indiretto del documento ora escluso
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Body\" = @b AND \"ChatTurnId\" = @t", ("b", a1), ("t", t1)));   // ma resta leggibile nello storico
    }

    [Fact]
    public async Task The_session_config_version_follows_the_selection_hash_and_never_equals_the_001_dialogue_config()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var a = await NewDocument(client, project, "uno", "Contenuto del primo.");
        var b = await NewDocument(client, project, "due", "Contenuto del secondo.");
        var t1 = await PostAsync(client, project, "U1");
        await RunAsync(t1, new FakeChatModel());
        var first = await Scalar<string>("SELECT \"ChatConfigVersion\" FROM \"Conversations\"");
        Assert.NotEqual(ConversationalAgent.ChatConfigVersion, first);
        Assert.StartsWith("document-chat-v2:", first);
        Assert.True(first.Length <= 64);
        await PutAsync(client, project, Item(a.Document), Item(b.Document, included: false));
        var t2 = await PostAsync(client, project, "U2");

        await RunAsync(t2, new FakeChatModel());

        var second = await Scalar<string>("SELECT \"ChatConfigVersion\" FROM \"Conversations\"");
        Assert.NotEqual(first, second);                                              // snapshot di sessione di un'altra selezione non riusabile
        Assert.Equal(2, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task A_payload_beyond_the_chat_capacity_fails_explicitly_before_any_model_call()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "grande", string.Join(' ', Enumerable.Range(0, 3500).Select(i => $"parola{i}")));
        var turn = await PostAsync(client, project, "Cosa dice?");
        var model = new FakeChatModel();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(turn, model));

        Assert.Equal(DocumentWorkException.CallLimitExceeded, ModelFailure.Classify(Unwrap(failure)).Code);
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
    }

    [Fact]
    public async Task A_lost_lease_commits_nothing_and_is_not_a_model_error()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "regole", "Vincolo di prova.");
        var turn = await PostAsync(client, project, "Cosa dice?");
        var claim = await ClaimAsync(turn);
        var model = new FakeChatModel { OnRequest = _ => Execute("UPDATE \"ChatTurns\" SET \"FencingToken\" = \"FencingToken\" + 1 WHERE \"Id\" = @t", ("t", turn)) };

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, model));

        Assert.IsAssignableFrom<LeaseLostException>(Unwrap(failure));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task The_atomic_commit_rejects_a_citation_outside_the_turn_snapshot_before_writing_anything()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var doc = await NewDocument(client, project, "regole", "Vincolo di prova.");
        var turnId = await PostAsync(client, project, "Cosa dice il documento?");
        var claim = await ClaimAsync(turnId);
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var turn = await db.ChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId, Ct);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == turn.ConversationId, Ct);
        var agent = new Microsoft.Agents.AI.ChatClientAgent(new ConversationalAgentTests.FakeModel([]));
        var session = await agent.CreateSessionAsync(Ct);
        DocumentChatAnswer Reply(Guid id) => new("documented", $"x [fonte:{id}]", [id]);
        ConversationalReply Make(DocumentChatAnswer answer) => new(
            answer.Answer, agent, session, new RestoredChatSession(session, false, "test"), conversation, turn, "document-chat-v2:test", "h", answer);
        var committer = scope.ServiceProvider.GetRequiredService<IChatTurnCommitter>();

        var failure = await Assert.ThrowsAsync<DocumentWorkException>(async () => await committer.CommitAsync(claim, Make(Reply(Guid.NewGuid())), Ct));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, failure.Code);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'Assistant'"));
        Assert.Equal("Running", await Scalar<string>("SELECT \"Status\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turnId)));
        // La stessa risposta con una fonte dello snapshot e' invece committata, con lo stesso identico percorso.
        var ok = await committer.CommitAsync(claim, Make(Reply(doc.SourceId)), Ct);
        Assert.Equal(ChatTurnCommitOutcome.Committed, ok.Outcome);
    }

    // ================================================================ supporto

    private sealed record Doc(Guid Document, Guid Version, Guid SourceId);

    private static object Item(Guid document, bool included = true) => new { documentId = document, included, mode = "LatestReady", versionId = (Guid?)null };

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

    /// <summary>Documento con una evidenza (se <paramref name="statement"/> è dato il documento è Ready, altrimenti resta in preparazione).</summary>
    private async Task<Doc> NewDocument(HttpClient client, Guid project, string name, string? statement)
    {
        var created = await Json(await Upload(client, project, Text(name), name + ".txt", displayName: name));
        var doc = new Doc(created.GetProperty("documentId").GetGuid(), created.GetProperty("id").GetGuid(), Guid.NewGuid());
        return statement is null ? doc : await MakeReady(doc, statement);
    }

    private async Task<Doc> MakeReady(Doc doc, string statement)
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
            Version = 1, JobId = job, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 1, CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
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
                    SourceId = doc.SourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
                    MessageId = null, Sequence = null, DocumentId = doc.Document, DocumentVersionId = doc.Version, PreparationId = preparation.Id,
                    ArtifactId = Guid.NewGuid(), BlockId = Guid.NewGuid(), Start = 0, End = statement.Length, Quote = statement,
                },
            ],
        };
        db.Evidences.Add(new EvidenceItem
        {
            Id = Guid.NewGuid(), VersionId = version, ProjectId = preparation.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Ordinal = 1,
            Key = "e1", Kind = EvidenceKind.Requirement, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement,
            Content = DocumentEvidenceCodec.EncodeContent(content),
        });
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

    /// <summary>Presa in carico come il dispatcher: Running con owner, fence e lease validi (il claim reale non e' parte di T058).</summary>
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

    private async Task<ChatTurnCommitResult> RunAsync(Guid turn, FakeChatModel model, Func<string, CancellationToken, ValueTask>? onDelta = null) =>
        await RunAsync(await ClaimAsync(turn), model, onDelta);

    private async Task<ChatTurnCommitResult> RunAsync(ChatTurnClaim claim, FakeChatModel model, Func<string, CancellationToken, ValueTask>? onDelta = null)
    {
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = FakeChatModel.Window,
            MaxOutputTokens = 1500, SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var client = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(client, options, NullLogger<ModelRuntimeMonitor>.Instance);
        var factory = new AgentFactory(options, client.ChatClient, (text, ct) => new(client.CountTokensAsync(text, ct)), null, monitor, client);
        var workflow = new DocumentChatWorkflow(App.Services.GetRequiredService<IServiceScopeFactory>(), factory, client, Options.Create(new DocumentOptions()));
        return await workflow.ExecuteAsync(claim, onDelta, Ct);
    }

    private async Task<string> AssistantOfAsync(Guid turn) =>
        await Scalar<string>("SELECT \"Body\" FROM \"ConversationMessages\" WHERE \"ChatTurnId\" = @t", ("t", turn));

    private async Task<string> FormalCountsAsync() => string.Join(
        ',', await Task.WhenAll(new[] { "Evidences", "EvidenceExtractionVersions", "EvidenceExtractionJobs", "SourceSnapshots", "AnalysisRevisions" }
            .Select(async t => await Count($"SELECT count(*) FROM \"{t}\""))));

    private static string Answer(string mode, string answer) => JsonSerializer.Serialize(new { mode, answer });

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

    internal sealed record ChatRequest(string System, IReadOnlyList<(string Role, string Text)> Messages)
    {
        public string AllText => string.Join("\n", Messages.Select(m => m.Text));

        public string FirstSourceId => Regex.Match(Messages[^1].Text, "fonte:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})").Groups[1].Value;
    }

    /// <summary>Modello finto a livello HTTP (llama.cpp simulato): tokenizer a parole, finestra fissa, risposta documentata sulla prima fonte per default.</summary>
    internal sealed class FakeChatModel : HttpMessageHandler
    {
        public const int Window = 4000;

        public List<ChatRequest> Requests { get; } = [];

        public Func<ChatRequest, string>? Responder { get; init; }

        public Func<ChatRequest, Task>? OnRequest { get; init; }

        public string FinishReason { get; init; } = "stop";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models":
                    return Json("""{"object":"list","data":[{"id":"Qwen-A","object":"model","status":{"value":"loaded","args":["llama-server"]}}]}""");
                case "/props":
                    return Json("{\"default_generation_settings\":{\"n_ctx\":" + Window + ",\"params\":{\"n_predict\":-1}},\"total_slots\":1}");
                case "/tokenize":
                {
                    using var doc = JsonDocument.Parse(body);
                    var count = Words(doc.RootElement.GetProperty("content").GetString()!);
                    return Json("{\"tokens\":[" + string.Join(',', Enumerable.Range(0, count)) + "]}");
                }

                case "/apply-template":
                {
                    using var doc = JsonDocument.Parse(body);
                    var prompt = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => $"[{m.GetProperty("role").GetString()}] {TextOf(m.GetProperty("content"))}"));
                    return Json(JsonSerializer.Serialize(new { prompt }));
                }

                case "/v1/chat/completions":
                {
                    using var doc = JsonDocument.Parse(body);
                    var messages = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (Role: m.GetProperty("role").GetString()!, Text: TextOf(m.GetProperty("content")))).ToList();
                    var record = new ChatRequest(
                        string.Join("\n", messages.Where(m => m.Role is "system" or "developer").Select(m => m.Text)),
                        [.. messages.Where(m => m.Role is not ("system" or "developer"))]);
                    lock (Requests)
                    {
                        Requests.Add(record);
                    }

                    if (OnRequest is not null)
                    {
                        await OnRequest(record);
                    }

                    var content = Responder?.Invoke(record) ?? Answer("documented", $"Massimo 5 ordini al giorno [fonte:{record.FirstSourceId}].");
                    return Json(JsonSerializer.Serialize(new
                    {
                        id = "x", @object = "chat.completion", created = 1, model = "Qwen-A",
                        choices = new[] { new { index = 0, finish_reason = FinishReason, message = new { role = "assistant", content } } },
                        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
                    }));
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static string TextOf(JsonElement content) => content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));

        private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
