using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// DTO di output AI `reconcile-alias-1` (contracts/ai-workflow.md §11, T131): il modello restituisce, per UN batch della riconciliazione gerarchica, soli
// contenuti semantici con alias brevi (E<n> evidenze, N<n> nodi o insiemi, i<n> chiavi locali o item già proposti, R<n> relazioni note, f<n>/p<n> chiavi
// locali). Mai UUID, hash, batchId, inputHash, visited*, candidateId, patchId, baseHash, expectedHash, sourceIds, originBatchIds: li deriva il server
// (ReconcileAliasComposer) e il motore condiviso valida il risultato canonico `reconcile-1`. Nessun database, nessun LLM.

/// <summary>Sezioni di analysis dello schema v3 (le stesse di <see cref="AiItems"/>): il nome nel JSON è quello di <see cref="AiItems.Sections"/>.</summary>
public enum AiSection
{
    [JsonStringEnumMemberName("goals")] Goals,
    [JsonStringEnumMemberName("scopeOverview")] ScopeOverview,
    [JsonStringEnumMemberName("scopeIncluded")] ScopeIncluded,
    [JsonStringEnumMemberName("scopeExcluded")] ScopeExcluded,
    [JsonStringEnumMemberName("requirements")] Requirements,
    [JsonStringEnumMemberName("flows")] Flows,
    [JsonStringEnumMemberName("constraints")] Constraints,
    [JsonStringEnumMemberName("dependencies")] Dependencies,
    [JsonStringEnumMemberName("assumptions")] Assumptions,
    [JsonStringEnumMemberName("openQuestions")] OpenQuestions,
}

/// <summary>Varianti dello schema nel profilo per batch v8 (<see cref="ReconcileAliasContracts.SchemaProfileByBatch"/>): stesso DTO, vincoli di grammatica diversi per stadio e vista.</summary>
public enum ReconcileSchemaVariant
{
    /// <summary>Foglia della vista A: lo schema completo <c>reconcile-alias-1</c> (fissato), invariato.</summary>
    Full,

    /// <summary>Foglia della vista B, riduzione e cross: <c>items</c> e <c>patches</c> vuoti, <c>focus</c> null.</summary>
    NoItems,

    /// <summary>Espansione e confronto: <c>items</c> vuoto; patch e focus come nello schema completo.</summary>
    Candidate,
}

/// <summary>
/// Varianti dello schema nel profilo per batch v9 (<see cref="ReconcileAliasContracts.SchemaProfileByBatchV2"/>): stesso DTO <c>reconcile-alias-1</c>, una variante per stadio (e per vista nelle foglie)
/// con i vincoli strutturali del contratto di quello stadio (nullabilità di <c>descriptor</c> e <c>focus</c>, cardinalità di <c>items</c> e <c>patches</c>, prefisso degli estremi dei candidati).
/// Il profilo v8 (<see cref="ReconcileSchemaVariant"/>) resta congelato e distinto.
/// </summary>
public enum ReconcileBatchSchema
{
    /// <summary>Foglia della vista A: items ammessi, descriptor obbligatorio, estremi E, focus null, patches vuote.</summary>
    LeafA,

    /// <summary>Foglia della vista B: items vuoti, descriptor obbligatorio, estremi E, focus null, patches vuote.</summary>
    LeafB,

    /// <summary>Riduzione (viste A e B): items vuoti, descriptor obbligatorio, estremi N, focus null, patches vuote.</summary>
    Reduce,

    /// <summary>Cross: items vuoti, descriptor null, estremi N, focus null, patches vuote.</summary>
    Cross,

    /// <summary>Espansione: items vuoti, descriptor null, estremi E, focus null oppure Unresolved, patches ammesse.</summary>
    Expand,

    /// <summary>Confronto: items vuoti, descriptor null, estremi E, focus obbligatorio Compared oppure Open, patches ammesse.</summary>
    Compare,
}

/// <summary>Esito dichiarato dal modello sul candidato in esame: Compared/Open nel confronto, Unresolved nell'espansione (il server lo traduce in NotProcessed candidate_unresolved).</summary>
public enum AiFocusOutcome { Compared, Open, Unresolved }

/// <summary>Estremo di un candidato: evidenza `E&lt;n&gt;` oppure nodo/insieme `N&lt;n&gt;` ricevuto nel batch.</summary>
public sealed class EndpointAliasConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return AnalysisAliasPattern.Matches(text, 'E') || AnalysisAliasPattern.Matches(text, 'N')
            ? text!
            : throw new JsonException("Formato non canonico: atteso E<n> o N<n> con n intero positivo senza zeri iniziali.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>`contract` è la costante «reconcile-alias-1».</summary>
public sealed class ReconcileAliasContractConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == ReconcileAliasContracts.SchemaVersion
            ? ReconcileAliasContracts.SchemaVersion
            : throw new JsonException($"contract deve essere \"{ReconcileAliasContracts.SchemaVersion}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>`contract` è la costante «reconcile-alias-2» (T135).</summary>
public sealed class ReconcileAliasContractV2Converter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == ReconcileAliasContracts.SchemaVersionV2
            ? ReconcileAliasContracts.SchemaVersionV2
            : throw new JsonException($"contract deve essere \"{ReconcileAliasContracts.SchemaVersionV2}\".");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileItem
{
    [JsonConverter(typeof(ItemKeyConverter))]
    public required string Key { get; init; }

    public required AiSection Section { get; init; }
    public required string Text { get; init; }
    public required DocumentAnalysisClassification Classification { get; init; }

    [JsonConverter(typeof(EvidenceAliasListConverter))]
    public required IReadOnlyList<string> Evidence { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileCandidate
{
    [JsonConverter(typeof(FindingKeyConverter))]
    public required string Key { get; init; }

    [JsonConverter(typeof(EndpointAliasConverter))]
    public required string Left { get; init; }

    [JsonConverter(typeof(EndpointAliasConverter))]
    public required string Right { get; init; }

    public required ReconcileProposedType Type { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
    public required string Rationale { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileFocus
{
    public required AiFocusOutcome Outcome { get; init; }
    public required string? Reason { get; init; }
}

/// <summary>
/// Output AI di un batch (<c>reconcile-alias-1</c>): tutte le proprietà obbligatorie, extra/duplicate/enum numerici/numeri fra virgolette rifiutati, <c>null</c> solo dove
/// dichiarato. Le patch riusano <see cref="AiPatch"/> di <c>document-analysis-alias-1</c> (bersaglio: item già proposto <c>i&lt;n&gt;</c> o relazione nota <c>R&lt;n&gt;</c>).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileOutput
{
    [JsonConverter(typeof(ReconcileAliasContractConverter))]
    public required string Contract { get; init; }

    public required string? Descriptor { get; init; }
    public required string? NoFindings { get; init; }
    public required IReadOnlyList<AiReconcileItem> Items { get; init; }
    public required IReadOnlyList<AiReconcileCandidate> Candidates { get; init; }
    public required AiReconcileFocus? Focus { get; init; }
    public required IReadOnlyList<AiPatch> Patches { get; init; }
}

/// <summary>Output AI di un batch `reconcile-alias-2` (T135, §13.2): i campi di reconcile-alias-1 più <c>gapAssessments</c>, obbligatoria anche vuota.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiReconcileOutputV2
{
    [JsonConverter(typeof(ReconcileAliasContractV2Converter))]
    public required string Contract { get; init; }

    public required string? Descriptor { get; init; }
    public required string? NoFindings { get; init; }
    public required IReadOnlyList<AiReconcileItem> Items { get; init; }
    public required IReadOnlyList<AiReconcileCandidate> Candidates { get; init; }
    public required AiReconcileFocus? Focus { get; init; }
    public required IReadOnlyList<AiPatch> Patches { get; init; }
    public required IReadOnlyList<AiGapAssessment> GapAssessments { get; init; }
}

/// <summary>Codec strict, JSON Schema e prompt di `reconcile-alias-1` (T131); codec e schema di `reconcile-alias-2` (T135).</summary>
public static class ReconcileAliasContracts
{
    public const string SchemaVersion = "reconcile-alias-1";
    public const string SchemaVersionV2 = "reconcile-alias-2";
    public const string SchemaName = "reconcile_alias";
    public const string PromptVersion = "document-reconcile-v3";
    public const string CatalogVersion = "reconcile-alias-catalog-1";

    /// <summary>
    /// Prompt della gerarchia della definizione v8: il v3 con l'obbligo di copertura tramite <c>items</c> ristretto alle foglie della vista A e il compito delle foglie della vista B
    /// (solo candidati, nessun collegamento inventato). Le definizioni v4–v7 restano sul v3 (<see cref="PromptVersion"/>).
    /// </summary>
    public const string PromptVersionV4 = "document-reconcile-v4";

    /// <summary>
    /// Prompt della gerarchia della definizione v10: il v4 più la sezione dell'espansione con membership esplicita (<see cref="MembershipFormat"/>: ogni voce indica il gruppo di appartenenza) e il tetto di
    /// <see cref="ExpansionCandidateCap"/> candidati. v4 resta intatto per le definizioni v8 e v9.
    /// </summary>
    public const string PromptVersionV5 = "document-reconcile-v5";

    /// <summary>Tetto di candidati per risposta dell'espansione nel profilo -3 (protezione contro l'enumerazione quadratica, mai criterio di selezione): oltre, il server non dichiara la copertura.</summary>
    public const int ExpansionCandidateCap = 16;

    /// <summary>Algoritmo di espansione congelato nella v10 (chiave <c>reconcileExpansion</c>): griglia deterministica di blocchi con membership, saturazione fail-closed e precheck del budget.</summary>
    public const string ExpansionAlgorithm = "expansion-grid-1";

    /// <summary>Formato del messaggio dell'espansione con membership esplicita (ogni VOCE porta <c>gruppo=…</c>); solo v10.</summary>
    public const string MembershipFormat = "membership-1";

    private static readonly Lazy<string> Prompt = new(() => LoadPrompt(PromptVersion + ".md"));
    private static readonly Lazy<string> PromptV4 = new(() => LoadPrompt(PromptVersionV4 + ".md"));
    private static readonly Lazy<string> PromptV5 = new(() => LoadPrompt(PromptVersionV5 + ".md"));

    /// <summary>Istruzioni di sistema dei batch alias (AI/Prompts/document-reconcile-v3.md, risorsa incorporata); il v2 resta congelato.</summary>
    public static string Instructions => Prompt.Value;

    /// <summary>Istruzioni di una versione congelata del prompt della gerarchia con alias: v3 (v4–v7) o v4 (v8). Versione ignota ⇒ eccezione, mai un ripiego su un altro prompt.</summary>
    public static string InstructionsFor(string promptVersion) => promptVersion switch
    {
        PromptVersion => Prompt.Value,
        PromptVersionV4 => PromptV4.Value,
        PromptVersionV5 => PromptV5.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(promptVersion), promptVersion, "Prompt della riconciliazione con alias non supportato."),
    };

    /// <summary>Stesse regole del codec dell'analisi con alias: extra, duplicati, null non dichiarati, enum fuori dal set e numeri fra virgolette sono <see cref="JsonException"/>.</summary>
    public static AiReconcileOutput Decode(string json)
    {
        try
        {
            // Rifiuta i duplicati anche nei sotto-oggetti letti tramite JsonElement (patch).
            using (JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }))
            {
            }

            return JsonSerializer.Deserialize<AiReconcileOutput>(json, AnalysisAliasContracts.Options) ?? throw new JsonException("Valore nullo.");
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            throw new JsonException(e.Message, e);
        }
    }

    public static string Encode(AiReconcileOutput output) => JsonSerializer.Serialize(output, AnalysisAliasContracts.Options);

    /// <summary>Forma canonica RFC 8785 del DTO.</summary>
    public static string Canonical(AiReconcileOutput output) => CanonicalJson.Canonicalize(Encode(output));

    /// <summary>`aiOutputHash`: SHA-256 della forma canonica del DTO.</summary>
    public static string Hash(AiReconcileOutput output) => CanonicalJson.HashText(Canonical(output));

    /// <summary>Codec strict di `reconcile-alias-2`: regole di <see cref="Decode"/> più la forma dei record `gap-assessment-1`.</summary>
    public static AiReconcileOutputV2 DecodeV2(string json)
    {
        try
        {
            using (JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }))
            {
            }

            var output = JsonSerializer.Deserialize<AiReconcileOutputV2>(json, AnalysisAliasContracts.Options) ?? throw new JsonException("Valore nullo.");
            GapAssessmentRules.ValidateShape(output.GapAssessments);
            return output;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            throw new JsonException(e.Message, e);
        }
    }

    public static string Encode(AiReconcileOutputV2 output) => JsonSerializer.Serialize(output, AnalysisAliasContracts.Options);

    public static string Canonical(AiReconcileOutputV2 output) => CanonicalJson.Canonicalize(Encode(output));

    /// <summary>`aiOutputHash` del DTO reconcile-alias-2: copre anche <c>gapAssessments</c>.</summary>
    public static string Hash(AiReconcileOutputV2 output) => CanonicalJson.HashText(Canonical(output));

    private static readonly Lazy<JsonElement> SchemaV2 = new(() => BuildSchema(SchemaVersionV2, gapAssessments: true));

    public static JsonElement JsonSchemaV2() => SchemaV2.Value;

    private static readonly Lazy<JsonElement> Schema = new(() => BuildSchema(SchemaVersion, gapAssessments: false));

    /// <summary>JSON Schema strict del DTO (stesso criterio di <see cref="AnalysisAliasContracts.JsonSchema"/>): vincola la forma, la risoluzione degli alias è del compositore.</summary>
    public static JsonElement JsonSchema() => Schema.Value;

    /// <summary>
    /// Profilo degli schemi per batch della definizione v8 (003, correzione T040 Run 2, chiamata 026): lo stesso DTO <c>reconcile-alias-1</c>, ma lo schema inviato al modello è scelto da stadio
    /// e vista del batch. Chiave di freeze <c>reconcileSchemaProfile</c>; v4–v7 inviano sempre <see cref="JsonSchema"/> (fissato), mai le varianti.
    /// </summary>
    public const string SchemaProfileByBatch = "reconcile-alias-schema-by-batch-1";

    private static readonly Lazy<JsonElement> SchemaNoItems = new(() => Restrict(Schema.Value, patchesAndFocus: true));

    private static readonly Lazy<JsonElement> SchemaCandidate = new(() => Restrict(Schema.Value, patchesAndFocus: false));

    /// <summary>
    /// Schema del batch nel profilo <see cref="SchemaProfileByBatch"/>, ricavato dalle stesse regole del compositore (mai più permissivo né più restrittivo): foglia della vista A ⇒ <see cref="JsonSchema"/>
    /// invariato; ogni altro batch ⇒ <c>items</c> vuoto (<c>maxItems: 0</c>); fuori da espansione e confronto anche <c>patches</c> vuoto e <c>focus</c> null. È un aiuto alla generazione (grammatica del
    /// provider), non una validazione: il compositore resta l'unica garanzia e rifiuta comunque ogni violazione (<c>work_output_invalid</c>).
    /// </summary>
    public static JsonElement JsonSchemaFor(string stage, ReconcileView view) => SchemaOf(SchemaVariantOf(stage, view));

    /// <summary>Variante del profilo per stadio e vista; uno stadio sconosciuto è un errore (nessun ripiego sullo schema completo).</summary>
    public static ReconcileSchemaVariant SchemaVariantOf(string stage, ReconcileView view) => stage switch
    {
        "leaf" => view == ReconcileView.A ? ReconcileSchemaVariant.Full : ReconcileSchemaVariant.NoItems,
        "reduce" or "cross" => ReconcileSchemaVariant.NoItems,
        "expand" or "compare" => ReconcileSchemaVariant.Candidate,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stadio di riconciliazione sconosciuto: nessuno schema del profilo per batch."),
    };

    public static JsonElement SchemaOf(ReconcileSchemaVariant variant) => variant switch
    {
        ReconcileSchemaVariant.Full => Schema.Value,
        ReconcileSchemaVariant.NoItems => SchemaNoItems.Value,
        ReconcileSchemaVariant.Candidate => SchemaCandidate.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Variante dello schema sconosciuta."),
    };

    /// <summary>Hash canonici delle varianti del profilo, in ordine fisso: entrano nel <c>configHash</c> della v8, così una variante diversa è un'identità diversa.</summary>
    public static string SchemaProfileHashes() => string.Join(
        ',', Enum.GetValues<ReconcileSchemaVariant>().Select(v => $"{v}={CanonicalJson.HashText(CanonicalJson.Canonicalize(SchemaOf(v).GetRawText()))}"));

    /// <summary>Copia dello schema completo con <c>items</c> vuoto e, se richiesto, <c>patches</c> vuoto e <c>focus</c> null. Ordine e insieme delle proprietà obbligatorie invariati.</summary>
    private static JsonElement Restrict(JsonElement full, bool patchesAndFocus)
    {
        var schema = JsonNode.Parse(full.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        // `items` (schema degli elementi) resta accanto a `maxItems`: il convertitore schema→grammatica di llama.cpp applica i limiti agli array con schema degli elementi.
        properties["items"]!["maxItems"] = 0;
        if (patchesAndFocus)
        {
            properties["patches"]!["maxItems"] = 0;
            properties["focus"] = new JsonObject { ["type"] = "null" };
        }

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Profilo degli schemi per batch della definizione v9 (003, T040 dopo il Run 3, chiamata 026: <c>descriptor: null</c> ammesso dallo schema v8 e vietato dal validatore). Stesso DTO, stesso
    /// compositore e validatore: lo schema di ogni batch esprime i vincoli STRUTTURALI del contratto del suo stadio (livelli 1 e 2), mai più permissivi né più restrittivi del validatore.
    /// Restano al solo validatore le regole semantiche e quelle condizionali (livello 3, D9–D12: coerenza op/value/target delle patch, sezione/classificazione/evidenze degli item,
    /// motivo dell'esito Open, stringhe vuote nei testi), la lunghezza massima di 512 caratteri del descriptor (nessun <c>maxLength</c>: la grammatica troncherebbe invece di rifiutare) e i testi
    /// composti di soli spazi (<c>\S</c> non è supportato dal convertitore di llama.cpp).
    /// </summary>
    public const string SchemaProfileByBatchV2 = "reconcile-alias-schema-by-batch-2";

    /// <summary>
    /// Profilo degli schemi per batch della definizione v10 (003, T040 dopo il Run 4, chiamate 034–036): le stesse sei varianti del profilo -2, byte per byte, tranne l'espansione che riceve
    /// <c>candidates.maxItems = <see cref="ExpansionCandidateCap"/></c>. Il tetto è una protezione contro l'enumerazione quadratica osservata (162 candidati, <c>finish=length</c>), non un criterio di selezione:
    /// un'espansione che raggiunge il tetto è «satura» e il server non dichiara mai la copertura. Il profilo -2 resta congelato e distinto.
    /// </summary>
    public const string SchemaProfileByBatchV3 = "reconcile-alias-schema-by-batch-3";

    /// <summary>Estremi dei candidati che sono nodi o insiemi (riduzione e cross); le evidenze usano <see cref="AnalysisAliasPattern.Evidence"/>.</summary>
    public const string NodeAliasPattern = "^N[1-9][0-9]*$";

    private static readonly Lazy<IReadOnlyDictionary<ReconcileBatchSchema, JsonElement>> BatchSchemas = new(() =>
        Enum.GetValues<ReconcileBatchSchema>().ToDictionary(s => s, s => BuildBatchSchema(Schema.Value, s)));

    /// <summary>Variante del profilo v9 per stadio e vista. Uno stadio sconosciuto o una foglia fuori dalle viste A e B è un errore: nessun ripiego su un altro schema.</summary>
    public static ReconcileBatchSchema BatchSchemaOf(string stage, ReconcileView view) => stage switch
    {
        "leaf" => view switch
        {
            ReconcileView.A => ReconcileBatchSchema.LeafA,
            ReconcileView.B => ReconcileBatchSchema.LeafB,
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Le foglie della riconciliazione appartengono alla vista A o B."),
        },
        "reduce" => ReconcileBatchSchema.Reduce,
        "cross" => ReconcileBatchSchema.Cross,
        "expand" => ReconcileBatchSchema.Expand,
        "compare" => ReconcileBatchSchema.Compare,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stadio di riconciliazione sconosciuto: nessuno schema del profilo per batch v9."),
    };

    public static JsonElement SchemaOf(ReconcileBatchSchema variant) =>
        BatchSchemas.Value.TryGetValue(variant, out var schema) ? schema : throw new ArgumentOutOfRangeException(nameof(variant), variant, "Variante dello schema v9 sconosciuta.");

    // Profilo -3: dizionario SEPARATO (clone delle varianti -2 con il solo tetto sull'espansione). `ReconcileBatchSchema` non riceve nuovi membri e `SchemaProfileV2Hashes`/`ProfileVariants(-2)` non cambiano.
    private static readonly Lazy<IReadOnlyDictionary<ReconcileBatchSchema, JsonElement>> BatchSchemasV3 = new(() =>
        Enum.GetValues<ReconcileBatchSchema>().ToDictionary(s => s, s => s == ReconcileBatchSchema.Expand ? WithCandidateCap(SchemaOf(s), ExpansionCandidateCap) : SchemaOf(s)));

    /// <summary>Schema del batch nel profilo <see cref="SchemaProfileByBatchV3"/>: identico al -2 salvo <c>candidates.maxItems</c> nell'espansione (accanto a <c>items</c>, il costrutto che il convertitore di llama.cpp applica).</summary>
    public static JsonElement SchemaOfV3(ReconcileBatchSchema variant) =>
        BatchSchemasV3.Value.TryGetValue(variant, out var schema) ? schema : throw new ArgumentOutOfRangeException(nameof(variant), variant, "Variante dello schema v10 sconosciuta.");

    /// <summary>Hash canonici delle varianti del profilo -3, in ordine fisso: entrano nel <c>configHash</c> della v10 (i profili -1 e -2 hanno i propri, invariati).</summary>
    public static string SchemaProfileV3Hashes() => string.Join(
        ',', Enum.GetValues<ReconcileBatchSchema>().Select(v => $"{v}={CanonicalJson.HashText(CanonicalJson.Canonicalize(SchemaOfV3(v).GetRawText()))}"));

    private static JsonElement WithCandidateCap(JsonElement schema, int cap)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node["properties"]!["candidates"]!["maxItems"] = cap;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>Hash canonici delle varianti del profilo v9, in ordine fisso: entrano nel <c>configHash</c> della v9 (il profilo v8 ha i propri, invariati).</summary>
    public static string SchemaProfileV2Hashes() => string.Join(
        ',', Enum.GetValues<ReconcileBatchSchema>().Select(v => $"{v}={CanonicalJson.HashText(CanonicalJson.Canonicalize(SchemaOf(v).GetRawText()))}"));

    /// <summary>
    /// Variante (nome stabile e schema) di un profilo per batch congelato, per stadio e vista: <see cref="SchemaProfileByBatch"/> (v8) o <see cref="SchemaProfileByBatchV2"/> (v9).
    /// Profilo ignoto ⇒ eccezione: mai un ripiego su un altro profilo o sullo schema completo.
    /// </summary>
    public static (string Name, JsonElement Schema) ProfileSchemaFor(string profile, string stage, ReconcileView view) => profile switch
    {
        SchemaProfileByBatch => Named(SchemaVariantOf(stage, view)),
        SchemaProfileByBatchV2 => Named(BatchSchemaOf(stage, view)),
        SchemaProfileByBatchV3 => NamedV3(BatchSchemaOf(stage, view)),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Profilo degli schemi per batch non supportato."),
    };

    /// <summary>Tutte le varianti di un profilo congelato, in ordine fisso (un agente per variante).</summary>
    public static IReadOnlyList<(string Name, JsonElement Schema)> ProfileVariants(string profile) => profile switch
    {
        SchemaProfileByBatch => [.. Enum.GetValues<ReconcileSchemaVariant>().Select(Named)],
        SchemaProfileByBatchV2 => [.. Enum.GetValues<ReconcileBatchSchema>().Select(Named)],
        SchemaProfileByBatchV3 => [.. Enum.GetValues<ReconcileBatchSchema>().Select(NamedV3)],
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Profilo degli schemi per batch non supportato."),
    };

    private static (string Name, JsonElement Schema) Named(ReconcileSchemaVariant variant) => (variant.ToString(), SchemaOf(variant));

    private static (string Name, JsonElement Schema) Named(ReconcileBatchSchema variant) => (variant.ToString(), SchemaOf(variant));

    private static (string Name, JsonElement Schema) NamedV3(ReconcileBatchSchema variant) => (variant.ToString(), SchemaOfV3(variant));

    /// <summary>Hash delle varianti del profilo congelato che entrano nel <c>configHash</c> (per la v8 la stessa stringa di sempre, <see cref="SchemaProfileHashes"/>).</summary>
    public static string ProfileHashes(string profile) => profile switch
    {
        SchemaProfileByBatch => SchemaProfileHashes(),
        SchemaProfileByBatchV2 => SchemaProfileV2Hashes(),
        SchemaProfileByBatchV3 => SchemaProfileV3Hashes(),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Profilo degli schemi per batch non supportato."),
    };

    /// <summary>
    /// Copia dello schema completo con i vincoli strutturali dello stadio. Ordine e insieme delle proprietà obbligatorie invariati; solo costrutti del convertitore schema→grammatica di llama.cpp
    /// (<c>type</c> anche <c>null</c>, <c>enum</c>, <c>pattern</c> ancorato, <c>minLength</c> con <c>type: string</c> e senza <c>pattern</c>, <c>maxItems</c> accanto a <c>items</c>).
    /// </summary>
    private static JsonElement BuildBatchSchema(JsonElement full, ReconcileBatchSchema variant)
    {
        var schema = JsonNode.Parse(full.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        var buildsNode = variant is ReconcileBatchSchema.LeafA or ReconcileBatchSchema.LeafB or ReconcileBatchSchema.Reduce;
        var nodeEndpoints = variant is ReconcileBatchSchema.Reduce or ReconcileBatchSchema.Cross;
        var candidateStage = variant is ReconcileBatchSchema.Expand or ReconcileBatchSchema.Compare;

        // descriptor: stringa non vuota nei batch che costruiscono nodi (foglie e riduzioni), null altrove. Lunghezza massima e soli spazi restano al validatore.
        properties["descriptor"] = buildsNode ? new JsonObject { ["type"] = "string", ["minLength"] = 1 } : new JsonObject { ["type"] = "null" };

        if (variant != ReconcileBatchSchema.LeafA)
        {
            properties["items"]!["maxItems"] = 0;
        }

        // Estremi dei candidati: evidenze nelle foglie, nell'espansione e nel confronto; nodi o insiemi nella riduzione e nel cross.
        var endpoint = nodeEndpoints ? NodeAliasPattern : AnalysisAliasPattern.Evidence;
        var candidate = properties["candidates"]!["items"]!["properties"]!.AsObject();
        candidate["left"]!["pattern"] = endpoint;
        candidate["right"]!["pattern"] = endpoint;

        var focus = properties["focus"]!.AsObject();
        switch (variant)
        {
            case ReconcileBatchSchema.Expand:
                focus["properties"]!["outcome"]!["enum"] = new JsonArray(nameof(AiFocusOutcome.Unresolved));
                break;
            case ReconcileBatchSchema.Compare:
                focus["type"] = "object";
                focus["properties"]!["outcome"]!["enum"] = new JsonArray(nameof(AiFocusOutcome.Compared), nameof(AiFocusOutcome.Open));
                break;
            default:
                properties["focus"] = new JsonObject { ["type"] = "null" };
                break;
        }

        if (!candidateStage)
        {
            properties["patches"]!["maxItems"] = 0;
        }

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonElement BuildSchema(string contract, bool gapAssessments)
    {
        var evidenceList = AnalysisAliasContracts.ArraySchema(AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.Evidence));
        var sections = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. new[] { "goals", "scopeOverview", "scopeIncluded", "scopeExcluded", "requirements", "flows", "constraints", "dependencies", "assumptions", "openQuestions" }.Select(n => (JsonNode)JsonValue.Create(n)!)]) };
        var item = AnalysisAliasContracts.ObjectSchema(
            ("key", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.ItemKey)), ("section", sections), ("text", AnalysisAliasContracts.TextSchema()),
            ("classification", AnalysisAliasContracts.EnumSchema<DocumentAnalysisClassification>()), ("evidence", evidenceList.DeepClone()));
        var candidate = AnalysisAliasContracts.ObjectSchema(
            ("key", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.FindingKey)), ("left", AnalysisAliasContracts.AliasSchema("^[EN][1-9][0-9]*$")),
            ("right", AnalysisAliasContracts.AliasSchema("^[EN][1-9][0-9]*$")), ("type", AnalysisAliasContracts.EnumSchema<ReconcileProposedType>()),
            ("basis", AnalysisAliasContracts.EnumSchema<EvidenceRelationBasis>()), ("rationale", AnalysisAliasContracts.TextSchema()));
        var focus = AnalysisAliasContracts.ObjectSchema(("outcome", AnalysisAliasContracts.EnumSchema<AiFocusOutcome>()), ("reason", AnalysisAliasContracts.NullableTextSchema()));
        focus["type"] = new JsonArray("object", "null");
        var relationValue = AnalysisAliasContracts.ObjectSchema(
            ("from", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.Evidence)), ("to", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.Evidence)),
            ("type", AnalysisAliasContracts.EnumSchema<EvidenceRelationType>()), ("basis", AnalysisAliasContracts.EnumSchema<EvidenceRelationBasis>()),
            ("rationale", AnalysisAliasContracts.NullableTextSchema()));
        var alternativeValue = AnalysisAliasContracts.ObjectSchema(
            ("text", AnalysisAliasContracts.TextSchema()), ("classification", AnalysisAliasContracts.EnumSchema<DocumentAnalysisClassification>()), ("reason", AnalysisAliasContracts.TextSchema()));
        var patch = AnalysisAliasContracts.ObjectSchema(
            ("key", AnalysisAliasContracts.AliasSchema(AnalysisAliasPattern.PatchKey)), ("op", AnalysisAliasContracts.EnumSchema<PatchOperation>()),
            ("target", AnalysisAliasContracts.ObjectSchema(
                ("item", AnalysisAliasContracts.NullableAliasSchema(AnalysisAliasPattern.ItemKey)), ("relation", AnalysisAliasContracts.NullableAliasSchema(AnalysisAliasPattern.Relation)))),
            ("evidence", evidenceList.DeepClone()),
            ("value", new JsonObject { ["anyOf"] = new JsonArray(AnalysisAliasContracts.ObjectSchema(), relationValue, alternativeValue) }));
        var schema = AnalysisAliasContracts.ObjectSchema(
            ("contract", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(contract) }),
            ("descriptor", AnalysisAliasContracts.NullableTextSchema()), ("noFindings", AnalysisAliasContracts.NullableTextSchema()),
            ("items", AnalysisAliasContracts.ArraySchema(item)), ("candidates", AnalysisAliasContracts.ArraySchema(candidate)), ("focus", focus),
            ("patches", AnalysisAliasContracts.ArraySchema(patch)));
        if (gapAssessments)
        {
            schema["properties"]!["gapAssessments"] = AnalysisAliasContracts.ArraySchema(AnalysisAliasContracts.GapAssessmentSchema());
            schema["required"]!.AsArray().Add("gapAssessments");
        }

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string LoadPrompt(string file)
    {
        using var stream = typeof(ReconcileAliasContracts).Assembly.GetManifestResourceStream("Prompts." + file)
            ?? throw new InvalidOperationException($"Prompt {file} non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
}
