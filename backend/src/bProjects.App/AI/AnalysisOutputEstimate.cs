namespace bProjects.App.AI;

/// <summary>
/// Stima PREVENTIVA e conservativa dell'output della chiamata singola di analisi documentale (002 T082, instradamento oltre il budget di output).
/// Il controllo del budget di INPUT da solo non basta: poche evidenze brevi entrano nel contesto, ma la risposta (una voce per evidenza, più le liste
/// <c>visited</c> A e B che le ripetono tutte, le valutazioni dei Gap e lo scheletro JSON) può superare il tetto di output O e viene troncata; il troncamento
/// non è ritentabile (AI §5.3). Quando la stima, con il suo margine, supera O la chiamata singola non è affidabile e si instrada alla riconciliazione
/// gerarchica (o al fail-fast previsto per i Gap, <see cref="DocumentWorkException.HierarchicalGapAssessmentUnsupported"/>): mai un invio che può troncare.
/// </summary>
/// <remarks>
/// Criterio generale: dipende solo da numero di evidenze, lunghezza degli enunciati, numero di Gap e di relazioni note, non dal corpus. I coefficienti sono
/// LIMITI SUPERIORI prudenti (non una misura puntuale) e sono fissati dalla versione <see cref="PolicyVersion"/>, congelata nel run alla richiesta: cambiarli
/// richiede una nuova versione, mai una modifica di questa; i run storici (senza la chiave) mantengono l'instradamento sul solo input.
/// Unica misura reale disponibile al momento della policy: 297 evidenze di un documento oltre contesto hanno prodotto più di 16384 token (≥ 55 per evidenza);
/// quel valore è un limite inferiore da una risposta troncata e NON è usato come fattore.
/// </remarks>
public static class AnalysisOutputEstimate
{
    /// <summary>Versione della policy di stima congelata nelle versioni del run (<c>analysisOutputPolicy</c>).</summary>
    public const string PolicyVersion = "single-call-output-estimate-1";

    /// <summary>Caratteri per token assunti per i testi in italiano dentro JSON: prudente (la lingua corrente fa ~3,5–4, il JSON e i nomi propri peggiorano).</summary>
    public const double CharsPerToken = 2.5;

    /// <summary>Il modello può riformulare l'enunciato nel testo della voce: lunghezza attesa rispetto all'enunciato.</summary>
    public const double TextExpansion = 1.25;

    /// <summary>Scheletro fisso: nomi delle nove proprietà, sezioni di <c>items</c>, spiegazioni <c>noFindings</c>.</summary>
    public const int FixedTokens = 600;

    /// <summary>Per evidenza non-Gap, una voce: chiave, classificazione, array delle evidenze, punteggiatura e indentazione.</summary>
    public const int ItemStructureTokens = 40;

    /// <summary>Per evidenza: l'alias compare in entrambe le liste <c>visited</c> (A e B), con separatori e a capo.</summary>
    public const int VisitedTokens = 14;

    /// <summary>Per Gap: record di <c>gapAssessments</c> con motivazione e voce di <c>openQuestions</c> collegata.</summary>
    public const int GapTokens = 200;

    /// <summary>Per relazione nota: verdetto in <c>knownRelationChecks</c>.</summary>
    public const int RelationTokens = 50;

    /// <summary>Riserva per i conflitti (alternative che ripetono i testi) e per le voci aggiuntive: quota del totale.</summary>
    public const double ConflictReserve = 0.08;

    /// <summary>Margine di sicurezza moltiplicativo sul totale.</summary>
    public const double SafetyMargin = 1.25;

    /// <summary>
    /// Token di output attesi (limite superiore prudente) per un'analisi a chiamata singola sulle evidenze date. <paramref name="statementLengths"/>: lunghezza in
    /// caratteri dell'enunciato di ogni evidenza NON Gap; <paramref name="gaps"/>: numero di Gap; <paramref name="relations"/>: relazioni note.
    /// </summary>
    public static long Estimate(IEnumerable<int> statementLengths, int gaps, int relations)
    {
        ArgumentNullException.ThrowIfNull(statementLengths);
        ArgumentOutOfRangeException.ThrowIfNegative(gaps);
        ArgumentOutOfRangeException.ThrowIfNegative(relations);
        double total = FixedTokens + (double)gaps * (GapTokens + VisitedTokens) + (double)relations * RelationTokens;
        foreach (var length in statementLengths)
        {
            total += ItemStructureTokens + VisitedTokens + Math.Ceiling(Math.Max(0, length) * TextExpansion / CharsPerToken);
        }

        return (long)Math.Ceiling(total * (1 + ConflictReserve) * SafetyMargin);
    }

    /// <summary>
    /// La stima non entra nel tetto di output <paramref name="outputTokens"/> del profilo (O, ragionamento incluso): la chiamata singola non è affidabile.
    /// Un tetto non positivo è trattato come superato (nessun invio incerto).
    /// </summary>
    public static bool Exceeds(IEnumerable<int> statementLengths, int gaps, int relations, int outputTokens) =>
        outputTokens <= 0 || Estimate(statementLengths, gaps, relations) > outputTokens;
}
