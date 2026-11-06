using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T131 (PG + modello FINTO) — riconciliazione gerarchica per run con alias (contracts/ai-workflow.md §11). Il modello finto legge SOLO il messaggio
/// realmente inviato (alias E/N/i, mai UUID o hash) e risponde con il DTO `reconcile-alias-1`; il server compone il canonico, lo valida con il motore
/// condiviso di T072–T077 e committa la revisione. I run sono creati dal vero endpoint Analizza (POST), quindi v4.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentReconcileAliasWorkflowTests : ReconcileRig
{
    private const int Documents = 8;
    private const string V4 = "project-analysis-documents-v4";

    private static readonly Regex AnyUuid = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", RegexOptions.Compiled);
    private static readonly Regex AnyHash = new("[0-9a-f]{64}", RegexOptions.Compiled);

    /// <summary>Sezione di destinazione per numero di requisito: una per ciascuna sezione di analysis (la 8 è un'assunzione).</summary>
    private static readonly string[] Sections = ["goals", "requirements", "constraints", "dependencies", "scopeIncluded", "flows", "scopeExcluded", "assumptions"];

    private static string Statement(int n, int words = 120) => $"Requisito {n}: " + string.Join(' ', Enumerable.Range(0, words).Select(i => $"w{n}x{i}"));

    // ================================================================ modello finto reconcile-alias-1

    internal sealed record AliasParsed(
        string Stage, string View, (string Alias, string Statement)[] Voci, string[] Nodes, (string Handle, string Evidence, string Text)[] Items, string? Focus, string Text)
    {
        public int NumberOf(string alias) => int.Parse(Regex.Match(Voci.Single(v => v.Alias == alias).Statement, @"Requisito (\d+):").Groups[1].Value);
    }

    internal static AliasParsed ParseAlias(string text)
    {
        var stage = Regex.Match(text, "^Tipo di batch: (\\w+)$", RegexOptions.Multiline).Groups[1].Value switch
        {
            "foglia" => "leaf", "riduzione" => "reduce", "espansione" => "expand", "confronto" => "compare", var s => s,
        };
        var view = Regex.Match(text, "^Vista: (\\w+)$", RegexOptions.Multiline).Groups[1].Value;
        var voci = Regex.Matches(text, @"<<<VOCE \[(E[1-9][0-9]*)\][^>\n]*>>>\n(.*)\n<<<FONTE").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToArray();
        var nodes = Regex.Matches(text, @"<<<NODO \[(N[1-9][0-9]*)\]").Select(m => m.Groups[1].Value).ToArray();
        var items = Regex.Matches(text, @"^\[(i[1-9][0-9]*)\] \((E[1-9][0-9]*(?:, E[1-9][0-9]*)*)\) (.*)$", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)).ToArray();
        var focus = Regex.Match(text, "^Candidato in esame: (.*)$", RegexOptions.Multiline);
        return new(stage, view, voci, nodes, items, focus.Success ? focus.Groups[1].Value : null, text);
    }

    private static string Item(string key, string section, string text, string classification, params string[] evidence) =>
        JsonSerializer.Serialize(new { key, section, text, classification, evidence });

    /// <summary>Risposta valida: una voce per evidenza nella foglia A (sezione per numero di requisito), descriptor dove si costruiscono nodi, nessun collegamento altrove.</summary>
    internal static string Dto(
        AliasParsed p, Func<AliasParsed, JsonObject, JsonObject>? change = null, bool descriptorOnly = false)
    {
        var items = new JsonArray();
        if (p is { Stage: "leaf", View: "A" } && !descriptorOnly)
        {
            var n = 0;
            foreach (var (alias, statement) in p.Voci)
            {
                n++;
                var number = p.NumberOf(alias);
                var section = Sections[(number - 1) % Sections.Length];
                items.Add(JsonNode.Parse(Item($"i{n}", section, statement, section == "assumptions" ? "Assumed" : "Documented", alias)));
            }
        }

        var obj = new JsonObject
        {
            ["contract"] = "reconcile-alias-1",
            ["descriptor"] = p.Stage is "leaf" or "reduce" ? $"gruppo {p.Stage} {p.View} {string.Join('-', p.Voci.Select(v => v.Alias).Concat(p.Nodes))}" : null,
            ["noFindings"] = items.Count > 0 ? null : "nessun collegamento emerso",
            ["items"] = items,
            ["candidates"] = new JsonArray(),
            ["focus"] = p.Stage == "compare" ? JsonNode.Parse("""{"outcome":"Compared","reason":null}""") : null,
            ["patches"] = new JsonArray(),
        };
        return (change?.Invoke(p, obj) ?? obj).ToJsonString();
    }

    private static JsonObject Link(string left, string right, string type = "Contradicts") => new()
    {
        ["key"] = "f1", ["left"] = left, ["right"] = right, ["type"] = type, ["basis"] = "Inferred", ["rationale"] = "30 giorni nel documento contro 45 giorni dichiarati",
    };

    /// <summary>
    /// Modello finto del corpus «sharedKey»: l'espansione che riapre i documenti 1 e 8 NELLO STESSO batch trova il collegamento (usa solo alias ricevuti in quel messaggio), il confronto
    /// è Open (conflitto) o Compared (con una patch sulla base del server). Con <paramref name="unresolved"/> l'espansione segnala un collegamento senza estremi.
    /// </summary>
    private static DocumentChatWorkflowTests.FakeChatModel LinkModel(string compareOutcome = "Open", bool patch = false, bool unresolved = false) => new()
    {
        Responder = r =>
        {
            var p = ParseAlias(r.AllText);
            var one = p.Voci.Select(v => v.Alias).FirstOrDefault(a => p.NumberOf(a) == 1);
            var eight = p.Voci.Select(v => v.Alias).FirstOrDefault(a => p.NumberOf(a) == Documents);
            return Dto(p, (_, o) =>
            {
                if (p.Stage == "expand" && unresolved)
                {
                    o["focus"] = JsonNode.Parse("""{"outcome":"Unresolved","reason":"collegamento senza estremi"}""");
                }
                else if (p.Stage == "expand" && one is not null && eight is not null)
                {
                    o["candidates"] = new JsonArray(Link(one, eight));
                    o["noFindings"] = null;
                }
                else if (p.Stage == "compare")
                {
                    o["focus"] = JsonNode.Parse(compareOutcome == "Open"
                        ? """{"outcome":"Open","reason":"30 giorni nel documento contro 45 giorni dichiarati: alternative integrali da sottoporre al PM"}"""
                        : """{"outcome":"Compared","reason":null}""");
                    if (patch && one is not null && eight is not null && p.Items.FirstOrDefault(i => i.Evidence.Contains(one)) is { Handle: not null } target)
                    {
                        o["patches"] = new JsonArray(new JsonObject
                        {
                            ["key"] = "p1", ["op"] = "AddRelation", ["target"] = new JsonObject { ["item"] = target.Handle, ["relation"] = null }, ["evidence"] = new JsonArray(),
                            ["value"] = new JsonObject { ["from"] = one, ["to"] = eight, ["type"] = "Confirms", ["basis"] = "Inferred", ["rationale"] = "stesso requisito ribadito" },
                        });
                        o["noFindings"] = null;
                    }
                }

                return o;
            });
        },
    };

    // ================================================================ scenari

    [Fact]
    public async Task The_real_analyze_endpoint_creates_a_v4_run_with_the_hierarchical_alias_versions_frozen()
    {
        var (run, _) = await ArrangeAsync(documents: 1);

        Assert.Equal(V4, await Scalar<string>("SELECT \"WorkflowDefinitionVersion\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        var prompts = JsonDocument.Parse(await Scalar<string>("SELECT \"PromptVersions\"::text FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))).RootElement;
        Assert.Equal("document-analysis-v3", prompts.GetProperty("analysis").GetString());
        Assert.Equal("document-analysis-alias-1", prompts.GetProperty("analysisAiSchema").GetString());
        Assert.Equal("document-reconcile-v3", prompts.GetProperty("reconcile").GetString());
        Assert.Equal("reconcile-alias-1", prompts.GetProperty("reconcileAiSchema").GetString());
        Assert.Equal("global-reconcile-v1", prompts.GetProperty("reconcileAlgorithm").GetString());
    }

    [Fact]
    public async Task A_small_v4_payload_uses_the_single_call_alias_path_without_hierarchical_batches()
    {
        var (run, claim) = await ArrangeAsync(documents: 1);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Single(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(V4, await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        Assert.Equal("document-analysis-alias-1", await Scalar<string>("SELECT \"SchemaVersion\" FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_v4_run_above_budget_reaches_the_hierarchy_with_isolated_batches_and_alias_only_messages()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.True(model.Requests.Count > 1);
        Assert.All(model.Requests, r => Assert.Single(r.Messages));
        Assert.All(model.Requests, r =>
        {
            var everything = r.System + "\n" + r.AllText;
            Assert.DoesNotMatch(AnyUuid, everything);
            Assert.DoesNotMatch(AnyHash, everything);
            Assert.DoesNotContain("batchId", everything);
            Assert.DoesNotContain("inputHash", everything);
            Assert.DoesNotContain("entryId", everything);
        });
        Assert.All(model.Requests, r => Assert.Equal(ReconcileHierarchyPrompt, r.System.Replace("\r\n", "\n")));
        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r AND \"SchemaVersion\" = '1.1.0'", ("r", run)));
        Assert.True(await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)) >= 3);
        // Copertura completa: ogni evidenza è mappata su un item della revisione e visitata in A e B.
        Assert.Equal(Documents, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
        Assert.Equal(Documents, model.Requests.Select(r => ParseAlias(r.AllText)).Where(p => p is { Stage: "leaf", View: "A" }).SelectMany(p => p.Voci).Select(v => v.Alias).Distinct().Count());
        Assert.Equal(Documents, model.Requests.Select(r => ParseAlias(r.AllText)).Where(p => p is { Stage: "leaf", View: "B" }).SelectMany(p => p.Voci).Select(v => v.Alias).Distinct().Count());
        Assert.Equal(2 * Documents, await Count("SELECT count(*) FROM \"RunCatalogMembers\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task Hierarchical_v4_preserves_the_sections_classifications_and_sources_of_the_alias_items()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Single(content.Goals);
        Assert.Single(content.Requirements);
        Assert.Single(content.Constraints);
        Assert.Single(content.Dependencies);
        Assert.Single(content.ContextAndScope.Included);
        Assert.Single(content.ContextAndScope.Excluded);
        Assert.Single(content.OperationalFlows);
        Assert.Single(content.Assumptions);
        Assert.Empty(content.ContextAndScope.Overview);
        Assert.Empty(content.OpenQuestions);
        Assert.Equal(DocumentAnalysisClassification.Assumed, content.Assumptions[0].Classification);
        Assert.Contains("Requisito 1:", content.Goals[0].Text);
        Assert.Contains("Requisito 2:", content.Requirements[0].Text);
        Assert.Contains("Requisito 5:", content.ContextAndScope.Included[0].Text);
        Assert.All(content.Goals.Concat(content.Requirements).Concat(content.Constraints), i =>
        {
            Assert.Equal(DocumentAnalysisClassification.Documented, i.Classification);
            Assert.Single(i.EvidenceRefs);
            Assert.Contains(content.Sources, s => s.Id == i.EvidenceRefs[0] && s.Quote!.StartsWith("Requisito", StringComparison.Ordinal));
        });
        Assert.Equal(Documents, content.Sources.Count);
        Assert.Empty(content.Conflicts);
    }

    [Fact]
    public async Task Evidence_in_distant_batches_with_a_cross_document_conflict_is_kept_as_an_open_conflict_with_both_alternatives_and_sources()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var model = LinkModel("Open");

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Contains(model.Requests.Select(r => ParseAlias(r.AllText)), p => p.Stage == "expand");
        Assert.Contains(model.Requests.Select(r => ParseAlias(r.AllText)), p => p.Stage == "compare");
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        var conflict = Assert.Single(content.Conflicts);
        Assert.Equal(AnalysisConflictStatus.Open, conflict.Status);
        Assert.Null(conflict.Resolution);
        Assert.Equal(2, conflict.Alternatives.Count);
        Assert.All(conflict.Alternatives, a => Assert.NotEmpty(a.EvidenceRefs));
        Assert.Equal(2, conflict.Alternatives.SelectMany(a => a.EvidenceRefs).Distinct().Count());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"RunCandidateDispositions\" WHERE \"RunId\" = @r AND \"State\" = 'Open'", ("r", run)));
        Assert.All(model.Requests, r => Assert.DoesNotMatch(AnyUuid, r.AllText));
    }

    [Fact]
    public async Task A_valid_applied_alias_patch_is_composed_on_the_server_base_stored_and_completes_the_run_with_its_relation()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var model = LinkModel("Compared", patch: true);

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"RunPatchDispositions\" WHERE \"RunId\" = @r AND \"Kind\" = 'Applied'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunPatchDispositions\" WHERE \"RunId\" = @r AND \"Kind\" <> 'Applied'", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisEvidenceRelations\" WHERE \"RunId\" = @r AND \"Type\" = 'Confirms' AND \"Basis\" = 'Inferred'", ("r", run)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task Work_outputs_store_the_composed_canonical_output_with_schema_version_and_the_three_verified_hashes()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var model = LinkModel("Open");

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r AND \"SchemaVersion\" = 'reconcile-alias-1'", ("r", run)));
        Assert.Equal(V4, await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
        var payloads = new List<JsonElement>();
        foreach (var batch in await Strings("SELECT \"PayloadJson\"::text FROM \"WorkOutputs\" WHERE \"OwnerId\" = @r", ("r", run)))
        {
            payloads.Add(JsonDocument.Parse(batch).RootElement.Clone());
        }

        Assert.All(payloads, p =>
        {
            Assert.Equal("reconcile-alias-1", p.GetProperty("schemaVersion").GetString());
            foreach (var name in new[] { "catalogHash", "aiOutputHash", "canonicalHash" })
            {
                Assert.Matches("^[0-9a-f]{64}$", p.GetProperty(name).GetString()!);
            }

            var canonical = ReconciliationCodec.DecodeResult(p.GetProperty("output").GetRawText());
            Assert.Equal(p.GetProperty("canonicalHash").GetString(), CanonicalJson.Sha256Hex(ReconciliationCodec.Encode(canonical)));
            Assert.Equal(JsonValueKind.Object, p.GetProperty("ai").ValueKind);
            Assert.Equal(JsonValueKind.Object, p.GetProperty("catalog").ValueKind);
        });
    }

    [Fact]
    public async Task Replaying_a_completed_run_and_resuming_an_interrupted_one_send_nothing_already_committed_and_reuse_the_verified_outputs()
    {
        var (run, claim) = await ArrangeAsync();
        var seen = 0;
        var failing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Interlocked.Increment(ref seen) == 4 ? throw new InvalidOperationException("rete caduta") : Dto(ParseAlias(r.AllText)),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        Assert.Equal(3, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));

        var healthy = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        var all = healthy.Requests.Count + 3;
        Assert.Equal(all, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(all, await Count("SELECT count(*) FROM \"DerivedBatches\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r", ("r", run)));
        Assert.Equal(failing.Requests.Count + healthy.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));   // la chiamata caduta ha consumato la sua riserva (costo prudenziale)
        var resent = healthy.Requests.Select(r => r.AllText).Intersect(failing.Requests.Take(3).Select(r => r.AllText)).ToList();
        Assert.Empty(resent);   // nessun batch già Committed è reinviato

        var again = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };
        var replay = await RunAsync(claim, again);
        Assert.Equal(resumed.ResultRevisionId, replay.ResultRevisionId);
        Assert.Empty(again.Requests);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_crash_after_a_committed_batch_replays_without_a_new_inference_even_for_the_sections()
    {
        var (run, claim) = await ArrangeAsync();
        var seen = 0;
        // Cade subito dopo le foglie A: l'assemblaggio dovrà ricostruire le sezioni dagli output Committed (nessuna cache di processo).
        var failing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Interlocked.Increment(ref seen) == 6 ? throw new InvalidOperationException("processo terminato") : Dto(ParseAlias(r.AllText)),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));

        var healthy = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };
        var resumed = await RunAsync(claim, healthy);

        Assert.True(resumed.Status == ProcessingRunStatus.Completed, await Message(run));
        var content = DocumentAnalysisContentJson.Deserialize(await Scalar<string>("SELECT \"ContentJson\"::text FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Single(content.Goals);
        Assert.Single(content.Assumptions);
        Assert.Single(content.Constraints);
    }

    [Fact]
    public async Task A_stored_output_whose_verified_hash_does_not_match_is_rejected_on_replay_without_a_new_inference()
    {
        var (run, claim) = await ArrangeAsync();
        var seen = 0;
        var failing = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Interlocked.Increment(ref seen) == 4 ? throw new InvalidOperationException("rete caduta") : Dto(ParseAlias(r.AllText)),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(claim, failing));
        await Execute(
            """
            ALTER TABLE "WorkOutputs" DISABLE TRIGGER USER;
            UPDATE "WorkOutputs" SET "PayloadJson" = jsonb_set("PayloadJson", '{canonicalHash}', to_jsonb(repeat('b', 64)))
            WHERE "OwnerId" = @r AND "BatchId" = (SELECT "BatchId" FROM "DerivedBatches" WHERE "OwnerId" = @r AND "Ordinal" = 0 ORDER BY "CreatedAt" LIMIT 1);
            ALTER TABLE "WorkOutputs" ENABLE TRIGGER USER;
            """,
            ("r", run));

        var healthy = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };
        var resumed = await RunAsync(claim, healthy);

        Assert.Equal(ProcessingRunStatus.Failed, resumed.Status);
        Assert.True(resumed.ErrorCode == DocumentWorkException.IntegrityConflict, resumed.ErrorCode + ": " + await Message(run));
        Assert.Empty(healthy.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    // ---------------------------------------------------------------- output invalido: nessuna riparazione, nessun WorkOutput

    [Theory]
    [InlineData("unknown_alias")]
    [InlineData("malformed_alias")]
    [InlineData("duplicate_alias")]
    [InlineData("duplicate_key")]
    [InlineData("alias_of_another_batch")]
    [InlineData("invented_uuid_property")]
    [InlineData("items_outside_leaf_a")]
    [InlineData("patch_in_leaf")]
    public async Task An_invalid_alias_output_is_a_permanent_error_with_no_work_output_no_repair_and_no_fuzzy_correction(string kind)
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r =>
            {
                var p = ParseAlias(r.AllText);
                return Dto(p, (_, o) =>
                {
                    var first = p.Voci.FirstOrDefault().Alias;
                    switch (kind)
                    {
                        case "unknown_alias" when p is { Stage: "leaf", View: "A" }:
                            o["items"]![0]!["evidence"] = new JsonArray("E999");
                            break;
                        case "malformed_alias" when p is { Stage: "leaf", View: "A" }:
                            o["items"]![0]!["evidence"] = new JsonArray(first.ToLowerInvariant());
                            break;
                        case "duplicate_alias" when p is { Stage: "leaf", View: "A" }:
                            o["items"]![0]!["evidence"] = new JsonArray(first, first);
                            break;
                        case "duplicate_key" when p is { Stage: "leaf", View: "A" } && p.Voci.Length > 1:
                            o["items"]![1]!["key"] = "i1";
                            break;
                        case "alias_of_another_batch" when p is { Stage: "leaf", View: "A" }:
                            o["items"]![0]!["evidence"] = new JsonArray($"E{Documents}");   // esiste nel run ma non in questo batch (la prima foglia non lo contiene)
                            break;
                        case "invented_uuid_property":
                            o["batchId"] = Guid.NewGuid().ToString();
                            break;
                        case "items_outside_leaf_a" when p is { Stage: "leaf", View: "B" }:
                            o["items"] = new JsonArray(JsonNode.Parse(Item("i1", "requirements", "testo", "Documented", first)));
                            break;
                        case "patch_in_leaf" when p is { Stage: "leaf", View: "A" }:
                            o["patches"] = new JsonArray(JsonNode.Parse("""{"key":"p1","op":"AddSupport","target":{"item":"i1","relation":null},"evidence":["E1"],"value":{}}"""));
                            break;
                    }

                    return o;
                });
            },
        };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.WorkOutputInvalid, result.ErrorCode + ": " + await Message(run));
        Assert.Equal(model.Requests.Count - 1, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_valid_output_that_leaves_an_evidence_unmapped_is_kept_but_never_completes_the_run_and_is_never_retried_or_corrected()
    {
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel
        {
            Responder = r => Dto(ParseAlias(r.AllText), (p, o) =>
            {
                if (p is { Stage: "leaf", View: "A" } && o["items"] is JsonArray { Count: > 1 } items)
                {
                    items.RemoveAt(0);
                }

                return o;
            }),
        };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CoverageIncomplete, result.ErrorCode + ": " + await Message(run));
        Assert.Equal(model.Requests.Count, await Count("SELECT count(*) FROM \"WorkOutputs\" WHERE \"OwnerKind\" = 'ProcessingRun' AND \"OwnerId\" = @r AND \"Status\" = 'Committed'", ("r", run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));   // nessun retry guidato nel percorso gerarchico
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task An_irreducible_batch_fails_explicitly_with_no_revision()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        await NewDocument(client, project, "enorme", "Requisito 1: " + string.Join(' ', Enumerable.Range(0, 3000).Select(i => $"w{i}")), "aaa");
        await NewDocument(client, project, "altro", Statement(2), "bbb");
        var run = await StartV4Async(client, project);
        var claim = await ClaimAsync(run);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.DocumentIncomplete, result.ErrorCode + ": " + await Message(run));
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task An_unfinished_frontier_never_completes_the_run_an_unresolved_candidate_stays_not_processed_and_is_reported()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var model = LinkModel(unresolved: true);

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CoverageIncomplete, result.ErrorCode + ": " + await Message(run));
        Assert.Contains("candidate_unresolved", await Message(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisEvidenceLinks\" WHERE \"RunId\" = @r", ("r", run)));
    }

    // ---------------------------------------------------------------- limiti dalla policy congelata

    [Fact]
    public async Task The_frozen_policy_call_cap_stops_the_run_at_exactly_the_cap()
    {
        // I limiti si fissano nello snapshot alla creazione del run: la policy va modificata PRIMA della richiesta (dopo, il run conserva quelli originali).
        await SetLimitsAsync(l => l with { MaxAiCallsPerRun = 3 });
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model, new DocumentOptions { MaxAiCallsPerRun = 512 });   // le opzioni correnti non prevalgono sulla policy congelata

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CallLimitExceeded, result.ErrorCode + ": " + await Message(run));
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(3, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_frozen_policy_level_cap_stops_the_hierarchy_with_coverage_incomplete()
    {
        // I limiti si fissano nello snapshot alla creazione del run: la policy va modificata PRIMA della richiesta (dopo, il run conserva quelli originali).
        await SetLimitsAsync(l => l with { MaxReconciliationLevels = 1 });
        var (run, claim) = await ArrangeAsync();
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CoverageIncomplete, result.ErrorCode + ": " + await Message(run));
        Assert.Contains("livelli congelati", await Message(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_frozen_policy_candidate_cap_stops_the_run_when_a_proposed_candidate_exceeds_it()
    {
        // I limiti si fissano nello snapshot alla creazione del run: la policy va modificata PRIMA della richiesta (dopo, il run conserva quelli originali).
        await SetLimitsAsync(l => l with { MaxCandidatesPerRun = 1 });
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var model = LinkModel("Compared");

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CoverageIncomplete, result.ErrorCode + ": " + await Message(run));
        Assert.Contains("oltre 1 candidati", await Message(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task The_frozen_policy_derived_bytes_cap_stops_the_run_before_the_next_candidate_batch()
    {
        // I limiti si fissano nello snapshot alla creazione del run: la policy va modificata PRIMA della richiesta (dopo, il run conserva quelli originali).
        await SetLimitsAsync(l => l with { MaxDerivedBytes = 1 });
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var model = LinkModel("Compared");

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CoverageIncomplete, result.ErrorCode + ": " + await Message(run));
        Assert.Contains("byte derivati esauriti", await Message(run));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"AnalysisRevisions\" WHERE \"AnalysisId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_valid_promotion_keeps_t0_and_the_hard_cap_and_does_not_reset_the_counters_for_v4()
    {
        var (run, claim) = await ArrangeAsync(ordinary: true);
        var before = await Scalar<DateTime>("SELECT \"RequestedAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        var hardBefore = await Scalar<DateTime>("SELECT \"HardDeadlineAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run));
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal(("Extended", 1), (await Scalar<string>("SELECT \"Regime\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)), await Scalar<int>("SELECT \"PromotionCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run))));
        Assert.Equal(before, await Scalar<DateTime>("SELECT \"RequestedAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(hardBefore, await Scalar<DateTime>("SELECT \"HardDeadlineAt\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
        Assert.Equal(model.Requests.Count, await Scalar<int>("SELECT \"CallCount\" FROM \"ProcessingRuns\" WHERE \"Id\" = @r", ("r", run)));
    }

    // ---------------------------------------------------------------- compatibilità storica

    [Fact]
    public async Task A_historical_v3_run_above_budget_keeps_failing_as_documented_and_never_reaches_the_hierarchy()
    {
        var (run, claim) = await ArrangeAsync();
        await FreezeHistoricalAsync(run, "project-analysis-documents-v3", V3Versions);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => Dto(ParseAlias(r.AllText)) };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.True(result.ErrorCode == DocumentWorkException.CallLimitExceeded, result.ErrorCode + ": " + await Message(run));
        Assert.Contains("non è ancora disponibile", await Message(run));
        Assert.Empty(model.Requests);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"RunCatalogNodes\" WHERE \"RunId\" = @r", ("r", run)));
    }

    [Fact]
    public async Task A_historical_v3_run_within_budget_still_completes_with_its_own_v3_definition_in_the_manifest()
    {
        var (run, claim) = await ArrangeAsync(documents: 1);
        await FreezeHistoricalAsync(run, "project-analysis-documents-v3", V3Versions);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        Assert.Equal("project-analysis-documents-v3", await Scalar<string>("SELECT \"DefinitionVersion\" FROM \"WorkManifests\" WHERE \"OwnerId\" = @r", ("r", run)));
    }

    [Theory]
    [InlineData("""{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v2","validation":"document-analysis-validation-v2"}""")]   // v4 senza le versioni della riconciliazione
    [InlineData("""{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v2","validation":"document-analysis-validation-v2","reconcile":"document-reconcile-v2","reconcileAiSchema":"reconcile-alias-1","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1"}""")]   // prompt di riconciliazione v2 con definizione v4
    [InlineData("""{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v2","validation":"document-analysis-validation-v2","reconcile":"document-reconcile-v3","reconcileAiSchema":"reconcile-alias-9","reconcileAlgorithm":"global-reconcile-v1","reconcileCatalog":"reconcile-alias-catalog-1"}""")]   // schema AI della riconciliazione ignoto
    public async Task A_v4_run_with_missing_or_foreign_frozen_versions_fails_workflow_incompatible_without_reinterpretation(string versions)
    {
        var (run, claim) = await ArrangeAsync(documents: 1);
        await FreezeHistoricalAsync(run, V4, versions);
        var model = new DocumentChatWorkflowTests.FakeChatModel { Responder = r => AliasModelReplies.Reply(r.AllText) };

        var result = await RunAsync(claim, model);

        Assert.Equal(ProcessingRunStatus.Failed, result.Status);
        Assert.Equal("workflow_incompatible", result.ErrorCode);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task The_input_hash_published_for_every_batch_is_the_hash_of_the_payload_the_model_actually_receives_with_prompt_schema_candidate_items_and_nodes()
    {
        var (run, claim) = await ArrangeAsync(sharedKey: true);
        var model = LinkModel("Open");

        var result = await RunAsync(claim, model);

        Assert.True(result.Status == ProcessingRunStatus.Completed, await Message(run));
        var stages = model.Requests.Select(r => ParseAlias(r.AllText).Stage).ToList();
        Assert.Contains("expand", stages);      // con candidato in esame
        Assert.Contains("compare", stages);     // con candidato, item già proposti e relazioni
        Assert.Contains("reduce", stages);      // con i descriptor dei nodi
        var options = new AiOptions
        {
            Endpoint = "http://dgx.internal:8080/v1/", ModelId = "Qwen-A", ApiKey = "test-key", ContextWindowTokens = DocumentChatWorkflowTests.FakeChatModel.Window, MaxOutputTokens = 1500,
            SafetyMarginTokens = 20, RuntimeCheckSeconds = 60,
        };
        using var meterClient = new InternalModelClient(options, model);
        var monitor = new ModelRuntimeMonitor(meterClient, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelRuntimeMonitor>.Instance);
        var preflight = new AgentFactory(options, meterClient.ChatClient, (text, ct) => new(meterClient.CountTokensAsync(text, ct)), null, monitor, meterClient).Preflight;
        var schema = ReconcileAliasContracts.JsonSchema().GetRawText();
        for (var k = 0; k < model.Requests.Count; k++)
        {
            var sent = model.Requests[k];
            var published = await Scalar<string>("SELECT \"InputHash\" FROM \"DerivedBatches\" WHERE \"OwnerId\" = @r ORDER BY \"Ordinal\" LIMIT 1 OFFSET " + k, ("r", run));
            var measured = await preflight.MeasureAsync(
                new DocumentPayload(AiPhase.Analysis, sent.System, [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, sent.AllText)], schema), Ct);
            Assert.Equal(published, measured.PayloadHash);   // il payload misurato (istruzioni, messaggio e schema) è quello inviato, batch per batch
            Assert.True(measured.Fits);
        }
    }

    // ================================================================ supporto

    /// <summary>Prompt `document-reconcile-v3`: risorsa incorporata nell'assembly dell'app (le istruzioni di sistema che il modello riceve nel percorso alias).</summary>
    private static string ReconcileHierarchyPrompt => LoadReconcilePrompt();

    private static string LoadReconcilePrompt()
    {
        using var stream = typeof(ReconciliationHierarchy).Assembly.GetManifestResourceStream("Prompts.document-reconcile-v3.md")
            ?? throw new InvalidOperationException("Prompt document-reconcile-v3 non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd().Trim().Replace("\r\n", "\n");
    }

    private const string V3Versions =
        """{"analysis":"document-analysis-v3","analysisSchema":"1.1.0","analysisAiSchema":"document-analysis-alias-1","aliasCatalog":"alias-catalog-1","canonIds":"canon-ids-1","algorithm":"analysis-single-v2","validation":"document-analysis-validation-v2"}""";

    private async Task FreezeHistoricalAsync(Guid run, string definition, string versions) =>
        await Execute("UPDATE \"ProcessingRuns\" SET \"WorkflowDefinitionVersion\" = @d, \"PromptVersions\" = @v::jsonb WHERE \"Id\" = @r", ("d", definition), ("v", versions), ("r", run));

    /// <summary>Run creato dal vero endpoint Analizza (POST): la definizione e le versioni sono quelle congelate dall'endpoint, mai riscritte dal test.</summary>
    private async Task<Guid> StartV4Async(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<List<string>> Strings(string sql, params (string Name, object Value)[] args)
    {
        var count = await Count($"SELECT count(*) FROM ({sql}) q", args);
        var result = new List<string>();
        for (var i = 0; i < count; i++)
        {
            result.Add(await Scalar<string>($"{sql} ORDER BY \"BatchId\" LIMIT 1 OFFSET {i}", args));
        }

        return result;
    }

    private async Task SetLimitsAsync(Func<DocumentLimits, DocumentLimits> change)
    {
        using var scope = App.Services.CreateScope();
        var (_, snapshot) = await scope.ServiceProvider.GetRequiredService<DocumentPolicyService>().GetAsync(Ct);
        var limits = change(snapshot.Limits);
        Assert.Empty(limits.Validate());
        await Execute("UPDATE \"DocumentPolicies\" SET \"LimitsJson\" = @j::jsonb, \"PolicyHash\" = @h WHERE \"Id\" = 1", ("j", limits.ToJson()), ("h", limits.Hash()));
    }

    private async Task<(Guid Run, AnalysisRunClaim Claim)> ArrangeAsync(bool ordinary = false, bool sharedKey = false, int documents = Documents)
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        for (var n = 1; n <= documents; n++)
        {
            // Soggetti distinti ordinano le evidenze in A; con sharedKey i documenti 1 e 8 (primo e ultimo) condividono la chiave di scope e cadono in foglie diverse.
            var subject = n == 1 ? "aaa" : n == Documents ? "zzz" : $"mmm{n}";
            // Gli id di evidenza dei documenti 1 e 8 ordinano per primi: il manifesto di espansione (ordinato per EvidenceId) li mette nello stesso batch, quindi un modello che legge solo il messaggio può vederli insieme.
            Guid? evidenceId = sharedKey && n == 1 ? Guid.Parse("00000000-0000-4000-8000-000000000001") : sharedKey && n == Documents ? Guid.Parse("00000000-0000-4000-8000-000000000002") : null;
            await NewDocument(client, project, $"doc{n}", documents == 1 ? "Il cliente A puo emettere al massimo 5 ordini al giorno." : Statement(n, sharedKey ? 90 : 120), subject, sharedKey && n is 1 or Documents ? "regola" : null, evidenceId);
        }

        var run = await StartV4Async(client, project);
        if (ordinary)
        {
            await Execute(
                """
                UPDATE "ProcessingRuns" SET "Regime" = 'Ordinary', "InitialRegime" = 'Ordinary', "ClassificationStatus" = 'Classified', "PromotionCount" = 0,
                       "OrdinaryDeadlineAt" = "RequestedAt" + interval '180 seconds', "EffectiveDeadlineAt" = "RequestedAt" + interval '180 seconds',
                       "DeadlineAt" = "RequestedAt" + interval '180 seconds', "HardDeadlineAt" = "RequestedAt" + interval '3600 seconds', "CallCount" = 0,
                       "PolicyHash" = repeat('a', 64)
                WHERE "Id" = @r
                """,
                ("r", run));
        }

        return (run, await ClaimAsync(run));
    }
}
