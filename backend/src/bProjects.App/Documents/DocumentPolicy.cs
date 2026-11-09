using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace bProjects.App.Documents;

/// <summary>
/// Limiti non segreti, immutabili e validati. Il record è un valore: chi ne prende una copia (snapshot di una richiesta) non vede
/// modifiche successive della policy. L'ordine e i nomi delle proprietà fanno parte dell'hash.
/// </summary>
public sealed record DocumentLimits(
    long MaxFileBytes,
    long ProjectQuotaBytes,
    long DerivedQuotaBytes,
    long MaxExpandedBytes,
    int MaxPages,
    int MaxBlocks,
    int ParseTimeoutSeconds,
    int UploadTimeoutSeconds,
    int PreparationDeadlineSeconds,
    int OrdinaryDeadlineSeconds,
    int ExtendedDeadlineSeconds,
    int MaxAiCallsPerPreparation,
    int MaxAiCallsPerRun,
    long MaxDerivedBytes,
    int MaxReconciliationLevels,
    int MaxCandidatesPerRun)
{
    /// <summary>Tetto finito dei timer (24 h): un valore enorme equivale a «nessun limite», che il Plan non ammette.</summary>
    public const int MaxDeadlineSeconds = 86_400;

    /// <summary>Limite per file: non oltre il corpo che il proxy accetta (client_max_body_size 52m in infra/web.conf).</summary>
    public const long MaxFileBytesCeiling = 52_000_000;

    /// <summary>Massimo dei byte espansi di un contenitore (DOCX): oltre è una configurazione che esaurirebbe memoria o disco.</summary>
    public const long MaxExpandedBytesCeiling = 1_000_000_000;

    /// <summary>Massimo di quota per progetto (originali o derivati): 1 TB.</summary>
    public const long MaxQuotaBytesCeiling = 1_000_000_000_000;

    /// <summary>Soglia minima del regime Extended (Plan: 180 s di base).</summary>
    public const int MinExtendedSeconds = 180;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Corpo del PUT Admin: campi sconosciuti (es. PromotionAfterSeconds) o mancanti sono errori, mai ignorati.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Violazioni nel formato «campo: descrizione»; vuoto se i limiti sono coerenti.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Positive(string field, long value)
        {
            if (value <= 0)
            {
                errors.Add($"{field}: deve essere positivo.");
            }
        }

        void Ceiling(string field, long value, long ceiling)
        {
            if (value > ceiling)
            {
                errors.Add($"{field}: non può superare {ceiling} byte.");
            }
        }

        void Timer(string field, int value)
        {
            if (value <= 0 || value > MaxDeadlineSeconds)
            {
                errors.Add($"{field}: deve essere finito, tra 1 e {MaxDeadlineSeconds} secondi.");
            }
        }

        Positive(nameof(MaxFileBytes), MaxFileBytes);
        Positive(nameof(ProjectQuotaBytes), ProjectQuotaBytes);
        Positive(nameof(DerivedQuotaBytes), DerivedQuotaBytes);
        Positive(nameof(MaxExpandedBytes), MaxExpandedBytes);

        // Tetti assoluti: il proxy (infra/web.conf, client_max_body_size 52m) rifiuta corpi oltre 52 MB, quindi un limite per file più alto
        // sarebbe inattuabile; espansione e quote hanno un massimo ragionevole contro configurazioni che esaurirebbero disco o memoria.
        Ceiling(nameof(MaxFileBytes), MaxFileBytes, MaxFileBytesCeiling);
        Ceiling(nameof(MaxExpandedBytes), MaxExpandedBytes, MaxExpandedBytesCeiling);
        Ceiling(nameof(ProjectQuotaBytes), ProjectQuotaBytes, MaxQuotaBytesCeiling);
        Ceiling(nameof(DerivedQuotaBytes), DerivedQuotaBytes, MaxQuotaBytesCeiling);
        Ceiling(nameof(MaxDerivedBytes), MaxDerivedBytes, MaxQuotaBytesCeiling);
        Positive(nameof(MaxPages), MaxPages);
        Positive(nameof(MaxBlocks), MaxBlocks);
        Positive(nameof(MaxAiCallsPerPreparation), MaxAiCallsPerPreparation);
        Positive(nameof(MaxAiCallsPerRun), MaxAiCallsPerRun);
        Positive(nameof(MaxDerivedBytes), MaxDerivedBytes);
        Positive(nameof(MaxReconciliationLevels), MaxReconciliationLevels);
        Positive(nameof(MaxCandidatesPerRun), MaxCandidatesPerRun);
        Timer(nameof(ParseTimeoutSeconds), ParseTimeoutSeconds);
        Timer(nameof(UploadTimeoutSeconds), UploadTimeoutSeconds);
        Timer(nameof(PreparationDeadlineSeconds), PreparationDeadlineSeconds);
        Timer(nameof(OrdinaryDeadlineSeconds), OrdinaryDeadlineSeconds);
        Timer(nameof(ExtendedDeadlineSeconds), ExtendedDeadlineSeconds);

        // Coerenza fra i limiti: un file singolo deve poter entrare nella quota; Extended è almeno 180 s e non inferiore a Ordinary.
        if (MaxFileBytes > 0 && ProjectQuotaBytes > 0 && MaxFileBytes > ProjectQuotaBytes)
        {
            errors.Add($"{nameof(MaxFileBytes)}: non può superare {nameof(ProjectQuotaBytes)}.");
        }

        if (ExtendedDeadlineSeconds is > 0 and < MinExtendedSeconds)
        {
            errors.Add($"{nameof(ExtendedDeadlineSeconds)}: deve essere almeno {MinExtendedSeconds} secondi.");
        }

        if (ExtendedDeadlineSeconds > 0 && OrdinaryDeadlineSeconds > ExtendedDeadlineSeconds)
        {
            errors.Add($"{nameof(OrdinaryDeadlineSeconds)}: non può superare {nameof(ExtendedDeadlineSeconds)}.");
        }

        return errors;
    }

    /// <summary>Serializzazione canonica (camelCase, ordine fisso): base dell'hash e del JSONB persistito.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DocumentLimits FromJson(string json) => JsonSerializer.Deserialize<DocumentLimits>(json, Json)!;

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));
}

/// <summary>Copia immutabile della policy in un istante: limiti, revisione e hash congelabili da una richiesta.</summary>
public sealed record DocumentPolicySnapshot(DocumentLimits Limits, long Revision, string PolicyHash)
{
    public static DocumentPolicySnapshot Of(DocumentLimits limits, long revision) => new(limits, revision, limits.Hash());
}

/// <summary>Riga globale unica con la configurazione Admin (data-model.md): Revision per il confronto atomico, nessun segreto.</summary>
public sealed class DocumentPolicy
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;
    public long Revision { get; set; }

    /// <summary>Limiti in JSONB (<see cref="DocumentLimits.ToJson"/>).</summary>
    public required string LimitsJson { get; set; }

    public required string PolicyHash { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public DocumentPolicySnapshot ToSnapshot() => new(DocumentLimits.FromJson(LimitsJson), Revision, PolicyHash);
}

/// <summary>Esito di un aggiornamento: applicato, revisione obsoleta o limiti non validi.</summary>
public abstract record PolicyUpdateResult
{
    public sealed record Updated(DocumentPolicySnapshot Policy, DateTimeOffset UpdatedAt) : PolicyUpdateResult;

    public sealed record Conflict(long CurrentRevision) : PolicyUpdateResult;

    public sealed record Invalid(IReadOnlyList<string> Violations) : PolicyUpdateResult;
}

/// <summary>
/// Accesso alla policy persistente. La riga nasce al primo uso dai default della configurazione server (<see cref="DocumentOptions"/>);
/// da allora l'autorità è il database, modificabile solo con PUT atomico su Revision.
/// </summary>
public sealed class DocumentPolicyService(AppDbContext db, IOptions<DocumentOptions> options)
{
    public async Task<(DocumentPolicy Row, DocumentPolicySnapshot Snapshot)> GetAsync(CancellationToken ct)
    {
        var row = await db.DocumentPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == DocumentPolicy.SingletonId, ct);
        if (row is null)
        {
            var limits = options.Value.ToLimits();
            var violations = limits.Validate();
            if (violations.Count > 0)
            {
                throw new InvalidOperationException("Configurazione Documents non valida: " + string.Join("; ", violations));
            }

            // Inserimento idempotente: se un'altra richiesta ha già creato la riga, vince la prima.
            var limitsJson = limits.ToJson();
            var hash = limits.Hash();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "DocumentPolicies" ("Id", "Revision", "LimitsJson", "PolicyHash", "UpdatedBy", "UpdatedAt") VALUES ({DocumentPolicy.SingletonId}, 1, {limitsJson}::jsonb, {hash}, NULL, now()) ON CONFLICT ("Id") DO NOTHING""", ct);
            row = await db.DocumentPolicies.AsNoTracking().SingleAsync(p => p.Id == DocumentPolicy.SingletonId, ct);
        }

        return (row, row.ToSnapshot());
    }

    /// <summary>Sostituzione atomica: UPDATE condizionato alla revisione attesa; esito diverso da una riga aggiornata è un conflitto.</summary>
    public async Task<PolicyUpdateResult> UpdateAsync(DocumentLimits limits, long expectedRevision, Guid updatedBy, CancellationToken ct)
    {
        var violations = limits.Validate();
        if (violations.Count > 0)
        {
            return new PolicyUpdateResult.Invalid(violations);
        }

        await GetAsync(ct); // garantisce la riga
        var limitsJson = limits.ToJson();
        var hash = limits.Hash();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "DocumentPolicies" SET "Revision" = "Revision" + 1, "LimitsJson" = {limitsJson}::jsonb, "PolicyHash" = {hash}, "UpdatedBy" = {updatedBy}, "UpdatedAt" = now() WHERE "Id" = {DocumentPolicy.SingletonId} AND "Revision" = {expectedRevision}""", ct);
        var (row, snapshot) = await GetAsync(ct);
        return affected == 1 ? new PolicyUpdateResult.Updated(snapshot, row.UpdatedAt) : new PolicyUpdateResult.Conflict(row.Revision);
    }
}
