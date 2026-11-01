using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace bProjects.IntegrationTests;

/// <summary>T001: discovery e riproducibilità dei builder 002 (nessuna rete, nessun segreto, nessun modello reale).</summary>
public sealed class DocumentTestFixturesTests
{
    private static ParseManifestBuilder SamplePdf() => new ParseManifestBuilder()
        .Format("pdf", pageCount: 2)
        .Original(DocumentTestFixtures.Sha256("originale"), 9)
        .Block("b1", "heading", "Requisiti", page: 1)
        .Block("b2", "paragraph", "Il portale supporta 500 utenti.", page: 1, headingPath: ["Requisiti"], parentRef: "b1")
        .Chunk("b2", 0, 31);

    [Fact]
    public void Parse_manifest_follows_document_parse_1_and_is_reproducible()
    {
        var manifest = SamplePdf().Build();

        Assert.Equal("document-parse-1", manifest["schemaVersion"]!.GetValue<string>());
        Assert.Equal("Complete", manifest["status"]!.GetValue<string>());
        foreach (var field in new[] { "originalSha256", "parserVersion", "dependencyLockHash", "modelArtifactsHash", "configHash", "originFormat", "originalByteLength", "pageCount", "document", "blocks", "inventory", "chunks", "issues" })
        {
            Assert.True(manifest.ContainsKey(field), field);
        }

        var inventory = manifest["inventory"]!.AsArray();
        Assert.Equal(2, inventory.Count);
        Assert.All(inventory, i => Assert.Equal("represented", i!["state"]!.GetValue<string>()));
        Assert.Equal("unicode-codepoint", manifest["chunks"]![0]!["offsetConvention"]!.GetValue<string>());
        Assert.Equal(SamplePdf().BuildJson(), SamplePdf().BuildJson());
    }

    [Fact]
    public void An_issue_affecting_completeness_makes_the_manifest_incomplete()
    {
        var manifest = SamplePdf().Issue("unprocessed_image_page", blocking: true, affectsCompleteness: true).Build();

        Assert.Equal("Incomplete", manifest["status"]!.GetValue<string>());
        Assert.Equal("blocking", manifest["issues"]![0]!["severity"]!.GetValue<string>());
    }

    [Fact]
    public void Evidence_output_has_every_required_property_and_zero_evidences_need_a_reason()
    {
        var output = new EvidenceOutputBuilder().Evidence("e1", "r1", "500 utenti", "Il portale supporta 500 utenti.").Build();

        Assert.Equal("evidence-1.1.0", output["schemaVersion"]!.GetValue<string>());
        Assert.Null(output["noEvidenceReason"]);
        var candidate = output["evidences"]![0]!.AsObject();
        foreach (var field in new[] { "key", "sources", "kind", "kindRationale", "statement", "subject", "action", "object", "scope", "purpose", "instrument", "channel", "recipient", "qualifiers", "polarity", "certainty", "relations" })
        {
            Assert.True(candidate.ContainsKey(field), field);
        }

        var source = candidate["sources"]![0]!;
        Assert.Equal("DocumentPassage", source["kind"]!.GetValue<string>());
        Assert.Equal("Primary", source["role"]!.GetValue<string>());

        var empty = new EvidenceOutputBuilder().NoEvidence("Nessun requisito nel testo.").Build();
        Assert.Empty(empty["evidences"]!.AsArray());
        Assert.Equal("Nessun requisito nel testo.", empty["noEvidenceReason"]!.GetValue<string>());
        Assert.Equal(new EvidenceOutputBuilder().Evidence("e1", "r1", "q", "s").BuildJson(), new EvidenceOutputBuilder().Evidence("e1", "r1", "q", "s").BuildJson());
    }

    [Fact]
    public async Task The_fake_chat_client_counts_every_send_and_replays_replies()
    {
        using var client = new CountingChatClient("uno", "due");
        Assert.Equal(0, client.SendCount);   // una prova che non invia al modello vede il contatore a zero

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "a")], cancellationToken: TestContext.Current.CancellationToken);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "b")], cancellationToken: TestContext.Current.CancellationToken);
        var streamed = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "c")], cancellationToken: TestContext.Current.CancellationToken))
        {
            streamed.Add(update);
        }

        Assert.Equal(3, client.SendCount);
        Assert.Equal("uno", first.Text);
        Assert.Equal("due", second.Text);
        Assert.Equal("due", string.Concat(streamed.Select(u => u.Text)));   // l'ultima risposta resta in uso
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public void The_manual_clock_moves_only_when_told()
    {
        var clock = new ManualTimeProvider();
        var start = clock.GetUtcNow();

        Assert.Equal(start, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(119_999));
        Assert.Equal(start.AddMilliseconds(119_999), clock.GetUtcNow());
    }

    [Fact]
    public void Stable_identifiers_and_hashes_do_not_vary_between_runs()
    {
        Assert.Equal(DocumentTestFixtures.StableGuid("doc-1"), DocumentTestFixtures.StableGuid("doc-1"));
        Assert.NotEqual(DocumentTestFixtures.StableGuid("doc-1"), DocumentTestFixtures.StableGuid("doc-2"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", DocumentTestFixtures.Sha256("abc"));
        Assert.IsType<JsonObject>(SamplePdf().Build());
    }
}
