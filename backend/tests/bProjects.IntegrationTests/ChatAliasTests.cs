using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>Fixture comuni dei test della chat con alias (T128): snapshot di fonti documentali, evidenze con citazioni condivise e multi-blocco.</summary>
internal static class ChatAliasFixtures
{
    public static readonly Guid S1 = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid S2 = Guid.Parse("22222222-2222-4222-8222-222222222222");
    public static readonly Guid S3 = Guid.Parse("33333333-3333-4333-8333-333333333333");
    public static readonly Guid Group1 = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    public static readonly Guid Group2 = Guid.Parse("a2222222-2222-4222-8222-222222222222");
    public const string SnapshotHash = "5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a";

    public static PersistedSource Source(Guid id, Guid group, int part, string quote, int start = 0, EvidenceSourceRole role = EvidenceSourceRole.Primary) => new()
    {
        SourceId = id, Kind = EvidenceSourceKind.DocumentPassage, Role = role, CitationGroupId = group, Part = part, MessageId = null, Sequence = null,
        DocumentId = Guid.Parse("d0000000-0000-4000-8000-000000000001"), DocumentVersionId = Guid.Parse("d0000000-0000-4000-8000-000000000002"),
        PreparationId = Guid.Parse("d0000000-0000-4000-8000-000000000003"), ArtifactId = Guid.Parse("d0000000-0000-4000-8000-000000000004"),
        BlockId = Guid.Parse("d0000000-0000-4000-8000-000000000005"), Start = start, End = start + quote.Length, Quote = quote,
    };

    public static ChatEvidence Evidence(int ordinal, string statement, params PersistedSource[] sources) => new(
        Guid.Parse($"e0000000-0000-4000-8000-{ordinal:D12}"), Guid.Parse("f0000000-0000-4000-8000-000000000001"), ordinal, EvidenceKind.Requirement,
        EvidencePolarity.Affirmed, EvidenceCertainty.Declared, statement, sources);

    public static ChatSnapshotSources Snapshot(params ChatEvidence[] evidence) => new(SnapshotHash, 0, evidence);

    /// <summary>E1 → S1; E2 → S2 e S1 (S1 condiviso); E3 → multi-blocco: gruppo 2 parti 0/1 con S3 e S2 (S2 condiviso).</summary>
    public static ChatSnapshotSources Rich() => Snapshot(
        Evidence(1, "Massimo 5 ordini al giorno.", Source(S1, Group1, 0, "Il cliente A puo emettere al massimo 5 ordini al giorno.")),
        Evidence(2, "Il termine e 30 giorni.", Source(S2, Group1, 0, "Pagamento a 30 giorni.", start: 100), Source(S1, Group2, 1, "Il cliente A puo emettere al massimo 5 ordini al giorno.")),
        Evidence(3, "Fatturazione mensile.", Source(S3, Group2, 0, "La fatturazione e mensile", start: 200), Source(S2, Group2, 1, "Pagamento a 30 giorni.", start: 100)));

    public static string Reply(string mode, string answer) => JsonSerializer.Serialize(new { mode, answer });

    public static ChatAliasCatalog RichCatalog() => ChatAliasCatalog.Build(Rich());
}

/// <summary>
/// T128 (DET) — catalogo alias della chat documentale (`chat-alias-catalog-1`): un alias per SourceId distinto in ordine di lettura, fonte condivisa da più
/// evidenze con un solo alias, parti di una citazione multi-blocco con alias distinti e legami (gruppo, parte, ruolo) invariati, hash deterministico.
/// </summary>
public sealed class ChatAliasCatalogTests
{
    [Fact]
    public void Aliases_are_per_distinct_source_in_reading_order_not_per_evidence()
    {
        var catalog = ChatAliasFixtures.RichCatalog();

        Assert.Equal(["F1", "F2", "F3"], catalog.Sources.Select(s => s.Alias));
        Assert.Equal([ChatAliasFixtures.S1, ChatAliasFixtures.S2, ChatAliasFixtures.S3], catalog.Sources.Select(s => s.SourceId));
        Assert.Equal(3, catalog.Evidence.Count);                                       // tre evidenze, tre fonti: ma S1 e S2 sono citate da due evidenze ciascuna
        Assert.Equal(catalog.SourceIds, new HashSet<Guid> { ChatAliasFixtures.S1, ChatAliasFixtures.S2, ChatAliasFixtures.S3 });
        Assert.Equal("F1", catalog.AliasOf(ChatAliasFixtures.S1));
        Assert.Equal(ChatAliasFixtures.S3, catalog.Source("F3")!.SourceId);
    }

    [Fact]
    public void A_source_shared_by_several_evidences_has_one_alias_and_the_links_of_each_evidence_are_kept()
    {
        var catalog = ChatAliasFixtures.RichCatalog();

        Assert.Equal(["F1"], catalog.Evidence[0].Links.Select(l => l.SourceAlias));
        Assert.Equal(["F2", "F1"], catalog.Evidence[1].Links.Select(l => l.SourceAlias));          // ordine persistito, non riordinato per alias
        Assert.Contains(catalog.Evidence[1].Links, l => l.SourceAlias == "F1" && l.Part == 1 && l.CitationGroupId == ChatAliasFixtures.Group2);
        Assert.Contains(catalog.Evidence[0].Links, l => l.SourceAlias == "F1" && l.Part == 0 && l.CitationGroupId == ChatAliasFixtures.Group1);
    }

    [Fact]
    public void The_parts_of_a_multi_block_citation_are_distinct_sources_with_their_own_alias_and_the_group_and_order_unchanged()
    {
        var catalog = ChatAliasFixtures.RichCatalog();

        var links = catalog.Evidence[2].Links;
        Assert.Equal(["F3", "F2"], links.Select(l => l.SourceAlias));
        Assert.Equal([0, 1], links.Select(l => l.Part));
        Assert.All(links, l => Assert.Equal(ChatAliasFixtures.Group2, l.CitationGroupId));
        Assert.NotEqual(links[0].SourceAlias, links[1].SourceAlias);
    }

    [Fact]
    public void The_catalog_keeps_verbatim_quote_range_and_provenance_of_the_ledger_unchanged()
    {
        var f3 = ChatAliasFixtures.RichCatalog().Source("F3")!;

        Assert.Equal("La fatturazione e mensile", f3.Quote);
        Assert.Equal((200, 225), (f3.Start, f3.End));
        Assert.Equal(EvidenceSourceKind.DocumentPassage, f3.Provenance);
        Assert.Equal(Guid.Parse("d0000000-0000-4000-8000-000000000005"), f3.BlockId);
    }

    [Fact]
    public void The_catalog_and_its_hash_are_a_pure_deterministic_function_of_the_snapshot()
    {
        var a = ChatAliasFixtures.RichCatalog();
        var b = ChatAliasFixtures.RichCatalog();

        Assert.Equal(a.CatalogHash, b.CatalogHash);
        Assert.Matches("^[0-9a-f]{64}$", a.CatalogHash);
        Assert.Equal(a.Sources, b.Sources);
    }

    [Fact]
    public void The_hash_changes_when_quote_range_role_part_group_snapshot_or_source_order_change()
    {
        var baseline = ChatAliasFixtures.RichCatalog().CatalogHash;
        PersistedSource Src(Guid id, Guid group, int part, string quote, int start = 0, EvidenceSourceRole role = EvidenceSourceRole.Primary) =>
            ChatAliasFixtures.Source(id, group, part, quote, start, role);

        ChatSnapshotSources Variant(Func<ChatEvidence[], ChatEvidence[]> change) => ChatAliasFixtures.Snapshot(change([.. ChatAliasFixtures.Rich().Evidence]));

        var quote = Variant(e => [e[0], e[1], e[2] with { Sources = [Src(ChatAliasFixtures.S3, ChatAliasFixtures.Group2, 0, "La fatturazione e trimestrale", 200), e[2].Sources[1]] }]);
        var role = Variant(e => [e[0], e[1], e[2] with { Sources = [Src(ChatAliasFixtures.S3, ChatAliasFixtures.Group2, 0, "La fatturazione e mensile", 200, EvidenceSourceRole.Context), e[2].Sources[1]] }]);
        var order = Variant(e => [e[1], e[0], e[2]]);
        var snapshot = ChatAliasFixtures.Rich() with { SnapshotHash = new string('b', 64) };

        Assert.NotEqual(baseline, ChatAliasCatalog.Build(quote).CatalogHash);
        Assert.NotEqual(baseline, ChatAliasCatalog.Build(role).CatalogHash);
        Assert.NotEqual(baseline, ChatAliasCatalog.Build(order).CatalogHash);
        Assert.NotEqual(baseline, ChatAliasCatalog.Build(snapshot).CatalogHash);
    }

    [Fact]
    public void The_same_source_id_with_a_different_quote_or_range_in_another_evidence_is_source_snapshot_invalid()
    {
        var snapshot = ChatAliasFixtures.Snapshot(
            ChatAliasFixtures.Evidence(1, "Uno.", ChatAliasFixtures.Source(ChatAliasFixtures.S1, ChatAliasFixtures.Group1, 0, "Testo uno.")),
            ChatAliasFixtures.Evidence(2, "Due.", ChatAliasFixtures.Source(ChatAliasFixtures.S1, ChatAliasFixtures.Group2, 0, "Testo diverso.")));

        var failure = Assert.Throws<DocumentWorkException>(() => ChatAliasCatalog.Build(snapshot));

        Assert.Equal(DocumentWorkException.SourceSnapshotInvalid, failure.Code);
    }

    [Fact]
    public void An_evidence_with_invalid_sources_is_source_snapshot_invalid_and_never_silently_aliased()
    {
        var snapshot = ChatAliasFixtures.Snapshot(ChatAliasFixtures.Evidence(
            1, "Uno.", ChatAliasFixtures.Source(ChatAliasFixtures.S1, ChatAliasFixtures.Group1, 0, "Testo uno."), ChatAliasFixtures.Source(ChatAliasFixtures.S1, ChatAliasFixtures.Group1, 0, "Testo uno.")));

        Assert.Equal(DocumentWorkException.SourceSnapshotInvalid, Assert.Throws<DocumentWorkException>(() => ChatAliasCatalog.Build(snapshot)).Code);
    }

    [Fact]
    public void An_empty_snapshot_has_an_empty_catalog_and_resolves_nothing()
    {
        var catalog = ChatAliasCatalog.Build(ChatAliasFixtures.Snapshot());

        Assert.Empty(catalog.Sources);
        Assert.Null(catalog.Source("F1"));
    }

    [Theory]
    [InlineData("f1")]
    [InlineData("F01")]
    [InlineData("F0")]
    [InlineData(" F1")]
    [InlineData("F1 ")]
    [InlineData("F")]
    [InlineData("")]
    [InlineData("F4")]
    [InlineData("S1")]
    public void Resolution_is_exact_and_case_sensitive_never_fuzzy(string alias)
    {
        Assert.Null(ChatAliasFixtures.RichCatalog().Source(alias));
    }
}

/// <summary>
/// T128 (DET) — risoluzione degli alias nella risposta: formato esatto <c>[fonte:F&lt;n&gt;]</c>, rifiuto di sconosciuti/malformati/UUID, traduzione al solo
/// formato canonico <c>[fonte:UUID]</c>, validazione esistente sul canonico, nessuna alterazione del testo né fonte aggiunta o sostituita.
/// </summary>
public sealed class ChatAliasResolutionTests
{
    private static readonly Guid S1 = ChatAliasFixtures.S1;
    private static readonly Guid S2 = ChatAliasFixtures.S2;
    private static readonly Guid S3 = ChatAliasFixtures.S3;

    private static DocumentChatAliasResult Resolve(string mode, string answer) =>
        DocumentChatContract.ValidateAlias(ChatAliasFixtures.Reply(mode, answer), ChatAliasFixtures.RichCatalog());

    private static DocumentWorkException Reject(string response) =>
        Assert.Throws<DocumentWorkException>(() => DocumentChatContract.ValidateAlias(response, ChatAliasFixtures.RichCatalog()));

    [Fact]
    public void Valid_aliases_are_translated_to_canonical_ids_in_the_text_and_the_citation_order_is_that_of_the_first_citation()
    {
        var result = Resolve("documented", "Massimo 5 [fonte:F1] e a 30 giorni [fonte:F2] poi ancora [fonte:F1].");

        Assert.Equal($"Massimo 5 [fonte:{S1}] e a 30 giorni [fonte:{S2}] poi ancora [fonte:{S1}].", result.Answer.Answer);
        Assert.Equal("documented", result.Answer.Mode);
        Assert.Equal([S1, S2], result.Answer.SourceIds);
        Assert.Matches("^[0-9a-f]{64}$", result.AiOutputHash);
    }

    [Fact]
    public void The_result_never_contains_an_alias_marker()
    {
        var result = Resolve("documented", "A [fonte:F3] B [fonte:F2] C [fonte:F1].");

        Assert.DoesNotMatch(@"\[fonte:F\d", result.Answer.Answer);
        Assert.Equal([S3, S2, S1], result.Answer.SourceIds);
    }

    [Fact]
    public void Repeated_markers_of_the_same_alias_are_allowed_exactly_like_the_canonical_contract_and_stay_in_place()
    {
        var result = Resolve("documented", "[fonte:F2][fonte:F2] [fonte:F2]");

        Assert.Equal($"[fonte:{S2}][fonte:{S2}] [fonte:{S2}]", result.Answer.Answer);
        Assert.Equal([S2], result.Answer.SourceIds);
    }

    [Fact]
    public void Only_the_source_chosen_by_the_model_is_attributed_and_uncited_sources_are_never_added()
    {
        var result = Resolve("documented", "Fatturazione mensile [fonte:F3].");

        Assert.Equal([S3], result.Answer.SourceIds);
        Assert.DoesNotContain(S1.ToString(), result.Answer.Answer);
        Assert.DoesNotContain(S2.ToString(), result.Answer.Answer);
    }

    [Fact]
    public void A_multi_block_citation_cites_each_part_with_its_own_alias_in_the_chosen_order()
    {
        var result = Resolve("documented", "Prima la parte 1 [fonte:F2] e poi la parte 0 [fonte:F3].");

        Assert.Equal([S2, S3], result.Answer.SourceIds);
        Assert.Equal($"Prima la parte 1 [fonte:{S2}] e poi la parte 0 [fonte:{S3}].", result.Answer.Answer);
    }

    [Fact]
    public void Everything_but_the_markers_is_preserved_byte_for_byte_including_unicode_newlines_quotes_and_alias_like_words()
    {
        const string text = "Perché «F1» è la fonte F1: \"ordini\"\nriga 2 \\ tab\t€ 😀 [nota] fonte:F1 [fonte] [fonte:F1] fine.";

        var result = Resolve("documented", text);

        Assert.Equal(text.Replace("[fonte:F1]", $"[fonte:{S1}]"), result.Answer.Answer);
    }

    [Fact]
    public void A_no_document_use_answer_without_markers_passes_untouched_with_no_sources()
    {
        var result = Resolve("no_document_use", "Buongiorno, quali utenti userebbero il portale?");

        Assert.Equal("Buongiorno, quali utenti userebbero il portale?", result.Answer.Answer);
        Assert.Empty(result.Answer.SourceIds);
    }

    [Fact]
    public void An_unknown_alias_is_rejected_as_work_output_invalid_never_replaced_by_a_plausible_source()
    {
        var failure = Reject(ChatAliasFixtures.Reply("documented", "Dice X [fonte:F4]."));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, failure.Code);
        Assert.DoesNotContain("F4", failure.Message);                              // il messaggio non riporta il testo del modello
        Assert.DoesNotContain("Dice X", failure.Message);
    }

    [Fact]
    public void One_unknown_alias_among_valid_ones_invalidates_the_whole_answer()
    {
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, Reject(ChatAliasFixtures.Reply("documented", "A [fonte:F1] B [fonte:F99].")).Code);
    }

    [Theory]
    [InlineData("[fonte:f1]")]
    [InlineData("[fonte:F01]")]
    [InlineData("[fonte:F0]")]
    [InlineData("[fonte: F1]")]
    [InlineData("[fonte:F1 ]")]
    [InlineData("[fonte:F1,F2]")]
    [InlineData("[fonte:F1][fonte:F-1]")]
    [InlineData("[FONTE:F1]")]
    [InlineData("[Fonte:F1]")]
    [InlineData("[fonte:F1")]
    [InlineData("[fonte:]")]
    [InlineData("[fonte:S1]")]
    [InlineData("[fonte:I1]")]
    [InlineData("[fonte:F1a]")]
    [InlineData("[fonte:１]")]
    [InlineData("[fonte:F１]")]
    [InlineData("[fonte:F1]]  [fonte:F]")]
    public void Malformed_markers_invalidate_the_whole_answer(string marker)
    {
        var failure = Reject(ChatAliasFixtures.Reply("documented", $"Valida [fonte:F1] poi {marker} fine."));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, failure.Code);
    }

    [Fact]
    public void A_canonical_uuid_marker_in_place_of_the_alias_is_rejected_even_if_the_id_belongs_to_the_snapshot()
    {
        var failure = Reject(ChatAliasFixtures.Reply("documented", $"Massimo 5 [fonte:{S1}]."));

        Assert.Equal(DocumentWorkException.WorkOutputInvalid, failure.Code);
    }

    [Fact]
    public void A_documented_answer_without_any_marker_and_a_no_document_use_answer_with_one_are_rejected_by_the_existing_validation()
    {
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, Reject(ChatAliasFixtures.Reply("documented", "Nessuna fonte.")).Code);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, Reject(ChatAliasFixtures.Reply("no_document_use", "Cita [fonte:F1].")).Code);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"mode\":\"documented\"}")]
    [InlineData("{\"mode\":\"documented\",\"answer\":\"x [fonte:F1]\",\"extra\":1}")]
    [InlineData("{\"mode\":\"documented\",\"answer\":\"x [fonte:F1]\",\"answer\":\"y [fonte:F1]\"}")]
    [InlineData("{\"mode\":\"altro\",\"answer\":\"x [fonte:F1]\"}")]
    [InlineData("{\"mode\":\"documented\",\"answer\":\"  \"}")]
    public void The_closed_json_contract_is_unchanged(string response)
    {
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, Reject(response).Code);
    }

    [Fact]
    public void Resolution_is_deterministic_and_does_not_depend_on_other_snapshot_content_or_call_order()
    {
        var a = Resolve("documented", "X [fonte:F2] Y [fonte:F1].");
        var b = Resolve("documented", "X [fonte:F2] Y [fonte:F1].");

        Assert.Equal(a.Answer, b.Answer, new AnswerComparer());
        Assert.Equal(a.AiOutputHash, b.AiOutputHash);
        Assert.NotEqual(a.AiOutputHash, Resolve("documented", "X [fonte:F1] Y [fonte:F2].").AiOutputHash);
    }

    [Fact]
    public void The_alias_contract_does_not_change_the_canonical_validation_of_v2()
    {
        // Lo stesso testo con UUID continua a essere valido per il decoder v2 e il marcatore con alias non lo e': nessuna interpretazione condivisa.
        var allowed = new HashSet<Guid> { S1, S2 };

        Assert.Equal([S1], DocumentChatContract.Validate(ChatAliasFixtures.Reply("documented", $"X [fonte:{S1}]"), allowed).SourceIds);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, Assert.Throws<DocumentWorkException>(() => DocumentChatContract.Validate(ChatAliasFixtures.Reply("documented", "X [fonte:F1]"), allowed)).Code);
    }

    private sealed class AnswerComparer : IEqualityComparer<DocumentChatAnswer>
    {
        public bool Equals(DocumentChatAnswer? x, DocumentChatAnswer? y) => x!.Mode == y!.Mode && x.Answer == y.Answer && x.SourceIds.SequenceEqual(y.SourceIds);

        public int GetHashCode(DocumentChatAnswer obj) => obj.Answer.GetHashCode();
    }
}

/// <summary>
/// T128 (DET) — messaggio e prompt v3: alias `F&lt;n&gt;` con citazione letterale, nessun UUID/hash/id di gruppo o evidenza; prompt nuovo, v2 storico intatto.
/// </summary>
public sealed class ChatAliasMessageTests
{
    private static string Message(string question = "Quanti ordini?")
    {
        var sources = ChatAliasFixtures.Rich();
        var messages = DocumentChatWorkflow.BuildAliasMessages([], sources, ChatAliasCatalog.Build(sources), question);
        return Assert.Single(messages).Text;
    }

    [Fact]
    public void The_message_exposes_aliases_and_verbatim_quotes_and_never_a_uuid_hash_group_or_evidence_id()
    {
        var text = Message();

        Assert.Contains("fonte:F1", text);
        Assert.Contains("fonte:F2", text);
        Assert.Contains("fonte:F3", text);
        Assert.Contains("Il cliente A puo emettere al massimo 5 ordini al giorno.", text);
        Assert.Contains("La fatturazione e mensile", text);
        Assert.DoesNotMatch("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", text);
        Assert.DoesNotContain(ChatAliasFixtures.SnapshotHash, text);
        Assert.DoesNotMatch("[0-9a-f]{32}", text);
        Assert.DoesNotContain("fonte:<UUID>", text);
    }

    [Fact]
    public void A_source_shared_by_several_evidences_appears_under_the_same_alias_each_time_and_there_is_no_alias_beyond_the_catalog()
    {
        var text = Message();

        Assert.Equal(2, Regex.Matches(text, @"fonte:F1 ").Count);                      // E1 e E2
        Assert.Equal(2, Regex.Matches(text, @"fonte:F2 ").Count);                      // E2 e E3
        Assert.Single(Regex.Matches(text, @"fonte:F3 "));
        Assert.DoesNotContain("fonte:F4", text);
    }

    [Fact]
    public void The_message_ends_with_the_pm_question_and_declares_the_evidence_count_as_in_v2()
    {
        var text = Message("Domanda del PM?");

        Assert.StartsWith("Evidenze documentali congelate per questo turno (3):", text);
        Assert.Contains("\nMessaggio del project manager:\nDomanda del PM?", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void The_document_span_covers_only_the_server_composed_evidence_block_and_the_dialogue_is_kept_as_normal_messages()
    {
        var sources = ChatAliasFixtures.Rich();
        var history = new List<ConversationMessage>
        {
            new() { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Sequence = 1, Role = MessageRole.User, Body = "U1", CreatedAt = DateTimeOffset.UtcNow },
            new() { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Sequence = 2, Role = MessageRole.Assistant, Body = "A1", CreatedAt = DateTimeOffset.UtcNow },
        };

        var messages = DocumentChatWorkflow.BuildAliasMessages(history, sources, ChatAliasCatalog.Build(sources), "Q");

        Assert.Equal(["U1", "A1"], messages.Take(2).Select(m => m.Text));
        var last = messages[^1];
        var span = (int[])last.AdditionalProperties![bProjects.App.AI.ContextComposition.DocumentSpansProperty]!;
        Assert.Equal(0, span[0]);
        Assert.Equal("Evidenze", last.Text[span[0]..][..8]);
        Assert.DoesNotContain("Messaggio del project manager", last.Text[span[0]..span[1]]);
    }

    [Fact]
    public void The_v3_prompt_is_a_new_resource_citing_aliases_and_the_v2_prompt_is_historic_and_unchanged()
    {
        var v3 = DocumentChatWorkflow.InstructionsV3;
        var v2 = DocumentChatWorkflow.Instructions;

        Assert.NotEqual(v2, v3);
        Assert.Contains("[fonte:F1]", v3);
        Assert.Contains("F<n>", v3);
        Assert.Contains("no_document_use", v3);
        Assert.Contains("SOLO le evidenze", v3);
        Assert.Contains("Non produci requisiti formali", v3);
        Assert.DoesNotContain("<UUID>", v3);
        Assert.DoesNotContain("UUID", v3);
        Assert.Contains("[fonte:<UUID>]", v2);
        Assert.DoesNotContain("F<n>", v2);
    }

    [Fact]
    public void The_v3_prompt_forbids_copying_identifiers_from_previous_answers_and_inventing_aliases()
    {
        var v3 = DocumentChatWorkflow.InstructionsV3;

        Assert.Contains("mai", v3);
        Assert.Matches("(?i)risposte precedenti", v3);
        Assert.Matches("(?i)alias.*(non|mai).*inventa|inventa.*alias", v3);
    }
}

/// <summary>
/// T128 (DET) — versioni e congelamento: definizione/prompt/schema/catalogo nuovi per le sole nuove richieste; turni storici (senza versioni) restano v2;
/// definizione o versioni ignote non sono mai reinterpretate (workflow_incompatible).
/// </summary>
public sealed class ChatAliasVersionTests
{
    private static ChatTurn Turn(string? prompt = null, string? config = null) => new() { Id = Guid.NewGuid(), PromptVersion = prompt, AgentConfig = config };

    [Fact]
    public void The_v2_definition_and_the_v3_versions_are_explicit_and_distinct()
    {
        Assert.Equal("project-chat-documents-v2", DocumentChatWorkflow.DefinitionVersion);
        Assert.Equal("document-chat-v2", DocumentChatWorkflow.PromptVersion);
        Assert.Equal("document-chat-1", DocumentChatContract.SchemaVersion);
        Assert.Equal("project-chat-documents-v3", DocumentChatWorkflow.DefinitionVersionV3);
        Assert.Equal("document-chat-v3", DocumentChatWorkflow.PromptVersionV3);
        Assert.Equal("document-chat-alias-1", DocumentChatWorkflow.SchemaVersionV3);
        Assert.Equal("chat-alias-catalog-1", ChatAliasCatalog.Version);
        Assert.Equal(DocumentChatWorkflow.DefinitionVersionV3, SourceSnapshotStore.ChatDefinition);
        Assert.Equal(DocumentChatWorkflow.PromptVersionV3, SourceSnapshotStore.ChatPromptVersion);
    }

    [Fact]
    public void A_turn_without_frozen_versions_is_a_historic_v2_turn()
    {
        Assert.Equal(2, DocumentChatWorkflow.ContractOf(Turn()));
    }

    [Fact]
    public void Freezing_a_new_documentary_turn_writes_the_v3_versions_and_they_are_recognised_as_v3()
    {
        var turn = Turn();

        SourceSnapshotStore.FreezeChat(turn);

        Assert.Equal(DocumentChatWorkflow.PromptVersionV3, turn.PromptVersion);
        using var versions = JsonDocument.Parse(turn.AgentConfig!);
        var root = versions.RootElement;
        Assert.Equal(DocumentChatWorkflow.DefinitionVersionV3, root.GetProperty("chatDefinition").GetString());
        Assert.Equal(DocumentChatWorkflow.PromptVersionV3, root.GetProperty("chat").GetString());
        Assert.Equal(DocumentChatWorkflow.SchemaVersionV3, root.GetProperty("chatAiSchema").GetString());
        Assert.Equal(ChatAliasCatalog.Version, root.GetProperty("chatCatalog").GetString());
        Assert.Equal(DocumentChatWorkflow.AlgorithmVersionV3, root.GetProperty("algorithm").GetString());
        Assert.Equal(DocumentChatWorkflow.ValidationVersionV3, root.GetProperty("validation").GetString());
        Assert.Equal(3, DocumentChatWorkflow.ContractOf(turn));
        Assert.Equal(SourceSnapshotStore.ChatPromptVersions(), turn.AgentConfig);
    }

    [Theory]
    [InlineData("document-chat-v2", null)]                                               // v2 non e mai scritta: nessuna reinterpretazione
    [InlineData("document-chat-v3", null)]                                               // v3 senza versioni congelate
    [InlineData("document-chat-v3", "{}")]
    [InlineData("document-chat-v9", "FROZEN")]                                           // prompt ignoto con versioni v3
    [InlineData(null, "FROZEN")]                                                         // versioni senza prompt
    [InlineData("document-chat-v3", "not json")]
    [InlineData("document-chat-v3", "[]")]
    public void An_unknown_or_partial_freeze_is_not_supported_never_downgraded_or_reinterpreted(string? prompt, string? config)
    {
        Assert.Null(DocumentChatWorkflow.ContractOf(Turn(prompt, config == "FROZEN" ? SourceSnapshotStore.ChatPromptVersions() : config)));
    }

    [Theory]
    [InlineData("chatDefinition", "project-chat-documents-v9")]
    [InlineData("chat", "document-chat-v9")]
    [InlineData("chatAiSchema", "document-chat-alias-9")]
    [InlineData("chatCatalog", "chat-alias-catalog-9")]
    [InlineData("algorithm", "chat-single-v9")]
    [InlineData("validation", "document-chat-validation-v9")]
    public void Any_single_different_frozen_version_makes_the_turn_incompatible(string key, string value)
    {
        var frozen = System.Text.Json.Nodes.JsonNode.Parse(SourceSnapshotStore.ChatPromptVersions())!.AsObject();
        frozen[key] = value;

        Assert.Null(DocumentChatWorkflow.ContractOf(Turn(DocumentChatWorkflow.PromptVersionV3, frozen.ToJsonString())));
    }

    [Fact]
    public void The_chat_session_config_version_is_specific_to_the_contract_and_fits_the_column()
    {
        var v2 = DocumentChatWorkflow.ChatConfigVersionFor("snap", "cfg");
        var v3 = DocumentChatWorkflow.ChatConfigVersionFor("snap", "cfg", aliasContract: true);

        Assert.StartsWith("document-chat-v2:", v2);
        Assert.StartsWith("document-chat-v3:", v3);
        Assert.True(v3.Length <= 64);
    }
}
