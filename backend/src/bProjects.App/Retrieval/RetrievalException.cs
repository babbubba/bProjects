namespace bProjects.App.Retrieval;

/// <summary>
/// Errore tipizzato del recupero (003, contracts/index-and-registry.md §3). Il <see cref="Code"/> è il motivo stabile (prefisso testuale di <c>ErrorMessage</c>,
/// come nella 002); il messaggio è un'azione leggibile per l'utente e non contiene mai testi dei documenti, vettori o segreti.
/// </summary>
public class RetrievalException(string code, string message) : Exception(message)
{
    public const string EmbeddingUnavailable = "embedding_unavailable";
    public const string EmbeddingModelMismatch = "embedding_model_mismatch";
    public const string EmbeddingDimensionMismatch = "embedding_dimension_mismatch";
    public const string EmbeddingInputTooLong = "embedding_input_too_long";
    public const string EmbeddingMalformedResponse = "embedding_malformed_response";
    public const string RerankerUnavailable = "reranker_unavailable";
    public const string RerankerMalformedResponse = "reranker_malformed_response";
    public const string IndexNotReady = "retrieval_index_not_ready";
    public const string IndexBuildFailed = "retrieval_index_build_failed";

    public string Code { get; } = code;

    public override string ToString() => $"{Code}: {Message}";
}
