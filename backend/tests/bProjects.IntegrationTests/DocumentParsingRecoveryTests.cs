using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Documents;
using Microsoft.EntityFrameworkCore;

namespace bProjects.IntegrationTests;

/// <summary>
/// T029 (parser reale) e T031: parsing e recupero su PostgreSQL reale. Le prove con il container Docling (livello PARSER) coprono i quattro
/// formati end-to-end con DGX assente; quelle con HTTP finto rendono riproducibili kill/crash/429, interruzioni del backend prima e dopo il
/// commit dell artefatto, reclaim, padre scaduto e due worker. Un prerequisito mancante produce Skipped, mai un PASS.
/// </summary>
[Trait("Category", TestCategories.Integration)]
[Collection(RealParser.CollectionName)]
public sealed class DocumentParsingRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Job(SeededParse s) => $"WHERE \"Id\" = '{s.Job}'";

    private static async Task<long> Count(ParseRig rig, string table) => await rig.Db.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"");

    private static async Task AssertOriginalIntact(ParseRig rig, SeededParse seed)
    {
        await using var stored = await rig.Store.OpenVerifiedAsync(seed.StorageKey, seed.Sha256, seed.ByteLength, Ct);
        using var copy = new MemoryStream();
        await stored.CopyToAsync(copy, Ct);
        Assert.Equal(seed.Content, copy.ToArray());   // byte e hash invariati
    }

    // ---- parser reale (T029 reale) --------------------------------------------------------------------------------------------------------

    public static TheoryData<string, PreparationStatus> RealFixtures => new()
    {
        { "pdf-text.pdf", PreparationStatus.Extracting },
        { "pdf-mixed.pdf", PreparationStatus.Incomplete },
        { "docx-basic.docx", PreparationStatus.Extracting },
        { "docx-image.docx", PreparationStatus.Incomplete },
        { "md-basic.md", PreparationStatus.Extracting },
        { "txt-utf16.txt", PreparationStatus.Extracting },
    };

    [Theory]
    [Trait("Category", DocumentTestCategories.Parser)]
    [MemberData(nameof(RealFixtures))]
    public async Task The_worker_publishes_the_real_parser_output_of_each_format_without_any_model(string fixture, PreparationStatus expected)
    {
        await using var rig = await ParseRig.CreateRealAsync();
        var seed = await rig.SeedFixtureAsync(fixture);

        var outcome = await rig.RunAsync();

        Assert.Equal(expected == PreparationStatus.Extracting ? ParseRunOutcome.Published : ParseRunOutcome.PublishedIncomplete, outcome);
        Assert.Equal(expected.ToString(), await rig.Str($"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));   // mai Ready
        Assert.Equal("Completed", await rig.Str($"SELECT \"Status\" FROM \"DocumentJobs\" {Job(seed)}"));
        Assert.Equal(1, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" {Job(seed)}"));
        Assert.Equal(1L, await Count(rig, "ParsedArtifacts"));
        Assert.True(await Count(rig, "DocumentBlocks") > 0);
        Assert.True(await Count(rig, "DocumentPortions") > 0);
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"DocumentPortions\" WHERE \"Status\" <> 'Pending'"));
        if (expected == PreparationStatus.Incomplete)
        {
            Assert.True(await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"PreparationIssues\" WHERE \"AffectsCompleteness\"") > 0);
        }

        await AssertOriginalIntact(rig, seed);
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT (SELECT count(*) FROM \"ChatTurns\") + (SELECT count(*) FROM \"ProcessingRuns\")"));   // nessun lavoro AI
        Assert.Null(await rig.RunAsync("worker-2"));   // terminale: nessun nuovo claim
    }

    [Fact]
    [Trait("Category", DocumentTestCategories.Parser)]
    public async Task A_busy_real_parser_answers_429_and_the_job_is_requeued_with_the_parser_wait_then_published()
    {
        await using var rig = await ParseRig.CreateRealAsync();
        var slow = await rig.SeedFixtureAsync("pdf-table-multipage.pdf");
        var second = await rig.SeedFixtureAsync("md-basic.md");   // un solo slot di conversione: la seconda richiesta contemporanea trova il parser occupato

        var first = rig.RunAsync("worker-1");
        ParseRunOutcome? busy = null;
        double wait = double.NaN;
        for (var i = 0; i < 40 && busy != ParseRunOutcome.Requeued; i++)
        {
            await Task.Delay(150, Ct);
            if (await rig.Db.ScalarAsync<long>($"SELECT count(*) FROM \"DocumentJobs\" WHERE \"Id\" = '{second.Job}' AND \"AttemptCount\" = 0") == 1 &&
                await rig.Db.ScalarAsync<long>($"SELECT count(*) FROM \"DocumentJobs\" WHERE \"Id\" = '{slow.Job}' AND \"Status\" = 'Running'") == 1)
            {
                busy = await rig.RunAsync("worker-2");
                if (busy == ParseRunOutcome.Requeued)
                {
                    wait = await rig.Db.ScalarAsync<double>($"SELECT extract(epoch FROM (\"NextAttemptAt\" - clock_timestamp())) FROM \"DocumentJobs\" {Job(second)}");
                }
                if (busy is ParseRunOutcome.Published or ParseRunOutcome.PublishedIncomplete)
                {
                    break;   // la prima conversione era gia finita: nessuna sovrapposizione osservabile
                }
            }
        }

        var firstOutcome = await first;
        Assert.Contains(firstOutcome, new ParseRunOutcome?[] { ParseRunOutcome.Published, ParseRunOutcome.PublishedIncomplete });
        Assert.SkipWhen(busy != ParseRunOutcome.Requeued, "il parser reale ha concluso la prima conversione prima della seconda richiesta: 429 non osservato in questa esecuzione");
        Assert.InRange(wait, 2.5, 5.1);   // Retry-After del parser (5 s) prevale sul backoff di 2 s
        Assert.Equal("Queued", await rig.Str($"SELECT \"Status\" FROM \"DocumentJobs\" {Job(second)}"));
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"NextAttemptAt\" = clock_timestamp() WHERE \"Id\" = '{second.Job}'");
        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync("worker-2"));
        Assert.Equal(2, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" {Job(second)}"));
    }

    // ---- parser kill / crash / 429 ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kill")]
    [InlineData("crash")]
    [InlineData("busy")]
    public async Task A_parser_killed_crashed_or_busy_is_retried_three_times_then_fails_explicitly_with_originals_and_storage_untouched(string failure)
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        var deadline = await rig.Str($"SELECT \"DeadlineAt\"::text FROM \"DocumentJobs\" {Job(seed)}");
        rig.OnConvert = (_, _) => failure switch
        {
            "kill" => throw new HttpRequestException("connessione interrotta: processo del parser terminato"),
            "crash" => Task.FromResult(ParseRig.Status(503, "{\"code\":\"parser_crash\"}")),
            _ => Task.FromResult(ParseRig.Status(429, "{\"code\":\"parser_busy\"}", retryAfterSeconds: 1)),
        };

        Assert.Equal(ParseRunOutcome.Requeued, await rig.RunAsync());
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"NextAttemptAt\" = clock_timestamp() WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(ParseRunOutcome.Requeued, await rig.RunAsync());
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"NextAttemptAt\" = clock_timestamp() WHERE \"Id\" = '{seed.Job}'");
        Assert.Equal(ParseRunOutcome.Failed, await rig.RunAsync());

        Assert.Equal(3, rig.ConvertCalls);
        Assert.Equal("attempts_exhausted", await rig.Str($"SELECT \"ErrorCode\" FROM \"DocumentJobs\" {Job(seed)}"));
        Assert.Equal("Failed", await rig.Str($"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Equal(deadline, await rig.Str($"SELECT \"DeadlineAt\"::text FROM \"DocumentJobs\" {Job(seed)}"));   // nessun reset della scadenza
        Assert.Equal(0L, await Count(rig, "ParsedArtifacts") + await Count(rig, "DocumentBlocks") + await Count(rig, "DocumentPortions"));
        Assert.Single(rig.Store.EnumerateObjects());   // solo l originale: nessun artefatto orfano
        await AssertOriginalIntact(rig, seed);
        Assert.Null(await rig.RunAsync());
    }

    [Fact]
    public async Task A_busy_parser_wait_longer_than_the_backoff_is_honoured_without_moving_the_deadline()
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => Task.FromResult(ParseRig.Status(429, "{\"code\":\"parser_busy\"}", retryAfterSeconds: 9));

        Assert.Equal(ParseRunOutcome.Requeued, await rig.RunAsync());

        var wait = await rig.Db.ScalarAsync<double>($"SELECT extract(epoch FROM (\"NextAttemptAt\" - clock_timestamp())) FROM \"DocumentJobs\" {Job(seed)}");
        Assert.InRange(wait, 7.0, 9.1);
        Assert.Null(await rig.RunAsync("worker-2"));   // non ancora dovuto: nessun secondo invio al parser
        Assert.Equal(1, rig.ConvertCalls);
    }

    // ---- backend interrotto prima / dopo il commit dell artefatto ---------------------------------------------------------------------------

    [Fact]
    public async Task A_backend_stopped_before_the_artifact_commit_leaves_a_running_job_that_is_reclaimed_after_lease_expiry_and_published_once()
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var inFlight = new TaskCompletionSource();
        rig.OnConvert = async (_, token) =>
        {
            inFlight.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(60), token);
            return ParseRig.Ok(ParseRig.FakeManifest(seed));
        };

        var run = rig.RunAsync("processo-1", stop.Token);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await stop.CancelAsync();   // arresto del processo mentre il parser lavora
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal("Running", await rig.Str($"SELECT \"Status\" FROM \"DocumentJobs\" {Job(seed)}"));   // nessun reset al riavvio: il lease scade da solo
        Assert.Equal(0L, await Count(rig, "ParsedArtifacts"));
        Assert.Null(await rig.RunAsync("processo-2"));   // lease ancora vivo: nessun reclaim anticipato
        Assert.Equal(1, rig.ConvertCalls);

        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Job}'");
        rig.OnConvert = (_, _) => Task.FromResult(ParseRig.Ok(ParseRig.FakeManifest(seed)));
        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync("processo-2"));

        Assert.Equal(2, await rig.Db.ScalarAsync<int>($"SELECT \"AttemptCount\" FROM \"DocumentJobs\" {Job(seed)}"));
        Assert.Equal(2, await rig.Db.ScalarAsync<int>($"SELECT \"FencingToken\" FROM \"DocumentJobs\" {Job(seed)}"));
        Assert.Equal(1L, await Count(rig, "ParsedArtifacts"));
        Assert.Equal(2, rig.Store.EnumerateObjects().Count);   // originale + un solo artefatto
        await AssertOriginalIntact(rig, seed);
    }

    [Fact]
    public async Task An_artifact_file_left_by_a_crash_before_the_commit_is_never_referenced_and_the_reclaim_publishes_its_own_unique_artifact()
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        // Stato lasciato da un processo morto fra la scrittura del file dell artefatto e la transazione: job Running a lease scaduto, file senza riga.
        var orphanId = Guid.NewGuid();
        var orphan = await rig.Store.WriteAsync(seed.Project, orphanId, new MemoryStream("artefatto-incompleto"u8.ToArray()), 64, Ct);
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"Status\" = 'Running', \"LeaseOwner\" = 'morto', \"LeaseUntil\" = clock_timestamp() - interval '1 second', \"FencingToken\" = 1, \"AttemptCount\" = 1 WHERE \"Id\" = '{seed.Job}'");
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"Status\" = 'Parsing' WHERE \"Id\" = '{seed.Preparation}'");
        rig.OnConvert = (_, _) => Task.FromResult(ParseRig.Ok(ParseRig.FakeManifest(seed)));

        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync("nuovo"));

        await using var db = rig.Db.NewContext();
        var artifact = await db.ParsedArtifacts.AsNoTracking().SingleAsync(Ct);
        Assert.NotEqual(orphan.StorageKey, artifact.StorageKey);   // l artefatto pubblicato non riusa il file orfano
        Assert.False(await db.ParsedArtifacts.AnyAsync(a => a.StorageKey == orphan.StorageKey, Ct));   // l orfano non e referenziato: lo raccoglie la pulizia dei file non referenziati
        Assert.Equal(1, await db.DocumentBlocks.Select(b => b.ArtifactId).Distinct().CountAsync(Ct));
        await AssertOriginalIntact(rig, seed);
    }

    [Fact]
    public async Task A_backend_stopped_after_the_artifact_commit_never_republishes_and_a_duplicate_artifact_is_rejected_by_the_database()
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => Task.FromResult(ParseRig.Ok(ParseRig.FakeManifest(seed)));
        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync("processo-1"));
        var hash = await rig.Str("SELECT \"ResultHash\" FROM \"DocumentJobs\" LIMIT 1");

        // Riavvio dopo il commit (anche con lease apparentemente scaduto): il job e Completed, nessun nuovo claim, nessuna seconda conversione.
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 hour' WHERE \"Id\" = '{seed.Job}'");
        Assert.Null(await rig.RunAsync("processo-2"));
        Assert.Equal(1, rig.ConvertCalls);
        Assert.Equal(hash, await rig.Str("SELECT \"ResultHash\" FROM \"DocumentJobs\" LIMIT 1"));
        Assert.Equal(1L, await Count(rig, "ParsedArtifacts"));

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => rig.Db.ExecuteAsync(
            $"INSERT INTO \"ParsedArtifacts\" (\"Id\",\"ProjectId\",\"PreparationId\",\"JobId\",\"FencingToken\",\"SchemaVersion\",\"StorageKey\",\"Sha256\",\"ByteLength\",\"ParserConfigHash\",\"BlockCount\",\"IssueCount\",\"CreatedAt\") " +
            $"SELECT gen_random_uuid(),\"ProjectId\",\"PreparationId\",\"JobId\",\"FencingToken\",\"SchemaVersion\",'altra-chiave',\"Sha256\",\"ByteLength\",\"ParserConfigHash\",\"BlockCount\",\"IssueCount\",\"CreatedAt\" FROM \"ParsedArtifacts\""));
        Assert.Contains("duplicate key", duplicate.ToString(), StringComparison.OrdinalIgnoreCase);   // al massimo un artefatto per preparazione
    }

    // ---- reclaim, due worker, padre scaduto ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reclaimed_job_ignores_the_late_result_of_the_stale_owner_and_only_the_new_fence_publishes()
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        var staleStarted = new TaskCompletionSource();
        var releaseStale = new TaskCompletionSource();
        var calls = 0;
        rig.OnConvert = async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                staleStarted.SetResult();
                await releaseStale.Task.WaitAsync(token);   // il primo owner resta bloccato nel parser
            }

            return ParseRig.Ok(ParseRig.FakeManifest(seed));
        };
        var stale = rig.RunAsync("vecchio");
        await staleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"LeaseUntil\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Job}'");

        Assert.Equal(ParseRunOutcome.Published, await rig.RunAsync("nuovo"));   // reclaim: fence 2
        releaseStale.SetResult();
        Assert.Equal(ParseRunOutcome.Rejected, await stale);   // il risultato tardivo non pubblica

        Assert.Equal(1L, await Count(rig, "ParsedArtifacts"));
        Assert.Equal(2, await rig.Db.ScalarAsync<int>($"SELECT \"FencingToken\" FROM \"DocumentJobs\" {Job(seed)}"));
        Assert.Equal(2, rig.Store.EnumerateObjects().Count);   // originale + artefatto del nuovo owner
        Assert.Equal("Completed", await rig.Str($"SELECT \"Status\" FROM \"DocumentJobs\" {Job(seed)}"));
    }

    [Fact]
    public async Task Two_workers_never_publish_together_for_the_same_job_and_each_publishes_its_own_job()
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = async (_, token) =>
        {
            await Task.Delay(600, token);
            return ParseRig.Ok(ParseRig.FakeManifest(seed));
        };

        var outcomes = await Task.WhenAll(rig.RunAsync("a"), rig.RunAsync("b"));

        Assert.Single(outcomes, o => o == ParseRunOutcome.Published);
        Assert.Single(outcomes, o => o is null);
        Assert.Equal(1L, await Count(rig, "ParsedArtifacts"));

        var other = await rig.Db.SeedParseAsync(rig.Store, "Altro\n\nTesto del documento.\n"u8.ToArray());
        rig.OnConvert = (_, _) => Task.FromResult(ParseRig.Ok(ParseRig.FakeManifest(other)));
        var pair = await Task.WhenAll(rig.RunAsync("a"), rig.RunAsync("b"));
        Assert.Single(pair, o => o == ParseRunOutcome.Published);
        Assert.Equal(2L, await Count(rig, "ParsedArtifacts"));
    }

    [Fact]
    public async Task A_parent_that_expired_while_the_worker_was_dead_fails_the_job_without_conversion_and_leaves_no_derived_data()
    {
        await using var rig = await ParseRig.CreateAsync();
        var seed = await rig.SeedAsync();
        rig.OnConvert = (_, _) => throw new InvalidOperationException("nessuna conversione dopo la scadenza del padre");
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentJobs\" SET \"Status\" = 'Running', \"LeaseOwner\" = 'morto', \"LeaseUntil\" = clock_timestamp() - interval '1 second', \"FencingToken\" = 1, \"AttemptCount\" = 1 WHERE \"Id\" = '{seed.Job}'");
        await rig.Db.ExecuteAsync($"UPDATE \"DocumentPreparations\" SET \"Status\" = 'Parsing', \"EffectiveDeadlineAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = '{seed.Preparation}'");

        Assert.Null(await rig.RunAsync());

        Assert.Equal("parent_deadline_exceeded", await rig.Str($"SELECT \"ErrorCode\" FROM \"DocumentJobs\" {Job(seed)}"));
        Assert.Equal("Failed", await rig.Str($"SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = '{seed.Preparation}'"));
        Assert.Equal(0, rig.ConvertCalls);
        Assert.Equal(0L, await Count(rig, "ParsedArtifacts") + await Count(rig, "DocumentBlocks") + await Count(rig, "DocumentPortions"));
        Assert.Single(rig.Store.EnumerateObjects());
        await AssertOriginalIntact(rig, seed);
    }

    // ---- limiti di tempo: 300 s per tentativo, 3600 s per il root inclusa la coda ---------------------------------------------------------------

    private static int SentTimeout(ParseRig rig) =>
        int.Parse(Regex.Match(rig.LastRequestBody!, "name=\"?timeoutSeconds\"?\\r?\\n\\r?\\n(\\d+)").Groups[1].Value);

    [Fact]
    public async Task The_attempt_timeout_is_capped_at_300_seconds_and_never_exceeds_the_residual_root_time_queue_included()
    {
        await using var fresh = await ParseRig.CreateAsync();
        var seed = await fresh.SeedAsync();
        fresh.OnConvert = (_, _) => Task.FromResult(ParseRig.Ok(ParseRig.FakeManifest(seed)));
        Assert.Equal(ParseRunOutcome.Published, await fresh.RunAsync());
        Assert.Equal(300, SentTimeout(fresh));   // root da 3600 s, tentativo da 300 s

        await using var queued = await ParseRig.CreateAsync();
        var late = await queued.SeedAsync(hardWindow: TimeSpan.FromSeconds(90));   // la coda ha consumato quasi tutto il root
        queued.OnConvert = (_, _) => Task.FromResult(ParseRig.Ok(ParseRig.FakeManifest(late)));
        Assert.Equal(ParseRunOutcome.Published, await queued.RunAsync());
        Assert.InRange(SentTimeout(queued), 80, 90);   // min(300, residuo del root)

        await using var nearly = await ParseRig.CreateAsync();
        var last = await nearly.SeedAsync(hardWindow: TimeSpan.FromMilliseconds(600));
        nearly.OnConvert = (_, _) => throw new InvalidOperationException("sotto il budget minimo il parser non va chiamato");
        await Task.Delay(100, Ct);
        var outcome = await nearly.RunAsync();
        Assert.True(outcome is null or ParseRunOutcome.Failed);   // scaduto o troppo vicino alla scadenza: mai una conversione senza budget
        Assert.Equal(0, nearly.ConvertCalls);
        Assert.Equal(0L, await Count(nearly, "ParsedArtifacts"));
        _ = last;
    }

    [Fact]
    public async Task Queued_requests_survive_without_a_parser_and_are_picked_up_in_request_order_once_it_is_available()
    {
        await using var rig = await ParseRig.CreateAsync(parser: new ParserOptions());
        var first = await rig.SeedAsync();
        var second = await rig.Db.SeedParseAsync(rig.Store, "Altro\n\nTesto del documento.\n"u8.ToArray());
        Assert.Null(await rig.RunAsync());   // parser non configurato: nessun tentativo consumato
        Assert.Equal(0L, await rig.Db.ScalarAsync<long>("SELECT count(*) FROM \"DocumentJobs\" WHERE \"AttemptCount\" > 0 OR \"Status\" <> 'Queued'"));

        await using var live = await ParseRig.CreateAsync();   // nuovo processo, nuovo database: stessa semantica con il parser raggiungibile
        var queued = await live.SeedAsync();
        var queued2 = await live.Db.SeedParseAsync(live.Store, "Altro\n\nTesto del documento.\n"u8.ToArray());
        live.OnConvert = (request, _) => Task.FromResult(ParseRig.Ok(ParseRig.FakeManifest(live.LastRequestBody!.Contains("Altro", StringComparison.Ordinal) ? queued2 : queued)));
        Assert.Equal(ParseRunOutcome.Published, await live.RunAsync());
        Assert.Equal("Completed", await live.Str($"SELECT \"Status\" FROM \"DocumentJobs\" {Job(queued)}"));
        Assert.Equal("Queued", await live.Str($"SELECT \"Status\" FROM \"DocumentJobs\" {Job(queued2)}"));
        _ = (first, second);
    }
}
