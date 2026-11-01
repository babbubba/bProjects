using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Documents;

/// <summary>
/// Lock di progetto condiviso da upload, selezione fonti, Analizza e chat (Plan §Selezione: ordine progetto → conversazione → job).
/// È un advisory lock transazionale di PostgreSQL: si rilascia al commit/rollback e va sempre acquisito dentro una transazione
/// breve — mai durante la ricezione di uno stream.
/// </summary>
internal static class ProjectLock
{
    public static Task AcquireAsync(AppDbContext db, Guid projectId, CancellationToken ct)
    {
        var key = "bprojects:project-sources:" + projectId.ToString("N");
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    /// <summary>Orario del database: stessa fonte temporale di lease e scadenze, mai l'orologio dell'applicazione.</summary>
    public static async Task<DateTimeOffset> NowAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
}
