using System.Diagnostics;

namespace bProjects.App.AI;

/// <summary>
/// Strumentazione dei tempi della pipeline documentale (diagnosi T082: dove va il tempo fra una chiamata al modello e la successiva). Usa
/// <see cref="ActivitySource"/>: senza un listener <see cref="Span"/> restituisce null e non costa nulla; non scrive su database né log e non cambia
/// alcun comportamento. I test e i benchmark attaccano un <see cref="ActivityListener"/> e ricavano tempi propri (self) e attese dalle durate annidate.
/// </summary>
public static class DocumentTiming
{
    public const string SourceName = "bProjects.Documents.Timing";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>Apre un intervallo cronometrato (disporre con <c>using</c>); null se nessun listener è attaccato.</summary>
    public static Activity? Span(string name) => Source.StartActivity(name);

    /// <summary>Cronometra l'esecuzione di <paramref name="work"/> e ne restituisce il risultato.</summary>
    public static async Task<T> TimeAsync<T>(string name, Func<Task<T>> work)
    {
        using var span = Span(name);
        return await work();
    }
}
