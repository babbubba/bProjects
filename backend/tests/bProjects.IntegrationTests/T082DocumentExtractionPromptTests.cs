using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 T082 (dopo il 4° FAIL reale) — parte DETERMINISTICA: l'estrazione documentale usa <c>document-extraction-v3</c> (divieto di
/// estrarre riempitivo narrativo come <c>Other</c>), mentre <c>document-extraction-v2</c> resta invariato byte per byte.
/// NON dimostra che il DGX risponda correttamente: quello lo accerta solo la prova reale.
/// </summary>
public sealed class T082DocumentExtractionPromptTests
{
    private const string V2Sha = "551a3abf8a9bdea5e65fb48dc5ee966887c07851b3c9531b562f970e5d95feea";

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Embedded(string file)
    {
        using var stream = typeof(DocumentEvidenceAgent).Assembly.GetManifestResourceStream("Prompts." + file)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Document_extraction_v2_is_preserved_byte_for_byte_and_embedded()
    {
        using var stream = typeof(DocumentEvidenceAgent).Assembly.GetManifestResourceStream("Prompts.document-extraction-v2.md")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(V2Sha, Sha(memory.ToArray()));
    }

    [Fact]
    public void The_document_extraction_agent_uses_v3_and_v3_differs_from_v2()
    {
        Assert.Equal("document-extraction-v3", DocumentEvidenceAgent.PromptVersion);
        Assert.Equal(Embedded("document-extraction-v3.md").Trim(), DocumentEvidenceAgent.Instructions);
        Assert.NotEqual(Embedded("document-extraction-v2.md").Trim(), DocumentEvidenceAgent.Instructions);
    }

    [Fact]
    public void V3_forbids_filler_as_other_and_keeps_the_v2_schema_rules()
    {
        var v3 = DocumentEvidenceAgent.Instructions;
        Assert.Contains("Contenuto non pertinente", v3);
        Assert.Contains("nemmeno come `Other`", v3);
        Assert.Contains("Non usare mai `Other` per contenuto non pertinente", v3);
        Assert.Contains("`kindRationale`", v3);
        // regole portanti di v2 ancora presenti (citazione esatta, nessuna invenzione, negazioni)
        Assert.Contains("copiandolo ESATTAMENTE", v3);
        Assert.Contains("Mai inventare", v3);
        Assert.Contains("Le negazioni valgono solo nel loro ambito", v3);
    }

    [Theory]
    [InlineData(null, "document-extraction-v2")]
    [InlineData("", "document-extraction-v2")]
    [InlineData("{}", "document-extraction-v2")]
    [InlineData("""{"modelId":"m","MaxAiCallsPerPreparation":40}""", "document-extraction-v2")]
    [InlineData("""{"promptVersion":"document-extraction-v2"}""", "document-extraction-v2")]
    [InlineData("""{"promptVersion":"document-extraction-v3"}""", "document-extraction-v3")]
    public void A_job_keeps_the_prompt_it_was_frozen_with_and_only_an_absent_key_means_historic_v2(string? config, string expected) =>
        Assert.Equal(expected, DocumentEvidenceAgent.EffectivePromptVersion(config));

    [Theory]
    [InlineData("""{"promptVersion":"document-extraction-v9"}""")]
    [InlineData("""{"promptVersion":"DOCUMENT-EXTRACTION-V3"}""")]
    [InlineData("""{"promptVersion":""}""")]
    [InlineData("""{"promptVersion":null}""")]
    [InlineData("""{"promptVersion":3}""")]
    [InlineData("""{"promptVersion":["document-extraction-v3"]}""")]
    [InlineData("non è json")]
    [InlineData("""{"promptVersion":""")]
    [InlineData("[1]")]
    [InlineData("\"document-extraction-v3\"")]
    public void An_explicit_unknown_or_malformed_version_is_a_permanent_compatibility_error_never_an_implicit_v2(string config)
    {
        var failure = Assert.Throws<DocumentWorkException>(() => DocumentEvidenceAgent.EffectivePromptVersion(config));
        Assert.Equal(DocumentWorkException.CheckpointIncompatible, failure.Code);
    }

    [Fact]
    public void The_historic_instructions_are_the_v2_file_and_the_current_ones_are_v3()
    {
        Assert.Equal(Embedded("document-extraction-v2.md").Trim(), DocumentEvidenceAgent.InstructionsFor(DocumentEvidenceAgent.PromptVersionV2));
        Assert.Equal(Embedded("document-extraction-v3.md").Trim(), DocumentEvidenceAgent.InstructionsFor(DocumentEvidenceAgent.PromptVersion));
        Assert.NotEqual(DocumentEvidenceAgent.InstructionsFor(DocumentEvidenceAgent.PromptVersionV2), DocumentEvidenceAgent.InstructionsFor(DocumentEvidenceAgent.PromptVersion));
    }
}
