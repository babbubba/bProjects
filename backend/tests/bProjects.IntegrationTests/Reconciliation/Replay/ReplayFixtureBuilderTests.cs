using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-006 (deterministici, nessun database, nessun tokenizer, nessuna inferenza): il costruttore trasforma ogni fixture di replay in
/// richiesta, catalogo del run, catalogo del batch, snapshot e base del passaggio con i tipi di produzione, e il messaggio alias del compositore di produzione
/// (<see cref="ReconcileAliasMessages.Build"/>) riproduce quello archiviato. Il replay e' per batch: nessuna gerarchia intera.
/// </summary>
public sealed class ReplayFixtureBuilderTests
{
    public static TheoryData<int, int> Cases() => ReplayFixtures.Cases();

    private static readonly string[] StageLabels = ["foglia", "riduzione", "cross", "espansione", "confronto"];

    /// <summary>Messaggio utente atteso, riscritto dai soli campi della fixture (specchio indipendente di <see cref="ReconcileAliasMessages.Build"/>, formato Legacy).</summary>
    private static string ExpectedUserText(ReplayFixture f)
    {
        var label = f.Stage switch { "leaf" => "foglia", "reduce" => "riduzione", "cross" => "cross", "expand" => "espansione", "compare" => "confronto", _ => throw new InvalidOperationException(f.Stage) };
        var text = new StringBuilder();
        text.Append("Tipo di batch: ").Append(label).Append('\n').Append("Vista: ").Append(f.View).Append('\n');
        if (f.Focus is { } focus)
        {
            var tail = f.Groups is null
                ? "Risolvilo con le voci integrali sotto."
                : "Ogni voce indica il gruppo di appartenenza (gruppo=...): cerca collegamenti fra una voce di un gruppo e una dell'altro.";
            text.Append($"Candidato in esame: {focus.Type} fra [{focus.Left}] e [{focus.Right}] (motivo: {focus.Rationale}). {tail}\n");
        }

        foreach (var e in f.Entries)
        {
            text.Append(EntryHeader(f, e)).Append('\n').Append(e.Statement).Append('\n');
            foreach (var s in e.Sources)
            {
                text.Append($"<<<FONTE [{s.Alias}] ruolo={s.Role}>>>\n").Append(s.Quote).Append("\n<<<FINE FONTE>>>\n");
            }

            text.Append("<<<FINE VOCE>>>\n");
        }

        foreach (var n in f.Nodes)
        {
            text.Append($"<<<NODO [{n.Alias}] vista={n.View}>>>\n").Append(n.Descriptor).Append("\n<<<FINE NODO>>>\n");
        }

        if (f.Items.Count > 0)
        {
            text.Append("Item già proposti per le evidenze di questo batch:\n");
            foreach (var i in f.Items)
            {
                text.Append($"[{i.Handle}] ({string.Join(", ", i.Evidence)}) {i.Text}\n");
            }
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON `reconcile-alias-1`.");
        return text.ToString();
    }

    private static string EntryHeader(ReplayFixture f, ReplayEntry e) =>
        $"<<<VOCE [{e.Alias}] origine={e.Origin} tipo={e.Kind} polarita={e.Polarity}{(f.Groups is null ? string.Empty : " gruppo=" + f.Groups[e.Alias])}>>>";

    private static string[] VoceLines(string text) => [.. text.Split('\n').Where(l => l.StartsWith("<<<VOCE [E", StringComparison.Ordinal))];

    // ================================================================ messaggio

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_alias_message_has_the_same_entry_lines_in_the_same_order_as_the_original_request(int run, int call)
    {
        var f = ReplayFixtures.Load(run, call);

        var message = ReplayFixtureBuilder.Build(f).UserText();

        Assert.Equal(f.Entries.Select(e => EntryHeader(f, e)), VoceLines(message));
        Assert.Equal(f.Stage is "reduce" or "cross" ? 0 : f.Entries.Count, VoceLines(message).Length);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_message_of_the_production_composer_is_identical_to_the_message_of_the_archived_request(int run, int call)
    {
        var f = ReplayFixtures.Load(run, call);

        var message = ReplayFixtureBuilder.Build(f).UserText();

        Assert.Equal(ExpectedUserText(f), message);
        Assert.Equal(f.Stage, ReplayFixtureBuilder.Build(f).Request.Batch.Stage);
        Assert.Equal(f.Entries.Count, ReplayFixtureBuilder.Build(f).Request.Entries.Count);
    }

    [Fact]
    public void Run3_call025_message_is_equal_line_by_line_including_headers_and_there_are_no_prior_items()
    {
        var f = ReplayFixtures.Load(3, 25);
        Assert.Equal(("leaf", "B", 142), (f.Stage, f.View, f.Entries.Count));
        Assert.Empty(f.Items);

        var actual = ReplayFixtureBuilder.Build(f).UserText().Split('\n');
        var expected = ExpectedUserText(f).Split('\n');

        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == actual[i], $"riga {i + 1}: atteso «{expected[i]}», ottenuto «{actual[i]}».");
        }

        Assert.Equal("Tipo di batch: foglia", actual[0]);
        Assert.Equal("Vista: B", actual[1]);
        Assert.Equal(142, actual.Count(l => l.StartsWith("<<<VOCE [E", StringComparison.Ordinal)));
        Assert.DoesNotContain(actual, l => l.StartsWith("Item già proposti", StringComparison.Ordinal));
        Assert.Equal("Rispondi ora con l'unico oggetto JSON `reconcile-alias-1`.", actual[^1]);
    }

    [Fact]
    public void When_the_local_archive_is_present_the_message_and_the_system_prompt_equal_the_archived_request_byte_for_byte()
    {
        var root = Environment.GetEnvironmentVariable("BPROJECTS_REPLAY_ARCHIVE") ?? Path.Combine(Path.GetTempPath(), "bprojects-diag");
        Assert.SkipUnless(Directory.Exists(root), $"archivio locale dei Run 2-5 assente ({root}): verifica opzionale, impostare BPROJECTS_REPLAY_ARCHIVE.");

        foreach (var f in ReplayFixtures.All())
        {
            var path = Path.Combine(root, f.Session, $"{f.Call:D3}-request.json");
            Assert.True(File.Exists(path), $"{f.Name}: {path} assente");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(f.RequestSha256, ReplayFixtures.Sha256Hex(bytes));
            using var document = JsonDocument.Parse(bytes);
            var messages = document.RootElement.GetProperty("body").GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(2, messages.Length);
            Assert.Equal(f.System, messages[0].GetProperty("content").GetString());
            Assert.Equal(messages[1].GetProperty("content").GetString(), ReplayFixtureBuilder.Build(f).UserText());
        }
    }

    // ================================================================ identita' e catalogo

    [Fact]
    public void Identities_are_deterministic_from_run_and_alias_and_the_content_hash_is_the_statement_sha()
    {
        var f = ReplayFixtures.Load(4, 30);

        var first = ReplayFixtureBuilder.Build(f);
        var second = ReplayFixtureBuilder.Build(f);

        Assert.Equal(first.RunCatalog.CatalogHash, second.RunCatalog.CatalogHash);
        Assert.Equal(first.Catalog.CatalogHash, second.Catalog.CatalogHash);
        Assert.Equal(first.Input.InputHash, second.Input.InputHash);
        foreach (var e in f.Entries)
        {
            var known = first.RunCatalog.Evidence(e.Alias)!;
            Assert.Equal(ReplayFixtureBuilder.EvidenceId(4, e.Alias), known.EvidenceId);
            Assert.Equal(ReplayFixtures.Sha256Hex(e.Statement), known.ContentHash);
            Assert.Equal(ReplayFixtureBuilder.SourceId(4, e.Sources[0].Alias), first.RunCatalog.Source(e.Sources[0].Alias)!.SourceId);
            Assert.Equal(e.Origin == "Message" ? EvidenceSourceKind.Message : EvidenceSourceKind.DocumentPassage, first.RunCatalog.Source(e.Sources[0].Alias)!.Provenance);
        }

        // Stesso alias, run diverso: identita' diverse (nessuna collisione fra i Run).
        Assert.NotEqual(ReplayFixtureBuilder.EvidenceId(4, "E42"), ReplayFixtureBuilder.EvidenceId(5, "E42"));
        Assert.NotEqual(ReplayFixtureBuilder.SourceId(4, "S42"), ReplayFixtureBuilder.SourceId(5, "S42"));
        Assert.Equal(first.Snapshot.Evidence.Select(e => e.ContentHash), first.Input.Evidence.Select(e => e.ContentHash));
        Assert.Equal(f.Entries.Select(e => e.Alias).Order(), first.Request.Entries.Select(e => first.RunCatalog.EvidenceById(e.EvidenceId)!.Alias).Order());
        Assert.Equal(first.Catalog.Data.Entries.Select(e => e.Alias), f.Entries.Select(e => e.Alias));
    }

    [Fact]
    public void The_run_catalog_pads_the_aliases_outside_the_batch_so_that_the_archived_labels_are_kept()
    {
        var f = ReplayFixtures.Load(5, 24);   // E4 e S4: il ledger archiviato ha E1..E3 fuori dal batch

        var batch = ReplayFixtureBuilder.Build(f);

        Assert.Equal(4, batch.RunCatalog.Data.Evidence.Count);
        Assert.Equal(["E4"], batch.Catalog.Data.Entries.Select(e => e.Alias));
        Assert.Equal("S4", batch.RunCatalog.Evidence("E4")!.Sources.Single().SourceAlias);
        Assert.Equal("Il PM chiede se il termine di pagamento delle fatture sia di 30 o di 60 giorni.", batch.Evidence[ReplayFixtureBuilder.EvidenceId(5, "E4")].Statement);
        Assert.Contains("<<<VOCE [E4] origine=Message tipo=Gap polarita=Undefined>>>", batch.UserText());
        Assert.DoesNotContain("segnaposto", batch.UserText(), StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ candidato in esame, item e base

    [Fact]
    public void The_compare_batch_resolves_the_focus_on_the_batch_aliases_and_keeps_the_global_item_handles()
    {
        var f = ReplayFixtures.Load(5, 30);   // Contradicts fra [E165] e [E262]; gli handle degli item sono indici globali del run

        var batch = ReplayFixtureBuilder.Build(f);

        var focus = Assert.Single(batch.Request.Focus!);
        Assert.Equal((CandidateEndpointKind.Evidence, CandidateEndpointKind.Evidence), (focus.Left.Kind, focus.Right.Kind));
        Assert.Equal(("E165", "E262"), (batch.Catalog.AliasOf(focus.Left), batch.Catalog.AliasOf(focus.Right)));
        Assert.Equal(CandidateKind.Conflict, focus.Kind);
        Assert.Equal(ReconcileProposedType.Contradicts, focus.ProposedType);
        Assert.Equal(f.Focus!.Rationale, focus.Rationale);
        Assert.Equal(f.Items.Select(i => i.Handle), batch.Catalog.Data.Items.Select(i => i.Handle));
        Assert.Equal(f.Items.Select(i => i.Text), batch.Catalog.Data.Items.Select(i => batch.Catalog.ItemTexts[i.Handle]));
        Assert.True(batch.PriorItems.Count >= f.Items.Max(i => int.Parse(i.Handle[1..])));   // gli handle sono indici globali: il segnaposto non compare nel messaggio
        Assert.DoesNotContain("segnaposto", batch.UserText(), StringComparison.OrdinalIgnoreCase);

        // La base del passaggio e' costruita dagli item della fixture: un bersaglio per item, gli altri target non esistono.
        Assert.NotNull(batch.Base);
        Assert.Equal(f.Items.Select(i => $"Item:replay.{i.Handle}").Order(StringComparer.Ordinal), batch.Base!.TargetHashes.Keys.Order(StringComparer.Ordinal));
        Assert.Matches("^[0-9a-f]{64}$", batch.Base.BaseHash);
        Assert.Equal(batch.Base.BaseHash, ReplayFixtureBuilder.Build(f).Base!.BaseHash);
    }

    [Fact]
    public void The_expansion_batch_resolves_the_group_endpoints_as_sets_and_has_no_membership_in_the_historical_format()
    {
        var f = ReplayFixtures.Load(4, 36);   // SameRule fra [N1] e [N2] senza gruppo= (v9)

        var batch = ReplayFixtureBuilder.Build(f);

        var focus = Assert.Single(batch.Request.Focus!);
        Assert.Equal((CandidateEndpointKind.Set, CandidateEndpointKind.Set), (focus.Left.Kind, focus.Right.Kind));
        Assert.Equal(("N1", "N2"), (batch.Catalog.AliasOf(focus.Left), batch.Catalog.AliasOf(focus.Right)));
        Assert.Null(batch.Request.Groups);
        Assert.DoesNotContain(" gruppo=", batch.UserText(), StringComparison.Ordinal);
        Assert.Equal(f.Items.Count, batch.Catalog.Data.Items.Count);
        Assert.Equal(CandidateKind.Relation, focus.Kind);
    }

    [Fact]
    public void A_fixture_with_membership_composes_the_group_labels_of_the_v10_format()
    {
        // Nessun archivio ha la membership (v10 mai osservata nei Run 2-5): variante sintetica della 036, solo per esercitare il percorso del costruttore.
        var f = ReplayFixtures.Load(4, 36);
        var groups = f.Entries.Select((e, i) => (e.Alias, Tag: (i % 3) switch { 0 => "N1", 1 => "N2", _ => "N1,N2" })).ToDictionary(t => t.Alias, t => t.Tag);
        var membership = f with { Groups = groups };

        var batch = ReplayFixtureBuilder.Build(membership);
        var lines = VoceLines(batch.UserText());

        Assert.NotNull(batch.Request.Groups);
        Assert.Equal(f.Entries.Select(e => EntryHeader(membership, e)), lines);
        Assert.Contains("Ogni voce indica il gruppo di appartenenza (gruppo=...)", batch.UserText(), StringComparison.Ordinal);
        Assert.All(lines, l => Assert.Matches(@" gruppo=(N1|N2|N1,N2)>>>$", l));
        Assert.Equal(ExpectedUserText(membership), batch.UserText());
    }

    [Fact]
    public void The_archived_outputs_compose_with_the_production_composer_on_the_rebuilt_catalogs_except_the_two_it_rejects_itself()
    {
        // Caratterizzazione del solo compositore alias (non della validazione canonica, di RH-017): serve a provare che catalogo, snapshot e base ricostruiti sono utilizzabili.
        var rejected = new Dictionary<string, string>();
        foreach (var f in ReplayFixtures.All())
        {
            var composition = ReplayFixtureBuilder.Build(f).Compose();
            if (!composition.IsValid)
            {
                rejected[f.Name] = string.Join(" | ", composition.Violations);
            }
        }

        Assert.Equal(["run2-026", "run4-036"], rejected.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("items: ammessi solo nelle foglie della vista A", rejected["run2-026"], StringComparison.Ordinal);   // Run 2, foglia B con 139 item
        Assert.Contains("JSON non conforme a reconcile-alias-1", rejected["run4-036"], StringComparison.Ordinal);            // Run 4, espansione troncata (finish=length)
        Assert.True(ReplayFixtureBuilder.Build(ReplayFixtures.Load(5, 30)).Compose().IsValid);                               // Run 5, confronto con focus Open: il compositore lo accetta
    }

    // ================================================================ limiti dichiarati (STOP: nessun GUID reale)

    [Fact]
    public void A_fixture_that_needs_real_identities_is_declared_not_reconstructable_instead_of_being_approximated()
    {
        var f = ReplayFixtures.Load(4, 30);
        var withRelation = f with { Relations = [new ReplayRelation("R1", "E42", "Contradicts", "E254", "Declared")] };
        var twoSources = f with { Entries = [.. f.Entries.Take(1).Select(e => e with { Sources = [.. e.Sources, .. e.Sources] }), .. f.Entries.Skip(1)] };

        Assert.Contains("relazioni note", ReplayFixtureBuilder.NotReconstructableReason(withRelation));
        Assert.Contains("fonti", ReplayFixtureBuilder.NotReconstructableReason(twoSources));
        Assert.Throws<ReplayNotReconstructableException>(() => ReplayFixtureBuilder.Build(withRelation));
        Assert.All(ReplayFixtures.All(), fixture => Assert.Null(ReplayFixtureBuilder.NotReconstructableReason(fixture)));
    }
}
