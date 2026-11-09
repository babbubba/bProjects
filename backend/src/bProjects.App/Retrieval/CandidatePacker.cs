namespace bProjects.App.Retrieval;

/// <summary>
/// Esito della composizione del pacchetto. <see cref="Package"/> in ordine di consegna (prima le evidenze sempre incluse, poi per rango finale); <see cref="Excluded"/> con motivo
/// <c>budget</c>. <see cref="Complete"/> è falso se un'evidenza sempre inclusa o un candidato entro la finestra di riordino è stato escluso per budget.
/// </summary>
public sealed record PackResult(IReadOnlyList<Guid> Package, IReadOnlyList<RegistryExclusion> Excluded, bool Complete);

/// <summary>
/// Composizione a budget del pacchetto per lo stadio (003 T025, FR-012, FR-017, A-4). Funzione pura e deterministica. Si scende per rango finale accumulando evidenze INTERE
/// finché il budget di input dello stadio non è esaurito: nessun troncamento a metà passaggio, nessuna scelta per punteggio. Regole:
/// <list type="bullet">
/// <item>le evidenze sempre incluse (<c>Message</c> non Gap, FR-006) vengono prima, in ordine di catalogo; una che non entra è esclusa e il recupero è incompleto, ma le altre proseguono;</item>
/// <item>fra i candidati ordinati ci si ferma alla prima evidenza che non entra (nessun salto a evidenze di rango inferiore, che sarebbe una scelta non ordinata); tutte le successive sono escluse con motivo <c>budget</c>;</item>
/// <item>un passaggio più lungo dell'intero budget non entra mai e non viene accorciato: il pacchetto resta senza di esso e senza quelli che lo seguono;</item>
/// <item>il costo di ogni evidenza è un conteggio MISURATO dal chiamante (<see cref="RegistryBudget.Cost"/>); un costo mancante o negativo è un errore, mai zero né una stima;</item>
/// <item><see cref="PackResult.Complete"/> è falso se è escluso un candidato entro <paramref name="rerankWindow"/> (rango finale ≤ finestra); i candidati oltre la finestra, esclusi per budget, sono registrati ma non rendono incompleto il recupero.
/// Senza finestra (M1, senza riordino) ogni esclusione lo rende incompleto.</item>
/// </list>
/// Il budget assente significa «nessun limite» (il chiamante non ha un budget da applicare).
/// </summary>
public static class CandidatePacker
{
    public static PackResult Pack(IReadOnlyList<Guid> always, IReadOnlyList<(Guid EvidenceId, int Rank)> ranked, RegistryBudget? budget, int? rerankWindow = null)
    {
        ArgumentNullException.ThrowIfNull(always);
        ArgumentNullException.ThrowIfNull(ranked);
        if (budget is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(budget.Limit);
        }

        var package = new List<Guid>();
        var excluded = new List<RegistryExclusion>();
        var complete = true;
        var remaining = budget?.Limit ?? long.MaxValue;

        bool Fits(Guid id)
        {
            if (budget is null)
            {
                return true;
            }

            var cost = CostOf(budget, id);
            if (cost > remaining)
            {
                return false;
            }

            remaining -= cost;
            return true;
        }

        foreach (var id in always)
        {
            if (Fits(id))
            {
                package.Add(id);
            }
            else
            {
                excluded.Add(new(id, CandidateRegistry.ReasonBudget));
                complete = false;
            }
        }

        var stopped = false;
        foreach (var (id, rank) in ranked)
        {
            if (!stopped && Fits(id))
            {
                package.Add(id);
                continue;
            }

            stopped = true;
            excluded.Add(new(id, CandidateRegistry.ReasonBudget));
            if (rerankWindow is not { } window || rank <= window)
            {
                complete = false;
            }
        }

        return new(package, excluded, complete);
    }

    private static long CostOf(RegistryBudget budget, Guid evidenceId)
    {
        if (!budget.Cost.TryGetValue(evidenceId, out var cost))
        {
            throw new ArgumentException($"Costo non misurato per l'evidenza {evidenceId}: il budget richiede un conteggio misurato per ogni candidato, nessuna stima.", nameof(budget));
        }

        return cost >= 0 ? cost : throw new ArgumentOutOfRangeException(nameof(budget), $"Costo negativo per l'evidenza {evidenceId}.");
    }
}
