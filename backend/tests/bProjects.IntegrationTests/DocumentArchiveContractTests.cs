using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>
/// T008 (DET): contratto HTTP dell'archivio documenti (contracts/http-api.md §Documenti) su API reale + PostgreSQL + filesystem isolato:
/// POST documents / versions, GET lista / dettaglio / versione / download, idempotenza 201/200/409, errori sincroni e identità separata
/// dal nome. Scritte prima dell'implementazione (T012–T014) e ora eseguite contro il servizio reale: il PASS integrato è il gate T018.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class DocumentArchiveContractTests : ArchiveTestBase
{
    protected override void Tune(DocumentOptions options)
    {
        options.MaxFileBytes = 5_000;   // 413 provabile senza inviare 50 MB
        options.ProjectQuotaBytes = 1_000_000;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ================================================================ POST nominale e idempotenza

    [Fact]
    public async Task Posting_a_new_document_is_201_with_an_acquired_version_a_queued_preparation_and_no_server_paths()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var bytes = Text("prima versione");

        var response = await Upload(client, project, bytes, "Requisiti cliente.txt", displayName: "Requisiti");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var view = JsonDocument.Parse(body).RootElement;
        Assert.Equal(project, view.GetProperty("projectId").GetGuid());
        Assert.Equal(1, view.GetProperty("version").GetInt32());
        Assert.Equal("Requisiti cliente.txt", view.GetProperty("originalName").GetString());
        Assert.Equal(bytes.Length, view.GetProperty("byteLength").GetInt64());
        Assert.Equal(Sha(bytes), view.GetProperty("sha256").GetString());
        Assert.Equal("Queued", view.GetProperty("processingStatus").GetString());   // acquisito e in coda: nessuna promessa Ready
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("latestJobId").ValueKind);
        Assert.True(view.TryGetProperty("uploadedAt", out _));
        Assert.DoesNotContain("storageKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(StorageRoot, body);
        Assert.NotNull(response.Headers.Location);

        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"Status\" = 'Queued'"));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"Status\" = 'Queued'"));
        Assert.Single(StoredObjects());
    }

    [Fact]
    public async Task Replaying_the_same_key_and_input_is_200_with_the_same_version_and_enqueues_nothing_new()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Text("replay");
        var first = await Upload(client, project, bytes, key: key);
        var created = await Json(first);

        var replay = await Upload(client, project, bytes, key: key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var again = await Json(replay);
        Assert.Equal(created.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.Equal(created.GetProperty("documentId").GetGuid(), again.GetProperty("documentId").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Single(StoredObjects());
    }

    [Fact]
    public async Task The_same_key_with_different_bytes_metadata_or_target_is_409_idempotency_conflict_and_changes_nothing()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var key = Guid.NewGuid();
        var bytes = Text("originale");
        var created = await Json(await Upload(client, project, bytes, "a.txt", key: key, displayName: "Alfa"));
        var other = await Json(await Upload(client, project, Text("altro documento")));

        var differentBytes = await Upload(client, project, Text("byte diversi"), "a.txt", key: key, displayName: "Alfa");
        var differentName = await Upload(client, project, bytes, "b.txt", key: key, displayName: "Alfa");
        var differentLabel = await Upload(client, project, bytes, "a.txt", key: key, displayName: "Beta");
        var differentTarget = await Upload(client, project, bytes, "a.txt", key: key, documentId: other.GetProperty("documentId").GetGuid());

        foreach (var response in new[] { differentBytes, differentName, differentLabel, differentTarget })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("idempotency_conflict", (await Json(response)).GetProperty("code").GetString());
        }

        Assert.Equal(2, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Equal(2, await Count("SELECT count(*) FROM \"DocumentJobs\""));
        Assert.Equal(2, StoredObjects().Length);
        Assert.Equal(created.GetProperty("sha256").GetString(), Sha(bytes));
    }

    [Fact]
    public async Task A_new_version_keeps_the_document_identity_gets_the_next_number_and_never_replaces_earlier_bytes()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var v1 = Text("versione uno");
        var v2 = Text("versione due, più lunga", 400);
        var first = await Json(await Upload(client, project, v1, "spec.txt"));
        var document = first.GetProperty("documentId").GetGuid();

        var response = await Upload(client, project, v2, "spec-rev2.txt", documentId: document);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var second = await Json(response);
        Assert.Equal(document, second.GetProperty("documentId").GetGuid());
        Assert.Equal(2, second.GetProperty("version").GetInt32());
        Assert.NotEqual(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"ProjectDocuments\""));
        Assert.Equal(2, StoredObjects().Length);
        var download1 = await client.GetAsync($"/api/projects/{project}/documents/{document}/versions/1/download", Ct);
        var download2 = await client.GetAsync($"/api/projects/{project}/documents/{document}/versions/2/download", Ct);
        Assert.Equal(v1, await download1.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(v2, await download2.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Two_uploads_with_the_same_file_name_are_two_distinct_documents_the_name_is_not_the_identity()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        var one = await Json(await Upload(client, project, Text("uno"), "verbale.txt", displayName: "Verbale"));
        var two = await Json(await Upload(client, project, Text("due"), "verbale.txt", displayName: "Verbale"));

        Assert.NotEqual(one.GetProperty("documentId").GetGuid(), two.GetProperty("documentId").GetGuid());
        Assert.Equal(1, one.GetProperty("version").GetInt32());
        Assert.Equal(1, two.GetProperty("version").GetInt32());
        Assert.Equal(2, await Count("SELECT count(*) FROM \"ProjectDocuments\""));
    }

    // ================================================================ GET

    [Fact]
    public async Task Listing_is_cursor_paginated_with_limit_one_to_one_hundred_and_never_starts_work()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await Json(await Upload(client, project, Text($"doc {i}"), $"d{i}.txt", displayName: $"Doc {i}"))).GetProperty("documentId").GetGuid());
        }

        var jobsBefore = await Count("SELECT count(*) FROM \"DocumentJobs\"");
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/api/projects/{project}/documents?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await Json(await client.GetAsync(url, Ct));
            var items = page.GetProperty("items").EnumerateArray().ToList();
            Assert.InRange(items.Count, 1, 2);
            seen.AddRange(items.Select(i => i.GetProperty("documentId").GetGuid()));
            Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("displayName").GetString())));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(ids.OrderBy(i => i), seen.OrderBy(i => i));
        Assert.Equal(seen.Count, seen.Distinct().Count());   // nessun duplicato fra pagine
        foreach (var bad in new[] { 0, 101, -1 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{project}/documents?limit={bad}", Ct)).StatusCode);
        }

        Assert.Equal(jobsBefore, await Count("SELECT count(*) FROM \"DocumentJobs\""));   // GET non avvia lavori
    }

    [Fact]
    public async Task Detail_and_version_views_expose_versions_and_the_download_returns_identical_bytes_with_safe_headers()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var bytes = Text("scarico identico", 1_500);
        var created = await Json(await Upload(client, project, bytes, "..\\..\\evil/report.txt", displayName: "Report"));
        var document = created.GetProperty("documentId").GetGuid();

        var detail = await Json(await client.GetAsync($"/api/projects/{project}/documents/{document}", Ct));
        Assert.Equal(document, detail.GetProperty("documentId").GetGuid());
        Assert.Single(detail.GetProperty("versions").EnumerateArray());
        var version = await client.GetAsync($"/api/projects/{project}/documents/{document}/versions/1", Ct);
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal(Sha(bytes), (await Json(version)).GetProperty("sha256").GetString());

        var download = await client.GetAsync($"/api/projects/{project}/documents/{document}/versions/1/download", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        var fileName = download.Content.Headers.ContentDisposition.FileNameStar ?? download.Content.Headers.ContentDisposition.FileName!;
        Assert.False(string.IsNullOrWhiteSpace(fileName));
        Assert.DoesNotContain('/', fileName);
        Assert.DoesNotContain('\\', fileName);
        Assert.StartsWith("text/plain", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains("nosniff", download.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("no-store", string.Join(',', download.Headers.GetValues("Cache-Control")));
        Assert.Null(download.Headers.Location);
    }

    [Fact]
    public async Task A_download_of_altered_bytes_is_refused_not_served()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var created = await Json(await Upload(client, project, Text("integro"), "i.txt"));
        var file = Assert.Single(StoredObjects());
        File.SetAttributes(file, FileAttributes.Normal);
        File.AppendAllText(file, "manomesso");

        var download = await client.GetAsync($"/api/projects/{project}/documents/{created.GetProperty("documentId").GetGuid()}/versions/1/download", Ct);

        Assert.True((int)download.StatusCode >= 500, $"atteso errore server, ricevuto {(int)download.StatusCode}");
        Assert.DoesNotContain("manomesso", await download.Content.ReadAsStringAsync(Ct));
    }

    // ================================================================ isolamento fra progetti e autorizzazione

    [Fact]
    public async Task Ids_of_another_project_are_404_for_every_route()
    {
        using var client = await App.LoginAsync();
        var owner = await NewProject(client);
        var intruder = await NewProject(client);
        var created = await Json(await Upload(client, owner, Text("riservato"), "r.txt"));
        var document = created.GetProperty("documentId").GetGuid();

        string[] urls =
        [
            $"/api/projects/{intruder}/documents/{document}",
            $"/api/projects/{intruder}/documents/{document}/versions/1",
            $"/api/projects/{intruder}/documents/{document}/versions/1/download",
        ];
        foreach (var url in urls)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url, Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Upload(client, intruder, Text("x"), documentId: document)).StatusCode);
        Assert.Empty((await Json(await client.GetAsync($"/api/projects/{intruder}/documents", Ct))).GetProperty("items").EnumerateArray());
        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
    }

    [Fact]
    public async Task The_same_bytes_in_two_projects_never_merge_documents_or_share_storage()
    {
        using var client = await App.LoginAsync();
        var a = await NewProject(client);
        var b = await NewProject(client);
        var bytes = Text("contenuto identico");

        var first = await Json(await Upload(client, a, bytes, "x.txt"));
        var second = await Json(await Upload(client, b, bytes, "x.txt"));

        Assert.Equal(first.GetProperty("sha256").GetString(), second.GetProperty("sha256").GetString());
        Assert.NotEqual(first.GetProperty("documentId").GetGuid(), second.GetProperty("documentId").GetGuid());
        Assert.Equal(2, StoredObjects().Length);
        Assert.Equal(2, await Count("SELECT count(DISTINCT \"StorageKey\") FROM \"DocumentVersions\""));
    }

    [Fact]
    public async Task Authentication_role_and_unknown_project_are_401_403_404()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(App.Anonymous(), project, Text("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.Anonymous().GetAsync($"/api/projects/{project}/documents", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(App.WithoutPmRole(), project, Text("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(client, Guid.NewGuid(), Text("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project}/documents/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Empty(StoredObjects());
    }

    // ================================================================ errori sincroni §Documenti

    [Fact]
    public async Task Malformed_requests_are_400_without_storing_anything()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        var noKey = await Upload(client, project, Text("x"), withKey: false);
        var badKeyRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/documents")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(Text("x")), "file", "x.txt" } },
        };
        badKeyRequest.Headers.Add("Idempotency-Key", "non-un-uuid");
        var badKey = await client.SendAsync(badKeyRequest, Ct);
        var notMultipart = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/documents") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        notMultipart.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var json = await client.SendAsync(notMultipart, Ct);
        var noFileRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project}/documents")
        {
            Content = new MultipartFormDataContent { { new StringContent("Solo etichetta"), "displayName" } },
        };
        noFileRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var noFile = await client.SendAsync(noFileRequest, Ct);

        Assert.All(new[] { noKey, badKey, json, noFile }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task Oversize_unsupported_empty_and_macro_files_are_413_415_422_with_clean_errors_and_earlier_originals_stay_downloadable()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);
        var good = Text("già acquisito");
        var created = await Json(await Upload(client, project, good, "ok.txt"));
        var document = created.GetProperty("documentId").GetGuid();

        var oversize = await Upload(client, project, new byte[5_001].Select(_ => (byte)'a').ToArray(), "grande.txt", documentId: document);
        var unsupported = await Upload(client, project, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00], "programma.exe", "application/octet-stream");
        var empty = await Upload(client, project, [], "vuoto.txt");
        var macro = await Upload(client, project, Text("PK finto"), "macro.docm", "application/vnd.ms-word.document.macroEnabled.12");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversize.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Contains((int)macro.StatusCode, new[] { 415, 422 });
        foreach (var response in new[] { oversize, unsupported, empty, macro })
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(StorageRoot, text);
            Assert.DoesNotContain("   at ", text);   // nessuno stack trace
            Assert.DoesNotContain("storageKey", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Single(StoredObjects());
        Assert.Empty(TemporaryFiles());
        var download = await client.GetAsync($"/api/projects/{project}/documents/{document}/versions/1/download", Ct);
        Assert.Equal(good, await download.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task A_file_whose_extension_does_not_match_its_content_is_415_not_stored()
    {
        using var client = await App.LoginAsync();
        var project = await NewProject(client);

        // Estensione .pdf ma nessuna firma %PDF-: formato incoerente → 415, nulla di acquisito.
        var response = await Upload(client, project, Text("non sono un pdf"), "finto.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, await Count("SELECT count(*) FROM \"DocumentVersions\""));
        Assert.Empty(StoredObjects());
    }
}
