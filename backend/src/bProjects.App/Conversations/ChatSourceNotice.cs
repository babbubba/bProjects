using bProjects.App.Documents;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Conversations;

/// <summary>
/// Dichiarazione deterministica delle fonti documentali indisponibili in una risposta di chat (contracts/http-api.md, POST /messages).
/// Lo snapshot del turno è congelato alla richiesta: se contiene documenti inclusi ma non pronti, la risposta committata lo dice
/// in chiaro e non ne ricava contenuto. Non passa dal modello (nessuna inferenza per spiegare l'indisponibilità) e non riporta i nomi
/// dei documenti. Un turno senza snapshot (ramo 001) o senza fonti indisponibili non cambia di una virgola.
/// </summary>
public static class ChatSourceNotice
{
    public static async Task<int> UnavailableCountAsync(AppDbContext db, Guid turnId, CancellationToken ct)
    {
        var snapshotId = await db.ChatTurns.AsNoTracking().Where(t => t.Id == turnId).Select(t => t.SourceSnapshotId).SingleOrDefaultAsync(ct);
        return snapshotId is null
            ? 0
            : await db.SourceSnapshotItems.AsNoTracking().CountAsync(i => i.SnapshotId == snapshotId && i.Availability == SourceAvailability.Unavailable, ct);
    }

    public static string Append(string text, int unavailable) => unavailable <= 0
        ? text
        : text.TrimEnd() + "\n\n" + (unavailable == 1
            ? "Nota sulle fonti: 1 documento incluso nel progetto non è ancora disponibile (in preparazione o non utilizzabile). "
            : $"Nota sulle fonti: {unavailable} documenti inclusi nel progetto non sono ancora disponibili (in preparazione o non utilizzabili). ")
          + "Questa risposta si basa solo sul dialogo e sulle fonti già pronte; non usa il contenuto dei documenti indisponibili.";
}
