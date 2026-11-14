using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>Spec di una evidenza del ledger di prova della M2: origine, tipo, certezza ed enunciato (le fonti sono derivate dal numero).</summary>
internal sealed record M2Evidence(EvidenceOriginKind Origin, EvidenceKind Kind, EvidenceCertainty Certainty, string Statement);

/// <summary>
/// Ledger di prova della M2 (reconciliation-hardening RH-020/RH-021, solo test): dieci evidenze che coprono le categorie del prodotto — Gap del PM e Gap di documento, requisiti contesi, decisione,
/// certezza <c>ToClarify</c>, alternativa, ipotesi/assunzione, requisito inferito. Gli alias sono E1…E10 nell'ordine del ledger. Funzioni pure: nessun GUID casuale.
/// </summary>
internal static class M2Ledger
{
    public const int GapPm = 1, GapDoc = 2, LimitFive = 3, LimitEight = 4, Decision = 5, ToClarify = 6, Alternative = 7, Hypothesis = 8, PmRequirement = 9, Inferred = 10;

    public static IReadOnlyList<M2Evidence> Specs { get; } =
    [
        new(EvidenceOriginKind.Message, EvidenceKind.Gap, EvidenceCertainty.Declared, "Chi gestisce gli scarti della DESADV?"),
        new(EvidenceOriginKind.Document, EvidenceKind.Gap, EvidenceCertainty.Declared, "Quale tracciato EDI è previsto per le conferme d'ordine?"),
        new(EvidenceOriginKind.Document, EvidenceKind.Requirement, EvidenceCertainty.Declared, "Il limite giornaliero è di 5 ordini per cliente."),
        new(EvidenceOriginKind.Document, EvidenceKind.Requirement, EvidenceCertainty.Declared, "Il limite giornaliero è di 8 ordini per cliente."),
        new(EvidenceOriginKind.Document, EvidenceKind.Decision, EvidenceCertainty.Declared, "Si adotta il tracciato EDI versione 2 per i nuovi clienti."),
        new(EvidenceOriginKind.Document, EvidenceKind.Requirement, EvidenceCertainty.ToClarify, "Il ritardo massimo di consegna accettato è ancora da chiarire."),
        new(EvidenceOriginKind.Document, EvidenceKind.Alternative, EvidenceCertainty.Declared, "In alternativa gli ordini arrivano dal portale web."),
        new(EvidenceOriginKind.Document, EvidenceKind.Assumption, EvidenceCertainty.Hypothesized, "Si presume che il cliente invii un solo file al giorno."),
        new(EvidenceOriginKind.Message, EvidenceKind.Requirement, EvidenceCertainty.Declared, "Servono almeno 10 ordini al giorno."),
        new(EvidenceOriginKind.Document, EvidenceKind.Requirement, EvidenceCertainty.Inferred, "Il canale preferito per gli ordini è EDI."),
    ];

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static DocumentAnalysisEvidence EvidenceOf(int n, M2Evidence spec)
    {
        var quote = $"Passo {n}: " + spec.Statement;
        var source = spec.Origin == EvidenceOriginKind.Message
            ? AliasFixtures.Msg(AliasFixtures.G(800 + n), AliasFixtures.M1, 1, quote) with { Start = 1000 * n, End = 1000 * n + quote.Length }   // stesso messaggio, intervalli distinti
            : AliasFixtures.Doc(AliasFixtures.G(800 + n), AliasFixtures.G(1000 + n), 0, 1 + n % 3, n, 100 * n, quote);
        return new(AliasFixtures.EvidenceId(n), AliasFixtures.G(200 + n), spec.Origin, spec.Kind, EvidencePolarity.Affirmed, spec.Certainty, spec.Statement, Sha(spec.Statement), [source]);
    }

    public static DocumentAnalysisInput Input(IReadOnlyList<M2Evidence>? specs = null, IReadOnlyList<SnapshotRelation>? relations = null)
    {
        var list = (specs ?? Specs).Select((s, i) => EvidenceOf(i + 1, s)).ToList();
        return AliasFixtures.Input(evidence: list, relations: relations);
    }

    public static IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> ById(DocumentAnalysisInput input) => input.Evidence.ToDictionary(e => e.EvidenceId);

    /// <summary>Candidato Evidence–Evidence fra le evidenze <paramref name="a"/> e <paramref name="b"/> (numeri del ledger) come in esame in un confronto.</summary>
    public static ReconcileCandidate Pair(DocumentAnalysisInput input, int a, int b, ReconcileProposedType type = ReconcileProposedType.Contradicts) =>
        ReconcileBatchWorld.PairFocus(input, a, b, type);
}
