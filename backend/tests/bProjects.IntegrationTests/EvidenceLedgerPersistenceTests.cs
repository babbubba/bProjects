using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T055/T056 — persistenza del registro su PostgreSQL reale: vincoli e unicità dello schema, numerazione delle versioni, promozione
/// automatica della prima versione valida e concorrenza ottimistica della promozione, append-only applicativo, migrazione additiva in
/// discesa e risalita con i dati esistenti invariati (poi il backfill li estrae). Nessun modello: estrazioni con l'uscita deterministica
/// dei test attraverso claim e commit reali.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class EvidenceLedgerPersistenceTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261028220808_InferenceCalls";

    private readonly PostgresDatabaseFixture _database = new();
    private ApiHarness? _app;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (TestEnvironment.PostgresAdminConnectionString is not null)
        {
            _app = await ApiHarness.StartAsync(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private ApiHarness App
    {
        get
        {
            TestEnvironment.RequirePostgres();
            return _app!;
        }
    }

    [Fact]
    public async Task One_open_job_per_message_and_the_state_checks_are_enforced_by_the_database()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il portale gestisce i resi.");
        var job = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionJobs" """);

        // Un secondo lavoro aperto per lo stesso messaggio viola l'indice univoco filtrato.
        await AssertViolation("23505", """
            INSERT INTO "EvidenceExtractionJobs" ("Id","ProjectId","ConversationId","MessageId","MessageSequence","Reason","Status","RequestedAt",
                "DeadlineAt","AttemptCount","MaxAttempts","FencingToken","ExtractorConfig")
            SELECT gen_random_uuid(),"ProjectId","ConversationId","MessageId","MessageSequence",'Reanalysis','Queued',now(),now() + interval '1 hour',0,3,0,'{}'
            FROM "EvidenceExtractionJobs" WHERE "Id" = @j
            """, ("j", job));

        // Completed senza risultato e Failed senza codice non sono stati ammessi.
        await AssertViolation("23514", """UPDATE "EvidenceExtractionJobs" SET "Status" = 'Completed' WHERE "Id" = @j""", ("j", job));
        await AssertViolation("23514", """UPDATE "EvidenceExtractionJobs" SET "Status" = 'Failed' WHERE "Id" = @j""", ("j", job));

        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var version = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" """);

        // Motivo di zero evidenze ⇔ nessuna evidenza; enunciato non vuoto; nessuna relazione verso sé stessa.
        await AssertViolation("23514", """UPDATE "EvidenceExtractionVersions" SET "NoEvidenceReason" = 'niente' WHERE "Id" = @v""", ("v", version));
        await AssertViolation("23514", """UPDATE "Evidences" SET "Statement" = '   ' WHERE "VersionId" = @v""", ("v", version));
        await AssertViolation("23505", """
            INSERT INTO "EvidenceExtractionVersions" SELECT * FROM "EvidenceExtractionVersions" WHERE "Id" = @v
            """, ("v", version));

        // Concluso il lavoro, un nuovo lavoro per lo stesso messaggio è ammesso.
        using var reanalysis = await Command(client, $"/api/projects/{project}/messages/{await Message(1)}/evidence/reanalyses");
        Assert.Equal(HttpStatusCode.Accepted, reanalysis.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Command(client, $"/api/projects/{project}/messages/{await Message(1)}/evidence/reanalyses")).StatusCode);
    }

    [Fact]
    public async Task Versions_are_numbered_per_message_the_first_valid_one_becomes_current_and_later_ones_stay_candidates_without_rewriting_history()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Le fatture vanno archiviate per dieci anni.");
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var message = await Message(1);

        var first = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "Version" = 1""");
        Assert.Equal(first, await Scalar<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));
        Assert.Equal(1, await Scalar<int>("""SELECT "Revision" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));
        Assert.Equal(EvidencePromotion.FirstValidVersionReason, await Scalar<string>("""SELECT "Reason" FROM "EvidencePromotions" WHERE "ToVersionId" = @v""", ("v", first)));

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Command(client, $"/api/projects/{project}/messages/{message}/evidence/reanalyses")).StatusCode);
            await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        }

        var snapshot = await Snapshot();
        Assert.Equal([1, 2, 3], await List<int>("""SELECT "Version" FROM "EvidenceExtractionVersions" WHERE "MessageId" = @m ORDER BY "Version" """, ("m", message)));
        Assert.Equal(first, await Scalar<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message))); // le nuove sono candidate

        var third = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "Version" = 3""");
        var promoted = await client.PostAsJsonAsync($"/api/projects/{project}/messages/{message}/evidence/promotions",
            new { versionId = third, expectedStateRevision = 1, reason = "verificata" }, Ct);
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal(2, await Scalar<int>("""SELECT "Revision" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));

        // Append-only: versioni ed evidenze identiche prima e dopo la promozione; cambia solo lo stato corrente e si aggiunge la promozione.
        Assert.Equal(snapshot, await Snapshot());
        Assert.Equal(2L, await Scalar<long>("""SELECT count(*) FROM "EvidencePromotions" WHERE "MessageId" = @m""", ("m", message)));
    }

    [Fact]
    public async Task Concurrent_promotions_with_the_same_expected_revision_let_exactly_one_win_and_the_other_gets_promotion_conflict()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Il magazzino lavora su due turni.");
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var message = await Message(1);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Command(client, $"/api/projects/{project}/messages/{message}/evidence/reanalyses")).StatusCode);
            await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        }

        var candidates = await List<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "Version" > 1 ORDER BY "Version" """);
        var responses = await Task.WhenAll(candidates.Select(v => client.PostAsJsonAsync(
            $"/api/projects/{project}/messages/{message}/evidence/promotions", new { versionId = v, expectedStateRevision = 1, reason = (string?)null }, Ct)));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var conflict = await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("promotion_conflict", conflict.GetProperty("code").GetString());
        var current = await Scalar<Guid>("""SELECT "CurrentVersionId" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message));
        Assert.Contains(current, candidates);
        Assert.Equal(2, await Scalar<int>("""SELECT "Revision" FROM "EvidenceMessageStates" WHERE "MessageId" = @m""", ("m", message)));
        Assert.Equal(2L, await Scalar<long>("""SELECT count(*) FROM "EvidencePromotions" WHERE "MessageId" = @m""", ("m", message)));
    }

    [Fact]
    public async Task The_additive_migration_goes_down_and_up_leaving_existing_data_unchanged_and_Analyze_backfills_the_old_messages()
    {
        using var client = await App.LoginAsync();
        var project = await CreateProject(client);
        await PostMessage(client, project, "Primo messaggio esistente prima del registro.");
        await PostMessage(client, project, "Secondo messaggio esistente prima del registro.");
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var originals = await List<string>("""SELECT "Id"::text || '|' || "Sequence" || '|' || "Body" FROM "ConversationMessages" ORDER BY "Sequence" """);

        using (var scope = App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);
        }

        Assert.Null(await Scalar<string?>("""SELECT to_regclass('"EvidenceExtractionJobs"')::text"""));
        Assert.Equal(originals, await List<string>("""SELECT "Id"::text || '|' || "Sequence" || '|' || "Body" FROM "ConversationMessages" ORDER BY "Sequence" """));

        using (var scope = App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(Ct);
        }

        Assert.Equal(originals, await List<string>("""SELECT "Id"::text || '|' || "Sequence" || '|' || "Body" FROM "ConversationMessages" ORDER BY "Sequence" """));
        Assert.Equal(0L, await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionJobs" """));

        // I messaggi già esistenti non hanno evidenze: Analizza accoda il backfill di tutti e attende.
        using var analyze = await Command(client, $"/api/projects/{project}/analyses");
        Assert.Equal(HttpStatusCode.Accepted, analyze.StatusCode);
        Assert.Equal(2L, await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionJobs" WHERE "Reason" = 'Backfill' AND "Status" = 'Queued'"""));
    }

    // ---------------------------------------------------------------- supporto

    /// <summary>Versioni ed evidenze così come sono scritte: qualunque riscrittura le farebbe differire.</summary>
    private Task<List<string>> Snapshot() => List<string>("""
        SELECT v."Id"::text || '|' || v."Version" || '|' || v."EvidenceCount" || '|' || coalesce(v."NoEvidenceReason", '') || '|' || v."CreatedAt"
               || '|' || coalesce(string_agg(e."Id"::text || ':' || e."Statement" || ':' || e."Content"::text, ';' ORDER BY e."Ordinal"), '')
        FROM "EvidenceExtractionVersions" v LEFT JOIN "Evidences" e ON e."VersionId" = v."Id"
        GROUP BY v."Id" ORDER BY v."Version"
        """);

    private async Task AssertViolation(string sqlState, string sql, params (string Name, object Value)[] parameters)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(sql, parameters));
        Assert.Equal(sqlState, error.SqlState);
    }

    private Task<Guid> Message(long sequence) =>
        Scalar<Guid>("""SELECT "Id" FROM "ConversationMessages" WHERE "Sequence" = @s AND "Role" = 'User'""", ("s", sequence));

    private static async Task<Guid> CreateProject(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = $"Registro {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task PostMessage(HttpClient client, Guid project, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Command(HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request, Ct);
    }

    private async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    private async Task<List<T>> List<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var list = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            list.Add(reader.GetFieldValue<T>(0));
        }

        return list;
    }
}
