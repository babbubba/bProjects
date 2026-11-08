using System.Net;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Db = bProjects.IntegrationTests.DocumentEvidenceMigrationTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T112 (REGRESSION) — riuso e confini della 002 verso la 001: definizioni, prompt, schema e checker storici congelati e distinti da quelli
/// documentali; nessun secondo engine, ledger, RAG, embedding, KB o stima AI; i GET di un database 001 migrato non avviano né creano lavoro
/// (nessuna analisi, nessun documento, nessuna chiamata AI). L'esecuzione dei vecchi checkpoint, la 1.0 edit/conferma/generazione e il checker
/// coverage-v1 sono provati dalle suite storiche elencate in validation/regressions.md. Nessun LLM, nessun DGX.
/// </summary>
public sealed class DocumentLegacyRegressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bProjects.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Radice backend non trovata.");
    }

    [Fact]
    public void The_001_definitions_prompts_schema_and_checker_stay_frozen_and_distinct_from_the_document_ones()
    {
        Assert.Equal("project-analysis-v3", ProcessingRun.AnalysisWorkflowVersion);
        Assert.Equal("project-document-v1", ProcessingRun.DocumentWorkflowVersion);
        Assert.Equal("conversation-v2", ConversationalAgent.PromptVersion);
        Assert.Equal("analysis-v4", ConversationalAgent.ConsolidationPromptVersion);
        Assert.Equal("document-v2", DocumentAgent.PromptVersion);
        Assert.Equal("1.0.0", AnalysisContent.CurrentSchemaVersion);
        Assert.Equal("coverage-v1", EvidenceCoverage.CheckerVersion);

        // Le definizioni documentali sono altre stringhe: un run 001 non è mai instradato al workflow documentale e viceversa.
        string[] documentary =
        [
            DocumentAnalysisWorkflow.DefinitionVersion, DocumentAnalysisWorkflow.DefinitionVersionV3, DocumentAnalysisWorkflow.DefinitionVersionV4,
            DocumentAnalysisWorkflow.DefinitionVersionV5, DocumentSourceDraftWorkflow.DefinitionVersion, DocumentSourceDraftWorkflow.DefinitionVersionV3,
        ];
        Assert.DoesNotContain(ProcessingRun.AnalysisWorkflowVersion, documentary);
        Assert.DoesNotContain(ProcessingRun.DocumentWorkflowVersion, documentary);
        Assert.Equal(documentary.Length, documentary.Distinct().Count());
    }

    [Fact]
    public void No_second_engine_ledger_rag_embedding_knowledge_base_or_ai_estimate_is_introduced()
    {
        var root = BackendRoot();

        // Un solo engine di workflow (Microsoft Agent Framework 1.26.0) e nessun pacchetto di embedding, vettori, KB o orchestrazione concorrente.
        var references = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Where(l => l.Contains("PackageReference", StringComparison.Ordinal)))
            .ToList();
        Assert.NotEmpty(references);
        string[] forbiddenPackages = ["Embedding", "Vector", "pgvector", "Qdrant", "Pinecone", "Weaviate", "Chroma", "Lucene", "Elastic", "Semantic", "LangChain", "Elsa", "Hangfire", "Quartz", "Temporal", "MassTransit", "MediatR", "KnowledgeBase"];
        Assert.DoesNotContain(references, r => forbiddenPackages.Any(p => r.Contains(p, StringComparison.OrdinalIgnoreCase)));
        var maf = references.Where(r => r.Contains("Microsoft.Agents.AI", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, maf.Count);
        Assert.All(maf, r => Assert.Contains("[1.26.0]", r, StringComparison.Ordinal));

        // Un solo registro delle evidenze: nessuna tabella o proprietà per embedding, vettori, KB, chunk indicizzati o stime AI.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options;
        using var db = new AppDbContext(options);
        var tables = db.Model.GetEntityTypes().Select(t => t.GetTableName()).OfType<string>().ToList();
        var properties = db.Model.GetEntityTypes().SelectMany(t => t.GetProperties().Select(p => $"{t.GetTableName()}.{p.Name}")).ToList();
        static bool Forbidden(string name) => System.Text.RegularExpressions.Regex.Matches(name, "[A-Z]+(?![a-z])|[A-Z]?[a-z0-9]+")
            .Select(m => m.Value.ToLowerInvariant())
            .Any(w => w is "rag" or "kb" || w.StartsWith("embed", StringComparison.Ordinal) || w.StartsWith("vector", StringComparison.Ordinal)
                || w.StartsWith("knowledge", StringComparison.Ordinal) || w.StartsWith("chunk", StringComparison.Ordinal) || w.StartsWith("estimat", StringComparison.Ordinal));
        Assert.DoesNotContain(tables, Forbidden);   // per parola PascalCase: «Coverage» non è «Rag»
        Assert.DoesNotContain(properties, Forbidden);
        Assert.Single(tables, t => t == "Evidences");
        Assert.DoesNotContain(tables, t => t.Contains("Ledger", StringComparison.OrdinalIgnoreCase));
        Assert.Single(tables, t => t == "WorkflowCheckpoints");   // un solo store di checkpoint, esteso e non duplicato
        Assert.Single(tables, t => t == "InferenceCalls");        // misure della telemetria esistente, nessuna stima parallela
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task Reading_a_migrated_001_database_creates_no_analysis_document_or_ai_work()
    {
        await using var database = await Db.TempDatabase.CreateAsync(migrate: true);
        var ids = Db.Legacy.New();
        await Db.SeedLegacyAsync(database, ids);
        string[] workTables =
        [
            "ProcessingRuns", "ChatTurns", "InferenceCalls", "ExecutionIntervals", "WorkflowCheckpoints", "AnalysisRevisions", "Drafts", "EvidenceExtractionJobs",
            "EvidenceExtractionVersions", "Evidences", "EvidenceCoverageReports", "DocumentJobs", "DocumentPreparations", "ProjectDocuments", "DocumentVersions",
            "SourceSnapshots", "ClassificationRecords", "RegimeTransitions", "WorkManifests", "WorkOutputs", "DerivedBatches", "AnalysisEvidenceSelections",
        ];
        var before = new Dictionary<string, string>();
        foreach (var table in workTables)
        {
            before[table] = await database.SnapshotAsync(table, await database.ColumnsAsync(table));
        }

        // Il client AI dell'harness lancia se costruito: ogni GET che lo toccasse fallirebbe.
        await using (var harness = await ApiHarness.StartAsync(database.ConnectionString))
        {
            var client = await harness.LoginAsync();
            var project = ids.Project;
            string[] core =
            [
                "/api/projects", $"/api/projects/{project}", $"/api/projects/{project}/messages",
                $"/api/projects/{project}/analyses/{ids.Run}", $"/api/projects/{project}/documents",
            ];
            // Non inclusi: context-usage (per progetto esistente sonda i metadati del modello tramite il monitor, risorsa che l'harness vieta) ed evidence-selection. La vista congelata della fixture T037 è un segnaposto non valido e non rappresenta una vista reale.
            string[] others =
            [
                $"/api/projects/{project}/draft", $"/api/projects/{project}/processing-runs/{ids.Run}", $"/api/projects/{project}/analyses/{ids.Run}/revisions/2",
                $"/api/projects/{project}/analyses/{ids.Run}/coverage-reports",
                $"/api/projects/{project}/evidence", $"/api/projects/{project}/evidence/status",
            ];
            foreach (var path in core)
            {
                var response = await client.GetAsync(path, Ct);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} -> {(int)response.StatusCode}");
            }

            foreach (var path in others)
            {
                var response = await client.GetAsync(path, Ct);
                Assert.True((int)response.StatusCode < 500, $"{path} -> {(int)response.StatusCode}");
            }

            // La revisione storica si legge come 1.0.0, mai riscritta o riletta come documentale.
            var analysis = await client.GetStringAsync($"/api/projects/{project}/analyses/{ids.Run}", Ct);
            Assert.Contains("1.0.0", analysis, StringComparison.Ordinal);
        }

        foreach (var table in workTables)
        {
            Assert.Equal(before[table], await database.SnapshotAsync(table, await database.ColumnsAsync(table)));   // nessuna riga creata o modificata
        }
    }
}
