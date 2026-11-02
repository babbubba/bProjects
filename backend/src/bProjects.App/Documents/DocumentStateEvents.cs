using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

/// <summary>
/// Produttore best-effort di <c>document.state</c> sullo stream del progetto (contracts/http-api.md). Le transizioni di stato delle
/// preparazioni avvengono in più punti (parsing, aggregazione, sweep delle scadenze) e in SQL: invece di duplicare una notifica in ognuno,
/// un sondaggio leggero confronta lo stato corrente con l'ultimo noto. La prima passata fissa la base (lo stato esistente non è un
/// cambiamento); poi ogni cambio di stato produce un evento e una preparazione terminale esce dal controllo. Nessun replay, nessun
/// contenuto: solo identificativi e stato. Il GET resta la fonte e un evento perso si riconcilia con esso.
/// </summary>
public sealed class DocumentStateEvents(ProjectEventHub hub, IServiceScopeFactory scopes, ILogger<DocumentStateEvents> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private readonly Dictionary<Guid, PreparationStatus> _known = [];
    private bool _baselined;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await PollAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Sondaggio document.state fallito: {ExceptionType}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Arresto dell'applicazione.
        }
    }

    public async Task PollAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = _known.Keys.ToList();
        var rows = await db.DocumentPreparations.AsNoTracking()
            .Where(p => p.Status == PreparationStatus.Queued || p.Status == PreparationStatus.Parsing || p.Status == PreparationStatus.Extracting || tracked.Contains(p.Id))
            .Select(p => new { p.Id, p.ProjectId, p.DocumentVersionId, p.ParseJobId, p.Status })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            var changed = !_known.TryGetValue(row.Id, out var previous) ? _baselined : previous != row.Status;
            if (row.Status is PreparationStatus.Queued or PreparationStatus.Parsing or PreparationStatus.Extracting)
            {
                _known[row.Id] = row.Status;
            }
            else
            {
                _known.Remove(row.Id); // terminale: notificato una volta, poi non più seguito
            }

            if (changed)
            {
                hub.PublishDocumentState(row.ProjectId, row.ParseJobId, row.DocumentVersionId, row.Id, row.Status.ToString());
            }
        }

        _baselined = true;
    }
}
