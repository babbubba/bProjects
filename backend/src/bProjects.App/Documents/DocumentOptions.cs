using Microsoft.Extensions.Options;

namespace bProjects.App.Documents;

/// <summary>
/// Limiti operativi dei documenti di progetto (Plan §Configurazione e budget). Sono i default iniziali della sezione <c>Documents</c>;
/// la riga <see cref="DocumentPolicy"/> li rende modificabili dall'Admin. Non sono requisiti di prodotto: nessun limite è «illimitato».
/// Documents:PromotionAfterSeconds è stato rimosso dal Plan (document-regime-v2) e non esiste qui.
/// </summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    /// <summary>
    /// Radice privata degli originali (volume persistente, fuori da webroot e senza URL pubblica). Vuota = sottocartella di LocalApplicationData
    /// (sviluppo); nei container si imposta Documents__StorageRoot sul volume dedicato. Non è un limite né modificabile dall'Admin.
    /// </summary>
    public string? StorageRoot { get; set; }

    public long MaxFileBytes { get; set; } = 50_000_000;
    public long ProjectQuotaBytes { get; set; } = 1_000_000_000;

    /// <summary>Quota dei derivati del progetto, separata da quella degli originali e di pari valore iniziale.</summary>
    public long DerivedQuotaBytes { get; set; } = 1_000_000_000;
    public long MaxExpandedBytes { get; set; } = 250_000_000;
    public int MaxPages { get; set; } = 1_000;
    public int MaxBlocks { get; set; } = 100_000;
    public int ParseTimeoutSeconds { get; set; } = 300;
    public int UploadTimeoutSeconds { get; set; } = 300;
    public int PreparationDeadlineSeconds { get; set; } = 3_600;
    public int OrdinaryDeadlineSeconds { get; set; } = 180;
    public int ExtendedDeadlineSeconds { get; set; } = 3_600;
    public int MaxAiCallsPerPreparation { get; set; } = 512;
    public int MaxAiCallsPerRun { get; set; } = 512;

    /// <summary>Byte di derivati (manifesti, range, output intermedi) per versione di documento.</summary>
    public long MaxDerivedBytes { get; set; } = 250_000_000;
    public int MaxReconciliationLevels { get; set; } = 32;
    public int MaxCandidatesPerRun { get; set; } = 100_000;

    public DocumentLimits ToLimits() => new(
        MaxFileBytes, ProjectQuotaBytes, DerivedQuotaBytes, MaxExpandedBytes, MaxPages, MaxBlocks, ParseTimeoutSeconds, UploadTimeoutSeconds,
        PreparationDeadlineSeconds, OrdinaryDeadlineSeconds, ExtendedDeadlineSeconds, MaxAiCallsPerPreparation, MaxAiCallsPerRun,
        MaxDerivedBytes, MaxReconciliationLevels, MaxCandidatesPerRun);
}

/// <summary>Convalida all'avvio della configurazione server (stesse regole del PUT Admin: <see cref="DocumentLimits.Validate"/>).</summary>
public sealed class DocumentOptionsValidator : IValidateOptions<DocumentOptions>
{
    public ValidateOptionsResult Validate(string? name, DocumentOptions options)
    {
        var errors = options.ToLimits().Validate().ToList();
        if (!string.IsNullOrWhiteSpace(options.StorageRoot) && !Path.IsPathFullyQualified(options.StorageRoot))
        {
            return ValidateOptionsResult.Fail($"{DocumentOptions.SectionName}:StorageRoot deve essere un percorso assoluto.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors.Select(e => $"{DocumentOptions.SectionName}:{e}"));
    }
}
