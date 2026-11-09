using System.Net;
using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;

namespace bProjects.IntegrationTests;

/// <summary>003 T010 (PG + server di embedding FINTO): costruzione idempotente dell'indice derivato, crash a metà, errore di servizio, readiness prima di ogni chiamata.</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RetrievalIndexBuilderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Texts =
    [
        "Il termine di pagamento è di trenta giorni dalla data fattura.",
        "La penale per ritardo è del cinque per cento.",
        "Il fornitore garantisce la disponibilità del servizio.",
        "   \n\t  ",   // solo spazi: non indicizzabile
        "Le comunicazioni avvengono tramite posta elettronica certificata.",
        "Il foro competente è quello di Milano.",
    ];

    [Fact]
    public async Task Building_a_ready_version_writes_one_coherent_entry_per_indexable_block_and_marks_the_state_ready()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();

        var result = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);

        Assert.Equal(RetrievalIndexStatus.Ready, result.Status);
        Assert.Equal(5, result.BlockCount);
        Assert.Equal(5, result.EntryCount);
        Assert.Equal(5, rig.ContentInputs);
        var state = await db.RetrievalIndexStates.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((RetrievalIndexStatus.Ready, 5, 5, (string?)null), (state.Status, state.BlockCount, state.EntryCount, state.ErrorCode));
        Assert.NotNull(state.BuiltAt);
        var entries = await db.RetrievalIndexEntries.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(5, entries.Count);
        Assert.All(entries, e => Assert.Equal(result.IndexConfigHash, e.IndexConfigHash));
        Assert.DoesNotContain(document.BlockIds[3], entries.Select(e => e.BlockId));   // blocco di soli spazi
        Assert.Equal(5L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\" WHERE vector_dims(\"Embedding\") = 1024 AND \"Tsv\" <> ''::tsvector"));
        // TextHash coerente con il blocco.
        var hashes = await db.DocumentBlocks.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.TextHash, Ct);
        Assert.All(entries, e => Assert.Equal(hashes[e.BlockId], e.TextHash));
    }

    [Fact]
    public async Task A_second_build_is_a_no_op_that_calls_no_service_and_creates_no_duplicates()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();
        await rig.Builder(db).BuildAsync(document.PreparationId, Ct);
        var before = rig.Server.Paths.Count;

        var again = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);

        Assert.Equal((RetrievalIndexStatus.Ready, 0), (again.Status, again.EmbeddedNow));
        Assert.Equal(before, rig.Server.Paths.Count);
        Assert.Equal(5L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexStates\""));
    }

    [Fact]
    public async Task A_service_failure_midway_leaves_partial_entries_but_never_a_ready_state_and_the_resume_completes_only_the_missing_without_duplicates()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();
        var contentBatches = 0;
        rig.Server.EmbeddingsOverride = inputs =>
        {
            if (inputs is ["probe"])
            {
                return FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([FakeEmbeddingServer.VectorOf("probe", 1024)]));
            }

            // Primo batch di contenuto (4 testi) riuscito; il secondo cade (crash a metà).
            return ++contentBatches == 1
                ? FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([.. inputs.Select(t => FakeEmbeddingServer.VectorOf(t, 1024))]))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        var failed = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);

        Assert.Equal((RetrievalIndexStatus.Failed, RetrievalException.EmbeddingUnavailable, 4, 5), (failed.Status, failed.ErrorCode, failed.EntryCount, failed.BlockCount));
        Assert.Equal(4L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal("Failed", await database.ScalarAsync("SELECT \"Status\" FROM \"RetrievalIndexStates\""));
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexStates\" WHERE \"Status\" = 'Ready'"));

        // Ripresa a servizio ripristinato: si calcola solo la voce mancante, nessun duplicato, stato Ready.
        rig.Server.EmbeddingsOverride = null;
        rig.Server.EmbeddingInputs.Clear();
        var resumed = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);

        Assert.Equal((RetrievalIndexStatus.Ready, 5, 1), (resumed.Status, resumed.EntryCount, resumed.EmbeddedNow));
        Assert.Equal(1, rig.ContentInputs);
        Assert.Equal(5L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal(5L, await database.ScalarAsync("SELECT count(DISTINCT \"BlockId\") FROM \"RetrievalIndexEntries\""));
        Assert.Equal("Ready", await database.ScalarAsync("SELECT \"Status\" FROM \"RetrievalIndexStates\""));
    }

    [Fact]
    public async Task An_unavailable_embedding_service_fails_the_state_with_a_typed_cause_and_leaves_no_entry()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();
        rig.Server.Models = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);   // la sonda di readiness fallisce: nessuna richiesta di contenuto

        var result = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);

        Assert.Equal((RetrievalIndexStatus.Failed, RetrievalException.EmbeddingUnavailable, 0), (result.Status, result.ErrorCode, result.EntryCount));
        Assert.Equal(0, rig.ContentInputs);
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal(("Failed", RetrievalException.EmbeddingUnavailable), (
            (string)(await database.ScalarAsync("SELECT \"Status\" FROM \"RetrievalIndexStates\""))!,
            (string)(await database.ScalarAsync("SELECT \"ErrorCode\" FROM \"RetrievalIndexStates\""))!));
    }

    [Fact]
    public async Task A_vector_of_the_wrong_dimension_is_a_typed_failure_with_no_partial_ready()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();
        rig.Server.EmbeddingsOverride = inputs => inputs is ["probe"]
            ? FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([FakeEmbeddingServer.VectorOf("probe", 1024)]))
            : FakeEmbeddingServer.Json(FakeEmbeddingServer.EmbeddingsBody([.. inputs.Select(t => FakeEmbeddingServer.VectorOf(t, 768))]));

        var result = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);

        Assert.Equal((RetrievalIndexStatus.Failed, RetrievalException.EmbeddingDimensionMismatch), (result.Status, result.ErrorCode));
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexStates\" WHERE \"Status\" = 'Ready'"));
    }

    [Fact]
    public async Task A_block_whose_hash_changed_is_stale_and_gets_reembedded_while_the_others_are_kept()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();
        await rig.Builder(db).BuildAsync(document.PreparationId, Ct);
        await database.ExecuteAsync($"UPDATE \"RetrievalIndexEntries\" SET \"TextHash\" = '{new string('9', 64)}' WHERE \"BlockId\" = '{document.BlockIds[1]}'");
        rig.Server.EmbeddingInputs.Clear();

        var result = await rig.Builder(db).BuildAsync(document.PreparationId, Ct);

        Assert.Equal((RetrievalIndexStatus.Ready, 1), (result.Status, result.EmbeddedNow));
        Assert.Equal(1, rig.ContentInputs);
        Assert.Equal(5L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal(0L, await database.ScalarAsync($"SELECT count(*) FROM \"RetrievalIndexEntries\" WHERE \"TextHash\" = '{new string('9', 64)}'"));
    }

    [Fact]
    public async Task Only_a_ready_preparation_can_be_indexed()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await database.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"Status\" = 'Incomplete' WHERE \"Id\" = '{document.PreparationId}'");
        await using var db = database.NewContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Builder(db).BuildAsync(document.PreparationId, Ct));
        Assert.Empty(rig.Server.Paths);
    }
}
