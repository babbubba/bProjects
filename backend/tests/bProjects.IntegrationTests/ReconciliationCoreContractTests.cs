using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>Dati congelati condivisi dai test del nucleo `reconcile-1` (T120/T121): due evidenze documentali, una fonte ciascuna, nessun database.</summary>
internal static class ReconciliationFixtures
{
    public static readonly Guid Project = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    public static readonly Guid Run = Guid.Parse("a0000000-0000-4000-8000-000000000009");
    public static readonly Guid E1 = Guid.Parse("10000000-0000-4000-8000-000000000001");
    public static readonly Guid E2 = Guid.Parse("20000000-0000-4000-8000-000000000002");
    public static readonly Guid E3 = Guid.Parse("30000000-0000-4000-8000-000000000003");
    public static readonly Guid S1 = Guid.Parse("11000000-0000-4000-8000-000000000001");
    public static readonly Guid S2 = Guid.Parse("22000000-0000-4000-8000-000000000002");
    public static readonly Guid S3 = Guid.Parse("33000000-0000-4000-8000-000000000003");
    public static readonly Guid Item1 = Guid.Parse("40000000-0000-4000-8000-000000000001");
    public static readonly Guid Item2 = Guid.Parse("40000000-0000-4000-8000-000000000002");
    public static readonly string SourceHash = new('a', 64);
    public static readonly string BaseHash = new('b', 64);
    public static readonly string PolicyHash = new('c', 64);
    public static readonly string InputHash = new('d', 64);
    public static readonly string Hash1 = new('1', 64);
    public static readonly string Hash2 = new('2', 64);
    public const string Quote = "Cliente A | 5 ordini";

    public static PersistedSource Source(Guid id, int start = 10) => new()
    {
        SourceId = id, Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = 0,
        MessageId = null, Sequence = null, DocumentId = Guid.Parse("d0000000-0000-4000-8000-000000000001"),
        DocumentVersionId = Guid.Parse("d0000000-0000-4000-8000-000000000002"), PreparationId = Guid.Parse("d0000000-0000-4000-8000-000000000003"),
        ArtifactId = Guid.Parse("d0000000-0000-4000-8000-000000000004"), BlockId = Guid.Parse("d0000000-0000-4000-8000-00000000000" + (id == S1 ? "5" : id == S2 ? "6" : "7")),
        Start = start, End = start + Quote.Length, Quote = Quote,
    };

    public static ReconciliationSnapshot Snapshot(bool withThird = false)
    {
        var evidence = new List<SnapshotEvidence>
        {
            new(E1, Guid.Parse("50000000-0000-4000-8000-000000000001"), Hash1, [Source(S1)]),
            new(E2, Guid.Parse("50000000-0000-4000-8000-000000000002"), Hash2, [Source(S2)]),
        };
        if (withThird)
        {
            evidence.Add(new(E3, Guid.Parse("50000000-0000-4000-8000-000000000003"), new string('3', 64), [Source(S3)]));
        }

        return new(Project, SourceHash, evidence, []);
    }

    public static string EntryId(Guid evidenceId, string contentHash) => ReconciliationIds.EntryId(SourceHash, evidenceId, contentHash);

    public static IReadOnlyList<string> BothEntries(ReconciliationSnapshot s) =>
        [.. s.Evidence.Select(e => EntryId(e.EvidenceId, e.ContentHash)).OrderBy(x => x, StringComparer.Ordinal)];

    public static CandidateEndpoint Endpoint(Guid id, string hash) => new() { Kind = CandidateEndpointKind.Evidence, Id = id.ToString("D"), Hash = hash };

    public static ReconcileCandidate Candidate(CandidateKind kind = CandidateKind.Relation, ReconcileProposedType type = ReconcileProposedType.SameRule, bool swap = false)
    {
        var left = swap ? Endpoint(E2, Hash2) : Endpoint(E1, Hash1);
        var right = swap ? Endpoint(E1, Hash1) : Endpoint(E2, Hash2);
        return new()
        {
            CandidateId = ReconciliationIds.CandidateId(kind, left, right, type, SourceHash), Kind = kind, Left = left, Right = right, ProposedType = type,
            Rationale = "stessa regola con soglie diverse", Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = ["batch-1"], ParentCandidateId = null,
        };
    }

    public static Patch WithId(Patch patch) => patch with { PatchId = ReconciliationIds.PatchId(patch) };

    public static Patch AddSupport(Guid evidence = default, Guid source = default)
    {
        var e = evidence == default ? E1 : evidence;
        var s = source == default ? S1 : source;
        return WithId(new Patch
        {
            PatchId = "x", BaseHash = BaseHash, Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = Hash1 }, Operation = PatchOperation.AddSupport,
            Value = new AddSupportValue { EvidenceIds = [e], SourceIds = [s] }, EvidenceIds = [e], SourceIds = [s], OriginBatchIds = ["batch-1"],
        });
    }

    public static Patch AddRelation(EvidenceRelationType type = EvidenceRelationType.SameRule) => WithId(new Patch
    {
        PatchId = "x", BaseHash = BaseHash, Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = Hash1 }, Operation = PatchOperation.AddRelation,
        Value = new AddRelationValue { FromEvidenceId = E1, ToEvidenceId = E2, Type = type, Basis = EvidenceRelationBasis.Inferred, Rationale = "stessa regola" },
        EvidenceIds = [E1, E2], SourceIds = [S1, S2], OriginBatchIds = ["batch-1"],
    });

    public static Patch Alternative(string text = "limite 8 ordini") => WithId(new Patch
    {
        PatchId = "x", BaseHash = BaseHash, Target = new PatchTarget { Kind = PatchTargetKind.Item, Id = "r1", ExpectedHash = Hash1 }, Operation = PatchOperation.ProposeAlternative,
        Value = new ProposeAlternativeValue { Text = text, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [E2], SourceIds = [S2], Reason = "valore diverso nel secondo documento" },
        EvidenceIds = [E2], SourceIds = [S2], OriginBatchIds = ["batch-1"],
    });

    public static ReconcileResult Result(ReconcileView view, ReconciliationSnapshot snapshot, bool withItem = false) => new()
    {
        BatchId = "batch-1", InputHash = InputHash, View = view, Level = 0, VisitedEntryIds = BothEntries(snapshot), VisitedNodeIds = [],
        Candidates = [], Dispositions = [],
        ItemProposals = withItem
            ? [new ItemProposal { LocalKey = "r1", Text = "Massimo 5 ordini per cliente A", Classification = DocumentAnalysisClassification.Documented, EvidenceIds = [E1, E2], SourceIds = [S1, S2] }]
            : [],
        Patches = [], Descriptor = null, NoFindingsReason = withItem ? null : "nessun collegamento oltre quelli già noti",
    };

    public static DocumentAnalysisContent Analysis(ReconciliationSnapshot snapshot)
    {
        DocumentAnalysisSource FromSnapshot(PersistedSource p) => new()
        {
            Id = p.SourceId, Kind = DocumentAnalysisSourceKind.DocumentPassage, MessageId = null, MessageRole = null, RevisionId = null, DocumentId = p.DocumentId,
            DocumentVersionId = p.DocumentVersionId, PreparationId = p.PreparationId, ArtifactId = p.ArtifactId, BlockId = p.BlockId, Start = p.Start, End = p.End, Quote = p.Quote,
        };

        var sources = snapshot.Evidence.SelectMany(e => e.Sources).Select(FromSnapshot).ToList();
        var items = sources.Count == 0
            ? []
            : new List<DocumentAnalysisItem> { new() { Id = Item1, Text = "Massimo 5 ordini per cliente A", Classification = DocumentAnalysisClassification.Documented, EvidenceRefs = [.. sources.Select(s => s.Id)] } };
        return new()
        {
            SchemaVersion = DocumentAnalysisContent.SchemaVersionValue, AnalysisId = Guid.Parse("a0000000-0000-4000-8000-000000000002"), ProjectId = Project,
            SourceCutoffSequence = 0, ValidationState = AnalysisContentState.Unvalidated, Sources = sources, Goals = [],
            ContextAndScope = new DocumentAnalysisContextAndScope { Overview = [], Included = [], Excluded = [] },
            Requirements = items, OperationalFlows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [], Conflicts = [],
        };
    }

    public static SingleCallOutput Valid(ReconciliationSnapshot? snapshot = null)
    {
        snapshot ??= Snapshot();
        var hasEvidence = snapshot.Evidence.Count > 0;
        return new()
        {
            Results = [Result(ReconcileView.A, snapshot, hasEvidence), Result(ReconcileView.B, snapshot), Result(ReconcileView.Cross, snapshot)],
            Analysis = Analysis(snapshot),
            EvidenceMap = hasEvidence ? [new EvidenceMapEntry { ItemId = Item1, EvidenceIds = [.. snapshot.Evidence.Select(e => e.EvidenceId)] }] : [],
        };
    }

    public static JsonObject Node(SingleCallOutput output) => JsonNode.Parse(ReconciliationCodec.Encode(output))!.AsObject();
}

/// <summary>
/// T120 (DET) — nucleo condiviso `reconcile-1` (contracts/ai-workflow.md §4.1/§4.5): codec strict, identità derivate, regole strutturali e
/// risoluzione dei riferimenti nello snapshot congelato, con modelli finti (nessun LLM, nessun database).
/// </summary>
public sealed class ReconciliationCoreContractTests
{
    private static readonly ReconciliationSnapshot Snapshot = ReconciliationFixtures.Snapshot();

    private static IReadOnlyList<string> Violations(SingleCallOutput output, ReconciliationSnapshot? snapshot = null)
    {
        snapshot ??= Snapshot;
        return [.. ReconciliationContractRules.CheckSingleCall(output, snapshot.SourceHash), .. ReconciliationReferences.CheckSingleCall(output, snapshot)];
    }

    [Fact]
    public void Valid_single_call_round_trips_and_has_no_violations()
    {
        var json = ReconciliationCodec.Encode(ReconciliationFixtures.Valid());
        var decoded = ReconciliationCodec.DecodeSingleCall(json);

        Assert.Equal(json, ReconciliationCodec.Encode(decoded));
        Assert.Equal(3, decoded.Results.Count);
        Assert.Empty(Violations(decoded));
    }

    [Fact]
    public void Canonical_form_does_not_depend_on_property_order()
    {
        var node = ReconciliationFixtures.Node(ReconciliationFixtures.Valid());
        var reversed = new JsonObject();
        foreach (var property in node.ToList().AsEnumerable().Reverse())
        {
            node.Remove(property.Key);
            reversed[property.Key] = property.Value;
        }

        var a = ReconciliationCodec.Canonical(ReconciliationCodec.DecodeSingleCall(ReconciliationCodec.Encode(ReconciliationFixtures.Valid())));
        var b = ReconciliationCodec.Canonical(ReconciliationCodec.DecodeSingleCall(reversed.ToJsonString()));
        Assert.Equal(a, b);
        Assert.Equal(CanonicalJson.HashText(a), CanonicalJson.HashText(b));
    }

    public static TheoryData<string, Action<JsonObject>> StrictRejections => new()
    {
        { "extra root property", n => n["extra"] = 1 },
        { "missing root property", n => n.Remove("evidenceMap") },
        { "null non dichiarato", n => n["results"]![0]!["batchId"] = null },
        { "missing result property", n => n["results"]![0]!.AsObject().Remove("noFindingsReason") },
        { "extra result property", n => n["results"]![0]!["rank"] = 1 },
        { "enum wrong case", n => n["results"]![0]!["view"] = "a" },
        { "enum unknown", n => n["results"]![0]!["view"] = "D" },
        { "enum integer", n => n["results"]![0]!["view"] = 0 },
        { "uppercase UUID", n => n["results"]![0]!["itemProposals"]![0]!["evidenceIds"]![0] = "A0000000-0000-4000-8000-000000000001" },
        { "UUID without hyphens", n => n["results"]![0]!["itemProposals"]![0]!["evidenceIds"]![0] = ReconciliationFixtures.E1.ToString("N") },
        { "number as string", n => n["results"]![0]!["level"] = "0" },
        { "fractional level", n => n["results"]![0]!["level"] = 0.5 },
        { "array replaced by object", n => n["results"]![0]!["visitedEntryIds"] = new JsonObject() },
        { "null array", n => n["results"]![0]!["candidates"] = null },
    };

    [Theory]
    [MemberData(nameof(StrictRejections))]
    public void Strict_decoder_rejects(string name, Action<JsonObject> mutate)
    {
        var node = ReconciliationFixtures.Node(ReconciliationFixtures.Valid());
        mutate(node);

        var failure = Record.Exception(() => ReconciliationCodec.DecodeSingleCall(node.ToJsonString()));
        Assert.True(failure is JsonException, $"{name}: atteso JsonException, ottenuto {failure?.GetType().Name ?? "nessuna eccezione"}.");
    }

    [Fact]
    public void Strict_decoder_rejects_duplicate_properties_and_case_variants()
    {
        var json = ReconciliationCodec.Encode(ReconciliationFixtures.Valid());
        var duplicated = json.Replace("\"level\":0", "\"level\":0,\"level\":0", StringComparison.Ordinal);
        Assert.NotEqual(json, duplicated);
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.DecodeSingleCall(duplicated));

        var cased = json.Replace("\"evidenceMap\"", "\"EvidenceMap\"", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.DecodeSingleCall(cased));
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.DecodeSingleCall("null"));
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.DecodeSingleCall("{"));
    }

    [Fact]
    public void Patch_value_is_a_strict_union_discriminated_by_operation()
    {
        var all = new[] { ReconciliationFixtures.AddSupport(), ReconciliationFixtures.AddRelation(), ReconciliationFixtures.Alternative() };
        foreach (var patch in all)
        {
            var round = ReconciliationCodec.Decode<Patch>(ReconciliationCodec.Encode(patch));
            Assert.Equal(patch.Value.GetType(), round.Value.GetType());
            Assert.Equal(patch.PatchId, round.PatchId);
            Assert.Empty(ReconciliationContractRules.CheckPatch(round));
        }

        var node = JsonNode.Parse(ReconciliationCodec.Encode(all[0]))!.AsObject();
        node["operation"] = "Replace";
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.Decode<Patch>(node.ToJsonString()));

        node = JsonNode.Parse(ReconciliationCodec.Encode(all[0]))!.AsObject();
        node["operation"] = "AddRelation"; // value di AddSupport non è un AddRelation
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.Decode<Patch>(node.ToJsonString()));

        node = JsonNode.Parse(ReconciliationCodec.Encode(all[0]))!.AsObject();
        node["op"] = "delete"; // niente JSON Patch generico né proprietà extra
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.Decode<Patch>(node.ToJsonString()));

        node = JsonNode.Parse(ReconciliationCodec.Encode(all[0]))!.AsObject();
        node["value"]!["approved"] = true;
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.Decode<Patch>(node.ToJsonString()));
    }

    [Fact]
    public void Single_call_requires_views_A_B_Cross_in_order_level_zero_and_no_descriptor()
    {
        var valid = ReconciliationFixtures.Valid();
        Assert.Contains(Violations(valid with { Results = [valid.Results[1], valid.Results[0], valid.Results[2]] }), m => m.StartsWith("results: esattamente tre"));
        Assert.Contains(Violations(valid with { Results = [valid.Results[0], valid.Results[1]] }), m => m.StartsWith("results: esattamente tre"));
        Assert.Contains(Violations(valid with { Results = [valid.Results[0], valid.Results[0], valid.Results[2]] }), m => m.StartsWith("results: esattamente tre"));
        Assert.Contains(Violations(valid with { Results = [valid.Results[0], valid.Results[1], valid.Results[2] with { Level = 1 }] }), m => m.Contains("level"));
        Assert.Contains(Violations(valid with { Results = [valid.Results[0], valid.Results[1], valid.Results[2] with { Descriptor = "riepilogo" }] }), m => m.Contains("descriptor"));
    }

    [Fact]
    public void Structural_rules_reject_malformed_hashes_order_duplicates_and_missing_reason()
    {
        var a = ReconciliationFixtures.Valid().Results[0];
        string[] Check(ReconcileResult r) => [.. ReconciliationContractRules.CheckResult(r, ReconciliationFixtures.SourceHash)];

        Assert.Empty(Check(a));
        Assert.Contains(Check(a with { InputHash = new string('D', 64) }), m => m.StartsWith("inputHash"));
        Assert.Contains(Check(a with { InputHash = "abc" }), m => m.StartsWith("inputHash"));
        Assert.Contains(Check(a with { VisitedEntryIds = [.. a.VisitedEntryIds.Reverse()] }), m => m.StartsWith("visitedEntryIds"));
        Assert.Contains(Check(a with { VisitedEntryIds = [a.VisitedEntryIds[0], a.VisitedEntryIds[0]] }), m => m.StartsWith("visitedEntryIds"));
        Assert.Contains(Check(a with { ItemProposals = [], NoFindingsReason = null }), m => m.StartsWith("noFindingsReason"));
        Assert.Contains(Check(a with { NoFindingsReason = "  ", ItemProposals = [] }), m => m.StartsWith("noFindingsReason"));
        Assert.Contains(Check(a with { Descriptor = new string('x', 513) }), m => m.StartsWith("descriptor"));
        Assert.Contains(Check(a with { Level = -1 }), m => m.StartsWith("level"));

        var proposal = a.ItemProposals[0];
        Assert.Contains(Check(a with { ItemProposals = [proposal with { EvidenceIds = [] }] }), m => m.Contains("evidenceIds"));
        Assert.Contains(Check(a with { ItemProposals = [proposal with { EvidenceIds = [ReconciliationFixtures.E2, ReconciliationFixtures.E1] }] }), m => m.Contains("evidenceIds"));
        Assert.Contains(Check(a with { ItemProposals = [proposal with { SourceIds = [ReconciliationFixtures.S1, ReconciliationFixtures.S1] }] }), m => m.Contains("sourceIds"));
        Assert.Contains(Check(a with { ItemProposals = [proposal, proposal] }), m => m.Contains("localKey"));
    }

    [Fact]
    public void Candidate_rules_cover_endpoints_kind_type_and_derived_id()
    {
        string[] Check(ReconcileCandidate c) => [.. ReconciliationContractRules.CheckCandidate(c, ReconciliationFixtures.SourceHash)];
        var ok = ReconciliationFixtures.Candidate();
        Assert.Empty(Check(ok));

        Assert.Contains(Check(ok with { CandidateId = new string('0', 64) }), m => m.StartsWith("candidateId"));
        Assert.Contains(Check(ok with { Right = ok.Left }), m => m.StartsWith("endpoint"));
        Assert.Contains(Check(ok with { Kind = CandidateKind.Alias }), m => m.StartsWith("proposedType"));
        Assert.Contains(Check(ok with { Kind = CandidateKind.Conflict }), m => m.Contains("Conflict"));
        Assert.Contains(Check(ok with { ProposedType = ReconcileProposedType.Alias }), m => m.StartsWith("proposedType"));
        Assert.Contains(Check(ok with { OriginBatchIds = [] }), m => m.StartsWith("originBatchIds"));
        Assert.Contains(Check(ok with { Rationale = " " }), m => m.StartsWith("rationale"));
        Assert.Contains(Check(ok with { Left = ok.Left with { Hash = "xyz" } }), m => m.StartsWith("endpoint.hash"));
        Assert.Empty(Check(ReconciliationFixtures.Candidate(CandidateKind.Conflict, ReconcileProposedType.Contradicts)));
        Assert.Empty(Check(ReconciliationFixtures.Candidate(CandidateKind.Conflict, ReconcileProposedType.AlternativeTo)));
    }

    [Fact]
    public void Disposition_states_require_their_evidence()
    {
        string[] Check(CandidateDisposition d) => [.. ReconciliationContractRules.CheckDisposition(d)];
        var id = new string('9', 64);
        CandidateDisposition D(CandidateState s, string[]? children = null, string[]? outputs = null, string? reason = null) =>
            new() { CandidateId = id, State = s, ChildCandidateIds = children ?? [], ComparisonOutputHashes = outputs ?? [], Reason = reason };

        Assert.Empty(Check(D(CandidateState.Pending)));
        Assert.NotEmpty(Check(D(CandidateState.Expanded)));
        Assert.Empty(Check(D(CandidateState.Expanded, children: [new string('8', 64)])));
        Assert.NotEmpty(Check(D(CandidateState.Compared)));
        Assert.Empty(Check(D(CandidateState.Compared, outputs: [new string('7', 64)], reason: "no_relation")));
        Assert.NotEmpty(Check(D(CandidateState.Open, outputs: [new string('7', 64)])));
        Assert.NotEmpty(Check(D(CandidateState.Open, reason: "dubbio")));
        Assert.Empty(Check(D(CandidateState.Open, outputs: [new string('7', 64)], reason: "valori discordanti")));
        Assert.NotEmpty(Check(D(CandidateState.NotProcessed)));
        Assert.Empty(Check(D(CandidateState.NotProcessed, reason: "coverage_incomplete")));
    }

    [Fact]
    public void Patch_rules_reject_wrong_hash_ids_relation_target_and_mismatched_sets()
    {
        string[] Check(Patch p) => [.. ReconciliationContractRules.CheckPatch(p)];
        var support = ReconciliationFixtures.AddSupport();
        Assert.Empty(Check(support));

        Assert.Contains(Check(support with { PatchId = new string('0', 64) }), m => m.StartsWith("patchId"));
        Assert.Contains(Check(support with { BaseHash = "BAD" }), m => m.StartsWith("baseHash"));
        Assert.Contains(Check(support with { Target = support.Target with { ExpectedHash = "BAD" } }), m => m.StartsWith("baseHash"));
        Assert.Contains(Check(support with { EvidenceIds = [ReconciliationFixtures.E2] }), m => m.StartsWith("evidenceIds/sourceIds"));
        Assert.Contains(Check(support with { OriginBatchIds = [] }), m => m.StartsWith("originBatchIds"));
        Assert.Contains(Check(support with { Operation = PatchOperation.AddRelation }), m => m.StartsWith("value"));

        var relation = ReconciliationFixtures.AddRelation();
        Assert.Empty(Check(relation));
        Assert.Contains(Check(relation with { Target = relation.Target with { Kind = PatchTargetKind.Relation } }), m => m.StartsWith("target"));
        var self = (AddRelationValue)relation.Value with { ToEvidenceId = ReconciliationFixtures.E1 };
        Assert.Contains(Check(relation with { Value = self }), m => m.Contains("distinti"));

        var alternative = ReconciliationFixtures.Alternative();
        Assert.Empty(Check(alternative));
        var blank = (ProposeAlternativeValue)alternative.Value with { Reason = " " };
        Assert.Contains(Check(alternative with { Value = blank }), m => m.Contains("reason"));
    }

    [Fact]
    public void Derived_ids_are_typed_deterministic_and_respect_symmetry()
    {
        var sameRule = ReconciliationFixtures.Candidate(type: ReconcileProposedType.SameRule);
        var swapped = ReconciliationFixtures.Candidate(type: ReconcileProposedType.SameRule, swap: true);
        Assert.Equal(sameRule.CandidateId, swapped.CandidateId);

        var directed = ReconciliationFixtures.Candidate(type: ReconcileProposedType.DependsOn);
        var directedSwapped = ReconciliationFixtures.Candidate(type: ReconcileProposedType.DependsOn, swap: true);
        Assert.NotEqual(directed.CandidateId, directedSwapped.CandidateId);
        Assert.NotEqual(sameRule.CandidateId, directed.CandidateId);
        Assert.All(new[] { sameRule.CandidateId, directed.CandidateId }, id => Assert.True(ReconciliationContractRules.IsHash(id)));

        // Rationale e origini non definiscono l'identità; la sorgente sì.
        var otherRationale = sameRule with { Rationale = "altro", OriginBatchIds = ["batch-2"] };
        Assert.Equal(sameRule.CandidateId, ReconciliationIds.CandidateId(otherRationale.Kind, otherRationale.Left, otherRationale.Right, otherRationale.ProposedType, ReconciliationFixtures.SourceHash));
        Assert.NotEqual(sameRule.CandidateId, ReconciliationIds.CandidateId(sameRule.Kind, sameRule.Left, sameRule.Right, sameRule.ProposedType, new string('f', 64)));

        var entry = ReconciliationIds.EntryId(ReconciliationFixtures.SourceHash, ReconciliationFixtures.E1, ReconciliationFixtures.Hash1);
        Assert.Equal(entry, ReconciliationIds.EntryId(ReconciliationFixtures.SourceHash, ReconciliationFixtures.E1, ReconciliationFixtures.Hash1));
        Assert.NotEqual(entry, ReconciliationIds.EntryId(ReconciliationFixtures.SourceHash, ReconciliationFixtures.E2, ReconciliationFixtures.Hash1));
    }

    [Fact]
    public void PatchId_ignores_origins_but_depends_on_base_target_operation_and_value()
    {
        var p = ReconciliationFixtures.AddSupport();
        Assert.Equal(p.PatchId, ReconciliationIds.PatchId(p with { OriginBatchIds = ["batch-9"] }));
        Assert.NotEqual(p.PatchId, ReconciliationIds.PatchId(p with { BaseHash = new string('e', 64) }));
        Assert.NotEqual(p.PatchId, ReconciliationIds.PatchId(p with { Target = p.Target with { ExpectedHash = new string('e', 64) } }));
        Assert.NotEqual(p.PatchId, ReconciliationFixtures.AddSupport(ReconciliationFixtures.E2, ReconciliationFixtures.S2).PatchId);
        Assert.NotEqual(ReconciliationFixtures.Alternative("a").PatchId, ReconciliationFixtures.Alternative("b").PatchId);

        // Stesso insieme in ordine diverso: stessa identità (gli insiemi sono canonicalizzati; l'ordine errato è rifiutato a parte).
        var relation = ReconciliationFixtures.AddRelation();
        Assert.Equal(relation.PatchId, ReconciliationIds.PatchId(relation with { EvidenceIds = [ReconciliationFixtures.E2, ReconciliationFixtures.E1] }));
    }

    [Fact]
    public void Envelope_round_trips_and_rejects_wrong_versions_and_hashes()
    {
        var envelope = new ReconcileEnvelope<ReconcileResult>
        {
            SchemaVersion = "reconcile-1", RunId = ReconciliationFixtures.Run, SourceHash = ReconciliationFixtures.SourceHash, AlgorithmVersion = "global-reconcile-v1",
            PolicyHash = ReconciliationFixtures.PolicyHash, BaseHash = ReconciliationFixtures.BaseHash, Payload = ReconciliationFixtures.Result(ReconcileView.A, Snapshot, true),
        };
        var round = ReconciliationCodec.Decode<ReconcileEnvelope<ReconcileResult>>(ReconciliationCodec.Encode(envelope));
        Assert.Equal(envelope.RunId, round.RunId);
        Assert.Empty(ReconciliationContractRules.CheckEnvelope(round));

        Assert.NotEmpty(ReconciliationContractRules.CheckEnvelope(envelope with { SchemaVersion = "reconcile-2" }));
        Assert.NotEmpty(ReconciliationContractRules.CheckEnvelope(envelope with { AlgorithmVersion = "global-reconcile-v2" }));
        Assert.NotEmpty(ReconciliationContractRules.CheckEnvelope(envelope with { SourceHash = "ABC" }));
        Assert.NotEmpty(ReconciliationContractRules.CheckEnvelope(envelope with { PolicyHash = new string('A', 64) }));
        Assert.NotEmpty(ReconciliationContractRules.CheckEnvelope(envelope with { BaseHash = "" }));

        var node = JsonNode.Parse(ReconciliationCodec.Encode(envelope))!.AsObject();
        node["author"] = "ai";
        Assert.ThrowsAny<JsonException>(() => ReconciliationCodec.Decode<ReconcileEnvelope<ReconcileResult>>(node.ToJsonString()));
    }

    [Fact]
    public void References_reject_evidence_sources_and_endpoints_outside_the_snapshot()
    {
        var valid = ReconciliationFixtures.Valid();
        var a = valid.Results[0];
        var outsider = Guid.Parse("99000000-0000-4000-8000-000000000009");
        var proposal = a.ItemProposals[0];
        IReadOnlyList<string> Refs(ReconcileResult r) => ReconciliationReferences.CheckResult(r, Snapshot);

        Assert.Empty(Refs(a));
        Assert.Contains(Refs(a with { ItemProposals = [proposal with { EvidenceIds = [ReconciliationFixtures.E1, outsider] }] }), m => m.Contains("evidenceIds") && m.Contains("fuori snapshot"));
        Assert.Contains(Refs(a with { ItemProposals = [proposal with { SourceIds = [ReconciliationFixtures.S1, outsider] }] }), m => m.Contains("sourceIds") && m.Contains("fuori snapshot"));
        Assert.Contains(Refs(a with { ItemProposals = [proposal with { EvidenceIds = [ReconciliationFixtures.E1], SourceIds = [ReconciliationFixtures.S2] }] }), m => m.Contains("non appartiene"));
        Assert.Contains(Refs(a with { VisitedEntryIds = [new string('0', 64)] }), m => m.StartsWith("visitedEntryIds"));
        Assert.Contains(Refs(a with { Patches = [ReconciliationFixtures.AddSupport(outsider, ReconciliationFixtures.S1)] }), m => m.StartsWith("patches[0]"));

        var ghost = ReconciliationFixtures.Candidate() with { Right = ReconciliationFixtures.Endpoint(outsider, ReconciliationFixtures.Hash2) };
        Assert.Contains(Refs(a with { Candidates = [ghost] }), m => m.Contains("fuori snapshot"));
        var wrongHash = ReconciliationFixtures.Candidate() with { Right = ReconciliationFixtures.Endpoint(ReconciliationFixtures.E2, new string('5', 64)) };
        Assert.Contains(Refs(a with { Candidates = [wrongHash] }), m => m.Contains("contentHash"));
        var node = ReconciliationFixtures.Candidate() with { Right = new CandidateEndpoint { Kind = CandidateEndpointKind.Node, Id = new string('4', 64), Hash = new string('4', 64) } };
        Assert.Contains(Refs(a with { Candidates = [node] }), m => m.Contains("cataloghi gerarchici"));
        Assert.Empty(ReconciliationReferences.CheckResult(a with { Candidates = [node] }, Snapshot, new HashSet<string> { new('4', 64) }));

        var relation = ReconciliationFixtures.AddRelation();
        var relationOutside = relation with { Value = ((AddRelationValue)relation.Value) with { ToEvidenceId = outsider } };
        Assert.Contains(Refs(a with { Patches = [relationOutside] }), m => m.Contains("endpoint"));
        var targetRelation = ReconciliationFixtures.AddSupport() with { Target = new PatchTarget { Kind = PatchTargetKind.Relation, Id = outsider.ToString("D"), ExpectedHash = ReconciliationFixtures.Hash1 } };
        Assert.Contains(Refs(a with { Patches = [targetRelation] }), m => m.Contains("relazione"));
    }

    [Fact]
    public void Analysis_sources_must_match_snapshot_block_range_and_quote()
    {
        var valid = ReconciliationFixtures.Valid();
        Assert.Empty(ReconciliationReferences.CheckSingleCall(valid, Snapshot));

        var first = valid.Analysis.Sources[0];
        IReadOnlyList<string> Refs(DocumentAnalysisSource s) =>
            ReconciliationReferences.CheckSingleCall(valid with { Analysis = valid.Analysis with { Sources = [s, .. valid.Analysis.Sources.Skip(1)] } }, Snapshot);

        Assert.Contains(Refs(first with { Quote = "Cliente B | 9 ordini" }), m => m.Contains("quote"));
        Assert.Contains(Refs(first with { Start = 11, End = 31 }), m => m.Contains("range"));
        Assert.Contains(Refs(first with { BlockId = Guid.NewGuid() }), m => m.Contains("blocco"));
        Assert.Contains(Refs(first with { Id = Guid.Parse("99000000-0000-4000-8000-000000000009") }), m => m.Contains("fuori snapshot"));
        Assert.Contains(
            ReconciliationReferences.CheckSingleCall(valid with { Analysis = valid.Analysis with { ProjectId = Guid.NewGuid() } }, Snapshot),
            m => m.Contains("projectId"));
    }

    [Fact]
    public void Evidence_map_must_reference_snapshot_evidence_and_analysis_items()
    {
        var valid = ReconciliationFixtures.Valid();
        var outsider = Guid.Parse("99000000-0000-4000-8000-000000000009");

        var badEvidence = valid with { EvidenceMap = [new EvidenceMapEntry { ItemId = ReconciliationFixtures.Item1, EvidenceIds = [ReconciliationFixtures.E1, outsider] }] };
        Assert.Contains(Violations(badEvidence), m => m.Contains("evidenceMap") && m.Contains("fuori snapshot"));

        var badItem = valid with { EvidenceMap = [new EvidenceMapEntry { ItemId = outsider, EvidenceIds = [ReconciliationFixtures.E1] }] };
        Assert.Contains(Violations(badItem), m => m.Contains("itemId non presente"));

        var duplicated = valid with { EvidenceMap = [.. valid.EvidenceMap, .. valid.EvidenceMap] };
        Assert.Contains(Violations(duplicated), m => m.Contains("duplicato"));

        var unsorted = valid with { EvidenceMap = [new EvidenceMapEntry { ItemId = ReconciliationFixtures.Item1, EvidenceIds = [ReconciliationFixtures.E2, ReconciliationFixtures.E1] }] };
        Assert.Contains(Violations(unsorted), m => m.Contains("evidenceMap") && m.Contains("ordine"));
    }

    [Fact]
    public void Zero_evidence_with_reason_is_accepted_without_artificial_roots()
    {
        var empty = ReconciliationSnapshot.Empty(ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash);
        var output = ReconciliationFixtures.Valid(empty);

        Assert.All(output.Results, r => Assert.Empty(r.VisitedEntryIds));
        Assert.All(output.Results, r => Assert.Empty(r.VisitedNodeIds));
        Assert.All(output.Results, r => Assert.False(string.IsNullOrWhiteSpace(r.NoFindingsReason)));
        Assert.Empty(output.EvidenceMap);
        var decoded = ReconciliationCodec.DecodeSingleCall(ReconciliationCodec.Encode(output));
        Assert.Empty(Violations(decoded, empty));

        var unmotivated = output with { Results = [.. output.Results.Select(r => r with { NoFindingsReason = null })] };
        Assert.Contains(Violations(unmotivated, empty), m => m.Contains("noFindingsReason"));
    }

    [Fact]
    public void Large_snapshot_of_ten_thousand_entries_resolves_references()
    {
        var evidence = Enumerable.Range(0, 10_000)
            .Select(i => new SnapshotEvidence(
                Guid.Parse($"{i:x8}-0000-4000-8000-000000000001"), Guid.NewGuid(), CanonicalJson.HashText($"c{i}"),
                [ReconciliationFixtures.Source(Guid.Parse($"{i:x8}-1111-4000-8000-000000000001"))]))
            .ToList();
        var snapshot = new ReconciliationSnapshot(ReconciliationFixtures.Project, ReconciliationFixtures.SourceHash, evidence, []);
        var visited = evidence.Select(e => ReconciliationIds.EntryId(snapshot.SourceHash, e.EvidenceId, e.ContentHash)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var result = ReconciliationFixtures.Result(ReconcileView.A, snapshot) with { VisitedEntryIds = visited };

        Assert.Empty(ReconciliationReferences.CheckResult(result, snapshot));
        Assert.Empty(ReconciliationContractRules.CheckResult(result, snapshot.SourceHash));
    }

    [Fact]
    public void Unicode_and_special_characters_survive_round_trip_and_canonical_hashing()
    {
        var text = "Cliente «Ñandú» 🚚 — \"5\" ordini\\giorno\n<script>'; DROP TABLE--";
        var a = ReconciliationFixtures.Valid().Results[0];
        var result = a with { ItemProposals = [a.ItemProposals[0] with { Text = text }] };
        var round = ReconciliationCodec.DecodeResult(ReconciliationCodec.Encode(result));

        Assert.Equal(text, round.ItemProposals[0].Text);
        Assert.Equal(ReconciliationCodec.Canonical(result), ReconciliationCodec.Canonical(round));
        Assert.Contains("\\n", ReconciliationCodec.Canonical(result), StringComparison.Ordinal);
    }
}
