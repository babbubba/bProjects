namespace bProjects.App.AI;

/// <summary>
/// Segnale in-process che sveglia il dispatcher quando un endpoint accoda un turno o un run, invece di attendere il prossimo giro di
/// polling (<see cref="AiDispatchService"/>, 1 s). Non sostituisce il polling: il lavoro resta durevole nel database e il dispatcher
/// continua a rileggerlo a intervalli, quindi un segnale perso (altra istanza, riavvio) costa al più un intervallo. Segnali ripetuti
/// prima dell'attesa si fondono in uno solo: nessuna coda, nessun ciclo parallelo.
/// </summary>
public sealed class DispatchWakeup
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Segnala che c'è nuovo lavoro eleggibile. Idempotente finché il dispatcher non l'ha consumato.</summary>
    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Già segnalato: il dispatcher leggerà comunque lo stato del database.
        }
    }

    /// <summary>Attende il segnale o, al più, <paramref name="timeout"/>. True se è arrivato un segnale.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
