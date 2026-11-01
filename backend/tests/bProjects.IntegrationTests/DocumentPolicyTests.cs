using System.Text.Json;
using bProjects.App.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T004 (DET): policy e configurazione dei documenti, senza database né rete. Default di Plan §Configurazione, quote positive,
/// Extended &gt;= 180 s e finito, snapshot immutabile; nessun PromotionAfterSeconds operativo (document-regime-v2).
/// La parte server (GET/PUT Admin, limiti rifiutati, If-Match) è in <see cref="DocumentAdminAuthorizationTests"/>.
/// </summary>
public sealed class DocumentPolicyTests
{
    [Fact]
    public void Defaults_match_the_plan_configuration_table()
    {
        var limits = new DocumentOptions().ToLimits();

        Assert.Equal(50_000_000, limits.MaxFileBytes);
        Assert.Equal(1_000_000_000, limits.ProjectQuotaBytes);
        Assert.Equal(limits.ProjectQuotaBytes, limits.DerivedQuotaBytes);   // quota derivati separata, di pari valore
        Assert.Equal(250_000_000, limits.MaxExpandedBytes);
        Assert.Equal(1_000, limits.MaxPages);
        Assert.Equal(100_000, limits.MaxBlocks);
        Assert.Equal(300, limits.ParseTimeoutSeconds);
        Assert.Equal(300, limits.UploadTimeoutSeconds);
        Assert.Equal(3_600, limits.PreparationDeadlineSeconds);
        Assert.Equal(180, limits.OrdinaryDeadlineSeconds);
        Assert.Equal(3_600, limits.ExtendedDeadlineSeconds);
        Assert.Equal(512, limits.MaxAiCallsPerPreparation);
        Assert.Equal(512, limits.MaxAiCallsPerRun);
        Assert.Equal(250_000_000, limits.MaxDerivedBytes);
        Assert.Equal(32, limits.MaxReconciliationLevels);
        Assert.Equal(100_000, limits.MaxCandidatesPerRun);
        Assert.Empty(limits.Validate());
    }

    [Fact]
    public void The_shipped_appsettings_carry_the_same_defaults_and_pass_startup_validation()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var section = configuration.GetSection(DocumentOptions.SectionName);

        var bound = section.Get<DocumentOptions>();

        Assert.NotNull(bound);
        Assert.Equal(new DocumentOptions().ToLimits(), bound.ToLimits());
        Assert.True(new DocumentOptionsValidator().Validate(null, bound).Succeeded);
        Assert.DoesNotContain(section.GetChildren(), c => c.Key.Contains("Promotion", StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string> NumericLimits() =>
        [
            nameof(DocumentLimits.MaxFileBytes), nameof(DocumentLimits.ProjectQuotaBytes), nameof(DocumentLimits.DerivedQuotaBytes),
            nameof(DocumentLimits.MaxExpandedBytes), nameof(DocumentLimits.MaxPages), nameof(DocumentLimits.MaxBlocks),
            nameof(DocumentLimits.ParseTimeoutSeconds), nameof(DocumentLimits.UploadTimeoutSeconds), nameof(DocumentLimits.PreparationDeadlineSeconds),
            nameof(DocumentLimits.OrdinaryDeadlineSeconds), nameof(DocumentLimits.ExtendedDeadlineSeconds), nameof(DocumentLimits.MaxAiCallsPerPreparation),
            nameof(DocumentLimits.MaxAiCallsPerRun), nameof(DocumentLimits.MaxDerivedBytes), nameof(DocumentLimits.MaxReconciliationLevels),
            nameof(DocumentLimits.MaxCandidatesPerRun),
        ];

    [Theory]
    [MemberData(nameof(NumericLimits))]
    public void Every_limit_must_be_strictly_positive(string field)
    {
        foreach (var invalid in new[] { 0, -1 })
        {
            var violations = With(new DocumentOptions().ToLimits(), field, invalid).Validate();

            Assert.Contains(violations, v => v.StartsWith(field + ":", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(179, false)]
    [InlineData(180, true)]
    [InlineData(3_600, true)]
    [InlineData(86_400, true)]
    [InlineData(86_401, false)]
    [InlineData(int.MaxValue, false)]
    public void Extended_deadline_is_at_least_180_seconds_and_finite(int seconds, bool valid)
    {
        var limits = new DocumentOptions().ToLimits() with { ExtendedDeadlineSeconds = seconds };

        Assert.Equal(valid, limits.Validate().Count == 0);
    }

    [Fact]
    public void Limits_must_be_mutually_coherent()
    {
        var defaults = new DocumentOptions().ToLimits();

        Assert.Contains((defaults with { MaxFileBytes = defaults.ProjectQuotaBytes + 1 }).Validate(), v => v.StartsWith("MaxFileBytes:", StringComparison.Ordinal));
        Assert.Contains((defaults with { OrdinaryDeadlineSeconds = 3_601 }).Validate(), v => v.StartsWith("OrdinaryDeadlineSeconds:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_snapshot_is_an_immutable_copy_whose_hash_changes_only_with_the_limits()
    {
        var options = new DocumentOptions();
        var snapshot = DocumentPolicySnapshot.Of(options.ToLimits(), revision: 1);
        var hash = snapshot.PolicyHash;

        options.MaxFileBytes = 1;           // la configurazione cambia dopo lo snapshot
        options.ExtendedDeadlineSeconds = 999;

        Assert.Equal(50_000_000, snapshot.Limits.MaxFileBytes);
        Assert.Equal(3_600, snapshot.Limits.ExtendedDeadlineSeconds);
        Assert.Equal(hash, DocumentPolicySnapshot.Of(new DocumentOptions().ToLimits(), 7).PolicyHash);   // la revisione non entra nell'hash dei limiti
        Assert.NotEqual(hash, DocumentPolicySnapshot.Of(options.ToLimits(), 1).PolicyHash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void The_limits_round_trip_through_the_canonical_json()
    {
        var limits = new DocumentOptions().ToLimits() with { MaxPages = 1234 };

        Assert.Equal(limits, DocumentLimits.FromJson(limits.ToJson()));
        Assert.Contains("\"maxFileBytes\":50000000", limits.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionAfterSeconds_is_not_an_operational_setting()
    {
        Assert.DoesNotContain(typeof(DocumentOptions).GetProperties(), p => p.Name.Contains("Promotion", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(DocumentLimits).GetProperties(), p => p.Name.Contains("Promotion", StringComparison.OrdinalIgnoreCase));

        // Un corpo che lo contiene è rifiutato, non ignorato.
        var json = new DocumentOptions().ToLimits().ToJson().TrimEnd('}') + ",\"promotionAfterSeconds\":120}";
        Assert.Throws<JsonException>(() => DocumentLimits.FromJson(json));
    }

    [Fact]
    public void A_body_with_a_missing_limit_is_rejected_instead_of_defaulting_to_zero()
    {
        var json = new DocumentOptions().ToLimits().ToJson().Replace("\"maxPages\":1000,", string.Empty, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => DocumentLimits.FromJson(json));
    }

    [Fact]
    public void Invalid_server_configuration_fails_startup_validation_with_every_violation()
    {
        var result = new DocumentOptionsValidator().Validate(null, new DocumentOptions { MaxPages = 0, ExtendedDeadlineSeconds = 60 });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.StartsWith("Documents:MaxPages:", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.StartsWith("Documents:ExtendedDeadlineSeconds:", StringComparison.Ordinal));
    }

    private static DocumentLimits With(DocumentLimits limits, string field, long value) => field switch
    {
        nameof(DocumentLimits.MaxFileBytes) => limits with { MaxFileBytes = value },
        nameof(DocumentLimits.ProjectQuotaBytes) => limits with { ProjectQuotaBytes = value },
        nameof(DocumentLimits.DerivedQuotaBytes) => limits with { DerivedQuotaBytes = value },
        nameof(DocumentLimits.MaxExpandedBytes) => limits with { MaxExpandedBytes = value },
        nameof(DocumentLimits.MaxPages) => limits with { MaxPages = (int)value },
        nameof(DocumentLimits.MaxBlocks) => limits with { MaxBlocks = (int)value },
        nameof(DocumentLimits.ParseTimeoutSeconds) => limits with { ParseTimeoutSeconds = (int)value },
        nameof(DocumentLimits.UploadTimeoutSeconds) => limits with { UploadTimeoutSeconds = (int)value },
        nameof(DocumentLimits.PreparationDeadlineSeconds) => limits with { PreparationDeadlineSeconds = (int)value },
        nameof(DocumentLimits.OrdinaryDeadlineSeconds) => limits with { OrdinaryDeadlineSeconds = (int)value },
        nameof(DocumentLimits.ExtendedDeadlineSeconds) => limits with { ExtendedDeadlineSeconds = (int)value },
        nameof(DocumentLimits.MaxAiCallsPerPreparation) => limits with { MaxAiCallsPerPreparation = (int)value },
        nameof(DocumentLimits.MaxAiCallsPerRun) => limits with { MaxAiCallsPerRun = (int)value },
        nameof(DocumentLimits.MaxDerivedBytes) => limits with { MaxDerivedBytes = value },
        nameof(DocumentLimits.MaxReconciliationLevels) => limits with { MaxReconciliationLevels = (int)value },
        nameof(DocumentLimits.MaxCandidatesPerRun) => limits with { MaxCandidatesPerRun = (int)value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}
