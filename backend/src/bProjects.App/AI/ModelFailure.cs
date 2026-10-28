using System.ClientModel;
using System.Net;

namespace bProjects.App.AI;

/// <summary>Esito della classificazione di un errore del modello (contracts/ai-workflow.md §2).</summary>
public readonly record struct ModelFailure(bool IsTransient, string Code, TimeSpan? RetryAfter)
{
    /// <summary>
    /// Transitori: trasporto interrotto, timeout e HTTP 408/429/5xx. Tutto il resto (401/403, 400, 404,
    /// risposte non valide) è permanente. Il chiamante non deve classificare la propria cancellazione
    /// (arresto/deadline): un OperationCanceledException qui è inteso come timeout di rete.
    /// </summary>
    public static ModelFailure Classify(Exception exception)
    {
        switch (exception)
        {
            case ContextOverflowException overflow:
                return new(false, overflow.Code, null);
            case CompactionFailedException failed:
                return new(false, failed.Code, null);
            case ClientResultException { Status: 0 }:
                // Nessuna risposta HTTP: l'SDK incapsula guasti di trasporto e timeout.
                return new(true, "model_unreachable", null);
            case ClientResultException { Status: var status } cre:
                return FromStatus(status, RetryAfterOf(cre));
            case HttpRequestException { StatusCode: { } code } hre:
                return FromStatus((int)code, null);
            case HttpRequestException:
            case TimeoutException:
            case IOException:
                return new(true, "model_unreachable", null);
            case OperationCanceledException:
                return new(true, "model_timeout", null);
            default:
                return new(false, "model_error", null);
        }
    }

    private static ModelFailure FromStatus(int status, TimeSpan? retryAfter) => status switch
    {
        408 => new(true, "model_timeout", retryAfter),
        429 => new(true, "model_rate_limited", retryAfter),
        >= 500 => new(true, "model_unavailable", retryAfter),
        401 or 403 => new(false, "model_unauthorized", null),
        _ => new(false, "model_rejected", null),
    };

    private static TimeSpan? RetryAfterOf(ClientResultException exception)
    {
        if (exception.GetRawResponse() is { } raw
            && raw.Headers.TryGetValue("Retry-After", out var value)
            && int.TryParse(value, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}
