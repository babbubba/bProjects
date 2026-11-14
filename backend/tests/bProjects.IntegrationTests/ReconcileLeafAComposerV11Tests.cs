using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-021 (M2, T169) — il compositore v11 compone gli item della foglia A con <see cref="ReconcileLeafProjection"/> (<c>items</c> del modello diversi da <c>[]</c> ⇒ <c>StageShape</c>),
/// le altre foglie e gli altri stadi non proiettano nulla, la v10 resta quella di sempre; il messaggio <c>items-ref-1</c> mostra gli item già proposti senza testo mentre il formato storico resta byte per byte
/// invariato. Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcileLeafAComposerV11Tests
{
    private static ReconcileBatchWorld LeafAWorld(ContractProfile profile, DocumentAnalysisInput input, int[]? entries = null) =>
        ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafA, profile, input, entries ?? [.. Enumerable.Range(1, input.Evidence.Count)]);

    private static ReconcileAliasComposition Compose(ReconcileBatchWorld world, string json, ReconcileComposeOptions? options, string? expectedCanonicalHash = null) =>
        ReconcileAliasComposer.Compose(json, world.Catalog, world.Request, world.Input.Snapshot, world.Base, expectedCanonicalHash, options);

    private static ReconcileComposeOptions V11(DocumentAnalysisInput input) => new(ReconcileAliasContracts.StageContract, M2Ledger.ById(input));

    // ---------------------------------------------------------------- foglia A v11: item composti dal server

    [Fact]
    public void A_v11_leaf_a_composes_one_projected_item_per_entry_and_recomposition_verifies_the_canonical_hash()
    {
        var input = M2Ledger.Input();
        var world = LeafAWorld(ContractProfile.V11, input);
        var json = ReconcileContractMatrixTests.Dto(descriptor: "gruppo").ToJsonString();
        Assert.Empty(ReconcileSchemaSubset.Violations(world.Schema, json));                      // lo schema LeafA ammette items vuoto

        var composition = Compose(world, json, V11(input));

        Assert.True(composition.IsValid, string.Join(" | ", composition.Violations));
        Assert.Empty(composition.TypedViolations);
        Assert.Empty(composition.Ungrounded);                                                    // nessuna domanda aperta senza evidenze: le domande canoniche sono delle voci Gap
        var proposals = composition.Result!.ItemProposals;
        Assert.Equal(input.Evidence.Count, proposals.Count);
        var expected = ReconcileLeafProjection.Project(world.Request, M2Ledger.ById(input));
        Assert.Equal(ReconciliationCodec.Canonical(expected.Proposals), ReconciliationCodec.Canonical(proposals));
        Assert.Equal(proposals.Select(p => p.LocalKey), composition.Sections.Keys.OrderBy(k => proposals.ToList().FindIndex(p => p.LocalKey == k)));
        Assert.Null(composition.Result.NoFindingsReason);                                        // V2: nessuna spiegazione richiesta, le proposte ci sono

        // Replay: lo stesso DTO ricomposto con l'hash canonico committato è identico (nessuna dipendenza dall'ordine o dal momento).
        var again = Compose(world, json, V11(input), composition.CanonicalHash);
        Assert.True(again.IsValid, string.Join(" | ", again.Violations));
        Assert.Equal(composition.CanonicalHash, again.CanonicalHash);
        Assert.Equal(composition.AiOutputHash, again.AiOutputHash);
    }

    [Fact]
    public void A_v11_leaf_a_whose_model_output_carries_items_is_a_stage_shape_rejection_and_the_schema_does_not_admit_it_either()
    {
        var input = M2Ledger.Input();
        var world = LeafAWorld(ContractProfile.V11, input);
        var json = ReconcileContractMatrixTests.Dto(descriptor: "gruppo", change: o => o["items"] = new JsonArray(JsonNode.Parse(
            """{"key":"i1","section":"requirements","text":"testo del modello","classification":"Documented","evidence":["E3"]}""")!)).ToJsonString();

        var composition = Compose(world, json, V11(input));

        Assert.False(composition.IsValid);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, composition.ErrorCode);
        var violation = Assert.Single(composition.TypedViolations);
        Assert.Equal((ReconcileViolationKind.StageShape, "items"), (violation.Kind, violation.Path));
        Assert.Contains("items", violation.Message, StringComparison.Ordinal);
        Assert.Equal(composition.Violations, composition.TypedViolations.Select(v => v.Message));   // stesse stringhe, stesso ordine
        Assert.NotEmpty(ReconcileSchemaSubset.Violations(world.Schema, json));                       // grammatica: items.maxItems = 0 (difesa in profondità duplicata nel compositore)
    }

    [Fact]
    public void A_v11_leaf_a_without_the_evidence_of_the_ledger_is_a_configuration_error_and_never_a_silent_empty_projection()
    {
        var input = M2Ledger.Input();
        var world = LeafAWorld(ContractProfile.V11, input);
        var json = ReconcileContractMatrixTests.Dto(descriptor: "gruppo").ToJsonString();

        var error = Assert.Throws<ArgumentException>(() => Compose(world, json, new ReconcileComposeOptions(ReconcileAliasContracts.StageContract)));

        Assert.Contains("evidenze", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_other_stages_never_project_items_and_do_not_need_the_ledger_evidence()
    {
        var input = M2Ledger.Input();
        var bare = new ReconcileComposeOptions(ReconcileAliasContracts.StageContract);
        var leafB = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafB, ContractProfile.V11, input, [1, 2, 3]);
        var reduce = ReconcileBatchWorld.Create(ReconcileBatchSchema.Reduce, ContractProfile.V11, input);

        var b = Compose(leafB, ReconcileContractMatrixTests.Dto(noFindings: "nessun collegamento", descriptor: "gruppo").ToJsonString(), bare);
        var r = Compose(reduce, ReconcileContractMatrixTests.Dto(noFindings: "nessun collegamento", descriptor: "gruppo").ToJsonString(), bare);

        Assert.True(b.IsValid, string.Join(" | ", b.Violations));
        Assert.True(r.IsValid, string.Join(" | ", r.Violations));
        Assert.Empty(b.Result!.ItemProposals);
        Assert.Empty(r.Result!.ItemProposals);
        Assert.Empty(b.Sections);
    }

    [Fact]
    public void The_v10_leaf_a_is_unchanged_it_takes_the_items_from_the_model_with_its_own_keys_and_sections()
    {
        var input = ReconcileBatchWorld.DefaultInput();
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.LeafA, ContractProfile.V10, input, [1, 2, 3]);
        var json = ReconcileContractMatrixTests.Dto(descriptor: "gruppo", change: o => o["items"] = new JsonArray(
            JsonNode.Parse("""{"key":"i1","section":"goals","text":"testo del modello 1","classification":"Declared","evidence":["E1"]}""")!,
            JsonNode.Parse("""{"key":"i2","section":"requirements","text":"testo del modello 2","classification":"Documented","evidence":["E2","E3"]}""")!)).ToJsonString();

        var composition = Compose(world, json, null);

        Assert.True(composition.IsValid, string.Join(" | ", composition.Violations));
        Assert.Equal(["testo del modello 1", "testo del modello 2"], composition.Result!.ItemProposals.Select(p => p.Text));
        Assert.Equal(["leaf-A0.i1", "leaf-A0.i2"], composition.Result.ItemProposals.Select(p => p.LocalKey));
        Assert.Equal([AiSection.Goals, AiSection.Requirements], composition.Result.ItemProposals.Select(p => composition.Sections[p.LocalKey]));
    }

    // ---------------------------------------------------------------- messaggio items-ref-1

    private static (ReconcileBatchWorld World, IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> Evidence) CompareWorld()
    {
        var input = ReconcileBatchWorld.DefaultInput();
        var world = ReconcileBatchWorld.Create(ReconcileBatchSchema.Compare, ContractProfile.V11, input, focus: ReconcileBatchWorld.PairFocus(input, 3, 5, ReconcileProposedType.SameRule));
        return (world, M2Ledger.ById(input));
    }

    [Fact]
    public void The_legacy_message_format_is_unchanged_byte_for_byte_and_is_the_default()
    {
        var (world, evidence) = CompareWorld();

        var byDefault = ReconcileAliasMessages.Build(world.RunCatalog, world.Catalog, world.Request, evidence).Single().Text;
        var legacy = ReconcileAliasMessages.Build(world.RunCatalog, world.Catalog, world.Request, evidence, ReconcileMessageFormat.Legacy).Single().Text;

        Assert.Equal(byDefault, legacy);
        Assert.Contains("Item già proposti per le evidenze di questo batch:\n", legacy, StringComparison.Ordinal);
        Assert.Contains("[i2] (E3) item 2\n", legacy, StringComparison.Ordinal);                  // handle, evidenze citate e TESTO dell'item
        Assert.Matches(new Regex(@"^\[i\d+\] \(E\d+(, E\d+)*\) .+$", RegexOptions.Multiline), legacy);
    }

    [Fact]
    public void The_items_ref_1_format_shows_the_already_proposed_items_without_their_text_and_changes_nothing_else()
    {
        var (world, evidence) = CompareWorld();

        var legacy = ReconcileAliasMessages.Build(world.RunCatalog, world.Catalog, world.Request, evidence, ReconcileMessageFormat.Legacy).Single().Text;
        var refs = ReconcileAliasMessages.Build(world.RunCatalog, world.Catalog, world.Request, evidence, ReconcileMessageFormat.ItemsRef1).Single().Text;

        Assert.Contains("Item già proposti per le evidenze di questo batch:\n", refs, StringComparison.Ordinal);   // stessa intestazione
        Assert.Contains("[i2] (E3)\n", refs, StringComparison.Ordinal);
        Assert.DoesNotContain("item 2", refs, StringComparison.Ordinal);                                             // niente testo dell'item
        Assert.DoesNotMatch(new Regex(@"^\[i\d+\] \(E\d+(, E\d+)*\) .+$", RegexOptions.Multiline), refs);
        // L'unica differenza è il testo in coda alle righe degli item: tolto quello, i due messaggi coincidono riga per riga.
        var stripped = Regex.Replace(legacy, @"^(\[i\d+\] \(E\d+(?:, E\d+)*\)) .+$", "$1", RegexOptions.Multiline);
        Assert.Equal(stripped, refs);
        Assert.True(refs.Length < legacy.Length);
    }

    [Fact]
    public void A_batch_without_already_proposed_items_has_the_same_message_in_both_formats()
    {
        var input = M2Ledger.Input();
        var world = LeafAWorld(ContractProfile.V11, input);
        var evidence = M2Ledger.ById(input);

        var legacy = ReconcileAliasMessages.Build(world.RunCatalog, world.Catalog, world.Request, evidence, ReconcileMessageFormat.Legacy).Single().Text;
        var refs = ReconcileAliasMessages.Build(world.RunCatalog, world.Catalog, world.Request, evidence, ReconcileMessageFormat.ItemsRef1).Single().Text;

        Assert.Equal(legacy, refs);
        Assert.DoesNotContain("Item già proposti", refs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_v11_run_the_expansion_and_comparison_messages_carry_the_projected_items_by_handle_and_evidence_only()
    {
        var input = M2Ledger.Input();
        var focus = M2Ledger.Pair(input, M2Ledger.LimitFive, M2Ledger.LimitEight);
        var rig = await V11Rig.CreateAsync(input, (_, _) => V11Rig.CompareOpen("5 ordini contro 8 ordini: alternative integrali da sottoporre al PM"), TestContext.Current.CancellationToken);
        rig.Engine.Add(focus, ReconcileView.A);
        await rig.Engine.ProcessAsync(focus.CandidateId, TestContext.Current.CancellationToken);

        var compare = rig.Calls.Single(c => c.Batch.Stage == "compare");
        var catalog = ReconcileAliasCatalog.Build(rig.RunCatalog, compare, rig.Items);
        var message = ReconcileAliasMessages.Build(rig.RunCatalog, catalog, compare, rig.Evidence, ReconcileMessageFormat.ItemsRef1).Single().Text;

        Assert.Equal(2, catalog.Data.Items.Count);                                             // un item proiettato per ciascuna delle due evidenze del confronto
        foreach (var handle in catalog.Data.Items.Select(i => i.Handle))
        {
            Assert.Matches(new Regex($@"^\[{handle}\] \(E[34]\)$", RegexOptions.Multiline), message);
        }

        // Il testo dell'item è l'enunciato, già presente nella voce: nel messaggio compare UNA volta (nella voce), non due.
        foreach (var number in new[] { M2Ledger.LimitFive, M2Ledger.LimitEight })
        {
            // (la citazione della fonte contiene l'enunciato: si conta l'enunciato come corpo della voce, cioè subito prima della prima FONTE)
            Assert.Equal(1, Regex.Count(message, Regex.Escape(input.Evidence[number - 1].Statement + "\n<<<FONTE")));
            Assert.DoesNotContain("] " + input.Evidence[number - 1].Statement, message.Split("Item già proposti")[^1], StringComparison.Ordinal);   // nessun testo nelle righe degli item
        }
    }
}
