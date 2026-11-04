using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace bProjects.App.AI;

// Pack comune (002 T069; contracts/ai-workflow.md §4.2 punto 2) a foglie, pagine, confronti e riduzioni: massimo prefisso contiguo di membri il cui
// payload completo, MISURATO ad ogni tentativo, entra in <see cref="PlannerLimits"/> (i token non sono additivi). Funzione pura dei dati e del
// misuratore: nessun Guid né orologio, quindi il replay ricostruisce gli stessi batch. È incrementale: PackNextAsync legge solo da `start` in poi,
// così un livello derivato si impacchetta dopo che gli output dei genitori sono Committed (append-only), senza congelare in anticipo i pacchetti.

/// <summary>Misura il payload COMPLETO di un prefisso di membri (istruzioni, contesto replicato, schema inclusi).</summary>
public delegate ValueTask<UnitMeasure> PackMeasurer<T>(IReadOnlyList<T> members, CancellationToken ct);

/// <summary>Batch impacchettato; <see cref="Measure"/> è null per un nodo singolo inoltrato (carry) che non richiede alcuna AI.</summary>
public sealed record PackedBatch<T>(int Ordinal, IReadOnlyList<T> Members, UnitMeasure? Measure)
{
    public bool Carried => Measure is null;
}

public static class DocumentBatchPacking
{
    /// <summary>
    /// Prossimo batch a partire da <paramref name="start"/>. Con <paramref name="minMembers"/> = 2 (riduzione di livello) servono almeno due
    /// descriptor: se non entrano insieme, <see cref="DocumentWorkException.ReconciliationIrreducible"/>; un ultimo nodo singolo è inoltrato (carry).
    /// Con 1, un membro che da solo non entra è irriducibile (<see cref="DocumentWorkException.DocumentIncomplete"/>, con ID e motivo, mai testo).
    /// </summary>
    public static async ValueTask<PackedBatch<T>> PackNextAsync<T>(
        IReadOnlyList<T> items, int start, int ordinal, int minMembers, PlannerLimits limits, Func<T, string> idOf, PackMeasurer<T> measure, CancellationToken ct)
    {
        if (start < 0 || start >= items.Count || minMembers is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Intervallo di Pack non valido.");
        }

        if (minMembers == 2 && start == items.Count - 1)
        {
            return new PackedBatch<T>(ordinal, [items[start]], null);
        }

        var members = new List<T> { items[start] };
        var best = await MeasureAsync(members, measure, ct);
        if (minMembers == 2)
        {
            members.Add(items[start + 1]);
            best = await MeasureAsync(members, measure, ct);
            if (!limits.Fits(best))
            {
                throw new DocumentWorkException(
                    DocumentWorkException.ReconciliationIrreducible,
                    $"I descriptor {idOf(items[start])} e {idOf(items[start + 1])} non entrano insieme nel budget ({best.Tokens} token su input {limits.InputBudgetTokens}): riconciliazione irriducibile.");
            }
        }
        else if (!limits.Fits(best))
        {
            throw new DocumentWorkException(
                DocumentWorkException.DocumentIncomplete,
                $"L'elemento {idOf(items[start])} non entra nel budget dell'unità ({best.Tokens} token, input {limits.InputBudgetTokens}, "
                + $"output stimato {Math.Ceiling(best.OwnedTokens * limits.OutputExpansion):0} su {limits.OutputTokens}) e non è divisibile: unità irriducibile.");
        }

        while (start + members.Count < items.Count)
        {
            var candidate = new List<T>(members) { items[start + members.Count] };
            var measured = await MeasureAsync(candidate, measure, ct);
            if (!limits.Fits(measured))
            {
                break;
            }

            members = candidate;
            best = measured;
        }

        return new PackedBatch<T>(ordinal, members, best);
    }

    /// <summary>Tutti i batch di un livello già noto, in ordine, con ordinal 0..n-1; ogni elemento compare esattamente una volta.</summary>
    public static async ValueTask<IReadOnlyList<PackedBatch<T>>> PackLevelAsync<T>(
        IReadOnlyList<T> items, int minMembers, PlannerLimits limits, Func<T, string> idOf, PackMeasurer<T> measure, CancellationToken ct)
    {
        var batches = new List<PackedBatch<T>>();
        var start = 0;
        while (start < items.Count)
        {
            var batch = await PackNextAsync(items, start, batches.Count, minMembers, limits, idOf, measure, ct);
            batches.Add(batch);
            start += batch.Members.Count;
        }

        return batches;
    }

    /// <summary>
    /// Batch derivato append-only: input hash = hash del payload misurato, identità = proprietario, manifesto, definizione, stadio, vista, livello,
    /// ordinal, input hash e hash degli output genitori Committed. Un nodo inoltrato (carry) non ha chiamata AI né batch.
    /// </summary>
    public static DerivedBatchInput ToDerived<T>(
        PackedBatch<T> batch, string owner, string manifestIdentity, string definition, string stage, string? view, int? level,
        IReadOnlyList<string> parentOutputHashes, Func<T, string> idOf, string schemaVersion, string promptVersion)
    {
        var measure = batch.Measure ?? throw new InvalidOperationException("Un nodo inoltrato non genera un batch derivato.");
        var parents = string.Join(',', parentOutputHashes);
        var batchId = Sha(string.Create(
            CultureInfo.InvariantCulture,
            $"batch|{owner}|{manifestIdentity}|{definition}|{stage}|{view}|{level}|{batch.Ordinal}|{measure.PayloadHash}|{parents}"));
        var provenance = measure.Quality == TokenCountQuality.Measured ? "measured" : "calibrated_upper_bound";
        var inputIds = JsonSerializer.Serialize(new { members = batch.Members.Select(idOf).ToArray() });
        return new DerivedBatchInput(
            batchId, stage, view, null, level, batch.Ordinal, [.. parentOutputHashes], inputIds, measure.Tokens, provenance, measure.PayloadHash, schemaVersion, promptVersion);
    }

    private static async ValueTask<UnitMeasure> MeasureAsync<T>(IReadOnlyList<T> members, PackMeasurer<T> measure, CancellationToken ct)
    {
        var measured = await measure(members, ct);
        return measured.Quality == TokenCountQuality.Unavailable
            ? throw new RuntimeUnverifiedException("conteggio del payload non disponibile (nessun tokenizer o calibrazione identificata)")
            : measured;
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
