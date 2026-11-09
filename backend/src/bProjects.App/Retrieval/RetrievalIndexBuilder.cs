using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace bProjects.App.Retrieval;

/// <summary>Esito di una costruzione: stato finale dell'indice della versione per la configurazione, conteggi e causa tipizzata se <see cref="RetrievalIndexStatus.Failed"/>.</summary>
public sealed record RetrievalIndexBuildResult(
    Guid DocumentVersionId, string IndexConfigHash, RetrievalIndexStatus Status, int BlockCount, int EntryCount, string? ErrorCode, int EmbeddedNow);

/// <summary>
/// Costruzione dell'indice derivato (003 T010, FR-004, FR-007, FR-020, R-5). Dai <c>DocumentBlock</c> di una preparazione <c>Ready</c> calcola il testo
/// <c>index-text-1</c>, il <c>tsvector</c> italiano e l'embedding (batch fisso) e scrive le <see cref="RetrievalIndexEntry"/> con upsert per
/// <c>(BlockId, IndexConfigHash)</c>: ogni passo è idempotente e una ripresa dopo un crash rilegge le voci già coerenti (stesso <c>TextHash</c>) e calcola solo
/// quelle mancanti o stantie, senza duplicati. Lo stato diventa <c>Ready</c> SOLO a fine lavoro con una voce coerente per ogni blocco indicizzabile; un errore del
/// servizio di embedding lo porta a <c>Failed</c> con causa tipizzata (mai <c>Ready</c> parziale, nessun ritorno al solo lessicale). La readiness (T008) è
/// verificata prima di ogni richiesta. Non tocca la macchina a stati delle fonti della 002: legge soltanto.
/// <para>
/// Sono indicizzabili i blocchi con testo non vuoto dopo <c>index-text-1</c> (un blocco di soli spazi non ha contenuto ricercabile e non è embeddabile);
/// <c>BlockCount</c> conta solo questi. Le voci sono per blocco: blocchi di preparazioni diverse della stessa versione non si confondono (la ricerca filtra per preparazione).
/// </para>
/// </summary>
public sealed class RetrievalIndexBuilder(
    AppDbContext db, EmbeddingClient embedding, RetrievalModelReadiness readiness, RetrievalOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Configurazione d'indice della preparazione: versione del parser inclusa (A-5, FR-007).</summary>
    public string ConfigHashOf(DocumentPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return IndexText.ConfigHash(options, preparation.ParserVersion
            ?? throw new InvalidOperationException("La preparazione non ha una versione del parser: non è Ready."));
    }

    public async Task<RetrievalIndexBuildResult> BuildAsync(Guid preparationId, CancellationToken ct = default)
    {
        var preparation = await db.DocumentPreparations.AsNoTracking().SingleOrDefaultAsync(p => p.Id == preparationId, ct)
            ?? throw new InvalidOperationException("Preparazione inesistente.");
        if (preparation.Status != PreparationStatus.Ready)
        {
            throw new InvalidOperationException("L'indice si costruisce solo da una preparazione Ready.");
        }

        var version = await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == preparation.DocumentVersionId, ct);
        var hash = ConfigHashOf(preparation);

        var blocks = (await (from b in db.DocumentBlocks.AsNoTracking()
                             join a in db.ParsedArtifacts.AsNoTracking() on b.ArtifactId equals a.Id
                             where a.PreparationId == preparationId
                             orderby b.Ordinal
                             select new { b.Id, b.ProjectId, b.CanonicalText, b.TextHash }).ToListAsync(ct))
            .Select(b => (b.Id, b.ProjectId, b.TextHash, Text: IndexText.Of(b.CanonicalText)))
            .Where(b => b.Text.Length > 0)
            .ToList();

        var existing = await db.RetrievalIndexEntries.AsNoTracking()
            .Where(e => e.DocumentVersionId == version.Id && e.IndexConfigHash == hash)
            .Select(e => new { e.BlockId, e.TextHash }).ToListAsync(ct);
        var coherent = existing.ToDictionary(e => e.BlockId, e => e.TextHash);
        var missing = blocks.Where(b => !coherent.TryGetValue(b.Id, out var h) || h != b.TextHash).ToList();
        var already = blocks.Count - missing.Count;

        var state = await db.RetrievalIndexStates.AsNoTracking().SingleOrDefaultAsync(s => s.DocumentVersionId == version.Id && s.IndexConfigHash == hash, ct);
        if (state is { Status: RetrievalIndexStatus.Ready } && missing.Count == 0 && state.BlockCount == blocks.Count)
        {
            return new(version.Id, hash, RetrievalIndexStatus.Ready, blocks.Count, blocks.Count, null, 0);
        }

        await SetStateAsync(version, hash, RetrievalIndexStatus.Building, blocks.Count, already, null, ct);
        var written = 0;
        try
        {
            foreach (var chunk in missing.Chunk(EmbeddingClient.BatchSize))
            {
                await readiness.EnsureEmbeddingReadyAsync(ct);
                var vectors = await embedding.EmbedAsync([.. chunk.Select(b => b.Text)], ct);
                for (var i = 0; i < chunk.Length; i++)
                {
                    await UpsertAsync(version, hash, chunk[i].Id, chunk[i].ProjectId, chunk[i].TextHash, chunk[i].Text, vectors[i], ct);
                    written++;
                }

                await SetStateAsync(version, hash, RetrievalIndexStatus.Building, blocks.Count, already + written, null, ct);
            }
        }
        catch (RetrievalException ex)
        {
            await SetStateAsync(version, hash, RetrievalIndexStatus.Failed, blocks.Count, already + written, ex.Code, ct);
            return new(version.Id, hash, RetrievalIndexStatus.Failed, blocks.Count, already + written, ex.Code, written);
        }

        await SetStateAsync(version, hash, RetrievalIndexStatus.Ready, blocks.Count, blocks.Count, null, ct);
        return new(version.Id, hash, RetrievalIndexStatus.Ready, blocks.Count, blocks.Count, null, written);
    }

    private Task<int> UpsertAsync(
        DocumentVersion version, string hash, Guid blockId, Guid projectId, string textHash, string text, float[] vector, CancellationToken ct) =>
        RetrievalSql.ExecuteAsync(db, """
            INSERT INTO "RetrievalIndexEntries"
                ("Id","BlockId","IndexConfigHash","ProjectId","DocumentId","DocumentVersionId","TextHash","Tsv","Embedding","CreatedAt")
            VALUES (@id, @block, @hash, @project, @document, @version, @textHash, to_tsvector('italian', @text), @embedding::vector(1024), now())
            ON CONFLICT ("BlockId","IndexConfigHash") DO UPDATE SET
                "TextHash" = EXCLUDED."TextHash", "Tsv" = EXCLUDED."Tsv", "Embedding" = EXCLUDED."Embedding"
            """, ct,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
            new NpgsqlParameter("block", NpgsqlDbType.Uuid) { Value = blockId },
            new NpgsqlParameter("hash", NpgsqlDbType.Varchar) { Value = hash },
            new NpgsqlParameter("project", NpgsqlDbType.Uuid) { Value = projectId },
            new NpgsqlParameter("document", NpgsqlDbType.Uuid) { Value = version.DocumentId },
            new NpgsqlParameter("version", NpgsqlDbType.Uuid) { Value = version.Id },
            new NpgsqlParameter("textHash", NpgsqlDbType.Varchar) { Value = textHash },
            new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = text },
            new NpgsqlParameter("embedding", NpgsqlDbType.Text) { Value = RetrievalSql.VectorLiteral(vector) });

    private Task<int> SetStateAsync(
        DocumentVersion version, string hash, RetrievalIndexStatus status, int blockCount, int entryCount, string? errorCode, CancellationToken ct) =>
        RetrievalSql.ExecuteAsync(db, """
            INSERT INTO "RetrievalIndexStates"
                ("DocumentVersionId","IndexConfigHash","ProjectId","Status","BlockCount","EntryCount","ErrorCode","BuiltAt","UpdatedAt")
            VALUES (@version, @hash, @project, @status, @blocks, @entries, @error, @built, @now)
            ON CONFLICT ("DocumentVersionId","IndexConfigHash") DO UPDATE SET
                "Status" = EXCLUDED."Status", "BlockCount" = EXCLUDED."BlockCount", "EntryCount" = EXCLUDED."EntryCount",
                "ErrorCode" = EXCLUDED."ErrorCode", "BuiltAt" = EXCLUDED."BuiltAt", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct,
            new NpgsqlParameter("version", NpgsqlDbType.Uuid) { Value = version.Id },
            new NpgsqlParameter("hash", NpgsqlDbType.Varchar) { Value = hash },
            new NpgsqlParameter("project", NpgsqlDbType.Uuid) { Value = version.ProjectId },
            new NpgsqlParameter("status", NpgsqlDbType.Varchar) { Value = status.ToString() },
            new NpgsqlParameter("blocks", NpgsqlDbType.Integer) { Value = blockCount },
            new NpgsqlParameter("entries", NpgsqlDbType.Integer) { Value = entryCount },
            new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = (object?)errorCode ?? DBNull.Value },
            new NpgsqlParameter("built", NpgsqlDbType.TimestampTz) { Value = status == RetrievalIndexStatus.Ready ? _time.GetUtcNow() : DBNull.Value },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = _time.GetUtcNow() });
}
