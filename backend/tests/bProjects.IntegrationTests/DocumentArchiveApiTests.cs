using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// T018 (PG): gate integrato dell'archivio I1 su API reale + PostgreSQL reale + volume reale, con il modello irraggiungibile: limiti del
/// server e ruoli, formati effettivi e file ostili, interruzione fra originale e database, riavvio, quota concorrente, preferenze di
/// fonte (T015) e guard di Analizza e della chat. Completa le prove contrattuali (T008) e di recupero (T010), che girano contro lo stesso servizio.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentArchiveApiTests : ArchiveTestBase
{
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string LimitsUrl = "/api/admin/document-limits";

    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;
        options.ProjectQuotaBytes = 20_000;
        options.MaxExpandedBytes = 20_000;   // un DOCX «bomba» è provabile con poche centinaia di byte compressi
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Bytes(int length, char fill = 'a') => Encoding.ASCII.GetBytes(new string(fill, length));

    private static byte[] Docx(Action<ZipArchive>? extra = null, bool document = true, string? contentTypesXml = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] content)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }

            Add("[Content_Types].xml", Encoding.UTF8.GetBytes(contentTypesXml ?? "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"));

            if (document)
            {
                Add("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>"u8.ToArray());
            }

            extra?.Invoke(zip);
        }

        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(content);
    }

    private static async Task<HttpResponseMessage> Put(HttpClient client, string url, string? ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> PutSelection(HttpClient client, Guid project, string? ifMatch, params object[] items) =>
        Put(client, $"/api/projects/{project}/source-selection", ifMatch, new { items });

    private static object Item(Guid document, bool included = true, string mode = "LatestReady", Guid? versionId = null) =>
        new { documentId = document, included, mode, versionId };

    private static async Task<JsonElement> Selection(HttpClient client, Guid project) =>
        await Json(await client.GetAsync($"/api/projects/{project}/source-selection", Ct));

    // ================================================================ limiti del server, ruoli

    [Fact]
    public async Task A_file_exactly_at_the_limit_is_accepted_one_byte_over_is_413_and_earlier_originals_stay_intact()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var atLimit = Bytes(5_000);
        var created = await Json(await Upload(client, project, atLimit, "limite.txt"));

        var over = await Upload(client, project, Bytes(5_001), "oltre.txt", documentId: created.GetProperty("documentId").GetGuid());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal(5_000, created.GetProperty("byteLength").GetInt64());
        var detail = await Json(await client.GetAsync($"/api/projects/{project}/documents/{created.GetProperty("documentId").GetGuid()}", Ct));
        var version = Assert.Single(detail.GetProperty("versions").EnumerateArray());
        Assert.Equal(Sha(atLimit), version.GetProperty("sha256").GetString());   // hash e versione precedente invariati
        var download = await client.GetAsync($"/api/projects/{project}/documents/{created.GetProperty("documentId").GetGuid()}/versions/1/download", Ct);
        Assert.Equal(atLimit, await download.Content.ReadAsByteArrayAsync(Ct));
        Assert.Empty(TemporaryFiles());
        Assert.Single(StoredObjects());
    }

    [Fact]
    public async Task The_admin_policy_in_the_database_governs_uploads_raising_the_quota_makes_the_next_upload_fit()
    {
        using var client = await App.LoginAsync();
        using var admin = await App.LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);
        var project = await NewProject(client);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await Upload(client, project, Bytes(5_000, (char)('a' + i)))).StatusCode);   // 20 000 = quota
        }

        Assert.Equal(HttpStatusCode.InsufficientStorage, (await Upload(client, project, Bytes(10))).StatusCode);

        var view = (await (await admin.GetAsync(LimitsUrl, Ct)).Content.ReadFromJsonAsync<DocumentLimitEndpoints.PolicyView>(Ct))!;
        var raised = await Put(admin, LimitsUrl, $"\"{view.Revision}\"", view.Limits with { ProjectQuotaBytes = 30_000 });
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await Upload(client, project, Bytes(10, 'z'))).StatusCode);
        Assert.Equal(5, await Count("SELECT count(*) FROM \"DocumentVersions\""));
    }

    [Fact]
    public async Task An_admin_cannot_use_project_documents_and_a_pm_cannot_change_limits()
    {
        using var client = await App.LoginAsync();
        using var admin = await App.LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);
        var project = await NewProject(client);

        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(admin, project, Bytes(20))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/projects/{project}/documents", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(LimitsUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(client, LimitsUrl, "\"1\"", new { })).StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));
    }

    [Fact]
    public async Task Two_pms_see_the_same_project_documents_and_same_name_files_stay_distinct_documents()
    {
        using var first = await App.LoginAsync();
        using var second = await App.LoginAsync(TestApp.SecondPmUser, TestApp.SecondPmPassword);
        var project = await NewProject(first);
        await Upload(first, project, Text("uno"), "verbale.txt", displayName: "Verbale");
        await Upload(second, project, Text("due"), "verbale.txt", displayName: "Verbale");

        var page = await Json(await second.GetAsync($"/api/projects/{project}/documents", Ct));

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Select(i => i.GetProperty("documentId").GetGuid()).Distinct().Count());
        Assert.All(items, i => Assert.Equal("Verbale", i.GetProperty("displayName").GetString()));
    }

    // ================================================================ formato effettivo e file ostili

    [Fact]
    public async Task Pdf_docx_markdown_and_text_are_accepted_with_a_server_verified_media_type()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");

        var results = new[]
        {
            (await Json(await Upload(client, project, pdf, "a.pdf", "application/pdf")), "application/pdf"),
            (await Json(await Upload(client, project, Docx(), "b.docx", DocxType)), DocxType),
            (await Json(await Upload(client, project, Text("# Titolo"), "c.md", "text/markdown")), "text/markdown"),
            (await Json(await Upload(client, project, Text("testo"), "d.txt")), "text/plain"),
            (await Json(await Upload(client, project, Text("# senza tipo"), "e.md", "application/octet-stream")), "text/markdown"),
        };

        foreach (var (view, mediaType) in results)
        {
            Assert.Equal(mediaType, view.GetProperty("mediaType").GetString());
            Assert.Equal("Queued", view.GetProperty("processingStatus").GetString());
        }

        Assert.Equal(5, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"Status\" = 'Queued' AND \"Kind\" = 'Parse'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"Status\" <> 'Queued'"));   // nessuna esecuzione: solo accodamento
    }

    [Fact]
    public async Task Macro_encrypted_hostile_and_incoherent_files_are_rejected_and_leave_nothing_behind()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var cases = new (string Name, byte[] Bytes, string Type, HttpStatusCode Expected)[]
        {
            ("macro.docx", Docx(zip => AddEntry(zip, "word/vbaProject.bin", [1, 2, 3])), DocxType, HttpStatusCode.UnsupportedMediaType),
            ("macro-ct.docx", Docx(contentTypesXml: "<Types><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.ms-word.document.macroEnabled.main+xml\"/></Types>"), DocxType, HttpStatusCode.UnsupportedMediaType),
            ("bomba.docx", Docx(zip => AddEntry(zip, "word/media/bomba.bin", new byte[100_000])), DocxType, HttpStatusCode.UnprocessableEntity),
            ("percorso.docx", Docx(zip => AddEntry(zip, "../fuori.xml", [1])), DocxType, HttpStatusCode.UnprocessableEntity),
            ("incompleto.docx", Docx(document: false), DocxType, HttpStatusCode.UnsupportedMediaType),
            ("cifrato.docx", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0], DocxType, HttpStatusCode.UnprocessableEntity),
            ("cifrato.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj<<>>endobj\ntrailer<</Encrypt 5 0 R>>\n%%EOF"), "application/pdf", HttpStatusCode.UnprocessableEntity),
            ("binario.txt", [0x68, 0x69, 0x00, 0x01, 0x02], "text/plain", HttpStatusCode.UnsupportedMediaType),
            ("pdf-finto.txt", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"), "text/plain", HttpStatusCode.UnsupportedMediaType),
            ("dichiarato.txt", Text("ciao"), "application/pdf", HttpStatusCode.UnsupportedMediaType),
            ("vecchio.doc", Text("x"), "application/msword", HttpStatusCode.UnsupportedMediaType),
        };

        var accepted = 0;
        foreach (var (name, bytes, type, expected) in cases)
        {
            var response = await Upload(client, project, bytes, name, type);
            Assert.True(response.StatusCode == expected, $"{name}: atteso {(int)expected}, ricevuto {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(StorageRoot, body);
            Assert.DoesNotContain("   at ", body);
            accepted += response.StatusCode == HttpStatusCode.Created ? 1 : 0;
        }

        Assert.Equal(accepted, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(accepted, StoredObjects().Length);   // i rifiutati non hanno lasciato originali
        Assert.Empty(TemporaryFiles());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Receiving'"));
    }

    // ================================================================ interruzione originale/database, riavvio

    [Fact]
    public async Task A_database_failure_after_the_original_is_durable_rolls_back_every_row_and_the_replay_acquires_it_once()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Text("interruzione database", 600);
        await Execute("""
            CREATE FUNCTION fail_job_insert() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'guasto iniettato'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_job_insert BEFORE INSERT ON "DocumentJobs" FOR EACH ROW EXECUTE FUNCTION fail_job_insert();
            """);

        var failed = await Upload(client, project, bytes, key: key);

        Assert.True((int)failed.StatusCode >= 500);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));   // tutto o nulla: versione, preparazione e ricevuta
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentPreparations\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProjectDocuments\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Committed'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Receiving'"));   // prenotazione rilasciata
        var orphan = Assert.Single(StoredObjects());   // l'originale durevole resta, non referenziato
        Assert.Equal(bytes, await File.ReadAllBytesAsync(orphan, Ct));

        await Execute("DROP TRIGGER fail_job_insert ON \"DocumentJobs\"; DROP FUNCTION fail_job_insert();");
        var replay = await Upload(client, project, bytes, key: key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Single(StoredObjects());
    }

    [Fact]
    public async Task After_a_restart_originals_receipts_and_numbering_are_intact()
    {
        var key = Guid.NewGuid();
        var v1 = Text("prima del riavvio", 900);
        Guid project, document;
        using (var before = await App.LoginAsync())
        {
            project = await NewProject(before);
            var created = await Json(await Upload(before, project, v1, "r.txt", key: key));
            document = created.GetProperty("documentId").GetGuid();
        }

        await RestartAppAsync();

        using var client = await App.LoginAsync();
        var download = await client.GetAsync($"/api/projects/{project}/documents/{document}/versions/1/download", Ct);
        Assert.Equal(v1, await download.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await Upload(client, project, v1, "r.txt", key: key)).StatusCode);   // la ricevuta sopravvive al riavvio
        var next = await Json(await Upload(client, project, Text("dopo il riavvio"), "r2.txt", documentId: document));
        Assert.Equal(2, next.GetProperty("version").GetInt32());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"RequestKey\" = @k", ("k", key)));
        Assert.Equal(2, StoredObjects().Length);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"Status\" <> 'Queued'"));
    }

    // ================================================================ quota concorrente

    [Fact]
    public async Task Many_concurrent_uploads_never_exceed_the_original_quota_and_leave_no_residue()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        // 8 × 4 000 byte contro una quota di 20 000: esattamente 5 entrano, gli altri 3 sono 507.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Upload(client, project, Bytes(4_000, (char)('a' + i)))));

        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.InsufficientStorage));
        Assert.Equal(20_000, await Scalar<long>("SELECT sum(\"ByteLength\") FROM \"DocumentVersions\""));
        Assert.Equal(20_000, await Scalar<long>("SELECT coalesce(sum(\"ReservedBytes\"), 0) FROM \"UploadReceipts\" WHERE \"Status\" = 'Committed'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"UploadReceipts\" WHERE \"Status\" = 'Receiving'"));
        Assert.Equal(5, StoredObjects().Length);   // i rifiutati dopo il rename non lasciano orfani
        Assert.Empty(TemporaryFiles());
        Assert.Equal(5, await Count("SELECT count(*) FROM \"DocumentJobs\""));
    }

    // ================================================================ preferenze di fonte (T015)

    [Fact]
    public async Task Reading_the_selection_changes_nothing_and_reports_an_unready_document_as_blocking()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var created = await Json(await Upload(client, project, Text("fonte"), "f.txt", displayName: "Fonte"));
        var jobs = await Count("SELECT count(*) FROM \"DocumentJobs\"");

        var response = await client.GetAsync($"/api/projects/{project}/source-selection", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag!.Tag);
        var view = await Json(response);
        Assert.Equal(0, view.GetProperty("revision").GetInt64());
        var item = Assert.Single(view.GetProperty("items").EnumerateArray());
        Assert.Equal(created.GetProperty("documentId").GetGuid(), item.GetProperty("documentId").GetGuid());
        Assert.True(item.GetProperty("included").GetBoolean());
        Assert.Equal("LatestReady", item.GetProperty("mode").GetString());
        Assert.Equal("Unavailable", item.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("resolvedVersionId").ValueKind);
        var blocking = Assert.Single(view.GetProperty("blockingSources").EnumerateArray());
        Assert.Equal("Queued", blocking.GetProperty("status").GetString());
        Assert.Equal("Fonte", blocking.GetProperty("displayName").GetString());
        Assert.Equal(jobs, await Count("SELECT count(*) FROM \"DocumentJobs\""));   // GET non crea job
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProjectSourceSelections\""));   // né righe di preferenza
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/source-selection", Ct)).StatusCode);
    }

    [Fact]
    public async Task Put_requires_if_match_rejects_a_stale_revision_and_exclusion_survives_new_versions()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var document = (await Json(await Upload(client, project, Text("v1"), "d.txt"))).GetProperty("documentId").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await PutSelection(client, project, null, Item(document))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, $"/api/projects/{project}/source-selection", "\"0\"", new { })).StatusCode);
        var stale = await PutSelection(client, project, "\"5\"", Item(document));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await Json(stale);
        Assert.Equal("source_selection_conflict", problem.GetProperty("code").GetString());
        Assert.Equal(0, problem.GetProperty("currentRevision").GetInt64());

        var excluded = await PutSelection(client, project, "\"0\"", Item(document, included: false));
        Assert.Equal(HttpStatusCode.OK, excluded.StatusCode);
        Assert.Equal("\"1\"", excluded.Headers.ETag!.Tag);
        var view = await Json(excluded);
        Assert.Equal("Excluded", view.GetProperty("items")[0].GetProperty("availability").GetString());
        Assert.Empty(view.GetProperty("blockingSources").EnumerateArray());   // escluso: non blocca

        await Upload(client, project, Text("v2 più lunga", 400), "d2.txt", documentId: document);   // nuova versione
        var after = await Selection(client, project);
        Assert.False(after.GetProperty("items")[0].GetProperty("included").GetBoolean());
        Assert.Empty(after.GetProperty("blockingSources").EnumerateArray());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProjectSourceSelectionItems\""));
    }

    [Fact]
    public async Task Two_puts_on_the_same_revision_have_one_winner_and_the_others_are_409()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var document = (await Json(await Upload(client, project, Text("c"), "c.txt"))).GetProperty("documentId").GetGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => PutSelection(client, project, "\"0\"", Item(document, included: i % 2 == 0))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, (await Selection(client, project)).GetProperty("revision").GetInt64());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProjectSourceSelectionItems\""));
    }

    [Fact]
    public async Task Pinned_versions_are_validated_and_an_unready_pinned_version_is_not_applicable_and_blocks()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var other = await NewProject(client);
        var a = await Json(await Upload(client, project, Text("a"), "a.txt"));
        var b = await Json(await Upload(client, project, Text("b"), "b.txt"));
        var foreign = await Json(await Upload(client, other, Text("x"), "x.txt"));

        Assert.Equal((HttpStatusCode)422, (await PutSelection(client, project, "\"0\"", Item(a.GetProperty("documentId").GetGuid(), mode: "Pinned", versionId: b.GetProperty("id").GetGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutSelection(client, project, "\"0\"", Item(a.GetProperty("documentId").GetGuid(), mode: "Pinned", versionId: Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutSelection(client, project, "\"0\"", Item(a.GetProperty("documentId").GetGuid(), mode: "Pinned", versionId: foreign.GetProperty("id").GetGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutSelection(client, project, "\"0\"", Item(foreign.GetProperty("documentId").GetGuid()))).StatusCode);   // documento di altro progetto
        Assert.Equal((HttpStatusCode)422, (await PutSelection(client, project, "\"0\"", Item(a.GetProperty("documentId").GetGuid(), mode: "Pinned"))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await PutSelection(client, project, "\"0\"", Item(a.GetProperty("documentId").GetGuid(), versionId: a.GetProperty("id").GetGuid()))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await PutSelection(client, project, "\"0\"", Item(a.GetProperty("documentId").GetGuid()), Item(a.GetProperty("documentId").GetGuid()))).StatusCode);
        Assert.Equal(0, (await Selection(client, project)).GetProperty("revision").GetInt64());   // nessun PUT rifiutato ha avanzato la revisione

        var pinned = await PutSelection(client, project, "\"0\"", Item(a.GetProperty("documentId").GetGuid(), mode: "Pinned", versionId: a.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        var view = await Json(pinned);
        var item = view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("documentId").GetGuid() == a.GetProperty("documentId").GetGuid());
        Assert.Equal("Pinned", item.GetProperty("mode").GetString());
        Assert.Equal(a.GetProperty("id").GetGuid(), item.GetProperty("resolvedVersionId").GetGuid());
        Assert.Equal("Unavailable", item.GetProperty("availability").GetString());   // scelta esplicita di una versione non pronta
        Assert.Equal(2, view.GetProperty("blockingSources").GetArrayLength());
        var detail = await Json(await client.GetAsync($"/api/projects/{project}/documents/{a.GetProperty("documentId").GetGuid()}", Ct));
        var versionView = detail.GetProperty("versions")[0];
        Assert.False(versionView.GetProperty("isApplicable").GetBoolean());
        Assert.Equal("Pinned", versionView.GetProperty("selectionMode").GetString());
    }

    [Fact]
    public async Task A_selection_with_more_items_than_documents_or_repeated_documents_is_422_before_the_project_lock_is_taken()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var document = (await Json(await Upload(client, project, Text("uno"), "uno.txt"))).GetProperty("documentId").GetGuid();

        // Un altro scrittore tiene il lock di progetto: un PUT che lo cercasse resterebbe appeso, quello respinto prima risponde subito.
        await using var holder = await OpenConnection();
        await using var tx = await holder.BeginTransactionAsync(Ct);
        await using (var lockCommand = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", holder, tx))
        {
            lockCommand.Parameters.AddWithValue("k", ProjectLockKey(project));
            await lockCommand.ExecuteNonQueryAsync(Ct);
        }

        var tooMany = await Put(client, $"/api/projects/{project}/source-selection", "\"0\"",
            new { items = new[] { Item(document), Item(Guid.NewGuid()) } });
        var flood = await Put(client, $"/api/projects/{project}/source-selection", "\"0\"",
            new { items = Enumerable.Range(0, 500).Select(_ => Item(document)).ToArray() });
        var repeated = await PutSelection(client, project, "\"0\"", Item(document), Item(document));

        Assert.Equal((HttpStatusCode)422, tooMany.StatusCode);
        Assert.Equal((HttpStatusCode)422, flood.StatusCode);
        Assert.Equal((HttpStatusCode)422, repeated.StatusCode);
        Assert.Equal("too_many_items", (await Json(flood)).GetProperty("code").GetString());
        await tx.RollbackAsync(Ct);
        Assert.Equal(0, (await Selection(client, project)).GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task A_valid_selection_of_many_pinned_documents_loads_versions_in_one_batch_and_applies()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var items = new List<object>();
        for (var i = 0; i < 6; i++)
        {
            var created = await Json(await Upload(client, project, Text("doc" + i, 100 + i), $"d{i}.txt"));
            items.Add(Item(created.GetProperty("documentId").GetGuid(), mode: "Pinned", versionId: created.GetProperty("id").GetGuid()));
        }

        var response = await PutSelection(client, project, "\"0\"", [.. items]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6, await Count("SELECT count(*) FROM \"ProjectSourceSelectionItems\" WHERE \"Mode\" = 'Pinned'"));
    }

    // ================================================================ review di sicurezza: lock, quota, tetti, messaggi

    [Fact]
    public async Task The_unreferenced_original_of_a_quota_rejected_upload_is_removed_while_the_project_lock_is_still_held()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        bool? lockFreeAtDelete = null;
        Store.OnStage = stage =>
        {
            if (stage == DocumentStoreStage.AfterRename)
            {
                // Un'altra prenotazione viva consuma la quota: al commit (sotto lock) il file reale non entra più.
                Task.Run(() => Execute(
                    "INSERT INTO \"UploadReceipts\" (\"Id\", \"ProjectId\", \"RequestKey\", \"InputHash\", \"ReservedBytes\", \"Status\", \"CreatedAt\", \"ExpiresAt\") " +
                    "VALUES (@id, @p, @k, @h, 19000, 'Receiving', now(), now() + interval '10 minutes')",
                    ("id", Guid.NewGuid()), ("p", project), ("k", Guid.NewGuid()), ("h", new string('c', 64)))).GetAwaiter().GetResult();
            }
            else if (stage == DocumentStoreStage.BeforeDelete)
            {
                lockFreeAtDelete = Task.Run(() => Scalar<bool>("SELECT pg_try_advisory_xact_lock(hashtextextended(@k, 0))", ("k", ProjectLockKey(project)))).GetAwaiter().GetResult();
            }
        };

        var rejected = await Upload(client, project, Bytes(3_000));
        Store.OnStage = null;

        Assert.Equal(HttpStatusCode.InsufficientStorage, rejected.StatusCode);
        Assert.False(lockFreeAtDelete, "la cancellazione dell'orfano è avvenuta fuori dal lock di progetto (o non è avvenuta)");
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task An_upload_without_content_length_reserves_the_maximum_file_size_and_one_with_it_reserves_the_lower_bound()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        // Il TestServer bufferizza il corpo e valorizza sempre Content-Length: il caso chunked si prova sul servizio, con la stessa policy.
        using var scope = App.Services.CreateScope();
        var uploads = scope.ServiceProvider.GetRequiredService<DocumentUploadService>();
        var limits = new DocumentOptions { MaxFileBytes = 5_000, ProjectQuotaBytes = 20_000 }.ToLimits();

        var unknown = await uploads.OpenAsync(project, Guid.NewGuid(), null, contentLength: null, limits, Ct);
        var known = await uploads.OpenAsync(project, Guid.NewGuid(), null, contentLength: 100, limits, Ct);

        Assert.Equal(UploadOpenKind.Proceed, unknown.Kind);
        Assert.Equal(5_000, await Scalar<long>("SELECT \"ReservedBytes\" FROM \"UploadReceipts\" WHERE \"Id\" = @i", ("i", unknown.ReceiptId)));
        Assert.Equal(0, await Scalar<long>("SELECT \"ReservedBytes\" FROM \"UploadReceipts\" WHERE \"Id\" = @i", ("i", known.ReceiptId)));
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(UploadOpenKind.Proceed, (await uploads.OpenAsync(project, Guid.NewGuid(), null, null, limits, Ct)).Kind);
        }

        Assert.Equal(15_000, await Scalar<long>("SELECT sum(\"ReservedBytes\") FROM \"UploadReceipts\" WHERE \"Status\" = 'Receiving'"));
    }

    [Fact]
    public async Task Admin_limits_above_the_absolute_ceilings_are_422_and_change_nothing()
    {
        using var admin = await App.LoginAsync(TestApp.AdminUser, TestApp.AdminPassword);
        var view = (await (await admin.GetAsync(LimitsUrl, Ct)).Content.ReadFromJsonAsync<DocumentLimitEndpoints.PolicyView>(Ct))!;
        var etag = $"\"{view.Revision}\"";

        Assert.Equal((HttpStatusCode)422, (await Put(admin, LimitsUrl, etag, view.Limits with { MaxFileBytes = 52_000_001, ProjectQuotaBytes = 100_000_000 })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Put(admin, LimitsUrl, etag, view.Limits with { MaxExpandedBytes = 1_000_000_001 })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Put(admin, LimitsUrl, etag, view.Limits with { ProjectQuotaBytes = 2_000_000_000_000 })).StatusCode);

        var after = (await (await admin.GetAsync(LimitsUrl, Ct)).Content.ReadFromJsonAsync<DocumentLimitEndpoints.PolicyView>(Ct))!;
        Assert.Equal(view.Revision, after.Revision);
    }

    [Fact]
    public async Task The_issues_summary_exposes_the_error_code_and_a_fixed_text_never_the_free_preparation_message()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var created = await Json(await Upload(client, project, Text("problema"), "p.txt"));
        var document = created.GetProperty("documentId").GetGuid();
        await Execute("UPDATE \"DocumentPreparations\" SET \"ErrorCode\" = 'parse_failed', \"ErrorMessage\" = 'System.IO.IOException: C:\\segreto\\storage\\objects\\x at Parser.Run()'");

        var detail = await client.GetAsync($"/api/projects/{project}/documents/{document}", Ct);
        var text = await detail.Content.ReadAsStringAsync(Ct);
        var version = JsonDocument.Parse(text).RootElement.GetProperty("versions")[0];

        Assert.Equal("parse_failed", version.GetProperty("issuesSummary").GetProperty("code").GetString());
        Assert.Equal(IssuesSummary.FixedMessage, version.GetProperty("issuesSummary").GetProperty("message").GetString());
        Assert.DoesNotContain("segreto", text);
        Assert.DoesNotContain("IOException", text);
    }

    // ================================================================ guard di Analizza e della chat

    private async Task<HttpResponseMessage> Analyze(HttpClient client, Guid project)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/analyses");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> Message(HttpClient client, Guid project, Guid? clientMessageId = null) =>
        await client.PostAsJsonAsync($"/api/projects/{project}/messages", new { clientMessageId = clientMessageId ?? Guid.NewGuid(), text = "Vorrei un portale per i clienti" }, Ct);

    [Fact]
    public async Task Analyze_is_blocked_by_an_included_unready_source_creating_no_run_and_proceeds_once_excluded()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        Assert.Equal(HttpStatusCode.Created, (await Message(client, project)).StatusCode);
        var document = (await Json(await Upload(client, project, Text("non pronto"), "n.txt", displayName: "Non pronto"))).GetProperty("documentId").GetGuid();

        var blocked = await Analyze(client, project);

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var problem = await Json(blocked);
        Assert.Equal("sources_not_ready", problem.GetProperty("code").GetString());
        var source = Assert.Single(problem.GetProperty("blockingSources").EnumerateArray());
        Assert.Equal(document, source.GetProperty("documentId").GetGuid());
        Assert.Equal("Queued", source.GetProperty("status").GetString());
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\""));   // nessun run creato
        Assert.Equal(0, await Count("SELECT count(*) FROM \"SourceSnapshots\""));

        Assert.Equal(HttpStatusCode.OK, (await PutSelection(client, project, "\"0\"", Item(document, included: false))).StatusCode);
        var accepted = await Analyze(client, project);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"SourceSnapshotId\" IS NOT NULL"));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"Availability\" = 'Excluded' AND \"Included\" = false"));
    }

    [Fact]
    public async Task Analyze_without_documents_keeps_the_001_branch_with_no_snapshot()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        var noInput = await Analyze(client, project);
        Assert.Equal((HttpStatusCode)422, noInput.StatusCode);
        Assert.Equal("no_pm_input", (await Json(noInput)).GetProperty("code").GetString());

        await Message(client, project);
        Assert.Equal(HttpStatusCode.Accepted, (await Analyze(client, project)).StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"SourceSnapshots\""));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ProcessingRuns\" WHERE \"SourceSnapshotId\" IS NOT NULL"));
    }

    [Fact]
    public async Task A_chat_message_with_an_unready_source_is_saved_with_the_unavailability_frozen_in_the_turn_snapshot()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var document = (await Json(await Upload(client, project, Text("in coda"), "q.txt"))).GetProperty("documentId").GetGuid();
        var clientMessageId = Guid.NewGuid();

        var posted = await Message(client, project, clientMessageId);

        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);   // il messaggio non attende la preparazione
        var turn = (await Json(posted)).GetProperty("chatTurnId").GetGuid();
        var snapshot = await Scalar<Guid>("SELECT \"SourceSnapshotId\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"Availability\" = 'Unavailable' AND \"DocumentId\" = @d AND \"EvidenceVersionId\" IS NULL", ("s", snapshot), ("d", document)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ConversationMessages\" WHERE \"Role\" = 'User'"));

        // La fonte diventa esclusa dopo: lo snapshot del turno resta quello della richiesta, e il replay non ne crea un altro.
        await PutSelection(client, project, "\"0\"", Item(document, included: false));
        Assert.Equal(HttpStatusCode.OK, (await Message(client, project, clientMessageId)).StatusCode);
        Assert.Equal(snapshot, await Scalar<Guid>("SELECT \"SourceSnapshotId\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"SourceSnapshots\""));
        Assert.Equal("Unavailable", (await Scalar<string>("SELECT \"Availability\" FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s", ("s", snapshot))));
    }

    [Fact]
    public async Task A_snapshot_of_an_unready_pinned_source_keeps_the_pinned_version_identity_even_after_a_later_version_and_selection_change()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var v1 = await Json(await Upload(client, project, Text("v1 in coda"), "p.txt"));
        var document = v1.GetProperty("documentId").GetGuid();
        var v1Id = v1.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PutSelection(client, project, "\"0\"", Item(document, mode: "Pinned", versionId: v1Id))).StatusCode);

        var turn = (await Json(await Message(client, project))).GetProperty("chatTurnId").GetGuid();
        var snapshot = await Scalar<Guid>("SELECT \"SourceSnapshotId\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn));
        var hash = await Scalar<string>("SELECT \"Hash\" FROM \"SourceSnapshots\" WHERE \"Id\" = @s", ("s", snapshot));
        var pinnedRows = "SELECT count(*) FROM \"SourceSnapshotItems\" WHERE \"SnapshotId\" = @s AND \"Availability\" = 'Unavailable' AND \"DocumentVersionId\" = @v AND \"PreparationId\" IS NULL AND \"EvidenceVersionId\" IS NULL";

        // Il snapshot registra QUALE versione era fissata, pur senza preparazione/evidenza Ready.
        Assert.Equal(1, await Count(pinnedRows, ("s", snapshot), ("v", v1Id)));

        // Una v2 successiva e un cambio di selezione non riscrivono retroattivamente lo snapshot.
        Assert.Equal(HttpStatusCode.Created, (await Upload(client, project, Text("v2 in coda"), "p2.txt", documentId: document)).StatusCode);
        var revision = (await Selection(client, project)).GetProperty("revision").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await PutSelection(client, project, $"\"{revision}\"", Item(document, included: false))).StatusCode);

        Assert.Equal(1, await Count(pinnedRows, ("s", snapshot), ("v", v1Id)));
        Assert.Equal(hash, await Scalar<string>("SELECT \"Hash\" FROM \"SourceSnapshots\" WHERE \"Id\" = @s", ("s", snapshot)));
        Assert.Equal(snapshot, await Scalar<Guid>("SELECT \"SourceSnapshotId\" FROM \"ChatTurns\" WHERE \"Id\" = @t", ("t", turn)));
    }

    [Fact]
    public async Task A_chat_message_in_a_project_without_documents_has_no_snapshot()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        var posted = await Message(client, project);

        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"ChatTurns\" WHERE \"SourceSnapshotId\" IS NOT NULL"));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"SourceSnapshots\""));
    }

    [Fact]
    public void The_chat_notice_declares_unavailable_sources_without_names_and_changes_nothing_when_none()
    {
        Assert.Equal("risposta", ChatSourceNotice.Append("risposta", 0));
        var one = ChatSourceNotice.Append("risposta", 1);
        var many = ChatSourceNotice.Append("risposta\n", 3);

        Assert.StartsWith("risposta\n\nNota sulle fonti: 1 documento", one);
        Assert.Contains("3 documenti", many);
        Assert.Contains("non usa il contenuto dei documenti indisponibili", one);
    }
}
