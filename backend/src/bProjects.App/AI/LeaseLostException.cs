namespace bProjects.App.AI;

/// <summary>
/// L'owner non detiene più un lease valido (scaduto, fencing cambiato o deadline passata): una scrittura è stata rifiutata.
/// Non è un errore del modello né del lavoro: il dispatcher non rilascia il run (non è più suo) e lascia a scadenza del
/// lease, sweep e reclaim la decisione corretta (nuovo tentativo, deadline_exceeded o attempts_exhausted).
/// </summary>
public class LeaseLostException(string message) : InvalidOperationException(message);
