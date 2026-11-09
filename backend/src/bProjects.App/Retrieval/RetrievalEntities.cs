using NpgsqlTypes;

namespace bProjects.App.Retrieval;

/// <summary>
/// Voce dell'indice derivato di recupero (003 T006): una riga per blocco figlio e per <c>IndexConfigHash</c>. PROIEZIONE ricostruibile dai soli
/// <c>DocumentBlock</c>: nessun dato esiste solo qui (FR-007, Costituzione IV); non duplica il testo del blocco (si legge da <c>DocumentBlock</c>).
/// <para>
/// La colonna <c>Embedding vector(1024)</c> (pgvector, L2-normalizzato) esiste nella tabella ma NON è mappata in EF (nessun pacchetto aggiuntivo): si scrive e
/// si interroga solo con SQL esplicito (<c>RetrievalIndexBuilder</c>, T010; <c>HybridSearch</c>, T013). Gli identificativi di progetto, documento e versione sono
/// denormalizzati dal blocco per filtrare PRIMA del ranking; i filtri di versione inclusa/esclusa usano lo snapshot, non una colonna di stato.
/// </para>
/// </summary>
public sealed class RetrievalIndexEntry
{
    public Guid Id { get; set; }

    /// <summary>FK logica a <c>DocumentBlock.Id</c> (immutabile); unica insieme a <see cref="IndexConfigHash"/>.</summary>
    public Guid BlockId { get; set; }

    /// <summary>Hash (64 esadecimali) di testo indicizzato, modello+artefatto+dimensione, configurazione FTS e versione parser/manifest (<c>IndexText.ConfigHash</c>).</summary>
    public required string IndexConfigHash { get; set; }

    public Guid ProjectId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid DocumentVersionId { get; set; }

    /// <summary><c>DocumentBlock.TextHash</c> al momento dell'indicizzazione; incoerenza con il blocco ⇒ voce stantia (da ricostruire).</summary>
    public required string TextHash { get; set; }

    /// <summary><c>to_tsvector('italian', testo indicizzato)</c>.</summary>
    public required NpgsqlTsVector Tsv { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum RetrievalIndexStatus
{
    Pending,
    Building,

    /// <summary>Solo se tutte le voci dei blocchi della versione sono presenti e coerenti (<c>EntryCount = BlockCount</c>).</summary>
    Ready,
    Failed,
}

/// <summary>Stato dell'indice per versione di documento e configurazione (003 T006): <c>Pending → Building → Ready | Failed</c>; <c>Failed → Building</c> è la riprova idempotente.</summary>
public sealed class RetrievalIndexState
{
    public Guid DocumentVersionId { get; set; }
    public required string IndexConfigHash { get; set; }
    public Guid ProjectId { get; set; }
    public RetrievalIndexStatus Status { get; set; }
    public int BlockCount { get; set; }
    public int EntryCount { get; set; }

    /// <summary>Motivo tipizzato (<c>RetrievalException</c>) se <see cref="RetrievalIndexStatus.Failed"/>, altrimenti null.</summary>
    public string? ErrorCode { get; set; }

    public DateTimeOffset? BuiltAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
