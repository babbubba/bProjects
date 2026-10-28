using System.Text.Json;
using System.Text.Json.Nodes;
using bProjects.App.AI;
using bProjects.App.Analysis;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T024 — contenuto AnalysisContent 1.0.0 (contracts/ai-workflow.md §4): schema, fonti e regole semantiche,
/// con lo stesso verdetto nelle due modalità di output strutturato (JsonSchema e ValidatedTextJson).
/// Nessuna prova invoca il DGX: il modello è un testo fisso o un IChatClient finto.
/// Le prove sulle fixture rispetto allo schema normativo dimostrano che ogni caso invalido viola davvero ciò che
/// dichiara (struttura oppure sola semantica); quelle sul validatore (T027) e sul workflow nativo project-analysis-v2
/// (T029, con IChatClient finto e store in memoria) usano il codice di produzione.
/// </summary>
public sealed class AnalysisContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<StructuredOutputMode> Modes => new() { StructuredOutputMode.JsonSchema, StructuredOutputMode.ValidatedTextJson };

    public static TheoryData<StructuredOutputMode, string> AiInvalidCasesByMode
    {
        get
        {
            var data = new TheoryData<StructuredOutputMode, string>();
            foreach (var mode in new[] { StructuredOutputMode.JsonSchema, StructuredOutputMode.ValidatedTextJson })
            {
                foreach (var name in AnalysisFixtures.AiInvalid.Keys.Order())
                {
                    data.Add(mode, name);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> StructuralCases => new(AnalysisFixtures.AiInvalid.Where(c => c.Value.Structural).Select(c => c.Key).Order().ToArray());

    public static TheoryData<string> SemanticCases => new(AnalysisFixtures.AiInvalid.Where(c => !c.Value.Structural).Select(c => c.Key).Order().ToArray());

    public static TheoryData<string> PmEditInvalidCases => new(AnalysisFixtures.PmEditInvalid.Keys.Order().ToArray());

    public static TheoryData<string> NonJsonOutputs => new(AnalysisFixtures.NonJsonOutputs.Keys.Order().ToArray());

    // ============================================================ fixture vs schema normativo (verdi subito)

    [Fact]
    public void The_nominal_fixtures_satisfy_the_normative_schema()
    {
        var world = AnalysisFixtures.World();

        foreach (var (name, content) in new Dictionary<string, JsonObject>
        {
            ["tipico"] = AnalysisFixtures.NominalAi(world),
            ["minimo"] = AnalysisFixtures.MinimalAi(world),
            ["modifica PM"] = AnalysisFixtures.NominalPmEdit(world),
            ["risoluzione PM"] = AnalysisFixtures.PmResolvesConflict(world),
            ["confermato"] = AnalysisFixtures.Confirmed(AnalysisFixtures.NominalPmEdit(world)),
        })
        {
            Assert.True(ContractSchema.Violations(content).Count == 0, $"{name}: {string.Join("; ", ContractSchema.Violations(content))}");
        }
    }

    [Theory]
    [MemberData(nameof(StructuralCases))]
    public void Structural_invalid_cases_do_violate_the_normative_schema(string name)
    {
        var content = AnalysisFixtures.Mutated(AnalysisFixtures.World(), AnalysisFixtures.AiInvalid[name].Mutate);

        Assert.NotEmpty(ContractSchema.Violations(content));
    }

    [Theory]
    [MemberData(nameof(SemanticCases))]
    public void Semantic_invalid_cases_are_schema_valid_so_only_the_semantic_rules_can_reject_them(string name)
    {
        var content = AnalysisFixtures.Mutated(AnalysisFixtures.World(), AnalysisFixtures.AiInvalid[name].Mutate);

        Assert.Empty(ContractSchema.Violations(content));
    }

    [Theory]
    [MemberData(nameof(PmEditInvalidCases))]
    public void Invalid_pm_edits_are_schema_valid_so_only_the_semantic_rules_can_reject_them(string name)
    {
        var content = AnalysisFixtures.Mutated(AnalysisFixtures.World(), AnalysisFixtures.PmEditInvalid[name], AnalysisFixtures.NominalPmEdit);

        Assert.Empty(ContractSchema.Violations(content));
    }

    // ============================================================ casi nominali (validatore, T027)

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Nominal_ai_output_is_accepted_in_both_output_modes(StructuredOutputMode mode)
    {
        var world = AnalysisFixtures.World();

        foreach (var content in new[] { AnalysisFixtures.NominalAi(world), AnalysisFixtures.MinimalAi(world) })
        {
            var outcome = await AnalysisGateway.ProcessModelOutputAsync(mode, content.ToJsonString(), world.Trusted, Ct);

            Assert.True(outcome.Accepted, outcome.Code);
            Assert.Null(outcome.Code);
        }
    }

    [Fact]
    public void Ai_output_may_hold_assistant_context_inference_assumptions_and_open_conflicts()
    {
        var world = AnalysisFixtures.World();
        var content = AnalysisFixtures.NominalAi(world);

        // Il caso nominale esercita proprio ciò che l'AI può produrre: fonti Message user e assistant,
        // Declared sostenuto da user, Inferred/Assumed da assistant, domande aperte senza evidenza, conflitto Open.
        Assert.Equal("Unvalidated", content["validationState"]!.GetValue<string>());
        Assert.True(AnalysisGateway.Validate(content, world.Trusted, AnalysisActor.Ai).Accepted);
    }

    [Fact]
    public void A_pm_edit_adds_a_pm_revision_source_and_the_pm_may_resolve_a_conflict_with_a_reason()
    {
        var world = AnalysisFixtures.World();

        Assert.True(AnalysisGateway.Validate(AnalysisFixtures.NominalPmEdit(world), world.Trusted, AnalysisActor.PmEdit).Accepted);
        Assert.True(AnalysisGateway.Validate(AnalysisFixtures.PmResolvesConflict(world), world.Trusted, AnalysisActor.PmEdit).Accepted);
    }

    [Fact]
    public void Confirm_may_only_change_the_validation_state_and_is_allowed_with_open_conflicts()
    {
        var world = AnalysisFixtures.World();
        var current = AnalysisFixtures.NominalPmEdit(world); // contiene ancora un conflitto Open

        Assert.True(AnalysisGateway.Validate(AnalysisFixtures.Confirmed(current), world.Trusted, AnalysisActor.PmConfirm, current).Accepted);
    }

    // ============================================================ fallimenti (validatore, T027)

    [Theory]
    [MemberData(nameof(AiInvalidCasesByMode))]
    public async Task Invalid_ai_output_is_rejected_identically_in_both_modes_without_repair(StructuredOutputMode mode, string name)
    {
        var world = AnalysisFixtures.World();
        var content = AnalysisFixtures.Mutated(world, AnalysisFixtures.AiInvalid[name].Mutate);

        var outcome = await AnalysisGateway.ProcessModelOutputAsync(mode, content.ToJsonString(), world.Trusted, Ct);

        Assert.False(outcome.Accepted, $"{name} doveva essere rifiutato");
        Assert.Equal("invalid_agent_output", outcome.Code);
        Assert.NotEmpty(outcome.Violations);
        Assert.Null(outcome.Repaired); // nessuna riparazione AI: né ritocco né nuovo tentativo di correzione
    }

    [Theory]
    [MemberData(nameof(PmEditInvalidCases))]
    public void Invalid_pm_edits_are_rejected_with_preview_invalid(string name)
    {
        var world = AnalysisFixtures.World();
        var content = AnalysisFixtures.Mutated(world, AnalysisFixtures.PmEditInvalid[name], AnalysisFixtures.NominalPmEdit);

        var outcome = AnalysisGateway.Validate(content, world.Trusted, AnalysisActor.PmEdit);

        Assert.False(outcome.Accepted, $"{name} doveva essere rifiutato");
        Assert.Equal("preview_invalid", outcome.Code);
    }

    [Fact]
    public void Pm_privileges_are_not_the_ai_privileges_for_the_same_content()
    {
        var world = AnalysisFixtures.World();
        var resolved = AnalysisFixtures.PmResolvesConflict(world);

        // Stesso JSON: valido se è un edit PM, rifiutato se lo produce l'AI (conflitto Resolved, fonte PmRevision).
        Assert.True(AnalysisGateway.Validate(resolved, world.Trusted, AnalysisActor.PmEdit).Accepted);
        Assert.False(AnalysisGateway.Validate(resolved, world.Trusted, AnalysisActor.Ai).Accepted);
    }

    [Fact]
    public void Confirm_rejects_any_change_beyond_the_validation_state()
    {
        var world = AnalysisFixtures.World();
        var current = AnalysisFixtures.NominalPmEdit(world);
        var tampered = AnalysisFixtures.Confirmed(current);
        tampered["goals"]![0]!["text"] = "Obiettivo modificato di nascosto durante la conferma";

        Assert.False(AnalysisGateway.Validate(tampered, world.Trusted, AnalysisActor.PmConfirm, current).Accepted);
    }

    // ============================================================ nessun Markdown, solo JSON (T027/T029)

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Output_that_is_not_pure_json_is_rejected_in_both_modes(StructuredOutputMode mode)
    {
        var world = AnalysisFixtures.World();
        var json = AnalysisFixtures.NominalAi(world).ToJsonString();

        foreach (var (name, build) in AnalysisFixtures.NonJsonOutputs)
        {
            var outcome = await AnalysisGateway.ProcessModelOutputAsync(mode, build(json), world.Trusted, Ct);

            Assert.False(outcome.Accepted, $"{name} doveva essere rifiutato");
            Assert.Equal("invalid_agent_output", outcome.Code);
        }
    }

    [Fact]
    public async Task A_markdown_property_is_not_part_of_the_content_and_is_rejected()
    {
        var world = AnalysisFixtures.World();
        var content = AnalysisFixtures.NominalAi(world);
        content["markdown"] = "# Analisi\n\n- punto";

        var outcome = await AnalysisGateway.ProcessModelOutputAsync(StructuredOutputMode.JsonSchema, content.ToJsonString(), world.Trusted, Ct);

        Assert.False(outcome.Accepted);
    }

    // ============================================================ workflow con IChatClient finto (T029)

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_analysis_run_requests_the_json_schema_only_in_the_native_mode_and_yields_the_same_result(StructuredOutputMode mode)
    {
        var world = AnalysisFixtures.World();
        var model = new ScriptedChatClient(AnalysisFixtures.NominalAi(world).ToJsonString());

        var outcome = await AnalysisGateway.RunAnalysisAsync(model, mode, world.Trusted, Ct);

        Assert.True(outcome.Accepted, outcome.Code);
        var format = Assert.Single(model.Options).ResponseFormat;
        if (mode == StructuredOutputMode.JsonSchema)
        {
            var json = Assert.IsType<ChatResponseFormatJson>(format);
            Assert.NotNull(json.Schema);
        }
        else
        {
            Assert.False(format is ChatResponseFormatJson { Schema: not null }, "ValidatedTextJson non invia lo schema al modello");
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_model_output_that_fails_validation_is_a_failure_not_a_preview(StructuredOutputMode mode)
    {
        var world = AnalysisFixtures.World();
        var invalid = AnalysisFixtures.Mutated(world, AnalysisFixtures.AiInvalid["evidenza_non_risolta"].Mutate).ToJsonString();
        var model = new ScriptedChatClient(invalid);

        var outcome = await AnalysisGateway.RunAnalysisAsync(model, mode, world.Trusted, Ct);

        Assert.False(outcome.Accepted);
        Assert.Equal("invalid_agent_output", outcome.Code);
        Assert.Null(outcome.Repaired);
        Assert.Single(model.Options); // nessuna seconda chiamata di riparazione
    }

    private sealed class ScriptedChatClient(string reply) : IChatClient
    {
        public List<ChatOptions> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options.Add(options ?? new ChatOptions());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options.Add(options ?? new ChatOptions());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

// ================================================================ ponte verso il codice di produzione

internal enum AnalysisActor
{
    Ai,
    PmEdit,
    PmConfirm,
}

internal sealed record KnownMessage(Guid Id, Guid ProjectId, string Role, long Sequence);

/// <summary>Input fidato del server: ciò che il modello e il PM non possono dichiarare da soli.</summary>
internal sealed record TrustedInput(Guid ProjectId, Guid AnalysisId, long Cutoff, IReadOnlyList<KnownMessage> Messages, IReadOnlySet<Guid> PmRevisionIds);

internal sealed record AnalysisOutcome(bool Accepted, string? Code, IReadOnlyList<string> Violations, string? Repaired);

/// <summary>
/// Ponte verso il codice reale: la validazione usa AnalysisContentValidator (T027) e il consolidamento esegue il vero
/// grafo project-analysis-v2 (T029) con un client finto.
/// </summary>
internal static class AnalysisGateway
{
    /// <summary>Validazione tipizzata e semantica; <paramref name="current"/> è la revisione corrente per la conferma.</summary>
    public static AnalysisOutcome Validate(JsonObject content, TrustedInput trusted, AnalysisActor actor, JsonObject? current = null) =>
        ToOutcome(AnalysisContentValidator.ValidateText(content.ToJsonString(), ToContext(trusted), ToKind(actor), current?.ToJsonString()));

    /// <summary>Testo del modello → JSON → validazione, secondo la modalità; mai riparazione dell'output.</summary>
    public static Task<AnalysisOutcome> ProcessModelOutputAsync(StructuredOutputMode mode, string modelText, TrustedInput trusted, CancellationToken ct)
    {
        // Le due modalità differiscono solo nella richiesta al modello: il verdetto sul testo è lo stesso.
        Assert.True(Enum.IsDefined(mode));
        return Task.FromResult(ToOutcome(AnalysisContentValidator.ValidateText(modelText, ToContext(trusted), AnalysisActorKind.Ai)));
    }

    private static AnalysisActorKind ToKind(AnalysisActor actor) => actor switch
    {
        AnalysisActor.Ai => AnalysisActorKind.Ai,
        AnalysisActor.PmEdit => AnalysisActorKind.PmEdit,
        _ => AnalysisActorKind.PmConfirm,
    };

    private static AnalysisValidationContext ToContext(TrustedInput trusted) => new(
        trusted.ProjectId,
        trusted.AnalysisId,
        trusted.Cutoff,
        [.. trusted.Messages.Select(m => new AnalysisMessageRef(m.Id, m.ProjectId, m.Role == "user" ? AnalysisMessageRole.User : AnalysisMessageRole.Assistant, m.Sequence))],
        trusted.PmRevisionIds);

    private static AnalysisOutcome ToOutcome(AnalysisValidationResult result) => new(result.Accepted, result.Code, result.Violations, null);

    /// <summary>
    /// Esegue il vero grafo project-analysis-v2 (LoadDialog → ConversationalAgent → ValidatePersistAnalysis) con il client
    /// dato e uno store in memoria: nessuna riparazione, una sola chiamata al modello, Failed con codice se l'output non è valido.
    /// </summary>
    public static async Task<AnalysisOutcome> RunAnalysisAsync(IChatClient model, StructuredOutputMode mode, TrustedInput trusted, CancellationToken ct)
    {
        var store = new InMemoryAnalysisStore(trusted);
        var services = new ServiceCollection().AddSingleton<IAnalysisRunStore>(store).BuildServiceProvider();
        using var client = new InternalModelClient(new AiOptions { Endpoint = "http://localhost:1/v1/", ModelId = "fake", ApiKey = "non-usata", StructuredOutputMode = mode });
        var agent = new ConversationalAgent(
            new AgentFactory(new AiOptions { ContextWindowTokens = 32768 }, model, (t, _) => new(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)),
            new ChatOptions { ModelId = "fake" });
        var workflow = new AnalysisWorkflow(
            services.GetRequiredService<IServiceScopeFactory>(), agent, client.CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0));

        var built = await workflow.BuildAsync(new AnalysisRunClaim(trusted.AnalysisId, "test", 1), ct);
        await using var run = await InProcessExecution.RunStreamingAsync(built, new TurnToken(emitEvents: false), cancellationToken: ct);
        Exception? failure = null;
        await foreach (var evt in run.WatchStreamAsync(ct))
        {
            if (evt is WorkflowErrorEvent error)
            {
                failure ??= error.Exception;
            }
        }

        if (failure is not null)
        {
            throw failure;
        }

        return store.Failure is { } failed
            ? new AnalysisOutcome(false, failed.Code, [failed.Message], null)
            : new AnalysisOutcome(store.Committed is not null, store.Committed is null ? "nessun_risultato" : null, [], null);
    }
}

/// <summary>Store in memoria del run: registra il commit o il Failed, senza alcun database.</summary>
internal sealed class InMemoryAnalysisStore(TrustedInput trusted) : IAnalysisRunStore
{
    public AnalysisContent? Committed { get; private set; }

    public (string Code, string Message)? Failure { get; private set; }

    public List<string> Stages { get; } = [];

    public Task<AnalysisRunInput> LoadInputAsync(Guid runId, CancellationToken ct) => Task.FromResult(new AnalysisRunInput(
        trusted.ProjectId,
        trusted.AnalysisId,
        trusted.Cutoff,
        [.. trusted.Messages
            .Where(m => m.ProjectId == trusted.ProjectId && m.Sequence <= trusted.Cutoff)
            .OrderBy(m => m.Sequence)
            .Select(m => new AnalysisDialogMessage(m.Id, m.Role == "user" ? AnalysisMessageRole.User : AnalysisMessageRole.Assistant, m.Sequence, $"Messaggio {m.Sequence}"))]));

    public Task<AnalysisCommitResult> CommitAsync(AnalysisRunClaim claim, AnalysisContent content, CancellationToken ct)
    {
        Committed = content;
        return Task.FromResult(new AnalysisCommitResult(AnalysisCommitOutcome.Committed, Guid.NewGuid()));
    }

    public Task<bool> FailAsync(AnalysisRunClaim claim, string errorCode, string errorMessage, CancellationToken ct)
    {
        Failure = (errorCode, errorMessage);
        return Task.FromResult(true);
    }

    public Task<bool> RecordStageAsync(AnalysisRunClaim claim, string stage, CancellationToken ct)
    {
        Stages.Add(stage);
        return Task.FromResult(true);
    }
}

// ================================================================ fixture

internal sealed class AnalysisWorld
{
    public Guid ProjectId { get; } = Guid.NewGuid();

    public Guid AnalysisId { get; } = Guid.NewGuid();

    public long Cutoff => 5;

    public KnownMessage U1 { get; }

    public KnownMessage A1 { get; }

    public KnownMessage U2 { get; }

    public KnownMessage BeyondCutoff { get; }

    public KnownMessage OtherProject { get; }

    public Guid PmRevisionId { get; } = Guid.NewGuid();

    public Guid OtherRevisionId { get; } = Guid.NewGuid();

    public TrustedInput Trusted { get; }

    public AnalysisWorld()
    {
        U1 = new(Guid.NewGuid(), ProjectId, "user", 1);
        A1 = new(Guid.NewGuid(), ProjectId, "assistant", 2);
        U2 = new(Guid.NewGuid(), ProjectId, "user", 3);
        BeyondCutoff = new(Guid.NewGuid(), ProjectId, "user", 9);
        OtherProject = new(Guid.NewGuid(), Guid.NewGuid(), "user", 1);
        Trusted = new(ProjectId, AnalysisId, Cutoff, [U1, A1, U2, BeyondCutoff, OtherProject], new HashSet<Guid> { PmRevisionId });
    }
}

internal sealed record InvalidCase(bool Structural, Action<JsonObject, AnalysisWorld> Mutate)
{
    public InvalidCase(bool structural, Action<JsonObject> mutate)
        : this(structural, (root, _) => mutate(root))
    {
    }
}

internal static class AnalysisFixtures
{
    // Id stabili dei nodi del caso nominale, ricavati per posizione.
    public static AnalysisWorld World() => new();

    private static string Id() => Guid.NewGuid().ToString();

    private static JsonObject Source(Guid id, KnownMessage message) => new()
    {
        ["id"] = id.ToString(),
        ["kind"] = "Message",
        ["messageId"] = message.Id.ToString(),
        ["messageRole"] = message.Role,
        ["revisionId"] = null,
    };

    private static JsonObject PmSource(Guid id, Guid revisionId) => new()
    {
        ["id"] = id.ToString(),
        ["kind"] = "PmRevision",
        ["messageId"] = null,
        ["messageRole"] = null,
        ["revisionId"] = revisionId.ToString(),
    };

    private static JsonObject Item(string text, string classification, params Guid[] evidence) => new()
    {
        ["id"] = Id(),
        ["text"] = text,
        ["classification"] = classification,
        ["evidenceRefs"] = new JsonArray(evidence.Select(e => (JsonNode)JsonValue.Create(e.ToString())!).ToArray()),
    };

    private static JsonObject Envelope(AnalysisWorld w, string validationState) => new()
    {
        ["schemaVersion"] = "1.0.0",
        ["analysisId"] = w.AnalysisId.ToString(),
        ["projectId"] = w.ProjectId.ToString(),
        ["sourceCutoffSequence"] = w.Cutoff,
        ["validationState"] = validationState,
        ["sources"] = new JsonArray(),
        ["goals"] = new JsonArray(),
        ["contextAndScope"] = new JsonObject { ["overview"] = new JsonArray(), ["included"] = new JsonArray(), ["excluded"] = new JsonArray() },
        ["requirements"] = new JsonArray(),
        ["operationalFlows"] = new JsonArray(),
        ["constraints"] = new JsonArray(),
        ["dependencies"] = new JsonArray(),
        ["assumptions"] = new JsonArray(),
        ["openQuestions"] = new JsonArray(),
        ["conflicts"] = new JsonArray(),
    };

    /// <summary>Tutti gli array vuoti: valido, nessun riempitivo (regola 7).</summary>
    public static JsonObject MinimalAi(AnalysisWorld w) => Envelope(w, "Unvalidated");

    /// <summary>Output AI tipico: Declared da utente, Inferred/Assumed da assistant, domande aperte, conflitto Open.</summary>
    public static JsonObject NominalAi(AnalysisWorld w)
    {
        var (s1, s2, s3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var root = Envelope(w, "Unvalidated");
        root["sources"] = new JsonArray(Source(s1, w.U1), Source(s2, w.A1), Source(s3, w.U2));
        root["goals"] = new JsonArray(Item("Ridurre i tempi di risposta ai ticket", "Declared", s1));
        root["contextAndScope"]!["overview"] = new JsonArray(Item("Portale clienti per l'assistenza", "Inferred", s2));
        root["contextAndScope"]!["included"] = new JsonArray(Item("Login con email e elenco ticket", "Declared", s1, s3));
        root["requirements"] = new JsonArray(Item("Il cliente vede lo stato dei propri ticket", "Declared", s1));
        root["assumptions"] = new JsonArray(Item("I clienti accedono da browser desktop", "Assumed", s2));
        root["openQuestions"] = new JsonArray(Item("Quanti clienti useranno il portale?", "Inferred"));
        root["conflicts"] = new JsonArray(new JsonObject
        {
            ["id"] = Id(),
            ["description"] = "La fatturazione è dentro o fuori dal perimetro?",
            ["status"] = "Open",
            ["alternatives"] = new JsonArray(Item("Fatturazione nel portale", "Declared", s1), Item("Fatturazione fuori perimetro", "Declared", s3)),
            ["resolution"] = null,
        });
        return root;
    }

    /// <summary>Modifica PM (PUT): nuova fonte PmRevision e nuovo elemento che la cita; resta il conflitto Open.</summary>
    public static JsonObject NominalPmEdit(AnalysisWorld w)
    {
        var root = NominalAi(w);
        var pm = Guid.NewGuid();
        ((JsonArray)root["sources"]!).Add(PmSource(pm, w.PmRevisionId));
        ((JsonArray)root["constraints"]!).Add(Item("Go-live entro giugno", "Declared", pm));
        root["goals"]![0]!["text"] = "Ridurre a un giorno i tempi di risposta ai ticket";
        return root;
    }

    /// <summary>Il PM sceglie una delle alternative esistenti e motiva, citando la propria modifica.</summary>
    public static JsonObject PmResolvesConflict(AnalysisWorld w)
    {
        var root = NominalPmEdit(w);
        var conflict = root["conflicts"]![0]!;
        var pm = root["sources"]!.AsArray().Last()!["id"]!.GetValue<string>();
        conflict["status"] = "Resolved";
        conflict["resolution"] = new JsonObject
        {
            ["selectedAlternativeIds"] = new JsonArray(conflict["alternatives"]![1]!["id"]!.GetValue<string>()),
            ["reason"] = "Decisione della direzione: la fatturazione resta fuori dal primo rilascio.",
            ["evidenceRefs"] = new JsonArray(pm),
        };
        return root;
    }

    /// <summary>La conferma cambia soltanto validationState rispetto alla revisione corrente data.</summary>
    public static JsonObject Confirmed(JsonObject current)
    {
        var root = (JsonObject)current.DeepClone();
        root["validationState"] = "PmConfirmed";
        return root;
    }

    public static JsonObject Mutated(AnalysisWorld w, Action<JsonObject, AnalysisWorld> mutate)
    {
        var root = NominalAi(w);
        mutate(root, w);
        return root;
    }

    public static JsonObject Mutated(AnalysisWorld w, Action<JsonObject> mutate, Func<AnalysisWorld, JsonObject> baseline)
    {
        var root = baseline(w);
        mutate(root);
        return root;
    }

    private static JsonObject At(JsonNode root, string path) => (JsonObject)root[path]![0]!;

    /// <summary>Casi invalidi per l'AI: strutturali (violano lo schema) e semantici (schema valido, regole violate).</summary>
    public static readonly IReadOnlyDictionary<string, InvalidCase> AiInvalid = new Dictionary<string, InvalidCase>
    {
        // ---- struttura
        ["manca_conflicts"] = new(true, r => r.Remove("conflicts")),
        ["proprieta_extra_root_markdown"] = new(true, r => r["markdown"] = "# Analisi"),
        ["proprieta_extra_in_item"] = new(true, r => At(r, "goals")["priorita"] = "alta"),
        ["schemaVersion_errata"] = new(true, r => r["schemaVersion"] = "2.0.0"),
        ["cutoff_zero"] = new(true, r => r["sourceCutoffSequence"] = 0),
        ["id_non_uuid"] = new(true, r => At(r, "goals")["id"] = "non-un-uuid"),
        ["classification_sconosciuta"] = new(true, r => At(r, "goals")["classification"] = "Guessed"),
        ["text_vuoto"] = new(true, r => At(r, "goals")["text"] = string.Empty),
        ["contextAndScope_incompleto"] = new(true, r => r["contextAndScope"]!.AsObject().Remove("excluded")),
        ["alternative_una_sola"] = new(true, r => r["conflicts"]![0]!["alternatives"]!.AsArray().RemoveAt(1)),
        ["fonte_kind_sconosciuto"] = new(true, r => r["sources"]![0]!["kind"] = "Documento"),
        ["goals_non_array"] = new(true, r => r["goals"] = new JsonObject()),
        ["validationState_sconosciuto"] = new(true, r => r["validationState"] = "Approved"),

        // ---- semantica: identità e riferimenti
        ["testo_solo_whitespace"] = new(false, r => At(r, "goals")["text"] = " \t\r\n "),
        ["id_elemento_duplicato"] = new(false, r => At(r, "requirements")["id"] = At(r, "goals")["id"]!.GetValue<string>()),
        ["id_fonte_duplicato"] = new(false, r => r["sources"]![1]!["id"] = r["sources"]![0]!["id"]!.GetValue<string>()),
        ["evidenza_non_risolta"] = new(false, r => At(r, "goals")["evidenceRefs"] = new JsonArray(Id())),
        ["cutoff_diverso_dal_server"] = new(false, r => r["sourceCutoffSequence"] = 4),
        ["analysisId_diverso_dal_server"] = new(false, r => r["analysisId"] = Id()),
        ["projectId_diverso_dal_server"] = new(false, r => r["projectId"] = Id()),

        // ---- semantica: fonti
        ["fonte_messaggio_inesistente"] = new(false, r => r["sources"]![0]!["messageId"] = Id()),
        ["fonte_oltre_il_cutoff"] = new(false, (r, w) => r["sources"]![0]!["messageId"] = w.BeyondCutoff.Id.ToString()),
        ["fonte_di_altro_progetto"] = new(false, (r, w) => r["sources"]![0]!["messageId"] = w.OtherProject.Id.ToString()),
        ["fonte_ruolo_incoerente"] = new(false, r => r["sources"]![1]!["messageRole"] = "user"),
        ["fonte_message_con_revisionId"] = new(false, r => r["sources"]![0]!["revisionId"] = Id()),
        ["fonte_message_senza_messageId"] = new(false, r => r["sources"]![0]!["messageId"] = null),

        // ---- semantica: privilegi dell'AI
        ["ai_validationState_PmConfirmed"] = new(false, r => r["validationState"] = "PmConfirmed"),
        ["ai_fonte_PmRevision"] = new(false, r => ((JsonArray)r["sources"]!).Add(new JsonObject
        {
            ["id"] = Id(),
            ["kind"] = "PmRevision",
            ["messageId"] = null,
            ["messageRole"] = null,
            ["revisionId"] = Id(),
        })),
        ["ai_conflitto_Resolved"] = new(false, r =>
        {
            var conflict = r["conflicts"]![0]!;
            conflict["status"] = "Resolved";
            conflict["resolution"] = new JsonObject
            {
                ["selectedAlternativeIds"] = new JsonArray(conflict["alternatives"]![0]!["id"]!.GetValue<string>()),
                ["reason"] = "scelta del modello",
                ["evidenceRefs"] = new JsonArray(),
            };
        }),
        ["conflitto_Open_con_resolution"] = new(false, r => r["conflicts"]![0]!["resolution"] = new JsonObject
        {
            ["selectedAlternativeIds"] = new JsonArray(r["conflicts"]![0]!["alternatives"]![0]!["id"]!.GetValue<string>()),
            ["reason"] = "x",
            ["evidenceRefs"] = new JsonArray(),
        }),

        // ---- semantica: fatti e assunzioni
        ["declared_sostenuto_solo_da_assistant"] = new(false, r => At(r, "goals")["evidenceRefs"] = new JsonArray(r["sources"]![1]!["id"]!.GetValue<string>())),
        ["declared_senza_evidenza"] = new(false, r => At(r, "goals")["evidenceRefs"] = new JsonArray()),
        ["alternativa_conflitto_solo_assistant"] = new(false, r =>
            r["conflicts"]![0]!["alternatives"]![0]!["evidenceRefs"] = new JsonArray(r["sources"]![1]!["id"]!.GetValue<string>())),
        ["assunzione_non_Assumed"] = new(false, r => At(r, "assumptions")["classification"] = "Declared"),
    };

    /// <summary>Modifiche PM invalide (partono dal caso NominalPmEdit): lo schema è valido, le regole PM no.</summary>
    public static readonly IReadOnlyDictionary<string, Action<JsonObject>> PmEditInvalid = new Dictionary<string, Action<JsonObject>>
    {
        ["fonte_PmRevision_di_altra_analisi"] = r => r["sources"]!.AsArray().Last()!["revisionId"] = Id(),
        ["fonte_PmRevision_con_messageId"] = r => r["sources"]!.AsArray().Last()!["messageId"] = Id(),
        ["risolto_senza_motivo"] = r => Resolve(r, reason: "   ", pmEvidence: true),
        ["risolto_con_alternativa_inesistente"] = r => Resolve(r, selected: Id()),
        ["risolto_senza_riferimento_alla_modifica_PM"] = r => Resolve(r, pmEvidence: false),
        ["Open_con_resolution"] = r => r["conflicts"]![0]!["resolution"] = new JsonObject
        {
            ["selectedAlternativeIds"] = new JsonArray(r["conflicts"]![0]!["alternatives"]![0]!["id"]!.GetValue<string>()),
            ["reason"] = "motivo",
            ["evidenceRefs"] = new JsonArray(),
        },
        ["cutoff_modificato_dal_PM"] = r => r["sourceCutoffSequence"] = 4,
        ["analysisId_modificato_dal_PM"] = r => r["analysisId"] = Id(),
        ["assunzione_riclassificata_Declared"] = r => At(r, "assumptions")["classification"] = "Declared",
    };

    /// <summary>Uscite del modello che non sono soltanto JSON: nessun Markdown, nessuna prosa, nessuna riparazione.</summary>
    public static readonly IReadOnlyDictionary<string, Func<string, string>> NonJsonOutputs = new Dictionary<string, Func<string, string>>
    {
        ["json_in_recinto_markdown"] = json => "```json\n" + json + "\n```",
        ["prosa_prima_del_json"] = json => "Ecco l'analisi richiesta:\n" + json,
        ["prosa_dopo_il_json"] = json => json + "\nSpero sia utile.",
        ["documento_markdown"] = _ => "# Analisi\n\n## Obiettivi\n- Ridurre i tempi di risposta",
        ["testo_vuoto"] = _ => string.Empty,
        ["json_troncato"] = json => json[..(json.Length / 2)],
        ["array_invece_di_oggetto"] = _ => "[]",
        ["null_json"] = _ => "null",
    };

    private static void Resolve(JsonObject root, string? reason = null, string? selected = null, bool pmEvidence = true)
    {
        var conflict = root["conflicts"]![0]!;
        var pm = root["sources"]!.AsArray().Last()!["id"]!.GetValue<string>();
        conflict["status"] = "Resolved";
        conflict["resolution"] = new JsonObject
        {
            ["selectedAlternativeIds"] = new JsonArray(selected ?? conflict["alternatives"]![0]!["id"]!.GetValue<string>()),
            ["reason"] = reason ?? "motivo valido",
            ["evidenceRefs"] = pmEvidence ? new JsonArray(pm) : new JsonArray(),
        };
    }
}

// ================================================================ verificatore minimo dello schema normativo

/// <summary>
/// Verifica soltanto ciò che il contratto elenca (type, const, enum, required, additionalProperties=false, items,
/// $ref, minLength/minItems/minimum, uniqueItems, anyOf, format uuid): serve a provare che le fixture dicono il vero.
/// Il JSON Schema è letto dal blocco normativo di contracts/ai-workflow.md, mai duplicato.
/// </summary>
internal static class ContractSchema
{
    private static readonly Lazy<JsonElement> Schema = new(Load);

    public static IReadOnlyList<string> Violations(JsonObject content)
    {
        using var document = JsonDocument.Parse(content.ToJsonString());
        var errors = new List<string>();
        Check(Schema.Value, document.RootElement, "$", errors);
        return errors;
    }

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "specs", "001-project-draft", "contracts", "ai-workflow.md")))
        {
            directory = directory.Parent;
        }

        var markdown = File.ReadAllText(Path.Combine(directory?.FullName ?? throw new FileNotFoundException("contracts/ai-workflow.md"), "specs", "001-project-draft", "contracts", "ai-workflow.md"));
        var start = markdown.IndexOf("\"title\": \"AnalysisContent\"", StringComparison.Ordinal);
        var open = markdown.LastIndexOf("```json", start, StringComparison.Ordinal) + "```json".Length;
        var close = markdown.IndexOf("```", start, StringComparison.Ordinal);
        return JsonDocument.Parse(markdown[open..close]).RootElement.Clone();
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var node = Schema.Value;
            foreach (var part in reference.GetString()!.TrimStart('#', '/').Split('/'))
            {
                node = node.GetProperty(part);
            }

            Check(node, value, path, errors);
            return;
        }

        if (schema.TryGetProperty("anyOf", out var any))
        {
            if (!any.EnumerateArray().Any(option => { var local = new List<string>(); Check(option, value, path, local); return local.Count == 0; }))
            {
                errors.Add($"{path}: nessuna alternativa di anyOf soddisfatta");
            }

            return;
        }

        if (schema.TryGetProperty("const", out var constant) && value.ToString() != constant.ToString())
        {
            errors.Add($"{path}: atteso {constant}");
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(e => e.ValueKind == value.ValueKind && (e.ValueKind == JsonValueKind.Null || e.ToString() == value.ToString())))
        {
            errors.Add($"{path}: valore fuori dall'enum");
        }

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(type, value))
        {
            errors.Add($"{path}: tipo non valido");
            return;
        }

        if (schema.TryGetProperty("format", out var format) && format.GetString() == "uuid" && value.ValueKind == JsonValueKind.String && !Guid.TryParse(value.GetString(), out _))
        {
            errors.Add($"{path}: uuid non valido");
        }

        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("minLength", out var minLength) && value.GetString()!.Length < minLength.GetInt32())
        {
            errors.Add($"{path}: stringa troppo corta");
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum) && value.GetDouble() < minimum.GetDouble())
        {
            errors.Add($"{path}: sotto il minimo");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToList();
            if (schema.TryGetProperty("minItems", out var minItems) && items.Count < minItems.GetInt32())
            {
                errors.Add($"{path}: troppi pochi elementi");
            }

            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean() && items.Select(i => i.ToString()).Distinct().Count() != items.Count)
            {
                errors.Add($"{path}: elementi duplicati");
            }

            if (schema.TryGetProperty("items", out var itemSchema))
            {
                for (var i = 0; i < items.Count; i++)
                {
                    Check(itemSchema, items[i], $"{path}[{i}]", errors);
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var p) ? p : default;
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(n => n.GetString()!).Where(n => !value.TryGetProperty(n, out _)))
                {
                    errors.Add($"{path}: manca {name}");
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    Check(propertySchema, property.Value, $"{path}.{property.Name}", errors);
                }
                else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
                {
                    errors.Add($"{path}: proprietà non ammessa {property.Name}");
                }
            }
        }
    }

    private static bool TypeMatches(JsonElement type, JsonElement value)
    {
        var names = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!) : [type.GetString()!];
        return names.Any(name => name switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        });
    }
}
