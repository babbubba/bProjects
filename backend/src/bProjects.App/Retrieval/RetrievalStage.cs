using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace bProjects.App.Retrieval;

/// <summary>
/// Identità del recupero nello stadio Gap (003 T028–T030, contracts/candidate-source.md §4): chiavi di freeze della definizione <c>project-analysis-documents-v7</c>, schema del
/// WorkOutput del registro e stadio del batch deterministico. Nessun LLM: il batch <c>retrieval</c> non consuma <c>MaxAiCallsPerRun</c>.
/// </summary>
public static class RetrievalStage
{
    public const string IndexKey = "retrievalIndex";
    public const string PolicyKey = "retrievalPolicy";
    public const string RegistryKey = "retrievalRegistry";
    public const string OutputKey = "retrievalOutput";
    public const string ModelsKey = "retrievalModels";

    /// <summary>Stadio del batch deterministico del recupero (accanto a <c>gap</c>): non invia nulla al modello generativo.</summary>
    public const string BatchStage = "retrieval";

    /// <summary>Schema del WorkOutput del registro congelato (JSONB esistente di <c>WorkOutput</c>).</summary>
    public const string OutputSchema = "retrieval-output-1";

    public const string ValidationVersion = "retrieval-validation-1";

    /// <summary>Algoritmo del batch: lega il recupero (indice, registro, politica) all'identità del batch.</summary>
    public const string Algorithm = "retrieval-stage-1";

    /// <summary>Le cinque chiavi della v7, in ordine canonico.</summary>
    public static IReadOnlyList<string> Keys { get; } = [IndexKey, PolicyKey, RegistryKey, OutputKey, ModelsKey];

    /// <summary>
    /// Valori congelati da una NUOVA richiesta v7: versione del testo indicizzato, politica scelta, contratti del registro e dell'output, modelli (nome/artefatto/dimensione).
    /// L'<c>IndexConfigHash</c> dipende dalla versione del parser di ogni preparazione: non è noto alla richiesta, è registrato nel registro e nell'<c>inputHash</c> del batch.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FreezeValues(RetrievalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IndexKey] = RetrievalOptions.IndexTextVersion,
            [PolicyKey] = options.PolicyVersion,
            [RegistryKey] = CandidateRegistry.Contract,
            [OutputKey] = OutputSchema,
            [ModelsKey] = ModelsValue(options),
        };
    }

    public static string ModelsValue(RetrievalOptions options) =>
        $"{options.Embedding.ModelId}/{options.Embedding.Artifact}/{options.Embedding.Dimension}|{options.Reranker.ModelId}/{options.Reranker.Artifact}";

    /// <summary>Un valore congelato è ammesso solo se è uno dei contratti o delle politiche supportati (insieme chiuso); i modelli sono una stringa non vuota.</summary>
    public static bool IsSupported(string key, string? value) => key switch
    {
        IndexKey => value == RetrievalOptions.IndexTextVersion,
        PolicyKey => RetrievalPolicy.ByVersion(value) is not null,
        RegistryKey => value == CandidateRegistry.Contract,
        OutputKey => value == OutputSchema,
        ModelsKey => !string.IsNullOrWhiteSpace(value),
        _ => false,
    };

    /// <summary>JSON delle versioni congelate di un run: chiavi presenti (qualunque valore) e valori.</summary>
    public static (bool AnyKey, bool AllSupported) Inspect(string? promptVersions)
    {
        if (promptVersions is null)
        {
            return (false, false);
        }

        try
        {
            using var document = JsonDocument.Parse(promptVersions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (false, false);
            }

            var any = false;
            var all = true;
            foreach (var key in Keys)
            {
                if (document.RootElement.TryGetProperty(key, out var value))
                {
                    any = true;
                    all &= value.ValueKind == JsonValueKind.String && IsSupported(key, value.GetString());
                }
                else
                {
                    all = false;
                }
            }

            return (any, all);
        }
        catch (JsonException)
        {
            return (false, false);
        }
    }
}

/// <summary>
/// Persistenza del registro nel WorkOutput <c>retrieval-output-1</c> e traduzione degli esiti non consegnabili in errori tipizzati del run (003 T030, T031). Nessuna nuova tabella: il payload
/// vive nel JSONB esistente di <c>WorkOutput</c>.
/// </summary>
public static class RetrievalOutput
{
    /// <summary>Payload del WorkOutput: contratto, esito, hash del registro e registro canonico. Deterministico: stesso registro, stesso payload.</summary>
    public static string Build(RetrievalRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["schemaVersion"] = RetrievalStage.OutputSchema,
            ["outcome"] = registry.Outcome,
            ["registryHash"] = registry.Hash(),
            ["registry"] = System.Text.Json.Nodes.JsonNode.Parse(registry.ToCanonicalJson()),
        }.ToJsonString();
    }

    /// <summary>Registro dal payload di un WorkOutput Committed: schema e hash verificati (<c>work_output_invalid</c> altrimenti), mai ricostruito.</summary>
    public static RetrievalRegistry Read(string? payloadJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson ?? throw new AI.DocumentWorkException(AI.DocumentWorkException.WorkOutputInvalid, "Payload del registro mancante."));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != RetrievalStage.OutputSchema)
            {
                throw new AI.DocumentWorkException(AI.DocumentWorkException.CheckpointIncompatible, "Il WorkOutput del recupero non è dello schema congelato.");
            }

            var registry = RetrievalRegistry.FromJson(root.GetProperty("registry").GetRawText());
            return registry.Hash() == root.GetProperty("registryHash").GetString() && registry.Contract == CandidateRegistry.Contract
                ? registry
                : throw new AI.DocumentWorkException(AI.DocumentWorkException.IntegrityConflict, "L'hash del registro del recupero non corrisponde al suo contenuto.");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AI.DocumentWorkException(AI.DocumentWorkException.WorkOutputInvalid, "Il WorkOutput del recupero non è decodificabile.");
        }
    }

    /// <summary>
    /// Esito non consegnabile (<c>Failed</c>/<c>Diagnostic</c>) come errore tipizzato del run (FR-020, FR-035): codice stabile = causa (embedding/indice prima del riordino, D-3), messaggio con l'azione
    /// per l'utente. Nessuna riserva, nessun invio LLM, nessun aumento di budget né cambio di modello. Null se il registro è consegnabile.
    /// </summary>
    public static AI.DocumentWorkException? ToFailure(RetrievalRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.Outcome == CandidateRegistry.OutcomePackage)
        {
            return null;
        }

        var code = registry.Causes.FirstOrDefault(c => c is not (RetrievalException.RerankerUnavailable or RetrievalException.RerankerMalformedResponse))
            ?? registry.Causes.FirstOrDefault() ?? RetrievalException.IndexNotReady;
        var action = code switch
        {
            RetrievalException.IndexNotReady => "L'indice di ricerca delle fonti non è pronto: attendi il completamento dell'indicizzazione (o la ricostruzione dell'indice) e rilancia l'analisi.",
            RetrievalException.RerankerUnavailable or RetrievalException.RerankerMalformedResponse =>
                "Il servizio di riordino dei candidati non è disponibile: il registro diagnostico è consultabile, ma nessuna valutazione dei Gap è stata presentata. Ripristina il servizio e rilancia l'analisi.",
            _ => "Il servizio di embedding non è disponibile o non è conforme alla configurazione: ripristinalo e rilancia l'analisi.",
        };
        return new(code, $"{code}: {action} Nessuna valutazione parziale è stata presentata e nessuna chiamata al modello è stata eseguita (zero invii).");
    }
}

/// <summary>Configurazione d'indice effettiva di uno snapshot (003 T030): dipende dalla versione del parser delle preparazioni fissate, quindi si calcola a runtime e entra nell'<c>inputHash</c> del batch.</summary>
public static class RetrievalSnapshot
{
    /// <summary>
    /// <c>IndexConfigHash</c> dello snapshot: una sola versione del parser fra le preparazioni incluse e Ready, altrimenti <c>retrieval_index_not_ready</c> (la ricerca usa una configurazione per volta;
    /// versioni miste richiedono di rielaborare le fonti con lo stesso parser). Nessun valore dedotto.
    /// </summary>
    public static async Task<string> IndexConfigHashAsync(Persistence.AppDbContext db, Guid snapshotId, RetrievalOptions options, CancellationToken ct)
    {
        var parsers = await (from item in db.SourceSnapshotItems.AsNoTracking()
                             join preparation in db.DocumentPreparations.AsNoTracking() on item.PreparationId equals preparation.Id
                             where item.SnapshotId == snapshotId && item.Included && item.Availability == Documents.SourceAvailability.Ready
                             select preparation.ParserVersion).Distinct().ToListAsync(ct);
        return parsers.Count == 1 && !string.IsNullOrWhiteSpace(parsers[0])
            ? IndexText.ConfigHash(options, parsers[0]!)
            : throw new RetrievalException(RetrievalException.IndexNotReady, parsers.Count == 0
                ? "Lo snapshot non ha preparazioni Ready con una versione del parser: nessun indice utilizzabile."
                : "Le preparazioni dello snapshot hanno versioni del parser diverse: l'indice di ricerca usa una configurazione per volta.");
    }
}
