using Microsoft.Extensions.Options;

namespace bProjects.App.AI;

/// <summary>
/// Politica di dispatch/lease/retry (sezione <c>Execution</c>, contracts/ai-workflow.md §2). I valori
/// iniziali sono quelli approvati; il contatore dei tentativi è persistente e non vive in questa classe.
/// </summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    public int LeaseDurationSeconds { get; set; } = 30;

    public int RenewIntervalSeconds { get; set; } = 10;

    /// <summary>Tentativi totali, incluso il primo claim.</summary>
    public int MaxAttempts { get; set; } = 3;

    public int RetryBaseDelaySeconds { get; set; } = 2;

    public int RetryMaxDelaySeconds { get; set; } = 10;

    /// <summary>Deadline complessiva dal comando; non attraversa l'attesa umana.</summary>
    public int RunDeadlineSeconds { get; set; } = 180;

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public TimeSpan RenewInterval => TimeSpan.FromSeconds(RenewIntervalSeconds);

    public TimeSpan RunDeadline => TimeSpan.FromSeconds(RunDeadlineSeconds);

    /// <summary>Attesa prima del claim successivo: min(RetryMaxDelay, RetryBaseDelay * 2^(AttemptCount-1)), cioè 2s e 4s con i default.</summary>
    public TimeSpan BackoffAfterAttempt(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 30);
        var seconds = Math.Min((double)RetryMaxDelaySeconds, RetryBaseDelaySeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}

public sealed class ExecutionOptionsValidator(IOptions<AiOptions>? ai = null) : IValidateOptions<ExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, ExecutionOptions o)
    {
        var errors = new List<string>();

        if (o.LeaseDurationSeconds <= 0 || o.RenewIntervalSeconds <= 0 || o.RetryBaseDelaySeconds <= 0
            || o.RetryMaxDelaySeconds <= 0 || o.RunDeadlineSeconds <= 0)
        {
            errors.Add("Execution: durate (lease, rinnovo, backoff, deadline) devono essere positive.");
        }

        if (o.MaxAttempts < 1)
        {
            errors.Add("Execution:MaxAttempts deve essere almeno 1 (incluso il primo claim).");
        }

        if ((long)o.RenewIntervalSeconds * 2 >= o.LeaseDurationSeconds)
        {
            errors.Add("Execution:RenewIntervalSeconds deve essere minore di metà di LeaseDurationSeconds.");
        }

        if (o.RetryBaseDelaySeconds > o.RetryMaxDelaySeconds)
        {
            errors.Add("Execution:RetryBaseDelaySeconds non può superare RetryMaxDelaySeconds.");
        }

        if (ai?.Value is { } a && a.RequestTimeoutSeconds >= o.RunDeadlineSeconds)
        {
            errors.Add("Ai:RequestTimeoutSeconds deve essere inferiore a Execution:RunDeadlineSeconds.");
        }

        if (o.RunDeadlineSeconds < o.LeaseDurationSeconds)
        {
            errors.Add("Execution:RunDeadlineSeconds non può essere inferiore a LeaseDurationSeconds.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
