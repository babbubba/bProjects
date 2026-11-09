namespace bProjects.App.Documents;

/// <summary>
/// Pulizia periodica degli originali orfani (hardening I1): un solo ciclo di manutenzione, non un workflow né uno scheduler AI. Parte poco dopo
/// l'avvio e poi a intervalli, in passi limitati (<see cref="MaxExamined"/>/<see cref="MaxRemoved"/>) con un cursore in memoria; un riavvio
/// ricomincia da zero senza perdere nulla, perché lo stato vero (file, ricevute, versioni) è nel volume e nel database. Gli errori non fermano il
/// servizio e non scrivono mai nomi di file né percorsi nei log.
/// </summary>
public sealed class DocumentOrphanSweepService(IServiceScopeFactory scopes, ILogger<DocumentOrphanSweepService> logger, OrphanSweepSchedule? schedule = null) : BackgroundService
{
    public const int MaxExamined = 500;
    public const int MaxRemoved = 100;

    private readonly OrphanSweepSchedule _schedule = schedule ?? OrphanSweepSchedule.Default;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? cursor = null;
        try
        {
            await Task.Delay(_schedule.InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = _schedule.Interval;
                try
                {
                    using var scope = scopes.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<DocumentUploadService>()
                        .SweepOrphansAsync(cursor, _schedule.MinAge, MaxExamined, MaxRemoved, stoppingToken);
                    cursor = result.NextCursor;
                    if (result.Removed > 0)
                    {
                        logger.LogInformation("Pulizia orfani: {Removed} originali rimossi su {Examined} esaminati", result.Removed, result.Examined);
                    }

                    if (cursor is not null)
                    {
                        wait = _schedule.ContinuationDelay;   // giro non completato: si riprende presto
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Pulizia degli originali orfani fallita: {ExceptionType}", ex.GetType().Name);
                }

                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // arresto richiesto
        }
    }
}

/// <summary>Tempi della pulizia. <see cref="MinAge"/> protegge i file appena scritti oltre alla ricevuta viva; i test li riducono.</summary>
public sealed record OrphanSweepSchedule(TimeSpan InitialDelay, TimeSpan Interval, TimeSpan ContinuationDelay, TimeSpan MinAge)
{
    public static OrphanSweepSchedule Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
}
