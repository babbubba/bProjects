using System.Text.Json;
using bProjects.App.Conversations;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T035 (DET) — validazione di quote, alias e sources dell'output evidence-1.1.0 (contracts/ai-workflow.md §2.1/§2.3): offset UTF-16
/// [start,end) calcolati dal server sul CanonicalText immutabile, Primary posseduta, gruppi/parti ordinati, header condivisi risolti
/// sul blocco originale, nessuna riparazione dell'output. Nessun database, nessun LLM.
/// </summary>
public sealed class DocumentEvidenceValidationTests
{
    private static readonly Guid Project = Guid.Parse("a0000000-0000-4000-8000-0000000000a1");
    private static readonly Guid Document = Guid.Parse("a0000000-0000-4000-8000-0000000000d1");
    private static readonly Guid DocVersion = Guid.Parse("a0000000-0000-4000-8000-0000000000e1");
    private static readonly Guid Preparation = Guid.Parse("a0000000-0000-4000-8000-0000000000f1");
    private static readonly Guid Artifact = Guid.Parse("a0000000-0000-4000-8000-0000000000b1");
    private static readonly Guid HeaderBlock = Guid.Parse("b0000000-0000-4000-8000-000000000010");
    private static readonly Guid RowBlock = Guid.Parse("b0000000-0000-4000-8000-000000000025");
    private static readonly Guid EmojiBlock = Guid.Parse("b0000000-0000-4000-8000-000000000030");
    private static readonly Guid VendorBlock = Guid.Parse("b0000000-0000-4000-8000-000000000040");
    private static readonly Guid PmMessage = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    private static readonly Guid OwnedMessage = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    private static readonly Guid AssistantMessage = Guid.Parse("c0000000-0000-4000-8000-000000000003");

    private const string HeaderText = "Massimo giornaliero";
    private const string RowText = "Cliente A | 5 ordini | Cliente B | 3 ordini";
    private const string EmojiText = "Prezzo 😀 €5 e 😀 €6";
    private const string VendorText = "Fornitore: Rossi SRL. Contatto: Rossi.";

    private static DocumentSourceRange Range(string key, Guid block, string text, int? start = null, int? end = null, bool owned = true, Guid? preparation = null) =>
        new(key, Document, DocVersion, preparation ?? Preparation, Artifact, block, text, start ?? 0, end ?? text.Length, owned);

    private static DocumentSourceMap Map(params DocumentSourceRange[] extra) => new(
    [
        Range("r10", HeaderBlock, HeaderText, owned: false),   // copia di contesto dell'intestazione: mai proprietaria
        Range("r25", RowBlock, RowText),
        Range("rEmoji", EmojiBlock, EmojiText),
        Range("rVendor", VendorBlock, VendorText, start: 0, end: 21),   // «Fornitore: Rossi SRL.»: il resto del blocco è fuori range
        .. extra,
    ]);

    private static DocumentExtractionContext Context(DocumentSourceMap? map = null, int unit = 3, params PriorEvidence[] priors) => new(
        Project, Preparation, unit, map ?? Map(),
        [
            new ExtractionMessage(PmMessage, 7, MessageRole.User, "Il limite è di 5 ordini al giorno."),
            new ExtractionMessage(OwnedMessage, 8, MessageRole.User, "Confermo il limite."),
            new ExtractionMessage(AssistantMessage, 6, MessageRole.Assistant, "Vuoi un limite giornaliero?"),
        ],
        new HashSet<Guid> { OwnedMessage },
        priors);

    private static DocumentPassageCandidateSource Passage(string range, string quote, EvidenceSourceRole role = EvidenceSourceRole.Primary, int occurrence = 1, string group = "g1", int part = 0) =>
        new() { RangeKey = range, Quote = quote, Occurrence = occurrence, Role = role, CitationGroupKey = group, Part = part };

    private static MessageCandidateSource Message(Guid id, string quote, EvidenceSourceRole role, string group = "m", int part = 0, int occurrence = 1) =>
        new() { MessageId = id, Quote = quote, Occurrence = occurrence, Role = role, CitationGroupKey = group, Part = part };

    private static DocumentExtractedEvidence Evidence(string key = "e1", params CandidateSource[] sources) => new()
    {
        Key = key,
        Sources = sources.Length == 0 ? [Passage("r25", "Cliente A | 5 ordini")] : sources,
        Kind = EvidenceKind.Constraint, KindRationale = null, Statement = "Massimo 5 ordini al giorno per il cliente A.",
        Subject = "Cliente A", Action = null, Object = "ordini", Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null,
        Qualifiers = [new ExtractedQualifier { Type = QualifierType.Maximum, Text = "5 al giorno" }],
        Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Relations = [],
    };

    private static DocumentExtractionOutput Output(params DocumentExtractedEvidence[] evidences) => new()
    {
        SchemaVersion = "evidence-1.1.0", Evidences = evidences.Length == 0 ? [Evidence()] : evidences, NoEvidenceReason = null,
    };

    private static DocumentValidationResult Validate(DocumentExtractionOutput output, DocumentExtractionContext? context = null) =>
        DocumentEvidenceValidator.Validate(output, context ?? Context());

    private static DocumentValidationResult Validate(DocumentExtractedEvidence evidence, DocumentExtractionContext? context = null) => Validate(Output(evidence), context);

    private static void AssertRejected(DocumentValidationResult result, string expectedFragment)
    {
        Assert.False(result.Accepted);
        Assert.True(result.Permanent);   // schema/fonti non validi sono permanenti: nessuna riparazione né retry dello stesso output
        Assert.Empty(result.Evidences);
        Assert.Empty(result.Relations);
        Assert.Contains(result.Violations, v => v.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Caso valido, identità e offset ----

    [Fact]
    public void A_valid_candidate_gets_server_offsets_ids_and_a_unit_scoped_global_key()
    {
        var result = Validate(Evidence());

        Assert.True(result.Accepted);
        var evidence = Assert.Single(result.Evidences);
        Assert.Equal(("e1", "3e1"), (evidence.Key, evidence.GlobalKey));   // chiave globale = unitOrdinal + chiave locale
        Assert.Equal(1, evidence.Ordinal);
        var source = Assert.Single(evidence.Content.Sources);
        Assert.Equal(EvidenceSourceKind.DocumentPassage, source.Kind);
        Assert.Equal((Document, DocVersion, Preparation, Artifact, RowBlock), (source.DocumentId!.Value, source.DocumentVersionId!.Value, source.PreparationId!.Value, source.ArtifactId!.Value, source.BlockId!.Value));
        Assert.Equal((0, 20, "Cliente A | 5 ordini"), (source.Start, source.End, source.Quote));
        Assert.Null(source.MessageId);
        Assert.Null(source.Sequence);
        Assert.NotEqual(Guid.Empty, source.SourceId);
        Assert.NotEqual(Guid.Empty, source.CitationGroupId);
        Assert.Equal("evidence-1.1.0", evidence.Content.SchemaVersion);
        Assert.Equal(EvidenceCertainty.Declared, evidence.Certainty);   // «asserito dalla fonte», non promosso né declassato
        // Il contenuto prodotto è esattamente il Content persistibile e rilegge identico.
        var reread = DocumentEvidenceCodec.DecodeContent(DocumentEvidenceCodec.EncodeContent(evidence.Content));
        Assert.Equal(evidence.Content.Sources, reread.Sources);
    }

    [Fact]
    public void Identifiers_are_deterministic_for_replay_and_distinct_across_ranges_and_groups()
    {
        var first = Validate(Evidence()).Evidences[0].Content.Sources[0];
        var replay = Validate(Evidence()).Evidences[0].Content.Sources[0];
        var other = Validate(Evidence("e1", Passage("r25", "3 ordini"))).Evidences[0].Content.Sources[0];

        Assert.Equal(first.SourceId, replay.SourceId);
        Assert.Equal(first.CitationGroupId, replay.CitationGroupId);
        Assert.NotEqual(first.SourceId, other.SourceId);
        Assert.Equal((first.Start, first.End), (0, 20));
        Assert.Equal((other.Start, other.End), (RowText.IndexOf("3 ordini", StringComparison.Ordinal), RowText.Length));
    }

    // ---- Quote ----

    [Theory]
    [InlineData("Cliente C | 9 ordini")]
    [InlineData("cliente a | 5 ordini")]
    [InlineData("Cliente A |  5 ordini")]
    [InlineData("Cliente A | 5 ordini.")]
    [InlineData("“Cliente A | 5 ordini”")]
    public void Invented_or_normalized_quotes_are_rejected_without_repair(string quote)
    {
        var result = Validate(Evidence("e1", Passage("r25", quote)));

        AssertRejected(result, "citazione");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_quotes_are_rejected(string quote)
    {
        AssertRejected(Validate(Evidence("e1", Passage("r25", quote))), "citazione vuota");
    }

    [Fact]
    public void The_quote_must_lie_inside_the_allowed_range_not_just_inside_the_block()
    {
        // «Contatto: Rossi.» è nel blocco ma fuori dal range [0,21) assegnato a rVendor.
        AssertRejected(Validate(Evidence("e1", Passage("rVendor", "Contatto: Rossi."))), "citazione");
        // Dentro il range è valida, e gli offset restano quelli del blocco.
        var inside = Validate(Evidence("e1", Passage("rVendor", "Rossi SRL")));
        Assert.True(inside.Accepted);
        Assert.Equal((VendorText.IndexOf("Rossi SRL", StringComparison.Ordinal), VendorText.IndexOf("Rossi SRL", StringComparison.Ordinal) + 9),
            (inside.Evidences[0].Content.Sources[0].Start, inside.Evidences[0].Content.Sources[0].End));
        // «Rossi» compare anche nel contatto fuori range: l'occorrenza si conta solo nel range assegnato.
        AssertRejected(Validate(Evidence("e1", Passage("rVendor", "Rossi", occurrence: 2))), "occurrence");
    }

    [Fact]
    public void Occurrence_selects_the_nth_match_inside_the_range()
    {
        var second = Validate(Evidence("e1", Passage("r25", "ordini", occurrence: 2)));
        var third = Validate(Evidence("e1", Passage("r25", "ordini", occurrence: 3)));

        Assert.True(second.Accepted);
        var source = second.Evidences[0].Content.Sources[0];
        Assert.Equal(RowText.LastIndexOf("ordini", StringComparison.Ordinal), source.Start);
        AssertRejected(third, "occurrence");
        AssertRejected(Validate(Evidence("e1", Passage("r25", "ordini", occurrence: 0))), "occurrence");
    }

    [Fact]
    public void Non_bmp_text_uses_utf16_offsets_and_never_splits_a_surrogate_pair()
    {
        var second = Validate(Evidence("e1", Passage("rEmoji", "😀 €6")));
        Assert.True(second.Accepted);
        var source = second.Evidences[0].Content.Sources[0];
        Assert.Equal(EmojiText.LastIndexOf("😀", StringComparison.Ordinal), source.Start);
        Assert.Equal(source.Start + "😀 €6".Length, source.End);   // l'emoji vale due unità UTF-16
        Assert.Equal("😀 €6", EmojiText[source.Start..source.End]);

        var occurrence = Validate(Evidence("e1", Passage("rEmoji", "😀", occurrence: 2)));
        Assert.Equal(EmojiText.LastIndexOf("😀", StringComparison.Ordinal), occurrence.Evidences[0].Content.Sources[0].Start);

        // Una citazione che inizia con il surrogato basso o finisce con quello alto non è testo valido: rifiutata, non troncata.
        AssertRejected(Validate(Evidence("e1", Passage("rEmoji", "\uDE00 €5"))), "surrogat");
        AssertRejected(Validate(Evidence("e1", Passage("rEmoji", "Prezzo \uD83D"))), "surrogat");
        // Un carattere di sostituzione non sostituisce l'emoji dell'originale.
        AssertRejected(Validate(Evidence("e1", Passage("rEmoji", "� €5"))), "citazione");
    }

    // ---- Intervalli e snapshot ----

    [Fact]
    public void Ranges_outside_the_unit_manifest_and_messages_outside_the_snapshot_are_rejected()
    {
        AssertRejected(Validate(Evidence("e1", Passage("r99", "Cliente A"))), "rangeKey");
        AssertRejected(Validate(Evidence("e1", [Passage("r25", "Cliente A | 5 ordini"), Message(Guid.NewGuid(), "ok", EvidenceSourceRole.Context)])), "non fornito");
    }

    [Fact]
    public void The_model_cannot_choose_the_primary_range_it_does_not_own()
    {
        AssertRejected(Validate(Evidence("e1", Passage("r10", HeaderText, EvidenceSourceRole.Primary))), "proprietar");
        // Solo contesto: nessuna Primary posseduta.
        AssertRejected(Validate(Evidence("e1", Passage("r10", HeaderText, EvidenceSourceRole.Context))), "primary");
    }

    [Fact]
    public void A_shared_header_resolves_to_the_original_block_and_does_not_make_a_new_evidence_by_itself()
    {
        var evidence = Evidence("e1", [
            Passage("r10", HeaderText, EvidenceSourceRole.Context, group: "g1", part: 0),
            Passage("r25", "Cliente A | 5 ordini", EvidenceSourceRole.Primary, group: "g1", part: 1),
        ]);

        var result = Validate(evidence);

        Assert.True(result.Accepted);
        var sources = result.Evidences[0].Content.Sources;
        Assert.Equal([HeaderBlock, RowBlock], sources.Select(s => s.BlockId!.Value));   // l'intestazione risolve al blocco b10, non alla copia nel prompt
        Assert.Equal([EvidenceSourceRole.Context, EvidenceSourceRole.Primary], sources.Select(s => s.Role));
        Assert.Equal([0, 1], sources.Select(s => s.Part));
        Assert.Single(sources.Select(s => s.CitationGroupId).Distinct());
        Assert.Equal((0, HeaderText.Length, HeaderText), (sources[0].Start, sources[0].End, sources[0].Quote));
        Assert.DoesNotContain(sources, s => s.Quote.Contains("Massimo giornaliero Cliente A", StringComparison.Ordinal));   // nessuna citazione concatenata

        // Lo stesso header in un'altra evidenza ha lo stesso sourceId ma un altro gruppo di citazione.
        var second = Evidence("e2", Passage("r10", HeaderText, EvidenceSourceRole.Context, group: "h", part: 0), Passage("r25", "3 ordini", group: "h", part: 1));
        var both = Validate(Output(evidence, second));
        Assert.True(both.Accepted);
        Assert.Equal(both.Evidences[0].Content.Sources[0].SourceId, both.Evidences[1].Content.Sources[0].SourceId);
        Assert.NotEqual(both.Evidences[0].Content.Sources[0].CitationGroupId, both.Evidences[1].Content.Sources[0].CitationGroupId);
    }

    [Fact]
    public void A_header_alone_as_context_of_another_evidence_is_not_an_owned_primary()
    {
        // L'unica Primary è sul range non posseduto: l'header replicato come contesto non genera un'evidenza.
        var evidence = Evidence("e1", [Passage("r10", HeaderText, EvidenceSourceRole.Primary, group: "g", part: 0)]);

        AssertRejected(Validate(evidence), "proprietar");
    }

    // ---- Messaggi ----

    [Fact]
    public void Message_sources_follow_the_001_roles_and_the_assistant_is_only_a_referent()
    {
        var referent = Validate(Evidence("e1", [
            Passage("r25", "Cliente A | 5 ordini"),
            Message(AssistantMessage, "limite giornaliero", EvidenceSourceRole.Referent, group: "a"),
            Message(PmMessage, "5 ordini al giorno", EvidenceSourceRole.Context, group: "p"),
        ]));
        Assert.True(referent.Accepted);
        var message = referent.Evidences[0].Content.Sources[1];
        Assert.Equal((EvidenceSourceKind.Message, AssistantMessage, 6L), (message.Kind, message.MessageId!.Value, message.Sequence!.Value));
        Assert.Null(message.DocumentId);
        Assert.Equal("limite giornaliero", message.Quote);

        AssertRejected(Validate(Evidence("e1", [Passage("r25", "Cliente A | 5 ordini"), Message(AssistantMessage, "limite", EvidenceSourceRole.Primary, group: "a")])), "assistant");
        AssertRejected(Validate(Evidence("e1", [Passage("r25", "Cliente A | 5 ordini"), Message(AssistantMessage, "limite", EvidenceSourceRole.Context, group: "a")])), "assistant");
        AssertRejected(Validate(Evidence("e1", [Passage("r25", "Cliente A | 5 ordini"), Message(PmMessage, "5 ordini", EvidenceSourceRole.Primary, group: "p")])), "proprietar");
        AssertRejected(Validate(Evidence("e1", [Passage("r25", "Cliente A | 5 ordini"), Message(PmMessage, "limite inesistente", EvidenceSourceRole.Context, group: "p")])), "citazione");
    }

    [Fact]
    public void An_owned_message_can_be_the_primary_of_a_message_only_evidence()
    {
        var result = Validate(Evidence("e1", Message(OwnedMessage, "Confermo il limite", EvidenceSourceRole.Primary, group: "p")));

        Assert.True(result.Accepted);
        Assert.Equal((EvidenceSourceKind.Message, 8L, 0, 18), (result.Evidences[0].Content.Sources[0].Kind, result.Evidences[0].Content.Sources[0].Sequence!.Value, result.Evidences[0].Content.Sources[0].Start, result.Evidences[0].Content.Sources[0].End));
    }

    // ---- Gruppi e parti ----

    [Fact]
    public void Citation_group_parts_must_be_unique_contiguous_from_zero_and_listed_in_order()
    {
        DocumentPassageCandidateSource At(string group, int part, string range = "r25", string quote = "Cliente A | 5 ordini") => Passage(range, quote, group: group, part: part);

        AssertRejected(Validate(Evidence("e1", [At("g", 0), At("g", 2, "rEmoji", "Prezzo")])), "contigu");                              // buco
        AssertRejected(Validate(Evidence("e1", [At("g", 1)])), "contigu");                                                              // non parte da zero
        AssertRejected(Validate(Evidence("e1", [At("g", 0), At("g", 0, "rEmoji", "Prezzo")])), "duplicat");                              // part duplicata
        AssertRejected(Validate(Evidence("e1", [At("g", 1, "rEmoji", "Prezzo"), At("g", 0)])), "ordin");                                 // elencate fuori ordine
        AssertRejected(Validate(Evidence("e1", [At("g", -1)])), "part");
        Assert.True(Validate(Evidence("e1", [At("g", 0), At("g", 1, "rEmoji", "Prezzo"), At("h", 0, "rVendor", "Rossi SRL")])).Accepted);   // gruppi indipendenti
        AssertRejected(Validate(Evidence("e1", [At("", 0)])), "citationGroupKey");
    }

    [Fact]
    public void A_group_cannot_mix_origins_or_repeat_the_same_range()
    {
        AssertRejected(Validate(Evidence("e1", [
            Passage("r25", "Cliente A | 5 ordini", group: "g", part: 0),
            Message(PmMessage, "5 ordini", EvidenceSourceRole.Context, group: "g", part: 1),
        ])), "gruppo");
        AssertRejected(Validate(Evidence("e1", [
            Passage("r25", "Cliente A | 5 ordini", group: "g1", part: 0),
            Passage("r25", "Cliente A | 5 ordini", group: "g2", part: 0),
        ])), "duplicat");
    }

    [Fact]
    public void Parts_of_a_group_from_different_document_versions_are_rejected()
    {
        var otherVersion = Guid.Parse("a0000000-0000-4000-8000-0000000000e2");
        var map = Map(new DocumentSourceRange("rV2", Document, otherVersion, Preparation, Artifact, VendorBlock, VendorText, 0, VendorText.Length, true));
        var evidence = Evidence("e1", [
            Passage("r25", "Cliente A | 5 ordini", group: "g", part: 0),
            Passage("rV2", "Fornitore", group: "g", part: 1),
        ]);

        AssertRejected(Validate(evidence, Context(map)), "versioni");
    }

    // ---- Relazioni e alias ----

    [Fact]
    public void Relations_target_a_key_of_the_same_unit_or_a_server_alias_and_never_themselves()
    {
        var prior = new PriorEvidence("P1", Guid.NewGuid(), PmMessage, 7, EvidenceKind.Requirement, EvidencePolarity.Affirmed, "Limite giornaliero.");
        DocumentExtractedEvidence With(string key, params ExtractedRelation[] relations) => Evidence(key) with { Relations = relations };
        ExtractedRelation Rel(string target, EvidenceRelationType type = EvidenceRelationType.Confirms) =>
            new() { Type = type, Target = target, Basis = EvidenceRelationBasis.Inferred, Rationale = null };

        var ok = Validate(Output(With("e1", Rel("e2"), Rel("P1", EvidenceRelationType.Qualifies)), With("e2")), Context(null, 3, prior));
        Assert.True(ok.Accepted);
        Assert.Equal(2, ok.Relations.Count);
        Assert.Equal(("e1", "e2", null), (ok.Relations[0].FromKey, ok.Relations[0].ToKey, ok.Relations[0].ToPrior?.Alias));
        Assert.Equal("P1", ok.Relations[1].ToPrior!.Alias);

        AssertRejected(Validate(Output(With("e1", Rel("e9"))), Context(null, 3, prior)), "inesistente");
        AssertRejected(Validate(Output(With("e1", Rel("P2"))), Context(null, 3, prior)), "inesistente");   // alias non fornito dal server
        AssertRejected(Validate(Output(With("e1", Rel("e1")))), "sé stessa");
        AssertRejected(Validate(Output(With("e1", Rel("e2"), Rel("e2")), With("e2"))), "duplicata");
        AssertRejected(Validate(Output(With("e1", Rel("e2", (EvidenceRelationType)99)), With("e2"))), "tipo");
    }

    // ---- Chiavi, campi, zero evidenze ----

    [Theory]
    [InlineData("E1")]
    [InlineData("e0")]
    [InlineData("e01")]
    [InlineData("1")]
    [InlineData("e1 ")]
    [InlineData("")]
    public void Keys_must_match_e_followed_by_a_positive_number(string key)
    {
        AssertRejected(Validate(Evidence(key)), "chiave");
    }

    [Fact]
    public void The_validator_flags_structurally_when_the_only_violations_are_other_without_rationale_and_lists_their_keys()
    {
        var other = Evidence("e1") with { Kind = EvidenceKind.Other };
        var other3 = Evidence("e3") with { Kind = EvidenceKind.Other, KindRationale = "  " };
        var result = Validate(Output(other, Evidence("e2"), other3));
        Assert.False(result.Accepted);
        Assert.Equal(["e1", "e3"], result.OtherWithoutRationaleKeys);

        // Mista: qualunque altra violazione (statement vuoto, citazione inventata, zero evidenze senza motivo...) annulla il segnale.
        Assert.Null(Validate(Output(other, Evidence("e2") with { Statement = " " })).OtherWithoutRationaleKeys);
        Assert.Null(Validate(Output(other, Evidence("e2", Passage("r25", "testo inesistente")))).OtherWithoutRationaleKeys);
        Assert.Null(Validate(Output(other with { Statement = "" })).OtherWithoutRationaleKeys);
        Assert.Null(Validate(Output(other, Evidence("e1"))).OtherWithoutRationaleKeys);   // chiave duplicata
        Assert.Null(Validate(Output(Evidence("e1") with { Statement = " " })).OtherWithoutRationaleKeys);
        Assert.Null(Validate(Output(Evidence("e1"))).OtherWithoutRationaleKeys);          // accettato
        Assert.Null(Validate(Output(Evidence("e1") with { Kind = EvidenceKind.Other, KindRationale = "Clausola di manleva." })).OtherWithoutRationaleKeys);
    }

    [Fact]
    public void Duplicate_keys_other_without_rationale_and_blank_statements_are_rejected()
    {
        AssertRejected(Validate(Output(Evidence("e1"), Evidence("e1"))), "duplicata");
        AssertRejected(Validate(Evidence() with { Kind = EvidenceKind.Other }), "kindRationale");
        Assert.True(Validate(Evidence() with { Kind = EvidenceKind.Other, KindRationale = "Clausola di manleva." }).Accepted);
        AssertRejected(Validate(Evidence() with { Statement = "   " }), "statement");
        AssertRejected(Validate(Evidence() with { Qualifiers = [new ExtractedQualifier { Type = QualifierType.Maximum, Text = " " }] }), "qualificatore");
        AssertRejected(Validate(Evidence() with { Kind = (EvidenceKind)99 }), "tipo");
    }

    [Fact]
    public void Zero_evidences_require_a_reason_and_a_reason_requires_zero_evidences()
    {
        var empty = new DocumentExtractionOutput { SchemaVersion = "evidence-1.1.0", Evidences = [], NoEvidenceReason = "Sommario senza requisiti." };

        var accepted = Validate(empty);
        Assert.True(accepted.Accepted);
        Assert.Empty(accepted.Evidences);
        Assert.Equal("Sommario senza requisiti.", accepted.NoEvidenceReason);
        AssertRejected(Validate(empty with { NoEvidenceReason = null }), "noEvidenceReason");
        AssertRejected(Validate(empty with { NoEvidenceReason = "  " }), "noEvidenceReason");
        AssertRejected(Validate(Output() with { NoEvidenceReason = "Motivo" }), "noEvidenceReason");
    }

    [Fact]
    public void Limits_of_the_001_validator_are_not_copied_as_silent_caps()
    {
        var many = Enumerable.Range(1, 150).Select(i => Evidence($"e{i}")).ToArray();
        var longStatement = Evidence() with { Statement = new string('x', 5_000), Subject = new string('s', 3_000) };

        Assert.True(Validate(Output(many)).Accepted);
        Assert.True(Validate(longStatement).Accepted);
    }

    // ---- Testo grezzo ----

    [Fact]
    public void Raw_text_invalid_json_and_truncated_output_are_permanent_failures_without_repair()
    {
        var valid = DocumentEvidenceCodec.EncodeOutput(Output());
        var context = Context();

        Assert.True(DocumentEvidenceValidator.ValidateText(valid, context).Accepted);
        var broken = DocumentEvidenceValidator.ValidateText(valid[..^7], context);
        AssertRejected(broken, "json");
        AssertRejected(DocumentEvidenceValidator.ValidateText("```json\n" + valid + "\n```", context), "json");
        AssertRejected(DocumentEvidenceValidator.ValidateText("{\"schemaVersion\":\"evidence-1.0.0\",\"evidences\":[],\"noEvidenceReason\":\"x\"}", context), "json");
        // Output arrestato dal limite di lunghezza: incompleto, mai accettato anche se il JSON risulta chiuso.
        AssertRejected(DocumentEvidenceValidator.ValidateText(valid, context, finishedByLength: true), "troncato");
        Assert.Equal(EvidenceExtractionValidator.InvalidAgentOutput, broken.Code);
    }

    // ---- Costruzione del manifest del server ----

    [Fact]
    public void The_source_map_rejects_inconsistent_ranges_and_foreign_owners()
    {
        Assert.Throws<ArgumentException>(() => new DocumentSourceMap([Range("a", RowBlock, RowText), Range("a", HeaderBlock, HeaderText)]));      // chiave duplicata
        Assert.Throws<ArgumentException>(() => new DocumentSourceMap([Range("a", RowBlock, RowText, start: 3, end: 3)]));                        // end > start
        Assert.Throws<ArgumentException>(() => new DocumentSourceMap([Range("a", RowBlock, RowText, start: -1, end: 4)]));                       // start >= 0
        Assert.Throws<ArgumentException>(() => new DocumentSourceMap([Range("a", RowBlock, RowText, start: 0, end: RowText.Length + 1)]));       // oltre il testo
        Assert.Throws<ArgumentException>(() => new DocumentSourceMap([Range("a", EmojiBlock, EmojiText, start: 0, end: 8)]));                    // 8 cade a metà dell'emoji (7 e 8 sono il surrogato alto e basso)
        Assert.Throws<ArgumentException>(() => new DocumentSourceMap([Range("a", EmojiBlock, EmojiText, start: 8, end: 12)]));                   // inizia sul surrogato basso
        Assert.Throws<ArgumentException>(() => Context(new DocumentSourceMap([Range("a", RowBlock, RowText, preparation: Guid.NewGuid())])));   // preparazione diversa dall'unità
    }
}
