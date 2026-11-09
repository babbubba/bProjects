using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>Dati congelati condivisi dai test degli alias (T125 parte A): messaggio PM, passaggi multi-blocco, due evidenze sullo stesso passaggio. Nessun database.</summary>
internal static class AliasFixtures
{
    public static readonly Guid Project = G(1);
    public static readonly Guid Run = G(2);
    public static readonly Guid OtherRun = G(3);
    public static readonly Guid M1 = G(10);
    public static readonly Guid M2 = G(11);
    public static readonly Guid RelationId = G(900);
    public static readonly string SourceHash = new('a', 64);
    public static readonly string SnapshotHash = new('e', 64);

    // Sorgenti: Sm1 (messaggio), Sa/Sb (stesso gruppo G1, parti 0 e 1, blocchi diversi), Sc (gruppo G2), Sx (condivisa da E3 ed E4), Sy, Sz.
    public static readonly Guid Sm1 = G(301), Sa = G(302), Sb = G(303), Sc = G(304), Sx = G(305), Sy = G(306), Sz = G(307);
    public static readonly Guid G1 = G(401), G2 = G(402), G3 = G(403), Gm = G(404);

    public static Guid G(int n) => Guid.Parse($"{n:x8}-0000-4000-8000-{n:x12}");

    public static Guid EvidenceId(int n) => G(100 + n);

    public static PersistedSource Msg(Guid id, Guid message, long sequence, string quote, EvidenceSourceRole role = EvidenceSourceRole.Primary) => new()
    {
        SourceId = id, Kind = EvidenceSourceKind.Message, Role = role, CitationGroupId = Gm, Part = 0, MessageId = message, Sequence = sequence, DocumentId = null,
        DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null, Start = 0, End = quote.Length, Quote = quote,
    };

    public static PersistedSource Doc(Guid id, Guid group, int part, int doc, int block, int start, string quote, EvidenceSourceRole role = EvidenceSourceRole.Primary) => new()
    {
        SourceId = id, Kind = EvidenceSourceKind.DocumentPassage, Role = role, CitationGroupId = group, Part = part, MessageId = null, Sequence = null,
        DocumentId = G(500 + doc), DocumentVersionId = G(510 + doc), PreparationId = G(520 + doc), ArtifactId = G(530 + doc), BlockId = G(600 + block),
        Start = start, End = start + quote.Length, Quote = quote,
    };

    public static DocumentAnalysisEvidence Ev(int n, EvidenceOriginKind origin, string statement, params PersistedSource[] sources) => new(
        EvidenceId(n), G(200 + n), origin, EvidenceKind.Requirement, EvidencePolarity.Affirmed, EvidenceCertainty.Declared, statement,
        new string("0123456789abcdef"[n % 16], 64), sources);

    /// <summary>E1 messaggio PM; E2 documento D1 con tre parti (due blocchi nello stesso gruppo + un altro gruppo); E3/E4 documento D2 sullo stesso passaggio Sx; E5 documento D3.</summary>
    public static List<DocumentAnalysisEvidence> Evidences() =>
    [
        Ev(1, EvidenceOriginKind.Message, "Servono 10 ordini al giorno.", Msg(Sm1, M1, 1, "Servono 10 ordini al giorno")),
        Ev(2, EvidenceOriginKind.Document, "Il cliente ordina via portale.",
            Doc(Sa, G1, 0, 1, 1, 100, "Il cliente ordina"), Doc(Sb, G1, 1, 1, 2, 220, "via portale web"), Doc(Sc, G2, 0, 1, 3, 400, "Contesto del portale", EvidenceSourceRole.Context)),
        Ev(3, EvidenceOriginKind.Document, "Il limite è 5 ordini.", Doc(Sx, G3, 0, 2, 4, 50, "Limite: 5 ordini")),
        Ev(4, EvidenceOriginKind.Document, "Il limite giornaliero non supera 5.", Doc(Sx, G3, 0, 2, 4, 50, "Limite: 5 ordini"), Doc(Sy, G2, 0, 2, 5, 90, "Nessun ordine oltre il limite")),
        Ev(5, EvidenceOriginKind.Document, "Il limite è 8 ordini.", Doc(Sz, G3, 0, 3, 6, 10, "Limite massimo 8 ordini")),
    ];

    public static List<AnalysisDialogMessage> Dialog() =>
    [
        new(M1, AnalysisMessageRole.User, 1, "Servono 10 ordini al giorno"),
        new(M2, AnalysisMessageRole.Assistant, 2, "Capito."),
    ];

    public static DocumentAnalysisInput Input(Guid? run = null, IReadOnlyList<DocumentAnalysisEvidence>? evidence = null, IReadOnlyList<SnapshotRelation>? relations = null, IReadOnlyList<AnalysisDialogMessage>? dialog = null)
    {
        var list = evidence ?? Evidences();
        var snapshot = new ReconciliationSnapshot(
            Project, SourceHash, [.. list.Select(e => new SnapshotEvidence(e.EvidenceId, e.VersionId, e.ContentHash, e.Sources))], relations ?? []);
        return new DocumentAnalysisInput(Project, run ?? Run, 2, 1, 3, 0, SnapshotHash, snapshot, list, dialog ?? Dialog());
    }

    public static SnapshotRelation Relation(int from, int to, EvidenceRelationType type, EvidenceRelationBasis basis = EvidenceRelationBasis.Declared, int id = 0) =>
        new(id == 0 ? RelationId : G(900 + id), EvidenceId(from), EvidenceId(to), type, basis);

    public static AiItem Item(string key, string text, DocumentAnalysisClassification classification, params string[] evidence) =>
        new() { Key = key, Text = text, Classification = classification, Evidence = evidence };

    public static readonly string[] AllAliases = ["E1", "E2", "E3", "E4", "E5"];

    public static AiAnalysisOutput ValidDto() => new()
    {
        Contract = "alias-1",
        Visited = new() { A = AllAliases, B = AllAliases },
        NoFindings = new() { A = "Nessuna relazione fra le evidenze.", B = "Nessuna relazione fra le evidenze.", Cross = null },
        Items = new()
        {
            Goals = [Item("i1", "Gestire gli ordini dei clienti.", DocumentAnalysisClassification.Declared, "E1")],
            ScopeOverview = [], ScopeIncluded = [], ScopeExcluded = [],
            Requirements =
            [
                Item("i2", "Il cliente ordina tramite il portale web.", DocumentAnalysisClassification.Documented, "E2"),
                Item("i3", "Il limite giornaliero è di 5 ordini.", DocumentAnalysisClassification.Documented, "E3", "E4"),
                Item("i4", "Il limite massimo è di 8 ordini.", DocumentAnalysisClassification.Documented, "E5"),
            ],
            Flows = [], Constraints = [], Dependencies = [], Assumptions = [], OpenQuestions = [],
        },
        Conflicts = [], Findings = [], KnownRelationChecks = [], Patches = [],
    };

    /// <summary>Un DTO con conflitto, finding (Compared e Open), verdetto su relazione nota e le tre operazioni di patch: solo per round-trip e schema.</summary>
    public static AiAnalysisOutput FullDto() => ValidDto() with
    {
        Conflicts =
        [
            new() { Key = "c1", Description = "Limite giornaliero discordante.", Alternatives =
            [
                Item("i5", "Il limite è 5.", DocumentAnalysisClassification.Documented, "E3"), Item("i6", "Il limite è 8.", DocumentAnalysisClassification.Documented, "E5"),
            ] },
        ],
        Findings =
        [
            new() { Key = "f1", View = ReconcileView.A, Left = "E3", Right = "E5", Type = EvidenceRelationType.Contradicts, Basis = EvidenceRelationBasis.Inferred, Rationale = "Limiti diversi.", Outcome = AiFindingOutcome.Compared, Reason = null },
            new() { Key = "f2", View = ReconcileView.B, Left = "E2", Right = "E3", Type = EvidenceRelationType.Qualifies, Basis = EvidenceRelationBasis.Declared, Rationale = "Contesto.", Outcome = AiFindingOutcome.Open, Reason = "Non è chiaro il perimetro." },
        ],
        KnownRelationChecks = [new() { Relation = "R1", Verdict = AiKnownRelationVerdict.Confirmed, Rationale = "Confermata." }],
        Patches =
        [
            new() { Key = "p1", Op = PatchOperation.AddSupport, Target = new() { Item = "i1", Relation = null }, Evidence = ["E2"], Value = new AiAddSupportValue() },
            new() { Key = "p2", Op = PatchOperation.AddRelation, Target = new() { Item = "i2", Relation = null }, Evidence = ["E2", "E3"], Value = new AiAddRelationValue { From = "E2", To = "E3", Type = EvidenceRelationType.Qualifies, Basis = EvidenceRelationBasis.Inferred, Rationale = null } },
            new() { Key = "p3", Op = PatchOperation.ProposeAlternative, Target = new() { Item = "i3", Relation = null }, Evidence = ["E5"], Value = new AiProposeAlternativeValue { Text = "Limite 8.", Classification = DocumentAnalysisClassification.Inferred, Reason = "Altro documento." } },
        ],
    };
}

/// <summary>
/// T125 parte A (DET) — catalogo alias `alias-catalog-1`, id canonici `canon-ids-1` e compositore deterministico alias → SingleCallOutput canonico
/// (contracts/ai-workflow.md §7.3–7.7). Il canonico è validato dai validatori esistenti, mai allentati; il server non decide semanticamente. Nessun database.
/// </summary>
public sealed class AnalysisAliasComposerTests
{
    private static AliasComposition Compose(AiAnalysisOutput dto, DocumentAnalysisInput? input = null, AnalysisAliasCatalog? catalog = null, string? expectedCanonicalHash = null)
    {
        input ??= AliasFixtures.Input();
        return AnalysisAliasComposer.Compose(dto, catalog ?? AnalysisAliasCatalog.Build(input), input, expectedCanonicalHash: expectedCanonicalHash);
    }

    private static AliasComposition Complete(AiAnalysisOutput dto, DocumentAnalysisInput? input = null)
    {
        var result = Compose(dto, input);
        Assert.True(result.Outcome == ReconciliationOutcome.Complete, string.Join(" | ", result.Violations.Concat(result.Validation?.Completion?.Gaps.Select(g => g.Code + ": " + g.Detail) ?? [])));
        return result;
    }

    private static AliasComposition Rejected(AiAnalysisOutput dto, string expectedFragment, DocumentAnalysisInput? input = null)
    {
        var result = Compose(dto, input);
        Assert.Equal(ReconciliationOutcome.Invalid, result.Outcome);
        Assert.Equal(DocumentWorkException.WorkOutputInvalid, result.ErrorCode);
        Assert.False(result.IsReusableWorkOutput);
        Assert.Contains(result.Violations, v => v.Contains(expectedFragment, StringComparison.Ordinal));
        return result;
    }

    private static AiAnalysisOutput WithRequirements(AiAnalysisOutput dto, params AiItem[] items) => dto with { Items = dto.Items with { Requirements = items } };

    private static string Id(string kind, string key, Guid? root = null) => AnalysisAliasComposer.CanonicalId(kind, root ?? AliasFixtures.Run, AliasFixtures.SourceHash, key).ToString("D");

    // ---------------------------------------------------------------- catalogo

    [Fact]
    public void Evidence_aliases_follow_the_reading_order_and_there_is_one_source_alias_per_distinct_source()
    {
        var catalog = AnalysisAliasCatalog.Build(AliasFixtures.Input());

        Assert.Equal(["E1", "E2", "E3", "E4", "E5"], catalog.Data.Evidence.Select(e => e.Alias));
        Assert.Equal(Enumerable.Range(1, 5).Select(AliasFixtures.EvidenceId), catalog.Data.Evidence.Select(e => e.EvidenceId));
        Assert.Equal(["S1", "S2", "S3", "S4", "S5", "S6", "S7"], catalog.Data.Sources.Select(s => s.Alias));
        Assert.Equal(7, catalog.Data.Sources.Select(s => s.SourceId).Distinct().Count());
        // E3 ed E4 citano lo stesso passaggio: stesso S.
        var shared = catalog.Data.Evidence[2].Sources.Single().SourceAlias;
        Assert.Equal("S5", shared);
        Assert.Contains(catalog.Data.Evidence[3].Sources, l => l.SourceAlias == shared);
        Assert.Equal(["S5", "S6"], catalog.Data.Evidence[3].Sources.Select(l => l.SourceAlias));
        Assert.Equal(AliasFixtures.Sx, catalog.SourceById(AliasFixtures.Sx)!.SourceId);
        Assert.Same(catalog.Evidence("E3"), catalog.EvidenceById(AliasFixtures.EvidenceId(3)));
        Assert.Null(catalog.Evidence("E6"));
        Assert.Null(catalog.Evidence("e1"));
        Assert.Null(catalog.Source("S8"));
        Assert.Null(catalog.Relation("R1"));
    }

    [Fact]
    public void Source_aliases_are_numbered_at_first_encounter_in_group_and_part_order_whatever_the_persisted_order()
    {
        var shuffled = AliasFixtures.Evidences();
        var e2 = shuffled[1];
        shuffled[1] = e2 with { Sources = [e2.Sources[2], e2.Sources[1], e2.Sources[0]] };      // Sc (gruppo 2), Sb (G1 parte 1), Sa (G1 parte 0)

        var catalog = AnalysisAliasCatalog.Build(AliasFixtures.Input(evidence: shuffled));
        var reference = AnalysisAliasCatalog.Build(AliasFixtures.Input());

        // il gruppo che compare per primo (G2 via Sc) apre la sequenza; dentro un gruppo le parti vanno in ordine.
        Assert.Equal([AliasFixtures.Sc, AliasFixtures.Sa, AliasFixtures.Sb], catalog.Data.Evidence[1].Sources.Select(l => catalog.Source(l.SourceAlias)!.SourceId));
        Assert.Equal([0, 1], catalog.Data.Evidence[1].Sources.Where(l => l.CitationGroupId == AliasFixtures.G1).Select(l => l.Part));
        Assert.Equal(["S2", "S3", "S4"], catalog.Data.Evidence[1].Sources.Select(l => l.SourceAlias));

        var inOrder = reference.Data.Evidence[1].Sources.Select(l => (l.CitationGroupId, l.Part)).ToList();
        Assert.Equal([(AliasFixtures.G1, 0), (AliasFixtures.G1, 1), (AliasFixtures.G2, 0)], inOrder);
    }

    [Fact]
    public void The_catalog_maps_every_alias_back_to_the_original_ids_ranges_quotes_and_provenance()
    {
        var catalog = AnalysisAliasCatalog.Build(AliasFixtures.Input());

        var e1 = catalog.Evidence("E1")!;
        Assert.Equal(EvidenceOriginKind.Message, e1.Origin);
        Assert.Null(e1.DocumentId);
        Assert.Equal(ReconciliationIds.EntryId(AliasFixtures.SourceHash, e1.EvidenceId, e1.ContentHash), e1.EntryId);
        Assert.Equal(AliasFixtures.Evidences()[0].ContentHash, e1.ContentHash);
        var s1 = catalog.Source("S1")!;
        Assert.Equal(EvidenceSourceKind.Message, s1.Provenance);
        Assert.Equal(AliasFixtures.M1, s1.MessageId);
        Assert.Equal(1, s1.Sequence);
        Assert.Equal("Servono 10 ordini al giorno", s1.Quote);

        var e2 = catalog.Evidence("E2")!;
        Assert.Equal(EvidenceOriginKind.Document, e2.Origin);
        Assert.Equal(AliasFixtures.G(501), e2.DocumentId);
        Assert.Equal(AliasFixtures.G(511), e2.DocumentVersionId);
        Assert.Equal(AliasFixtures.G(521), e2.PreparationId);
        Assert.Equal(AliasFixtures.G(202), e2.EvidenceVersionId);
        var sb = catalog.Source("S3")!;
        Assert.Equal(AliasFixtures.Sb, sb.SourceId);
        Assert.Equal(EvidenceSourceKind.DocumentPassage, sb.Provenance);
        Assert.Equal(AliasFixtures.G(531), sb.ArtifactId);
        Assert.Equal(AliasFixtures.G(602), sb.BlockId);
        Assert.Equal((220, 220 + "via portale web".Length, "via portale web"), (sb.Start, sb.End, sb.Quote));
        var link = e2.Sources.Single(l => l.SourceAlias == "S3");
        Assert.Equal((AliasFixtures.G1, 1, EvidenceSourceRole.Primary), (link.CitationGroupId, link.Part, link.Role));
        Assert.Equal(EvidenceSourceRole.Context, e2.Sources.Single(l => l.SourceAlias == "S4").Role);
        Assert.Equal(AliasFixtures.Run, catalog.Data.RunId);
        Assert.Equal(AliasFixtures.Project, catalog.Data.ProjectId);
        Assert.Equal(AliasFixtures.SourceHash, catalog.Data.SourceHash);
        Assert.Equal(("alias-catalog-1", "document-analysis-alias-1", "canon-ids-1"), (catalog.Data.CatalogVersion, catalog.Data.ContractVersion, catalog.Data.CanonIdsVersion));
    }

    [Fact]
    public void Known_relations_get_R_aliases_ordered_by_from_to_type_and_keep_their_basis()
    {
        var relations = new[]
        {
            AliasFixtures.Relation(3, 2, EvidenceRelationType.Qualifies, EvidenceRelationBasis.Declared, 1),
            AliasFixtures.Relation(2, 3, EvidenceRelationType.Contradicts, EvidenceRelationBasis.Inferred, 2),
            AliasFixtures.Relation(2, 3, EvidenceRelationType.Confirms, EvidenceRelationBasis.Declared, 3),
        };
        var catalog = AnalysisAliasCatalog.Build(AliasFixtures.Input(relations: relations));

        Assert.Equal(["R1", "R2", "R3"], catalog.Data.Relations.Select(r => r.Alias));
        Assert.Equal([EvidenceRelationType.Confirms, EvidenceRelationType.Contradicts, EvidenceRelationType.Qualifies], catalog.Data.Relations.Select(r => r.Type));
        Assert.Equal([EvidenceRelationBasis.Declared, EvidenceRelationBasis.Inferred, EvidenceRelationBasis.Declared], catalog.Data.Relations.Select(r => r.Basis));
        Assert.Equal(("E2", "E3"), (catalog.Relation("R1")!.FromAlias, catalog.Relation("R1")!.ToAlias));
        Assert.Equal(AliasFixtures.G(903), catalog.Relation("R1")!.RelationId);
        Assert.Equal(AliasFixtures.EvidenceId(3), catalog.Relation("R3")!.FromEvidenceId);
    }

    [Fact]
    public void The_snapshot_relation_carries_the_persisted_basis_and_existing_consumers_keep_compiling()
    {
        var withBasis = new SnapshotRelation(AliasFixtures.RelationId, AliasFixtures.EvidenceId(1), AliasFixtures.EvidenceId(2), EvidenceRelationType.Confirms, EvidenceRelationBasis.Declared);
        var legacy = new SnapshotRelation(AliasFixtures.RelationId, AliasFixtures.EvidenceId(1), AliasFixtures.EvidenceId(2), EvidenceRelationType.Confirms);

        Assert.Equal(EvidenceRelationBasis.Declared, withBasis.Basis);
        Assert.Equal(EvidenceRelationBasis.Inferred, legacy.Basis);
        // l'hash di base della relazione (v2, frozen) non dipende dal basis.
        Assert.Equal(DocumentAnalysisBase.RelationHash(withBasis), DocumentAnalysisBase.RelationHash(legacy));
    }

    [Fact]
    public void The_catalog_hash_is_stable_covers_the_whole_content_and_the_run_independent_hash_survives_a_new_root()
    {
        var first = AnalysisAliasCatalog.Build(AliasFixtures.Input());
        var second = AnalysisAliasCatalog.Build(AliasFixtures.Input());

        Assert.Equal(first.CatalogHash, second.CatalogHash);
        Assert.Equal(first.Encode(), second.Encode());
        Assert.True(ReconciliationContractRules.IsHash(first.CatalogHash));
        Assert.Equal(CanonicalJson.Sha256Hex(first.Encode()), first.CatalogHash);

        var otherRoot = AnalysisAliasCatalog.Build(AliasFixtures.Input(run: AliasFixtures.OtherRun));
        Assert.NotEqual(first.CatalogHash, otherRoot.CatalogHash);
        Assert.Equal(first.RunIndependentHash, otherRoot.RunIndependentHash);
        Assert.NotEqual(first.CatalogHash, first.RunIndependentHash);

        var altered = AliasFixtures.Evidences();
        altered[2] = altered[2] with { Sources = [AliasFixtures.Doc(AliasFixtures.Sx, AliasFixtures.G3, 0, 2, 4, 50, "Limite: 6 ordini")] };
        altered[3] = altered[3] with { Sources = [AliasFixtures.Doc(AliasFixtures.Sx, AliasFixtures.G3, 0, 2, 4, 50, "Limite: 6 ordini"), altered[3].Sources[1]] };
        var changedQuote = AnalysisAliasCatalog.Build(AliasFixtures.Input(evidence: altered));
        Assert.NotEqual(first.CatalogHash, changedQuote.CatalogHash);
        Assert.NotEqual(first.RunIndependentHash, changedQuote.RunIndependentHash);

        var reordered = AliasFixtures.Evidences();
        (reordered[0], reordered[4]) = (reordered[4], reordered[0]);
        Assert.NotEqual(first.CatalogHash, AnalysisAliasCatalog.Build(AliasFixtures.Input(evidence: reordered)).CatalogHash);
    }

    [Fact]
    public void A_catalog_round_trips_and_any_tampering_or_hash_mismatch_is_checkpoint_incompatible()
    {
        var catalog = AnalysisAliasCatalog.Build(AliasFixtures.Input(relations: [AliasFixtures.Relation(2, 3, EvidenceRelationType.Contradicts)]));
        var json = catalog.Encode();

        var decoded = AnalysisAliasCatalog.Decode(json, catalog.CatalogHash);
        Assert.Equal(catalog.CatalogHash, decoded.CatalogHash);
        Assert.Equal(json, decoded.Encode());
        Assert.Equal("R1", decoded.Relation("R1")!.Alias);

        var tampered = json.Replace("Limite: 5 ordini", "Limite: 9 ordini", StringComparison.Ordinal);
        Assert.NotEqual(json, tampered);
        foreach (var (text, hash) in new[] { (tampered, catalog.CatalogHash), (json, new string('0', 64)), ("{}", catalog.CatalogHash), ("non json", catalog.CatalogHash), (json[..40], catalog.CatalogHash) })
        {
            var e = Assert.Throws<DocumentWorkException>(() => AnalysisAliasCatalog.Decode(text, hash));
            Assert.Equal(DocumentWorkException.CheckpointIncompatible, e.Code);
        }
    }

    [Fact]
    public void Unverifiable_original_sources_fail_at_catalog_construction_as_source_snapshot_invalid()
    {
        var shortQuote = AliasFixtures.Evidences();
        shortQuote[2] = shortQuote[2] with { Sources = [shortQuote[2].Sources[0] with { End = shortQuote[2].Sources[0].End + 1 }] };

        var differentLocation = AliasFixtures.Evidences();
        differentLocation[3] = differentLocation[3] with { Sources = [differentLocation[3].Sources[0] with { BlockId = AliasFixtures.G(699) }, differentLocation[3].Sources[1]] };

        var noArtifact = AliasFixtures.Evidences();
        noArtifact[4] = noArtifact[4] with { Sources = [noArtifact[4].Sources[0] with { ArtifactId = null }] };

        var duplicated = AliasFixtures.Evidences();
        duplicated[4] = duplicated[4] with { EvidenceId = duplicated[3].EvidenceId };

        var cases = new[]
        {
            AliasFixtures.Input(evidence: shortQuote),
            AliasFixtures.Input(evidence: differentLocation),         // stesso SourceId, posizione diversa
            AliasFixtures.Input(evidence: noArtifact),
            AliasFixtures.Input(evidence: duplicated),
            AliasFixtures.Input(dialog: [AliasFixtures.Dialog()[1]]),                                              // il messaggio della fonte non è nello snapshot
            AliasFixtures.Input(dialog: [new(AliasFixtures.M1, AnalysisMessageRole.User, 7, "x"), AliasFixtures.Dialog()[1]]),   // sequenza diversa dal dialogo
        };
        foreach (var input in cases)
        {
            var e = Assert.Throws<DocumentWorkException>(() => AnalysisAliasCatalog.Build(input));
            Assert.Equal(DocumentWorkException.SourceSnapshotInvalid, e.Code);
        }
    }

    // ---------------------------------------------------------------- id canonici

    [Fact]
    public void Canonical_ids_are_uuid_v8_from_the_jcs_hash_of_the_tuple_and_independently_reproducible()
    {
        foreach (var kind in new[] { "item", "conflict" })
        {
            var root = AliasFixtures.Run;
            var jcs = "[\"canon-ids-1\",\"" + kind + "\",\"" + root.ToString("D") + "\",\"" + AliasFixtures.SourceHash + "\",\"i7\"]";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(jcs))[..16];
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            var expected = string.Create(36, bytes, (span, b) =>
            {
                var hex = Convert.ToHexStringLower(b);
                $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}".CopyTo(span);
            });

            var actual = AnalysisAliasComposer.CanonicalId(kind, root, AliasFixtures.SourceHash, "i7").ToString("D");
            Assert.Equal(expected, actual);
            Assert.Equal('8', actual[14]);
            Assert.Contains(actual[19], "89ab");
            Assert.Equal(actual.ToLowerInvariant(), actual);
        }
    }

    [Fact]
    public void Canonical_ids_are_stable_across_executions_and_change_with_root_source_hash_kind_and_local_key()
    {
        var a = AnalysisAliasComposer.CanonicalId("item", AliasFixtures.Run, AliasFixtures.SourceHash, "i1");

        Assert.Equal(a, AnalysisAliasComposer.CanonicalId("item", AliasFixtures.Run, AliasFixtures.SourceHash, "i1"));
        Assert.NotEqual(a, AnalysisAliasComposer.CanonicalId("item", AliasFixtures.OtherRun, AliasFixtures.SourceHash, "i1"));
        Assert.NotEqual(a, AnalysisAliasComposer.CanonicalId("item", AliasFixtures.Run, new string('b', 64), "i1"));
        Assert.NotEqual(a, AnalysisAliasComposer.CanonicalId("item", AliasFixtures.Run, AliasFixtures.SourceHash, "i2"));
        Assert.NotEqual(a, AnalysisAliasComposer.CanonicalId("conflict", AliasFixtures.Run, AliasFixtures.SourceHash, "i1"));
        var many = Enumerable.Range(1, 2000).Select(n => AnalysisAliasComposer.CanonicalId("item", AliasFixtures.Run, AliasFixtures.SourceHash, "i" + n)).ToList();
        Assert.Equal(many.Count, many.Distinct().Count());
        Assert.Throws<ArgumentException>(() => AnalysisAliasComposer.CanonicalId("alternative", AliasFixtures.Run, AliasFixtures.SourceHash, "i1"));
    }

    // ---------------------------------------------------------------- composizione

    [Fact]
    public void A_complete_dto_composes_a_canonical_output_that_the_existing_validators_accept()
    {
        var input = AliasFixtures.Input();
        var result = Complete(AliasFixtures.ValidDto());
        var output = result.Output!;

        Assert.Null(result.ErrorCode);
        Assert.Empty(result.Violations);
        Assert.Equal(ReconciliationOutcome.Complete, result.Validation!.Outcome);
        Assert.Equal(AnalysisAliasCatalog.Build(input).CatalogHash, result.CatalogHash);
        Assert.Equal(AnalysisAliasContracts.Hash(AliasFixtures.ValidDto()), result.AiOutputHash);
        Assert.Equal(CanonicalJson.Sha256Hex(ReconciliationCodec.Encode(output)), result.CanonicalHash);
        Assert.Equal(AnalysisAliasComposer.CanonicalHash(output), result.CanonicalHash);

        // metadati e identità: tutto del server
        Assert.Equal("1.1.0", output.Analysis.SchemaVersion);
        Assert.Equal((AliasFixtures.Run, AliasFixtures.Project, 2L, AnalysisContentState.Unvalidated), (output.Analysis.AnalysisId, output.Analysis.ProjectId, output.Analysis.SourceCutoffSequence, output.Analysis.ValidationState));
        Assert.Equal([ReconcileView.A, ReconcileView.B, ReconcileView.Cross], output.Results.Select(r => r.View));
        Assert.All(output.Results, r =>
        {
            Assert.Equal(input.BatchKey, r.BatchId);
            Assert.Equal(input.InputHash, r.InputHash);
            Assert.Equal(0, r.Level);
            Assert.Null(r.Descriptor);
            Assert.Empty(r.VisitedNodeIds);
        });

        // visited A/B da alias dichiarati, Cross = intersezione, ordine ordinal
        var entries = Enumerable.Range(1, 5).Select(n => ReconciliationIds.EntryId(AliasFixtures.SourceHash, AliasFixtures.EvidenceId(n), AliasFixtures.Evidences()[n - 1].ContentHash)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(entries, output.Results[0].VisitedEntryIds);
        Assert.Equal(entries, output.Results[1].VisitedEntryIds);
        Assert.Equal(entries, output.Results[2].VisitedEntryIds);

        // id canonici, item e proposte 1:1
        Assert.Equal(Id("item", "i1"), output.Analysis.Goals[0].Id.ToString("D"));
        Assert.Equal([Id("item", "i2"), Id("item", "i3"), Id("item", "i4")], output.Analysis.Requirements.Select(i => i.Id.ToString("D")));
        Assert.Equal(["i1", "i2", "i3", "i4"], output.Results[2].ItemProposals.Select(p => p.LocalKey));
        Assert.Empty(output.Results[0].ItemProposals);
        Assert.Equal("Il cliente ordina tramite il portale web.", output.Analysis.Requirements[0].Text);
        Assert.Equal(DocumentAnalysisClassification.Documented, output.Analysis.Requirements[0].Classification);
        Assert.Empty(output.Analysis.Conflicts);
    }



    // ---- helper dei test di composizione ----

    private static DocumentAnalysisInput WithRelation(EvidenceRelationType type = EvidenceRelationType.Contradicts) =>
        AliasFixtures.Input(relations: [AliasFixtures.Relation(2, 3, type)]);

    private static AiFinding Finding(
        string key, ReconcileView view, string left, string right, EvidenceRelationType type = EvidenceRelationType.Contradicts,
        EvidenceRelationBasis basis = EvidenceRelationBasis.Inferred, AiFindingOutcome outcome = AiFindingOutcome.Compared, string? reason = null) =>
        new() { Key = key, View = view, Left = left, Right = right, Type = type, Basis = basis, Rationale = "motivo " + key, Outcome = outcome, Reason = reason };

    private static AiPatch Patch(string key, PatchOperation op, string? item, string? relation, string[] evidence, AiPatchValue? value = null) => new()
    {
        Key = key, Op = op, Target = new() { Item = item, Relation = relation }, Evidence = evidence,
        Value = value ?? op switch
        {
            PatchOperation.AddSupport => new AiAddSupportValue(),
            PatchOperation.AddRelation => new AiAddRelationValue { From = "E2", To = "E3", Type = EvidenceRelationType.Qualifies, Basis = EvidenceRelationBasis.Inferred, Rationale = "legame" },
            _ => new AiProposeAlternativeValue { Text = "Alternativa " + key, Classification = DocumentAnalysisClassification.Documented, Reason = "altra fonte" },
        },
    };

    private static IReadOnlyList<string> Gaps(AliasComposition r) => [.. r.Validation!.Completion!.Gaps.Select(g => g.Code)];

    private static ReconciliationBase BaseOf(SingleCallOutput output, DocumentAnalysisInput input) =>
        DocumentAnalysisBase.Compute(output with { Results = [.. output.Results.Select(r => r with { Patches = [] })] }, input.Snapshot);

    // ---------------------------------------------------------------- composizione completa e determinismo

    [Fact]
    public void A_full_dto_with_conflicts_findings_known_relation_verdict_and_patches_composes_and_is_complete()
    {
        var input = WithRelation();
        var first = Complete(AliasFixtures.FullDto(), input);
        var second = Complete(AliasFixtures.FullDto(), input);

        Assert.Equal(ReconciliationCodec.Encode(first.Output!), ReconciliationCodec.Encode(second.Output!));
        Assert.Equal(first.CanonicalHash, second.CanonicalHash);
        Assert.Equal(ReconciliationOutcome.Complete, Compose(AliasFixtures.FullDto(), input, expectedCanonicalHash: first.CanonicalHash).Outcome);

        var diverged = Compose(AliasFixtures.FullDto(), input, expectedCanonicalHash: new string('0', 64));
        Assert.Equal(ReconciliationOutcome.Invalid, diverged.Outcome);
        Assert.Equal(DocumentWorkException.IntegrityConflict, diverged.ErrorCode);
        Assert.False(diverged.IsReusableWorkOutput);
    }

    [Fact]
    public void Analysis_sources_are_rebuilt_only_from_the_original_sources_of_the_cited_evidence()
    {
        var dto = AliasFixtures.ValidDto();
        dto = dto with { Items = dto.Items with { Requirements = [dto.Items.Requirements[0]] } };      // E3, E4, E5 non citate
        var result = Compose(dto);
        var output = result.Output!;

        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.True(result.IsReusableWorkOutput);
        Assert.Equal([AliasFixtures.Sm1, AliasFixtures.Sa, AliasFixtures.Sb, AliasFixtures.Sc], output.Analysis.Sources.Select(s => s.Id));

        var message = output.Analysis.Sources[0];
        Assert.Equal((DocumentAnalysisSourceKind.Message, AliasFixtures.M1, AnalysisMessageRole.User), (message.Kind, message.MessageId, message.MessageRole));
        Assert.All(new object?[] { message.RevisionId, message.DocumentId, message.DocumentVersionId, message.PreparationId, message.ArtifactId, message.BlockId, message.Start, message.End, message.Quote }, Assert.Null);

        var sa = output.Analysis.Sources[1];
        Assert.Equal(DocumentAnalysisSourceKind.DocumentPassage, sa.Kind);
        Assert.Equal((AliasFixtures.G(501), AliasFixtures.G(511), AliasFixtures.G(521), AliasFixtures.G(531), AliasFixtures.G(601)), (sa.DocumentId!.Value, sa.DocumentVersionId!.Value, sa.PreparationId!.Value, sa.ArtifactId!.Value, sa.BlockId!.Value));
        Assert.Equal((100, 117, "Il cliente ordina"), (sa.Start, sa.End, sa.Quote));
        Assert.Null(sa.MessageId);
        Assert.Null(sa.MessageRole);
        Assert.NotEqual(sa.BlockId, output.Analysis.Sources[2].BlockId);

        // nulla viene assegnato per somiglianza o prossimità: le evidenze non citate restano non mappate
        Assert.Equal(["evidence_not_mapped"], Gaps(result).Distinct());
        Assert.Equal(3, Gaps(result).Count);
        Assert.DoesNotContain(output.EvidenceMap.SelectMany(m => m.EvidenceIds), id => id == AliasFixtures.EvidenceId(3) || id == AliasFixtures.EvidenceId(4) || id == AliasFixtures.EvidenceId(5));
    }

    [Fact]
    public void Two_evidences_on_the_same_passage_share_one_source_and_every_source_of_a_cited_evidence_is_referenced()
    {
        var both = Complete(AliasFixtures.ValidDto()).Output!;
        Assert.Equal([AliasFixtures.Sx, AliasFixtures.Sy], both.Analysis.Requirements[1].EvidenceRefs);
        Assert.Single(both.Analysis.Sources, s => s.Id == AliasFixtures.Sx);
        Assert.Equal(7, both.Analysis.Sources.Count);

        var dto = AliasFixtures.ValidDto();
        var onlyE4 = dto with { Items = dto.Items with { Requirements = [dto.Items.Requirements[0], AliasFixtures.Item("i3", "Limite.", DocumentAnalysisClassification.Documented, "E4"), dto.Items.Requirements[2]] } };
        var result = Compose(onlyE4);
        Assert.Equal([AliasFixtures.Sx, AliasFixtures.Sy], result.Output!.Analysis.Requirements[1].EvidenceRefs);
        Assert.Equal(["evidence_not_mapped"], Gaps(result).Distinct());      // E3 non citata: nessuna assegnazione automatica anche se condivide il passaggio
    }

    [Fact]
    public void Multi_block_citations_keep_ranges_blocks_and_group_part_order()
    {
        var catalog = AnalysisAliasCatalog.Build(AliasFixtures.Input());
        var result = Complete(AliasFixtures.ValidDto());
        var refs = result.Output!.Analysis.Requirements[0].EvidenceRefs;

        Assert.Equal([AliasFixtures.Sa, AliasFixtures.Sb, AliasFixtures.Sc], refs);
        Assert.Equal(refs, catalog.Evidence("E2")!.Sources.Select(l => catalog.Source(l.SourceAlias)!.SourceId));
        var parts = refs.Select(r => result.Output.Analysis.Sources.Single(s => s.Id == r)).ToList();
        Assert.Equal([100, 220, 400], parts.Select(p => p.Start));
        Assert.Equal([AliasFixtures.G(601), AliasFixtures.G(602), AliasFixtures.G(603)], parts.Select(p => p.BlockId!.Value));
        Assert.Equal(["Il cliente ordina", "via portale web", "Contesto del portale"], parts.Select(p => p.Quote));

        var shuffled = AliasFixtures.Evidences();
        shuffled[1] = shuffled[1] with { Sources = [shuffled[1].Sources[2], shuffled[1].Sources[1], shuffled[1].Sources[0]] };
        var input = AliasFixtures.Input(evidence: shuffled);
        var shuffledResult = Complete(AliasFixtures.ValidDto(), input);
        var shuffledCatalog = AnalysisAliasCatalog.Build(input);
        var shuffledRefs = shuffledResult.Output!.Analysis.Requirements[0].EvidenceRefs.ToList();
        Assert.Equal(shuffledCatalog.Evidence("E2")!.Sources.Select(l => shuffledCatalog.Source(l.SourceAlias)!.SourceId), shuffledRefs);
        Assert.True(shuffledRefs.IndexOf(AliasFixtures.Sa) < shuffledRefs.IndexOf(AliasFixtures.Sb));
    }

    [Fact]
    public void Message_and_document_evidence_follow_the_declared_and_documented_rules_of_analysis_content_1_1()
    {
        var dto = AliasFixtures.ValidDto();
        var declaredFromDocument = dto with { Items = dto.Items with { Goals = [AliasFixtures.Item("i1", "Obiettivo.", DocumentAnalysisClassification.Declared, "E2")] } };
        Rejected(declaredFromDocument, "Declared richiede");

        var documentedFromMessage = dto with { Items = dto.Items with { Goals = [AliasFixtures.Item("i1", "Obiettivo.", DocumentAnalysisClassification.Documented, "E1")] } };
        Rejected(documentedFromMessage, "Documented richiede");

        var mixed = dto with { Items = dto.Items with { Goals = [AliasFixtures.Item("i1", "Obiettivo.", DocumentAnalysisClassification.Declared, "E1", "E2")] } };
        var result = Complete(mixed);
        Assert.Equal([AliasFixtures.Sm1, AliasFixtures.Sa, AliasFixtures.Sb, AliasFixtures.Sc], result.Output!.Analysis.Goals[0].EvidenceRefs);
    }

    [Fact]
    public void A_requirement_with_several_evidences_maps_all_of_them_in_uuid_order_in_the_evidence_map()
    {
        var dto = AliasFixtures.ValidDto();
        var reversed = dto with { Items = dto.Items with { Requirements = [dto.Items.Requirements[0], AliasFixtures.Item("i3", "Limite.", DocumentAnalysisClassification.Documented, "E4", "E3", "E5"), dto.Items.Requirements[2]] } };
        var output = Complete(reversed).Output!;
        var item = output.Analysis.Requirements[1];
        var map = output.EvidenceMap.Single(m => m.ItemId == item.Id);

        Assert.Equal(ReconciliationIds.SortedIds([AliasFixtures.EvidenceId(3), AliasFixtures.EvidenceId(4), AliasFixtures.EvidenceId(5)]).Select(Guid.Parse), map.EvidenceIds);
        Assert.Equal([AliasFixtures.Sx, AliasFixtures.Sy, AliasFixtures.Sz], item.EvidenceRefs);      // ordine di citazione, senza duplicati
        Assert.Equal(output.EvidenceMap.Select(m => m.ItemId).Distinct().Count(), output.EvidenceMap.Count);
        Assert.Equal(["i1", "i2", "i3", "i4"], output.Results[2].ItemProposals.Select(p => p.LocalKey));
        Assert.Equal(ReconciliationIds.SortedIds([AliasFixtures.Sx, AliasFixtures.Sy, AliasFixtures.Sz]).Select(Guid.Parse), output.Results[2].ItemProposals[2].SourceIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conflicts_between_documents_and_between_a_document_and_the_pm_get_canonical_ids_open_status_and_no_resolution(bool withPm)
    {
        var dto = AliasFixtures.ValidDto();
        var first = withPm
            ? AliasFixtures.Item("i5", "Servono 10 ordini.", DocumentAnalysisClassification.Declared, "E1")
            : AliasFixtures.Item("i5", "Il limite è 5.", DocumentAnalysisClassification.Documented, "E3");
        var second = AliasFixtures.Item("i6", "Il limite è 8.", DocumentAnalysisClassification.Documented, "E5");
        var withConflict = dto with { Conflicts = [new() { Key = "c1", Description = "Limite discordante.", Alternatives = [first, second] }] };

        var output = Complete(withConflict).Output!;
        var conflict = Assert.Single(output.Analysis.Conflicts);

        Assert.Equal(Id("conflict", "c1"), conflict.Id.ToString("D"));
        Assert.Equal([Id("item", "i5"), Id("item", "i6")], conflict.Alternatives.Select(a => a.Id.ToString("D")));
        Assert.Equal(AnalysisConflictStatus.Open, conflict.Status);
        Assert.Null(conflict.Resolution);
        Assert.Equal(withPm ? DocumentAnalysisClassification.Declared : DocumentAnalysisClassification.Documented, conflict.Alternatives[0].Classification);
        Assert.All(conflict.Alternatives, a => Assert.Contains(output.EvidenceMap, m => m.ItemId == a.Id));
        Assert.Equal(["i1", "i2", "i3", "i4", "i5", "i6"], output.Results[2].ItemProposals.Select(p => p.LocalKey));
        Assert.Equal(withPm ? [AliasFixtures.Sm1] : [AliasFixtures.Sx], conflict.Alternatives[0].EvidenceRefs);
    }

    [Fact]
    public void Malformed_conflicts_and_alternatives_are_rejected()
    {
        var dto = AliasFixtures.ValidDto();
        var one = dto with { Conflicts = [new() { Key = "c1", Description = "x", Alternatives = [AliasFixtures.Item("i5", "A.", DocumentAnalysisClassification.Documented, "E3")] }] };
        Rejected(one, "almeno due");

        var empty = dto with { Conflicts = [new() { Key = "c1", Description = "x", Alternatives = [] }] };
        Rejected(empty, "almeno due");

        var noEvidence = dto with { Conflicts = [new() { Key = "c1", Description = "x", Alternatives = [AliasFixtures.Item("i5", "A.", DocumentAnalysisClassification.Documented, "E3"), AliasFixtures.Item("i6", "B.", DocumentAnalysisClassification.Documented)] }] };
        Rejected(noEvidence, "alternatives[1]");

        var blank = dto with { Conflicts = [new() { Key = "c1", Description = "  ", Alternatives = [AliasFixtures.Item("i5", "A.", DocumentAnalysisClassification.Documented, "E3"), AliasFixtures.Item("i6", "B.", DocumentAnalysisClassification.Documented, "E5")] }] };
        Rejected(blank, "description");
    }

    // ---------------------------------------------------------------- finding

    [Fact]
    public void Findings_become_candidates_and_dispositions_in_the_declared_view_with_server_ids_and_the_dto_hash()
    {
        var dto = AliasFixtures.ValidDto() with
        {
            Findings =
            [
                Finding("f1", ReconcileView.A, "E3", "E5"),
                Finding("f2", ReconcileView.B, "E2", "E3", EvidenceRelationType.Qualifies, EvidenceRelationBasis.Declared, AiFindingOutcome.Open, "dubbio sul perimetro"),
                Finding("f3", ReconcileView.Cross, "E1", "E3", EvidenceRelationType.Confirms),
            ],
        };
        var input = AliasFixtures.Input();
        var result = Complete(dto, input);
        var output = result.Output!;
        var evidence = AliasFixtures.Evidences();

        var a = Assert.Single(output.Results[0].Candidates);
        var left = ReconciliationFixtures.Endpoint(AliasFixtures.EvidenceId(3), evidence[2].ContentHash);
        var right = ReconciliationFixtures.Endpoint(AliasFixtures.EvidenceId(5), evidence[4].ContentHash);
        Assert.Equal((CandidateKind.Conflict, ReconcileProposedType.Contradicts, EvidenceRelationBasis.Inferred), (a.Kind, a.ProposedType, a.Basis));
        Assert.Equal((left, right), (a.Left, a.Right));
        Assert.Equal(ReconciliationIds.CandidateId(CandidateKind.Conflict, left, right, ReconcileProposedType.Contradicts, AliasFixtures.SourceHash), a.CandidateId);
        Assert.Equal([input.BatchKey], a.OriginBatchIds);
        Assert.Null(a.ParentCandidateId);
        Assert.Equal("motivo f1", a.Rationale);
        var aiHash = AnalysisAliasContracts.Hash(dto);
        var da = Assert.Single(output.Results[0].Dispositions);
        Assert.Equal((a.CandidateId, CandidateState.Compared, (string?)null), (da.CandidateId, da.State, da.Reason));
        Assert.Empty(da.ChildCandidateIds);
        Assert.Equal([aiHash], da.ComparisonOutputHashes);

        var b = Assert.Single(output.Results[1].Candidates);
        Assert.Equal((CandidateKind.Relation, ReconcileProposedType.Qualifies, EvidenceRelationBasis.Declared), (b.Kind, b.ProposedType, b.Basis));
        var db = Assert.Single(output.Results[1].Dispositions);
        Assert.Equal((CandidateState.Open, "dubbio sul perimetro"), (db.State, db.Reason));
        Assert.Equal([aiHash], db.ComparisonOutputHashes);

        Assert.Equal(CandidateKind.Relation, Assert.Single(output.Results[2].Candidates).Kind);
        Assert.Equal(3, output.Results.Sum(r => r.Candidates.Count));
    }

    [Fact]
    public void Findings_that_are_malformed_or_duplicated_are_rejected()
    {
        var dto = AliasFixtures.ValidDto();
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E5", outcome: AiFindingOutcome.Open)] }, "reason");
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E5", outcome: AiFindingOutcome.Open, reason: "  ")] }, "reason");
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E3")] }, "coincident");
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E5"), Finding("f1", ReconcileView.B, "E2", "E3")] }, "f1");
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E5"), Finding("f2", ReconcileView.B, "E5", "E3")] }, "duplicat");       // Contradicts è simmetrico: stessa identità
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E5"), Finding("f2", ReconcileView.A, "E3", "E5")] }, "duplicat");
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E5") with { Rationale = " " }] }, "rationale");

        // direzione diversa su un tipo diretto: identità diverse, non duplicati
        Complete(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E5", EvidenceRelationType.Qualifies), Finding("f2", ReconcileView.B, "E5", "E3", EvidenceRelationType.Qualifies)] });
    }

    // ---------------------------------------------------------------- copertura

    [Fact]
    public void Incomplete_coverage_is_valid_incomplete_never_complete_and_the_server_never_fills_the_gaps()
    {
        var dto = AliasFixtures.ValidDto();
        var missingVisit = dto with { Visited = new() { A = ["E1", "E2", "E3", "E4"], B = AliasFixtures.AllAliases } };
        var result = Compose(missingVisit);

        Assert.Equal(ReconciliationOutcome.ValidIncomplete, result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.True(result.IsReusableWorkOutput);
        Assert.Equal(["entry_not_visited"], Gaps(result).Distinct());
        Assert.Equal(4, result.Output!.Results[0].VisitedEntryIds.Count);
        Assert.Equal(4, result.Output.Results[2].VisitedEntryIds.Count);      // intersezione di A e B
        Assert.Equal(5, result.Output.Results[1].VisitedEntryIds.Count);
        Assert.Contains(ReconciliationIds.EntryId(AliasFixtures.SourceHash, AliasFixtures.EvidenceId(5), AliasFixtures.Evidences()[4].ContentHash), result.Validation!.Completion!.MissingEntryIds);

        var nothingVisited = Compose(dto with { Visited = new() { A = [], B = [] } });
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, nothingVisited.Outcome);
        Assert.Equal(10, Gaps(nothingVisited).Count(g => g == "entry_not_visited"));

        var uncited = Compose(dto with { Items = dto.Items with { Requirements = [dto.Items.Requirements[0], dto.Items.Requirements[1]] } });
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, uncited.Outcome);
        Assert.Equal(["evidence_not_mapped"], Gaps(uncited));
    }

    [Fact]
    public void Known_relations_require_a_terminal_verdict_and_missing_verdicts_stay_unresolved()
    {
        var input = WithRelation();
        var withoutVerdict = Compose(AliasFixtures.ValidDto(), input);
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, withoutVerdict.Outcome);
        Assert.Equal(["known_relation_unresolved"], Gaps(withoutVerdict));
        Assert.Empty(withoutVerdict.Output!.Results.SelectMany(r => r.Candidates));      // il server non inventa il verdetto

        var evidence = AliasFixtures.Evidences();
        foreach (var (verdict, state, reason) in new[]
        {
            (AiKnownRelationVerdict.Confirmed, CandidateState.Compared, (string?)null),
            (AiKnownRelationVerdict.NoRelation, CandidateState.Compared, "no_relation"),
            (AiKnownRelationVerdict.OpenForPM, CandidateState.Open, "Serve la decisione del PM."),
        })
        {
            var dto = AliasFixtures.ValidDto() with { KnownRelationChecks = [new() { Relation = "R1", Verdict = verdict, Rationale = "Serve la decisione del PM." }] };
            var result = Complete(dto, input);
            var candidate = Assert.Single(result.Output!.Results[2].Candidates);
            var disposition = Assert.Single(result.Output.Results[2].Dispositions);

            Assert.Equal((AliasFixtures.EvidenceId(2).ToString("D"), AliasFixtures.EvidenceId(3).ToString("D")), (candidate.Left.Id, candidate.Right.Id));
            Assert.Equal((evidence[1].ContentHash, evidence[2].ContentHash), (candidate.Left.Hash, candidate.Right.Hash));
            Assert.Equal((ReconcileProposedType.Contradicts, CandidateKind.Conflict, EvidenceRelationBasis.Declared), (candidate.ProposedType, candidate.Kind, candidate.Basis));
            Assert.Equal((state, reason), (disposition.State, disposition.Reason));
            Assert.Equal([AnalysisAliasContracts.Hash(dto)], disposition.ComparisonOutputHashes);
        }

        // relazione non simmetrica: tipo diretto, kind Relation
        var directed = WithRelation(EvidenceRelationType.DependsOn);
        var confirmed = Complete(AliasFixtures.ValidDto() with { KnownRelationChecks = [new() { Relation = "R1", Verdict = AiKnownRelationVerdict.Confirmed, Rationale = "ok" }] }, directed);
        Assert.Equal((CandidateKind.Relation, ReconcileProposedType.DependsOn), (confirmed.Output!.Results[2].Candidates[0].Kind, confirmed.Output.Results[2].Candidates[0].ProposedType));
    }

    [Fact]
    public void Relation_verdicts_with_unknown_missing_or_duplicated_relations_are_rejected()
    {
        var input = WithRelation();
        var verdict = new AiKnownRelationCheck { Relation = "R1", Verdict = AiKnownRelationVerdict.Confirmed, Rationale = "ok" };

        Rejected(AliasFixtures.ValidDto() with { KnownRelationChecks = [verdict with { Relation = "R9" }] }, "R9", input);
        Rejected(AliasFixtures.ValidDto() with { KnownRelationChecks = [verdict] }, "R1");                                  // lo snapshot non ha relazioni
        Rejected(AliasFixtures.ValidDto() with { KnownRelationChecks = [verdict, verdict with { Verdict = AiKnownRelationVerdict.NoRelation }] }, "R1", input);
        Rejected(AliasFixtures.ValidDto() with { KnownRelationChecks = [verdict with { Rationale = "" }] }, "rationale", input);
        // un finding con la stessa identità di un verdetto è un duplicato vietato
        Rejected(AliasFixtures.ValidDto() with { KnownRelationChecks = [verdict], Findings = [Finding("f1", ReconcileView.Cross, "E3", "E2", EvidenceRelationType.Contradicts, EvidenceRelationBasis.Declared)] }, "duplicat", input);
    }

    // ---------------------------------------------------------------- patch

    [Fact]
    public void An_AddSupport_patch_gets_server_base_expected_hash_ids_and_sources_computed_before_the_patches()
    {
        var input = AliasFixtures.Input();
        var dto = AliasFixtures.ValidDto() with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", null, ["E2"])] };
        var result = Complete(dto, input);
        var output = result.Output!;
        var patch = Assert.Single(output.Results[2].Patches);
        var proposal = output.Results[2].ItemProposals.Single(p => p.LocalKey == "i1");

        Assert.Empty(output.Results[0].Patches);
        Assert.Equal(BaseOf(output, input).BaseHash, patch.BaseHash);
        Assert.Equal((PatchTargetKind.Item, "i1", DocumentAnalysisBase.ItemHash(proposal)), (patch.Target.Kind, patch.Target.Id, patch.Target.ExpectedHash));
        Assert.Equal(ReconciliationIds.PatchId(patch), patch.PatchId);
        Assert.Equal([AliasFixtures.EvidenceId(2)], patch.EvidenceIds);
        Assert.Equal(ReconciliationIds.SortedIds([AliasFixtures.Sa, AliasFixtures.Sb, AliasFixtures.Sc]).Select(Guid.Parse), patch.SourceIds);
        Assert.Equal([input.BatchKey], patch.OriginBatchIds);
        Assert.Equal(PatchOperation.AddSupport, patch.Operation);
        var value = Assert.IsType<AddSupportValue>(patch.Value);
        Assert.Equal(patch.EvidenceIds, value.EvidenceIds);
        Assert.Equal(patch.SourceIds, value.SourceIds);
        Assert.Equal(PatchDispositionKind.Applied, Assert.Single(result.Validation!.Patches!.Dispositions).Kind);
    }

    [Fact]
    public void AddRelation_and_ProposeAlternative_patches_derive_envelope_ids_from_the_declared_aliases()
    {
        var input = AliasFixtures.Input();
        var relation = Patch("p1", PatchOperation.AddRelation, "i2", null, ["E3", "E2"]);
        var alternative = Patch("p2", PatchOperation.ProposeAlternative, "i3", null, ["E5"]);
        var result = Complete(AliasFixtures.ValidDto() with { Patches = [relation, alternative] }, input);
        var patches = result.Output!.Results[2].Patches;

        var addRelation = patches.Single(p => p.Operation == PatchOperation.AddRelation);
        var value = Assert.IsType<AddRelationValue>(addRelation.Value);
        Assert.Equal((AliasFixtures.EvidenceId(2), AliasFixtures.EvidenceId(3), EvidenceRelationType.Qualifies, EvidenceRelationBasis.Inferred, "legame"), (value.FromEvidenceId, value.ToEvidenceId, value.Type, value.Basis, value.Rationale));
        Assert.Equal(ReconciliationIds.SortedIds([AliasFixtures.EvidenceId(2), AliasFixtures.EvidenceId(3)]).Select(Guid.Parse), addRelation.EvidenceIds);
        Assert.Equal(ReconciliationIds.SortedIds([AliasFixtures.Sa, AliasFixtures.Sb, AliasFixtures.Sc, AliasFixtures.Sx]).Select(Guid.Parse), addRelation.SourceIds);

        var propose = patches.Single(p => p.Operation == PatchOperation.ProposeAlternative);
        var alt = Assert.IsType<ProposeAlternativeValue>(propose.Value);
        Assert.Equal(("Alternativa p2", DocumentAnalysisClassification.Documented, "altra fonte"), (alt.Text, alt.Classification, alt.Reason));
        Assert.Equal([AliasFixtures.Sz], propose.SourceIds);
        Assert.All(result.Validation!.Patches!.Dispositions, d => Assert.Equal(PatchDispositionKind.Applied, d.Kind));

        // `evidence` vuota per una AddRelation: gli endpoint della relazione sono già dichiarati nel value
        Complete(AliasFixtures.ValidDto() with { Patches = [Patch("p1", PatchOperation.AddRelation, "i2", null, [])] }, input);
        Rejected(AliasFixtures.ValidDto() with { Patches = [Patch("p1", PatchOperation.AddRelation, "i2", null, ["E2"])] }, "evidence");
        Rejected(AliasFixtures.ValidDto() with { Patches = [Patch("p1", PatchOperation.AddRelation, "i2", null, [], new AiAddRelationValue { From = "E2", To = "E2", Type = EvidenceRelationType.Qualifies, Basis = EvidenceRelationBasis.Inferred, Rationale = null })] }, "endpoint");
    }

    [Fact]
    public void A_patch_can_target_a_known_relation_by_alias_with_the_server_relation_hash()
    {
        var input = WithRelation();
        var dto = AliasFixtures.ValidDto() with
        {
            KnownRelationChecks = [new() { Relation = "R1", Verdict = AiKnownRelationVerdict.Confirmed, Rationale = "ok" }],
            Patches = [Patch("p1", PatchOperation.AddSupport, null, "R1", ["E5"])],
        };
        var result = Complete(dto, input);
        var patch = Assert.Single(result.Output!.Results[2].Patches);

        Assert.Equal((PatchTargetKind.Relation, AliasFixtures.RelationId.ToString("D")), (patch.Target.Kind, patch.Target.Id));
        Assert.Equal(DocumentAnalysisBase.RelationHash(input.Snapshot.Relations[0]), patch.Target.ExpectedHash);
        Assert.Equal(BaseOf(result.Output, input).BaseHash, patch.BaseHash);
        Assert.Equal(PatchDispositionKind.Applied, Assert.Single(result.Validation!.Patches!.Dispositions).Kind);
    }

    [Fact]
    public void Incompatible_alternatives_are_preserved_as_a_conflict_only_when_the_revision_represents_them()
    {
        var alternatives = new[]
        {
            Patch("p1", PatchOperation.ProposeAlternative, "i2", null, ["E5"]),
            Patch("p2", PatchOperation.ProposeAlternative, "i2", null, ["E3"]),
        };
        var notRepresented = Compose(AliasFixtures.ValidDto() with { Patches = alternatives });
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, notRepresented.Outcome);
        Assert.Equal(["conflict_not_represented"], Gaps(notRepresented));
        Assert.All(notRepresented.Validation!.Patches!.Dispositions, d => Assert.Equal(PatchDispositionKind.PreservedAsConflict, d.Kind));
        Assert.Empty(notRepresented.Output!.Analysis.Conflicts);

        var represented = AliasFixtures.ValidDto() with
        {
            Patches = alternatives,
            Conflicts = [new() { Key = "c1", Description = "Alternative incompatibili.", Alternatives = [AliasFixtures.Item("i5", "A.", DocumentAnalysisClassification.Documented, "E5"), AliasFixtures.Item("i6", "B.", DocumentAnalysisClassification.Documented, "E3")] }],
        };
        var result = Complete(represented);
        Assert.All(result.Validation!.Patches!.Dispositions, d => Assert.Equal(PatchDispositionKind.PreservedAsConflict, d.Kind));
        Assert.Null(Assert.Single(result.Output!.Analysis.Conflicts).Resolution);
    }

    [Fact]
    public void Invalid_patch_targets_values_and_duplicates_are_rejected()
    {
        var dto = AliasFixtures.ValidDto();
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i99", null, ["E2"])] }, "i99");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, null, null, ["E2"])] }, "target");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", "R1", ["E2"])] }, "target");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, null, "R1", ["E2"])] }, "R1");                       // snapshot senza relazioni
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddRelation, null, "R1", [])] }, "target");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", null, [])] }, "evidence");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", null, ["E2", "E2"])] }, "duplicat");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", null, ["E2"]), Patch("p1", PatchOperation.AddSupport, "i1", null, ["E3"])] }, "p1");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", null, ["E2"]), Patch("p2", PatchOperation.AddSupport, "i1", null, ["E2"])] }, "duplicat");      // stesso patchId
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", null, ["E2"], new AiAddRelationValue { From = "E2", To = "E3", Type = EvidenceRelationType.Qualifies, Basis = EvidenceRelationBasis.Inferred, Rationale = null })] }, "value");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.ProposeAlternative, "i1", null, ["E2"], new AiProposeAlternativeValue { Text = " ", Classification = DocumentAnalysisClassification.Inferred, Reason = "x" })] }, "text");
    }

    // ---------------------------------------------------------------- rifiuti

    [Fact]
    public void A_local_key_repeated_in_two_sections_or_as_an_alternative_and_an_item_is_rejected_naming_the_key_and_both_places()
    {
        var dto = AliasFixtures.ValidDto();
        var i2 = dto.Items.Requirements[0];

        // N2: lo stesso item in scopeIncluded e in requirements. Nessuna deduplica silenziosa: rifiuto con chiave e sezioni.
        var twice = Rejected(dto with { Items = dto.Items with { ScopeIncluded = [i2] } }, "i2");
        var message = Assert.Single(twice.Violations, v => v.Contains("i2", StringComparison.Ordinal));
        Assert.Contains("items.scopeIncluded[0]", message, StringComparison.Ordinal);
        Assert.Contains("items.requirements[0]", message, StringComparison.Ordinal);
        Assert.Contains("una sola sezione", message, StringComparison.Ordinal);

        // N3: le alternative di un conflitto ripetute anche come item di una sezione.
        var alternatives = new[] { i2 with { Key = "i7", Evidence = ["E3"] }, i2 with { Key = "i8", Evidence = ["E5"] } };
        var inItems = dto with
        {
            Items = dto.Items with { Requirements = [.. dto.Items.Requirements, .. alternatives] },
            Conflicts = [new() { Key = "c1", Description = "d", Alternatives = alternatives }],
        };
        var repeated = Rejected(inItems, "i7");
        Assert.Contains(repeated.Violations, v => v.Contains("conflicts[0].alternatives[0]", StringComparison.Ordinal) && v.Contains("items.requirements[", StringComparison.Ordinal)
            && v.Contains("solo in conflicts", StringComparison.Ordinal));
        Assert.Contains(repeated.Violations, v => v.Contains("i8", StringComparison.Ordinal));
        Assert.Null(repeated.Output);      // mai deduplicato: nessun canonico
    }

    [Fact]
    public void Unknown_out_of_snapshot_and_repeated_aliases_are_rejected_with_the_alias_and_the_path()
    {
        var dto = AliasFixtures.ValidDto();
        var first = dto.Items.Goals[0];

        Rejected(dto with { Items = dto.Items with { Goals = [first with { Evidence = ["E1", "E99"] }] } }, "E99");
        Rejected(dto with { Visited = new() { A = [.. AliasFixtures.AllAliases, "E6"], B = AliasFixtures.AllAliases } }, "visited");
        Rejected(dto with { Findings = [Finding("f1", ReconcileView.A, "E3", "E99")] }, "E99");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddSupport, "i1", null, ["E99"])] }, "E99");
        Rejected(dto with { Patches = [Patch("p1", PatchOperation.AddRelation, "i1", null, [], new AiAddRelationValue { From = "E2", To = "E99", Type = EvidenceRelationType.Qualifies, Basis = EvidenceRelationBasis.Inferred, Rationale = null })] }, "E99");

        Rejected(dto with { Items = dto.Items with { Goals = [first with { Evidence = ["E1", "E1"] }] } }, "duplicat");
        Rejected(dto with { Visited = new() { A = [.. AliasFixtures.AllAliases, "E1"], B = AliasFixtures.AllAliases } }, "duplicat");
        Rejected(dto with { Visited = new() { A = AliasFixtures.AllAliases, B = ["E1", "E2", "E3", "E4", "E5", "E5"] } }, "duplicat");

        // chiavi locali: univoche fra item e alternative; per tipo per conflitti, finding e patch
        Rejected(dto with { Items = dto.Items with { Goals = [first], Requirements = [first with { Evidence = ["E2"] }, .. dto.Items.Requirements.Skip(1)] } }, "i1");
        var alternatives = new[] { first with { Key = "i5", Evidence = ["E3"] }, first with { Key = "i1", Evidence = ["E5"] } };
        Rejected(dto with { Conflicts = [new() { Key = "c1", Description = "d", Alternatives = alternatives }] }, "i1");
        var conflict = new AiConflict { Key = "c1", Description = "d", Alternatives = [first with { Key = "i5", Evidence = ["E3"] }, first with { Key = "i6", Evidence = ["E5"] }] };
        Rejected(dto with { Conflicts = [conflict, conflict with { Alternatives = [first with { Key = "i7", Evidence = ["E3"] }, first with { Key = "i8", Evidence = ["E5"] }] }] }, "c1");
    }

    [Fact]
    public void Semantically_malformed_items_are_rejected_by_the_existing_rules_and_evidence_free_open_questions_are_allowed()
    {
        var dto = AliasFixtures.ValidDto();
        Rejected(dto with { Items = dto.Items with { Assumptions = [AliasFixtures.Item("i9", "Ipotesi.", DocumentAnalysisClassification.Inferred, "E1")] } }, "Assumed");
        Rejected(dto with { Items = dto.Items with { Goals = [AliasFixtures.Item("i1", "   ", DocumentAnalysisClassification.Declared, "E1")] } }, "text");
        Rejected(dto with { Items = dto.Items with { Requirements = [AliasFixtures.Item("i2", "Senza evidenze.", DocumentAnalysisClassification.Declared), .. dto.Items.Requirements.Skip(1)] } }, "Declared richiede");

        // 1.1 non richiede evidenze per un item Inferred: nessuna proposta, nessuna mappa, e l'evidenza E2 resta non citata (copertura incompleta)
        var evidenceFree = Compose(dto with { Items = dto.Items with { Requirements = [AliasFixtures.Item("i2", "Senza evidenze.", DocumentAnalysisClassification.Inferred), .. dto.Items.Requirements.Skip(1)] } });
        Assert.Equal(ReconciliationOutcome.ValidIncomplete, evidenceFree.Outcome);
        Assert.Equal(["evidence_not_mapped"], Gaps(evidenceFree));
        Assert.DoesNotContain(evidenceFree.Output!.Results[2].ItemProposals, p => p.LocalKey == "i2");

        var withQuestion = dto with { Items = dto.Items with { OpenQuestions = [AliasFixtures.Item("i9", "Qual è il limite corretto?", DocumentAnalysisClassification.Inferred)] } };
        var output = Complete(withQuestion).Output!;
        Assert.Empty(output.Analysis.OpenQuestions[0].EvidenceRefs);
        Assert.DoesNotContain(output.EvidenceMap, m => m.ItemId == output.Analysis.OpenQuestions[0].Id);
        Assert.DoesNotContain(output.Results[2].ItemProposals, p => p.LocalKey == "i9");
        Assert.Equal(Id("item", "i9"), output.Analysis.OpenQuestions[0].Id.ToString("D"));

        var assumed = dto with { Items = dto.Items with { Assumptions = [AliasFixtures.Item("i9", "Ipotesi.", DocumentAnalysisClassification.Assumed, "E1")] } };
        Assert.Single(Complete(assumed).Output!.Analysis.Assumptions);
    }

    [Fact]
    public void Text_with_unicode_quotes_newlines_and_special_characters_is_preserved_verbatim()
    {
        const string text = "Prezzo: 5 € — \"sconto\" 🚚\nriga 2; '); -- \\ <b>x</b> %s {0}";
        var dto = AliasFixtures.ValidDto();
        var output = Complete(WithRequirements(dto, AliasFixtures.Item("i2", text, DocumentAnalysisClassification.Documented, "E2", "E3", "E4", "E5"))).Output!;

        Assert.Equal(text, output.Analysis.Requirements[0].Text);
        Assert.Equal(text, output.Results[2].ItemProposals[1].Text);
        Assert.Equal(text, AnalysisAliasContracts.Decode(AnalysisAliasContracts.Encode(WithRequirements(dto, AliasFixtures.Item("i2", text, DocumentAnalysisClassification.Documented, "E2")))).Items.Requirements[0].Text);
    }

    // ---------------------------------------------------------------- catalogo manomesso, input grezzo, replay, retry

    [Fact]
    public void A_catalog_that_does_not_match_the_frozen_snapshot_is_checkpoint_incompatible_even_with_a_consistent_hash()
    {
        var input = AliasFixtures.Input();
        var altered = AliasFixtures.Evidences();
        altered[2] = altered[2] with { Sources = [AliasFixtures.Doc(AliasFixtures.Sx, AliasFixtures.G3, 0, 2, 4, 50, "Limite: 6 ordini")] };
        altered[3] = altered[3] with { Sources = [AliasFixtures.Doc(AliasFixtures.Sx, AliasFixtures.G3, 0, 2, 4, 50, "Limite: 6 ordini"), altered[3].Sources[1]] };
        var foreign = AnalysisAliasCatalog.Build(AliasFixtures.Input(evidence: altered));
        var otherRun = AnalysisAliasCatalog.Build(AliasFixtures.Input(run: AliasFixtures.OtherRun));

        // manomissione del JSON con hash ricalcolato sul contenuto manomesso: il decode regge, il confronto con lo snapshot no
        var json = AnalysisAliasCatalog.Build(input).Encode().Replace("Limite: 5 ordini", "Limite: 9 ordini", StringComparison.Ordinal);
        var tampered = AnalysisAliasCatalog.Decode(json, CanonicalJson.Sha256Hex(json));

        foreach (var catalog in new[] { foreign, otherRun, tampered })
        {
            var e = Assert.Throws<DocumentWorkException>(() => AnalysisAliasComposer.Compose(AliasFixtures.ValidDto(), catalog, input));
            Assert.Equal(DocumentWorkException.CheckpointIncompatible, e.Code);
        }
    }

    [Fact]
    public void Raw_json_input_is_decoded_strictly_and_composes_like_the_typed_dto()
    {
        var input = AliasFixtures.Input();
        var catalog = AnalysisAliasCatalog.Build(input);
        var json = AnalysisAliasContracts.Encode(AliasFixtures.ValidDto());

        var fromJson = AnalysisAliasComposer.Compose(json, catalog, input);
        Assert.Equal(Complete(AliasFixtures.ValidDto()).CanonicalHash, fromJson.CanonicalHash);
        Assert.Equal(AnalysisAliasContracts.Hash(AliasFixtures.ValidDto()), fromJson.AiOutputHash);

        foreach (var text in new[] { json[..(json.Length / 2)], string.Empty, "null", json.Replace("\"E1\"", "\"e1\"", StringComparison.Ordinal), json.Insert(1, "\"id\":\"x\",") })
        {
            var rejected = AnalysisAliasComposer.Compose(text, catalog, input);
            Assert.Equal(ReconciliationOutcome.Invalid, rejected.Outcome);
            Assert.Equal(DocumentWorkException.WorkOutputInvalid, rejected.ErrorCode);
            Assert.Null(rejected.Output);
            Assert.Null(rejected.AiOutputHash);
            Assert.NotEmpty(rejected.Violations);
            Assert.Equal(catalog.CatalogHash, rejected.CatalogHash);
        }
    }

    [Fact]
    public void A_retry_with_a_new_root_recomposes_the_same_dto_changing_only_run_ids()
    {
        var first = Complete(AliasFixtures.ValidDto()).Output!;
        var secondInput = AliasFixtures.Input(run: AliasFixtures.OtherRun);
        var retried = Complete(AliasFixtures.ValidDto(), secondInput);
        var second = retried.Output!;

        Assert.Equal(AliasFixtures.OtherRun, second.Analysis.AnalysisId);
        Assert.Equal(AnalysisAliasCatalog.Build(AliasFixtures.Input()).RunIndependentHash, AnalysisAliasCatalog.Build(secondInput).RunIndependentHash);
        Assert.Empty(first.Analysis.Requirements.Select(i => i.Id).Intersect(second.Analysis.Requirements.Select(i => i.Id)));
        Assert.Equal(first.Analysis.Sources.Select(s => s.Id), second.Analysis.Sources.Select(s => s.Id));
        Assert.Equal(first.Analysis.Requirements.Select(i => i.EvidenceRefs), second.Analysis.Requirements.Select(i => i.EvidenceRefs));
        Assert.Equal(first.EvidenceMap.Select(m => m.EvidenceIds), second.EvidenceMap.Select(m => m.EvidenceIds));
        Assert.Equal(first.Results.Select(r => r.VisitedEntryIds), second.Results.Select(r => r.VisitedEntryIds));
        Assert.Equal(Id("item", "i2", AliasFixtures.OtherRun), second.Analysis.Requirements[0].Id.ToString("D"));
        Assert.NotEqual(AnalysisAliasComposer.CanonicalHash(first), retried.CanonicalHash);

        // il vecchio catalogo non appartiene al nuovo root
        var e = Assert.Throws<DocumentWorkException>(() => AnalysisAliasComposer.Compose(AliasFixtures.ValidDto(), AnalysisAliasCatalog.Build(AliasFixtures.Input()), secondInput));
        Assert.Equal(DocumentWorkException.CheckpointIncompatible, e.Code);
    }

    [Fact]
    public void A_large_snapshot_of_thousands_of_evidences_aliases_and_composes_to_a_complete_output()
    {
        const int count = 3000;
        var evidence = Enumerable.Range(1, count)
            .Select(n => AliasFixtures.Ev(n, EvidenceOriginKind.Document, "Requisito " + n, AliasFixtures.Doc(AliasFixtures.G(10000 + n), AliasFixtures.G(20000 + n), 0, 1, 7000 + n, 10, "Requisito numero " + n)))
            .ToList();
        var aliases = Enumerable.Range(1, count).Select(n => "E" + n).ToArray();
        var template = AliasFixtures.ValidDto();
        var dto = template with
        {
            Visited = new() { A = aliases, B = aliases },
            Items = template.Items with
            {
                Goals = [],
                Requirements = [.. Enumerable.Range(1, count).Select(n => AliasFixtures.Item("i" + n, "Requisito " + n, DocumentAnalysisClassification.Documented, "E" + n))],
            },
        };
        var input = AliasFixtures.Input(evidence: evidence);

        var clock = Stopwatch.StartNew();
        var catalog = AnalysisAliasCatalog.Build(input);
        var result = AnalysisAliasComposer.Compose(dto, catalog, input);

        Assert.Equal(ReconciliationOutcome.Complete, result.Outcome);
        Assert.Equal($"E{count}", catalog.Data.Evidence[^1].Alias);
        Assert.Equal($"S{count}", catalog.Data.Sources[^1].Alias);
        Assert.Equal(count, result.Output!.Analysis.Sources.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"composizione troppo lenta: {clock.Elapsed}");
    }
}
