using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>
/// T029/T031 (livello PARSER): i manifest del parser reale (Docling in container, nessun DGX) per i quattro formati devono superare il
/// validatore del backend senza adattamenti, e il segreto passa nell'header X-Internal-Secret. Skipped, mai superato, senza
/// BPROJECTS_TEST_PARSER_ENDPOINT / BPROJECTS_TEST_PARSER_SECRET.
/// </summary>
[Trait("Category", DocumentTestCategories.Parser)]
[Collection(RealParser.CollectionName)]
public sealed class ParsedManifestRealParserTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, ManifestStatus> Fixtures => new()
    {
        { "pdf-text.pdf", ManifestStatus.Complete },
        { "pdf-table-multipage.pdf", ManifestStatus.Complete },
        { "pdf-mixed.pdf", ManifestStatus.Incomplete },
        { "docx-basic.docx", ManifestStatus.Complete },
        { "docx-image.docx", ManifestStatus.Incomplete },
        { "md-basic.md", ManifestStatus.Complete },
        { "md-non-bmp.md", ManifestStatus.Complete },
        { "txt-long.txt", ManifestStatus.Complete },
        { "txt-utf16.txt", ManifestStatus.Complete },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task The_real_parser_manifest_is_accepted_by_the_backend_validator(string fixture, ManifestStatus expected)
    {
        var client = RealParser.Client();
        var bytes = RealParser.Fixture(fixture);
        var format = RealParser.FormatOf(fixture);
        var sha = RealParser.Sha256(bytes);

        var response = await client.ConvertAsync(
            new ParserRequest(new MemoryStream(bytes), format, Guid.NewGuid(), sha, bytes.Length, RealParser.Limits), TimeSpan.FromMinutes(5), Ct);
        var manifest = ParsedManifestValidator.Validate(response.Body, new(sha, bytes.Length, format, response.ConfigHash, 1_000, 100_000, 250_000_000));

        Assert.Equal(expected, manifest.Status);
        Assert.NotEmpty(manifest.Blocks);
        Assert.NotEmpty(manifest.Chunks);
        Assert.Equal(format == DocumentFormat.Pdf, manifest.PageCount is not null);
        if (format is DocumentFormat.Markdown or DocumentFormat.Text)
        {
            Assert.All(manifest.Blocks, b => Assert.True(b.LineStart >= 1 && b.Page is null));
        }

        if (format == DocumentFormat.Pdf)
        {
            Assert.Contains(manifest.Blocks, b => b.Page is not null && b.Bbox is { Length: 4 } && b.CoordinateSystem is not null);
        }

        if (expected == ManifestStatus.Incomplete)
        {
            Assert.Contains(manifest.Issues, i => i.AffectsCompleteness);
        }
    }

    [Fact]
    public async Task The_real_parser_rejects_a_wrong_secret_as_unauthorized()
    {
        var options = RealParser.Options();
        var client = RealParser.Client(new ParserOptions { BaseUrl = options.BaseUrl, Secret = options.Secret + "x" });
        var bytes = RealParser.Fixture("txt-long.txt");

        var ex = await Assert.ThrowsAsync<ParserException>(() => client.ConvertAsync(
            new ParserRequest(new MemoryStream(bytes), DocumentFormat.Text, Guid.NewGuid(), RealParser.Sha256(bytes), bytes.Length, RealParser.Limits), TimeSpan.FromMinutes(1), Ct));

        Assert.Equal("parser_unauthorized", ex.Failure.Code);
        Assert.False(ex.Failure.IsTransient);
    }
}
