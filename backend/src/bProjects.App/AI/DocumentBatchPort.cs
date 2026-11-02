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

    /// <summary>Agente della fase.</summary>
    BatchAgentSpec Agent { get; }

    /// <summary>Vero se esiste un batch con questo ordinale (cursore del grafo).</summary>
    bool HasBatch(int ordinal);

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
/// Seam della riserva del contatore chiamate del root PRIMA della rete (AI §5.2 punto 5). Può rifiutare lanciando (lease/fence/deadline;
/// in seguito anche limite di chiamate): nessuna sessione è creata prima. L'implementazione atomica con limiti, regime e CAS è T047/T118.
/// </summary>
public interface IBatchCallReservation
{
    ValueTask ReserveAsync(BatchRef batch, CancellationToken ct);
}

/// <summary>
/// Riserva minima di oggi: sotto il lock fenced del job (Running, owner, fence, lease e deadline ancora validi) incrementa il
/// <c>CallCount</c> della preparazione radice. Nessun limite, regime né CAS: li aggiunge T047 sostituendo questa implementazione.
/// Un crash fra riserva e rete consuma una chiamata non effettuata (costo prudenziale dichiarato, AI §5.3).
/// </summary>
public sealed class RootCallCounterReservation(AppDbContext db, CheckpointWriter writer) : IBatchCallReservation
{
    public async ValueTask ReserveAsync(BatchRef batch, CancellationToken ct)
    {
        if (writer.OwnerKind != WorkOwnerKind.DocumentJob)
        {
            throw new NotSupportedException("Il contatore del root è definito per i DocumentJob; ChatTurn e ProcessingRun lo avranno con T047.");
        }

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await WorkOwnerGuard.LockAsync(db, writer, includeDeadline: true, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "DocumentPreparations" p SET "CallCount" = p."CallCount" + 1
            FROM "DocumentJobs" j WHERE j."Id" = {writer.OwnerId} AND p."Id" = j."PreparationId"
            """,
            ct);
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
    DocumentWorkStore store, AppDbContext db, CheckpointWriter writer, AgentFactory factory, IDocumentBatchPlan plan, IBatchCallReservation calls)
    : IDocumentBatchPort, IBatchOutputs
{
    public async ValueTask<int> LoadAsync(CancellationToken ct)
    {
        await store.PublishManifestAsync(plan.Manifest, ct);   // replay identico = nessun effetto; contenuto diverso = integrity_conflict
        return 0;                                               // i guard di Lookup saltano gli output già Committed
    }

    public async ValueTask<BatchRef> PrepareAsync(int cursor, CancellationToken ct)
    {
        var batch = await plan.PrepareAsync(cursor, this, ct);
        await store.PublishBatchesAsync([batch], ct);
        return new BatchRef(batch.Ordinal, batch.BatchId, batch.InputHash);
    }

    public async ValueTask<bool> LookupAsync(BatchRef batch, CancellationToken ct) =>
        (await store.LookupAsync(batch.BatchId, batch.InputHash, plan.Expectation, ct)).Kind == WorkLookupKind.Hit;

    public ValueTask ReserveCallAsync(BatchRef batch, CancellationToken ct) => calls.ReserveAsync(batch, ct);

    public ChatClientAgent CreateAgent(BatchRef batch)
    {
        var agent = plan.Agent;
        return factory.CreateBatchAgent(agent.Name, agent.Id, agent.Instructions, agent.ChatOptions, agent.Phase, agent.ExpectedFingerprint);
    }

    public ValueTask<IReadOnlyList<ChatMessage>> ComposeAsync(BatchRef batch, CancellationToken ct) => plan.ComposeAsync(batch, this, ct);

    public async ValueTask PersistAsync(BatchRef batch, string responseText, CancellationToken ct)
    {
        var validated = plan.Validate(batch, responseText);
        if (validated.Output.BatchId != batch.BatchId || validated.Output.InputHash != batch.InputHash)
        {
            throw new DocumentWorkException(DocumentWorkException.IntegrityConflict, "L'output validato non appartiene al batch richiesto.");
        }

        await store.CommitOutputAsync(validated.Output, validated.Children, ct);
    }

    public ValueTask<bool> HasMoreAsync(int nextCursor, CancellationToken ct) => new(plan.HasBatch(nextCursor));

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
