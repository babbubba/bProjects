using System.Text.Json;
using bProjects.App.Projects;

namespace bProjects.IntegrationTests;

internal static class ProcessingStateEvents
{
    /// <summary>
    /// Legge dalla sottoscrizione gli eventi processing.state fino al primo stato terminale (Completed/Failed/Cancelled),
    /// senza attese a tempo fisso: le notifiche sono effimere ma l'ultima consegnata non è mai più vecchia del terminale.
    /// </summary>
    public static async Task<List<JsonElement>> UntilTerminalAsync(ProjectEventHub.Subscription subscription, CancellationToken ct)
    {
        var states = new List<JsonElement>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (await subscription.Reader.WaitToReadAsync(timeout.Token))
        {
            while (subscription.Reader.TryRead(out var evt))
            {
                if (evt.Name != ProjectEventHub.ProcessingState)
                {
                    continue;
                }

                var state = JsonDocument.Parse(evt.Data).RootElement.Clone();
                states.Add(state);
                if (state.GetProperty("status").GetString() is "Completed" or "Failed" or "Cancelled")
                {
                    return states;
                }
            }
        }

        throw new InvalidOperationException("Stream chiuso prima di uno stato terminale.");
    }
}
