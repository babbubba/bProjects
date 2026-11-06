using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T133 sul preflight: il classificatore dei run documentali di Analizza usa lo snapshot dei limiti del run (timer per il PolicyHash, tetto di chiamate), mai la
/// policy admin corrente. Il run nasce dal vero POST Analizza e resta Pending; la policy cambia prima della classificazione reale (PostgreSQL, sonda finta senza LLM).
/// Chat e Genera non cambiano: continuano a leggere la policy corrente.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentRootClassificationSnapshotTests : ReconcileRig
{
    private sealed class Probe(RegimeMeasurement measurement) : IDocumentRegimeProbe
    {
        public int Calls;

        public Task<RegimeMeasurement> MeasureAsync(ClassificationOwnerKind kind, Guid rootId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(measurement);
        }
    }

    private async Task SetPolicyAsync(Func<DocumentLimits, DocumentLimits> change)
    {
        using var scope = App.Services.CreateScope();
        var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
        var limits = change(snapshot.Limits);
        Assert.Empty(limits.Validate());
        await Execute(
            "UPDATE \"DocumentPolicies\" SET \"Revision\" = \"Revision\" + 1, \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
    }

    /// <summary>Run Pending dal vero POST Analizza (nessun claim, nessun dispatcher).</summary>
    private async Task<Guid> PendingRunAsync()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "doc", "Il cliente A puo emettere al massimo 5 ordini al giorno.", "aaa");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var run = (await Json(response)).GetProperty("id").GetGuid();
        Assert.Equal("Pending", await Scalar<string>("SELECT \"ClassificationStatus\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        return run;
    }

    private Task<ClassificationResult> ClassifyAsync(Guid run, Probe probe) =>
        new DocumentRootClassifier(App.Services.GetRequiredService<IServiceScopeFactory>(), probe).ClassifyAsync(ClassificationOwnerKind.ProcessingRun, run, Ct);

    private sealed record Times(DateTime Requested, DateTime Hard, string PolicyHash);

    private async Task<Times> TimesAsync(Guid run) => new(
        await Scalar<DateTime>("SELECT \"RequestedAt\" AT TIME ZONE 'UTC' FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<DateTime>("SELECT \"HardDeadlineAt\" AT TIME ZONE 'UTC' FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
        await Scalar<string>("SELECT \"PolicyHash\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));

    private async Task AssertClassifiedWithOriginalAsync(Guid run, ClassificationResult result, Times before)
    {
        Assert.True(result.Attempt == ClassificationAttempt.Classified, result.Attempt + " " + await Message(run));
        Assert.Equal(("Classified", "Ordinary"), (
            await Scalar<string>("SELECT \"ClassificationStatus\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
            await Scalar<string>("SELECT \"Regime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))));
        var after = await TimesAsync(run);
        Assert.Equal(before, after);   // t0, cap e PolicyHash del root invariati
        // Ordinary dal t0 originale con i secondi dello snapshot (180), mai quelli correnti.
        Assert.Equal(180, await Scalar<double>("SELECT extract(epoch FROM \"OrdinaryDeadlineAt\" - \"RequestedAt\")::float8 FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Id\" = @r AND \"ErrorCode\" = 'config_invalid'", ("r", run)));
    }

    [Fact]
    public async Task Changing_the_admin_timers_after_creation_and_before_classification_leaves_the_run_classified_with_the_original_deadline()
    {
        var run = await PendingRunAsync();
        var before = await TimesAsync(run);
        await SetPolicyAsync(l => l with { OrdinaryDeadlineSeconds = 90 });

        var result = await ClassifyAsync(run, new Probe(DocumentRootClassificationInputTests.Fit));

        await AssertClassifiedWithOriginalAsync(run, result, before);
    }

    [Fact]
    public async Task Lowering_the_admin_call_cap_after_creation_does_not_fail_the_planned_calls_of_a_run_created_under_a_higher_cap()
    {
        var run = await PendingRunAsync();
        var before = await TimesAsync(run);
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 1 });

        var result = await ClassifyAsync(run, new Probe(DocumentRootClassificationInputTests.Fit with { PlannedCalls = 3 }));

        // Il tetto originale (512) ammette 3 chiamate pianificate; nel regime corrente (1) sarebbero oltre il limite. La misura non cambia il regime di partenza.
        Assert.True(result.Attempt == ClassificationAttempt.Classified, result.Attempt + " " + await Message(run));
        Assert.Equal(before, await TimesAsync(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"Id\" = @r AND \"ErrorCode\" IS NOT NULL", ("r", run)));
    }

    [Fact]
    public async Task Raising_the_admin_call_cap_after_creation_does_not_admit_planned_calls_beyond_the_cap_the_run_was_created_with()
    {
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 2 });
        var run = await PendingRunAsync();
        await SetPolicyAsync(l => l with { MaxAiCallsPerRun = 512 });

        var result = await ClassifyAsync(run, new Probe(DocumentRootClassificationInputTests.Fit with { PlannedCalls = 3 }));

        Assert.Equal(ClassificationAttempt.Failed, result.Attempt);
        Assert.Equal("Failed", await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.NotEqual("config_invalid", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task Changing_both_timers_and_call_cap_classifies_with_the_original_values_and_no_config_invalid()
    {
        var run = await PendingRunAsync();
        var before = await TimesAsync(run);
        await SetPolicyAsync(l => l with { OrdinaryDeadlineSeconds = 60, MaxAiCallsPerRun = 1, ExtendedDeadlineSeconds = 7_200 });

        var result = await ClassifyAsync(run, new Probe(DocumentRootClassificationInputTests.Fit));

        await AssertClassifiedWithOriginalAsync(run, result, before);
    }

    public static IEnumerable<object[]> BadSnapshots()
    {
        var good = RunLimitsSnapshot.Of(DocumentPolicySnapshot.Of(new DocumentOptions().ToLimits(), 1));
        yield return ["hash_incoerente", good.ToJson().Replace(good.Hash, new string('0', 64))];
        yield return ["schema_non_supportato", good.ToJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":2")];
        yield return ["mancante", ""];
    }

    [Theory]
    [MemberData(nameof(BadSnapshots))]
    public async Task An_invalid_or_missing_snapshot_fails_the_run_config_invalid_without_measuring_and_never_uses_the_current_policy(string name, string json)
    {
        var run = await PendingRunAsync();
        await Execute("ALTER TABLE \"ProcessingRuns\" DROP CONSTRAINT \"CK_ProcessingRuns_DocumentLimitsSnapshot\"");
        await Execute("ALTER TABLE \"ProcessingRuns\" DISABLE TRIGGER \"TR_ProcessingRuns_DocumentLimitsSnapshot\"");
        await Execute("UPDATE \"ProcessingRuns\" SET \"DocumentLimitsSnapshot\" = NULLIF(@j, '')::jsonb WHERE \"Id\" = @r", ("j", json), ("r", run));
        var probe = new Probe(DocumentRootClassificationInputTests.Fit);

        var result = await ClassifyAsync(run, probe);

        Assert.True(result.Attempt == ClassificationAttempt.Failed, name + ": " + result.Attempt);
        Assert.Equal(("Failed", "config_invalid"), (
            await Scalar<string>("SELECT \"Status\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)),
            await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task A_root_policy_hash_that_no_longer_matches_the_snapshot_timers_fails_config_invalid()
    {
        var run = await PendingRunAsync();
        await Execute("UPDATE \"ProcessingRuns\" SET \"PolicyHash\" = repeat('9', 64) WHERE \"Id\" = @r", ("r", run));
        var probe = new Probe(DocumentRootClassificationInputTests.Fit);

        var result = await ClassifyAsync(run, probe);

        Assert.Equal(ClassificationAttempt.Failed, result.Attempt);
        Assert.Equal("config_invalid", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_chat_turn_still_fails_config_invalid_when_the_admin_timers_change_before_classification_nothing_changes_for_other_flows()
    {
        // Chat (e Genera) non hanno snapshot: restano sulla policy corrente. Turno Pending fabbricato come negli altri test del classificatore.
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var conversation = await Scalar<Guid>("SELECT \"Id\" FROM \"Conversations\" WHERE \"ProjectId\" = @p", ("p", project));
        var user = await Scalar<Guid>("SELECT \"Id\" FROM \"AspNetUsers\" LIMIT 1");
        var (message, turn) = (Guid.NewGuid(), Guid.NewGuid());
        await Execute(
            "INSERT INTO \"ConversationMessages\" (\"Id\",\"ConversationId\",\"Sequence\",\"Role\",\"Body\",\"CreatedAt\",\"ClientMessageId\",\"AuthorId\") VALUES (@m,@c,1,'User','ciao',now(),@k,@u)",
            ("m", message), ("c", conversation), ("k", Guid.NewGuid()), ("u", user));
        await Execute(
            """
            INSERT INTO "ChatTurns" ("Id","ConversationId","TriggerMessageId","SourceCutoffSequence","Status","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","FencingToken","SessionRevisionAtStart")
            VALUES (@t,@c,@m,1,'Queued',now(),now() + interval '1 hour',0,3,0,0)
            """,
            ("t", turn), ("c", conversation), ("m", message));
        var hash = DocumentRegimePolicy.ComputePolicyHash(180, 3_600);
        await Execute(
            """
            UPDATE "ChatTurns" SET "ClassificationStatus" = 'Pending', "Regime" = 'Extended', "ClassificationVersion" = 'document-regime-v2', "ClassificationReason" = 'preflight_pending',
                "PolicyHash" = @h, "HardDeadlineAt" = "RequestedAt" + interval '3600 seconds', "EffectiveDeadlineAt" = "RequestedAt" + interval '3600 seconds',
                "DeadlineAt" = "RequestedAt" + interval '3600 seconds', "PromotionCount" = 0, "CallCount" = 0
            WHERE "Id" = @t
            """,
            ("h", hash), ("t", turn));
        await SetPolicyAsync(l => l with { OrdinaryDeadlineSeconds = 90 });
        var probe = new Probe(DocumentRootClassificationInputTests.Fit);

        var result = await new DocumentRootClassifier(App.Services.GetRequiredService<IServiceScopeFactory>(), probe).ClassifyAsync(ClassificationOwnerKind.ChatTurn, turn, Ct);

        Assert.Equal(ClassificationAttempt.Failed, result.Attempt);
        Assert.Equal("config_invalid", await Scalar<string>("SELECT \"ErrorCode\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(0, probe.Calls);
    }
}
