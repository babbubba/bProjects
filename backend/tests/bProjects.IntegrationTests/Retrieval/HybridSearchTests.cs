using System.Net;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T012-T013 (PG + embedding FINTO): ricerca lessicale e densa con i filtri nella stessa query PRIMA del ranking, tie-break per BlockId, errori tipizzati senza ritorno al lessicale.</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class HybridSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Texts =
    [
        "Il termine di pagamento è di trenta giorni dalla data fattura.",
        "I pagamenti avvengono entro il termine pattuito; il pagamento è dovuto alla scadenza.",
        "La penale per ritardo è del cinque per cento.",
        "Il foro competente è quello di Milano.",
    ];

    private sealed class Fixture : IAsyncDisposable
    {
        public required RetrievalDb Database { get; init; }

        public required RetrievalRig Rig { get; init; }

        public required Guid Project { get; init; }

        public required SeededDocument Document { get; init; }

        public required string Hash { get; init; }

        public RetrievalScope Scope(params SeededDocument[] documents) =>
            new(Project, Hash, [.. documents.Select(d => d.VersionId)], [.. documents.Select(d => d.PreparationId)]);

        public static async Task<Fixture> CreateAsync()
        {
            var database = await RetrievalDb.CreateAsync();
            var rig = new RetrievalRig();
            var project = await database.SeedProjectAsync();
            var document = await database.AddDocumentAsync(project, "Contratto", Texts);
            await using var db = database.NewContext();
            var result = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);
            return new() { Database = database, Rig = rig, Project = project, Document = document, Hash = result.IndexConfigHash };
        }

        public async Task<SeededDocument> AddIndexedAsync(Guid project, string name, string[] texts)
        {
            var document = await Database.AddDocumentAsync(project, name, texts);
            await using var db = Database.NewContext();
            await Rig.Builder(db).BuildAsync(document.PreparationId, Ct);
            return document;
        }

        public async ValueTask DisposeAsync()
        {
            Rig.Dispose();
            await Database.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lexical_search_uses_italian_stemming_and_returns_the_whole_ranking_with_one_based_ranks()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Database.NewContext();

        var ranking = await f.Rig.Search(db).LexicalAsync(f.Scope(f.Document), "pagamenti", pool: 50, Ct);

        Assert.False(ranking.EmptyQuery);
        Assert.Equal([1, 2], ranking.Blocks.Select(b => b.Rank));
        Assert.Equal(new HashSet<Guid> { f.Document.BlockIds[0], f.Document.BlockIds[1] }, ranking.Blocks.Select(b => b.BlockId).ToHashSet());
        Assert.DoesNotContain(f.Document.BlockIds[2], ranking.Blocks.Select(b => b.BlockId));
    }

    [Fact]
    public async Task A_sentence_query_matches_blocks_sharing_any_term_and_ranks_the_one_with_more_terms_first()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Database.NewContext();

        var ranking = await f.Rig.Search(db).LexicalAsync(f.Scope(f.Document), "Qual è il termine di pagamento in giorni dalla fattura?", pool: 50, Ct);

        Assert.Equal(f.Document.BlockIds[0], ranking.Blocks[0].BlockId);   // termine + pagamento + giorni + fattura
        Assert.Contains(f.Document.BlockIds[1], ranking.Blocks.Select(b => b.BlockId));   // solo alcuni termini: partecipa comunque
        Assert.DoesNotContain(f.Document.BlockIds[3], ranking.Blocks.Select(b => b.BlockId));
    }

    [Fact]
    public async Task The_pool_limits_the_ranking_and_equal_scores_are_ordered_by_ascending_block_id()
    {
        await using var f = await Fixture.CreateAsync();
        var twin = await f.AddIndexedAsync(f.Project, "Gemelli", ["Clausola identica di riservatezza.", "Clausola identica di riservatezza.", "Clausola identica di riservatezza."]);
        await using var db = f.Database.NewContext();

        var ranking = await f.Rig.Search(db).LexicalAsync(f.Scope(twin), "riservatezza", pool: 50, Ct);

        Assert.Equal(twin.BlockIds.Order(), ranking.Blocks.Select(b => b.BlockId));
        var limited = await f.Rig.Search(db).LexicalAsync(f.Scope(twin), "riservatezza", pool: 2, Ct);
        Assert.Equal(twin.BlockIds.Order().Take(2), limited.Blocks.Select(b => b.BlockId));
    }

    [Fact]
    public async Task An_empty_or_stopword_only_query_gives_an_explicit_empty_result_not_an_error()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Database.NewContext();
        var search = f.Rig.Search(db);

        var empty = await search.LexicalAsync(f.Scope(f.Document), "   ", 50, Ct);
        var stopwords = await search.LexicalAsync(f.Scope(f.Document), "il la di", 50, Ct);

        Assert.Empty(empty.Blocks);
        Assert.True(empty.EmptyQuery);
        Assert.Empty(stopwords.Blocks);
        Assert.True(stopwords.EmptyQuery);
    }

    [Fact]
    public async Task Filters_apply_before_ranking_a_document_outside_the_scope_or_in_another_project_never_competes()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Database.NewContext();
        var before = await f.Rig.Search(db).LexicalAsync(f.Scope(f.Document), "pagamento", 50, Ct);
        var beforeDense = await f.Rig.Search(db).DenseAsync(f.Scope(f.Document), Texts[0], 50, Ct);

        // Stesso testo in una versione non inclusa e in un altro progetto (con più occorrenze: avrebbe rango migliore).
        var excluded = await f.AddIndexedAsync(f.Project, "Versione esclusa", ["pagamento pagamento pagamento pagamento", Texts[0]]);
        var otherProject = await f.Database.SeedProjectAsync("Altro");
        var foreign = await f.AddIndexedAsync(otherProject, "Altro progetto", ["pagamento pagamento pagamento pagamento", Texts[0]]);

        var after = await f.Rig.Search(db).LexicalAsync(f.Scope(f.Document), "pagamento", 50, Ct);
        var afterDense = await f.Rig.Search(db).DenseAsync(f.Scope(f.Document), Texts[0], 50, Ct);

        Assert.Equal(before.Blocks, after.Blocks);   // stessi blocchi e stessi ranghi: l'esclusione precede il ranking
        Assert.Equal(beforeDense.Blocks, afterDense.Blocks);
        var all = after.Blocks.Concat(afterDense.Blocks).Select(b => b.BlockId).ToHashSet();
        Assert.Empty(all.Intersect(excluded.BlockIds.Concat(foreign.BlockIds)));
        // Con il perimetro allargato la versione prima esclusa compete, a prova che il filtro (e non l'assenza) la teneva fuori.
        var widened = await f.Rig.Search(db).LexicalAsync(f.Scope(f.Document, excluded), "pagamento", 50, Ct);
        Assert.Contains(excluded.BlockIds[0], widened.Blocks.Select(b => b.BlockId));
        // Un perimetro con progetto diverso non restituisce i blocchi del primo.
        var wrongProject = f.Scope(f.Document) with { ProjectId = otherProject };
        Assert.Empty((await f.Rig.Search(db).LexicalAsync(wrongProject, "pagamento", 50, Ct)).Blocks);
        Assert.Empty((await f.Rig.Search(db).DenseAsync(wrongProject, Texts[0], 50, Ct)).Blocks);
    }

    [Fact]
    public async Task Dense_search_ranks_the_nearest_block_first_by_cosine_distance_and_covers_the_whole_scope()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Database.NewContext();

        var ranking = await f.Rig.Search(db).DenseAsync(f.Scope(f.Document), Texts[2], pool: 50, Ct);

        Assert.Equal(f.Document.BlockIds[2], ranking.Blocks[0].BlockId);   // vettore finto deterministico: testo identico = distanza 0
        Assert.Equal([1, 2, 3, 4], ranking.Blocks.Select(b => b.Rank));
        Assert.Equal(4, ranking.Blocks.Select(b => b.BlockId).Distinct().Count());
        Assert.Single((await f.Rig.Search(db).DenseAsync(f.Scope(f.Document), Texts[2], pool: 1, Ct)).Blocks);
    }

    [Fact]
    public async Task A_stale_entry_whose_hash_no_longer_matches_the_block_is_never_returned()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Database.ExecuteAsync($"UPDATE \"RetrievalIndexEntries\" SET \"TextHash\" = '{new string('9', 64)}' WHERE \"BlockId\" = '{f.Document.BlockIds[0]}'");
        await using var db = f.Database.NewContext();

        var lexical = await f.Rig.Search(db).LexicalAsync(f.Scope(f.Document), "pagamento", 50, Ct);
        var dense = await f.Rig.Search(db).DenseAsync(f.Scope(f.Document), Texts[0], 50, Ct);

        Assert.DoesNotContain(f.Document.BlockIds[0], lexical.Blocks.Select(b => b.BlockId));
        Assert.DoesNotContain(f.Document.BlockIds[0], dense.Blocks.Select(b => b.BlockId));
    }

    [Fact]
    public async Task An_unavailable_query_embedding_raises_the_typed_error_and_never_falls_back_to_lexical_only()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Database.NewContext();
        f.Rig.Server.EmbeddingsOverride = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var dense = await Assert.ThrowsAsync<RetrievalException>(() => f.Rig.Search(db).DenseAsync(f.Scope(f.Document), "pagamento", 50, Ct));
        var hybrid = await Assert.ThrowsAsync<RetrievalException>(() => f.Rig.Search(db).SearchAsync(f.Scope(f.Document), "pagamento", 50, Ct));

        Assert.Equal(RetrievalException.EmbeddingUnavailable, dense.Code);
        Assert.Equal(RetrievalException.EmbeddingUnavailable, hybrid.Code);   // la ricerca ibrida non restituisce il solo lessicale

        // Servizio non pronto (readiness): errore tipizzato senza alcuna richiesta di contenuto.
        f.Rig.Readiness.Invalidate();
        f.Rig.Server.EmbeddingsOverride = null;
        f.Rig.Server.Models = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        f.Rig.Server.EmbeddingInputs.Clear();
        var notReady = await Assert.ThrowsAsync<RetrievalException>(() => f.Rig.Search(db).DenseAsync(f.Scope(f.Document), "pagamento", 50, Ct));
        Assert.Equal(RetrievalException.EmbeddingUnavailable, notReady.Code);
        Assert.Equal(0, f.Rig.ContentInputs);
    }

    [Fact]
    public async Task The_hybrid_search_fuses_both_signals_over_the_same_filtered_blocks()
    {
        await using var f = await Fixture.CreateAsync();
        await using var db = f.Database.NewContext();

        var result = await f.Rig.Search(db).SearchAsync(f.Scope(f.Document), Texts[0], 50, Ct);

        Assert.Equal(f.Document.BlockIds[0], result.Fused[0].BlockId);   // primo in entrambi i segnali
        Assert.Equal((1, 1), (result.Fused[0].LexicalRank, result.Fused[0].DenseRank));
        Assert.Equal(Enumerable.Range(1, result.Fused.Count), result.Fused.Select(x => x.RrfRank));
        Assert.Equal(4, result.Fused.Count);   // i 4 blocchi compaiono nel denso, il lessicale ne ha solo alcuni
    }
}
