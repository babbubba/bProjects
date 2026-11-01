namespace bProjects.IntegrationTests;

/// <summary>T003: configurazione dei livelli 002 e comportamento dei prerequisiti mancanti (SKIP motivato, mai PASS).</summary>
public sealed class DocumentTestEnvironmentTests
{
    [Fact]
    public void Levels_without_external_services_have_no_prerequisite()
    {
        Assert.Null(DocumentTestEnvironment.MissingPrerequisite(DocumentTestLevel.Det));
        Assert.Null(DocumentTestEnvironment.MissingPrerequisite(DocumentTestLevel.MafFake));
    }

    [Fact]
    public void Service_levels_report_the_missing_variable_by_name()
    {
        AssertReason(DocumentTestLevel.Pg, TestEnvironment.PostgresAdminConnectionString, TestEnvironment.PostgresVariable);
        AssertReason(DocumentTestLevel.MafPg, TestEnvironment.PostgresAdminConnectionString, TestEnvironment.PostgresVariable);
        AssertReason(DocumentTestLevel.Parser, DocumentTestEnvironment.ParserEndpoint, DocumentTestEnvironment.ParserEndpointVariable);
        AssertReason(DocumentTestLevel.Dgx, TestEnvironment.DgxEndpoint, TestEnvironment.DgxEndpointVariable);
        AssertReason(DocumentTestLevel.Browser, DocumentTestEnvironment.BrowserBaseUrl, DocumentTestEnvironment.BrowserBaseUrlVariable);
    }

    [Fact]
    public void The_document_corpus_is_copied_next_to_the_tests()
    {
        Assert.True(File.Exists(Path.Combine(DocumentTestEnvironment.FixturesDirectory, "manifest.json")));
    }

    /// <summary>Senza BPROJECTS_TEST_PARSER_ENDPOINT il test risulta Skipped con il motivo: il livello PARSER non è dichiarato verificato.</summary>
    [Fact]
    [Trait("Category", DocumentTestCategories.Parser)]
    public void Parser_level_is_skipped_without_its_endpoint()
    {
        var endpoint = DocumentTestEnvironment.RequireParser();

        Assert.True(endpoint.IsAbsoluteUri);
    }

    private static void AssertReason(DocumentTestLevel level, string? configured, string variable)
    {
        var reason = DocumentTestEnvironment.MissingPrerequisite(level);
        if (configured is null)
        {
            Assert.Contains(variable, reason);
        }
        else
        {
            Assert.Null(reason);
        }
    }
}
