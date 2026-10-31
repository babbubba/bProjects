using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// T057/T061/T064/T066 — parti deterministiche del registro delle evidenze, senza database né modello: validazione dell'uscita
/// dell'estrattore (citazioni, offset UTF-16, ruoli, zero evidenze), schema per lavoro, vista della selezione, confronto fra
/// versioni e verifica di copertura. Gli esempi usano domini inventati: nessuna regola dipende dai casi N1–N5.
/// </summary>
public sealed class EvidenceLedgerUnitTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly ExtractionMessage Prior = new(Guid.NewGuid(), 2, MessageRole.User, "Il prestito dura al massimo 5 giorni.");
    private static readonly ExtractionMessage Assistant = new(Guid.NewGuid(), 3, MessageRole.Assistant, "Vuoi anche un avviso via SMS?");
    private static readonly ExtractionMessage Target = new(
        Guid.NewGuid(), 4, MessageRole.User, "Sì, ma niente SMS: solo avvisi in app. Intendevo 10 giorni, non 5. Il magazziniere registra i rientri con il lettore del tesserino.");

    private static ExtractionContext Context(params PriorEvidence[] priors) => new(Project, Guid.NewGuid(), Target, [Prior], Assistant, priors);

    private static ExtractedSource Src(string quote, EvidenceSourceRole role = EvidenceSourceRole.Primary, Guid? message = null, int occurrence = 1) =>
        new() { MessageId = message ?? Target.Id, Quote = quote, Occurrence = occurrence, Role = role };

    private static ExtractedEvidence Ev(
        string key, string quote, EvidenceKind kind = EvidenceKind.Requirement, EvidencePolarity polarity = EvidencePolarity.Affirmed,
        IReadOnlyList<ExtractedSource>? sources = null, IReadOnlyList<ExtractedRelation>? relations = null, string? instrument = null,
        string? action = null, IReadOnlyList<ExtractedQualifier>? qualifiers = null, string? kindRationale = null) => new()
    {
        Key = key,
        Sources = sources ?? [Src(quote)],
        Kind = kind,
        KindRationale = kindRationale,
        Statement = quote,
        Subject = null,
        Action = action,
        Object = null,
        Scope = null,
        Purpose = null,
        Instrument = instrument,
        Channel = null,
        Recipient = null,
        Qualifiers = qualifiers ?? [],
        Polarity = polarity,
        Certainty = EvidenceCertainty.Declared,
        Relations = relations ?? [],
    };

    private static ExtractionOutput Out(params ExtractedEvidence[] evidences) => new() { Evidences = evidences, NoEvidenceReason = null };

    // ---------------------------------------------------------------- validazione dell'estrazione

    [Fact]
    public void Several_facts_of_one_message_are_separate_evidences_with_exact_utf16_offsets()
    {
        var result = EvidenceExtractionValidator.Validate(Out(
            Ev("e1", "niente SMS", EvidenceKind.Constraint, EvidencePolarity.Prohibited),
            Ev("e2", "solo avvisi in app", EvidenceKind.Constraint),
            Ev("e3", "Il magazziniere registra i rientri con il lettore del tesserino", EvidenceKind.Action, instrument: "lettore del tesserino", action: "registra i rientri")),
            Context());

        Assert.True(result.Accepted, string.Join(" | ", result.Violations));
        Assert.Equal(3, result.Evidences.Count);
        foreach (var e in result.Evidences)
        {
            var span = Assert.Single(e.Content.Sources);
            Assert.Equal(span.Quote, Target.Body[span.Start..span.End]);
            Assert.Equal(EvidenceSourceRole.Primary, span.Role);
        }

        Assert.Equal("lettore del tesserino", result.Evidences[2].Content.Structure.Instrument);
        Assert.Equal([1, 2, 3], result.Evidences.Select(e => e.Ordinal));
    }

    [Fact]
    public void Zero_evidences_require_an_explicit_reason_and_are_distinct_from_a_failure()
    {
        var ok = EvidenceExtractionValidator.Validate(new ExtractionOutput { Evidences = [], NoEvidenceReason = "Solo un ringraziamento." }, Context());
        Assert.True(ok.Accepted);
        Assert.Empty(ok.Evidences);
        Assert.Equal("Solo un ringraziamento.", ok.NoEvidenceReason);

        var missing = EvidenceExtractionValidator.Validate(new ExtractionOutput { Evidences = [], NoEvidenceReason = "  " }, Context());
        Assert.False(missing.Accepted);
        Assert.Contains(missing.Violations, v => v.Contains("noEvidenceReason", StringComparison.Ordinal));

        var both = EvidenceExtractionValidator.Validate(new ExtractionOutput { Evidences = [Ev("e1", "niente SMS")], NoEvidenceReason = "x" }, Context());
        Assert.False(both.Accepted);
    }

    [Fact]
    public void An_invented_quote_is_rejected()
    {
        var result = EvidenceExtractionValidator.Validate(Out(Ev("e1", "niente notifiche push")), Context());
        Assert.False(result.Accepted);
        Assert.Contains(result.Violations, v => v.Contains("citazione non presente", StringComparison.Ordinal));
    }

    [Fact]
    public void Whitespace_case_and_typographic_quotes_are_tolerated_but_the_persisted_quote_is_the_original()
    {
        var context = new ExtractionContext(Project, Guid.NewGuid(), Target with { Body = "L’app   deve funzionare\nOFFLINE in magazzino." }, [], null, []);
        var result = EvidenceExtractionValidator.Validate(Out(Ev("e1", "l'app deve funzionare offline")), context);
        Assert.True(result.Accepted, string.Join(" | ", result.Violations));
        var span = result.Evidences[0].Content.Sources[0];
        Assert.Equal("L’app   deve funzionare\nOFFLINE", span.Quote);
        Assert.Equal(span.Quote, context.Target.Body[span.Start..span.End]);
    }

    [Fact]
    public void Offsets_are_utf16_code_units_also_after_characters_outside_the_bmp()
    {
        var context = new ExtractionContext(Project, Guid.NewGuid(), Target with { Body = "🚚 Consegna entro le 18 😀" }, [], null, []);
        var result = EvidenceExtractionValidator.Validate(Out(Ev("e1", "Consegna entro le 18 😀")), context);
        Assert.True(result.Accepted, string.Join(" | ", result.Violations));
        var span = result.Evidences[0].Content.Sources[0];
        Assert.Equal(3, span.Start); // il camion occupa due unità UTF-16, più lo spazio
        Assert.Equal(context.Target.Body.Length, span.End);
        Assert.Equal(span.Quote, context.Target.Body[span.Start..span.End]);
    }

    [Fact]
    public void Occurrence_selects_the_repeated_passage_and_cannot_exceed_the_occurrences()
    {
        var context = new ExtractionContext(Project, Guid.NewGuid(), Target with { Body = "Serve un report. Poi serve un report." }, [], null, []);
        var second = EvidenceExtractionValidator.Validate(Out(Ev("e1", "serve un report", sources: [Src("serve un report", occurrence: 1)])), context);
        Assert.True(second.Accepted);
        Assert.Equal(21, second.Evidences[0].Content.Sources[0].Start); // occorrenza esatta (minuscola), non quella normalizzata

        var tooMany = EvidenceExtractionValidator.Validate(Out(Ev("e1", "serve un report", sources: [Src("serve un report", occurrence: 3)])), context);
        Assert.False(tooMany.Accepted);
    }

    [Fact]
    public void The_assistant_is_never_a_primary_source_and_every_evidence_needs_a_primary_passage()
    {
        var assistantPrimary = EvidenceExtractionValidator.Validate(Out(Ev("e1", "avviso via SMS", sources: [Src("avviso via SMS", EvidenceSourceRole.Primary, Assistant.Id)])), Context());
        Assert.False(assistantPrimary.Accepted);

        var onlyReferent = EvidenceExtractionValidator.Validate(Out(Ev("e1", "avviso via SMS", sources: [Src("avviso via SMS", EvidenceSourceRole.Referent, Assistant.Id)])), Context());
        Assert.False(onlyReferent.Accepted);
        Assert.Contains(onlyReferent.Violations, v => v.Contains("manca una fonte Primary", StringComparison.Ordinal));

        var answered = EvidenceExtractionValidator.Validate(Out(Ev("e1", "Sì", sources:
            [Src("Sì"), Src("Vuoi anche un avviso via SMS?", EvidenceSourceRole.Referent, Assistant.Id)])), Context());
        Assert.True(answered.Accepted, string.Join(" | ", answered.Violations));

        var priorAsPrimary = EvidenceExtractionValidator.Validate(Out(Ev("e1", "al massimo 5 giorni", sources: [Src("al massimo 5 giorni", EvidenceSourceRole.Primary, Prior.Id)])), Context());
        Assert.False(priorAsPrimary.Accepted);
    }

    [Fact]
    public void Messages_not_provided_unknown_targets_and_duplicate_keys_are_rejected()
    {
        var foreign = EvidenceExtractionValidator.Validate(Out(Ev("e1", "x", sources: [Src("x", EvidenceSourceRole.Context, Guid.NewGuid())])), Context());
        Assert.False(foreign.Accepted);

        var target = EvidenceExtractionValidator.Validate(Out(Ev("e1", "niente SMS", relations:
            [new ExtractedRelation { Type = EvidenceRelationType.Corrects, Target = "P9", Basis = EvidenceRelationBasis.Declared, Rationale = null }])), Context());
        Assert.False(target.Accepted);

        var duplicate = EvidenceExtractionValidator.Validate(Out(Ev("e1", "niente SMS"), Ev("e1", "solo avvisi in app")), Context());
        Assert.False(duplicate.Accepted);

        var other = EvidenceExtractionValidator.Validate(Out(Ev("e1", "niente SMS", EvidenceKind.Other)), Context());
        Assert.False(other.Accepted);
        Assert.True(EvidenceExtractionValidator.Validate(Out(Ev("e1", "niente SMS", EvidenceKind.Other, kindRationale: "preferenza")), Context()).Accepted);
    }

    [Fact]
    public void A_declared_correction_points_to_the_prior_evidence_with_its_context_passage()
    {
        var prior = new PriorEvidence("P1", Guid.NewGuid(), Prior.Id, Prior.Sequence, EvidenceKind.Constraint, EvidencePolarity.Affirmed, "Il prestito dura al massimo 5 giorni");
        var result = EvidenceExtractionValidator.Validate(Out(Ev("e1", "Intendevo 10 giorni, non 5",
            EvidenceKind.Constraint,
            sources: [Src("Intendevo 10 giorni, non 5"), Src("Il prestito dura al massimo 5 giorni", EvidenceSourceRole.Context, Prior.Id)],
            relations: [new ExtractedRelation { Type = EvidenceRelationType.Corrects, Target = "P1", Basis = EvidenceRelationBasis.Declared, Rationale = "rettifica esplicita" }],
            qualifiers: [new ExtractedQualifier { Type = QualifierType.Maximum, Text = "10 giorni" }])), Context(prior));

        Assert.True(result.Accepted, string.Join(" | ", result.Violations));
        var relation = Assert.Single(result.Relations);
        Assert.Equal(prior, relation.ToPrior);
        Assert.Equal(EvidenceRelationBasis.Declared, relation.Basis);
        Assert.Equal(2, result.Evidences[0].Content.Sources.Count);
        Assert.Equal(EvidenceSourceRole.Context, result.Evidences[0].Content.Sources[1].Role);
    }

    [Fact]
    public void Strict_json_rejects_extra_properties_and_unknown_enums()
    {
        Assert.False(EvidenceExtractionValidator.ValidateText("""{"evidences":[],"noEvidenceReason":"x","extra":1}""", Context()).Accepted);
        Assert.False(EvidenceExtractionValidator.ValidateText("non è json", Context()).Accepted);
        var json = EvidenceJson.Serialize(Out(Ev("e1", "niente SMS"))).Replace("\"Requirement\"", "\"Wish\"", StringComparison.Ordinal);
        Assert.False(EvidenceExtractionValidator.ValidateText(json, Context()).Accepted);
    }

    [Fact]
    public void The_job_schema_admits_only_the_provided_messages_and_requires_a_source()
    {
        var schema = EvidenceExtractionSchema.ForJob(Context());
        var evidence = schema.GetProperty("properties").GetProperty("evidences").GetProperty("items");
        var sources = evidence.GetProperty("properties").GetProperty("sources");
        Assert.Equal(1, sources.GetProperty("minItems").GetInt32());
        var allowed = sources.GetProperty("items").GetProperty("properties").GetProperty("messageId").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { Target.Id.ToString(), Prior.Id.ToString(), Assistant.Id.ToString() }, allowed);
        var order = evidence.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(order.IndexOf("sources") < order.IndexOf("statement"), "le citazioni precedono l'interpretazione");
    }

    [Fact]
    public void The_extraction_prompt_contains_only_messages_up_to_the_extracted_one()
    {
        var text = EvidenceExtractor.BuildMessages(Context()).Single().Text;
        Assert.Contains(Target.Body, text, StringComparison.Ordinal);
        Assert.Contains("ruolo=assistant", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf(Prior.Body, StringComparison.Ordinal) < text.IndexOf(Target.Body, StringComparison.Ordinal));
        Assert.EndsWith("per il messaggio da estrarre.", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- confronto fra versioni

    private static EvidenceSnapshot Snap(Guid version, int ordinal, string quote, EvidenceKind kind = EvidenceKind.Requirement, string? statement = null, string body = "")
    {
        var source = string.IsNullOrEmpty(body) ? Target.Body : body;
        var start = source.IndexOf(quote, StringComparison.Ordinal);
        Assert.True(start >= 0, quote);
        return new(Guid.NewGuid(), version, Target.Id, Target.Sequence, ordinal, $"e{ordinal}", kind, EvidencePolarity.Affirmed, EvidenceCertainty.Declared,
            statement ?? quote,
            new EvidenceContent(null, new EvidenceStructure(null, null, null, null, null, null, null, null), [],
                [new EvidenceSpan(Target.Id, Target.Sequence, EvidenceSourceRole.Primary, start, start + quote.Length, quote)]));
    }

    [Fact]
    public void Versions_are_matched_by_overlapping_citations_and_changes_are_listed_field_by_field()
    {
        var (v1, v2) = (Guid.NewGuid(), Guid.NewGuid());
        var a1 = Snap(v1, 1, "niente SMS");
        var a2 = Snap(v1, 2, "solo avvisi in app");
        var a3 = Snap(v1, 3, "Intendevo 10 giorni, non 5");
        var b1 = Snap(v2, 1, "niente SMS");
        var b2 = Snap(v2, 2, "solo avvisi in app", EvidenceKind.Constraint);
        var b3 = Snap(v2, 3, "Il magazziniere registra i rientri con il lettore del tesserino");

        var diff = EvidenceVersionComparer.Compare([a1, a2, a3], [b1, b2, b3]);

        Assert.Equal(DiffStatus.Unchanged, diff.Entries.Single(e => e.From?.Id == a1.Id).Status);
        var modified = diff.Entries.Single(e => e.From?.Id == a2.Id);
        Assert.Equal(DiffStatus.Modified, modified.Status);
        Assert.Equal(b2.Id, modified.To!.Id);
        Assert.Equal("kind", Assert.Single(modified.Changes).Field);
        Assert.Equal(DiffStatus.Removed, diff.Entries.Single(e => e.From?.Id == a3.Id).Status);
        Assert.Equal(DiffStatus.Added, diff.Entries.Single(e => e.To?.Id == b3.Id).Status);
    }

    [Fact]
    public void Common_words_do_not_make_two_evidences_the_same_and_ties_are_ambiguous()
    {
        const string body = "Il socio prenota la sala. Il socio prenota la sala.";
        var (v1, v2) = (Guid.NewGuid(), Guid.NewGuid());
        var a = Snap(v1, 1, "Il socio prenota la sala.", body: body);
        // Stesso enunciato, citazione diversa (seconda frase): nessuna sovrapposizione, quindi nessuna identità.
        var second = body.LastIndexOf("Il socio", StringComparison.Ordinal);
        var b = a with
        {
            Id = Guid.NewGuid(), VersionId = v2,
            Content = a.Content with { Sources = [a.Content.Sources[0] with { Start = second, End = second + 25 }] },
        };
        var diff = EvidenceVersionComparer.Compare([a], [b]);
        Assert.Equal(DiffStatus.Removed, diff.Entries.Single(e => e.From is not null).Status);
        Assert.Equal(DiffStatus.Added, diff.Entries.Single(e => e.To is not null).Status);

        // Due candidate con la stessa sovrapposizione e lo stesso tipo: ambiguo, nessuna scelta silenziosa.
        var c1 = a with { Id = Guid.NewGuid(), VersionId = v2 };
        var c2 = a with { Id = Guid.NewGuid(), VersionId = v2, Ordinal = 2 };
        var ambiguous = EvidenceVersionComparer.Compare([a], [c1, c2]);
        var entry = ambiguous.Entries.Single(e => e.From is not null);
        Assert.Equal(DiffStatus.Ambiguous, entry.Status);
        Assert.Equal(2, entry.Candidates.Count);
        Assert.DoesNotContain(ambiguous.Entries, e => e.Status == DiffStatus.Added);
    }

    // ---------------------------------------------------------------- vista della selezione

    [Fact]
    public void The_selection_view_is_deterministic_keeps_open_conflicts_and_remaps_or_marks_stale_relations()
    {
        var messageA = Guid.NewGuid();
        var (vA, vB, vOld) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        EvidenceSnapshot Make(Guid version, Guid message, long seq, int ordinal, EvidenceKind kind, int start = 0, int end = 5) =>
            new(Guid.NewGuid(), version, message, seq, ordinal, $"e{ordinal}", kind, EvidencePolarity.Affirmed, EvidenceCertainty.Declared, $"s{seq}.{ordinal}",
                new EvidenceContent(null, new EvidenceStructure(null, null, null, null, null, null, null, null), [],
                    [new EvidenceSpan(message, seq, EvidenceSourceRole.Primary, start, end, "xxxxx")]));
        var a1 = Make(vA, messageA, 1, 1, EvidenceKind.Alternative);
        var b1 = Make(vB, Target.Id, 2, 1, EvidenceKind.Alternative);
        var b2 = Make(vB, Target.Id, 2, 2, EvidenceKind.Requirement, 10, 20);
        var old = a1 with { Id = Guid.NewGuid(), VersionId = vOld }; // stesso passo in una versione precedente di A
        var oldOther = Make(vOld, messageA, 1, 2, EvidenceKind.Requirement, 40, 50); // nessun corrispondente nella versione selezionata
        var items = new List<SelectionItem> { new(messageA, 1, vA, 2, 1, null), new(Target.Id, 2, vB, 1, 2, null) };
        var relations = new List<RelationRecord>
        {
            new(Guid.NewGuid(), vB, b1.Id, old.Id, vOld, messageA, EvidenceRelationType.AlternativeTo, EvidenceRelationBasis.Inferred, null),
            new(Guid.NewGuid(), vB, b2.Id, oldOther.Id, vOld, messageA, EvidenceRelationType.DependsOn, EvidenceRelationBasis.Inferred, null),
        };

        var view = EvidenceSelectionView.Build(items, [b2, b1, a1], relations, new Dictionary<Guid, IReadOnlyList<EvidenceSnapshot>> { [vOld] = [old, oldOther] });
        var again = EvidenceSelectionView.Build(items, [a1, b1, b2], relations, new Dictionary<Guid, IReadOnlyList<EvidenceSnapshot>> { [vOld] = [old, oldOther] });

        Assert.Equal(["E1", "E2", "E3"], view.Evidences.Select(e => e.Alias));
        Assert.Equal(a1.Id, view.Evidences[0].EvidenceId);
        var remapped = Assert.Single(view.Relations);
        Assert.True(remapped.Remapped);
        Assert.True(remapped.Open);
        Assert.Equal(("E2", "E1"), (remapped.FromAlias, remapped.ToAlias));
        Assert.Single(view.StaleRelations);
        Assert.Equal(view.Hash(), again.Hash());
        Assert.Contains("conflitto aperto", view.ToPromptText(), StringComparison.Ordinal);
        Assert.Equal(view.Hash(), EvidenceSelectionView.Deserialize(view.Serialize()).Hash());
    }

    // ---------------------------------------------------------------- mappa del consolidamento

    [Fact]
    public void Evidence_keys_are_split_from_the_analysis_json_before_validation()
    {
        var item = Guid.NewGuid();
        var json = $$"""{"requirements":[{"id":"{{item}}","text":"t","classification":"Declared","evidenceRefs":[],"evidenceKeys":["E1","E2"]}]}""";
        var (content, keys, violations) = AnalysisSchema.SplitEvidenceKeys(json, ["E1", "E2"]);
        Assert.DoesNotContain("evidenceKeys", content, StringComparison.Ordinal);
        Assert.Equal(["E1", "E2"], keys[item]);
        Assert.Empty(violations);

        var (_, _, invented) = AnalysisSchema.SplitEvidenceKeys(json, ["E1"]);
        Assert.Single(invented);
    }

    [Fact]
    public void The_run_schema_requires_evidence_keys_constrained_to_the_view_aliases()
    {
        var message = Guid.NewGuid();
        var view = new EvidenceSelectionView([new(message, 1, Guid.NewGuid(), 1, 1, null)],
            [new("E1", Guid.NewGuid(), message, 1, 1, EvidenceKind.Goal, EvidencePolarity.Affirmed, EvidenceCertainty.Declared, "x",
                new EvidenceContent(null, new EvidenceStructure(null, null, null, null, null, null, null, null), [], []))], [], []);
        var input = new AnalysisRunInput(Guid.NewGuid(), Guid.NewGuid(), 1, [new AnalysisDialogMessage(message, AnalysisMessageRole.User, 1, "x")], view);
        var schema = AnalysisSchema.ForRun(input).GetRawText();
        Assert.Contains("\"evidenceKeys\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"E1\"", schema, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(schema);
        Assert.DoesNotContain("evidenceKeys", doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    // ---------------------------------------------------------------- copertura

    private static (EvidenceSelectionView View, AnalysisContent Content, List<(Guid, Guid)> Links, Dictionary<Guid, string> Bodies) Coverage(
        string body, string quote, EvidencePolarity polarity, EvidenceKind kind, string itemText, string section, EvidenceStructure? structure = null,
        IReadOnlyList<EvidenceQualifier>? qualifiers = null, bool link = true, bool citeMessage = true)
    {
        var message = Guid.NewGuid();
        var start = body.IndexOf(quote, StringComparison.Ordinal);
        var evidence = new ViewEvidence("E1", Guid.NewGuid(), message, 1, 1, kind, polarity, EvidenceCertainty.Declared, quote,
            new EvidenceContent(null, structure ?? new EvidenceStructure(null, null, null, null, null, null, null, null), qualifiers ?? [],
                [new EvidenceSpan(message, 1, EvidenceSourceRole.Primary, start, start + quote.Length, quote)]));
        var view = new EvidenceSelectionView([new(message, 1, Guid.NewGuid(), 1, 1, null)], [evidence], [], []);
        var item = new AnalysisItem { Id = Guid.NewGuid(), Text = itemText, Classification = AnalysisClassification.Declared, EvidenceRefs = citeMessage ? [message] : [Guid.NewGuid()] };
        var content = Content(message, section, item);
        return (view, content, link ? [(item.Id, evidence.EvidenceId)] : [], new() { [message] = body });
    }

    private static AnalysisContent Content(Guid message, string section, AnalysisItem item, AnalysisConflict? conflict = null) => new()
    {
        SchemaVersion = AnalysisContent.CurrentSchemaVersion,
        AnalysisId = Guid.NewGuid(),
        ProjectId = Project,
        SourceCutoffSequence = 1,
        ValidationState = AnalysisContentState.Unvalidated,
        Sources = [new AnalysisSource { Id = message, Kind = AnalysisSourceKind.Message, MessageId = message, MessageRole = AnalysisMessageRole.User, RevisionId = null }],
        Goals = section == "goals" ? [item] : [],
        ContextAndScope = new AnalysisContextAndScope { Overview = [], Included = [], Excluded = section == "excluded" ? [item] : [] },
        Requirements = section == "requirements" ? [item] : [],
        OperationalFlows = [],
        Constraints = section == "constraints" ? [item] : [],
        Dependencies = [],
        Assumptions = [],
        OpenQuestions = [],
        Conflicts = conflict is null ? [] : [conflict],
    };

    private static IReadOnlyList<CoverageFinding> Check((EvidenceSelectionView View, AnalysisContent Content, List<(Guid, Guid)> Links, Dictionary<Guid, string> Bodies) c, string? draft = null) =>
        EvidenceCoverage.Check(c.View, c.Links, c.Content, c.Content, draft, c.Bodies);

    [Fact]
    public void Coverage_flags_unrepresented_evidence_and_wrong_sources_as_deterministic_errors()
    {
        var none = Check(Coverage("Serve un report mensile.", "Serve un report mensile", EvidencePolarity.Affirmed, EvidenceKind.Requirement, "Report mensile", "requirements", link: false));
        Assert.Contains(none, f => f is { Code: "evidence_unrepresented", Category: FindingCategory.DeterministicError });

        var wrong = Check(Coverage("Serve un report mensile.", "Serve un report mensile", EvidencePolarity.Affirmed, EvidenceKind.Requirement, "Serve un report mensile", "requirements", citeMessage: false));
        Assert.Contains(wrong, f => f.Code == "evidence_source_mismatch");

        var good = Check(Coverage("Serve un report mensile.", "Serve un report mensile", EvidencePolarity.Affirmed, EvidenceKind.Requirement, "Serve un report mensile", "requirements"));
        Assert.Empty(good);
    }

    [Fact]
    public void Coverage_flags_an_invalid_span()
    {
        var c = Coverage("Serve un report mensile.", "Serve un report mensile", EvidencePolarity.Affirmed, EvidenceKind.Requirement, "Serve un report mensile", "requirements");
        c.Bodies[c.View.Items[0].MessageId] = "Testo diverso, più lungo dell'originale citato.";
        Assert.Contains(Check(c), f => f is { Code: "evidence_span_invalid", Category: FindingCategory.DeterministicError });
    }

    [Fact]
    public void Coverage_estimates_lost_qualifiers_inverted_negations_and_detached_instruments()
    {
        var qualifier = Check(Coverage("Il prestito dura al massimo 10 giorni.", "al massimo 10 giorni", EvidencePolarity.Affirmed, EvidenceKind.Constraint,
            "Il prestito ha una durata massima", "constraints", qualifiers: [new(QualifierType.Maximum, "al massimo 10 giorni")]));
        Assert.Contains(qualifier, f => f is { Code: "qualifier_changed", Category: FindingCategory.SemanticRisk });
        Assert.DoesNotContain(Check(Coverage("Il prestito dura al massimo dieci giorni.", "al massimo dieci giorni", EvidencePolarity.Affirmed, EvidenceKind.Constraint,
            "Il prestito dura al massimo 10 giorni", "constraints", qualifiers: [new(QualifierType.Maximum, "al massimo dieci giorni")])), f => f.Code == "qualifier_changed");

        var negation = Check(Coverage("Niente SMS.", "Niente SMS", EvidencePolarity.Prohibited, EvidenceKind.Constraint, "Avvisi via SMS", "requirements"));
        Assert.Contains(negation, f => f.Code == "negation_inverted");
        Assert.DoesNotContain(Check(Coverage("Niente SMS.", "Niente SMS", EvidencePolarity.Prohibited, EvidenceKind.Constraint, "Gli avvisi non usano SMS", "requirements")),
            f => f.Code == "negation_inverted");

        var structure = new EvidenceStructure("Il magazziniere", "registra i rientri", null, null, null, "lettore del tesserino", null, null);
        var detached = Check(Coverage("Il magazziniere registra i rientri con il lettore del tesserino.", "registra i rientri con il lettore del tesserino",
            EvidencePolarity.Affirmed, EvidenceKind.Action, "Il magazziniere registra i rientri", "requirements", structure));
        Assert.Contains(detached, f => f is { Code: "instrument_detached", Category: FindingCategory.SemanticRisk });
        Assert.DoesNotContain(Check(Coverage("Il magazziniere registra i rientri con il lettore del tesserino.", "registra i rientri con il lettore del tesserino",
            EvidencePolarity.Affirmed, EvidenceKind.Action, "Il magazziniere registra i rientri con il lettore del tesserino", "requirements", structure)),
            f => f.Code == "instrument_detached");
    }

    [Fact]
    public void Coverage_flags_an_open_alternative_presented_as_a_choice()
    {
        var alternative = Check(Coverage("Il consiglio vuole la cauzione.", "Il consiglio vuole la cauzione", EvidencePolarity.Affirmed, EvidenceKind.Alternative,
            "Il prestito richiede una cauzione", "requirements"));
        Assert.Contains(alternative, f => f is { Code: "open_conflict_as_decision", Category: FindingCategory.DeterministicError });
    }

    [Fact]
    public void Coverage_separates_pm_removals_and_document_omissions()
    {
        var c = Coverage("Serve un report mensile delle consegne.", "Serve un report mensile delle consegne", EvidencePolarity.Affirmed, EvidenceKind.Requirement,
            "Serve un report mensile delle consegne", "requirements");
        var emptied = c.Content with { Requirements = [] };
        var removed = EvidenceCoverage.Check(c.View, c.Links, c.Content, emptied, null, c.Bodies);
        Assert.Contains(removed, f => f is { Code: "item_removed_by_pm", Category: FindingCategory.PmAssessment });

        var message = c.View.Items[0].MessageId;
        var missing = Check(c, "# Documento\n\nSi parla d'altro. [fonte:00000000-0000-0000-0000-000000000000]");
        Assert.Contains(missing, f => f is { Code: "revision_source_not_cited", Category: FindingCategory.DeterministicError });
        Assert.Contains(missing, f => f is { Code: "revision_item_missing_in_document", Category: FindingCategory.SemanticRisk });
        var present = Check(c, $"# Documento\n\nServe un report mensile delle consegne. [fonte:{message}]");
        Assert.DoesNotContain(present, f => f.Code is "revision_source_not_cited" or "revision_item_missing_in_document");
    }
}
