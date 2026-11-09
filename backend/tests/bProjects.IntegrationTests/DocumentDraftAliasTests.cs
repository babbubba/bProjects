using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>Fixture comuni dei test della bozza con alias (T127): contenuto confermato 1.1, catalogo e DTO equivalente alla risposta valida di <see cref="DraftFixtures"/>.</summary>
internal static class DraftAliasFixtures
{
    public static readonly Guid PmRev = Guid.Parse("a1000000-0000-4000-8000-000000000003");

    public static DocumentAnalysisContent Open() =>
        DraftFixtures.Content(Guid.Parse("c0000000-0000-4000-8000-000000000001"), Guid.Parse("c0000000-0000-4000-8000-000000000002"), Guid.Parse("c0000000-0000-4000-8000-000000000003"));

    /// <summary>Conflitto risolto dal PM: la fonte PmRevision sostiene la sola risoluzione (nessun item), quindi non e citabile.</summary>
    public static DocumentAnalysisContent Resolved()
    {
        var open = Open();
        var pmSource = new DocumentAnalysisSource
        {
            Id = PmRev, Kind = DocumentAnalysisSourceKind.PmRevision, MessageId = null, MessageRole = null, RevisionId = Guid.NewGuid(), DocumentId = null,
            DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = null, End = null, Quote = null,
        };
        var conflict = open.Conflicts[0] with
        {
            Status = AnalysisConflictStatus.Resolved,
            Resolution = new AnalysisConflictResolution { SelectedAlternativeIds = [DraftFixtures.Alt1], Reason = "Scelta del PM per la fatturazione nel portale", EvidenceRefs = [PmRev] },
        };
        return open with { Sources = [.. open.Sources, pmSource], Conflicts = [conflict] };
    }

    /// <summary>Alias del contenuto <see cref="Open"/>: I1 obiettivo, I2 requisito, I3 assunzione, I4 domanda, I5/I6 alternative; F1 messaggio, F2 passaggio.</summary>
    public static JsonObject Block(string[] items, string markdown, string[] sources) => new()
    {
        ["items"] = new JsonArray([.. items.Select(i => (JsonNode)JsonValue.Create(i)!)]),
        ["markdown"] = markdown,
        ["sources"] = new JsonArray([.. sources.Select(i => (JsonNode)JsonValue.Create(i)!)]),
    };

    public static JsonObject Valid() => new()
    {
        ["sectionKey"] = "documento",
        ["blocks"] = new JsonArray(
            Block(["I1"], "- Obiettivo: realizzare un portale clienti [fonte:F1]", ["F1"]),
            Block(["I2"], "- Requisito: il portale mostra lo stato dei ticket [fonte:F2]", ["F2"]),
            Block(["I3"], "- Ipotesi da verificare: i clienti usano un browser desktop", []),
            Block(["I4"], "- Da definire: quanti clienti useranno il portale?", []),
            Block(["I5", "I6"], "Conflitto aperto. Alternativa 1: fatturazione nel portale [fonte:F1]. Alternativa 2: fatturazione fuori perimetro [fonte:F2].", ["F1", "F2"])),
    };

    public static JsonObject BlockOf(this JsonObject o, int index) => o["blocks"]!.AsArray()[index]!.AsObject();
}

/// <summary>
/// T127 (DET) — catalogo alias della bozza (`draft-alias-catalog-1`): funzione pura della revisione confermata congelata; item e alternative
/// `I&lt;n&gt;`, fonti `F&lt;n&gt;` solo se sostengono un item; id del conflitto e fonti della sola risoluzione non hanno alias.
/// </summary>
public sealed class DraftAliasCatalogTests
{
    [Fact]
    public void Items_and_sources_are_numbered_deterministically_in_the_order_of_the_trusted_package()
    {
        var catalog = DraftAliasCatalog.Build(DraftAliasFixtures.Open());

        Assert.Equal(["I1", "I2", "I3", "I4", "I5", "I6"], catalog.Items.Select(i => i.Alias));
        Assert.Equal([DraftFixtures.Goal, DraftFixtures.Req, DraftFixtures.Assump, DraftFixtures.Question, DraftFixtures.Alt1, DraftFixtures.Alt2], catalog.Items.Select(i => i.ItemId));
        Assert.Equal([("F1", DraftFixtures.Msg), ("F2", DraftFixtures.Pass)], catalog.Sources.Select(s => (s.Alias, s.SourceId)));
        Assert.Equal(["F1"], catalog.Item("I1")!.SourceAliases);
        Assert.Empty(catalog.Item("I3")!.SourceAliases);
        Assert.Equal("I5", catalog.AliasOfItem(DraftFixtures.Alt1));
        Assert.Equal("F2", catalog.AliasOfSource(DraftFixtures.Pass));
        Assert.Null(catalog.Item("I7"));
        Assert.Null(catalog.Source("F3"));
    }

    [Fact]
    public void The_catalog_is_a_pure_function_of_the_content_and_its_hash_changes_with_the_identity_of_an_item()
    {
        var content = DraftAliasFixtures.Open();

        var first = DraftAliasCatalog.Build(content);
        var second = DraftAliasCatalog.Build(content);
        var other = DraftAliasCatalog.Build(content with { Goals = [content.Goals[0] with { Id = Guid.NewGuid() }] });

        Assert.Equal(first.CatalogHash, second.CatalogHash);
        Assert.Matches("^[0-9a-f]{64}$", first.CatalogHash);
        Assert.NotEqual(first.CatalogHash, other.CatalogHash);
        Assert.Equal("draft-alias-catalog-1", DraftAliasCatalog.Version);
    }

    [Fact]
    public void The_conflict_id_and_the_sources_of_the_resolution_alone_have_no_alias()
    {
        var catalog = DraftAliasCatalog.Build(DraftAliasFixtures.Resolved());

        Assert.Null(catalog.AliasOfItem(DraftFixtures.ConflictId));
        Assert.Null(catalog.AliasOfSource(DraftAliasFixtures.PmRev));
        Assert.Equal(2, catalog.Sources.Count);
        Assert.Equal(6, catalog.Items.Count);
    }

    [Fact]
    public void A_duplicate_item_id_or_a_source_missing_from_the_revision_is_an_integrity_conflict()
    {
        var content = DraftAliasFixtures.Open();

        var duplicate = Assert.Throws<DocumentWorkException>(() => DraftAliasCatalog.Build(content with { Requirements = [content.Requirements[0] with { Id = DraftFixtures.Goal }] }));
        var dangling = Assert.Throws<DocumentWorkException>(() => DraftAliasCatalog.Build(content with { Goals = [content.Goals[0] with { EvidenceRefs = [Guid.NewGuid()] }] }));

        Assert.Equal(DocumentWorkException.IntegrityConflict, duplicate.Code);
        Assert.Equal(DocumentWorkException.IntegrityConflict, dangling.Code);
    }
}

/// <summary>T127 (DET) — messaggio della generazione con soli alias: niente UUID, hash o id che il modello non deve riprodurre.</summary>
public sealed class DraftAliasMessageTests
{
    private static string Message(DocumentAnalysisContent content, int version = 2) =>
        DocumentSourceDraftWorkflow.BuildAliasMessages(
            new DocumentRunInputV11(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), version, content), DraftAliasCatalog.Build(content)).Single().Text;

    [Fact]
    public void The_message_exposes_every_item_and_alternative_with_an_alias_and_no_uuid_or_hash_at_all()
    {
        var text = Message(DraftAliasFixtures.Resolved());

        Assert.DoesNotMatch(new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-"), text);
        Assert.DoesNotMatch(new Regex("[0-9a-f]{32,}"), text);
        foreach (var (alias, snippet) in new[]
        {
            ("I1", "Realizzare un portale clienti"), ("I2", "Il portale mostra lo stato dei ticket"), ("I3", "I clienti usano un browser desktop"),
            ("I4", "Quanti clienti useranno il portale?"), ("I5", "Fatturazione nel portale"), ("I6", "Fatturazione fuori perimetro"),
        })
        {
            Assert.Matches(new Regex($@"\[{alias}\][^\n]*\n[^\n]*{Regex.Escape(snippet)}"), text);
        }

        Assert.Contains("La fatturazione e dentro o fuori dal perimetro?", text);
    }

    [Fact]
    public void Sources_are_listed_by_alias_and_the_resolution_keeps_the_selected_alias_and_reason_but_never_its_own_sources()
    {
        var text = Message(DraftAliasFixtures.Resolved());

        Assert.Contains("[F1]", text);
        Assert.Contains("[F2]", text);
        Assert.DoesNotContain("[F3]", text);
        Assert.Contains("Scelta del PM per la fatturazione nel portale", text);
        Assert.Matches(new Regex(@"alternativa scelta[^\n]*\[I5\]", RegexOptions.IgnoreCase), text);
        Assert.DoesNotContain("PmRevision", text);   // la fonte della sola risoluzione non e presente nei dati
        Assert.Contains("document-draft-alias-1", text);
    }

    [Fact]
    public void The_message_is_deterministic_and_does_not_depend_on_server_identifiers()
    {
        var content = DraftAliasFixtures.Open();

        Assert.Equal(Message(content, 2), Message(content, 7));
    }
}

/// <summary>T127 (DET) — DTO `document-draft-alias-1`: codec strict e JSON Schema con alias a pattern esatto.</summary>
public sealed class DraftAliasContractTests
{
    [Fact]
    public void The_schema_is_closed_and_constrains_aliases_with_exact_patterns_and_never_mentions_uuids()
    {
        var schema = DraftAliasContracts.JsonSchema();
        var text = schema.GetRawText();
        var block = schema.GetProperty("properties").GetProperty("blocks").GetProperty("items");

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(block.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["sectionKey", "blocks"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["items", "markdown", "sources"], block.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("^I[1-9][0-9]*$", block.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("pattern").GetString());
        Assert.Equal("^F[1-9][0-9]*$", block.GetProperty("properties").GetProperty("sources").GetProperty("items").GetProperty("pattern").GetString());
        Assert.DoesNotContain("uuid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("document-draft-alias-1", DraftAliasContracts.SchemaVersion);
    }

    [Fact]
    public void A_valid_dto_decodes_and_its_hash_is_independent_of_property_order_and_whitespace()
    {
        var dto = DraftAliasContracts.Decode(DraftAliasFixtures.Valid().ToJsonString());
        var reordered = """
            {  "blocks": [ { "sources": ["F1"], "markdown": "- Obiettivo", "items": ["I1"] } ],   "sectionKey": "documento" }
            """;
        var compact = """{"sectionKey":"documento","blocks":[{"items":["I1"],"markdown":"- Obiettivo","sources":["F1"]}]}""";

        Assert.Equal(5, dto.Blocks.Count);
        Assert.Equal(["I5", "I6"], dto.Blocks[4].Items);
        Assert.Equal(DraftAliasContracts.Hash(DraftAliasContracts.Decode(compact)), DraftAliasContracts.Hash(DraftAliasContracts.Decode(reordered)));
        Assert.Matches("^[0-9a-f]{64}$", DraftAliasContracts.Hash(dto));
    }

    [Theory]
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["I1"],"markdown":"x","sources":[]}],"extra":1}""")]
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["I1"],"markdown":"x","sources":[],"note":"x"}]}""")]
    [InlineData("""{"sectionKey":"documento","sectionKey":"altro","blocks":[{"items":["I1"],"markdown":"x","sources":[]}]}""")]
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["i1"],"markdown":"x","sources":[]}]}""")]                      // minuscolo
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["I01"],"markdown":"x","sources":[]}]}""")]                     // zero iniziale
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["I1"],"markdown":"x","sources":["S1"]}]}""")]                  // namespace sbagliato
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["b1000000-0000-4000-8000-000000000001"],"markdown":"x","sources":[]}]}""")]   // UUID al posto dell'alias
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":"I1","markdown":"x","sources":[]}]}""")]
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["I1"],"markdown":null,"sources":[]}]}""")]
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["I1"],"markdown":"x"}]}""")]
    [InlineData("""{"sectionKey":"documento","blocks":[{"itemIds":["I1"],"markdown":"x","sourceIds":[]}]}""")]                   // contratto v2
    [InlineData("""{"sectionKey":"documento","blocks":"I1"}""")]
    [InlineData("""{"sectionKey":"documento","blocks":[{"items":["I1"],"markdown":"x","sources":[]}""")]                         // troncato
    [InlineData("[]")]
    [InlineData("")]
    public void A_dto_that_is_not_the_closed_alias_object_is_rejected_by_the_strict_codec(string json) =>
        Assert.ThrowsAny<JsonException>(() => DraftAliasContracts.Decode(json));
}

/// <summary>
/// T127 (DET) — composizione server-side: alias risolti contro il catalogo, canonico ricostruito, marcatori riscritti in UUID e validatore invariato
/// sul risultato canonico. Il server non assegna mai item ai blocchi.
/// </summary>
public sealed class DraftAliasComposerTests
{
    private static readonly DocumentAnalysisContent Open = DraftAliasFixtures.Open();
    private static readonly DraftAliasCatalog OpenCatalog = DraftAliasCatalog.Build(Open);
    private static readonly DocumentDraftPackage OpenPackage = DocumentDraftPackage.From(Open);

    private static DraftComposition Compose(JsonObject dto, DocumentAnalysisContent? content = null, string? expectedCanonicalHash = null)
    {
        content ??= Open;
        return DraftAliasComposer.Compose(dto.ToJsonString(), DraftAliasCatalog.Build(content), DocumentDraftPackage.From(content), expectedCanonicalHash);
    }

    [Fact]
    public void A_valid_alias_output_is_composed_into_the_same_canonical_draft_as_the_v2_closed_output_and_markers_are_rewritten_to_uuids()
    {
        var composition = Compose(DraftAliasFixtures.Valid());
        var expected = DocumentDraftValidator.ValidateBlocks(DraftFixtures.Valid().ToJsonString(), OpenPackage);

        Assert.True(composition.Accepted, string.Join(" | ", composition.Violations));
        Assert.Null(composition.ErrorCode);
        Assert.True(expected.Accepted);
        Assert.Equal(expected.Markdown, composition.Validation!.Markdown);
        Assert.Equal([DraftFixtures.Alt1, DraftFixtures.Alt2], composition.Validation.Output!.Blocks[4].ItemIds);
        Assert.Equal([DraftFixtures.Msg, DraftFixtures.Pass], composition.Validation.Output.Blocks[4].SourceIds);
        Assert.Contains($"[fonte:{DraftFixtures.Msg:D}]", composition.Validation.Markdown);
        Assert.DoesNotContain("[fonte:F", composition.Validation.Markdown);
        Assert.Equal(OpenCatalog.CatalogHash, composition.CatalogHash);
        Assert.Matches("^[0-9a-f]{64}$", composition.AiOutputHash);
        Assert.Matches("^[0-9a-f]{64}$", composition.CanonicalHash);
    }

    [Fact]
    public void Composition_is_deterministic_and_an_expected_canonical_hash_that_differs_is_an_integrity_conflict()
    {
        var first = Compose(DraftAliasFixtures.Valid());
        var again = Compose(DraftAliasFixtures.Valid(), expectedCanonicalHash: first.CanonicalHash);
        var tampered = Compose(DraftAliasFixtures.Valid(), expectedCanonicalHash: new string('f', 64));

        Assert.Equal((first.AiOutputHash, first.CanonicalHash), (again.AiOutputHash, again.CanonicalHash));
        Assert.True(again.Accepted);
        Assert.False(tampered.Accepted);
        Assert.Equal(DocumentWorkException.IntegrityConflict, tampered.ErrorCode);
    }

    [Fact]
    public void The_server_does_not_assign_items_to_blocks_so_the_block_structure_is_exactly_the_one_of_the_model()
    {
        var dto = DraftAliasFixtures.Valid();
        dto["blocks"] = new JsonArray(
            DraftAliasFixtures.Block(["I1", "I2", "I3", "I4", "I5", "I6"], "Sintesi [fonte:F1] [fonte:F2]", ["F1", "F2"]));

        var composition = Compose(dto);

        Assert.True(composition.Accepted, string.Join(" | ", composition.Violations));
        var block = Assert.Single(composition.Validation!.Output!.Blocks);
        Assert.Equal(6, block.ItemIds.Count);
    }

    public static TheoryData<string, string> Rejected() => new()
    {
        { "unknown_item_alias", "alias" },
        { "unknown_source_alias", "alias" },
        { "duplicate_item_alias", "alias" },
        { "duplicate_source_alias", "alias" },
        { "marker_unknown_alias", "alias" },
        { "marker_with_uuid", "alias" },
        { "marker_with_two_aliases", "alias" },
        { "marker_with_space", "alias" },
        { "marker_lowercase_alias", "alias" },
        { "uuid_instead_of_item_alias", "struttura" },
        { "conflict_alias", "struttura" },
        { "item_not_represented", "itemIds" },
        { "source_not_of_the_items", "fonti" },
        { "declared_source_without_marker", "fonti" },
        { "marker_without_declared_source", "fonti" },
        { "hostile_html", "markdown" },
        { "item_with_sources_never_cited", "fonti" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void An_invalid_alias_output_is_rejected_permanently_with_a_specific_violation_and_exposes_nothing(string kind, string prefix)
    {
        var dto = DraftAliasFixtures.Valid();
        switch (kind)
        {
            case "unknown_item_alias":
                dto.BlockOf(2)["items"] = new JsonArray("I3", "I99");
                break;
            case "unknown_source_alias":
                dto.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F9]";
                dto.BlockOf(0)["sources"] = new JsonArray("F9");
                break;
            case "duplicate_item_alias":
                dto.BlockOf(2)["items"] = new JsonArray("I3", "I3");
                break;
            case "duplicate_source_alias":
                dto.BlockOf(0)["sources"] = new JsonArray("F1", "F1");
                break;
            case "marker_unknown_alias":
                dto.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F7]";
                break;
            case "marker_with_uuid":
                dto.BlockOf(0)["markdown"] = $"- Obiettivo [fonte:{DraftFixtures.Msg:D}]";
                break;
            case "marker_with_two_aliases":
                dto.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F1,F2]";
                break;
            case "marker_with_space":
                dto.BlockOf(0)["markdown"] = "- Obiettivo [fonte: F1]";
                break;
            case "marker_lowercase_alias":
                dto.BlockOf(0)["markdown"] = "- Obiettivo [fonte:f1]";
                break;
            case "uuid_instead_of_item_alias":
                dto.BlockOf(0)["items"] = new JsonArray(DraftFixtures.Goal.ToString("D"));
                break;
            case "conflict_alias":
                dto.BlockOf(4)["items"] = new JsonArray("C1", "I5", "I6");
                break;
            case "item_not_represented":
                dto.BlockOf(4)["items"] = new JsonArray("I5");
                dto.BlockOf(4)["markdown"] = "Conflitto aperto. Alternativa 1 [fonte:F1]";
                dto.BlockOf(4)["sources"] = new JsonArray("F1");
                break;
            case "source_not_of_the_items":
                dto.BlockOf(0)["markdown"] = "- Obiettivo [fonte:F2]";
                dto.BlockOf(0)["sources"] = new JsonArray("F2");
                break;
            case "declared_source_without_marker":
                dto.BlockOf(2)["sources"] = new JsonArray("F1");
                break;
            case "marker_without_declared_source":
                dto.BlockOf(0)["sources"] = new JsonArray();
                break;
            case "hostile_html":
                dto.BlockOf(2)["markdown"] = "- Ipotesi <script>alert(1)</script>";
                break;
            case "item_with_sources_never_cited":
                dto.BlockOf(0)["markdown"] = "- Obiettivo senza fonte";
                dto.BlockOf(0)["sources"] = new JsonArray();
                break;
            default:
                throw new InvalidOperationException(kind);
        }

        var composition = Compose(dto);

        Assert.False(composition.Accepted);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composition.ErrorCode);
        Assert.Null(composition.Validation);
        Assert.Null(composition.CanonicalHash);
        Assert.Contains(composition.Violations, v => v.StartsWith(prefix, StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_of_the_resolution_only_and_the_conflict_are_not_citable_even_by_a_well_formed_alias()
    {
        var content = DraftAliasFixtures.Resolved();   // F3 non esiste: la fonte PmRevision non ha alias
        var dto = DraftAliasFixtures.Valid();
        dto.BlockOf(4)["markdown"] = "Conflitto risolto dal PM [fonte:F1] [fonte:F2] [fonte:F3]";
        dto.BlockOf(4)["sources"] = new JsonArray("F1", "F2", "F3");

        var composition = Compose(dto, content);

        Assert.False(composition.Accepted);
        Assert.Contains(composition.Violations, v => v.StartsWith("alias", StringComparison.Ordinal) && v.Contains("F3"));
    }

    [Fact]
    public void A_complete_draft_of_a_resolved_conflict_citing_only_alternatives_and_their_sources_is_accepted()
    {
        var dto = DraftAliasFixtures.Valid();
        dto.BlockOf(4)["markdown"] = "Conflitto risolto dal PM: scelta l'alternativa 1 [fonte:F1]. Alternativa 2 [fonte:F2].";

        var composition = Compose(dto, DraftAliasFixtures.Resolved());

        Assert.True(composition.Accepted, string.Join(" | ", composition.Violations));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{\"sectionKey\":\"documento\",\"blocks\":")]
    public void Text_that_is_not_the_alias_object_is_a_permanent_work_output_error_not_an_exception(string text)
    {
        var composition = DraftAliasComposer.Compose(text, OpenCatalog, OpenPackage);

        Assert.False(composition.Accepted);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composition.ErrorCode);
    }

    [Fact]
    public void The_unchanged_validator_still_runs_on_the_canonical_result_so_a_fenced_json_or_prose_is_not_accepted()
    {
        var fenced = DraftAliasComposer.Compose("```json\n" + DraftAliasFixtures.Valid().ToJsonString() + "\n```", OpenCatalog, OpenPackage);

        Assert.False(fenced.Accepted);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, fenced.ErrorCode);
    }
}

/// <summary>T127 (DET) — prompt v3 della bozza, versioni e congelamento delle nuove richieste; il prompt v2 resta storico e invariato.</summary>
public sealed class DraftAliasPromptAndVersionTests
{
    private static string V3 => DocumentSourceDraftWorkflow.InstructionsV3;

    [Fact]
    public void The_v3_prompt_speaks_only_of_aliases_and_never_asks_the_model_to_reproduce_identifiers()
    {
        Assert.DoesNotContain("UUID", V3, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("itemIds", V3);
        Assert.DoesNotContain("sourceIds", V3);
        Assert.Contains("`I<n>`", V3);
        Assert.Contains("`F<n>`", V3);
        Assert.Contains("[fonte:F<n>]", V3);
        Assert.Contains("\"items\"", V3);
        Assert.Contains("\"sources\"", V3);
        Assert.Contains("mai l'alias di un conflitto", V3);
        Assert.Contains("fonti della risoluzione", V3);
    }

    [Fact]
    public void The_v3_prompt_keeps_every_obligation_of_the_v2_prompt_and_forbids_repeating_content()
    {
        foreach (var obligation in new[]
        {
            "Non fondere voci diverse in una sola", "non sostituire un elemento con una sintesi", "Da definire:", "Conflitto aperto", "Alternativa 1",
            "non sceglierne ne suggerirne una", "una voce per ogni elemento escluso", "mai che il documento e approvato", "niente HTML", "niente recinti di codice",
            "Non menzionare l'esistenza",
        })
        {
            Assert.Contains(obligation, V3.Replace("\r\n", "\n"), StringComparison.Ordinal);
        }

        Assert.Matches(new Regex(@"non ripetere[^.]*(stesso contenuto|stessi contenuti)[^.]*(voci|sezioni)", RegexOptions.IgnoreCase), V3);
        Assert.Contains("se non richiesto", V3);
    }

    [Fact]
    public void The_v2_prompt_is_historical_and_unchanged_and_the_two_prompts_are_distinguishable()
    {
        Assert.Contains("mai l'id di un conflitto", DocumentSourceDraftWorkflow.Instructions);
        Assert.Contains("`itemIds`", DocumentSourceDraftWorkflow.Instructions);
        Assert.DoesNotContain(DocumentSourceDraftWorkflow.Instructions.Replace("\r\n", "\n"), V3.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain(V3.Replace("\r\n", "\n"), DocumentSourceDraftWorkflow.Instructions.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void New_generation_requests_freeze_the_v3_definition_and_all_versions_while_the_v2_constants_stay_historical()
    {
        Assert.Equal("project-document-documents-v3", SourceSnapshotStore.DocumentDefinition);
        Assert.Equal("document-draft-v3", SourceSnapshotStore.DocumentPromptVersion);
        Assert.Equal("project-document-documents-v2", DocumentSourceDraftWorkflow.DefinitionVersion);
        Assert.Equal("document-documents-v2", DocumentSourceDraftWorkflow.PromptVersion);

        var prompts = JsonDocument.Parse(SourceSnapshotStore.DocumentPromptVersions()).RootElement;
        Assert.Equal("document-draft-v3", prompts.GetProperty("document").GetString());
        Assert.Equal("1.1.0", prompts.GetProperty("analysisSchema").GetString());
        Assert.Equal("document-draft-alias-1", prompts.GetProperty("draftAiSchema").GetString());
        Assert.Equal("draft-alias-catalog-1", prompts.GetProperty("draftCatalog").GetString());
        Assert.Equal("draft-single-v2", prompts.GetProperty("algorithm").GetString());
        Assert.Equal("document-draft-validation-v2", prompts.GetProperty("validation").GetString());
    }
}

/// <summary>
/// Modello finto della bozza v3 per i test di percorso (SmallFlow, Routing): legge dal messaggio a soli alias gli item `[I&lt;n&gt;]` e le fonti `F&lt;n&gt;` e produce
/// un unico blocco con tutti gli item e le loro fonti, nel DTO `document-draft-alias-1`.
/// </summary>
internal static class DraftAliasModelReplies
{
    private static readonly Regex Element = new(@"<<<(?:ELEMENTO|ALTERNATIVA) \[(I[1-9][0-9]*)\][^>\n]*fonti=([^>\n]*)>>>", RegexOptions.Compiled);

    public static string Reply(string prompt)
    {
        var elements = Element.Matches(prompt).Select(m => (Item: m.Groups[1].Value, Sources: m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))).ToList();
        var sources = elements.SelectMany(e => e.Sources).Distinct().ToList();
        return new JsonObject
        {
            ["sectionKey"] = "documento",
            ["blocks"] = new JsonArray(DraftAliasFixtures.Block(
                [.. elements.Select(e => e.Item)], "- Requisiti confermati " + string.Join(' ', sources.Select(s => $"[fonte:{s}]")), [.. sources])),
        }.ToJsonString();
    }
}
