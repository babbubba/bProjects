namespace bProjects.IntegrationTests;

/// <summary>Regole di verifica del test reale N1/N3 provate senza DGX: falsi positivi/negativi dimostrati e casi che devono restare violazioni.</summary>
public sealed class AnalysisVerificationRulesTests
{
    [Theory]
    [InlineData("Divieto di accesso anonimo ai dati degli ordini.")]
    [InlineData("Esiste il divieto di accesso anonimo ai dati degli ordini.")]
    [InlineData("Non è ammesso l'accesso anonimo ai dati degli ordini.")]
    [InlineData("L'accesso anonimo è vietato.")]
    public void Negation_is_recognised(string text) => Assert.True(AnalysisVerificationRules.IsNegative(text));

    [Theory]
    [InlineData("E' previsto un divieto di accesso anonimo?")]
    [InlineData("Se ci fosse un divieto di accesso anonimo, gli ordini sarebbero protetti.")]
    [InlineData("Ipotesi: divieto di accesso anonimo ai dati.")]
    [InlineData("Un esempio di requisito e' «divieto di accesso anonimo».")]
    [InlineData("Gli utenti anonimi possono accedere ai dati degli ordini.")]
    [InlineData("Accesso anonimo ai dati degli ordini.")]
    public void Divieto_in_hypothetical_question_or_quoted_form_or_absent_is_not_a_negation(string text) => Assert.False(AnalysisVerificationRules.IsNegative(text));

    // Falsi positivi dimostrati (terza esecuzione N3): domanda e discrepanza non affermano un termine.
    [Theory]
    [InlineData("Il termine di pagamento e' 30 oppure 60 giorni?")]
    [InlineData("Qual e' il termine di pagamento: 30 giorni o 60 giorni?")]
    [InlineData("I documenti riportano rispettivamente 30 e 60 giorni come termine di pagamento.")]
    [InlineData("Discrepanza sul pagamento: la scheda A indica 30 giorni, la scheda B indica 60 giorni.")]
    [InlineData("Il termine di pagamento e' in conflitto: 30 giorni nella scheda A e 60 giorni nella scheda B.")]
    public void Question_or_discrepancy_does_not_assert_a_payment_term(string text) => Assert.False(AnalysisVerificationRules.AssertsPaymentTerm(text));

    // Devono restare violazioni.
    [Theory]
    [InlineData("Il pagamento deve avvenire entro 30 giorni.")]
    [InlineData("Il termine di pagamento e' di 60 giorni.")]
    [InlineData("Pagamento a 60 giorni.")]
    [InlineData("I documenti riportano 30 giorni e 60 giorni per il pagamento; si applica 60 giorni.")]
    [InlineData("Il pagamento avviene a 60 giorni, non a 30 giorni.")]
    [InlineData("Le schede riportano rispettivamente 30 e 60 giorni per il pagamento. Il pagamento deve avvenire entro 30 giorni.")]
    [InlineData("Il termine di pagamento e' 30 oppure 60 giorni? Vale 60 giorni.")]
    public void Chosen_payment_term_stays_a_violation(string text) => Assert.True(AnalysisVerificationRules.AssertsPaymentTerm(text));
}
