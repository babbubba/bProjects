using System.Net;
using System.Text.Json;
using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T030 (PG): GET preparazione, blocchi paginati e passaggi su API reale + PostgreSQL reale. Gli artefatti di parsing sono scritti con le
/// stesse entità del worker (T029); nessun parser, nessun modello. Parsed/Extracting non sono mai presentati come Ready.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentPassageApiTests : ArchiveTestBase
{
    private sealed record Seeded(Guid Project, Guid Document, Guid Preparation, Guid[] Blocks);

    private async Task<Seeded> SeedAsync(HttpClient client, PreparationStatus status, bool withArtifact = true, string fileName = "nota.md")
    {
        var project = await NewProject(client);
        var created = await Json(await Upload(client, project, Text("Contenuto"), fileName, "text/markdown"));
        var document = created.GetProperty("documentId").GetGuid();
        var versionId = created.GetProperty("id").GetGuid();
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preparation = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == versionId, Ct);
        var blocks = new List<Guid>();
        if (withArtifact)
        {
            var artifact = new ParsedArtifact
            {
                Id = Guid.NewGuid(), ProjectId = project, PreparationId = preparation.Id, JobId = preparation.ParseJobId!.Value, FencingToken = 1,
                SchemaVersion = "parsed-1.0.0", StorageKey = "k/" + Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64), ByteLength = 10,
                ParserConfigHash = new string('b', 64), BlockCount = 5, IssueCount = 1,
            };
            db.ParsedArtifacts.Add(artifact);
            await db.SaveChangesAsync(Ct);
            // Blocchi in ordine di lettura su due pagine; il terzo ha bbox e righe, gli altri no (null, mai inventati). Il quarto ha un carattere non-BMP.
            (string Type, string Text, int? Page, string? Bbox, int? Lines)[] rows =
            [
                ("Heading", "Titolo", 1, "[0,0,10,10]", null),
                ("Paragraph", "Primo paragrafo.", 1, null, null),
                ("Paragraph", "Testo con bbox.", 2, "[1.5,2,30,40]", 7),
                ("Paragraph", "Emoji 😀 qui.", null, null, null),
                ("Paragraph", "<script>alert(1)</script> & testo", 3, null, null),
            ];
            for (var i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                var block = new DocumentBlock
                {
                    Id = Guid.NewGuid(), ProjectId = project, ArtifactId = artifact.Id, Ordinal = i, Ref = $"b{i}", Type = Enum.Parse<BlockType>(r.Type),
                    CanonicalText = r.Text, TextHash = new string('c', 64), HeadingPathJson = "[\"Titolo\"]", Page = r.Page, BboxJson = r.Bbox,
                    CoordinateSystem = r.Bbox is null ? null : "pdf-points", LineStart = r.Lines, LineEnd = r.Lines,
                };
                blocks.Add(block.Id);
                db.DocumentBlocks.Add(block);
            }

            db.PreparationIssues.Add(new PreparationIssue
            {
                Id = Guid.NewGuid(), ProjectId = project, PreparationId = preparation.Id, Code = "ocr_needed", Severity = IssueSeverity.Warning,
                Page = 2, Reason = "Pagina 2 senza testo", AffectsCompleteness = true,
            });
        }

        preparation.Status = status;
        await db.SaveChangesAsync(Ct);
        return new(project, document, preparation.Id, [.. blocks]);
    }

    private static string Base(Seeded s) => $"/api/projects/{s.Project}/documents/{s.Document}/versions/1/preparations/1";

    [Theory]
    [InlineData(PreparationStatus.Parsing)]
    [InlineData(PreparationStatus.Extracting)]
    [InlineData(PreparationStatus.Incomplete)]
    public async Task Parsed_content_is_readable_but_never_presented_as_ready(PreparationStatus status)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, status);

        var prep = await Json(await client.GetAsync(Base(s), Ct));
        Assert.Equal(status.ToString(), prep.GetProperty("processingStatus").GetString());
        Assert.False(prep.GetProperty("isReady").GetBoolean());
        Assert.Equal("Parsed", prep.GetProperty("contentState").GetString());
        Assert.Equal(5, prep.GetProperty("blockCount").GetInt32());
        var issue = Assert.Single(prep.GetProperty("issues").EnumerateArray());
        Assert.Equal("ocr_needed", issue.GetProperty("code").GetString());

        var blocks = await Json(await client.GetAsync(Base(s) + "/blocks", Ct));
        Assert.False(blocks.GetProperty("isReady").GetBoolean());
        Assert.Equal(5, blocks.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Ready_is_reported_only_for_the_ready_status()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);
        // La FK dell'Evidence non è oggetto di questa prova: nel database isolato si toglie solo il vincolo Ready→Evidence.
        await Execute("ALTER TABLE \"DocumentPreparations\" DROP CONSTRAINT \"CK_DocumentPreparations_Ready_Evidence\"");
        await Execute("UPDATE \"DocumentPreparations\" SET \"Status\" = 'Ready' WHERE \"Id\" = @id", ("id", s.Preparation));

        var prep = await Json(await client.GetAsync(Base(s), Ct));
        Assert.True(prep.GetProperty("isReady").GetBoolean());
        Assert.Equal("Ready", prep.GetProperty("contentState").GetString());
    }

    [Fact]
    public async Task Blocks_are_paged_in_reading_order_with_native_metadata_or_null()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);

        var first = await Json(await client.GetAsync(Base(s) + "/blocks?limit=2", Ct));
        var second = await Json(await client.GetAsync(Base(s) + $"/blocks?limit=2&cursor={first.GetProperty("nextCursor").GetString()}", Ct));
        var third = await Json(await client.GetAsync(Base(s) + $"/blocks?limit=2&cursor={second.GetProperty("nextCursor").GetString()}", Ct));
        var all = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Concat(third.GetProperty("items").EnumerateArray()).ToList();

        Assert.Equal([0, 1, 2, 3, 4], all.Select(b => b.GetProperty("ordinal").GetInt32()));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("nextCursor").ValueKind);
        Assert.Equal("Titolo", all[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, all[1].GetProperty("bbox").ValueKind);   // nessuna pagina/bbox/righe inventate
        Assert.Equal(JsonValueKind.Null, all[1].GetProperty("lines").ValueKind);
        Assert.Equal(2, all[2].GetProperty("page").GetInt32());
        Assert.Equal(7, all[2].GetProperty("lines").GetProperty("start").GetInt32());
        Assert.Equal("pdf-points", all[2].GetProperty("bbox").GetProperty("coordinateSystem").GetString());
        Assert.Equal(1.5, all[2].GetProperty("bbox").GetProperty("values")[0].GetDouble());
        Assert.Equal(JsonValueKind.Null, all[3].GetProperty("page").ValueKind);
        Assert.Equal("<script>alert(1)</script> & testo", all[4].GetProperty("text").GetString());   // testo grezzo: l'escaping è del client
    }

    [Fact]
    public async Task The_preparation_is_addressable_by_generation_id_or_latest()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);
        var root = $"/api/projects/{s.Project}/documents/{s.Document}/versions/1/preparations/";
        foreach (var g in new[] { "1", s.Preparation.ToString(), "latest" })
        {
            var prep = await Json(await client.GetAsync(root + g, Ct));
            Assert.Equal(s.Preparation, prep.GetProperty("id").GetGuid());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "abc", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("cursor=zzz")]
    [InlineData("cursor=-1")]
    public async Task Invalid_paging_is_400(string query)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Base(s) + "/blocks?" + query, Ct)).StatusCode);
    }

    [Fact]
    public async Task Blocks_before_parsing_is_409_and_unknown_generation_or_other_project_is_404()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Parsing, withArtifact: false);

        var prep = await Json(await client.GetAsync(Base(s), Ct));
        Assert.Equal("NotParsed", prep.GetProperty("contentState").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(Base(s) + "/blocks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{s.Project}/documents/{s.Document}/versions/1/preparations/9", Ct)).StatusCode);
        var other = await NewProject(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{other}/documents/{s.Document}/versions/1/preparations/1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.Anonymous().GetAsync(Base(s), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_passage_highlights_the_exact_quote_and_links_the_original()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);

        var whole = await Json(await client.GetAsync(Base(s) + $"/passages/{s.Blocks[1]}", Ct));
        Assert.Equal(JsonValueKind.Null, whole.GetProperty("quote").ValueKind);
        Assert.Equal("Primo paragrafo.", whole.GetProperty("block").GetProperty("text").GetString());

        var passage = await Json(await client.GetAsync(Base(s) + $"/passages/{s.Blocks[1]}?start=6&end=15", Ct));
        var quote = passage.GetProperty("quote");
        Assert.Equal("Primo ", quote.GetProperty("before").GetString());
        Assert.Equal("paragrafo", quote.GetProperty("text").GetString());
        Assert.Equal(".", quote.GetProperty("after").GetString());
        Assert.False(passage.GetProperty("isReady").GetBoolean());
        var link = passage.GetProperty("original");
        Assert.Equal($"/api/projects/{s.Project}/documents/{s.Document}/versions/1/download", link.GetProperty("downloadUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(link.GetProperty("downloadUrl").GetString(), Ct)).StatusCode);
        Assert.DoesNotContain("storage", passage.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("start=5")]
    [InlineData("end=5")]
    [InlineData("start=-1&end=3")]
    [InlineData("start=4&end=4")]
    [InlineData("start=5&end=3")]
    [InlineData("start=0&end=999")]
    [InlineData("start=7&end=9")]    // metà di una coppia surrogata (blocco "Emoji 😀 qui.", indice 6-7)
    public async Task A_malformed_range_is_422(string query)
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);
        var block = query == "start=7&end=9" ? s.Blocks[3] : s.Blocks[1];
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync(Base(s) + $"/passages/{block}?{query}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_non_bmp_quote_on_valid_boundaries_is_returned_whole_and_unknown_blocks_are_404()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);

        var passage = await Json(await client.GetAsync(Base(s) + $"/passages/{s.Blocks[3]}?start=6&end=8", Ct));
        Assert.Equal("😀", passage.GetProperty("quote").GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Base(s) + $"/passages/{Guid.NewGuid()}", Ct)).StatusCode);

        var other = await SeedAsync(client, PreparationStatus.Extracting);   // blocco di un altro progetto/preparazione: mai raggiungibile da qui
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Base(s) + $"/passages/{other.Blocks[0]}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Reads_never_start_work_or_change_the_preparation()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, PreparationStatus.Extracting);
        var before = await Count("SELECT count(*) FROM \"DocumentJobs\"");
        await client.GetAsync(Base(s), Ct);
        await client.GetAsync(Base(s) + "/blocks", Ct);
        await client.GetAsync(Base(s) + $"/passages/{s.Blocks[0]}", Ct);
        Assert.Equal(before, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Equal("Extracting", await Scalar<string>("SELECT \"Status\" FROM \"DocumentPreparations\" WHERE \"Id\" = @id", ("id", s.Preparation)));
    }
}
