using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Retrieval;

/// <summary>Un candidato recuperato per un Gap, nell'ordine finale del registro. Solo ciò che serve al PM: documento, versione, sezione, posizione e rango; nessun hash né identificativo tecnico di blocco.</summary>
public sealed record RetrievalCandidateView(
    int Rank, string DocumentName, int DocumentVersion, string Section, int RangeStart, int RangeEnd, int? LexicalRank, int? DenseRank, int RrfRank, string Origin, bool InPackage, string? ExcludedReason);

public sealed record RetrievalGapView(Guid GapEvidenceId, string Query, IReadOnlyList<RetrievalCandidateView> Candidates);

/// <summary>
/// Registro del recupero di un run (sola lettura). <c>Outcome</c> diverso da <c>Package</c> è un mancato completamento con le cause tipizzate: un risultato non consegnabile, mai valido.
/// </summary>
public sealed record RetrievalRegistryApiView(
    Guid AnalysisId, string SearchBasis, string Outcome, bool RetrievalComplete, bool Degraded, IReadOnlyList<string> Causes, string PolicyVersion, IReadOnlyList<RetrievalGapView> Gaps);

/// <summary>
/// Lettura del registro dei candidati (003 T033, contracts/index-and-registry.md §API): stesso gruppo e stessi permessi di progetto della copertura (il run deve appartenere al progetto dell'URL;
/// nessun registro di altri progetti). Letto dal WorkOutput <c>retrieval-output-1</c> con schema e hash verificati; non invoca servizi né modelli e non scrive.
/// </summary>
public static class RetrievalEndpoints
{
    public static RouteGroupBuilder MapRetrievalEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/analyses/{analysisId:guid}/retrieval-registry", Get);
        return group;
    }

    private static async Task<IResult> Get(Guid projectId, Guid analysisId, AppDbContext db, CancellationToken ct)
    {
        if (!await db.ProcessingRuns.AsNoTracking().AnyAsync(r => r.Id == analysisId && r.ProjectId == projectId && r.Kind == ProcessingRunKind.Analysis, ct))
        {
            return Results.NotFound();
        }

        var outputs = await db.WorkOutputs.AsNoTracking()
            .Where(o => o.OwnerKind == WorkOwnerKind.ProcessingRun && o.OwnerId == analysisId && o.SchemaVersion == RetrievalStage.OutputSchema && o.Status == WorkOutputStatus.Committed)
            .ToListAsync(ct);
        if (outputs.Count == 0)
        {
            return EvidenceEndpoints.Problem(StatusCodes.Status404NotFound, "Registro del recupero non disponibile per questo run", "retrieval_registry_not_available");
        }

        if (outputs.Count > 1)
        {
            return EvidenceEndpoints.Problem(StatusCodes.Status409Conflict, "Il run ha più di un registro del recupero", DocumentWorkException.IntegrityConflict);
        }

        RetrievalRegistry registry;
        try
        {
            registry = RetrievalOutput.Read(outputs[0].PayloadJson);
        }
        catch (DocumentWorkException ex)
        {
            return EvidenceEndpoints.Problem(StatusCodes.Status409Conflict, ex.Message, ex.Code);
        }

        return Results.Ok(ToView(analysisId, registry));
    }

    /// <summary>Proiezione pura del registro: l'ordine dei candidati è quello finale (rango 1-based), gli esclusi restano con il motivo. Nessuna interpretazione: il registro non dichiara mai «assente».</summary>
    public static RetrievalRegistryApiView ToView(Guid analysisId, RetrievalRegistry registry) =>
        new(
            analysisId, registry.SearchBasis, registry.Outcome, registry.RetrievalComplete, registry.Degraded, registry.Causes, registry.PolicyVersion,
            [.. registry.Gaps.Select(g => new RetrievalGapView(
                g.GapEvidenceId, g.Query,
                [.. g.Candidates.OrderBy(c => c.FinalRank).Select(c => new RetrievalCandidateView(
                    c.FinalRank, c.DocumentName, c.DocumentVersion, c.Section, c.Range[0], c.Range[1], c.LexicalRank, c.DenseRank, c.RrfRank, c.Origin, c.InPackage, c.ExcludedReason))]))]);
}
