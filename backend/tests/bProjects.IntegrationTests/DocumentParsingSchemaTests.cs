using bProjects.App.Documents;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T028 (PG): artefatti di parsing, blocchi, porzioni e segnalazioni su PostgreSQL reale. Vincoli strutturali (unique artefatto per preparazione,
/// ordinal/ref per artefatto, FK composte progetto/parent, CHECK di pagina/righe/risultato) e roundtrip del manifest normalizzato.
/// Nessun parser, nessun AI, nessuna sessione MAF.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentParsingSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ParsedArtifact Artifact(SeededParse seed, Guid? id = null, string? storageKey = null) => new()
    {
        Id = id ?? Guid.NewGuid(), ProjectId = seed.Project, PreparationId = seed.Preparation, JobId = seed.Job, FencingToken = 1,
        SchemaVersion = "document-parse-1", StorageKey = storageKey ?? DocumentFileStore.KeyFor(seed.Project, Guid.NewGuid()), Sha256 = new string('a', 64),
        ByteLength = 100, ParserConfigHash = "cfg", BlockCount = 2, IssueCount = 1,
    };

    private static DocumentBlock Block(Guid project, Guid artifact, int ordinal, string reference, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(), ProjectId = project, ArtifactId = artifact, Ordinal = ordinal, Ref = reference, ParentId = parent, Type = BlockType.Paragraph,
        CanonicalText = "Testo 😀", TextHash = new string('b', 64), HeadingPathJson = "[\"Titolo\"]", LineStart = 1, LineEnd = 2,
    };

    private static async Task<(ParsingDatabase Db, SeededParse Seed, Guid Artifact, Guid Block0, Guid Block1)> SeedWithArtifactAsync()
    {
        var database = await ParsingDatabase.CreateAsync();
        var seed = await database.SeedParseAsync();
        var artifact = Artifact(seed);
        var b0 = Block(seed.Project, artifact.Id, 0, "b0");
        var b1 = Block(seed.Project, artifact.Id, 1, "b1", parent: b0.Id);
        await using var db = database.NewContext();
        db.ParsedArtifacts.Add(artifact);
        await db.SaveChangesAsync(Ct);
        db.DocumentBlocks.AddRange(b0, b1);
        db.DocumentPortions.Add(Portion(seed.Project, seed.Preparation, 0));
        await db.SaveChangesAsync(Ct);
        return (database, seed, artifact.Id, b0.Id, b1.Id);
    }

    [Fact]
    public async Task The_parsing_schema_exists_after_the_explicit_migration()
    {
        await using var database = await ParsingDatabase.CreateAsync();

        foreach (var table in new[] { "ParsedArtifacts", "DocumentBlocks", "DocumentPortions", "PreparationIssues" })
        {
            Assert.Equal(0L, await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\""));
        }

        await using var db = database.NewContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Artifact_blocks_portions_and_issues_round_trip_with_manifest_fidelity()
    {
        var (database, seed, artifactId, b0, b1) = await SeedWithArtifactAsync();
        await using var _ = database;
        var table = "{\"rows\":1,\"columns\":2,\"cells\":[{\"row\":0,\"col\":0,\"rowSpan\":1,\"colSpan\":2,\"header\":true,\"text\":\"H\"}]}";
        await using (var write = database.NewContext())
        {
            var block = await write.DocumentBlocks.SingleAsync(b => b.Id == b1, Ct);
            block.Type = BlockType.Table;
            block.TableCellsJson = table;
            block.Page = 3;
            block.BboxJson = "[1.5,2.5,10,20]";
            block.CoordinateSystem = "pdf-bottom-left";
            block.DoclingRef = "#/tables/0";
            block.LineStart = null;
            block.LineEnd = null;
            write.DocumentPortions.Add(new DocumentPortion
            {
                Id = Guid.NewGuid(), ProjectId = seed.Project, PreparationId = seed.Preparation, Ordinal = 1,
                RangesJson = "[{\"rangeKey\":\"r0\",\"blockId\":\"" + b0 + "\",\"start\":0,\"end\":6}]", ContextRangesJson = "[]", TextHash = new string('c', 64),
                Status = PortionStatus.Pending,
            });
            write.PreparationIssues.Add(new PreparationIssue
            {
                Id = Guid.NewGuid(), ProjectId = seed.Project, PreparationId = seed.Preparation, BlockId = b1, Page = 3, Code = "image_unprocessed",
                Severity = IssueSeverity.Blocking, Reason = "Immagine non trattata", AffectsCompleteness = true,
            });
            await write.SaveChangesAsync(Ct);
        }

        await using var read = database.NewContext();
        var back = await read.DocumentBlocks.AsNoTracking().Where(b => b.ArtifactId == artifactId).OrderBy(b => b.Ordinal).ToListAsync(Ct);
        Assert.Equal(["b0", "b1"], back.Select(b => b.Ref));
        Assert.Equal(b0, back[1].ParentId);
        Assert.Equal("Testo 😀", back[0].CanonicalText);
        Assert.Equal(BlockType.Table, back[1].Type);
        Assert.Equal(3, back[1].Page);
        Assert.Equal("pdf-bottom-left", back[1].CoordinateSystem);
        using (var cells = System.Text.Json.JsonDocument.Parse(back[1].TableCellsJson!))
        {
            Assert.Equal(2, cells.RootElement.GetProperty("cells")[0].GetProperty("colSpan").GetInt32());
        }

        Assert.Equal(["Titolo"], System.Text.Json.JsonSerializer.Deserialize<string[]>(back[0].HeadingPathJson)!);
        var portion = await read.DocumentPortions.AsNoTracking().SingleAsync(p => p.Ordinal == 1, Ct);
        Assert.Equal(PortionStatus.Pending, portion.Status);
        Assert.Contains(b0.ToString(), portion.RangesJson, StringComparison.Ordinal);
        var issue = await read.PreparationIssues.AsNoTracking().SingleAsync(Ct);
        Assert.True(issue.AffectsCompleteness);
        Assert.Equal(IssueSeverity.Blocking, issue.Severity);
        var artifact = await read.ParsedArtifacts.AsNoTracking().SingleAsync(Ct);
        Assert.NotEqual(default, artifact.CreatedAt);   // timestamp UTC assegnato dal database
        Assert.Equal(seed.Job, artifact.JobId);
    }

    public static TheoryData<string> InvalidRows() =>
        [
            "second-artifact-for-preparation", "duplicate-storage-key", "artifact-with-job-of-other-preparation", "artifact-in-other-project",
            "duplicate-ordinal", "duplicate-ref", "negative-ordinal", "block-in-other-project", "parent-in-other-artifact", "parent-is-self",
            "page-zero", "lines-inverted", "line-start-without-end", "portion-duplicate-ordinal", "portion-completed-without-result",
            "portion-job-of-other-preparation", "portion-in-other-project", "issue-in-other-project", "issue-block-in-other-project", "issue-page-zero",
        ];

    [Theory]
    [MemberData(nameof(InvalidRows))]
    public async Task Structural_constraints_reject_invalid_rows(string caseName)
    {
        var (database, seed, artifactId, b0, _) = await SeedWithArtifactAsync();
        await using var _ = database;
        var other = await database.SeedParseAsync();
        var third = await database.SeedParseAsync(owner: (seed.User, seed.Project));   // stesso progetto, preparazione ancora senza artefatto
        await using var other2 = database.NewContext();
        var otherArtifact = Artifact(other);
        other2.ParsedArtifacts.Add(otherArtifact);
        await other2.SaveChangesAsync(Ct);
        var otherBlock = Block(other.Project, otherArtifact.Id, 0, "o0");
        other2.DocumentBlocks.Add(otherBlock);
        await other2.SaveChangesAsync(Ct);

        await using var db = database.NewContext();
        var (entity, state) = caseName switch
        {
            "second-artifact-for-preparation" => ((object)Artifact(seed), PostgresErrorCodes.UniqueViolation),
            "duplicate-storage-key" => (Artifact(third, storageKey: otherArtifact.StorageKey), PostgresErrorCodes.UniqueViolation),
            "artifact-with-job-of-other-preparation" => (With(Artifact(third), a => a.JobId = other.Job), PostgresErrorCodes.ForeignKeyViolation),
            "artifact-in-other-project" => (With(Artifact(third), a => a.ProjectId = other.Project), PostgresErrorCodes.ForeignKeyViolation),
            "duplicate-ordinal" => (Block(seed.Project, artifactId, 0, "nuovo"), PostgresErrorCodes.UniqueViolation),
            "duplicate-ref" => (Block(seed.Project, artifactId, 7, "b0"), PostgresErrorCodes.UniqueViolation),
            "negative-ordinal" => (Block(seed.Project, artifactId, -1, "neg"), PostgresErrorCodes.CheckViolation),
            "block-in-other-project" => (Block(other.Project, artifactId, 7, "xp"), PostgresErrorCodes.ForeignKeyViolation),
            "parent-in-other-artifact" => (Block(seed.Project, artifactId, 7, "xa", parent: otherBlock.Id), PostgresErrorCodes.ForeignKeyViolation),
            "parent-is-self" => (SelfParent(seed.Project, artifactId), PostgresErrorCodes.CheckViolation),
            "page-zero" => (With(Block(seed.Project, artifactId, 7, "p0"), b => b.Page = 0), PostgresErrorCodes.CheckViolation),
            "lines-inverted" => (With(Block(seed.Project, artifactId, 7, "li"), b => { b.LineStart = 5; b.LineEnd = 2; }), PostgresErrorCodes.CheckViolation),
            "line-start-without-end" => (With(Block(seed.Project, artifactId, 7, "ls"), b => { b.LineStart = 5; b.LineEnd = null; }), PostgresErrorCodes.CheckViolation),
            "portion-duplicate-ordinal" => (Portion(seed.Project, seed.Preparation, 0), PostgresErrorCodes.UniqueViolation),
            "portion-completed-without-result" => (With(Portion(seed.Project, seed.Preparation, 5), p => p.Status = PortionStatus.Completed), PostgresErrorCodes.CheckViolation),
            "portion-job-of-other-preparation" => (With(Portion(seed.Project, seed.Preparation, 5), p => p.JobId = other.Job), PostgresErrorCodes.ForeignKeyViolation),
            "portion-in-other-project" => (Portion(other.Project, seed.Preparation, 5), PostgresErrorCodes.ForeignKeyViolation),
            "issue-in-other-project" => (Issue(other.Project, seed.Preparation, null), PostgresErrorCodes.ForeignKeyViolation),
            "issue-block-in-other-project" => (Issue(seed.Project, seed.Preparation, otherBlock.Id), PostgresErrorCodes.ForeignKeyViolation),
            "issue-page-zero" => (With(Issue(seed.Project, seed.Preparation, b0), i => i.Page = 0), PostgresErrorCodes.CheckViolation),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        db.Add(entity);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal(state, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    private static T With<T>(T entity, Action<T> change)
    {
        change(entity);
        return entity;
    }

    private static DocumentBlock SelfParent(Guid project, Guid artifact)
    {
        var block = Block(project, artifact, 7, "self");
        block.ParentId = block.Id;
        return block;
    }

    private static DocumentPortion Portion(Guid project, Guid preparation, int ordinal) => new()
    {
        Id = Guid.NewGuid(), ProjectId = project, PreparationId = preparation, Ordinal = ordinal, RangesJson = "[]", ContextRangesJson = "[]",
        TextHash = new string('c', 64), Status = PortionStatus.Pending,
    };

    private static PreparationIssue Issue(Guid project, Guid preparation, Guid? block) => new()
    {
        Id = Guid.NewGuid(), ProjectId = project, PreparationId = preparation, BlockId = block, Code = "x", Severity = IssueSeverity.Warning, Reason = "r",
    };
}
