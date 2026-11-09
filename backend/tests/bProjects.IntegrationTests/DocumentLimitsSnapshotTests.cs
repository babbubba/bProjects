using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static bProjects.IntegrationTests.DocumentReconcileAliasWorkflowTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// Congelamento persistente dei limiti documentali dei run di Analizza (contracts/ai-workflow.md §11; sprint2-handoff, decisione sul congelamento).
/// PostgreSQL reale e modello FINTO: lo snapshot nasce con il run dal vero POST, è immutabile (CHECK + trigger) e l'esecuzione usa solo lui, mai la policy
/// corrente. Nessuna compatibilità legacy: uno snapshot mancante, corrotto, con hash incoerente o schema non supportato fallisce con config_invalid.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentLimitsSnapshotTests : ReconcileRig
{
    private const int Documents = 8;
    private const string Column = "DocumentLimitsSnapshot";

    private static string Statement(int n, int words = 120) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, words).Select(i => $"w{n}x{i}"));

    // ================================================================ supporto

    /// <summary>Modifica Admin della policy come il PUT reale: nuova revisione, stesso hash dei timer (i limiti toccati non sono timer).</summary>
    private async Task SetPolicyAsync(Func<DocumentLimits, DocumentLimits> change)
    {
        using var scope = App.Services.CreateScope();
        var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
        var limits = change(snapshot.Limits);
        Assert.Empty(limits.Validate());
        await Execute(
            "UPDATE \"DocumentPolicies\" SET \"Revision\" = \"Revision\" + 1, \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
    }

    private async Task<DocumentPolicySnapshot> PolicyAsync()
    {
        using var scope = App.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct)).Snapshot;
    }

    /// <summary>Run creato dal vero endpoint Analizza (v5, T141: senza Gap la gerarchia oltre contesto è quella v4); <paramref name="documents"/> = 1 entra in una chiamata, 8 richiedono la gerarchia.</summary>
    private async Task<(Guid Run, AnalysisRunClaim Claim)> ArrangeAsync(int documents = Documents)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 1; n <= documents; n++)
        {
            var subject = n == 1 ? "aaa" : n == Documents ? "zzz" : $"mmm{n}";
            await NewDocument(client, project, $"doc{n}", documents == 1 ? "Il cliente A puo emettere al massimo 5 ordini al giorno." : Statement(n), subject);
        }

        var run = await StartAsync(client, project);
        return (run, await ClaimAsync(run));
    }

    private async Task<Guid> StartAsync(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static DocumentChatWorkflowTests.FakeChatModel Healthy() => new() { Responder = r => Dto(ParseAlias(r.AllText)) };

    private static DocumentChatWorkflowTests.FakeChatModel HealthySingle() => new() { Responder = r => AliasModelRepliesV5.Reply(r.AllText) };   // POST reale = v5: DTO alias-2

    private async Task<string> SnapshotTextAsync(Guid run) => await Scalar<string>($"SELECT \"{Column}\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));

    private async Task<long> RevisionsAsync(Guid run) => await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run));

    /// <summary>Simula la corruzione che le protezioni del database impediscono (solo per provare la difesa applicativa): sul DB di prova si tolgono CHECK e trigger.</summary>
    private async Task CorruptAsync(Guid run, string? json)
    {
        await Execute("ALTER TABLE \"ProcessingRuns\" DROP CONSTRAINT \"CK_ProcessingRuns_DocumentLimitsSnapshot\"");
        await Execute("ALTER TABLE \"ProcessingRuns\" DISABLE TRIGGER \"TR_ProcessingRuns_DocumentLimitsSnapshot\"");
        await Execute($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = @j::jsonb WHERE \"Id\" = @r", ("j", json!), ("r", run));
    }

    private async Task<AnalysisRunClaim> TakeOverAsync(Guid run, string owner)
    {
        // Come in DocumentReconciliationPersistenceTests: il rig lascia il root fuori dalla classificazione solo per la durata del claim reale.
        var classification = await Scalar<string>("SELECT coalesce(\"ClassificationStatus\", '') FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        await Execute("UPDATE \"ProcessingRuns\" SET \"LeaseUntil\" = now() - interval '1 second', \"ClassificationStatus\" = NULL WHERE \"Id\" = @r AND \"Status\" = 'Running'", ("r", run));
        WorkClaim? work;
        using (var scope = App.Services.CreateScope())
        {
            work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>().ClaimNextWorkAsync(owner, turns: false, runs: true, Ct);
        }

        await Execute("UPDATE \"ProcessingRuns\" SET \"ClassificationStatus\" = NULLIF(@c, '') WHERE \"Id\" = @r", ("r", run), ("c", classification));
        return work?.Run ?? throw new InvalidOperationException("Il claim reale non ha preso in carico il run a lease scaduto.");
    }

    // ================================================================ 2. persistenza iniziale

    [Fact]
    public async Task The_real_Analizza_POST_persists_the_snapshot_with_the_limits_revision_and_hash_of_the_request_moment()
    {
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 77, MaxReconciliationLevels = 7, MaxCandidatesPerRun = 1234 });
        var policy = await PolicyAsync();

        var (run, _) = await ArrangeAsync(documents: 1);

        var text = await SnapshotTextAsync(run);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(policy.Revision, json.RootElement.GetProperty("policyRevision").GetInt64());
        var parsed = RunLimitsSnapshot.Parse(text);
        Assert.Equal(policy.Limits, parsed.Limits);
        Assert.Equal(77, parsed.Limits.MaxAiCallsPerRun);
        Assert.Equal(RunLimitsSnapshot.ComputeHash(1, policy.Revision, policy.Limits), parsed.Hash);
        // Non è il PolicyHash del root (solo timer): due fotografie diverse con scopi diversi.
        Assert.NotEqual(await Scalar<string>("SELECT \"PolicyHash\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), parsed.Hash);
        // D1 (T148): il run creato dal POST reale è v6 e snapshot dei limiti e versioni congelate nascono insieme nella stessa transazione del run.
        Assert.Equal(DocumentAnalysisWorkflow.DefinitionVersionV6, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal("gap-stage-1", await Scalar<string>("SELECT \"PromptVersions\"->>'gapStage' FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));   // v6: chiavi dello stadio Gap al posto della policy Gap temporanea
    }

    [Fact]
    public async Task A_policy_change_after_creation_never_touches_the_persisted_snapshot_and_a_new_run_gets_the_new_one()
    {
        var (first, _) = await ArrangeAsync(documents: 1);
        var before = await SnapshotTextAsync(first);
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 9 });
        Assert.Equal(before, await SnapshotTextAsync(first));
        Assert.NotEqual(9, RunLimitsSnapshot.Parse(before).Limits.MaxAiCallsPerRun);

        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x' WHERE \"Id\" = @r", ("r", first));
        var (second, _) = await ArrangeAsync(documents: 1);
        var parsed = RunLimitsSnapshot.Parse(await SnapshotTextAsync(second));
        Assert.Equal(9, parsed.Limits.MaxAiCallsPerRun);
        Assert.True(parsed.PolicyRevision > RunLimitsSnapshot.Parse(before).PolicyRevision);
    }

    // ================================================================ 1/3. mutabilità: policy cambiata dopo la creazione

    [Fact]
    public async Task A_hierarchical_run_keeps_the_original_level_cap_when_the_admin_lowers_it_after_creation()
    {
        var (run, claim) = await ArrangeAsync();
        await SetPolicyAsync(l => l with { MaxReconciliationLevels = 1 });

        var result = await RunAsync(claim, Healthy());

        Assert.True(result.Status == ProcessingRunStatus.Completed, result.ErrorCode + ": " + await Message(run));
        Assert.Equal(1, await RevisionsAsync(run));
    }

    [Fact]
    public async Task A_hierarchical_run_keeps_the_original_level_cap_when_the_admin_raises_it_after_creation()
    {
        await SetPolicyAsync(l => l with { MaxReconciliationLevels = 1 });
        var (run, claim) = await ArrangeAsync();
        await SetPolicyAsync(l => l with { MaxReconciliationLevels = 32 });
        var model = Healthy();

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal(DocumentWorkException.CoverageIncomplete, result.ErrorCode);
        Assert.Contains("livelli congelati", await Message(run));
        Assert.Equal(0, await RevisionsAsync(run));
    }

    [Fact]
    public async Task A_hierarchical_run_keeps_the_original_call_cap_when_the_admin_raises_it_after_creation()
    {
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 3 });
        var (run, claim) = await ArrangeAsync();
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 512 });
        var model = Healthy();

        var result = await RunAsync(claim, model, new DocumentOptions { MaxAiCallsPerRun = 512 });

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CallLimitExceeded, result.ErrorCode + ": " + await Message(run));
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(3, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_single_call_run_resumed_after_a_crash_keeps_the_original_call_cap_when_the_admin_raises_it()
    {
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 1 });
        var (run, claim) = await ArrangeAsync(documents: 1);
        var crashing = new DocumentChatWorkflowTests.FakeChatModel { Responder = _ => throw new InvalidOperationException("rete caduta") };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, crashing));
        Assert.Equal(1, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));   // la chiamata riservata resta consumata
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 512 });
        var healthy = HealthySingle();

        var resumed = await RunAsync(claim, healthy);

        Assert.Equal(ProcessingRunStatus.Failed, resumed.Status);
        Assert.True(resumed.ErrorCode == DocumentWorkException.CallLimitExceeded, resumed.ErrorCode + ": " + await Message(run));
        Assert.Empty(healthy.Requests);
    }

    [Fact]
    public async Task A_hierarchical_run_keeps_its_limits_across_a_crash_a_policy_change_and_a_real_lease_takeover()
    {
        var (run, original) = await ArrangeAsync();
        var seen = 0;
        var crashing = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Interlocked.Increment(ref seen) == 3 ? throw new InvalidOperationException("rete caduta") : Dto(ParseAlias(r.AllText)) };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(original, crashing));
        var before = await SnapshotTextAsync(run);
        await SetPolicyAsync(l => l with { MaxReconciliationLevels = 1, MaxAiCallsPerRun = 4 });   // se valessero, il run fallirebbe: i livelli/chiamate originali sono ben più ampi

        var takenOver = await TakeOverAsync(run, "worker-b");
        var healthy = Healthy();
        var result = await RunAsync(takenOver, healthy);

        Assert.True(takenOver.FencingToken > original.FencingToken && takenOver.LeaseOwner != original.LeaseOwner);
        Assert.True(result.Status == ProcessingRunStatus.Completed, result.ErrorCode + ": " + await Message(run));
        Assert.Equal(1, await RevisionsAsync(run));
        Assert.Equal(before, await SnapshotTextAsync(run));   // lease, fencing e recovery non la toccano
        Assert.True(await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)) > 4);
    }

    [Fact]
    public async Task Re_executing_the_same_run_after_each_policy_change_always_uses_the_same_persisted_limits()
    {
        await SetPolicyAsync(l => l with { MaxReconciliationLevels = 1 });
        var (run, claim) = await ArrangeAsync();

        for (var step = 0; step < 3; step++)
        {
            await SetPolicyAsync(l => l with { MaxReconciliationLevels = 5 + step });   // più ampio e diverso a ogni giro
            var result = await RunAsync(claim, Healthy());
            Assert.Equal(ProcessingRunStatus.Failed, result.Status);   // stesso terminale di sempre: i livelli sono 1
            Assert.Contains("livelli congelati", await Message(run));
            Assert.Equal(1, RunLimitsSnapshot.Parse(await SnapshotTextAsync(run)).Limits.MaxReconciliationLevels);
        }
    }

    // ================================================================ 5. hash, schema, corruzione

    [Fact]
    public async Task The_hash_survives_the_jsonb_roundtrip_with_reordered_keys_and_is_verified_independently_of_text_order()
    {
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 41 });
        var (run, _) = await ArrangeAsync(documents: 1);
        var written = await Scalar<string>($"SELECT \"{Column}\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        var read = RunLimitsSnapshot.Parse(written);

        // jsonb non conserva l'ordine d'inserimento (lo normalizza per lunghezza/valore delle chiavi): il testo letto differisce da quello scritto, l'hash no.
        Assert.NotEqual(read.ToJson(), written);
        Assert.Equal(read, RunLimitsSnapshot.Parse(read.ToJson()));
        Assert.Equal(read.Hash, RunLimitsSnapshot.ComputeHash(read.SchemaVersion, read.PolicyRevision, read.Limits));

        // Stesso contenuto con chiavi in ordine inverso (envelope e limits): ancora valido, stesso hash.
        var reversed = ReverseKeys(JsonDocument.Parse(read.ToJson()).RootElement);
        Assert.NotEqual(read.ToJson(), reversed);
        Assert.Equal(read, RunLimitsSnapshot.Parse(reversed));
    }

    private static string ReverseKeys(JsonElement element)
    {
        string Write(JsonElement e) => e.ValueKind == JsonValueKind.Object
            ? "{" + string.Join(",", e.EnumerateObject().Reverse().Select(p => JsonSerializer.Serialize(p.Name) + ":" + Write(p.Value))) + "}"
            : e.GetRawText();
        return Write(element);
    }

    public static IEnumerable<object[]> Corruptions()
    {
        var limits = new DocumentOptions().ToLimits();
        var good = RunLimitsSnapshot.Of(DocumentPolicySnapshot.Of(limits, 3));
        var json = good.ToJson();
        yield return ["hash_incoerente", json.Replace(good.Hash, new string('0', 64))];
        yield return ["limite_alterato", json.Replace($"\"maxAiCallsPerRun\":{limits.MaxAiCallsPerRun}", $"\"maxAiCallsPerRun\":{limits.MaxAiCallsPerRun + 1}")];
        yield return ["schema_non_supportato", json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")];
        yield return ["revisione_alterata", json.Replace("\"policyRevision\":3", "\"policyRevision\":4")];
        yield return ["campo_sconosciuto", json.Replace("\"limits\":{", "\"limits\":{\"nuovoLimite\":1,")];
        yield return ["limiti_non_validi", RunLimitsSnapshot.Of(DocumentPolicySnapshot.Of(limits with { MaxAiCallsPerRun = 0 }, 3)).ToJson()];
        yield return ["envelope_con_campo_extra", json.Replace("\"hash\":", "\"extra\":true,\"hash\":")];
        yield return ["limits_non_oggetto", $$"""{"schemaVersion":1,"policyRevision":3,"limits":[],"hash":"{{good.Hash}}"}"""];
        yield return ["array", "[]"];
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task A_corrupt_snapshot_fails_the_run_with_config_invalid_before_any_send_and_never_falls_back_to_the_current_policy(string name, string corrupted)
    {
        var (run, claim) = await ArrangeAsync();   // la policy corrente è valida e permetterebbe di eseguire: non deve mai essere usata come ripiego
        await CorruptAsync(run, corrupted);
        var model = Healthy();

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Failed && result.ErrorCode == "config_invalid", $"{name}: {result.Status} {result.ErrorCode}");
        Assert.Empty(model.Requests);
        Assert.Equal(0, await RevisionsAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Contains("config_invalid", await Message(run));
    }

    [Fact]
    public async Task A_missing_snapshot_fails_the_run_with_config_invalid_and_the_current_policy_is_never_used()
    {
        var (run, claim) = await ArrangeAsync(documents: 1);
        await Execute("ALTER TABLE \"ProcessingRuns\" DROP CONSTRAINT \"CK_ProcessingRuns_DocumentLimitsSnapshot\"");
        await Execute("ALTER TABLE \"ProcessingRuns\" DISABLE TRIGGER \"TR_ProcessingRuns_DocumentLimitsSnapshot\"");
        await Execute($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = NULL WHERE \"Id\" = @r", ("r", run));
        var model = Healthy();

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("config_invalid", result.ErrorCode);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public void The_analysis_workflow_reads_no_admin_policy_it_only_reads_the_run_snapshot()
    {
        var source = File.ReadAllText(Locate("src/bProjects.App/AI/DocumentAnalysisWorkflow.cs"));
        Assert.DoesNotContain("DocumentPolicyService", source);
        Assert.Contains("RunLimitsSnapshot.Read", source);
    }

    private static string Locate(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var prefix in new[] { "", "backend" })
            {
                var path = Path.Combine(dir.FullName, prefix, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        throw new FileNotFoundException($"{relative} non trovato risalendo da {AppContext.BaseDirectory}.");
    }

    // ================================================================ 6. SQL diretto: CHECK e trigger

    [Fact]
    public async Task The_database_refuses_to_change_clear_or_initialize_the_snapshot_but_allows_every_operational_update()
    {
        var (run, _) = await ArrangeAsync(documents: 1);
        var original = await SnapshotTextAsync(run);

        await AssertRestricted($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = jsonb_set(\"{Column}\", '{{limits,maxAiCallsPerRun}}', '999') WHERE \"Id\" = @r", run);
        await AssertRestricted($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = jsonb_set(\"{Column}\", '{{hash}}', to_jsonb(repeat('0', 64))) WHERE \"Id\" = @r", run);
        await AssertRestricted($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = jsonb_set(\"{Column}\", '{{policyRevision}}', '99') WHERE \"Id\" = @r", run);
        await AssertRestricted($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = jsonb_set(\"{Column}\", '{{schemaVersion}}', '2') WHERE \"Id\" = @r", run);
        await AssertRestricted($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = NULL WHERE \"Id\" = @r", run);
        await AssertRestricted($"UPDATE \"ProcessingRuns\" SET \"{Column}\" = '{{}}'::jsonb, \"Status\" = 'Running' WHERE \"Id\" = @r", run);   // anche in un UPDATE misto
        Assert.Equal(original, await SnapshotTextAsync(run));

        // Stato, lease, fencing, contatori, checkpoint e altri campi operativi si aggiornano normalmente; riscrivere lo stesso valore non è una modifica.
        await Execute(
            $"""
            UPDATE "ProcessingRuns" SET "Status" = 'Running', "LeaseOwner" = 'w1', "LeaseUntil" = now() + interval '5 minutes', "FencingToken" = "FencingToken" + 1, "AttemptCount" = 1,
                   "CallCount" = 2, "StartedAt" = now(), "LastObservedStage" = 'x', "UpdatedAt" = now(), "{Column}" = "{Column}"
            WHERE "Id" = @r
            """,
            ("r", run));
        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x', \"CompletedAt\" = now(), \"LeaseOwner\" = NULL, \"LeaseUntil\" = NULL WHERE \"Id\" = @r", ("r", run));
        Assert.Equal("Failed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(original, await SnapshotTextAsync(run));
    }

    private async Task AssertRestricted(string sql, Guid run)
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() => Execute(sql, ("r", run)));
        Assert.Equal("23001", failure.SqlState);   // restrict_violation del trigger
    }

    [Fact]
    public async Task The_snapshot_stays_intact_while_many_connections_update_lease_state_and_counters_and_others_try_to_rewrite_it()
    {
        var (run, _) = await ArrangeAsync(documents: 1);
        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Running', \"AttemptCount\" = 0, \"CallCount\" = 0, \"LeaseOwner\" = 'w0', \"LeaseUntil\" = now() + interval '10 minutes' WHERE \"Id\" = @r", ("r", run));
        var original = await SnapshotTextAsync(run);
        const int writers = 6, perWriter = 40, attackers = 3, attempts = 40;
        var operational = 0;
        var refused = 0;

        async Task Writer(int id)
        {
            await using var connection = new NpgsqlConnection(App.ConnectionString);
            await connection.OpenAsync(Ct);
            for (var i = 0; i < perWriter; i++)
            {
                await using var command = new NpgsqlCommand(
                    "UPDATE \"ProcessingRuns\" SET \"CallCount\" = \"CallCount\" + 1, \"FencingToken\" = \"FencingToken\" + 1, \"LeaseOwner\" = @o, \"LeaseUntil\" = now() + interval '10 minutes', \"UpdatedAt\" = now() WHERE \"Id\" = @r",
                    connection);
                command.Parameters.AddWithValue("o", $"w{id}");
                command.Parameters.AddWithValue("r", run);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
                Interlocked.Increment(ref operational);
            }
        }

        async Task Attacker(int id)
        {
            await using var connection = new NpgsqlConnection(App.ConnectionString);
            await connection.OpenAsync(Ct);
            for (var i = 0; i < attempts; i++)
            {
                await using var command = new NpgsqlCommand(
                    $"UPDATE \"ProcessingRuns\" SET \"{Column}\" = jsonb_set(\"{Column}\", '{{limits,maxAiCallsPerRun}}', to_jsonb({1000 + id * 100 + i})), \"CallCount\" = \"CallCount\" + 1000 WHERE \"Id\" = @r", connection);
                command.Parameters.AddWithValue("r", run);
                var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
                Assert.Equal("23001", failure.SqlState);
                Interlocked.Increment(ref refused);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, writers).Select(i => Task.Run(() => Writer(i))).Concat(Enumerable.Range(0, attackers).Select(i => Task.Run(() => Attacker(i)))));

        Assert.Equal((writers * perWriter, attackers * attempts), (operational, refused));
        Assert.Equal(original, await SnapshotTextAsync(run));
        Assert.Equal(writers * perWriter, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));   // nessun UPDATE dell'attaccante è passato (nemmeno il +1000)
        Assert.Equal(writers * perWriter + 1, await Scalar<int>("SELECT \"FencingToken\"::int FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    // ================================================================ 7. run non documentali e vincolo

    [Fact]
    public async Task The_check_requires_the_snapshot_only_for_documentary_analysis_runs_and_never_blocks_other_workflows()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var user = await Scalar<Guid>("SELECT \"Id\" FROM \"AspNetUsers\" LIMIT 1");
        var conversation = await Scalar<Guid>("SELECT \"Id\" FROM \"Conversations\" WHERE \"ProjectId\" = @p", ("p", project));

        async Task InsertAsync(string definition, bool classified) => await Execute(
            $"""
            INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt","WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt"{(classified ? ",\"ClassificationStatus\",\"EffectiveDeadlineAt\",\"HardDeadlineAt\"" : "")})
            VALUES (@id,@p,'Analysis',@c,0,@k,@u,now(),'Queued',now() + interval '3 minutes',@d,'1.26.0',0,3,0,now(){(classified ? ",'Pending',now() + interval '3 minutes',now() + interval '4 minutes'" : "")})
            """,
            ("id", Guid.NewGuid()), ("p", project), ("c", conversation), ("k", Guid.NewGuid()), ("u", user), ("d", definition));

        // Run 001 (grafo v3, nessuna classificazione): nessuno snapshot richiesto, il vincolo non lo blocca.
        await InsertAsync("project-analysis-v3", classified: false);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"DocumentLimitsSnapshot\" IS NULL", ("p", project)));
        await Execute("UPDATE \"ProcessingRuns\" SET \"Status\" = 'Failed', \"ErrorCode\" = 'x' WHERE \"ProjectId\" = @p", ("p", project));   // gli UPDATE operativi restano liberi

        // Run documentale senza snapshot: rifiutato dal vincolo, sia per definizione documentale sia per root classificato.
        foreach (var (definition, classified) in new[] { ("project-analysis-documents-v5", false), ("project-analysis-documents-v4", false), ("project-analysis-documents-v2", false), ("project-analysis-v3", true) })
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(definition, classified));
            Assert.Equal(("23514", "CK_ProcessingRuns_DocumentLimitsSnapshot"), (failure.SqlState, failure.ConstraintName));
        }

        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"ProjectId\" = @p AND \"Kind\" = 'Analysis'", ("p", project)));

        // Snapshot presente ma malformato (non un oggetto con i campi attesi): rifiutato anche con tutti i campi nulli o di tipo sbagliato.
        foreach (var bad in new[] { "[]", "{}", "{\"limits\":{}}", "\"x\"", "{\"schemaVersion\":\"a\",\"policyRevision\":1,\"limits\":{},\"hash\":\"" + new string('a', 64) + "\"}", "{\"schemaVersion\":1,\"policyRevision\":1,\"limits\":{},\"hash\":\"zz\"}" })
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => Execute(
                $"""
                INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt","WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt","{Column}")
                VALUES (@id,@p,'Analysis',@c,0,@k,@u,now(),'Queued',now() + interval '3 minutes','project-analysis-documents-v4','1.26.0',0,3,0,now(),@j::jsonb)
                """,
                ("id", Guid.NewGuid()), ("p", project), ("c", conversation), ("k", Guid.NewGuid()), ("u", user), ("j", bad)));
            Assert.Equal("CK_ProcessingRuns_DocumentLimitsSnapshot", failure.ConstraintName);
        }
    }
}
