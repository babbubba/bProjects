using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace bProjects.App.Documents;

public enum ParseRunOutcome
{
    /// <summary>Manifest completo pubblicato: la preparazione passa a Extracting (Parsed non è Ready).</summary>
    Published,

    /// <summary>Manifest con elementi non trattati pubblicato: la preparazione è Incomplete, mai Ready.</summary>
    PublishedIncomplete,

    /// <summary>Errore transitorio: il job è di nuovo Queued con backoff.</summary>
    Requeued,

    /// <summary>Errore permanente o tentativi/tempo esauriti: job e preparazione Failed.</summary>
    Failed,

    /// <summary>Autorità persa (lease, fence, deadline del padre): nulla è stato pubblicato.</summary>
    Rejected,
}

/// <summary>
/// Un tentativo di parsing di un job già in lease: originale verificato → parser → validazione del manifest → pubblicazione fenced di artefatto,
/// blocchi, porzioni e segnalazioni. Non usa il modello né lo scheduler AI e non occupa alcuno slot DGX. Il lease si rinnova ogni
/// <see cref="ExecutionOptions.RenewInterval"/> da un contesto proprio; se si perde, la chiamata al parser viene annullata e un eventuale risultato
/// tardivo non può essere pubblicato (commit sotto lock con owner, fence, lease e vita del padre).
/// </summary>
public sealed class DocumentParseRunner(
    AppDbContext db,
    DocumentJobStore jobs,
    DocumentFileStore store,
    ParserClient parser,
    DocumentPolicyService policy,
    IServiceScopeFactory scopes,
    IOptions<ExecutionOptions> execution,
    IOptions<ParserOptions> parserOptions,
    IOptions<DocumentOptions> documentOptions,
    ILogger<DocumentParseRunner> logger)
{
    /// <summary>Prende in carico il prossimo Parse eleggibile e lo esegue; null se non c'è nulla da fare (o il parser non è configurato).</summary>
    public async Task<ParseRunOutcome?> RunNextAsync(string owner, CancellationToken ct)
    {
        if (!parserOptions.Value.IsConfigured)
        {
            return null;
        }

        var claim = await jobs.ClaimNextParseAsync(owner, ct);
        return claim is null ? null : await ExecuteAsync(claim, ct);
    }

    private async Task<ParseRunOutcome> ExecuteAsync(ParseClaim claim, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewer = RenewLoopAsync(claim, lost);
        try
        {
            var outcome = await ConvertAndPublishAsync(claim, clock, lost.Token);
            // Pubblicazione negata per mancanza di autorità: il tentativo si chiude comunque. Se l'owner è ancora lui e il padre è scaduto il job
            // diventa Failed (parent_deadline_exceeded); se un altro owner ha il fence il rilascio non modifica nulla.
            return outcome == ParseRunOutcome.Rejected ? await ReleaseAsync(claim, new ParserFailure(true, "commit_rejected")) : outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // arresto del processo: il lease scade da solo e il job viene ripreso, senza consumare altro
        }
        catch (OperationCanceledException)
        {
            return ParseRunOutcome.Rejected;   // lease perso: un altro owner (o lo sweep) è l'autorità
        }
        catch (Exception ex)
        {
            var failure = Classify(ex);
            logger.LogWarning("Parsing del job {JobId} non riuscito: {Code}", claim.JobId, failure.Code);
            return await ReleaseAsync(claim, failure);
        }
        finally
        {
            await lost.CancelAsync();
            await renewer;
        }
    }

    private async Task<ParseRunOutcome> ConvertAndPublishAsync(ParseClaim claim, Stopwatch clock, CancellationToken ct)
    {
        var version = await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.Id == claim.DocumentVersionId && v.ProjectId == claim.ProjectId, ct);
        var format = Enum.GetValues<DocumentFormat>().First(f => DocumentUploadValidation.MediaTypeOf(f) == version.MediaType);
        var limits = FrozenLimits.From(claim.ConfigJson, documentOptions.Value);

        ParserResponse response;
        await using (var original = await store.OpenVerifiedAsync(version.StorageKey, version.Sha256, version.ByteLength, ct))
        {
            var request = new ParserRequest(
                original, format, version.Id, version.Sha256, version.ByteLength,
                new ParserLimits(limits.MaxExpandedBytes, limits.MaxPages, limits.MaxBlocks, limits.MaxDerivedBytes, limits.ParseTimeoutSeconds));
            response = await parser.ConvertAsync(request, claim.Remaining - clock.Elapsed, ct);
        }

        var manifest = ParsedManifestValidator.Validate(
            response.Body, new ManifestExpectations(version.Sha256, version.ByteLength, format, response.ConfigHash, limits.MaxPages, limits.MaxBlocks, limits.MaxExpandedBytes));
        return await PublishAsync(claim, manifest, response.Body, ct);
    }

    /// <summary>
    /// Pubblicazione: il file dell'artefatto (corpo grezzo del manifest, con il Docling originale) è scritto prima, poi una sola transazione corta —
    /// lock di progetto, lock di preparazione e job con verifica di autorità, righe di artefatto/blocchi/porzioni/segnalazioni, job Completed e
    /// preparazione aggiornata. Se l'autorità manca nulla è scritto e il file, non referenziato, viene rimosso.
    /// </summary>
    private async Task<ParseRunOutcome> PublishAsync(ParseClaim claim, ValidatedManifest manifest, byte[] body, CancellationToken ct)
    {
        var (_, snapshot) = await policy.GetAsync(ct);
        var used = await db.ParsedArtifacts.AsNoTracking().Where(a => a.ProjectId == claim.ProjectId).SumAsync(a => (long?)a.ByteLength, ct) ?? 0;
        if (used + body.Length > snapshot.Limits.DerivedQuotaBytes)
        {
            throw new ParserException(new(false, "derived_quota_exceeded"));
        }

        var artifactId = Guid.NewGuid();
        var stored = await store.WriteAsync(claim.ProjectId, artifactId, new MemoryStream(body, writable: false), body.Length, ct);
        var published = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ProjectLock.AcquireAsync(db, claim.ProjectId, ct);   // ordine uniforme: progetto → preparazione → job
            if (!await jobs.LockForCommitAsync(claim, ct))
            {
                return ParseRunOutcome.Rejected;
            }

            var rows = Rows.Build(claim, manifest, artifactId, stored);
            db.ParsedArtifacts.Add(rows.Artifact);
            db.DocumentBlocks.AddRange(rows.Blocks);
            db.DocumentPortions.AddRange(rows.Portions);
            db.PreparationIssues.AddRange(rows.Issues);
            await db.SaveChangesAsync(ct);
            var status = manifest.Status == ManifestStatus.Complete ? PreparationStatus.Extracting : PreparationStatus.Incomplete;
            await jobs.CompleteParseAsync(claim, rows.ResultHash, status, manifest.ParserVersion, manifest.ConfigHash, stored.Sha256, ct);
            await transaction.CommitAsync(ct);
            published = true;
            return status == PreparationStatus.Extracting ? ParseRunOutcome.Published : ParseRunOutcome.PublishedIncomplete;
        }
        finally
        {
            db.ChangeTracker.Clear();
            if (!published)
            {
                await RemoveOrphanAsync(stored.StorageKey);
            }
        }
    }

    private async Task RemoveOrphanAsync(string storageKey)
    {
        try
        {
            await store.DeleteOrphanAsync(storageKey, async (key, token) => await db.ParsedArtifacts.AsNoTracking().AnyAsync(a => a.StorageKey == key, token), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DocumentStoreException or IOException or UnauthorizedAccessException or DbException)
        {
            logger.LogWarning("Artefatto non pubblicato non rimosso: {ExceptionType}", ex.GetType().Name);   // lo raccoglie la pulizia periodica
        }
    }

    private async Task<ParseRunOutcome> ReleaseAsync(ParseClaim claim, ParserFailure failure)
    {
        db.ChangeTracker.Clear();
        var released = await jobs.ReleaseAfterFailureAsync(claim, failure, CancellationToken.None);
        return released switch
        {
            ParseRelease.Requeued => ParseRunOutcome.Requeued,
            ParseRelease.Failed => ParseRunOutcome.Failed,
            _ => ParseRunOutcome.Rejected,
        };
    }

    /// <summary>Classificazione di un errore del tentativo: transitorio (ritentabile entro tentativi e tempo) o permanente, con codice stabile e mai testo del documento.</summary>
    private static ParserFailure Classify(Exception ex) => ex switch
    {
        ParserException parserFailure => parserFailure.Failure,
        ManifestRejectedException rejected => new(false, rejected.Code),
        DocumentStoreException { Reason: DocumentStoreFailure.IntegrityFailure } => new(false, "original_integrity_failed"),
        DocumentStoreException { Reason: DocumentStoreFailure.NotFound } => new(false, "original_missing"),
        DocumentStoreException { Reason: DocumentStoreFailure.InsufficientSpace or DocumentStoreFailure.Unavailable } => new(true, "storage_unavailable"),
        DocumentStoreException => new(false, "storage_error"),
        DbException { IsTransient: true } or TimeoutException or IOException => new(true, "infrastructure_unavailable"),
        DbUpdateException { InnerException: DbException { IsTransient: true } } => new(true, "infrastructure_unavailable"),
        _ => new(false, "parse_internal_error"),
    };

    /// <summary>Rinnovo del lease da un contesto di database proprio (il contesto del runner è in uso). La perdita del lease annulla il lavoro in corso.</summary>
    private async Task RenewLoopAsync(ParseClaim claim, CancellationTokenSource lost)
    {
        try
        {
            using var timer = new PeriodicTimer(execution.Value.RenewInterval);
            while (await timer.WaitForNextTickAsync(lost.Token))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    if (!await scope.ServiceProvider.GetRequiredService<DocumentJobStore>().RenewAsync(claim, lost.Token))
                    {
                        await lost.CancelAsync();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Guasto momentaneo del database: si riprova al battito successivo; se il lease scade vince il reclaim e il fence.
                    logger.LogWarning("Rinnovo del lease del job {JobId} fallito: {ExceptionType}", claim.JobId, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // fine dell'esecuzione
        }
    }

    /// <summary>Limiti congelati nella configurazione del job alla richiesta; in assenza di un campo valgono i default correnti.</summary>
    private sealed record FrozenLimits(int ParseTimeoutSeconds, int MaxPages, int MaxBlocks, long MaxExpandedBytes, long MaxDerivedBytes)
    {
        public static FrozenLimits From(string? json, DocumentOptions fallback)
        {
            long Read(JsonElement root, string name, long otherwise) =>
                root.ValueKind == JsonValueKind.Object
                    ? root.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                        .Select(p => p.Value.GetInt64()).DefaultIfEmpty(otherwise).First()
                    : otherwise;

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement;
            return new(
                (int)Read(root, "ParseTimeoutSeconds", fallback.ParseTimeoutSeconds), (int)Read(root, "MaxPages", fallback.MaxPages),
                (int)Read(root, "MaxBlocks", fallback.MaxBlocks), Read(root, "MaxExpandedBytes", fallback.MaxExpandedBytes),
                Read(root, "MaxDerivedBytes", fallback.MaxDerivedBytes));
        }
    }

    /// <summary>Righe da persistere per un manifest validato.</summary>
    private sealed class Rows
    {
        public required ParsedArtifact Artifact { get; init; }
        public required List<DocumentBlock> Blocks { get; init; }
        public required List<DocumentPortion> Portions { get; init; }
        public required List<PreparationIssue> Issues { get; init; }
        public required string ResultHash { get; init; }

        public static Rows Build(ParseClaim claim, ValidatedManifest manifest, Guid artifactId, StoredDocumentFile stored)
        {
            var ids = manifest.Blocks.ToDictionary(b => b.Ref, _ => Guid.NewGuid(), StringComparer.Ordinal);
            var blocks = manifest.Blocks.Select(b => new DocumentBlock
            {
                Id = ids[b.Ref], ProjectId = claim.ProjectId, ArtifactId = artifactId, Ordinal = b.Ordinal, Ref = b.Ref,
                ParentId = b.ParentRef is null ? null : ids[b.ParentRef], Type = b.Type, CanonicalText = b.Text, TextHash = b.TextHash,
                HeadingPathJson = JsonSerializer.Serialize(b.HeadingPath), Page = b.Page, BboxJson = b.Bbox is null ? null : JsonSerializer.Serialize(b.Bbox),
                CoordinateSystem = b.CoordinateSystem, DoclingRef = b.DoclingRef, LineStart = b.LineStart, LineEnd = b.LineEnd, TableCellsJson = b.TableCellsJson,
            }).ToList();

            var portions = new List<DocumentPortion>();
            foreach (var chunk in manifest.Chunks)
            {
                var owned = chunk.Ranges.Where(r => r.Ownership == RangeOwnership.Primary).ToList();
                if (owned.Count == 0)
                {
                    continue;   // un gruppo di solo contesto non possiede testo: non è un'unità di estrazione
                }

                portions.Add(new DocumentPortion
                {
                    Id = Guid.NewGuid(), ProjectId = claim.ProjectId, PreparationId = claim.PreparationId, Ordinal = portions.Count,
                    RangesJson = RangesJson(owned, ids),
                    ContextRangesJson = RangesJson(chunk.Ranges.Where(r => r.Ownership == RangeOwnership.Context), ids),
                    TextHash = Sha256(string.Join('\n', owned.Select(r => r.TextHash))),
                    Status = PortionStatus.Pending,
                });
            }

            var issues = manifest.Issues.Select(i => new PreparationIssue
            {
                Id = Guid.NewGuid(), ProjectId = claim.ProjectId, PreparationId = claim.PreparationId, BlockId = i.BlockRef is null ? null : ids[i.BlockRef],
                Page = i.Page, Code = i.Code, Severity = i.Severity, Reason = i.Reason, AffectsCompleteness = i.AffectsCompleteness,
            }).ToList();

            var artifact = new ParsedArtifact
            {
                Id = artifactId, ProjectId = claim.ProjectId, PreparationId = claim.PreparationId, JobId = claim.JobId, FencingToken = claim.FencingToken,
                SchemaVersion = ParserClient.SchemaVersion, StorageKey = stored.StorageKey, Sha256 = stored.Sha256, ByteLength = stored.ByteLength,
                ParserConfigHash = manifest.ConfigHash, BlockCount = blocks.Count, IssueCount = issues.Count,
            };
            var resultHash = Sha256($"{stored.Sha256}|{manifest.Status}|{blocks.Count}|{portions.Count}|{issues.Count}");
            return new() { Artifact = artifact, Blocks = blocks, Portions = portions, Issues = issues, ResultHash = resultHash };
        }

        private static string RangesJson(IEnumerable<ValidatedRange> ranges, Dictionary<string, Guid> ids) =>
            JsonSerializer.Serialize(ranges.Select(r => new { rangeKey = r.RangeKey, blockId = ids[r.BlockRef], blockRef = r.BlockRef, start = r.Start, end = r.End, textHash = r.TextHash }));

        private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>
/// Piccolo BackgroundService CPU del parsing (plan.md §Lifecycle): un solo job alla volta per istanza, indipendente dal dispatcher AI. Senza
/// parser configurato non prende in carico nulla e i job restano Queued. Gli errori di un ciclo non fermano il servizio e non scrivono contenuti nei log.
/// </summary>
public sealed class DocumentParseWorker(IServiceScopeFactory scopes, IOptions<ParserOptions> parserOptions, ILogger<DocumentParseWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(2);

    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}:parse";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!parserOptions.Value.IsConfigured)
        {
            logger.LogInformation("Parser documentale non configurato (Parser:BaseUrl): i job di parsing restano in coda.");
            return;
        }

        var idle = TimeSpan.FromSeconds(Math.Max(1, parserOptions.Value.PollSeconds));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var outcome = await scope.ServiceProvider.GetRequiredService<DocumentParseRunner>().RunNextAsync(_owner, stoppingToken);
                    if (outcome is null)
                    {
                        await Task.Delay(idle, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Ciclo di parsing fallito: {ExceptionType}", ex.GetType().Name);
                    await Task.Delay(ErrorDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // arresto richiesto
        }
    }
}

public static class DocumentParsingRegistration
{
    /// <summary>Registra parser client, store dei job, runner e worker CPU. Con <c>Parser:BaseUrl</c> vuoto il worker non parte.</summary>
    public static IServiceCollection AddDocumentParsing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ParserOptions>().Bind(configuration.GetSection(ParserOptions.SectionName));
        services.AddHttpClient<ParserClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);   // il budget è del client (min(timeout, residuo deadline))
        services.AddScoped<DocumentJobStore>();
        services.AddScoped<DocumentParseRunner>();
        services.AddHostedService<DocumentParseWorker>();
        return services;
    }
}
