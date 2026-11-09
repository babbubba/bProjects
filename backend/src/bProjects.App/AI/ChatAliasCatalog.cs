using System.Text.Json;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Catalogo alias della chat documentale `chat-alias-catalog-1` (contracts/ai-workflow.md §8, T128): funzione pura delle fonti ammesse dello snapshot
// congelato del turno. Un alias `F<n>` per SourceId DISTINTO (non per Evidence): una fonte condivisa da più evidenze ha un solo alias; le parti di una
// citazione multi-blocco (citationGroupId, part) restano fonti distinte con il proprio alias, nell'ordine di lettura del messaggio. Gli alias sono
// etichette di lettura del solo turno, mai identità persistite; nessuna normalizzazione e nessuna ricerca per somiglianza.

/// <summary>Fonte ammessa del turno con il suo alias e la provenienza/citazione letterale invariate rispetto al ledger.</summary>
public sealed record ChatAliasSource(
    string Alias, Guid SourceId, EvidenceSourceKind Provenance, Guid? MessageId, long? Sequence, Guid? DocumentId, Guid? DocumentVersionId, Guid? PreparationId,
    Guid? ArtifactId, Guid? BlockId, int Start, int End, string Quote);

/// <summary>Legame evidenza→fonte: ruolo, gruppo di citazione e parte appartengono alla citazione dell'evidenza.</summary>
public sealed record ChatAliasLink(string SourceAlias, EvidenceSourceRole Role, Guid CitationGroupId, int Part);

public sealed record ChatAliasEvidence(Guid EvidenceId, IReadOnlyList<ChatAliasLink> Links);

public sealed class ChatAliasCatalog
{
    public const string Version = "chat-alias-catalog-1";

    private readonly Dictionary<string, ChatAliasSource> _byAlias;
    private readonly Dictionary<Guid, string> _aliasOf;

    private ChatAliasCatalog(string snapshotHash, IReadOnlyList<ChatAliasSource> sources, IReadOnlyList<ChatAliasEvidence> evidence)
    {
        SnapshotHash = snapshotHash;
        Sources = sources;
        Evidence = evidence;
        _byAlias = sources.ToDictionary(s => s.Alias, StringComparer.Ordinal);
        _aliasOf = sources.ToDictionary(s => s.SourceId, s => s.Alias);
        SourceIds = new HashSet<Guid>(sources.Select(s => s.SourceId));
        CatalogHash = CanonicalJson.Sha256Hex(JsonSerializer.Serialize(new
        {
            version = Version,
            snapshot = snapshotHash,
            sources = sources.Select(s => new
            {
                alias = s.Alias, id = s.SourceId.ToString("D"), kind = s.Provenance.ToString(), message = s.MessageId, sequence = s.Sequence, document = s.DocumentId,
                documentVersion = s.DocumentVersionId, preparation = s.PreparationId, artifact = s.ArtifactId, block = s.BlockId, start = s.Start, end = s.End, quote = s.Quote,
            }),
            evidence = evidence.Select(e => new
            {
                id = e.EvidenceId.ToString("D"),
                links = e.Links.Select(l => new { alias = l.SourceAlias, role = l.Role.ToString(), group = l.CitationGroupId.ToString("D"), part = l.Part }),
            }),
        }));
    }

    public string SnapshotHash { get; }

    public IReadOnlyList<ChatAliasSource> Sources { get; }

    public IReadOnlyList<ChatAliasEvidence> Evidence { get; }

    /// <summary>Identificativi canonici delle sole fonti ammesse (le stesse che <see cref="DocumentChatContract.Validate"/> accetta).</summary>
    public IReadOnlySet<Guid> SourceIds { get; }

    /// <summary>SHA-256 della forma canonica del catalogo (alias, id, provenienza, range, citazione letterale e legami evidenza→fonte).</summary>
    public string CatalogHash { get; }

    /// <summary>
    /// Costruisce il catalogo dallo snapshot del turno, nell'ordine di lettura del messaggio (evidenze nell'ordine dello snapshot, fonti nell'ordine
    /// persistito). Una fonte non verificabile o lo stesso SourceId con posizione o citazione diversa è <see cref="DocumentWorkException.SourceSnapshotInvalid"/>.
    /// </summary>
    public static ChatAliasCatalog Build(ChatSnapshotSources snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sources = new List<ChatAliasSource>();
        var known = new Dictionary<Guid, ChatAliasSource>();
        var evidence = new List<ChatAliasEvidence>(snapshot.Evidence.Count);
        var evidenceIds = new HashSet<Guid>();
        foreach (var e in snapshot.Evidence)
        {
            if (!evidenceIds.Add(e.EvidenceId))
            {
                throw Invalid("evidenza duplicata nello snapshot del turno.");
            }

            if (PersistedSourceRules.Check(e.Sources) is { Count: > 0 } violations)
            {
                throw Invalid($"fonti dell'evidenza {e.EvidenceId:D} non valide: {string.Join(" | ", violations)}");
            }

            var links = new List<ChatAliasLink>(e.Sources.Count);
            foreach (var s in e.Sources)
            {
                var entry = new ChatAliasSource(
                    "F" + (sources.Count + 1), s.SourceId, s.Kind, s.MessageId, s.Sequence, s.DocumentId, s.DocumentVersionId, s.PreparationId, s.ArtifactId, s.BlockId,
                    s.Start, s.End, s.Quote);
                if (known.TryGetValue(s.SourceId, out var existing))
                {
                    // Stessa fonte citata da più evidenze: un solo alias, ma posizione e citazione devono coincidere esattamente.
                    if (existing with { Alias = entry.Alias } != entry)
                    {
                        throw Invalid($"la fonte {s.SourceId:D} ha posizioni o citazioni diverse in evidenze diverse.");
                    }

                    entry = existing;
                }
                else
                {
                    known[s.SourceId] = entry;
                    sources.Add(entry);
                }

                links.Add(new ChatAliasLink(entry.Alias, s.Role, s.CitationGroupId, s.Part));
            }

            evidence.Add(new ChatAliasEvidence(e.EvidenceId, links));
        }

        return new ChatAliasCatalog(snapshot.SnapshotHash, sources, evidence);
    }

    /// <summary>Risoluzione esatta (case-sensitive): null se l'alias non è nel catalogo del turno.</summary>
    public ChatAliasSource? Source(string alias) => _byAlias.GetValueOrDefault(alias);

    public string? AliasOf(Guid sourceId) => _aliasOf.GetValueOrDefault(sourceId);

    private static DocumentWorkException Invalid(string message) =>
        new(DocumentWorkException.SourceSnapshotInvalid, "Fonte originale non verificabile per il catalogo alias della chat: " + message);
}
