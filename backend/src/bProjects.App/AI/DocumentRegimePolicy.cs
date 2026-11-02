using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bProjects.App.AI;

// Classificazione automatica riproducibile del regime (002 T046; Plan §Classificazione, policy document-regime-v2, B1).
// Funzione pura sugli input tecnici congelati: nessun orologio proprio, nessuna coda, nessun timer, nessuna durata o carico dell'LLM come
// predicato. Il solo istante letto è «Now» (clock DB), usato dalla regola 2 e dal controllo atomico della deadline derivata dal t0.
// PromotionDueAt non esiste: il tempo trascorso non promuove mai (la promozione è additional_work, T047).

/// <summary>Regime di esecuzione di un root: Ordinary (nominale 180 s dal t0) o Extended (cap iniziale configurato).</summary>
public enum ExecutionRegime
{
    Ordinary,
    Extended,
}

/// <summary>Stato della classificazione di un root documentale. Pending nasce prudenzialmente Extended e non ammette inferenza.</summary>
public enum ClassificationStatus
{
    Pending,
    Classified,
    Rejected,
}

public enum ClassificationOutcome
{
    AdmitOrdinary,
    AdmitExtended,
    Reject,
    Fail,
}

/// <summary>Root che possiede un ClassificationRecord (la preparazione non è un WorkOwnerKind: ha il proprio ciclo di vita).</summary>
public enum ClassificationOwnerKind
{
    ProcessingRun,
    ChatTurn,
    DocumentPreparation,
}

/// <summary>Origine di ciascuna misura (es. «runtime», «config», «tokenizer»): registrata, mai interpretata dalla policy.</summary>
public sealed record ClassificationProvenance(string Window, string Output, string Margin, string Tokens);

/// <summary>
/// Input tecnici congelati del classificatore. I campi nullable sono «non determinabili» e portano a Extended workload_unknown (regola 6);
/// <see cref="Now"/> è l'unico valore non tecnico e non entra nell'inputHash.
/// </summary>
public sealed record DocumentRegimeInput
{
    // Regola 1
    public bool InputValid { get; init; } = true;
    public string? InvalidReason { get; init; }

    // Regola 2
    public bool RootTerminal { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset Now { get; init; }
    public DateTimeOffset HardDeadlineAt { get; init; }

    /// <summary>Valorizzata soltanto se il root è già Classified: ne è la EffectiveDeadlineAt corrente (nessuna nuova ammissione).</summary>
    public DateTimeOffset? ClassifiedEffectiveDeadlineAt { get; init; }
    public int OrdinaryDeadlineSeconds { get; init; } = 180;

    // Regola 3
    public string? RuntimeFingerprint { get; init; }
    public int? WindowTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? MarginTokens { get; init; }

    // Regola 4
    public bool IsPreparation { get; init; }

    // Regola 5
    public bool IrreducibleInput { get; init; }
    public bool CallLimitExceeded { get; init; }

    // Regole 6-8
    public int? PayloadTokens { get; init; }
    public TokenCountQuality TokenQuality { get; init; } = TokenCountQuality.Unavailable;
    public int? PlannedCalls { get; init; }
    public bool? HasDependencies { get; init; }
    public bool ReasoningDisabled { get; init; }

    /// <summary>Tetto di ragionamento R verificato e congelato; null = non esiste (V sconosciuta se il ragionamento è attivo).</summary>
    public int? ReasoningCapTokens { get; init; }
    public int? ShapeFloorTokens { get; init; }
    public string? ShapeSkeletonHash { get; init; }
    public ClassificationProvenance? Provenance { get; init; }
}

public enum ClassificationDisposition
{
    /// <summary>Scrivere un nuovo ClassificationRecord.</summary>
    NewRecord,

    /// <summary>Root terminale: rileggere il risultato, nessuna transizione, nessun record.</summary>
    ReadTerminal,

    /// <summary>Root già Classified e ancora vivo: si rilegge il record originario, nessuna nuova ammissione.</summary>
    ReuseExisting,
}

/// <summary>Esito della policy: riproducibile da <see cref="InputHash"/> e <see cref="PolicyHash"/>.</summary>
public sealed record DocumentRegimeDecision
{
    public required ClassificationDisposition Disposition { get; init; }
    public ClassificationOutcome? Outcome { get; init; }
    public required string Reason { get; init; }

    /// <summary>Codice d'errore del root per Reject/Fail (e per la scadenza all'ammissione); null altrimenti.</summary>
    public string? ErrorCode { get; init; }
    public ExecutionRegime? Regime { get; init; }

    /// <summary>Calcolata dal t0 originale: Ordinary = min(Hard, t0+180 s), Extended = Hard. Mai spostata dal carico.</summary>
    public DateTimeOffset? EffectiveDeadlineAt { get; init; }
    public DateTimeOffset? OrdinaryDeadlineAt { get; init; }

    /// <summary>La deadline determinata è già raggiunta: il root termina Failed deadline_exceeded senza invio (mai Extended per sola attesa).</summary>
    public bool FailDeadlineExceeded { get; init; }
    public int? InputBudgetTokens { get; init; }
    public int? VisibleReserveTokens { get; init; }
    public IReadOnlyList<string> Unknowns { get; init; } = [];

    /// <summary>Cause verificabili di multipass_or_capacity (più chiamate, dipendenze, T&gt;B, floor&gt;V); non persistite.</summary>
    public IReadOnlyList<string> Causes { get; init; } = [];
    public required string InputHash { get; init; }
    public required string PolicyHash { get; init; }
}

/// <summary>Stato provvisorio di un root la cui classificazione non è ancora conclusa: nessuna inferenza ammessa, qualunque sia l'istante.</summary>
public sealed record PendingClassification(ExecutionRegime Regime, string Reason, bool AiAllowed, DateTimeOffset EffectiveDeadlineAt);

public static class DocumentRegimePolicy
{
    public const string Version = "document-regime-v2";
    public const string PendingReason = "preflight_pending";

    /// <summary>PolicyHash: congela versione e parametri temporali della policy; record con hash diverso non sono reinterpretati.</summary>
    public static string ComputePolicyHash(int ordinaryDeadlineSeconds, int extendedDeadlineSeconds) =>
        CanonicalJson.Sha256Hex(
            $"{{\"version\":\"{Version}\",\"ordinaryDeadlineSeconds\":{ordinaryDeadlineSeconds},\"extendedDeadlineSeconds\":{extendedDeadlineSeconds},\"promotion\":\"additional_work\"}}");

    /// <summary>Hash canonico degli input tecnici (esclude <see cref="DocumentRegimeInput.Now"/>): stessi input, stesso hash.</summary>
    public static string ComputeInputHash(DocumentRegimeInput input)
    {
        var node = JsonSerializer.SerializeToNode(input with { Now = default }, HashOptions)!;
        return CanonicalJson.Sha256Hex(node.ToJsonString());
    }

    private static readonly JsonSerializerOptions HashOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Root con preflight non concluso: Extended provvisorio, nessun invio AI, deadline = cap dal t0.</summary>
    public static PendingClassification Pending(DocumentRegimeInput input) =>
        new(ExecutionRegime.Extended, PendingReason, AiAllowed: false, input.HardDeadlineAt);

    public static DocumentRegimeDecision Classify(DocumentRegimeInput input, int extendedDeadlineSeconds = 3_600)
    {
        var policyHash = ComputePolicyHash(input.OrdinaryDeadlineSeconds, extendedDeadlineSeconds);
        var inputHash = ComputeInputHash(input);

        DocumentRegimeDecision Done(
            ClassificationDisposition disposition, ClassificationOutcome? outcome, string reason, string? errorCode = null,
            ExecutionRegime? regime = null, int? budget = null, int? reserve = null, IReadOnlyList<string>? unknowns = null,
            IReadOnlyList<string>? causes = null)
        {
            DateTimeOffset? ordinaryAt = null;
            DateTimeOffset? effective = null;
            var exceeded = false;
            if (regime is { } r)
            {
                var ordinary = input.RequestedAt.AddSeconds(input.OrdinaryDeadlineSeconds);
                ordinaryAt = r == ExecutionRegime.Ordinary ? ordinary : null;
                effective = r == ExecutionRegime.Ordinary ? Min(ordinary, input.HardDeadlineAt) : input.HardDeadlineAt;
                exceeded = input.Now >= effective;
            }

            return new DocumentRegimeDecision
            {
                Disposition = disposition,
                Outcome = outcome,
                Reason = reason,
                ErrorCode = errorCode ?? (exceeded ? "deadline_exceeded" : null),
                Regime = regime,
                EffectiveDeadlineAt = effective,
                OrdinaryDeadlineAt = ordinaryAt,
                FailDeadlineExceeded = exceeded,
                InputBudgetTokens = budget,
                VisibleReserveTokens = reserve,
                Unknowns = unknowns ?? [],
                Causes = causes ?? [],
                InputHash = inputHash,
                PolicyHash = policyHash,
            };
        }

        // 1. Input/autorizzazione/fonti non validi, quote violate.
        if (!input.InputValid)
        {
            var reason = string.IsNullOrWhiteSpace(input.InvalidReason) ? "input_invalid" : input.InvalidReason;
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.Reject, reason, errorCode: reason);
        }

        // 2. Root terminale, hard cap raggiunto, oppure già Classified e deadline corrente raggiunta; nessuna promozione tardiva.
        if (input.RootTerminal)
        {
            return Done(ClassificationDisposition.ReadTerminal, null, "root_terminal");
        }

        if (input.Now >= input.HardDeadlineAt
            || (input.ClassifiedEffectiveDeadlineAt is { } classifiedUntil && input.Now >= classifiedUntil))
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.Fail, "deadline_exceeded", errorCode: "deadline_exceeded");
        }

        if (input.ClassifiedEffectiveDeadlineAt is not null)
        {
            return Done(ClassificationDisposition.ReuseExisting, null, "already_classified");
        }

        // 3. Modello/finestra/tokenizer/template non identificabili, O<=0, M<0 o B<=0.
        if (string.IsNullOrWhiteSpace(input.RuntimeFingerprint) || input.WindowTokens is not > 0)
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.Fail, "runtime_unverified", errorCode: "runtime_unverified");
        }

        if (input.OutputTokens is not > 0 || input.MarginTokens is not >= 0 || input.WindowTokens - input.OutputTokens - input.MarginTokens <= 0)
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.Fail, "config_invalid", errorCode: "config_invalid");
        }

        var window = input.WindowTokens.Value;
        var output = input.OutputTokens.Value;
        var margin = input.MarginTokens.Value;
        var budget = window - output - margin;

        // 4. Preparazione documentale: Extended con deadline separata.
        if (input.IsPreparation)
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.AdmitExtended, "preparation",
                regime: ExecutionRegime.Extended, budget: budget);
        }

        // 5. Input obbligatorio irriducibile non inviabile o limite certo di chiamate/derivati superato.
        if (input.IrreducibleInput)
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.Fail, "document_limit_exceeded", errorCode: "document_limit_exceeded");
        }

        if (input.CallLimitExceeded)
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.Fail, "coverage_incomplete", errorCode: "coverage_incomplete");
        }

        // V: O se il ragionamento è disabilitato, O-R se esiste un tetto verificato e congelato; altrimenti sconosciuta.
        int? reserve = input.ReasoningDisabled ? output : input.ReasoningCapTokens is { } cap ? output - cap : null;

        // 6. Qualcosa non determinabile dagli input tecnici: Extended workload_unknown; l'invio resta soggetto a preflight per chiamata.
        var unknowns = new List<string>();
        if (input.PayloadTokens is null || input.TokenQuality == TokenCountQuality.Unavailable)
        {
            unknowns.Add("token_count");
        }

        if (input.PlannedCalls is null)
        {
            unknowns.Add("planned_calls");
        }

        if (input.HasDependencies is null)
        {
            unknowns.Add("dependencies");
        }

        if (reserve is null)
        {
            unknowns.Add("visible_reserve");
        }

        if (input.ShapeFloorTokens is null || string.IsNullOrEmpty(input.ShapeSkeletonHash))
        {
            unknowns.Add("shape_floor");
        }

        if (unknowns.Count > 0)
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.AdmitExtended, "workload_unknown",
                regime: ExecutionRegime.Extended, budget: budget, reserve: reserve, unknowns: unknowns);
        }

        // 7. Una sola chiamata, nessuna dipendenza, T<=B e floor<=V (l'uguaglianza è ammessa).
        var causes = new List<string>();
        if (input.PlannedCalls != 1)
        {
            causes.Add("multiple_calls");
        }

        if (input.HasDependencies == true)
        {
            causes.Add("dependencies");
        }

        if (input.PayloadTokens > budget)
        {
            causes.Add("tokens_exceed_budget");
        }

        if (input.ShapeFloorTokens > reserve)
        {
            causes.Add("floor_exceeds_visible_reserve");
        }

        if (causes.Count == 0)
        {
            return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.AdmitOrdinary, "single_call_fit",
                regime: ExecutionRegime.Ordinary, budget: budget, reserve: reserve);
        }

        // 8. Tutti gli altri input validi.
        return Done(ClassificationDisposition.NewRecord, ClassificationOutcome.AdmitExtended, "multipass_or_capacity",
            regime: ExecutionRegime.Extended, budget: budget, reserve: reserve, causes: causes);
    }

    /// <summary>Costruisce il record append-only dalla decisione (ordinal assegnato dal chiamante sotto lock del root).</summary>
    public static ClassificationRecord ToRecord(
        ClassificationOwnerKind ownerKind, Guid ownerId, Guid projectId, int ordinal, DocumentRegimeInput input, DocumentRegimeDecision decision)
    {
        if (decision.Disposition != ClassificationDisposition.NewRecord || decision.Outcome is not { } outcome)
        {
            throw new InvalidOperationException("Solo una decisione NewRecord produce un ClassificationRecord.");
        }

        var provenance = input.Provenance ?? new ClassificationProvenance("unknown", "unknown", "unknown", "unknown");
        return new ClassificationRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Ordinal = ordinal,
            Version = Version,
            InputHash = decision.InputHash,
            PolicyHash = decision.PolicyHash,
            Outcome = outcome,
            Reason = decision.Reason,
            RuntimeFingerprint = input.RuntimeFingerprint,
            WindowTokens = input.WindowTokens,
            OutputTokens = input.OutputTokens,
            MarginTokens = input.MarginTokens,
            InputBudgetTokens = decision.InputBudgetTokens,
            PayloadTokens = input.PayloadTokens,
            TokenQuality = input.TokenQuality switch
            {
                TokenCountQuality.Measured => "measured",
                TokenCountQuality.CalibratedUpperBound => "calibrated_upper_bound",
                _ => "unavailable",
            },
            ProvenanceJson = JsonSerializer.Serialize(
                new { window = provenance.Window, output = provenance.Output, margin = provenance.Margin, tokens = provenance.Tokens }),
            ShapeFloorTokens = input.ShapeFloorTokens,
            ShapeSkeletonHash = input.ShapeSkeletonHash,
            VisibleReserveTokens = decision.VisibleReserveTokens,
            PlannedCalls = input.PlannedCalls,
            UnknownReasonsJson = JsonSerializer.Serialize(decision.Unknowns),
        };
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}

/// <summary>
/// Esito di una classificazione, append-only (trigger): una riga per (owner, ordinal), mai aggiornata; reclassificazioni e transizioni
/// aggiungono righe e preservano la prima. Snapshot delle misure osservate; velocità o durata dell'LLM non vi compaiono.
/// </summary>
public sealed class ClassificationRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public ClassificationOwnerKind OwnerKind { get; set; }
    public Guid OwnerId { get; set; }
    public int Ordinal { get; set; }
    public required string Version { get; set; }
    public required string InputHash { get; set; }
    public required string PolicyHash { get; set; }

    /// <summary>Clock del database (default <c>clock_timestamp()</c>).</summary>
    public DateTimeOffset ObservedAt { get; set; }
    public ClassificationOutcome Outcome { get; set; }
    public required string Reason { get; set; }
    public string? RuntimeFingerprint { get; set; }
    public int? WindowTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? MarginTokens { get; set; }
    public int? InputBudgetTokens { get; set; }
    public int? PayloadTokens { get; set; }

    /// <summary>measured | calibrated_upper_bound | unavailable.</summary>
    public string? TokenQuality { get; set; }

    /// <summary>Origine di W/O/M/T (JSONB oggetto).</summary>
    public required string ProvenanceJson { get; set; }
    public int? ShapeFloorTokens { get; set; }
    public string? ShapeSkeletonHash { get; set; }
    public int? VisibleReserveTokens { get; set; }
    public int? PlannedCalls { get; set; }

    /// <summary>Campi non determinabili (JSONB array), vuoto se nessuno.</summary>
    public required string UnknownReasonsJson { get; set; }
}

/// <summary>Skeleton strutturale canonico, versionato, del batch: soglia strutturale prudenziale, non previsione della risposta.</summary>
public sealed record ShapeSkeleton(string Version, string Json, string Hash);

/// <summary>
/// Costruisce lo skeleton dallo schema JSON del batch (Plan §Classificazione): campi obbligatori nell'ordine dello schema, ID obbligatori
/// reali ordinati, minItems richiesto, stringhe segnaposto «x» ripetuta minLength (vuote se ammesse), numeri al minimo ammesso.
/// Uno schema di cui non si può derivare uno skeleton (keyword non supportate, riferimenti non risolvibili) restituisce null → unknown.
/// Il conteggio dei token dello <see cref="ShapeSkeleton.Json"/> col tokenizer del modello è lo shapeFloorTokens.
/// </summary>
public static class ShapeSkeletonBuilder
{
    public const string SkeletonVersion = "shape-skeleton-v1";

    public static ShapeSkeleton? Build(string schemaJson, IReadOnlyDictionary<string, IReadOnlyList<string>>? requiredIds = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(schemaJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject schema)
        {
            return null;
        }

        var node = Make(schema, schema, requiredIds, property: null, depth: 0);
        if (node is null)
        {
            return null;
        }

        var json = node.ToJsonString();
        var hash = CanonicalJson.HashText($"{SkeletonVersion}\n{CanonicalJson.Canonicalize(json)}");
        return new ShapeSkeleton(SkeletonVersion, json, hash);
    }

    private static JsonNode? Make(JsonObject root, JsonObject schema, IReadOnlyDictionary<string, IReadOnlyList<string>>? ids, string? property, int depth)
    {
        if (depth > 32)
        {
            return null;
        }

        if (schema["$ref"] is JsonValue reference)
        {
            var target = Resolve(root, reference.GetValue<string>());
            return target is null ? null : Make(root, target, ids, property, depth + 1);
        }

        if (schema["const"] is { } constant)
        {
            return constant.DeepClone();
        }

        if (schema["enum"] is JsonArray { Count: > 0 } values)
        {
            return values[0]!.DeepClone();
        }

        foreach (var combinator in new[] { "oneOf", "anyOf" })
        {
            if (schema[combinator] is JsonArray { Count: > 0 } branches && branches[0] is JsonObject first)
            {
                return Make(root, first, ids, property, depth + 1);
            }
        }

        var type = schema["type"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => null,
        };

        switch (type)
        {
            case "object":
            {
                var result = new JsonObject();
                var required = (schema["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).ToHashSet() ?? [];
                if (schema["properties"] is JsonObject properties)
                {
                    foreach (var (name, child) in properties)
                    {
                        if (!required.Contains(name))
                        {
                            continue;
                        }

                        if (child is not JsonObject childSchema || Make(root, childSchema, ids, name, depth + 1) is not { } value)
                        {
                            return null;
                        }

                        result[name] = value;
                    }
                }

                return required.Count > 0 && result.Count != required.Count ? null : result;
            }

            case "array":
            {
                var min = schema["minItems"] is JsonValue m && m.TryGetValue<int>(out var count) ? count : 0;
                var array = new JsonArray();
                var known = property is not null && ids is not null && ids.TryGetValue(property, out var list) ? list.Order(StringComparer.Ordinal).ToList() : null;
                if (known is not null)
                {
                    foreach (var id in known)
                    {
                        array.Add(id);
                    }
                }

                if (schema["items"] is not JsonObject items)
                {
                    return min > 0 && array.Count < min ? null : array;
                }

                while (array.Count < min)
                {
                    if (Make(root, items, ids, property: null, depth + 1) is not { } element)
                    {
                        return null;
                    }

                    array.Add(element);
                }

                return array;
            }

            case "string":
            {
                var min = schema["minLength"] is JsonValue m && m.TryGetValue<int>(out var length) ? length : 0;
                return JsonValue.Create(new StringBuilder().Insert(0, "x", Math.Max(0, min)).ToString());
            }

            case "integer":
            case "number":
            {
                var value = schema["minimum"] is JsonValue lo && lo.TryGetValue<long>(out var minimum) ? minimum : 0;
                if (schema["exclusiveMinimum"] is JsonValue ex && ex.TryGetValue<long>(out var exclusive))
                {
                    value = exclusive + 1;
                }

                return JsonValue.Create(value);
            }

            case "boolean":
                return JsonValue.Create(false);
            case "null":
                return null;
            default:
                return null;
        }
    }

    private static JsonObject? Resolve(JsonObject root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? current = root;
        foreach (var part in reference[2..].Split('/'))
        {
            current = (current as JsonObject)?[part.Replace("~1", "/").Replace("~0", "~")];
        }

        return current as JsonObject;
    }
}

/// <summary>Valutazione del profilo nominale N (Plan §Misure): funzionale e temporale distinti. Nessun parametro di regime: promozione e InitialRegime non entrano nella formula.</summary>
public sealed record NominalEvaluation(bool Functional, bool Temporal, bool Pass);

public static class NominalProfile
{
    public const int NominalSeconds = 180;

    /// <summary>
    /// PASS solo con risultato completo committato, qualità conforme e commit entro 180 s da RequestedAt (coda, preflight e retry inclusi).
    /// Un risultato completo oltre 180 s è successo funzionale con FAIL temporale; un Ordinary non promosso scade e non è mai Completed tardivo.
    /// </summary>
    public static NominalEvaluation Evaluate(
        bool completedAndCommitted, bool qualityConforms, DateTimeOffset requestedAt, DateTimeOffset? committedAt, int nominalSeconds = NominalSeconds)
    {
        var temporal = completedAndCommitted && committedAt is { } at && at - requestedAt <= TimeSpan.FromSeconds(nominalSeconds);
        return new NominalEvaluation(completedAndCommitted, temporal, completedAndCommitted && qualityConforms && temporal);
    }
}
