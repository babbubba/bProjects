using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T052 (PG) — envelope Evidence 1.0/1.1 su API e PostgreSQL reali, senza parser né modello: liste miste, dettaglio con sourceDocuments e
/// locator, gruppi di citazione condivisi, filtri documentali, confronto solo con mapping verificabile, formato legacy invariato per
/// l'origine Message (406 per la documentale, 400 per formato ignoto). Le righe documentali sono scritte con le stesse entità
/// dell'aggregatore (T051); le letture non creano lavori.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentEvidenceEndpointsTests : ArchiveTestBase
{
    private const string Hash1 = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222222222222222222222222222";

    private sealed record Seed(
        Guid Project, Guid Document, Guid DocumentVersion, Guid Prep1, Guid Version1, Guid Evidence1, Guid Evidence2, Guid Prep2, Guid Version2, Guid[] Blocks,
        Guid SharedSource, Guid Group);

    private static readonly string[] Texts = ["Titolo del capitolo", "Il cliente A puo ordinare al massimo 5 ordini al giorno.", "Eccezione: nessun limite per il cliente B."];

    private static PersistedSource Source(Seed ids, int block, int start, int end, Guid group, int part, Guid? sourceId = null) => new()
    {
        SourceId = sourceId ?? Guid.NewGuid(), Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = group, Part = part,
        MessageId = null, Sequence = null, DocumentId = ids.Document, DocumentVersionId = ids.DocumentVersion, PreparationId = ids.Prep1, ArtifactId = Guid.Empty,
        BlockId = ids.Blocks[block], Start = start, End = end, Quote = Texts[block][start..end],
    };

    private static string Content(string subject, params PersistedSource[] sources) => DocumentEvidenceCodec.EncodeContent(new DocumentEvidenceContent
    {
        SchemaVersion = "evidence-1.1.0", KindRationale = null,
        Structure = new DocumentEvidenceStructure { Subject = subject, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
        Qualifiers = [new DocumentEvidenceQualifier { Type = QualifierType.Quantity, Text = "5" }],
        Sources = sources,
    });

    private async Task<Seed> SeedAsync(HttpClient client, string manifest2 = Hash1)
    {
        var project = await NewProject(client);
        var created = await Json(await Upload(client, project, Text("Contenuto"), "regole.md", "text/markdown"));
        var document = created.GetProperty("documentId").GetGuid();
        var documentVersion = created.GetProperty("id").GetGuid();
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prep1 = await db.DocumentPreparations.SingleAsync(p => p.DocumentVersionId == documentVersion, Ct);
        var artifact = new ParsedArtifact
        {
            Id = Guid.NewGuid(), ProjectId = project, PreparationId = prep1.Id, JobId = prep1.ParseJobId!.Value, FencingToken = 1, SchemaVersion = "parsed-1.0.0",
            StorageKey = "k/" + Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64), ByteLength = 10, ParserConfigHash = new string('b', 64), BlockCount = 3, IssueCount = 0,
        };
        db.ParsedArtifacts.Add(artifact);
        await db.SaveChangesAsync(Ct);
        var blocks = new List<Guid>();
        (int? Page, string? Bbox, int? Lines)[] meta = [(1, null, null), (2, "[1.5,2,30,40]", 7), (null, null, null)];
        for (var i = 0; i < Texts.Length; i++)
        {
            var block = new DocumentBlock
            {
                Id = Guid.NewGuid(), ProjectId = project, ArtifactId = artifact.Id, Ordinal = i, Ref = $"b{i}", Type = i == 0 ? BlockType.Heading : BlockType.Paragraph,
                CanonicalText = Texts[i], TextHash = new string('c', 64), HeadingPathJson = "[\"Regole\"]", Page = meta[i].Page, BboxJson = meta[i].Bbox,
                CoordinateSystem = meta[i].Bbox is null ? null : "pdf-points", LineStart = meta[i].Lines, LineEnd = meta[i].Lines,
            };
            blocks.Add(block.Id);
            db.DocumentBlocks.Add(block);
        }

        var now = DateTimeOffset.UtcNow;
        var ids = new Seed(project, document, documentVersion, prep1.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [.. blocks], Guid.NewGuid(), Guid.NewGuid());
        EvidenceExtractionJob Job(Guid id, Guid preparation) => new()
        {
            Id = id, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation, Reason = ExtractionJobReason.Initial, Status = ExtractionJobStatus.Queued,
            RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
        };
        EvidenceExtractionVersion Version(Guid id, Guid preparation, Guid job, string manifest, int count) => new()
        {
            Id = id, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation, ManifestHash = manifest, Version = 1, JobId = job,
            SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = count, CreatedAt = now,
        };
        string WithArtifact(string content)
        {
            var decoded = DocumentEvidenceCodec.DecodeContent(content);
            return DocumentEvidenceCodec.EncodeContent(decoded with { Sources = [.. decoded.Sources.Select(x => x with { ArtifactId = artifact.Id })] });
        }

        // Bundle 1 (generazione 1): corrente. e1 una parte su b1; e2 citazione multi-blocco (b1 condiviso con e1, poi b2) nello stesso gruppo.
        var job1 = Guid.NewGuid();
        db.EvidenceExtractionJobs.Add(Job(job1, prep1.Id));
        db.EvidenceExtractionVersions.Add(Version(ids.Version1, prep1.Id, job1, Hash1, 2));
        await db.SaveChangesAsync(Ct);
        var shared = Source(ids, 1, 0, 25, ids.Group, 0, ids.SharedSource);
        var e2Group = Guid.NewGuid();
        db.Evidences.AddRange(
            Item(ids.Evidence1, ids.Version1, project, prep1.Id, 1, "1e1", "Il cliente A ha un tetto di 5 ordini.", EvidenceKind.Constraint, WithArtifact(Content("Cliente A", shared))),
            Item(Guid.NewGuid(), ids.Version1, project, prep1.Id, 2, "1e2", "Il cliente B non ha limiti.", EvidenceKind.Exclusion,
                WithArtifact(Content("Cliente B", Source(ids, 2, 11, 42, e2Group, 1), Source(ids, 1, 0, 25, e2Group, 0, ids.SharedSource)))));
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = documentVersion, ProjectId = project, CurrentPreparationId = prep1.Id, CurrentEvidenceVersionId = ids.Version1, Revision = 1, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        var first = await db.DocumentPreparations.SingleAsync(p => p.Id == prep1.Id, Ct);
        first.Status = PreparationStatus.Ready;
        first.CurrentEvidenceVersionId = ids.Version1;
        await db.SaveChangesAsync(Ct);

        // Bundle 2 (generazione 2): candidata completa, non corrente (le fonti restano sull'artefatto già letto).
        var prep2 = new DocumentPreparation
        {
            Id = ids.Prep2, ProjectId = project, DocumentVersionId = documentVersion, Generation = 2, Status = PreparationStatus.Extracting, PolicyHash = new string('e', 64),
            RequestedAt = now, HardDeadlineAt = now.AddHours(1), EffectiveDeadlineAt = now.AddHours(1),
        };
        db.DocumentPreparations.Add(prep2);
        await db.SaveChangesAsync(Ct);
        var job2 = Guid.NewGuid();
        db.EvidenceExtractionJobs.Add(Job(job2, prep2.Id));
        db.EvidenceExtractionVersions.Add(Version(ids.Version2, prep2.Id, job2, manifest2, 2));
        await db.SaveChangesAsync(Ct);
        db.Evidences.AddRange(
            Item(Guid.NewGuid(), ids.Version2, project, prep2.Id, 1, "1e1", "Il cliente A ha un tetto di 6 ordini.", EvidenceKind.Constraint, WithArtifact(Content("Cliente A", shared))),
            Item(Guid.NewGuid(), ids.Version2, project, prep2.Id, 2, "1e3", "Nuova regola.", EvidenceKind.Requirement, WithArtifact(Content("Regola", Source(ids, 0, 0, 6, Guid.NewGuid(), 0)))));
        await db.SaveChangesAsync(Ct);
        var second = await db.DocumentPreparations.SingleAsync(p => p.Id == prep2.Id, Ct);
        second.Status = PreparationStatus.Ready;
        second.CurrentEvidenceVersionId = ids.Version2;
        await db.SaveChangesAsync(Ct);
        return ids;
    }

    private static EvidenceItem Item(Guid id, Guid version, Guid project, Guid preparation, int ordinal, string key, string statement, EvidenceKind kind, string content) => new()
    {
        Id = id, VersionId = version, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation, Ordinal = ordinal, Key = key, Kind = kind,
        Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement, Content = content,
    };

    private static async Task<JsonElement> Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await Json(response);
    }

    private static string Url(Seed s, string path) => $"/api/projects/{s.Project}/{path}";

    private async Task<long> JobCount() => await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionJobs" """);

    [Fact]
    public async Task A_mixed_list_discriminates_each_item_by_its_view_version()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var post = await client.PostAsJsonAsync(Url(s, "messages"), new { clientMessageId = Guid.NewGuid(), text = "Il portale invia un promemoria via email entro 24 ore." }, Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var jobs = await JobCount();

        var list = await Body(await client.GetAsync(Url(s, "evidence?viewVersion=1.1.0"), Ct), HttpStatusCode.OK);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, list.GetProperty("nextCursor").ValueKind);
        var message = items.Where(i => i.GetProperty("evidenceViewVersion").GetString() == "1.0.0").ToList();
        var documents = items.Where(i => i.GetProperty("evidenceViewVersion").GetString() == "1.1.0").ToList();
        Assert.NotEmpty(message);
        Assert.Equal(2, documents.Count);   // solo il bundle corrente (versione 1), non la candidata
        Assert.All(message, i => Assert.True(i.GetProperty("evidence").TryGetProperty("messageId", out _)));
        Assert.All(documents, i =>
        {
            var e = i.GetProperty("evidence");
            Assert.False(e.TryGetProperty("messageId", out _));   // nessun MessageId fittizio
            Assert.Equal("Document", e.GetProperty("origin").GetProperty("kind").GetString());
            Assert.Equal(s.Prep1, e.GetProperty("origin").GetProperty("preparationId").GetGuid());
            Assert.True(e.GetProperty("isCurrent").GetBoolean());
            Assert.Equal("evidence-1.1.0", e.GetProperty("content").GetProperty("schemaVersion").GetString());
        });
        Assert.Equal(jobs, await JobCount());   // GET non esegue né accoda AI
    }

    [Fact]
    public async Task The_legacy_format_is_unchanged_and_never_carries_document_items()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        await client.PostAsJsonAsync(Url(s, "messages"), new { clientMessageId = Guid.NewGuid(), text = "Il portale invia un promemoria via email entro 24 ore." }, Ct);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);

        foreach (var path in new[] { "evidence", "evidence?viewVersion=1.0.0" })
        {
            var legacy = await Body(await client.GetAsync(Url(s, path), Ct), HttpStatusCode.OK);
            var items = legacy.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            Assert.False(legacy.TryGetProperty("nextCursor", out _));
            Assert.All(items, e => Assert.True(e.TryGetProperty("messageId", out _) && !e.TryGetProperty("origin", out _)));
        }

        foreach (var path in new[] { $"evidence?documentId={s.Document}", $"evidence?documentVersionId={s.DocumentVersion}", "evidence?originKind=Document", $"evidence?viewVersion=1.0.0&originKind=Document" })
        {
            var rejected = await Body(await client.GetAsync(Url(s, path), Ct), HttpStatusCode.NotAcceptable);
            Assert.Equal("evidence_view_not_supported", rejected.GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(s, "evidence?originKind=Message"), Ct)).StatusCode);
        var unknown = await Body(await client.GetAsync(Url(s, "evidence?viewVersion=2.0.0"), Ct), HttpStatusCode.BadRequest);
        Assert.Equal("unsupported_view_version", unknown.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync(Url(s, "evidence?viewVersion=1.1.0&originKind=Altro"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Document_filters_select_by_origin_document_and_version()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);

        async Task<int> Count(string query) =>
            (await Body(await client.GetAsync(Url(s, "evidence?viewVersion=1.1.0&" + query), Ct), HttpStatusCode.OK)).GetProperty("items").GetArrayLength();

        Assert.Equal(2, await Count("originKind=Document"));
        Assert.Equal(2, await Count($"documentId={s.Document}"));
        Assert.Equal(2, await Count($"documentVersionId={s.DocumentVersion}"));
        Assert.Equal(0, await Count($"documentId={Guid.NewGuid()}"));
        Assert.Equal(0, await Count("originKind=Message"));
        Assert.Equal(1, await Count("originKind=Document&kind=Exclusion"));
    }

    [Fact]
    public async Task Detail_resolves_every_cited_part_with_locator_and_shares_sources_between_evidences()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        var list = await Body(await client.GetAsync(Url(s, "evidence?viewVersion=1.1.0&originKind=Document"), Ct), HttpStatusCode.OK);
        var second = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Single(e => e.GetProperty("key").GetString() == "1e2");

        var detail = await Body(await client.GetAsync(Url(s, $"evidence/{second.GetProperty("id").GetGuid()}?viewVersion=1.1.0"), Ct), HttpStatusCode.OK);
        Assert.Equal("1.1.0", detail.GetProperty("evidenceViewVersion").GetString());
        var parts = detail.GetProperty("evidence").GetProperty("content").GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal([0, 1], parts.Select(p => p.GetProperty("part").GetInt32()));   // parti ordinate, mai fuse
        Assert.Single(parts.Select(p => p.GetProperty("citationGroupId").GetGuid()).Distinct());
        Assert.Equal(["Il cliente A puo ordinare", "Eccezione: nessun limite per il cliente B."[11..42]], parts.Select(p => p.GetProperty("quote").GetString()));
        Assert.Equal(0, detail.GetProperty("sourceMessages").GetArrayLength());

        var docs = detail.GetProperty("sourceDocuments").EnumerateArray().ToList();
        Assert.Equal(2, docs.Count);
        var withLocator = docs.Single(d => d.GetProperty("blockId").GetGuid() == s.Blocks[1]);
        Assert.Equal(s.SharedSource, withLocator.GetProperty("sourceId").GetGuid());
        Assert.Equal("regole.md", withLocator.GetProperty("originalName").GetString());
        Assert.Equal(1, withLocator.GetProperty("documentVersion").GetInt32());
        Assert.Equal(1, withLocator.GetProperty("generation").GetInt32());
        var locator = withLocator.GetProperty("locator");
        Assert.Equal(2, locator.GetProperty("page").GetInt32());
        Assert.Equal("pdf-points", locator.GetProperty("bbox").GetProperty("coordinateSystem").GetString());
        Assert.Equal(1.5, locator.GetProperty("bbox").GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(7, locator.GetProperty("sourceLines").GetProperty("start").GetInt32());
        Assert.Equal("Regole", locator.GetProperty("headingPath")[0].GetString());
        Assert.Contains("/passages/" + s.Blocks[1] + "?start=0&end=25", withLocator.GetProperty("passageUrl").GetString());
        var bare = docs.Single(d => d.GetProperty("blockId").GetGuid() == s.Blocks[2]).GetProperty("locator");
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("page").ValueKind);   // nessuna pagina presunta
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("bbox").ValueKind);
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("sourceLines").ValueKind);

        // La stessa fonte (stesso sourceId) citata da due evidenze compare una sola volta per dettaglio e con lo stesso id.
        var first = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("evidence")).Single(e => e.GetProperty("key").GetString() == "1e1");
        var firstDetail = await Body(await client.GetAsync(Url(s, $"evidence/{first.GetProperty("id").GetGuid()}?viewVersion=1.1.0"), Ct), HttpStatusCode.OK);
        Assert.Equal(s.SharedSource, Assert.Single(firstDetail.GetProperty("sourceDocuments").EnumerateArray()).GetProperty("sourceId").GetGuid());

        var rejected = await Body(await client.GetAsync(Url(s, $"evidence/{s.Evidence1}"), Ct), HttpStatusCode.NotAcceptable);
        Assert.Equal("evidence_view_not_supported", rejected.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/evidence/{s.Evidence1}?viewVersion=1.1.0", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_message_evidence_requested_as_1_1_keeps_its_1_0_content_inside_the_envelope()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        await client.PostAsJsonAsync(Url(s, "messages"), new { clientMessageId = Guid.NewGuid(), text = "Le fatture restano fuori perimetro." }, Ct);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var id = (await Body(await client.GetAsync(Url(s, "evidence"), Ct), HttpStatusCode.OK)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var legacy = await Body(await client.GetAsync(Url(s, $"evidence/{id}"), Ct), HttpStatusCode.OK);
        var wrapped = await Body(await client.GetAsync(Url(s, $"evidence/{id}?viewVersion=1.1.0"), Ct), HttpStatusCode.OK);
        Assert.Equal("1.0.0", wrapped.GetProperty("evidenceViewVersion").GetString());
        Assert.Equal(legacy.GetProperty("evidence").GetRawText(), wrapped.GetProperty("evidence").GetRawText());
        Assert.Equal(0, wrapped.GetProperty("sourceDocuments").GetArrayLength());
        Assert.True(wrapped.GetProperty("sourceMessages").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Document_versions_and_the_bundle_route_distinguish_current_from_candidate()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);

        var bundle = await Body(await client.GetAsync(Url(s, $"documents/{s.Document}/versions/1/evidence/versions"), Ct), HttpStatusCode.OK);
        Assert.Equal("1.1.0", bundle.GetProperty("evidenceViewVersion").GetString());
        Assert.Equal(1, bundle.GetProperty("bundleRevision").GetInt32());
        Assert.Equal(s.Prep1, bundle.GetProperty("currentPreparationId").GetGuid());
        Assert.Equal(s.Version1, bundle.GetProperty("currentEvidenceVersionId").GetGuid());
        var versions = bundle.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([1, 2], versions.Select(v => v.GetProperty("generation").GetInt32()));
        Assert.Equal([true, false], versions.Select(v => v.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.GetAsync(Url(s, $"documents/{s.Document}/versions/1/evidence/versions?viewVersion=1.0.0"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url(s, $"documents/{s.Document}/versions/1/evidence/versions?viewVersion=9"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(s, $"documents/{s.Document}/versions/9/evidence/versions"), Ct)).StatusCode);

        var version = await Body(await client.GetAsync(Url(s, $"evidence/versions/{s.Version2}?viewVersion=1.1.0"), Ct), HttpStatusCode.OK);
        Assert.False(version.GetProperty("version").GetProperty("isCurrent").GetBoolean());   // candidata visibile ma non corrente
        Assert.Equal(2, version.GetProperty("evidences").GetArrayLength());
        Assert.NotEmpty(version.GetProperty("sourceDocuments").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotAcceptable, (await client.GetAsync(Url(s, $"evidence/versions/{s.Version2}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Compare_is_allowed_only_with_a_verifiable_structural_mapping()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client, manifest2: Hash1);

        var diff = await Body(await client.GetAsync(Url(s, $"evidence/versions/{s.Version1}/compare/{s.Version2}?viewVersion=1.1.0"), Ct), HttpStatusCode.OK);
        Assert.Equal("1.1.0", diff.GetProperty("evidenceViewVersion").GetString());
        var summary = diff.GetProperty("summary");
        Assert.Equal((1, 1, 0, 1), (summary.GetProperty("added").GetInt32(), summary.GetProperty("removed").GetInt32(), summary.GetProperty("unchanged").GetInt32(), summary.GetProperty("modified").GetInt32()));
        var modified = diff.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("status").GetString() == "Modified");
        Assert.Contains(modified.GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "statement");

        var same = await Body(await client.GetAsync(Url(s, $"evidence/versions/{s.Version1}/compare/{s.Version1}?viewVersion=1.1.0"), Ct), HttpStatusCode.OK);
        Assert.Equal(2, same.GetProperty("summary").GetProperty("unchanged").GetInt32());

        var legacy = await Body(await client.GetAsync(Url(s, $"evidence/versions/{s.Version1}/compare/{s.Version2}"), Ct), HttpStatusCode.NotAcceptable);
        Assert.Equal("evidence_view_not_supported", legacy.GetProperty("code").GetString());

        await Execute("""UPDATE "EvidenceExtractionVersions" SET "ManifestHash" = @h WHERE "Id" = @id""", ("h", Hash2), ("id", s.Version2));
        var other = await Body(await client.GetAsync(Url(s, $"evidence/versions/{s.Version1}/compare/{s.Version2}?viewVersion=1.1.0"), Ct), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("versions_not_comparable", other.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Compare_never_mixes_origins()
    {
        using var client = await App.LoginAsync();
        var s = await SeedAsync(client);
        await client.PostAsJsonAsync(Url(s, "messages"), new { clientMessageId = Guid.NewGuid(), text = "Le fatture restano fuori perimetro." }, Ct);
        await EvidenceTestSeed.CompleteAsync(App.Services, Ct);
        var messageVersion = await Scalar<Guid>("""SELECT "Id" FROM "EvidenceExtractionVersions" WHERE "ProjectId" = @p AND "OriginKind" = 'Message'""", ("p", s.Project));

        var mixed = await Body(await client.GetAsync(Url(s, $"evidence/versions/{messageVersion}/compare/{s.Version1}?viewVersion=1.1.0"), Ct), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("versions_not_comparable", mixed.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(s, $"evidence/versions/{messageVersion}/compare/{messageVersion}"), Ct)).StatusCode);   // legacy invariato
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url(s, $"evidence/versions/{messageVersion}/compare/{messageVersion}?viewVersion=3"), Ct)).StatusCode);
    }
}
