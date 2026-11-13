using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

// 002 reconciliation-hardening RH-006: costruttore fixture -> richiesta, catalogo e snapshot, senza database ne' tokenizer. Il replay e' PER BATCH: non si ricostruiscono
// gerarchie intere (i confini delle foglie dipendono dal tokenizer reale). Tutto e' una funzione pura della fixture: nessun GUID reale, nessun Guid.NewGuid.
//
// Identita' deterministiche da (run, alias): EvidenceId, VersionId e SourceId; ContentHash = SHA-256 dell'enunciato. Il catalogo del run richiede alias CONTIGUI (E1..En, S1..Sm):
// le posizioni non presenti nel batch sono riempite da evidenze segnaposto (una fonte ciascuna, mai citate dal batch), cosi' gli alias E/S del messaggio sono quelli archiviati.
// Gli item gia' proposti hanno handle globali i<n> (indice nella lista del run): le posizioni non presenti nel batch sono item segnaposto che non citano evidenze del batch.

/// <summary>Una fixture non e' ricostruibile con il compositore di produzione senza dati reali (STOP di RH-006): il motivo e' nel messaggio.</summary>
internal sealed class ReplayNotReconstructableException(string fixture, string reason) : Exception($"{fixture}: non ricostruibile ({reason}).")
{
    public string Reason { get; } = reason;
}

/// <summary>Batch ricostruito: richiesta del motore, catalogo del run e del batch, snapshot, base del passaggio e item gia' proposti, tutto con i tipi di produzione.</summary>
internal sealed record ReplayBatch(
    ReplayFixture Fixture, DocumentAnalysisInput Input, AnalysisAliasCatalog RunCatalog, ReconcileBatchRequest Request, ReconcileAliasCatalog Catalog,
    IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> Evidence, IReadOnlyList<ReconcileItemRef> PriorItems, ReconciliationBase? Base)
{
    public ReconciliationSnapshot Snapshot => Input.Snapshot;

    /// <summary>Messaggi del batch, con la STESSA funzione che misura e invia il batch (<see cref="ReconcileAliasMessages.Build"/>).</summary>
    public IReadOnlyList<ChatMessage> Messages() => ReconcileAliasMessages.Build(RunCatalog, Catalog, Request, Evidence);

    public string UserText() => Messages().Single().Text;

    /// <summary>Compone il DTO alias (di default l'output archiviato dell'assistente) con il compositore di produzione.</summary>
    public ReconcileAliasComposition Compose(string? aiJson = null) => ReconcileAliasComposer.Compose(aiJson ?? Fixture.Assistant, Catalog, Request, Snapshot, Base);
}

internal static class ReplayFixtureBuilder
{
    public static Guid Det(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16]);

    public static string Hex(string seed) => ReplayFixtures.Sha256Hex(seed);

    public static Guid EvidenceId(int run, string alias) => Det($"replay-evidence|{run}|{alias}");

    public static Guid VersionId(int run, string alias) => Det($"replay-evidence-version|{run}|{alias}");

    public static Guid SourceId(int run, string sourceAlias) => Det($"replay-source|{run}|{sourceAlias}");

    public static string SourceHash(int run) => Hex($"replay-snapshot|{run}");

    private static int Number(string alias) => int.Parse(alias.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Vero se la fixture e' ricostruibile; altrimenti il motivo (mai un'eccezione): le 34 fixture archiviate lo sono tutte.</summary>
    public static string? NotReconstructableReason(ReplayFixture f)
    {
        if (f.Relations.Count > 0)
        {
            return "relazioni note: gli alias R<n> dipendono dall'ordine delle relazioni del run";
        }

        var numbers = new HashSet<int>();
        foreach (var e in f.Entries)
        {
            if (e.Sources.Count != 1)
            {
                return $"la voce {e.Alias} ha {e.Sources.Count} fonti: gli alias S<n> dipendono dall'ordine di prima comparsa nel ledger";
            }

            if (Number(e.Sources[0].Alias) != Number(e.Alias))
            {
                return $"la fonte {e.Sources[0].Alias} non ha lo stesso numero della voce {e.Alias}";
            }

            if (!numbers.Add(Number(e.Alias)))
            {
                return $"alias {e.Alias} ripetuto";
            }
        }

        if (f.Stage is "expand" or "compare")
        {
            if (f.Focus is null)
            {
                return "stadio senza candidato in esame";
            }

            if (f.Stage == "expand" && !(f.Focus.Left == "N1" && f.Focus.Right == "N2"))
            {
                return "estremi di gruppo diversi da N1/N2";
            }

            if (f.Stage == "compare" && (f.Focus.Left[0] != 'E' || f.Focus.Right[0] != 'E'))
            {
                return "estremi del confronto non evidenze";
            }
        }
        else if (f.Focus is not null || f.Groups is not null)
        {
            return "candidato in esame o membership in uno stadio che non li ha";
        }

        foreach (var item in f.Items)
        {
            if (item.Evidence.Any(a => !f.Entries.Any(e => e.Alias == a)))
            {
                return $"l'item {item.Handle} cita evidenze fuori dal batch";
            }
        }

        return null;
    }

    public static ReplayBatch Build(ReplayFixture f)
    {
        if (NotReconstructableReason(f) is { } reason)
        {
            throw new ReplayNotReconstructableException(f.Name, reason);
        }

        var run = f.Run;
        var sourceHash = SourceHash(run);
        var maxE = f.Entries.Count == 0 ? 1 : f.Entries.Max(e => Number(e.Alias));
        var byNumber = f.Entries.ToDictionary(e => Number(e.Alias));
        var evidence = new List<DocumentAnalysisEvidence>(maxE);
        var dialog = new List<AnalysisDialogMessage>();
        for (var n = 1; n <= maxE; n++)
        {
            var alias = "E" + n;
            var sourceAlias = "S" + n;
            var entry = byNumber.GetValueOrDefault(n);
            var statement = entry?.Statement ?? $"Evidenza segnaposto {alias} (non presente nel batch).";
            var message = entry?.Origin == nameof(EvidenceOriginKind.Message);
            var quote = entry?.Sources[0].Quote ?? $"Fonte segnaposto {sourceAlias}.";
            var role = entry is null ? EvidenceSourceRole.Primary : Enum.Parse<EvidenceSourceRole>(entry.Sources[0].Role);
            var sourceId = SourceId(run, sourceAlias);
            var source = message
                ? new PersistedSource
                {
                    SourceId = sourceId, Kind = EvidenceSourceKind.Message, Role = role, CitationGroupId = Det($"replay-group|{run}|{sourceAlias}"), Part = 0,
                    MessageId = Det($"replay-message|{run}|{sourceAlias}"), Sequence = n, DocumentId = null, DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null,
                    Start = 0, End = quote.Length, Quote = quote,
                }
                : new PersistedSource
                {
                    SourceId = sourceId, Kind = EvidenceSourceKind.DocumentPassage, Role = role, CitationGroupId = Det($"replay-group|{run}|{sourceAlias}"), Part = 0,
                    MessageId = null, Sequence = null, DocumentId = Det($"replay-document|{run}|{sourceAlias}"), DocumentVersionId = Det($"replay-document-version|{run}|{sourceAlias}"),
                    PreparationId = Det($"replay-preparation|{run}|{sourceAlias}"), ArtifactId = Det($"replay-artifact|{run}|{sourceAlias}"), BlockId = Det($"replay-block|{run}|{sourceAlias}"),
                    Start = 0, End = quote.Length, Quote = quote,
                };
            if (message)
            {
                dialog.Add(new AnalysisDialogMessage(source.MessageId!.Value, AnalysisMessageRole.User, n, quote));
            }

            evidence.Add(new DocumentAnalysisEvidence(
                EvidenceId(run, alias), VersionId(run, alias), entry is null ? EvidenceOriginKind.Document : Enum.Parse<EvidenceOriginKind>(entry.Origin),
                entry is null ? EvidenceKind.Other : Enum.Parse<EvidenceKind>(entry.Kind), entry is null ? EvidencePolarity.Undefined : Enum.Parse<EvidencePolarity>(entry.Polarity),
                EvidenceCertainty.Declared, statement, Hex(statement), [source]));
        }

        var snapshot = new ReconciliationSnapshot(
            Det($"replay-project|{run}"), sourceHash, [.. evidence.Select(e => new SnapshotEvidence(e.EvidenceId, e.VersionId, e.ContentHash, e.Sources))], []);
        var input = new DocumentAnalysisInput(snapshot.ProjectId, Det($"replay-run|{run}"), 1, dialog.Count, evidence.Count - dialog.Count, 0, Hex($"replay-snapshot-hash|{run}"), snapshot, evidence, dialog);
        var runCatalog = AnalysisAliasCatalog.Build(input);
        var evidenceById = evidence.ToDictionary(e => e.EvidenceId);

        // Il catalogo del run deve riprodurre gli alias archiviati: E e S delle voci del batch.
        foreach (var e in f.Entries)
        {
            var known = runCatalog.Evidence(e.Alias) ?? throw new InvalidOperationException($"{f.Name}: alias {e.Alias} assente dal catalogo del run ricostruito.");
            if (known.EvidenceId != EvidenceId(run, e.Alias) || known.Sources.Single().SourceAlias != e.Sources[0].Alias)
            {
                throw new InvalidOperationException($"{f.Name}: il catalogo del run non riproduce gli alias archiviati di {e.Alias}.");
            }
        }

        var batchId = Hex($"replay-batch|{run}|{f.Call}");
        var view = Enum.Parse<ReconcileView>(f.View);
        var level = f.Stage == "reduce" ? 1 : 0;
        var batch = new DerivedBatchInput(
            batchId, f.Stage, f.View, null, level, 0, [], "{}", null, "measured", Hex($"replay-input|{run}|{f.Call}"), ReconcileAliasContracts.SchemaVersion, "replay-fixture");
        var entries = f.Entries.Select(e => CatalogEntryOf(sourceHash, evidenceById[EvidenceId(run, e.Alias)])).ToList();
        var nodes = f.Nodes.Select((n, i) => new CatalogNode
        {
            NodeId = Hex($"replay-node|{run}|{f.Call}|{n.Alias}"), View = Enum.Parse<ReconcileView>(n.View), Level = Math.Max(0, level - 1), Ordinal = i, ChildNodeIds = [],
            MemberSetHash = Hex($"replay-members|{run}|{f.Call}|{n.Alias}"), MemberCount = 2, KeyPageHashes = [], RelationPageHashes = [], Descriptor = n.Descriptor,
            DescriptorOutputHash = Hex(n.Descriptor),
        }).ToList();

        IReadOnlyList<ReconcileCandidate>? focus = null;
        ExpansionGroups? groups = null;
        if (f.Focus is { } fc)
        {
            CandidateEndpoint Endpoint(string alias) => alias[0] == 'E'
                ? new() { Kind = CandidateEndpointKind.Evidence, Id = EvidenceId(run, alias).ToString("D"), Hash = evidenceById[EvidenceId(run, alias)].ContentHash }
                : new() { Kind = CandidateEndpointKind.Set, Id = Hex($"replay-set|{run}|{f.Call}|{alias}"), Hash = Hex($"replay-set-members|{run}|{f.Call}|{alias}") };
            var left = Endpoint(fc.Left);
            var right = Endpoint(fc.Right);
            var type = Enum.Parse<ReconcileProposedType>(fc.Type);
            var kind = type == ReconcileProposedType.Alias ? CandidateKind.Alias : type is ReconcileProposedType.Contradicts or ReconcileProposedType.AlternativeTo ? CandidateKind.Conflict : CandidateKind.Relation;
            focus =
            [
                new ReconcileCandidate
                {
                    CandidateId = ReconciliationIds.CandidateId(kind, left, right, type, sourceHash), Kind = kind, Left = left, Right = right, ProposedType = type, Rationale = fc.Rationale,
                    Basis = EvidenceRelationBasis.Inferred, OriginBatchIds = [], ParentCandidateId = null,
                },
            ];
            if (f.Groups is { } g)
            {
                // membership-1 (v10): le etichette dei gruppi sono gli alias degli estremi; l'espansione usa N1/N2.
                groups = new ExpansionGroups(f.Entries.ToDictionary(
                    e => EvidenceId(run, e.Alias),
                    e => g[e.Alias] switch
                    {
                        "N1" => ExpansionTag.Left,
                        "N2" => ExpansionTag.Right,
                        "N1,N2" => ExpansionTag.Both,
                        var other => throw new ReplayNotReconstructableException(f.Name, $"gruppo sconosciuto «{other}» per {e.Alias}"),
                    }));
            }
        }

        var request = new ReconcileBatchRequest(batch, view, level, entries, nodes, focus, groups);

        // Item gia' proposti (handle globali): segnaposto nelle posizioni non presenti nel batch, che non citano alcuna evidenza del batch.
        var priorItems = new List<ReconcileItemRef>();
        var proposals = new List<ItemProposal>();
        if (f.Stage is "expand" or "compare" && f.Items.Count > 0)
        {
            var byHandle = f.Items.ToDictionary(i => Number(i.Handle));
            for (var n = 1; n <= byHandle.Keys.Max(); n++)
            {
                if (byHandle.TryGetValue(n, out var item))
                {
                    var ids = item.Evidence.Select(a => EvidenceId(run, a)).ToList();
                    var localKey = $"replay.{item.Handle}";
                    priorItems.Add(new ReconcileItemRef(localKey, item.Text, ids));
                    proposals.Add(new ItemProposal
                    {
                        LocalKey = localKey, Text = item.Text, Classification = DocumentAnalysisClassification.Documented, EvidenceIds = ids,
                        SourceIds = [.. ids.SelectMany(id => evidenceById[id].Sources.Select(s => s.SourceId)).Distinct()],
                    });
                }
                else
                {
                    priorItems.Add(new ReconcileItemRef($"replay.filler{n}", $"Item segnaposto {n}.", [Det($"replay-filler-item|{run}|{n}")]));
                }
            }
        }
        else if (f.Items.Count > 0)
        {
            throw new ReplayNotReconstructableException(f.Name, "item gia' proposti in uno stadio che non li mostra");
        }

        var catalog = ReconcileAliasCatalog.Build(runCatalog, request, priorItems);
        var baseState = f.Stage is "expand" or "compare" ? DocumentAnalysisBase.Compute(proposals, snapshot) : null;
        return new ReplayBatch(f, input, runCatalog, request, catalog, evidenceById, priorItems, baseState);
    }

    private static CatalogEntry CatalogEntryOf(string sourceHash, DocumentAnalysisEvidence e) => new()
    {
        EntryId = ReconciliationIds.EntryId(sourceHash, e.EvidenceId, e.ContentHash), EvidenceId = e.EvidenceId, EvidenceVersionId = e.VersionId, ContentHash = e.ContentHash,
        SourceIds = [.. e.Sources.Select(s => s.SourceId).Distinct()], Keys = [], KnownRelationIds = [], UnclassifiedViews = [],
    };
}
