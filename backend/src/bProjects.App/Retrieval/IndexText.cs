using System.Text;
using System.Text.Json;
using bProjects.App.AI;

namespace bProjects.App.Retrieval;

/// <summary>
/// Testo indicizzato <c>index-text-1</c> e identità della configurazione d'indice (003 T009, A-5, FR-004, FR-007). Funzioni pure.
/// Il testo è il <c>CanonicalText</c> del blocco figlio normalizzato (Unicode NFC, spazi): NESSUN titolo ripetuto né altro contesto aggiunto,
/// così il segnale lessicale e quello denso vedono lo stesso testo; sezione e provenienza si leggono dal blocco, non dall'indice.
/// </summary>
public static class IndexText
{
    /// <summary>
    /// Normalizzazione dichiarata di <c>index-text-1</c>: (1) Unicode NFC; (2) ogni sequenza di spazi Unicode (a capo, tabulazioni, NBSP...) diventa un solo
    /// spazio ASCII; (3) niente spazi iniziali o finali. Nessun cambio di maiuscole, punteggiatura o cifre (il lessicale italiano e il modello le gestiscono).
    /// </summary>
    public static string Of(string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(canonicalText);
        var nfc = canonicalText.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(nfc.Length);
        var pendingSpace = false;
        foreach (var c in nfc)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Hash (SHA-256 esadecimale minuscolo, 64 caratteri) del testo indicizzato: rileva voci stantie rispetto al blocco.</summary>
    public static string TextHash(string canonicalText) => CanonicalJson.HashText(Of(canonicalText));

    /// <summary>
    /// <c>IndexConfigHash</c> = hash di (versione del testo, modello + artefatto + dimensione dell'embedding, configurazione FTS, versione del parser/manifest).
    /// Qualunque campo diverso ⇒ hash diverso ⇒ nuove voci (i run storici non dipendono dall'indice: il registro è congelato). Il titolo non è un input.
    /// </summary>
    public static string ConfigHash(RetrievalOptions options, string parserVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        return ConfigHash(RetrievalOptions.IndexTextVersion, options.Embedding.ModelId, options.Embedding.Artifact, options.Embedding.Dimension, RetrievalOptions.FtsConfiguration, parserVersion);
    }

    public static string ConfigHash(string textVersion, string embeddingModel, string embeddingArtifact, int embeddingDimension, string ftsConfiguration, string parserVersion)
    {
        var json = JsonSerializer.Serialize(new
        {
            textVersion,
            embeddingModel,
            embeddingArtifact,
            embeddingDimension,
            ftsConfiguration,
            parserVersion,
        });
        return CanonicalJson.HashText(CanonicalJson.Canonicalize(json));
    }
}
