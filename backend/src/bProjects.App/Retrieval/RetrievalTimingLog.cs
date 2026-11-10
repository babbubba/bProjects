using System.Collections.Concurrent;
using System.Diagnostics;

namespace bProjects.App.Retrieval;

/// <summary>
/// Misura OPZIONALE dei tempi del recupero (003 T040, SC-009): quando nel contenitore è registrato un <see cref="RetrievalTimingLog"/> (solo le prove, mai la produzione) <see cref="RetrievalPipeline"/>
/// vi registra durata e finestra temporale di ogni fase. Sola osservazione: nessun effetto su ricerca, riordino, registro, esito o errori. Le fasi per Gap (<see cref="PhaseSearch"/>, <see cref="PhaseRerank"/>)
/// portano il Gap; le fasi condivise (prontezza, provenienza, costruzione e pacchetto del registro) NON sono attribuibili a un singolo Gap e restano senza Gap.
/// </summary>
public sealed class RetrievalTimingLog
{
    public const string PhaseReadiness = "readiness";
    public const string PhaseIndexReady = "index_ready";
    public const string PhaseSearch = "search";
    public const string PhaseRerankerReady = "reranker_ready";
    public const string PhaseRerank = "rerank";
    public const string PhaseProvenance = "provenance";
    public const string PhaseRegistry = "registry";
    public const string PhaseTotal = "total";

    /// <summary>Una fase misurata; <see cref="GapEvidenceId"/> è null per le fasi condivise. Inizio e fine in UTC (finestra per correlare le chiamate HTTP annidate).</summary>
    public sealed record Entry(string Phase, Guid? GapEvidenceId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, double Milliseconds, bool Faulted);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public async Task<T> MeasureAsync<T>(string phase, Guid? gap, Func<Task<T>> work)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var faulted = true;
        try
        {
            var result = await work();
            faulted = false;
            return result;
        }
        finally
        {
            _entries.Enqueue(new Entry(phase, gap, started, DateTimeOffset.UtcNow, clock.Elapsed.TotalMilliseconds, faulted));
        }
    }

    public T Measure<T>(string phase, Guid? gap, Func<T> work)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var faulted = true;
        try
        {
            var result = work();
            faulted = false;
            return result;
        }
        finally
        {
            _entries.Enqueue(new Entry(phase, gap, started, DateTimeOffset.UtcNow, clock.Elapsed.TotalMilliseconds, faulted));
        }
    }
}
