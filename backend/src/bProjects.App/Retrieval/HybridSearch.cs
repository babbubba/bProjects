using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.App.Retrieval;

/// <summary>
/// Perimetro di una ricerca (FR-005): progetto, configurazione d'indice e le versioni/preparazioni INCLUSE dallo snapshot. Tutto ciò che non vi compare (versioni
/// escluse dal PM, non incluse nello snapshot, altri progetti) non compete mai al ranking: il perimetro è un filtro <c>WHERE</c> della stessa query.
/// </summary>
public sealed record RetrievalScope(Guid ProjectId, string IndexConfigHash, IReadOnlyCollection<Guid> DocumentVersionIds, IReadOnlyCollection<Guid> PreparationIds)
{
    /// <summary>Perimetro dalle righe dello snapshot: solo documenti inclusi e Ready, con la versione e la preparazione fissate alla richiesta.</summary>
    public static async Task<RetrievalScope> FromSnapshotAsync(AppDbContext db, Guid snapshotId, string indexConfigHash, CancellationToken ct = default)
    {
        var snapshot = await db.SourceSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId, ct);
        var items = await db.SourceSnapshotItems.AsNoTracking()
            .Where(i => i.SnapshotId == snapshotId && i.Included && i.Availability == SourceAvailability.Ready && i.DocumentVersionId != null && i.PreparationId != null)
            .ToListAsync(ct);
        return new(snapshot.ProjectId, indexConfigHash, [.. items.Select(i => i.DocumentVersionId!.Value).Distinct()], [.. items.Select(i => i.PreparationId!.Value).Distinct()]);
    }
}

/// <summary>Classifica di un segnale: blocchi in ordine con rango 1-based; <see cref="EmptyQuery"/> = interrogazione vuota o di sole stopword (risultato vuoto esplicito, non errore).</summary>
public sealed record SearchRanking(IReadOnlyList<RankedBlock> Blocks, bool EmptyQuery);

/// <summary>Esito completo per un'interrogazione: i due segnali e la fusione RRF (nessun riordino in M1).</summary>
public sealed record HybridSearchResult(SearchRanking Lexical, SearchRanking Dense, IReadOnlyList<FusedBlock> Fused);

/// <summary>
/// Ricerca ibrida sull'indice derivato (003 T012-T013, FR-005, FR-008, FR-020). Lessicale: <c>websearch_to_tsquery('italian')</c> (normalizzazione, stemming, stopword) con i termini
/// in OR (le interrogazioni dei Gap sono frasi: con l'AND di default quasi nessun blocco le soddisfa tutte) e ordinamento <c>ts_rank_cd</c>, che premia i blocchi con più termini; scelta da
/// confermare con la misura di T019 (R-1). Densa:
/// distanza coseno <c>&lt;=&gt;</c> con scansione esatta. In entrambe i filtri (progetto, configurazione, versioni e preparazioni incluse, voce coerente con il blocco)
/// stanno nella <c>WHERE</c> PRIMA dell'ordinamento: i candidati esclusi non influenzano i ranghi dei restanti. Parità risolta per <c>BlockId</c> ascendente.
/// L'embedding della query non disponibile lancia l'errore tipizzato (<c>embedding_unavailable</c> ecc.) e NON ripiega sul solo lessicale (D-3).
/// </summary>
public sealed class HybridSearch(AppDbContext db, EmbeddingClient embedding, RetrievalModelReadiness readiness)
{
    private const string FromWhere = """
        FROM "RetrievalIndexEntries" e
        JOIN "DocumentBlocks" b ON b."Id" = e."BlockId"
        JOIN "ParsedArtifacts" a ON a."Id" = b."ArtifactId"
        """;

    private const string Filters = """
        WHERE e."ProjectId" = @project AND b."ProjectId" = @project AND e."IndexConfigHash" = @hash
          AND e."DocumentVersionId" = ANY(@versions) AND a."PreparationId" = ANY(@preparations)
          AND e."TextHash" = b."TextHash"
        """;

    /// <summary>Classifica lessicale fino a <paramref name="pool"/> blocchi; interrogazione vuota o di sole stopword ⇒ lista vuota con <see cref="SearchRanking.EmptyQuery"/>.</summary>
    public async Task<SearchRanking> LexicalAsync(RetrievalScope scope, string query, int pool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(pool, 1);
        if (string.IsNullOrWhiteSpace(query) || scope.DocumentVersionIds.Count == 0)
        {
            return new([], string.IsNullOrWhiteSpace(query));
        }

        var sql = $"""
            SELECT e."BlockId" {FromWhere}
            CROSS JOIN LATERAL (SELECT replace(websearch_to_tsquery('italian', @query)::text, ' & ', ' | ')::tsquery AS q) s
            {Filters} AND numnode(s.q) > 0 AND e."Tsv" @@ s.q
            ORDER BY ts_rank_cd(e."Tsv", s.q) DESC, e."BlockId" ASC
            LIMIT @pool
            """;
        var ids = await RetrievalSql.QueryAsync(db, sql, r => r.GetGuid(0), ct,
            [.. Common(scope), new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = query }, new NpgsqlParameter("pool", NpgsqlDbType.Integer) { Value = pool }]);
        if (ids.Count == 0)
        {
            var onlyStopwords = await RetrievalSql.QueryAsync(db, "SELECT numnode(websearch_to_tsquery('italian', @query)) = 0", r => r.GetBoolean(0), ct,
                new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = query });
            return new([], onlyStopwords.Count == 1 && onlyStopwords[0]);
        }

        return new([.. ids.Select((id, index) => new RankedBlock(id, index + 1))], false);
    }

    /// <summary>
    /// Classifica densa fino a <paramref name="pool"/> blocchi. Verifica la readiness dell'embedding e calcola l'embedding della query; ogni errore è tipizzato e nessun
    /// risultato viene restituito.
    /// </summary>
    public async Task<SearchRanking> DenseAsync(RetrievalScope scope, string query, int pool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(pool, 1);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new([], true);
        }

        await readiness.EnsureEmbeddingReadyAsync(ct);
        var vector = await embedding.EmbedAsync(IndexText.Of(query), ct);
        if (scope.DocumentVersionIds.Count == 0)
        {
            return new([], false);
        }

        var sql = $"""
            SELECT e."BlockId" {FromWhere}
            {Filters}
            ORDER BY e."Embedding" <=> @query::vector(1024) ASC, e."BlockId" ASC
            LIMIT @pool
            """;
        var ids = await RetrievalSql.QueryAsync(db, sql, r => r.GetGuid(0), ct,
            [.. Common(scope), new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = RetrievalSql.VectorLiteral(vector) }, new NpgsqlParameter("pool", NpgsqlDbType.Integer) { Value = pool }]);
        return new([.. ids.Select((id, index) => new RankedBlock(id, index + 1))], false);
    }

    /// <summary>Entrambi i segnali sulle stesse condizioni e fusione RRF. Un errore di embedding si propaga (nessun ritorno al solo lessicale).</summary>
    public async Task<HybridSearchResult> SearchAsync(RetrievalScope scope, string query, int pool, CancellationToken ct = default)
    {
        var lexical = await LexicalAsync(scope, query, pool, ct);
        var dense = await DenseAsync(scope, query, pool, ct);
        return new(lexical, dense, Rrf.Fuse(lexical.Blocks, dense.Blocks));
    }

    private static NpgsqlParameter[] Common(RetrievalScope scope) =>
    [
        new("project", NpgsqlDbType.Uuid) { Value = scope.ProjectId },
        new("hash", NpgsqlDbType.Varchar) { Value = scope.IndexConfigHash },
        new("versions", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = scope.DocumentVersionIds.ToArray() },
        new("preparations", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = scope.PreparationIds.ToArray() },
    ];
}
