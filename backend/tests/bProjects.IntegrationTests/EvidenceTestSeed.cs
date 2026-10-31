using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// Completa le estrazioni delle evidenze nei test che non provano l'estrattore (analisi, documento, recovery) attraverso il percorso
/// reale del registro: claim del lavoro con lease e fencing, validazione dell'uscita, commit fenced della versione e promozione
/// automatica della prima versione valida. L'uscita è deterministica e non semantica: una evidenza per messaggio che cita il testo
/// del messaggio (o la sua parte iniziale). Non sostituisce le prove dell'estrattore, che usano il modello (finto o reale).
/// </summary>
internal static class EvidenceTestSeed
{
    private const int MaxQuote = 300;

    /// <summary>
    /// Accoda un lavoro per ogni messaggio PM senza versione corrente né lavoro aperto (come il backfill) e poi esegue tutti i lavori
    /// aperti del database, nell'ordine del claim. Ritorna il numero di versioni committate.
    /// </summary>
    public static async Task<int> CompleteAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var policy = scope.ServiceProvider.GetService<IOptions<ExecutionOptions>>()?.Value ?? new ExecutionOptions();
            var ai = scope.ServiceProvider.GetService<IOptions<AiOptions>>()?.Value ?? new AiOptions { ModelId = "seed" };
            var conversations = await db.Conversations.AsNoTracking().Select(c => new { c.Id, c.ProjectId, c.LastSequence }).ToListAsync(ct);
            var now = (await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(ct)).Single();
            foreach (var c in conversations)
            {
                await new EvidenceLedgerStore(db).EnqueueAsync(c.ProjectId, c.Id, long.MaxValue, onlyMissing: true,
                    ExtractionJobReason.Backfill, null, null, now, policy, EvidenceLedgerStore.ExtractorConfig(ai, policy), ct);
            }

            await db.SaveChangesAsync(ct);
        }

        var committed = 0;
        while (true)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var work = await scope.ServiceProvider.GetRequiredService<ExecutionLeaseStore>()
                .ClaimNextWorkAsync($"seed:{Guid.NewGuid():N}", turns: false, runs: false, documents: false, extractions: true, ct);
            if (work?.Extraction is not { } claim)
            {
                return committed;
            }

            var ledger = new EvidenceLedgerStore(db);
            var context = await ledger.LoadContextAsync(claim.JobId, ct);
            var validation = EvidenceExtractionValidator.Validate(Output(context), context);
            if (!validation.Accepted)
            {
                throw new InvalidOperationException("Uscita di prova non valida: " + string.Join(" | ", validation.Violations));
            }

            var result = await ledger.CommitAsync(claim, context, validation, "seed", """{"seed":true}""", ct);
            if (result.Outcome != ExtractionCommitOutcome.Committed)
            {
                throw new InvalidOperationException($"Commit di prova rifiutato: {result.Reason}");
            }

            committed++;
        }
    }

    /// <summary>Come <see cref="CompleteAsync(IServiceProvider, CancellationToken)"/> per le prove che lavorano sul solo database, senza app.</summary>
    public static async Task<int> CompleteAsync(string connectionString, CancellationToken ct = default)
    {
        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString))
            .AddScoped<ExecutionLeaseStore>()
            .AddSingleton(Options.Create(new ExecutionOptions()))
            .BuildServiceProvider();
        await using (services)
        {
            return await CompleteAsync(services, ct);
        }
    }

    /// <summary>Una evidenza Requirement dichiarata che cita la prima frase del messaggio (fino a <see cref="MaxQuote"/> caratteri).</summary>
    public static ExtractionOutput Output(ExtractionContext context)
    {
        // Come un'evidenza atomica: la prima frase del messaggio (entro MaxQuote caratteri), non il messaggio intero.
        var body = context.Target.Body.Trim();
        var end = body.IndexOfAny(['.', '!', '?', '\n']);
        var quote = (end >= 0 && end < MaxQuote ? body[..(end + 1)] : body.Length <= MaxQuote ? body : body[..MaxQuote]).TrimEnd();
        return new ExtractionOutput
        {
            NoEvidenceReason = null,
            Evidences =
            [
                new ExtractedEvidence
                {
                    Key = "e1",
                    Sources = [new ExtractedSource { MessageId = context.Target.Id, Quote = quote, Occurrence = 1, Role = EvidenceSourceRole.Primary }],
                    Kind = EvidenceKind.Requirement,
                    KindRationale = null,
                    Statement = quote,
                    Subject = null,
                    Action = null,
                    Object = null,
                    Scope = null,
                    Purpose = null,
                    Instrument = null,
                    Channel = null,
                    Recipient = null,
                    Qualifiers = [],
                    Polarity = EvidencePolarity.Affirmed,
                    Certainty = EvidenceCertainty.Declared,
                    Relations = [],
                },
            ],
        };
    }
}

/// <summary>
/// Esecutore di estrazione deterministico per i test che non provano l'estrattore: lo stesso dispatcher (claim, lease, rinnovo,
/// fencing) e lo stesso commit del registro, con l'uscita di <see cref="EvidenceTestSeed.Output"/> al posto del modello.
/// </summary>
internal sealed class SeedExtractionExecutor(IServiceScopeFactory scopes) : IEvidenceExtractionExecutor
{
    public int Executions { get; private set; }

    public async Task ExecuteAsync(ExtractionJobClaim claim, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var ledger = new EvidenceLedgerStore(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var context = await ledger.LoadContextAsync(claim.JobId, ct);
        var validation = EvidenceExtractionValidator.Validate(EvidenceTestSeed.Output(context), context);
        var result = await ledger.CommitAsync(claim, context, validation, "seed", """{"seed":true}""", ct);
        if (result.Outcome == ExtractionCommitOutcome.Rejected)
        {
            throw new LeaseLostException($"Commit di prova rifiutato: {result.Reason}");
        }

        Executions++;
    }
}
