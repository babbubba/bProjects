namespace bProjects.App.AI;

// Pack comune (002 T069; contracts/ai-workflow.md §4.2 punto 2). Scheletro T068: i test sono rossi finché T069 non lo implementa.

/// <summary>Misura il payload COMPLETO di un prefisso di membri (istruzioni, contesto replicato, schema inclusi).</summary>
public delegate ValueTask<UnitMeasure> PackMeasurer<T>(IReadOnlyList<T> members, CancellationToken ct);

/// <summary>Batch impacchettato; <see cref="Measure"/> è null per un nodo singolo inoltrato (carry) che non richiede alcuna AI.</summary>
public sealed record PackedBatch<T>(int Ordinal, IReadOnlyList<T> Members, UnitMeasure? Measure)
{
    public bool Carried => Measure is null;
}

public static class DocumentBatchPacking
{
    public static ValueTask<PackedBatch<T>> PackNextAsync<T>(
        IReadOnlyList<T> items, int start, int ordinal, int minMembers, PlannerLimits limits, Func<T, string> idOf, PackMeasurer<T> measure, CancellationToken ct) =>
        throw new NotImplementedException();

    public static ValueTask<IReadOnlyList<PackedBatch<T>>> PackLevelAsync<T>(
        IReadOnlyList<T> items, int minMembers, PlannerLimits limits, Func<T, string> idOf, PackMeasurer<T> measure, CancellationToken ct) =>
        throw new NotImplementedException();

    public static DerivedBatchInput ToDerived<T>(
        PackedBatch<T> batch, string owner, string manifestIdentity, string definition, string stage, string? view, int? level,
        IReadOnlyList<string> parentOutputHashes, Func<T, string> idOf, string schemaVersion, string promptVersion) =>
        throw new NotImplementedException();
}
