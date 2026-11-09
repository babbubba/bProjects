using bProjects.App.Retrieval;
using Microsoft.EntityFrameworkCore;

namespace bProjects.IntegrationTests;

/// <summary>003 T011 (PG): invalidazione per cambio di configurazione, cancellazione completa e ricostruzione dai soli blocchi (SC-006), indice corrotto.</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RetrievalIndexMaintenanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Texts =
    [
        "Il termine di pagamento è di trenta giorni dalla data fattura.",
        "La penale per ritardo è del cinque per cento.",
        "Il fornitore garantisce la disponibilità del servizio.",
    ];

    private static async Task<string> AuthoritativeAsync(RetrievalDb database) => string.Join('\n', await Task.WhenAll(
        new[] { "Projects", "ProjectDocuments", "DocumentVersions", "DocumentPreparations", "ParsedArtifacts", "DocumentBlocks", "EvidenceDocumentStates" }
            .Select(async t => t + "=" + (string)(await database.ScalarAsync($"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)::text, '[]') FROM \"{t}\" t"))!)));

    [Fact]
    public async Task Clearing_and_rebuilding_from_the_blocks_alone_reproduces_the_same_indexed_passages_and_loses_no_authoritative_data()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        await database.AddDocumentAsync(project, "Contratto", Texts);
        await database.AddDocumentAsync(project, "Allegato", ["Il canone annuo è fissato in euro.", "La durata è di tre anni."]);
        await using var db = database.NewContext();
        var maintenance = new RetrievalIndexMaintenance(db, rig.Builder(db));
        var first = await maintenance.RebuildAsync(clear: false, Ct);
        Assert.Equal((2, 2, 0), (first.Preparations, first.Ready, first.Failed));
        var preparation = await db.DocumentPreparations.AsNoTracking().FirstAsync(Ct);
        var hash = rig.Builder(db).ConfigHashOf(preparation);
        var before = await maintenance.IndexedPassagesAsync(hash, Ct);
        var authoritativeBefore = await AuthoritativeAsync(database);
        Assert.Equal(5, before.Count);

        var rebuilt = await maintenance.RebuildAsync(clear: true, Ct);

        Assert.Equal((2, 2, 0, 5), (rebuilt.Preparations, rebuilt.Ready, rebuilt.Failed, rebuilt.EntriesRemoved));
        Assert.Equal(before, await maintenance.IndexedPassagesAsync(hash, Ct));   // SC-006: stesso insieme di BlockId + TextHash
        Assert.Equal(authoritativeBefore, await AuthoritativeAsync(database));     // nessun dato autorevole perso o toccato
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexStates\" WHERE \"Status\" = 'Ready'"));
    }

    [Fact]
    public async Task A_corrupted_index_is_repaired_by_an_ordinary_rebuild_without_clearing()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();
        var maintenance = new RetrievalIndexMaintenance(db, rig.Builder(db));
        await maintenance.RebuildAsync(clear: false, Ct);
        var hash = (await db.RetrievalIndexEntries.AsNoTracking().FirstAsync(Ct)).IndexConfigHash;
        var before = await maintenance.IndexedPassagesAsync(hash, Ct);

        // Corruzione: una voce cancellata, una con hash stantio; lo stato resta Ready (era vero prima del danno).
        await database.ExecuteAsync($"DELETE FROM \"RetrievalIndexEntries\" WHERE \"BlockId\" = '{document.BlockIds[0]}'");
        await database.ExecuteAsync($"UPDATE \"RetrievalIndexEntries\" SET \"TextHash\" = '{new string('9', 64)}' WHERE \"BlockId\" = '{document.BlockIds[1]}'");
        rig.Server.EmbeddingInputs.Clear();

        var summary = await maintenance.RebuildAsync(clear: false, Ct);

        Assert.Equal((1, 1, 0), (summary.Preparations, summary.Ready, summary.Failed));
        Assert.Equal(2, rig.ContentInputs);   // solo le due voci danneggiate
        Assert.Equal(before, await maintenance.IndexedPassagesAsync(hash, Ct));
    }

    [Fact]
    public async Task A_changed_index_configuration_creates_new_entries_and_the_old_ones_can_be_purged_without_touching_the_new()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using var db = database.NewContext();
        var builder = rig.Builder(db);
        var old = await builder.BuildAsync(document.PreparationId, Ct);

        // Cambio di versione del parser (fa parte dell'IndexConfigHash): nuova configurazione, nuove voci; le vecchie restano finché eliminate.
        await database.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"ParserVersion\" = 'parser-2' WHERE \"Id\" = '{document.PreparationId}'");
        await using var db2 = database.NewContext();
        var changed = await rig.Builder(db2).BuildAsync(document.PreparationId, Ct);

        Assert.NotEqual(old.IndexConfigHash, changed.IndexConfigHash);
        Assert.Equal(6L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(DISTINCT \"IndexConfigHash\") FROM \"RetrievalIndexEntries\""));

        var purged = await new RetrievalIndexMaintenance(db2, rig.Builder(db2)).PurgeOtherConfigurationsAsync(changed.IndexConfigHash, Ct);

        Assert.Equal(3, purged);
        Assert.Equal(3L, await database.ScalarAsync($"SELECT count(*) FROM \"RetrievalIndexEntries\" WHERE \"IndexConfigHash\" = '{changed.IndexConfigHash}'"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexStates\""));
    }

    [Fact]
    public async Task The_rebuild_command_refuses_to_operate_while_retrieval_is_disabled_and_touches_nothing()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var document = await database.AddDocumentAsync(project, "Contratto", Texts);
        await using (var db = database.NewContext())
        {
            await rig.Builder(db).BuildAsync(document.PreparationId, Ct);
        }

        var entry = typeof(Program).Assembly.EntryPoint!;
        var previous = Environment.ExitCode;
        try
        {
            entry.Invoke(null, [new[]
            {
                "rebuild-retrieval-index", "clear",
                "--Auth:SigningKey=" + TestApp.SigningKey,
                "--ConnectionStrings:AppDb=" + database.ConnectionString,
                "--Ai:Endpoint=http://127.0.0.1:9/v1/", "--Ai:ModelId=test-model", "--Ai:ApiKey=test-key",
            }]);
            Assert.Equal(1, Environment.ExitCode);   // Retrieval:Enabled=false (default): rifiuto esplicito
        }
        finally
        {
            Environment.ExitCode = previous;
        }

        Assert.Equal(3L, await database.ScalarAsync("SELECT count(*) FROM \"RetrievalIndexEntries\""));   // nemmeno --clear ha agito
    }
}
