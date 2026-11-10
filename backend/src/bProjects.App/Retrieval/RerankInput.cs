using System.Globalization;
using System.Text;

namespace bProjects.App.Retrieval;

/// <summary>
/// Forma del documento inviato al riordino per ciascun candidato (003 T027, proposta approvata in <c>M2-proposal-rerank-context.md</c>). Funzioni pure.
/// <list type="bullet">
/// <item><see cref="TextOnly"/>: solo il testo indicizzato (<c>index-text-1</c>). È il comportamento di <c>retrieval-policy-1</c>, invariato.</item>
/// <item><see cref="Provenance1"/> (<c>rerank-input-1</c>): righe <c>Documento: …</c>, <c>Sezione: …</c> (solo se il percorso dei titoli non è vuoto) e poi il testo indicizzato
/// del blocco, immutato e INTERO (nessun troncamento).</item>
/// </list>
/// Riguarda SOLO l'input del riordino: l'indice, gli embedding, <c>index-text-1</c> e la ricerca non cambiano (A-5 vieta il titolo ripetuto nel testo indicizzato, non qui).
/// Nome del documento e titoli sono metadati NON attendibili: ogni valore è normalizzato su una sola riga (a capo, tabulazioni e caratteri di controllo non possono
/// aprire un nuovo campo né imitare un'etichetta a inizio riga) e il riordino è un modello di punteggio, non interpreta istruzioni.
/// </summary>
public static class RerankInput
{
    /// <summary>Solo testo del blocco (<c>retrieval-policy-1</c>); non compare nell'impronta della politica per non cambiare quella già usata.</summary>
    public const string TextOnly = "text-only";

    /// <summary>Documento + sezione + testo (<c>rerank-input-1</c>).</summary>
    public const string Provenance1 = "rerank-input-1";

    public const string DocumentLabel = "Documento: ";

    public const string SectionLabel = "Sezione: ";

    public static bool IsKnown(string mode) => mode is TextOnly or Provenance1;

    /// <summary>Documento inviato per un blocco. <paramref name="indexText"/> è già <c>index-text-1</c>; <paramref name="section"/> è il percorso dei titoli unito da « &gt; » (vuoto se assente).</summary>
    public static string Compose(string mode, string documentName, string section, string indexText)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(documentName);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(indexText);
        switch (mode)
        {
            case TextOnly:
                return indexText;
            case Provenance1:
                var sb = new StringBuilder().Append(DocumentLabel).Append(Field(documentName));
                var path = Field(section);
                if (path.Length > 0)
                {
                    sb.Append('\n').Append(SectionLabel).Append(path);
                }

                return sb.Append('\n').Append(indexText).ToString();
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Modalità di input del riordino sconosciuta.");
        }
    }

    /// <summary>Metadato su UNA riga: stessa normalizzazione di <see cref="IndexText.Of"/> (NFC, spazi e a capo in uno spazio) e rimozione degli altri caratteri di controllo e di formato.</summary>
    private static string Field(string value)
    {
        var cleaned = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || !(char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            {
                cleaned.Append(c);
            }
        }

        return IndexText.Of(cleaned.ToString());
    }
}
