using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// Fixture deterministiche della slice 002 (T001): nessuna rete, nessun segreto, nessun LLM reale. Contiene i builder delle risposte
/// del parser (<c>document-parse-1</c>, contracts/parser.md) e dell'output di estrazione (<c>evidence-1.1.0</c>, contracts/ai-workflow.md §2),
/// un IChatClient finto con contatore degli invii e un clock controllabile. Riusa TestApp/ApiHarness/PostgresDatabaseFixture dove serve
/// un'applicazione o un database: qui non ne duplica l'infrastruttura.
/// </summary>
public static class DocumentTestFixtures
{
    public const string ParseSchemaVersion = "document-parse-1";
    public const string EvidenceSchemaVersion = "evidence-1.1.0";

    /// <summary>SHA-256 esadecimale minuscolo dei byte UTF-8: lo stesso formato degli hash di documento del contratto.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>UUID stabile derivato da un nome: stesse fixture, stessi identificativi a ogni esecuzione.</summary>
    public static Guid StableGuid(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));
}

/// <summary>Costruisce una risposta <c>document-parse-1</c> valida; ogni parametro ha un default coerente e riproducibile.</summary>
public sealed class ParseManifestBuilder
{
    private readonly List<JsonObject> _blocks = [];
    private readonly List<JsonObject> _inventory = [];
    private readonly List<JsonObject> _chunks = [];
    private readonly List<JsonObject> _issues = [];
    private string _format = "pdf";
    private string _originalSha256 = new('a', 64);
    private int? _pageCount = 1;
    private int _originalByteLength = 1;

    public ParseManifestBuilder Format(string format, int? pageCount)
    {
        _format = format;
        _pageCount = pageCount;
        return this;
    }

    public ParseManifestBuilder Original(string sha256, int byteLength)
    {
        _originalSha256 = sha256;
        _originalByteLength = byteLength;
        return this;
    }

    /// <summary>Aggiunge un blocco; l'ordine di aggiunta è l'ordine di lettura. Il locator non inventa pagine: <paramref name="page"/> può essere null.</summary>
    public ParseManifestBuilder Block(string reference, string type, string text, int? page = 1, string[]? headingPath = null, string? parentRef = null)
    {
        _blocks.Add(new JsonObject
        {
            ["ref"] = reference,
            ["order"] = _blocks.Count,
            ["parentRef"] = parentRef,
            ["type"] = type,
            ["text"] = text,
            ["headingPath"] = new JsonArray([.. (headingPath ?? []).Select(h => (JsonNode)JsonValue.Create(h)!)]),
            ["locator"] = new JsonObject { ["page"] = page, ["bbox"] = null, ["coordinateSystem"] = null },
        });
        return this;
    }

    /// <summary>Un chunk con un solo range di proprietà; offset in codepoint Unicode come dichiara il contratto.</summary>
    public ParseManifestBuilder Chunk(string blockRef, int start, int end, string ownership = "Primary")
    {
        var block = _blocks.Single(b => b["ref"]!.GetValue<string>() == blockRef);
        _chunks.Add(new JsonObject
        {
            ["offsetConvention"] = "unicode-codepoint",
            ["ranges"] = new JsonArray(new JsonObject
            {
                ["rangeKey"] = $"r{_chunks.Count + 1}",
                ["blockRef"] = blockRef,
                ["start"] = start,
                ["end"] = end,
                ["ownership"] = ownership,
                ["sourceTextHash"] = DocumentTestFixtures.Sha256(block["text"]!.GetValue<string>()),
            }),
        });
        return this;
    }

    public ParseManifestBuilder Issue(string code, bool blocking, bool affectsCompleteness, string reason = "elemento non trattato")
    {
        _issues.Add(new JsonObject
        {
            ["code"] = code,
            ["severity"] = blocking ? "blocking" : "warning",
            ["reason"] = reason,
            ["affectsCompleteness"] = affectsCompleteness,
        });
        return this;
    }

    public JsonObject Build()
    {
        // Inventario: ogni blocco è rappresentato; un'issue che incide sulla completezza rende il manifest Incomplete.
        var inventory = _inventory.Count > 0
            ? _inventory
            : [.. _blocks.Select(b => new JsonObject { ["item"] = b["ref"]!.GetValue<string>(), ["state"] = "represented", ["blockRefs"] = new JsonArray(b["ref"]!.GetValue<string>()), ["issueRefs"] = new JsonArray() })];
        var incomplete = _issues.Any(i => i["affectsCompleteness"]!.GetValue<bool>());
        return new JsonObject
        {
            ["schemaVersion"] = DocumentTestFixtures.ParseSchemaVersion,
            ["originalSha256"] = _originalSha256,
            ["parserVersion"] = "docling-test",
            ["dependencyLockHash"] = new string('b', 64),
            ["modelArtifactsHash"] = new string('c', 64),
            ["configHash"] = new string('d', 64),
            ["status"] = incomplete ? "Incomplete" : "Complete",
            ["originFormat"] = _format,
            ["originalByteLength"] = _originalByteLength,
            ["pageCount"] = _pageCount,
            ["document"] = new JsonObject { ["serialization"] = "test-1" },
            ["blocks"] = new JsonArray([.. _blocks.Select(b => (JsonNode)b.DeepClone())]),
            ["inventory"] = new JsonArray([.. inventory.Select(i => (JsonNode)i.DeepClone())]),
            ["chunks"] = new JsonArray([.. _chunks.Select(c => (JsonNode)c.DeepClone())]),
            ["issues"] = new JsonArray([.. _issues.Select(i => (JsonNode)i.DeepClone())]),
        };
    }

    public string BuildJson() => Build().ToJsonString();
}

/// <summary>Costruisce l'output di unità <c>evidence-1.1.0</c>: tutte le proprietà obbligatorie, enum chiusi, fonti DocumentPassage.</summary>
public sealed class EvidenceOutputBuilder
{
    private readonly List<JsonObject> _evidences = [];
    private string? _noEvidenceReason;

    public EvidenceOutputBuilder Evidence(string key, string rangeKey, string quote, string statement, string kind = "Requirement", string polarity = "Affirmed", string certainty = "Declared")
    {
        _evidences.Add(new JsonObject
        {
            ["key"] = key,
            ["sources"] = new JsonArray(new JsonObject
            {
                ["kind"] = "DocumentPassage",
                ["rangeKey"] = rangeKey,
                ["quote"] = quote,
                ["occurrence"] = 1,
                ["role"] = "Primary",
                ["citationGroupKey"] = $"g-{key}",
                ["part"] = 0,
            }),
            ["kind"] = kind,
            ["kindRationale"] = null,
            ["statement"] = statement,
            ["subject"] = null,
            ["action"] = null,
            ["object"] = null,
            ["scope"] = null,
            ["purpose"] = null,
            ["instrument"] = null,
            ["channel"] = null,
            ["recipient"] = null,
            ["qualifiers"] = new JsonArray(),
            ["polarity"] = polarity,
            ["certainty"] = certainty,
            ["relations"] = new JsonArray(),
        });
        return this;
    }

    /// <summary>Zero evidenze richiede una motivazione non vuota (contratto §2.1).</summary>
    public EvidenceOutputBuilder NoEvidence(string reason)
    {
        _noEvidenceReason = reason;
        return this;
    }

    public JsonObject Build() => new()
    {
        ["schemaVersion"] = DocumentTestFixtures.EvidenceSchemaVersion,
        ["evidences"] = new JsonArray([.. _evidences.Select(e => (JsonNode)e.DeepClone())]),
        ["noEvidenceReason"] = _evidences.Count == 0 ? _noEvidenceReason : null,
    };

    public string BuildJson() => Build().ToJsonString();
}

/// <summary>Clock controllabile: il tempo avanza solo quando lo decide il test.</summary>
public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// IChatClient finto che conta gli invii (anche quelli in streaming) e risponde con le risposte in coda, poi con l'ultima.
/// Dimostra che un percorso non invia al modello (<see cref="SendCount"/> resta 0) o quante volte lo fa.
/// </summary>
public sealed class CountingChatClient(params string[] replies) : IChatClient
{
    private int _sendCount;

    public int SendCount => Volatile.Read(ref _sendCount);

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    private string NextReply()
    {
        var index = Interlocked.Increment(ref _sendCount) - 1;
        return replies.Length == 0 ? string.Empty : replies[Math.Min(index, replies.Length - 1)];
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add([.. messages]);
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, NextReply())));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add([.. messages]);
        }

        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, NextReply());
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
