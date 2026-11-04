using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.App.AI;

// Nucleo condiviso `reconcile-1` (contracts/ai-workflow.md §4.1, T120): DTO, codec strict, identità derivate e regole strutturali.
// Un solo contratto per single-call (I4) e gerarchico (I5): T071 estende CatalogEntry/Node/Page/Set senza ridefinire questi tipi.
// Nessun database, nessun LLM. Riferimenti allo snapshot: ReconciliationReferences.cs.

public enum ReconcileView { A, B, Cross }

public enum CandidateKind { Relation, Conflict, Alias }

public enum CandidateEndpointKind { Evidence, Node, Set }

public enum CandidateState { Pending, Expanded, Compared, Open, NotProcessed }

public enum PatchTargetKind { Item, Relation }

public enum PatchOperation { AddSupport, AddRelation, ProposeAlternative }

/// <summary>relation.type di §2, oppure Alias (solo per un candidato di kind Alias).</summary>
public enum ReconcileProposedType { Qualifies, Confirms, Contradicts, Corrects, Supersedes, DependsOn, AlternativeTo, SameRule, Alias }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateEndpoint
{
    public required CandidateEndpointKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Hash { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReconcileCandidate
{
    public required string CandidateId { get; init; }
    public required CandidateKind Kind { get; init; }
    public required CandidateEndpoint Left { get; init; }
    public required CandidateEndpoint Right { get; init; }
    public required ReconcileProposedType ProposedType { get; init; }
    public required string Rationale { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
    public required IReadOnlyList<string> OriginBatchIds { get; init; }
    public required string? ParentCandidateId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CandidateDisposition
{
    public required string CandidateId { get; init; }
    public required CandidateState State { get; init; }
    public required IReadOnlyList<string> ChildCandidateIds { get; init; }
    public required IReadOnlyList<string> ComparisonOutputHashes { get; init; }
    public required string? Reason { get; init; }
}

/// <summary>Proposta di item (schema item di AnalysisContent 1.1 con localKey): l'UUID finale è del server, mai del modello.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ItemProposal
{
    public required string LocalKey { get; init; }
    public required string Text { get; init; }
    public required DocumentAnalysisClassification Classification { get; init; }
    public required IReadOnlyList<Guid> EvidenceIds { get; init; }
    public required IReadOnlyList<Guid> SourceIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PatchTarget
{
    public required PatchTargetKind Kind { get; init; }
    public required string Id { get; init; }
    public required string ExpectedHash { get; init; }
}

/// <summary>Union strict del valore di una patch: il discriminante è <see cref="Patch.Operation"/>, mai JSON Patch generico.</summary>
public abstract record PatchValue;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddSupportValue : PatchValue
{
    public required IReadOnlyList<Guid> EvidenceIds { get; init; }
    public required IReadOnlyList<Guid> SourceIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddRelationValue : PatchValue
{
    public required Guid FromEvidenceId { get; init; }
    public required Guid ToEvidenceId { get; init; }
    public required EvidenceRelationType Type { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
    public required string? Rationale { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposeAlternativeValue : PatchValue
{
    public required string Text { get; init; }
    public required DocumentAnalysisClassification Classification { get; init; }
    public required IReadOnlyList<Guid> EvidenceIds { get; init; }
    public required IReadOnlyList<Guid> SourceIds { get; init; }
    public required string Reason { get; init; }
}

public sealed record Patch
{
    public required string PatchId { get; init; }
    public required string BaseHash { get; init; }
    public required PatchTarget Target { get; init; }
    public required PatchOperation Operation { get; init; }
    public required PatchValue Value { get; init; }
    public required IReadOnlyList<Guid> EvidenceIds { get; init; }
    public required IReadOnlyList<Guid> SourceIds { get; init; }
    public required IReadOnlyList<string> OriginBatchIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReconcileResult
{
    public required string BatchId { get; init; }
    public required string InputHash { get; init; }
    public required ReconcileView View { get; init; }
    public required int Level { get; init; }
    public required IReadOnlyList<string> VisitedEntryIds { get; init; }
    public required IReadOnlyList<string> VisitedNodeIds { get; init; }
    public required IReadOnlyList<ReconcileCandidate> Candidates { get; init; }
    public required IReadOnlyList<CandidateDisposition> Dispositions { get; init; }
    public required IReadOnlyList<ItemProposal> ItemProposals { get; init; }
    public required IReadOnlyList<Patch> Patches { get; init; }
    public required string? Descriptor { get; init; }
    public required string? NoFindingsReason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EvidenceMapEntry
{
    public required Guid ItemId { get; init; }
    public required IReadOnlyList<Guid> EvidenceIds { get; init; }
}

/// <summary>Output AI della singola chiamata (§4.5): `{results,analysis,evidenceMap}`, senza campi di identità/hash (del server).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SingleCallOutput
{
    public required IReadOnlyList<ReconcileResult> Results { get; init; }
    public required DocumentAnalysisContent Analysis { get; init; }
    public required IReadOnlyList<EvidenceMapEntry> EvidenceMap { get; init; }
}

// ---- Schemi gerarchici (§4.1, T071): solo forme; nessun algoritmo di partizione/riduzione (T072–T075) né tabelle (T122).

public enum CatalogKeyKind
{
    [JsonStringEnumMemberName("subject")] Subject,
    [JsonStringEnumMemberName("object")] Object,
    [JsonStringEnumMemberName("scope")] Scope,
    [JsonStringEnumMemberName("reference")] Reference,
    [JsonStringEnumMemberName("action")] Action,
    [JsonStringEnumMemberName("instrument")] Instrument,
    [JsonStringEnumMemberName("channel")] Channel,
    [JsonStringEnumMemberName("qualifier")] Qualifier,
    [JsonStringEnumMemberName("alias")] Alias,
}

public enum CatalogKeyOrigin { Literal, Inferred }

public enum CatalogPageKind { Members, Keys, Relations }

/// <summary>Chiave di directory: vista A|B (mai Cross); le chiavi letterali e gli alias inferiti (con originOutputHash) restano distinti.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogKey
{
    public required ReconcileView View { get; init; }
    public required CatalogKeyKind Kind { get; init; }
    public required string Value { get; init; }
    public required CatalogKeyOrigin Origin { get; init; }
    public required string? OriginOutputHash { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogEntry
{
    public required string EntryId { get; init; }
    public required Guid EvidenceId { get; init; }
    public required Guid EvidenceVersionId { get; init; }
    public required string ContentHash { get; init; }
    public required IReadOnlyList<Guid> SourceIds { get; init; }
    public required IReadOnlyList<CatalogKey> Keys { get; init; }
    public required IReadOnlyList<Guid> KnownRelationIds { get; init; }
    public required IReadOnlyList<ReconcileView> UnclassifiedViews { get; init; }
}

/// <summary>Nodo di routing: membership esatta fuori dal prompt (pagine), descriptor solo per instradare e non citabile.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogNode
{
    public required string NodeId { get; init; }
    public required ReconcileView View { get; init; }
    public required int Level { get; init; }
    public required int Ordinal { get; init; }
    public required IReadOnlyList<string> ChildNodeIds { get; init; }
    public required string MemberSetHash { get; init; }
    public required int MemberCount { get; init; }
    public required IReadOnlyList<string> KeyPageHashes { get; init; }
    public required IReadOnlyList<string> RelationPageHashes { get; init; }
    public required string Descriptor { get; init; }
    public required string DescriptorOutputHash { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogSet
{
    public required string SetId { get; init; }
    public required ReconcileView View { get; init; }
    public required IReadOnlyList<string> MemberNodeIds { get; init; }
    public required string MemberSetHash { get; init; }
    public required int MemberCount { get; init; }
}

/// <summary>Voce di pagina (union secondo <see cref="CatalogPage.Kind"/>): membership, directory di chiavi o relazioni note.</summary>
public abstract record PageEntry;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemberPageEntry : PageEntry
{
    public required Guid EvidenceId { get; init; }
    public required Guid EvidenceVersionId { get; init; }
    public required string ContentHash { get; init; }
}

/// <summary>Chiave con esattamente uno fra memberIds (UUID) e memberPageRefs (hash di pagine Members) non vuoto.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KeyPageEntry : PageEntry
{
    public required CatalogKey Key { get; init; }
    public required IReadOnlyList<Guid> MemberIds { get; init; }
    public required IReadOnlyList<string> MemberPageRefs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RelationPageEntry : PageEntry
{
    public required Guid RelationId { get; init; }
    public required Guid FromEvidenceId { get; init; }
    public required Guid ToEvidenceId { get; init; }
    public required EvidenceRelationType Type { get; init; }
    public required EvidenceRelationBasis Basis { get; init; }
}

/// <summary>Pagina append-only: ordinal contigui e catena di hash (previousPageHash = hash della pagina precedente, null alla prima).</summary>
public sealed record CatalogPage
{
    public required string PageId { get; init; }
    public required string NodeId { get; init; }
    public required CatalogPageKind Kind { get; init; }
    public required int Ordinal { get; init; }
    public required IReadOnlyList<PageEntry> Entries { get; init; }
    public required string? PreviousPageHash { get; init; }
}

/// <summary>Pagina con `entries` union discriminata da `kind`; esattamente le 6 proprietà, nessuna extra o duplicata.</summary>
internal sealed class CatalogPageConverter : JsonConverter<CatalogPage>
{
    private static readonly string[] Names = ["pageId", "nodeId", "kind", "ordinal", "entries", "previousPageHash"];

    public override CatalogPage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Pagina: oggetto richiesto.");
        }

        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != Names.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Count || Names.Any(n => !names.Contains(n)))
        {
            throw new JsonException("Pagina: proprietà esatte pageId, nodeId, kind, ordinal, entries, previousPageHash.");
        }

        var kind = root.GetProperty("kind").Deserialize<CatalogPageKind>(options);
        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Pagina.entries: array richiesto.");
        }

        List<PageEntry> list = kind switch
        {
            CatalogPageKind.Members => [.. entries.Deserialize<List<MemberPageEntry>>(options)!],
            CatalogPageKind.Keys => [.. entries.Deserialize<List<KeyPageEntry>>(options)!],
            _ => [.. entries.Deserialize<List<RelationPageEntry>>(options)!],
        };

        return new CatalogPage
        {
            PageId = root.GetProperty("pageId").Deserialize<string>(options) ?? throw new JsonException("Pagina.pageId: null non ammesso."),
            NodeId = root.GetProperty("nodeId").Deserialize<string>(options) ?? throw new JsonException("Pagina.nodeId: null non ammesso."),
            Kind = kind,
            Ordinal = root.GetProperty("ordinal").Deserialize<int>(options),
            Entries = list,
            PreviousPageHash = root.GetProperty("previousPageHash").Deserialize<string?>(options),
        };
    }

    public override void Write(Utf8JsonWriter writer, CatalogPage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("pageId", value.PageId);
        writer.WriteString("nodeId", value.NodeId);
        writer.WritePropertyName("kind");
        JsonSerializer.Serialize(writer, value.Kind, options);
        writer.WriteNumber("ordinal", value.Ordinal);
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        foreach (var entry in value.Entries)
        {
            JsonSerializer.Serialize(writer, entry, entry.GetType(), options);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("previousPageHash");
        JsonSerializer.Serialize(writer, value.PreviousPageHash, options);
        writer.WriteEndObject();
    }
}

/// <summary>Envelope server `reconcile-1`: identità e hash non sono mai ridefiniti dall'AI.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReconcileEnvelope<TPayload>
{
    public const string SchemaVersionValue = "reconcile-1";
    public const string AlgorithmVersionValue = "global-reconcile-v1";

    public required string SchemaVersion { get; init; }
    public required Guid RunId { get; init; }
    public required string SourceHash { get; init; }
    public required string AlgorithmVersion { get; init; }
    public required string PolicyHash { get; init; }
    public required string BaseHash { get; init; }
    public required TPayload Payload { get; init; }
}

/// <summary>Codec strict: extra, duplicati, null non dichiarati, enum fuori dal set (anche per maiuscole), numeri fra virgolette e UUID non lowercase sono <see cref="JsonException"/>.</summary>
public static class ReconciliationCodec
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        Converters = { new LowercaseGuidConverter(), new ExactEnumConverterFactory(), new PatchConverter(), new CatalogPageConverter() },
    };

    public static T Decode<T>(string json)
    {
        try
        {
            // Rifiuta i duplicati anche nei sotto-oggetti letti tramite JsonElement.
            using (JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }))
            {
            }

            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Valore nullo.");
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

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static SingleCallOutput DecodeSingleCall(string json) => Decode<SingleCallOutput>(json);

    public static ReconcileResult DecodeResult(string json) => Decode<ReconcileResult>(json);

    /// <summary>Forma canonica RFC 8785 di un valore serializzato con questo codec.</summary>
    public static string Canonical<T>(T value) => CanonicalJson.Canonicalize(Encode(value));
}

internal sealed class LowercaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return text is not null && Guid.TryParseExact(text, "D", out var guid) && text == guid.ToString("D")
            ? guid
            : throw new JsonException("UUID non valido: formato D lowercase richiesto.");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D"));

    public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString("D"));
}

internal sealed class ExactEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ExactEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        // Nome esatto del membro (o JsonStringEnumMemberName, es. AnalysisMessageRole "user"): niente maiuscole alternative né interi.
        private static readonly Dictionary<string, T> ByName = typeof(T).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .ToDictionary(f => f.GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), false).OfType<JsonStringEnumMemberNameAttribute>().FirstOrDefault()?.Name ?? f.Name, f => (T)f.GetValue(null)!, StringComparer.Ordinal);

        private static readonly Dictionary<T, string> ByValue = ByName.ToDictionary(kv => kv.Value, kv => kv.Key);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return text is not null && ByName.TryGetValue(text, out var value) ? value : throw new JsonException($"Valore di {typeof(T).Name} non ammesso.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(ByValue.TryGetValue(value, out var name) ? name : throw new JsonException($"Valore di {typeof(T).Name} non ammesso."));
    }
}

/// <summary>Patch con value union discriminato da `operation`; esattamente le 8 proprietà, nessuna extra o duplicata.</summary>
internal sealed class PatchConverter : JsonConverter<Patch>
{
    private static readonly string[] Names = ["patchId", "baseHash", "target", "operation", "value", "evidenceIds", "sourceIds", "originBatchIds"];

    public override Patch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Patch: oggetto richiesto.");
        }

        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != Names.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Count || Names.Any(n => !names.Contains(n)))
        {
            throw new JsonException("Patch: proprietà esatte patchId, baseHash, target, operation, value, evidenceIds, sourceIds, originBatchIds.");
        }

        T Get<T>(string name) => root.GetProperty(name).Deserialize<T>(options) ?? throw new JsonException($"Patch.{name}: null non ammesso.");
        var operation = Get<PatchOperation>("operation");
        var valueElement = root.GetProperty("value");
        if (valueElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Patch.value: oggetto richiesto.");
        }

        PatchValue value = operation switch
        {
            PatchOperation.AddSupport => valueElement.Deserialize<AddSupportValue>(options)!,
            PatchOperation.AddRelation => valueElement.Deserialize<AddRelationValue>(options)!,
            PatchOperation.ProposeAlternative => valueElement.Deserialize<ProposeAlternativeValue>(options)!,
            _ => throw new JsonException("Patch.operation sconosciuta."),
        };

        return new Patch
        {
            PatchId = Get<string>("patchId"),
            BaseHash = Get<string>("baseHash"),
            Target = Get<PatchTarget>("target"),
            Operation = operation,
            Value = value,
            EvidenceIds = Get<List<Guid>>("evidenceIds"),
            SourceIds = Get<List<Guid>>("sourceIds"),
            OriginBatchIds = Get<List<string>>("originBatchIds"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Patch value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("patchId", value.PatchId);
        writer.WriteString("baseHash", value.BaseHash);
        writer.WritePropertyName("target");
        JsonSerializer.Serialize(writer, value.Target, options);
        writer.WritePropertyName("operation");
        JsonSerializer.Serialize(writer, value.Operation, options);
        writer.WritePropertyName("value");
        JsonSerializer.Serialize(writer, value.Value, value.Value.GetType(), options);
        writer.WritePropertyName("evidenceIds");
        JsonSerializer.Serialize(writer, value.EvidenceIds, options);
        writer.WritePropertyName("sourceIds");
        JsonSerializer.Serialize(writer, value.SourceIds, options);
        writer.WritePropertyName("originBatchIds");
        JsonSerializer.Serialize(writer, value.OriginBatchIds, options);
        writer.WriteEndObject();
    }
}

/// <summary>
/// ID derivati (§4.1): SHA-256 della forma canonica di `{"c":[componenti],"t":prefisso}`; il prefisso tipizza il dominio, quindi un hash di
/// candidato non collide con uno di patch o di voce. Insiemi (evidenceIds, sourceIds) ordinati per UUID testuale lowercase (= byte order RFC 4122).
/// </summary>
public static class ReconciliationIds
{
    public static string EntryId(string sourceHash, Guid evidenceId, string contentHash) =>
        Typed("entry", sourceHash, evidenceId.ToString("D"), contentHash);

    /// <summary>Endpoint ordinati per (kind,id,hash) per i tipi simmetrici (Contradicts, AlternativeTo, SameRule) e per Alias; diretti altrimenti.</summary>
    public static string CandidateId(CandidateKind kind, CandidateEndpoint left, CandidateEndpoint right, ReconcileProposedType proposedType, string sourceHash)
    {
        var symmetric = kind == CandidateKind.Alias || proposedType is ReconcileProposedType.Contradicts or ReconcileProposedType.AlternativeTo or ReconcileProposedType.SameRule;
        var endpoints = new[] { left, right };
        if (symmetric)
        {
            endpoints = [.. endpoints.OrderBy(e => (int)e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Hash, StringComparer.Ordinal)];
        }

        return Typed("candidate", kind.ToString(), Endpoint(endpoints[0]), Endpoint(endpoints[1]), proposedType.ToString(), sourceHash);
    }

    /// <summary>PatchId = hash(baseHash,target,operation,value,evidenceIds,sourceIds); origini e batch non fanno parte dell'identità.</summary>
    public static string PatchId(Patch patch)
    {
        var value = JsonNode.Parse(ReconciliationCodec.Encode<object>(patch.Value))!.AsObject();
        foreach (var key in new[] { "evidenceIds", "sourceIds" })
        {
            if (value[key] is JsonArray array)
            {
                var sorted = array.Select(n => n!.GetValue<string>()).OrderBy(s => s, StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray();
                value[key] = new JsonArray(sorted);
            }
        }

        return Typed(
            "patch", patch.BaseHash, $"{patch.Target.Kind}|{patch.Target.Id}|{patch.Target.ExpectedHash}", patch.Operation.ToString(), value.ToJsonString(),
            string.Join(',', SortedIds(patch.EvidenceIds)), string.Join(',', SortedIds(patch.SourceIds)));
    }

    /// <summary>Hash di membership: SHA-256 tipizzato delle terne (evidenza, versione, contentHash) ordinate per EvidenceId.</summary>
    public static string MemberSetHash(IEnumerable<MemberPageEntry> members) => throw new NotImplementedException();

    /// <summary>nodeId = hash(view,level,ordinal,child/member hash,inputHash): non dipende dalle pagine del nodo (nessun ciclo).</summary>
    public static string NodeId(ReconcileView view, int level, int ordinal, string memberSetHash, IEnumerable<string> childNodeIds, string inputHash) => throw new NotImplementedException();

    /// <summary>Hash di pagina: SHA-256 della forma canonica della pagina senza pageId (è il previousPageHash della successiva).</summary>
    public static string PageHash(CatalogPage page) => throw new NotImplementedException();

    /// <summary>pageId = hash(nodeId,kind,ordinal,entries,previousPageHash).</summary>
    public static string PageId(CatalogPage page) => throw new NotImplementedException();

    /// <summary>setId = hash(view,memberNodeIds,memberSetHash).</summary>
    public static string SetId(ReconcileView view, IEnumerable<string> memberNodeIds, string memberSetHash) => throw new NotImplementedException();

    public static IEnumerable<string> SortedIds(IEnumerable<Guid> ids) => ids.Select(g => g.ToString("D")).OrderBy(s => s, StringComparer.Ordinal);

    private static string Endpoint(CandidateEndpoint e) => $"{e.Kind}|{e.Id}|{e.Hash}";

    private static string Typed(string prefix, params string[] components)
    {
        var node = new JsonObject { ["t"] = prefix, ["c"] = new JsonArray([.. components.Select(c => (JsonNode)c)]) };
        return CanonicalJson.Sha256Hex(node.ToJsonString());
    }
}

/// <summary>Regole strutturali verificabili senza snapshot: formati hash, insiemi ordinati, enum combinati, identità derivate, forma della singola chiamata.</summary>
public static class ReconciliationContractRules
{
    private static readonly ReconcileView[] SingleCallViews = [ReconcileView.A, ReconcileView.B, ReconcileView.Cross];

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static IReadOnlyList<string> CheckEnvelope<T>(ReconcileEnvelope<T> envelope)
    {
        var v = new List<string>();
        if (envelope.SchemaVersion != ReconcileEnvelope<T>.SchemaVersionValue)
        {
            v.Add("schemaVersion: deve essere \"reconcile-1\".");
        }

        if (envelope.AlgorithmVersion != ReconcileEnvelope<T>.AlgorithmVersionValue)
        {
            v.Add("algorithmVersion: deve essere \"global-reconcile-v1\".");
        }

        foreach (var (name, hash) in new[] { ("sourceHash", envelope.SourceHash), ("policyHash", envelope.PolicyHash), ("baseHash", envelope.BaseHash) })
        {
            if (!IsHash(hash))
            {
                v.Add($"{name}: SHA-256 esadecimale lowercase richiesto.");
            }
        }

        return v;
    }

    /// <summary>Contratto della singola chiamata: A/B/Cross esattamente in quest'ordine, level 0, descriptor null, tutto il resto come ogni ReconcileResult.</summary>
    public static IReadOnlyList<string> CheckSingleCall(SingleCallOutput output, string sourceHash)
    {
        var v = new List<string>();
        if (output.Results.Count != 3 || !output.Results.Select(r => r.View).SequenceEqual(SingleCallViews))
        {
            v.Add("results: esattamente tre risultati nell'ordine A, B, Cross.");
        }

        for (var i = 0; i < output.Results.Count; i++)
        {
            var r = output.Results[i];
            if (r.Level != 0)
            {
                v.Add($"results[{i}].level: la singola chiamata usa level 0.");
            }

            if (r.Descriptor is not null)
            {
                v.Add($"results[{i}].descriptor: la singola chiamata non costruisce nodi (null).");
            }

            if (r.VisitedNodeIds.Count != 0)
            {
                v.Add($"results[{i}].visitedNodeIds: nessun nodo nella singola chiamata.");
            }

            v.AddRange(CheckResult(r, sourceHash).Select(m => $"results[{i}].{m}"));
        }

        var items = ItemIds(output.Analysis).ToList();
        for (var i = 0; i < output.EvidenceMap.Count; i++)
        {
            Guids(v, $"evidenceMap[{i}].evidenceIds", output.EvidenceMap[i].EvidenceIds);
        }

        if (output.EvidenceMap.Select(e => e.ItemId).Distinct().Count() != output.EvidenceMap.Count)
        {
            v.Add("evidenceMap: itemId duplicato.");
        }

        if (output.EvidenceMap.Any(e => !items.Contains(e.ItemId)))
        {
            v.Add("evidenceMap: itemId non presente fra gli item di analysis.");
        }

        return v;
    }

    public static IEnumerable<Guid> ItemIds(DocumentAnalysisContent a) =>
        new[] { a.Goals, a.ContextAndScope.Overview, a.ContextAndScope.Included, a.ContextAndScope.Excluded, a.Requirements, a.OperationalFlows, a.Constraints, a.Dependencies, a.Assumptions, a.OpenQuestions }
            .SelectMany(s => s).Select(i => i.Id)
            .Concat(a.Conflicts.SelectMany(c => c.Alternatives.Select(x => x.Id)));

    public static IReadOnlyList<string> CheckResult(ReconcileResult r, string sourceHash)
    {
        var v = new List<string>();
        if (string.IsNullOrWhiteSpace(r.BatchId))
        {
            v.Add("batchId: vuoto.");
        }

        if (!IsHash(r.InputHash))
        {
            v.Add("inputHash: SHA-256 esadecimale lowercase richiesto.");
        }

        if (r.Level < 0)
        {
            v.Add("level: minimo 0.");
        }

        Sorted(v, "visitedEntryIds", r.VisitedEntryIds, hashes: true);
        Sorted(v, "visitedNodeIds", r.VisitedNodeIds, hashes: true);
        if (r.Descriptor is not null && (string.IsNullOrWhiteSpace(r.Descriptor) || r.Descriptor.EnumerateRunes().Count() > 512))
        {
            v.Add("descriptor: non vuoto e al massimo 512 caratteri Unicode.");
        }

        if (r.NoFindingsReason is not null && string.IsNullOrWhiteSpace(r.NoFindingsReason))
        {
            v.Add("noFindingsReason: non vuoto quando valorizzato.");
        }

        if (r.ItemProposals.Count == 0 && r.Candidates.Count == 0 && r.Patches.Count == 0 && r.NoFindingsReason is null)
        {
            v.Add("noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch.");
        }

        Unique(v, "candidates", r.Candidates.Select(c => c.CandidateId));
        for (var i = 0; i < r.Candidates.Count; i++)
        {
            v.AddRange(CheckCandidate(r.Candidates[i], sourceHash).Select(m => $"candidates[{i}].{m}"));
        }

        for (var i = 0; i < r.Dispositions.Count; i++)
        {
            v.AddRange(CheckDisposition(r.Dispositions[i]).Select(m => $"dispositions[{i}].{m}"));
        }

        Unique(v, "dispositions", r.Dispositions.Select(d => d.CandidateId));
        Unique(v, "itemProposals.localKey", r.ItemProposals.Select(p => p.LocalKey));
        for (var i = 0; i < r.ItemProposals.Count; i++)
        {
            var p = r.ItemProposals[i];
            if (string.IsNullOrWhiteSpace(p.LocalKey) || string.IsNullOrWhiteSpace(p.Text))
            {
                v.Add($"itemProposals[{i}]: localKey e text non vuoti.");
            }

            Guids(v, $"itemProposals[{i}].evidenceIds", p.EvidenceIds);
            Guids(v, $"itemProposals[{i}].sourceIds", p.SourceIds);
        }

        Unique(v, "patches", r.Patches.Select(p => p.PatchId));
        for (var i = 0; i < r.Patches.Count; i++)
        {
            v.AddRange(CheckPatch(r.Patches[i]).Select(m => $"patches[{i}].{m}"));
        }

        return v;
    }

    public static IReadOnlyList<string> CheckCandidate(ReconcileCandidate c, string sourceHash)
    {
        var v = new List<string>();
        if (!IsHash(c.Left.Hash) || !IsHash(c.Right.Hash))
        {
            v.Add("endpoint.hash: SHA-256 esadecimale lowercase richiesto.");
        }

        if (c.Left.Kind == c.Right.Kind && c.Left.Id == c.Right.Id)
        {
            v.Add("endpoint: devono essere distinti.");
        }

        if ((c.Kind == CandidateKind.Alias) != (c.ProposedType == ReconcileProposedType.Alias))
        {
            v.Add("proposedType: Alias se e solo se kind Alias.");
        }

        if (c.Kind == CandidateKind.Conflict && c.ProposedType is not (ReconcileProposedType.Contradicts or ReconcileProposedType.AlternativeTo))
        {
            v.Add("proposedType: un Conflict richiede Contradicts o AlternativeTo.");
        }

        if (string.IsNullOrWhiteSpace(c.Rationale))
        {
            v.Add("rationale: non vuota.");
        }

        if (c.OriginBatchIds.Count == 0)
        {
            v.Add("originBatchIds: non vuoto.");
        }

        Sorted(v, "originBatchIds", c.OriginBatchIds, hashes: false);
        if (c.ParentCandidateId is not null && !IsHash(c.ParentCandidateId))
        {
            v.Add("parentCandidateId: hash o null.");
        }

        if (c.CandidateId != ReconciliationIds.CandidateId(c.Kind, c.Left, c.Right, c.ProposedType, sourceHash))
        {
            v.Add("candidateId: diverso dall'ID derivato dal server.");
        }

        return v;
    }

    public static IReadOnlyList<string> CheckDisposition(CandidateDisposition d)
    {
        var v = new List<string>();
        if (!IsHash(d.CandidateId))
        {
            v.Add("candidateId: hash richiesto.");
        }

        Sorted(v, "childCandidateIds", d.ChildCandidateIds, hashes: true);
        Sorted(v, "comparisonOutputHashes", d.ComparisonOutputHashes, hashes: true);
        var hasReason = !string.IsNullOrWhiteSpace(d.Reason);
        if (d.Reason is not null && !hasReason)
        {
            v.Add("reason: non vuoto quando valorizzato.");
        }

        switch (d.State)
        {
            case CandidateState.Expanded when d.ChildCandidateIds.Count == 0:
                v.Add("Expanded richiede figli.");
                break;
            case CandidateState.Compared when d.ComparisonOutputHashes.Count == 0:
                v.Add("Compared richiede il confronto di evidenze integrali (comparisonOutputHashes).");
                break;
            case CandidateState.Open when d.ComparisonOutputHashes.Count == 0 || !hasReason:
                v.Add("Open richiede confronto integrale e motivo del dubbio.");
                break;
            case CandidateState.NotProcessed when !hasReason:
                v.Add("NotProcessed richiede l'errore in reason.");
                break;
        }

        return v;
    }

    public static IReadOnlyList<string> CheckPatch(Patch p)
    {
        var v = new List<string>();
        if (!IsHash(p.BaseHash) || !IsHash(p.Target.ExpectedHash))
        {
            v.Add("baseHash/expectedHash: SHA-256 esadecimale lowercase richiesto.");
        }

        if (string.IsNullOrWhiteSpace(p.Target.Id))
        {
            v.Add("target.id: vuoto.");
        }

        Guids(v, "evidenceIds", p.EvidenceIds);
        Guids(v, "sourceIds", p.SourceIds);
        Sorted(v, "originBatchIds", p.OriginBatchIds, hashes: false);
        if (p.OriginBatchIds.Count == 0)
        {
            v.Add("originBatchIds: non vuoto.");
        }

        var ok = true;
        switch (p.Value)
        {
            case AddSupportValue s when p.Operation == PatchOperation.AddSupport:
                ok = Same(s.EvidenceIds, p.EvidenceIds) && Same(s.SourceIds, p.SourceIds);
                break;
            case AddRelationValue r when p.Operation == PatchOperation.AddRelation:
                if (p.Target.Kind != PatchTargetKind.Item)
                {
                    v.Add("target: AddRelation ancora l'item esistente, non una relazione.");
                }

                if (r.FromEvidenceId == r.ToEvidenceId)
                {
                    v.Add("value: gli endpoint della relazione devono essere distinti.");
                }

                if (r.Rationale is not null && string.IsNullOrWhiteSpace(r.Rationale))
                {
                    v.Add("value.rationale: non vuota quando valorizzata.");
                }

                // La relazione cita i suoi due endpoint: l'envelope li ripete (stessa autorità, non due).
                ok = Same(new[] { r.FromEvidenceId, r.ToEvidenceId }, p.EvidenceIds);
                break;
            case ProposeAlternativeValue a when p.Operation == PatchOperation.ProposeAlternative:
                if (string.IsNullOrWhiteSpace(a.Text) || string.IsNullOrWhiteSpace(a.Reason))
                {
                    v.Add("value: text e reason non vuoti.");
                }

                ok = Same(a.EvidenceIds, p.EvidenceIds) && Same(a.SourceIds, p.SourceIds);
                break;
            default:
                v.Add("value: non coerente con operation.");
                break;
        }

        if (!ok)
        {
            v.Add("evidenceIds/sourceIds: devono coincidere con quelli del value.");
        }

        if (p.PatchId != ReconciliationIds.PatchId(p))
        {
            v.Add("patchId: diverso dall'ID derivato dal server.");
        }

        return v;
    }

    private static bool Same(IEnumerable<Guid> a, IEnumerable<Guid> b) =>
        ReconciliationIds.SortedIds(a).SequenceEqual(ReconciliationIds.SortedIds(b), StringComparer.Ordinal);

    private static bool IsSortedDistinct(List<string> sorted, List<string> actual) =>
        sorted.SequenceEqual(actual, StringComparer.Ordinal) && sorted.Distinct(StringComparer.Ordinal).Count() == sorted.Count;

    private static void Guids(List<string> v, string path, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 || !IsSortedDistinct([.. ReconciliationIds.SortedIds(ids)], [.. ids.Select(g => g.ToString("D"))]))
        {
            v.Add($"{path}: non vuoto, in ordine UUID e senza duplicati.");
        }
    }

    private static void Sorted(List<string> v, string path, IReadOnlyList<string> values, bool hashes)
    {
        if (!IsSortedDistinct([.. values.OrderBy(s => s, StringComparer.Ordinal)], [.. values]))
        {
            v.Add($"{path}: in ordine ordinal e senza duplicati.");
        }

        if (hashes && values.Any(s => !IsHash(s)))
        {
            v.Add($"{path}: SHA-256 esadecimali lowercase richiesti.");
        }
    }

    private static void Unique(List<string> v, string path, IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            v.Add($"{path}: identificativo duplicato.");
        }
    }
}

/// <summary>Regole strutturali dei cataloghi gerarchici (§4.1) verificabili da un singolo oggetto o dalla catena di pagine di un nodo.</summary>
public static class ReconciliationCatalogRules
{
    public const int MaxDescriptorRunes = 512;

    public static IReadOnlyList<string> CheckEntry(CatalogEntry entry, string sourceHash) => throw new NotImplementedException();

    public static IReadOnlyList<string> CheckKey(CatalogKey key) => throw new NotImplementedException();

    public static IReadOnlyList<string> CheckNode(CatalogNode node, string inputHash) => throw new NotImplementedException();

    public static IReadOnlyList<string> CheckPage(CatalogPage page) => throw new NotImplementedException();

    /// <summary>Pagine di un solo (nodo, kind): ordinal 0..n-1 contigui, previousPageHash = PageHash della precedente.</summary>
    public static IReadOnlyList<string> CheckPageChain(IReadOnlyList<CatalogPage> pages) => throw new NotImplementedException();

    public static IReadOnlyList<string> CheckSet(CatalogSet set) => throw new NotImplementedException();
}
