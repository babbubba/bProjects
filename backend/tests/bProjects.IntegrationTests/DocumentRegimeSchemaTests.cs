using bProjects.App.AI;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T046 (PG) — root del regime e ClassificationRecord append-only su PostgreSQL reale (migrazione DocumentRegime). Nessun AI, nessun DGX.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentRegimeSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Rejected(ParsingDatabase database, string sql, string state = "23514") =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql))).SqlState);

    private static ClassificationRecord Record(SeededParse seed, int ordinal, ClassificationOutcome outcome = ClassificationOutcome.AdmitExtended, int? plannedCalls = null) => new()
    {
        Id = Guid.NewGuid(), ProjectId = seed.Project, OwnerKind = ClassificationOwnerKind.DocumentPreparation, OwnerId = seed.Preparation, Ordinal = ordinal,
        Version = "document-regime-v2", InputHash = new string('1', 64), PolicyHash = new string('2', 64), Outcome = outcome, Reason = "preparation",
        ProvenanceJson = """{"window":"runtime"}""", UnknownReasonsJson = "[]", PlannedCalls = plannedCalls,
    };

    [Fact]
    public async Task Classification_record_roundtrips_is_unique_per_owner_ordinal_and_append_only()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();

        await using (var db = database.NewContext())
        {
            var record = Record(seed, 1);
            record.WindowTokens = 10_000;
            record.TokenQuality = "measured";
            record.UnknownReasonsJson = """["shape_floor"]""";
            db.Add(record);
            await db.SaveChangesAsync(Ct);
        }

        await using (var read = database.NewContext())
        {
            var stored = await read.ClassificationRecords.SingleAsync(Ct);
            Assert.Equal((ClassificationOutcome.AdmitExtended, "document-regime-v2", 10_000), (stored.Outcome, stored.Version, stored.WindowTokens));
            Assert.True(stored.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
            Assert.Equal("""["shape_floor"]""", await database.ScalarAsync<string>("SELECT \"UnknownReasonsJson\"::text FROM \"ClassificationRecords\""));
        }

        await using (var duplicate = database.NewContext())
        {
            duplicate.Add(Record(seed, 1));
            Assert.Equal("23505", (await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(Ct))).SqlState());
        }

        await using (var next = database.NewContext())
        {
            next.Add(Record(seed, 2));
            await next.SaveChangesAsync(Ct);
        }

        await Rejected(database, "UPDATE \"ClassificationRecords\" SET \"Reason\" = 'x'", "23001");
        await Rejected(database, "DELETE FROM \"ClassificationRecords\"", "23001");
    }

    [Fact]
    public async Task Classification_record_checks_reject_invalid_rows()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();

        // Ordinary richiede esattamente una chiamata iniziale (regola 7).
        await using (var ordinary = database.NewContext())
        {
            ordinary.Add(Record(seed, 1, ClassificationOutcome.AdmitOrdinary, plannedCalls: 2));
            Assert.Equal("23514", (await Assert.ThrowsAsync<DbUpdateException>(() => ordinary.SaveChangesAsync(Ct))).SqlState());
        }

        await using (var badHash = database.NewContext())
        {
            var record = Record(seed, 1);
            record.InputHash = "xyz";
            badHash.Add(record);
            Assert.Equal("23514", (await Assert.ThrowsAsync<DbUpdateException>(() => badHash.SaveChangesAsync(Ct))).SqlState());
        }

        await using (var badOwner = database.NewContext())
        {
            var record = Record(seed, 1);
            record.Ordinal = 0;
            badOwner.Add(record);
            Assert.Equal("23514", (await Assert.ThrowsAsync<DbUpdateException>(() => badOwner.SaveChangesAsync(Ct))).SqlState());
        }

        await using (var orphan = database.NewContext())
        {
            var record = Record(seed, 1);
            record.ProjectId = Guid.NewGuid();
            orphan.Add(record);
            Assert.Equal("23503", (await Assert.ThrowsAsync<DbUpdateException>(() => orphan.SaveChangesAsync(Ct))).SqlState());
        }
    }

    [Fact]
    public async Task Root_fields_are_nullable_for_legacy_rows_and_constrained_for_regime_rows()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        var p = $"WHERE \"Id\" = '{seed.Preparation}'";

        // Legacy: tutto nullo, nessun vincolo violato.
        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT count(*) FROM \"DocumentPreparations\" {p} AND (\"Regime\" IS NOT NULL OR \"PromotionCount\" IS NOT NULL OR \"ClassificationStatus\" IS NOT NULL)"));

        // Pending: Extended provvisorio senza InitialRegime.
        await database.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"ClassificationStatus\" = 'Pending', \"Regime\" = 'Extended', \"PromotionCount\" = 0 {p}");
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"Regime\" = 'Ordinary' {p}");
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"InitialRegime\" = 'Ordinary' {p}");

        // Classified Ordinary: richiede OrdinaryDeadlineAt; nessun valore fuori enumerazione.
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"ClassificationStatus\" = 'Classified', \"InitialRegime\" = 'Ordinary', \"Regime\" = 'Ordinary' {p}");
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"Regime\" = 'Standard' {p}");
        await database.ExecuteAsync(
            $"UPDATE \"DocumentPreparations\" SET \"ClassificationStatus\" = 'Classified', \"InitialRegime\" = 'Ordinary', \"Regime\" = 'Ordinary', \"OrdinaryDeadlineAt\" = \"RequestedAt\" + interval '180 seconds' {p}");

        // PromotionCount solo 0/1 e coerente con ricevuta temporale; nessun downgrade.
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"PromotionCount\" = 2 {p}");
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"PromotionCount\" = 1 {p}");
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"PromotionCount\" = 1, \"Regime\" = 'Extended', \"PromotedAt\" = now(), \"PromotedReason\" = 'queue_wait' {p}");
        await database.ExecuteAsync(
            $"UPDATE \"DocumentPreparations\" SET \"PromotionCount\" = 1, \"Regime\" = 'Extended', \"PromotedAt\" = now(), \"PromotedReason\" = 'additional_work' {p}");
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"Regime\" = 'Ordinary' {p}");

        // Cap finito: Effective <= Hard (già vincolo T115).
        await Rejected(database, $"UPDATE \"DocumentPreparations\" SET \"EffectiveDeadlineAt\" = \"HardDeadlineAt\" + interval '1 second' {p}");
    }

    [Fact]
    public async Task Chat_turns_and_runs_gain_nullable_root_columns_and_the_documentary_deadline_checks()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        foreach (var table in new[] { "ChatTurns", "ProcessingRuns" })
        {
            foreach (var column in new[] { "InitialRegime", "Regime", "ClassificationStatus", "PolicyHash", "HardDeadlineAt", "EffectiveDeadlineAt", "PromotionCount", "CallCount", "RetryOfId", "OrdinaryDeadlineAt" })
            {
                Assert.Equal("YES", await database.ScalarAsync<string>(
                    $"SELECT is_nullable FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'"));
            }

            foreach (var check in new[] { "Cap", "DeadlineIsEffective", "Regime", "Classification", "NoDowngrade", "Promotion", "PolicyHash", "CallCount" })
            {
                Assert.Equal(1, await database.ScalarAsync<int>($"SELECT count(*) FROM pg_constraint WHERE conname = 'CK_{table}_{check}'"));
            }

            Assert.Contains("DeadlineAt", await database.ScalarAsync<string>(
                $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_{table}_DeadlineIsEffective'"));
        }
    }
}
