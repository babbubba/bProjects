using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// RH-051 (ripresa dopo la 4ª run): verifica OFFLINE e di sola lettura, su una COPIA del database della run (ripristinata da un dump in un database scratch), che i WorkOutput committati dalla
/// gerarchia v11 siano riutilizzabili da un nuovo processo: si usa il <see cref="DocumentWorkStore.LookupAsync"/> di produzione (lo stesso del resume: nessuna scrittura, nessuna inferenza, nessun DGX).
/// Attivo solo con <c>BPROJECTS_RH051_RESUME_DB</c> (stringa di connessione al database scratch, mai al database della run originale né a quello di produzione); altrimenti ignorato.
/// </summary>
public sealed class Rh051ResumeReadinessTests
{
    private const string AliasSchema = "reconcile-alias-1";
    private const string ValidationV2 = "reconcile-validation-v2";

    [Fact]
    public async Task The_committed_outputs_of_the_failed_v11_run_are_reusable_and_only_the_failed_expand_batch_has_no_output()
    {
        var connectionString = Environment.GetEnvironmentVariable("BPROJECTS_RH051_RESUME_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), "BPROJECTS_RH051_RESUME_DB non impostata: verifica di ripresa su copia del database della run 4 non richiesta.");
        var ct = TestContext.Current.CancellationToken;

        Guid run;
        var batches = new List<(string BatchId, int Ordinal, string Stage, string InputHash)>();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM \"ProcessingRuns\" WHERE \"Kind\"::text = 'Analysis' ORDER BY \"RequestedAt\" DESC LIMIT 1", connection))
            {
                run = (Guid)(await command.ExecuteScalarAsync(ct))!;
            }

            await using var list = new NpgsqlCommand("SELECT \"BatchId\", \"Ordinal\", \"Stage\", \"InputHash\" FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r ORDER BY \"Ordinal\"", connection);
            list.Parameters.AddWithValue("r", run);
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                batches.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        Assert.NotEmpty(batches);
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        var store = new DocumentWorkStore(context, new CheckpointWriter(run, "resume-reader", 0, WorkOwnerKind.ProcessingRun));
        var expectation = new WorkLookupExpectation(AliasSchema, ValidationV2);

        var hits = new List<int>();
        var misses = new List<(int Ordinal, string Stage)>();
        foreach (var batch in batches)
        {
            var lookup = await store.LookupAsync(batch.BatchId, batch.InputHash, expectation, ct);   // eccezione = checkpoint incompatibile o payload non integro
            if (lookup.Kind == WorkLookupKind.Hit)
            {
                hits.Add(batch.Ordinal);
            }
            else
            {
                misses.Add((batch.Ordinal, batch.Stage));
            }
        }

        TestContext.Current.SendDiagnosticMessage($"RH-051 ripresa: batch pubblicati={batches.Count}; riutilizzabili (Hit)={hits.Count} [{string.Join(',', hits)}]; senza output (Miss)={string.Join(',', misses.Select(m => $"{m.Ordinal}:{m.Stage}"))}");
        Assert.Equal(30, hits.Count);
        Assert.Equal([(30, "expand")], misses);
    }
}
