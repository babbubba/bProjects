using System.Text.RegularExpressions;

namespace bProjects.IntegrationTests;

/// <summary>
/// Regole di verifica testuali del test reale <see cref="DocumentSmallFlowMafRealTests"/>, estratte per poterle provare senza DGX.
/// Sono regole del TEST (non requisiti di prodotto).
/// </summary>
internal static class AnalysisVerificationRules
{
    private static readonly Regex NegativeMarker = new(@"\b(non|nessun\w*|senza|vietat\w*|vieta\w*|esclus\w*|escluder\w*|esclud\w*|mai|tranne)\b", RegexOptions.Compiled);

    // «divieto» e l'unica forma aggiunta (N1, quinta esecuzione: «Divieto di accesso anonimo...» e una negazione corretta). Conta solo se asserito:
    // non in una domanda, non in un periodo ipotetico/di ipotesi, non citato come esempio tra virgolette.
    private static readonly Regex DivietoMarker = new(@"\bdivieto\b", RegexOptions.Compiled);
    private static readonly Regex NonAssertive = new(@"\?|[«""“]|\b(se|qualora|ipotesi|ipotetic\w*|esempio|ad esempio|supponendo|nel caso)\b", RegexOptions.Compiled);

    /// <summary>Il testo (anche non normalizzato) esprime una negazione/esclusione.</summary>
    internal static bool IsNegative(string text)
    {
        var t = DocumentSmallFlowMafRealTests.Fold(text);
        return NegativeMarker.IsMatch(t) || (DivietoMarker.IsMatch(t) && !NonAssertive.IsMatch(t));
    }

    private static readonly Regex Sentence = new(@"[^.;!?]+[.;!?]?", RegexOptions.Compiled);
    private static readonly Regex Term = new(@"\b(30|60) giorni\b", RegexOptions.Compiled);
    private static readonly Regex BothNumbers = new(@"\b30\b.*\b60\b|\b60\b.*\b30\b", RegexOptions.Compiled);
    private static readonly Regex DiscrepancyMarker = new(@"\b(rispettivamente|discrepanz\w*|divergen\w*|conflitt\w*|contrast\w*|incongru\w*|differisc\w*|differen\w*)\b", RegexOptions.Compiled);
    private static readonly Regex ChoiceMarker = new(@"\b(si applica|vale|valgono|prevale|scelt\w*|adott\w*|selezionat\w*|confermat\w*|deve|devono|dovra|entro|avviene|e' di|non (a )?(30|60))\b", RegexOptions.Compiled);

    /// <summary>
    /// Il testo afferma un termine di pagamento di 30 o 60 giorni (la regola N3 lo vieta fuori dal conflitto). Una frase non lo afferma se e una
    /// domanda (termina con «?») o descrive la discrepanza citando entrambi i numeri con un marcatore di discrepanza e senza scelta di un termine.
    /// </summary>
    internal static bool AssertsPaymentTerm(string text)
    {
        var t = DocumentSmallFlowMafRealTests.Fold(text);
        if (!t.Contains("pagamento", StringComparison.Ordinal))
        {
            return false;
        }

        return Sentence.Matches(t).Select(m => m.Value.Trim()).Where(s => Term.IsMatch(s)).Any(s =>
        {
            if (s.EndsWith('?'))
            {
                return false;
            }

            var describesDiscrepancy = BothNumbers.IsMatch(s) && DiscrepancyMarker.IsMatch(s) && !ChoiceMarker.IsMatch(s);
            return !describesDiscrepancy;
        });
    }
}
