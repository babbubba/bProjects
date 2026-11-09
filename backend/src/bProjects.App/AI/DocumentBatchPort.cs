using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Specifica dell'agente batch di una fase: costruito con <see cref="AgentFactory.CreateBatchAgent"/> (nessuna history, nessun tool).</summary>
public sealed record BatchAgentSpec(string Name, string Id, string Instructions, ChatOptions ChatOptions, AiPhase Phase, string? ExpectedFingerprint = null);

/// <summary>Risposta validata: l'output da rendere durevole e i figli già determinabili, pubblicati nello stesso commit fenced.</summary>
public sealed record ValidatedBatch(WorkOutputInput Output, IReadOnlyList<DerivedBatchInput>? Children = null);

/// <summary>Lettura degli output Committed dell'owner per ordinale del batch (genitori, assemblaggio): sempre dal database, mai da una cache di processo.</summary>
public interface IBatchOutputs
{
    /// <summary>Output Committed del batch con quell'ordinale, o null se il batch non è pubblicato o non è ancora committato.</summary>
    ValueTask<WorkOutput?> GetByOrdinalAsync(int ordinal, CancellationToken ct);
}

/// <summary>
/// Parte di dominio di un workflow documentale multipasso: cosa si elabora e come si valida, non come si recupera. Il recupero (lookup,
/// riserva, sessione nuova, commit fenced) è del <see cref="DocumentBatchPort"/>, uguale per ogni piano. Ogni metodo è deterministico nel
/// replay: nessun Guid né orologio nell'identità dei batch.
/// </summary>
public interface IDocumentBatchPlan
{
    /// <summary>Manifesto iniziale e immutabile dell'owner (pubblicato da Load; identico al replay).</summary>
    WorkManifestInput Manifest { get; }

    /// <summary>Versioni di schema e validazione con cui un output Committed è riusabile.</summary>
    WorkLookupExpectation Expectation { get; }

    /// <summary>
    /// Versioni di schema e validazione di UN batch, quando differiscono da quelle del piano (es. lo stadio Gap della riconciliazione v6 ha schema e validazione propri). Predefinito: <see cref="Expectation"/>.
    /// </summary>
    WorkLookupExpectation ExpectationFor(BatchRef batch) => Expectation;

    /// <summary>Agente della fase.</summary>
    BatchAgentSpec Agent { get; }

    /// <summary>
    /// Agente di UN batch, quando le opzioni dell'invio dipendono dal batch (es. uno schema strict che ammette solo le chiavi dei suoi range).
    /// Predefinito: l'agente della fase. Stessa fase, stesso modello e stessa impronta congelata del manifesto.
    /// </summary>
    BatchAgentSpec AgentFor(BatchRef batch) => Agent;

    /// <summary>Vero se esiste un batch con questo ordinale (cursore del grafo).</summary>
    bool HasBatch(int ordinal);

    /// <summary>
    /// Come <see cref="HasBatch"/> per i piani il cui numero di batch dipende dagli output già Committed (riconciliazione gerarchica, T078): il cursore resta
    /// quello del grafo, mai un secondo scheduler. Predefinito: la risposta statica del piano.
    /// </summary>
    ValueTask<bool> HasBatchAsync(int ordinal, IBatchOutputs prior, CancellationToken ct) => new(HasBatch(ordinal));

    /// <summary>
    /// Come <see cref="Validate"/> per i piani che validano la risposta nel contesto degli output precedenti (la richiesta del batch è ricostruita dagli output
    /// Committed, anche dopo una ripresa in un processo nuovo). Predefinito: <see cref="Validate"/>.
    /// </summary>
    ValueTask<ValidatedBatch> ValidateAsync(BatchRef batch, string responseText, IBatchOutputs prior, CancellationToken ct) => new(Validate(batch, responseText));

    /// <summary>PrepareBatch: batch derivato dell'ordinale; un figlio che dipende da più output legge i genitori Committed e fallisce se mancano.</summary>
    ValueTask<DerivedBatchInput> PrepareAsync(int ordinal, IBatchOutputs prior, CancellationToken ct);

    /// <summary>Messaggi del solo batch (input e contesto esplicito materializzato).</summary>
    ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, IBatchOutputs prior, CancellationToken ct);

    /// <summary>Valida la risposta; non valida = eccezione (<see cref="DocumentWorkException.WorkOutputInvalid"/>), mai una riparazione né un arco alternativo.</summary>
    ValidatedBatch Validate(BatchRef batch, string responseText);

    /// <summary>Assemble: composizione senza inferenza dagli output Committed.</summary>
    ValueTask AssembleAsync(IBatchOutputs outputs, CancellationToken ct);

    /// <summary>Commit finale di dominio, idempotente; ritorna il numero di batch percorsi.</summary>
    ValueTask<int> CommitAsync(IBatchOutputs outputs, CancellationToken ct);
}

/// <summary>
/// Validazione fallita per una sola delle classi che ammettono il retry guidato unico (T129 bozza, T130 analisi; AI §9/§10): violazione tipizzata,
/// riconosciuta dal validatore e non da stringhe. È un <see cref="DocumentWorkException"/> permanente a tutti gli effetti (codice originale); il port la
/// intercetta per il solo retry guidato. <see cref="OutputHash"/> è l'hash del primo output (diagnostica non sensibile: mai il testo).
/// </summary>
public abstract class GuidedRetryTrigger(string code, string message, string? outputHash) : DocumentWorkException(code, message)
{
    public string? OutputHash { get; } = outputHash;
}

/// <summary>
/// Validazione della bozza fallita SOLO perché item obbligatori non sono rappresentati (T129, AI §9): <c>work_output_invalid</c> permanente.
/// </summary>
public sealed class DraftOmissionException(string message, IReadOnlyList<string> missingItemAliases, string? outputHash)
    : GuidedRetryTrigger(WorkOutputInvalid, message, outputHash)
{
    public IReadOnlyList<string> MissingItemAliases { get; } = missingItemAliases;
}

/// <summary>Classe di errore dell'analisi che ammette il retry guidato (T130, AI §10).</summary>
public enum AnalysisRetryKind
{
    /// <summary>Chiavi locali duplicate: <c>work_output_invalid</c>.</summary>
    DuplicateKeys,

    /// <summary>Evidenze non mappate su alcun item come unico gap: <c>coverage_incomplete</c>.</summary>
    UnmappedEvidence,
}

/// <summary>Analisi rifiutata SOLO per chiavi locali duplicate o evidenze non mappate (T130, AI §10); <see cref="Items"/> sono le chiavi o gli alias <c>E&lt;n&gt;</c>.</summary>
public sealed class AnalysisRetryException(string code, string message, AnalysisRetryKind kind, IReadOnlyList<string> items, string? outputHash)
    : GuidedRetryTrigger(code, message, outputHash)
{
    public AnalysisRetryKind Kind { get; } = kind;

    public IReadOnlyList<string> Items { get; } = items;
}

/// <summary>
/// Analisi single-call v5 rifiutata per una sola classe ammessa dal retry informato (T137, AI §13.3): chiavi locali duplicate come unico difetto, oppure soli
/// <c>evidence_not_mapped</c>/<c>gap_not_assessed</c> (anche insieme). Porta SOLO chiavi e alias esatti e l'hash del primo output: mai il testo della risposta respinta.
/// </summary>
public sealed class InformedAnalysisRetryException(
    string code, string message, IReadOnlyList<string> duplicateKeys, IReadOnlyList<string> unmappedEvidence, IReadOnlyList<string> unassessedGaps, string? outputHash)
    : GuidedRetryTrigger(code, message, outputHash)
{
    public IReadOnlyList<string> DuplicateKeys { get; } = duplicateKeys;

    /// <summary>Alias <c>E&lt;n&gt;</c> non-Gap che nessun item cita.</summary>
    public IReadOnlyList<string> UnmappedEvidence { get; } = unmappedEvidence;

    /// <summary>Alias <c>E&lt;n&gt;</c> dei Gap senza valutazione finale.</summary>
    public IReadOnlyList<string> UnassessedGaps { get; } = unassessedGaps;
}

/// <summary>
/// Estrazione rifiutata SOLO perché tutte le violazioni sono <c>kind Other</c> senza <c>kindRationale</c> (T134, contracts/ai-workflow.md §2.3): segnale tipizzato del
/// validatore (mai da stringhe). <see cref="EvidenceKeys"/> sono le chiavi locali <c>eN</c> delle evidenze coinvolte; codice originale <c>work_output_invalid</c>.
/// </summary>
public sealed class ExtractionOtherRationaleException(string message, IReadOnlyList<string> evidenceKeys, string? outputHash)
    : GuidedRetryTrigger(WorkOutputInvalid, message, outputHash)
{
    public IReadOnlyList<string> EvidenceKeys { get; } = evidenceKeys;
}

/// <summary>
/// Capacità opzionale di un piano: un solo retry guidato di rigenerazione completa dopo un <see cref="GuidedRetryTrigger"/> (T129, T130, T134). Il piano compone il
/// marcatore del tentativo consumato (<see cref="DerivedBatchInput"/>, mai un output) e il messaggio del secondo invio; il port orchestra budget, deadline,
/// fencing e persistenza del marcatore. Nessun merge del primo output: la risposta del retry è validata per intero come la prima, ma con
/// <see cref="ValidateFinal"/>, che non lancia più un trigger di retry.
/// </summary>
public interface IGuidedRetryPlan
{
    DerivedBatchInput GuidedRetryMarker(BatchRef batch, GuidedRetryTrigger trigger);

    IReadOnlyList<ChatMessage> ComposeGuidedRetry(BatchRef batch, GuidedRetryTrigger trigger);

    /// <summary>
    /// Validazione definitiva di una risposta: come <see cref="IDocumentBatchPlan.Validate"/> ma senza ulteriore retry. Usata per la risposta del retry e quando il retry
    /// non è ammesso (deadline, budget, già consumato): vale il motivo originale, e un output valido ma incompleto resta persistito e riutilizzabile come prima di T130.
    /// </summary>
    ValidatedBatch ValidateFinal(BatchRef batch, string responseText);
}

/// <summary>
/// Retry informato (T137, AI §13.3): estensione opzionale di <see cref="IGuidedRetryPlan"/> per i piani che passano al secondo invio la prima risposta come dato esplicito
/// (mai nelle exception, nei marker o nei log) e vogliono il payload effettivo MISURATO prima del marker e della riserva. Con <see cref="InformedRetry"/> falso il port segue il
/// protocollo storico (T129/T130/T134), invariato.
/// </summary>
public interface IInformedRetryPlan : IGuidedRetryPlan
{
    bool InformedRetry { get; }

    /// <summary>Messaggi del secondo invio: originali integrali, motivi tipizzati, prima risposta come dato non fidato delimitato e istruzione di riesame integrale.</summary>
    IReadOnlyList<ChatMessage> ComposeInformedRetry(BatchRef batch, GuidedRetryTrigger trigger, string firstResponse);

    /// <summary>Marcatore del retry consumato: classi, hash del primo output e della richiesta correttiva (<paramref name="measure"/>.PayloadHash), misura; mai testo.</summary>
    DerivedBatchInput InformedRetryMarker(BatchRef batch, GuidedRetryTrigger trigger, string firstResponse, PayloadPreflight measure);
}

/// <summary>
/// Seam della riserva del contatore chiamate del root PRIMA della rete (AI §5.2 punto 5). Può rifiutare lanciando (lease/fence/deadline,
/// limite di chiamate): nessuna sessione è creata prima.
/// </summary>
public interface IBatchCallReservation
{
    ValueTask ReserveAsync(BatchRef batch, CancellationToken ct);
}

/// <summary>
/// Riserva del contatore chiamate del root (preparazione per un DocumentJob; ChatTurn o ProcessingRun documentali per gli altri owner), sotto
/// la guard dell'autorità temporale (T118): lock della riga dell'owner, Running, owner, fence e lease validi, e now &lt; min(deadline locale,
/// EffectiveDeadlineAt corrente del root/padre); un worker senza autorità riceve <see cref="CheckpointFencedException"/> e non incrementa nulla.
/// Con <paramref name="maxCalls"/> (MaxAiCallsPerPreparation/PerRun, congelato dal chiamante) una riserva oltre il massimo è rifiutata con
/// <see cref="DocumentWorkException.CallLimitExceeded"/>: esattamente il massimo è ammesso, e un risultato finale già completo non riserva
/// nulla. Il contatore appartiene al root: la promozione (T047) non lo azzera. Un crash fra riserva e rete consuma una chiamata non
/// effettuata (costo prudenziale dichiarato, AI §5.3).
/// </summary>
public sealed class RootCallCounterReservation(AppDbContext db, CheckpointWriter writer, int? maxCalls = null) : IBatchCallReservation
{
    public async ValueTask ReserveAsync(BatchRef batch, CancellationToken ct)
    {
        using var span = DocumentTiming.Span("port.reserveCall");
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await WorkOwnerGuard.LockAsync(db, writer, includeDeadline: true, ct);

        var max = maxCalls ?? int.MaxValue;
        var updated = writer.OwnerKind switch
        {
            WorkOwnerKind.DocumentJob => await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE "DocumentPreparations" p SET "CallCount" = p."CallCount" + 1
                FROM "DocumentJobs" j WHERE j."Id" = {writer.OwnerId} AND p."Id" = j."PreparationId" AND p."CallCount" < {max}
                """,
                ct),
            WorkOwnerKind.ChatTurn => await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "ChatTurns" SET "CallCount" = "CallCount" + 1 WHERE "Id" = {writer.OwnerId} AND "CallCount" IS NOT NULL AND "CallCount" < {max}""", ct),
            _ => await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "ProcessingRuns" SET "CallCount" = "CallCount" + 1 WHERE "Id" = {writer.OwnerId} AND "CallCount" IS NOT NULL AND "CallCount" < {max}""", ct),
        };

        if (updated != 1)
        {
            // Il lock è già nostro: o il root non ha i campi del regime (ramo 001, nessun contatore) o il massimo è raggiunto.
            var hasCounter = writer.OwnerKind == WorkOwnerKind.DocumentJob || (writer.OwnerKind == WorkOwnerKind.ChatTurn
                ? await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "ChatTurns" WHERE "Id" = {writer.OwnerId} AND "CallCount" IS NOT NULL""").SingleAsync(ct)
                : await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "ProcessingRuns" WHERE "Id" = {writer.OwnerId} AND "CallCount" IS NOT NULL""").SingleAsync(ct)) == 1;
            throw hasCounter
                ? new DocumentWorkException(DocumentWorkException.CallLimitExceeded, "Il limite di chiamate AI del root è stato raggiunto: nessun nuovo invio.")
                : new NotSupportedException("Il contatore delle chiamate è definito per i root documentali; il ramo 001 non ne ha uno.");
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }
}

/// <summary>
/// Port di produzione del grafo batch (T044): collega il grafo fisso di T043 a <see cref="DocumentWorkStore"/> (manifesto, batch, lookup,
/// commit fenced) e a <see cref="AgentFactory.CreateBatchAgent"/>. Non decide né ordina nulla: il piano dice cosa e come validare.
/// Recovery (contracts/ai-workflow.md §5.3): un output Committed è riusato dal lookup senza sessione né rete; prima del commit la
/// reinferenza è ammessa e la riserva già consumata resta; una risposta salvata nel checkpoint e non ancora committata torna a
/// <see cref="PersistAsync"/>, che la rivalida con le stesse regole e, se valida, la committa sotto il fence corrente (nessuna nuova
/// chiamata né nuova riserva); se non è valida l'errore permanente si ripete, mai una reinferenza nascosta. Uno scrittore con lease
/// perso riceve <see cref="LeaseLostException"/> da riserva, commit e checkpoint e non scrive nulla.
/// </summary>
public sealed class DocumentBatchPort(
    DocumentWorkStore store, AppDbContext db, CheckpointWriter writer, AgentFactory factory, IDocumentBatchPlan plan, IBatchCallReservation calls,
    BatchInvocationCounter? counter = null)
    : IDocumentBatchPort, IBatchOutputs
{
    public async ValueTask<int> LoadAsync(CancellationToken ct)
    {
        using var span = DocumentTiming.Span("port.load");
        await store.PublishManifestAsync(plan.Manifest, ct);   // replay identico = nessun effetto; contenuto diverso = integrity_conflict
        return 0;                                               // i guard di Lookup saltano gli output già Committed
    }

    public async ValueTask<BatchRef> PrepareAsync(int cursor, CancellationToken ct)
    {
        using var span = DocumentTiming.Span("port.prepare");
        var batch = await plan.PrepareAsync(cursor, this, ct);
        await store.PublishBatchesAsync([batch], ct);
        return new BatchRef(batch.Ordinal, batch.BatchId, batch.InputHash);
    }

    public async ValueTask<bool> LookupAsync(BatchRef batch, CancellationToken ct)
    {
        using var span = DocumentTiming.Span("port.lookup");
        if ((await store.LookupAsync(batch.BatchId, batch.InputHash, plan.ExpectationFor(batch), ct)).Kind == WorkLookupKind.Hit)
        {
            return true;
        }

        // Retry PM (T099): su Miss, un output compatibile di un root precedente è importato senza AI; altrimenti si procede col lavoro normale.
        return (await store.TryImportAsync(batch.BatchId, plan.ExpectationFor(batch), ct)).Kind is WorkImportKind.Imported or WorkImportKind.Replayed;
    }

    public ValueTask ReserveCallAsync(BatchRef batch, CancellationToken ct) => calls.ReserveAsync(batch, ct);

    /// <summary>
    /// T119: timeout dell'invio = min(timeout configurato della fase, residuo corrente di locale e padre dal database). Il residuo si rilegge a
    /// ogni invio (un vecchio claim o timer non vale) e un heartbeat non lo prolunga; esaurito, non parte alcuna richiesta.
    /// </summary>
    public async ValueTask<TimeSpan?> CallTimeoutAsync(BatchRef batch, CancellationToken ct)
    {
        using var span = DocumentTiming.Span("port.callTimeout");
        var authority = await DocumentDeadlineGuard.EvaluateAsync(db, writer.OwnerKind, writer.OwnerId, writer.LeaseOwner, writer.FencingToken, lockRow: false, ct);
        if (!authority.Allowed)
        {
            throw new CheckpointFencedException(writer.OwnerId);
        }

        var seconds = factory.Options.ForPhase(plan.Agent.Phase).RequestTimeoutSeconds;
        var configured = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromDays(1);
        return DocumentDeadlineGuard.CallTimeout(configured, authority.Remaining) ?? throw new CheckpointFencedException(writer.OwnerId);
    }

    public ChatClientAgent CreateAgent(BatchRef batch)
    {
        var agent = plan.AgentFor(batch);
        return factory.CreateBatchAgent(agent.Name, agent.Id, agent.Instructions, agent.ChatOptions, agent.Phase, agent.ExpectedFingerprint);
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, CancellationToken ct) => plan.ComposeAsync(batch, this, ct);

    public async ValueTask PersistAsync(BatchRef batch, string responseText, CancellationToken ct)
    {
        using var span = DocumentTiming.Span("port.persist");
        ValidatedBatch validated;
        try
        {
            validated = await plan.ValidateAsync(batch, responseText, this, ct);
        }
        catch (GuidedRetryTrigger trigger) when (plan is IGuidedRetryPlan retryPlan)
        {
            validated = await GuidedRetryOnceAsync(batch, retryPlan, trigger, responseText, ct);
        }

        if (validated.Output.BatchId != batch.BatchId || validated.Output.InputHash != batch.InputHash)
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "L'output validato non appartiene al batch richiesto.");
        }

        await store.CommitOutputAsync(validated.Output, validated.Children, ct);
    }

    /// <summary>
    /// Retry guidato unico (T129 bozza, T130 analisi; AI §9/§10). Ordine: (1) autorità e residuo della deadline originale (nessun retry se non basta il timeout pieno di una chiamata);
    /// (2) consumo del marcatore sotto il lock fenced (già presente = retry già consumato: nessun secondo, anche dopo restart/recovery/altro worker);
    /// (3) riserva normale del contatore chiamate (limite raggiunto = nessun retry); (4) invio nella stessa via di InvokeBatch; (5) validazione completa della
    /// risposta con le regole di sempre: un nuovo fallimento, di qualunque tipo, è definitivo. Ogni rinuncia riapplica la validazione definitiva alla prima risposta: vale il motivo ORIGINALE.
    /// </summary>
    private async ValueTask<ValidatedBatch> GuidedRetryOnceAsync(BatchRef batch, IGuidedRetryPlan retryPlan, GuidedRetryTrigger trigger, string firstResponse, CancellationToken ct)
    {
        if (!await HasRoomForOneFullCallAsync(ct))
        {
            return retryPlan.ValidateFinal(batch, firstResponse);
        }

        if (retryPlan is IInformedRetryPlan { InformedRetry: true } informed)
        {
            return await InformedRetryOnceAsync(batch, informed, trigger, firstResponse, ct);
        }

        if (!await store.TryClaimGuidedRetryAsync(retryPlan.GuidedRetryMarker(batch, trigger), ct))
        {
            return retryPlan.ValidateFinal(batch, firstResponse);
        }

        try
        {
            await calls.ReserveAsync(batch, ct);
        }
        catch (DocumentWorkException ex) when (ex.Code == DocumentWorkException.CallLimitExceeded)
        {
            return retryPlan.ValidateFinal(batch, firstResponse);
        }

        counter?.Increment();
        var text = await BatchSender.SendAsync(this, batch, retryPlan.ComposeGuidedRetry(batch, trigger), ct);
        return retryPlan.ValidateFinal(batch, text);
    }

    /// <summary>
    /// Autorità e residuo della deadline originale: falso se non basta il timeout pieno di una chiamata (la rinuncia mantiene il motivo originale); senza autorità
    /// (fence, lease, stato) lancia <see cref="CheckpointFencedException"/>, come per ogni scrittura.
    /// </summary>
    private async ValueTask<bool> HasRoomForOneFullCallAsync(CancellationToken ct)
    {
        var authority = await DocumentDeadlineGuard.EvaluateAsync(db, writer.OwnerKind, writer.OwnerId, writer.LeaseOwner, writer.FencingToken, lockRow: false, ct);
        if (!authority.Allowed && authority.Denial != DeadlineDenial.DeadlineExceeded)
        {
            throw new CheckpointFencedException(writer.OwnerId);
        }

        var seconds = factory.Options.ForPhase(plan.Agent.Phase).RequestTimeoutSeconds;
        return authority.Allowed && authority.Remaining >= (seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Retry informato (T137, AI §13.3). Ordine: (1) l'autorità e il residuo sono già stati verificati dal chiamante; (2) composizione e MISURA del payload effettivo completo, con lo stesso
    /// profilo/runtime congelato e le stesse regole dell'invio: overflow = <c>context_budget_exceeded</c> senza marker, riserva né invio (nessun taglio); misura non affidabile = gli errori del
    /// preflight; (3) nuovo controllo di autorità/deadline (il costo della misura non azzera il tempo trascorso); (4) corrispondenza fra la richiesta misurata e il marcatore; (5) consumo del
    /// marcatore fenced; (6) riserva; (7) invio del payload misurato; (8) validazione definitiva. Ogni rinuncia dopo il rifiuto mantiene il motivo ORIGINALE.
    /// </summary>
    private async ValueTask<ValidatedBatch> InformedRetryOnceAsync(BatchRef batch, IInformedRetryPlan retryPlan, GuidedRetryTrigger trigger, string firstResponse, CancellationToken ct)
    {
        var messages = retryPlan.ComposeInformedRetry(batch, trigger, firstResponse);
        var spec = plan.AgentFor(batch);
        var options = spec.ChatOptions.Clone();
        options.Instructions = spec.Instructions;
        var measure = await factory.Preflight.MeasureAsync(DocumentPayloadChatClient.BuildPayload(spec.Phase, messages, options), ct, spec.ExpectedFingerprint);
        if (measure.Quality == TokenCountQuality.Unavailable)
        {
            throw new RuntimeUnverifiedException("conteggio del payload del retry informato non disponibile (nessun tokenizer o calibrazione identificata)");
        }

        if (!measure.Fits)
        {
            throw new DocumentWorkException(
                DocumentWorkException.ContextBudgetExceeded,
                $"Il payload effettivo del retry informato è di {measure.Tokens} token ({(measure.Quality == TokenCountQuality.Measured ? "misurato" : "limite superiore calibrato")}), oltre il budget di input di {measure.InputBudgetTokens} token: nessun invio, nessun taglio.");
        }

        if (!await HasRoomForOneFullCallAsync(ct))
        {
            return retryPlan.ValidateFinal(batch, firstResponse);
        }

        var marker = retryPlan.InformedRetryMarker(batch, trigger, firstResponse, measure);
        using (var recorded = JsonDocument.Parse(marker.InputIdsJson))
        {
            if (!recorded.RootElement.TryGetProperty("retryPayloadHash", out var hash) || hash.GetString() != measure.PayloadHash)
            {
                throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "Il marcatore del retry non corrisponde alla richiesta correttiva misurata.");
            }
        }

        if (!await store.TryClaimGuidedRetryAsync(marker, ct))
        {
            return retryPlan.ValidateFinal(batch, firstResponse);
        }

        try
        {
            await calls.ReserveAsync(batch, ct);
        }
        catch (DocumentWorkException ex) when (ex.Code == DocumentWorkException.CallLimitExceeded)
        {
            return retryPlan.ValidateFinal(batch, firstResponse);
        }

        counter?.Increment();
        var text = await BatchSender.SendAsync(this, batch, messages, ct);
        return retryPlan.ValidateFinal(batch, text);
    }

    public ValueTask<bool> HasMoreAsync(int nextCursor, CancellationToken ct) => plan.HasBatchAsync(nextCursor, this, ct);

    public ValueTask AssembleAsync(CancellationToken ct) => plan.AssembleAsync(this, ct);

    public ValueTask<int> CommitAsync(CancellationToken ct) => plan.CommitAsync(this, ct);

    public async ValueTask<WorkOutput?> GetByOrdinalAsync(int ordinal, CancellationToken ct)
    {
        var batchId = await db.Set<DerivedBatch>().AsNoTracking()
            .Where(b => b.OwnerKind == writer.OwnerKind && b.OwnerId == writer.OwnerId && b.Ordinal == ordinal)
            .OrderBy(b => b.CreatedAt).Select(b => b.BatchId).FirstOrDefaultAsync(ct);
        return batchId is null
            ? null
            : await db.Set<WorkOutput>().AsNoTracking()
                .SingleOrDefaultAsync(o => o.OwnerKind == writer.OwnerKind && o.OwnerId == writer.OwnerId && o.BatchId == batchId, ct);
    }
}
