using System.ClientModel;
using System.Net;
using Npgsql;

namespace bProjects.App.AI;

/// <summary>Esito della classificazione di un errore del modello (contracts/ai-workflow.md §2).</summary>
public readonly record struct ModelFailure(bool IsTransient, string Code, TimeSpan? RetryAfter, string? Detail = null)
{
    /// <summary>
    /// Transitori (anche se incapsulati, es. DbUpdateException con causa transitoria): guasti transitori del database, trasporto interrotto, timeout e HTTP 408/429/5xx. Tutto il resto (401/403, 400, 404,
    /// risposte non valide) è permanente. Il chiamante non deve classificare la propria cancellazione
    /// (arresto/deadline): un OperationCanceledException qui è inteso come timeout di rete.
    /// </summary>
    public static ModelFailure Classify(Exception exception)
    {
        switch (exception)
        {
            case ContextOverflowException overflow:
                return new(false, overflow.Code, null, $"Contesto di {overflow.Tokens} token oltre il budget di input di {overflow.BudgetTokens} token, non riducibile con la compattazione nativa. Gli originali non sono stati modificati.");
            case CompactionFailedException failed:
                return new(false, failed.Code, null, "La compattazione nativa del contesto non è riuscita. Gli originali non sono stati modificati.");
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
            case NpgsqlException { IsTransient: true }:
                // Guasto momentaneo del database (connessione, timeout, risorse): l'infrastruttura non è un errore del modello.
                return new(true, "infrastructure_unavailable", null);
            default:
                return exception.InnerException is { } wrapped ? Wrapped(wrapped) : new(false, "model_error", null);
        }
    }

    /// <summary>Un'eccezione che ne incapsula un'altra è transitoria solo se lo è la causa; altrimenti resta un errore permanente.</summary>
    private static ModelFailure Wrapped(Exception inner)
    {
        var cause = Classify(inner);
        return cause.IsTransient ? cause : new(false, "model_error", null);
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
