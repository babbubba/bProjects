using System.Net.Http.Json;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>Seed e helper condivisi dei test di lifecycle delle versioni file (T093) e dei derivati T096-T098: stessi stati intermedi dell'aggregatore (T051), nessun modello.</summary>
public abstract class LifecycleTestBase : ParseRetryTestBase
{
    protected sealed record Generation(Guid Preparation, Guid ParseJob, Guid? EvidenceVersion, int Number);

    protected static string PrepUrl(Target t, string suffix = "") => $"/api/projects/{t.Project}/documents/{t.Document}/versions/1/preparations{suffix}";

    protected Task<HttpResponseMessage> PostPreparations(HttpClient client, Target t, Guid? key = null, bool withKey = true, Guid? project = null, Guid? document = null, string version = "1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project ?? t.Project}/documents/{document ?? t.Document}/versions/{version}/preparations");
        if (withKey)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        return client.SendAsync(request, Ct);
    }

    protected Task<HttpResponseMessage> Promote(HttpClient client, Target t, string generation, object? body, Guid? project = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project ?? t.Project}/documents/{t.Document}/versions/1/preparations/{generation}/promote");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request, Ct);
    }

    protected static object Expect(int revision, string? reason = null) => new { expectedRevision = revision, reason };

    // ---- seed ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Porta in una generazione dello stato dato. La 1 esiste già (acquisizione); le successive sono inserite come le lascerebbe il servizio.
    /// Ready scrive aggregatore, versione Evidence e collega la preparazione; Failed/Incomplete/Extracting restano senza versione Evidence.
    /// </summary>
    protected async Task<Generation> SeedGenerationAsync(Target t, int number, PreparationStatus status, bool issue = false)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        DocumentPreparation preparation;
        DocumentJob parse;
        if (number == 1)
        {
            preparation = await db.DocumentPreparations.SingleAsync(p => p.Id == t.Preparation, Ct);
            parse = await db.DocumentJobs.SingleAsync(j => j.Id == t.Job, Ct);
        }
        else
        {
            preparation = new DocumentPreparation
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, DocumentVersionId = t.Version, Generation = number, Status = PreparationStatus.Queued,
                PolicyHash = new string('e', 64), RequestedAt = now, HardDeadlineAt = now.AddHours(1), EffectiveDeadlineAt = now.AddHours(1), CreatedAt = now,
            };
            db.DocumentPreparations.Add(preparation);
            await db.SaveChangesAsync(Ct);
            parse = new DocumentJob
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, PreparationId = preparation.Id, Kind = DocumentJobKind.Parse, Status = DocumentJobStatus.Queued,
                RequestKey = Guid.NewGuid(), RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3,
            };
            db.DocumentJobs.Add(parse);
            await db.SaveChangesAsync(Ct);
            preparation.ParseJobId = parse.Id;
            await db.SaveChangesAsync(Ct);
        }

        if (status != PreparationStatus.Queued)
        {
            parse.Status = DocumentJobStatus.Completed;
            parse.ResultHash = new string('a', 64);
            parse.StartedAt = now;
            parse.CompletedAt = now;
            parse.AttemptCount = 1;
            parse.FencingToken = 1;
            await db.SaveChangesAsync(Ct);
        }

        Guid? evidenceVersion = null;
        if (status == PreparationStatus.Ready)
        {
            var job = new EvidenceExtractionJob
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, Reason = ExtractionJobReason.Initial,
                Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
            };
            db.EvidenceExtractionJobs.Add(job);
            await db.SaveChangesAsync(Ct);
            var version = new EvidenceExtractionVersion
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = preparation.Id, ManifestHash = new string((char)('0' + number), 64),
                Version = 1, JobId = job.Id, SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}",
                EvidenceCount = 0, NoEvidenceReason = "documento senza evidenze", CreatedAt = now,
            };
            db.EvidenceExtractionVersions.Add(version);
            await db.SaveChangesAsync(Ct);
            evidenceVersion = version.Id;
            preparation.CurrentEvidenceVersionId = version.Id;
        }

        preparation.Status = status;
        if (issue)
        {
            db.PreparationIssues.Add(new PreparationIssue
            {
                Id = Guid.NewGuid(), ProjectId = t.Project, PreparationId = preparation.Id, Code = "image_unprocessed", Severity = IssueSeverity.Blocking,
                Reason = "Immagine non trattata", AffectsCompleteness = true,
            });
        }

        if (status is PreparationStatus.Failed or PreparationStatus.Incomplete)
        {
            preparation.ErrorCode = status == PreparationStatus.Failed ? "extraction_failed" : "incomplete_coverage";
            preparation.ErrorMessage = "Estrazione non riuscita.";
        }

        await db.SaveChangesAsync(Ct);
        return new(preparation.Id, parse.Id, evidenceVersion, number);
    }

    /// <summary>Stato corrente della versione file, come lo lascia la pubblicazione automatica della prima preparazione completa (revisione 1).</summary>
    protected async Task MakeCurrentAsync(Target t, Generation g)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = t.Version, ProjectId = t.Project, CurrentPreparationId = g.Preparation, CurrentEvidenceVersionId = g.EvidenceVersion, Revision = 1, UpdatedAt = now,
        });
        db.DocumentPromotions.Add(new DocumentPromotion
        {
            Id = Guid.NewGuid(), ProjectId = t.Project, DocumentVersionId = t.Version, FromPreparationId = null, FromEvidenceVersionId = null,
            ToPreparationId = g.Preparation, ToEvidenceVersionId = g.EvidenceVersion!.Value, PromotedBy = null, PromotedAt = now, Reason = EvidencePromotion.FirstValidVersionReason,
        });
        await db.SaveChangesAsync(Ct);
        await Execute(
            "UPDATE \"ProjectDocuments\" SET \"LatestReadyVersionId\" = @v WHERE \"Id\" = @d", ("v", t.Version), ("d", t.Document));
    }

    /// <summary>v1 con g1 Ready e corrente (revisione 1) e le generazioni successive indicate come candidate.</summary>
    protected async Task<(Target Target, Generation[] Generations)> CurrentWithCandidatesAsync(HttpClient client, params PreparationStatus[] candidates)
    {
        var t = await NewTarget(client);
        var first = await SeedGenerationAsync(t, 1, PreparationStatus.Ready);
        await MakeCurrentAsync(t, first);
        var all = new List<Generation> { first };
        for (var i = 0; i < candidates.Length; i++)
        {
            all.Add(await SeedGenerationAsync(t, i + 2, candidates[i], issue: false));
        }

        return (t, [.. all]);
    }

    protected async Task<(Guid? Preparation, Guid? Evidence, int Revision)> State(Target t)
    {
        var revision = await Scalar<int>("SELECT COALESCE((SELECT \"Revision\" FROM \"EvidenceDocumentStates\" WHERE \"DocumentVersionId\" = @v), -1)", ("v", t.Version));
        var prep = await Scalar<string>("SELECT COALESCE((SELECT \"CurrentPreparationId\"::text FROM \"EvidenceDocumentStates\" WHERE \"DocumentVersionId\" = @v), '')", ("v", t.Version));
        var evidence = await Scalar<string>("SELECT COALESCE((SELECT \"CurrentEvidenceVersionId\"::text FROM \"EvidenceDocumentStates\" WHERE \"DocumentVersionId\" = @v), '')", ("v", t.Version));
        return (prep.Length == 0 ? null : Guid.Parse(prep), evidence.Length == 0 ? null : Guid.Parse(evidence), revision);
    }

    protected Task<string> StateRowJson(Target t) => Scalar<string>("SELECT row_to_json(t)::text FROM \"EvidenceDocumentStates\" t WHERE \"DocumentVersionId\" = @v", ("v", t.Version));

    protected Task<long> Promotions(Target t) => Count("SELECT count(*) FROM \"DocumentPromotions\" WHERE \"DocumentVersionId\" = @v", ("v", t.Version));

    protected Task<long> Preparations(Target t) => Count("SELECT count(*) FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", t.Version));

    protected Task<long> Jobs(Target t) => Count("SELECT count(*) FROM \"DocumentJobs\" WHERE \"ProjectId\" = @p", ("p", t.Project));

    protected async Task<Target> UploadNextVersionAsync(HttpClient client, Target t, string text)
    {
        var created = await Json(await Upload(client, t.Project, Text(text), "nota.txt", "text/plain", documentId: t.Document));
        var version = created.GetProperty("id").GetGuid();
        var preparation = await Scalar<Guid>("SELECT \"Id\" FROM \"DocumentPreparations\" WHERE \"DocumentVersionId\" = @v", ("v", version));
        var job = await Scalar<Guid>("SELECT \"ParseJobId\" FROM \"DocumentPreparations\" WHERE \"Id\" = @p", ("p", preparation));
        return new(t.Project, t.Document, version, preparation, job);
    }

    protected async Task<JsonElement> SelectionItemAsync(HttpClient client, Target t)
    {
        var selection = await Json(await client.GetAsync($"/api/projects/{t.Project}/source-selection", Ct));
        return selection.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("documentId").GetGuid() == t.Document).Clone();
    }

    protected async Task<HttpResponseMessage> PutSelectionAsync(HttpClient client, Target t, object item)
    {
        var revision = (await Json(await client.GetAsync($"/api/projects/{t.Project}/source-selection", Ct))).GetProperty("revision").GetInt64();
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{t.Project}/source-selection") { Content = JsonContent.Create(new { items = new[] { item } }) };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return await client.SendAsync(put, Ct);
    }
}
