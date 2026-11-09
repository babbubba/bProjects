using bProjects.App.AI;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>Tipo di root seminato dai test del regime: un ChatTurn o un ProcessingRun (Analysis) con i campi del regime documentale.</summary>
public enum RootKind
{
    Turn,
    Run,
}

/// <summary>
/// Stato di un root documentale da seminare. Tutti i tempi sono espressioni SQL sul clock del database: i test non dipendono dall'orologio
/// del processo. <see cref="ElapsedSeconds"/> sposta indietro il t0 (RequestedAt); le scadenze sono t0+180 s (Ordinary) e t0+3600 s (Hard)
/// salvo <see cref="EffectiveSql"/>.
/// </summary>
public sealed record RootSpec
{
    public ExecutionRegime Regime { get; init; } = ExecutionRegime.Ordinary;
    public double ElapsedSeconds { get; init; }
    public string Status { get; init; } = "Running";
    public string? LeaseOwner { get; init; } = "worker-a";
    public string LeaseUntilSql { get; init; } = "clock_timestamp() + interval '1 minute'";
    public long Fence { get; init; } = 3;
    public int AttemptCount { get; init; } = 1;
    public int CallCount { get; init; }

    /// <summary>Scadenza effettiva in SQL; se nulla segue il regime (Ordinary → t0+180 s, Extended → t0+3600 s).</summary>
    public string? EffectiveSql { get; init; }

    /// <summary>Root classificato Extended fin dall'inizio (InitialRegime=Extended) o promosso; in tal caso il regime è Extended.</summary>
    public ExecutionRegime InitialRegime { get; init; } = ExecutionRegime.Ordinary;
    public int PromotionCount { get; init; }

    /// <summary>Root della 001: nessun campo del regime (EffectiveDeadlineAt nullo), solo DeadlineAt.</summary>
    public bool Legacy { get; init; }
    public string LegacyDeadlineSql { get; init; } = "now() + interval '3 minutes'";
}

public sealed record SeededRoot(Guid Project, Guid Conversation, Guid Id, RootKind Kind, string Table, Guid User);

/// <summary>Semina di root documentali sul database di test (SQL puro: nessun servizio applicativo).</summary>
public static class DocumentRegimeRoots
{
    public static string PolicyHash => new('c', 64);

    /// <summary>Snapshot dei limiti di un run documentale di Analizza, costruito dal codice reale (RunLimitsSnapshot) sui limiti di default: il vincolo del database lo esige.</summary>
    public static string LimitsSnapshotJson => RunLimitsSnapshot.Of(DocumentPolicySnapshot.Of(new DocumentOptions().ToLimits(), 1)).ToJson();

    public static async Task<SeededRoot> SeedAsync(ParsingDatabase database, RootKind kind, RootSpec? spec = null, (Guid User, Guid Project)? owner = null)
    {
        spec ??= new RootSpec();
        var (user, project) = owner ?? await database.SeedProjectAsync();
        var (conversation, id, message) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var extended = spec.Regime == ExecutionRegime.Extended;
        var elapsed = spec.ElapsedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lease = spec.LeaseOwner is null ? "NULL" : $"'{spec.LeaseOwner}'";
        var leaseUntil = spec.LeaseOwner is null ? "NULL" : spec.LeaseUntilSql;
        var promoted = spec.PromotionCount == 1;
        var terminal = spec.Status is "Completed" or "Failed";
        var completed = terminal ? "clock_timestamp()" : "NULL";
        var error = spec.Status == "Failed" ? "'deadline_exceeded'" : "NULL";

        await database.ExecuteAsync($"""
            INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision") VALUES ('{conversation}','{project}',now(),1,'1.26.0',0,0);
            INSERT INTO "ConversationMessages" ("Id","ConversationId","Sequence","Role","Body","CreatedAt","ClientMessageId","AuthorId") VALUES ('{message}','{conversation}',1,'User','ciao',now(),'{Guid.NewGuid()}','{user}');
            """);

        if (kind == RootKind.Turn)
        {
            await database.ExecuteAsync($"""
                INSERT INTO "ChatTurns" ("Id","ConversationId","TriggerMessageId","SourceCutoffSequence","Status","RequestedAt","DeadlineAt","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","SessionRevisionAtStart","CompletedAt","ErrorCode")
                VALUES ('{id}','{conversation}','{message}',1,'{spec.Status}',now(),now() + interval '3 minutes',{spec.AttemptCount},3,{lease},{leaseUntil},{spec.Fence},0,{completed},{error});
                """);
        }
        else
        {
            await database.ExecuteAsync($"""
                INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt","WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","LeaseOwner","LeaseUntil","FencingToken","UpdatedAt","CompletedAt","ErrorCode","DocumentLimitsSnapshot")
                VALUES ('{id}','{project}','Analysis','{conversation}',1,'{Guid.NewGuid()}','{user}',now(),'{spec.Status}',now() + interval '3 minutes','project-analysis-documents-v2','1.26.0',{spec.AttemptCount},3,{lease},{leaseUntil},{spec.Fence},now(),{completed},{error},'{LimitsSnapshotJson}'::jsonb);
                """);
        }

        var table = kind == RootKind.Turn ? "ChatTurns" : "ProcessingRuns";
        if (spec.Legacy)
        {
            await database.ExecuteAsync($"UPDATE \"{table}\" SET \"DeadlineAt\" = {spec.LegacyDeadlineSql} WHERE \"Id\" = '{id}'");
            return new(project, conversation, id, kind, table, user);
        }

        // Un solo istante di riferimento per riga: le espressioni volatili si valutano una volta nella sottoquery, così DeadlineAt = EffectiveDeadlineAt.
        var ordinarySql = spec.InitialRegime == ExecutionRegime.Ordinary ? "v.o" : "NULL::timestamptz";
        var effective = $"COALESCE(v.ce, {(extended ? "v.h" : "v.o")})";
        await database.ExecuteAsync($"""
            UPDATE "{table}" x SET
                "RequestedAt" = v.r, "InitialRegime" = '{spec.InitialRegime}', "Regime" = '{spec.Regime}', "ClassificationStatus" = 'Classified', "PolicyHash" = '{PolicyHash}',
                "HardDeadlineAt" = v.h, "EffectiveDeadlineAt" = {effective}, "DeadlineAt" = {effective}, "OrdinaryDeadlineAt" = {ordinarySql}, "CallCount" = {spec.CallCount},
                "PromotionCount" = {spec.PromotionCount}, "PromotedAt" = {(promoted ? "v.r" : "NULL::timestamptz")}, "PromotedReason" = {(promoted ? "'additional_work'" : "NULL")}
            FROM (SELECT q.r, q.r + interval '180 seconds' AS o, q.r + interval '3600 seconds' AS h, {spec.EffectiveSql ?? "NULL::timestamptz"} AS ce
                  FROM (SELECT clock_timestamp() - interval '{elapsed} seconds' AS r) q) v
            WHERE x."Id" = '{id}'
            """);
        return new(project, conversation, id, kind, table, user);
    }

    public static WorkOwnerKind OwnerKind(this SeededRoot root) => root.Kind == RootKind.Turn ? WorkOwnerKind.ChatTurn : WorkOwnerKind.ProcessingRun;
}
