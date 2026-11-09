using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Cx = bProjects.IntegrationTests.DocumentReconciliationContractTests;

namespace bProjects.IntegrationTests;

/// <summary>
/// T122 (PG) — proiezioni, FK e migrazione di cataloghi, candidati e patch del run su PostgreSQL reale: roundtrip dei DTO T071, FK a run/progetto/batch/evidenza,
/// unique e conflitti d'integrità, membership e pagine senza buchi, append-only. Nessun AI, nessun DGX, nessuna pipeline gerarchica.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ReconciliationProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string H(char c) => new(c, 64);

    private static readonly string[] Tables =
    [
        "RunCatalogNodes", "RunCatalogPages", "RunCatalogMembers", "RunCatalogSets", "RunCatalogSetNodes",
        "RunCandidates", "RunCandidateOrigins", "RunCandidateDispositions", "RunPatches", "RunPatchDispositions",
    ];

    // ------------------------------------------------------------------ roundtrip e migrazione

    [Fact]
    public async Task Catalog_candidates_and_patches_roundtrip_with_dto_payloads_and_pass_the_membership_checks()
    {
        await using var env = await Env.CreateAsync(withOtherProject: false);
        var h = Cx.Build();
        await env.AddAsync(ReconciliationProjection.Catalog(env.Project, env.Run, "batch-1", h.Catalog));

        var candidate = ReconciliationFixtures.Candidate();
        var patch = ReconciliationFixtures.AddSupport();
        await env.AddAsync([
            .. ReconciliationProjection.Candidate(env.Project, env.Run, ReconciliationFixtures.SourceHash, H('d'), candidate with { OriginBatchIds = ["batch-1", "batch-2"] },
                new Dictionary<string, string> { ["batch-1"] = H('1'), ["batch-2"] = H('2') }),
            ReconciliationProjection.Patch(env.Project, env.Run, ReconciliationFixtures.SourceHash, patch, [H('2'), H('1')]),
        ]);
        await env.AddAsync([
            new RunCandidateDisposition { RunId = env.Run, CandidateId = candidate.CandidateId, Version = 1, State = CandidateState.Pending, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = "[]" },
            new RunCandidateDisposition { RunId = env.Run, CandidateId = candidate.CandidateId, Version = 2, State = CandidateState.Compared, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = $"[\"{H('3')}\"]" },
            new RunPatchDisposition { RunId = env.Run, PatchId = patch.PatchId, Version = 1, Kind = RunPatchDispositionKind.Applied, ResultHash = H('4') },
        ]);

        await using var read = env.Database.NewContext();
        var nodes = await read.RunCatalogNodes.OrderBy(n => n.Level).ThenBy(n => n.Ordinal).ToListAsync(Ct);
        var pages = await read.RunCatalogPages.ToListAsync(Ct);
        var sets = await read.RunCatalogSets.ToListAsync(Ct);
        var setNodes = await read.RunCatalogSetNodes.ToListAsync(Ct);
        Assert.Equal(3, nodes.Count);
        Assert.Equal(h.Pages.Count, pages.Count);
        Assert.Equal(3, await read.RunCatalogMembers.CountAsync(Ct));   // E1,E2,E3 una volta sola: solo identità, nessun contenuto

        // I payload sono i DTO T071 in forma canonica: decodificati con il codec strict, ricostruiscono un catalogo valido contro lo snapshot.
        var rebuilt = new ReconciliationCatalog(
            [.. nodes.Select(n => ReconciliationCodec.Decode<CatalogNode>(n.PayloadJson))],
            [.. pages.Select(p => ReconciliationCodec.Decode<CatalogPage>(p.PayloadJson))],
            [.. sets.Select(s => new CatalogSet
            {
                SetId = s.SetId, View = s.View, MemberNodeIds = [.. setNodes.Where(x => x.SetId == s.SetId).Select(x => x.NodeId).OrderBy(x => x, StringComparer.Ordinal)],
                MemberSetHash = s.MemberSetHash, MemberCount = s.MemberCount,
            })]);
        Assert.Empty(ReconciliationReferences.CheckCatalog(rebuilt, h.Snapshot, HierarchyInputHash, complete: true));
        Assert.All(pages, p => Assert.Equal(ReconciliationIds.PageHash(ReconciliationCodec.Decode<CatalogPage>(p.PayloadJson)), p.PageHash));
        Assert.All(nodes, n => Assert.Equal(CanonicalJson.Canonicalize(n.PayloadJson), ReconciliationCodec.Canonical(ReconciliationCodec.Decode<CatalogNode>(n.PayloadJson))));
        Assert.Equal(h.Leaf0.NodeId, nodes.Single(n => n is { Level: 0, Ordinal: 0 }).NodeId);   // identità non riscritte dalla proiezione

        var storedCandidate = await read.RunCandidates.SingleAsync(Ct);
        Assert.Equal(candidate.CandidateId, storedCandidate.CandidateId);
        Assert.Equal(candidate.CandidateId, ReconciliationIds.CandidateId(candidate.Kind, candidate.Left, candidate.Right, candidate.ProposedType, ReconciliationFixtures.SourceHash));
        Assert.Equal(ReconciliationFixtures.SourceHash, storedCandidate.SourceHash);
        Assert.Equal(["batch-1", "batch-2"], (await read.RunCandidateOrigins.OrderBy(o => o.BatchId).ToListAsync(Ct)).Select(o => o.BatchId));
        Assert.Equal([1, 2], (await read.RunCandidateDispositions.OrderBy(d => d.Version).ToListAsync(Ct)).Select(d => d.Version));
        var storedPatch = await read.RunPatches.SingleAsync(Ct);
        Assert.Equal(patch.PatchId, ReconciliationIds.PatchId(ReconciliationCodec.Decode<Patch>(storedPatch.PayloadJson)));
        Assert.Equal([H('1'), H('2')], JsonSerializer.Deserialize<string[]>(storedPatch.OriginOutputHashesJson)!);
        Assert.Equal((ReconciliationFixtures.SourceHash, ReconciliationFixtures.BaseHash), (storedPatch.SourceHash, storedPatch.BaseHash));

        // Nessuna colonna copia il contenuto delle evidenze e nessun indice di ricerca/registro parallelo esiste.
        Assert.Equal(0L, await env.Database.ScalarAsync<long>(
            $"SELECT count(*) FROM information_schema.columns WHERE table_name IN ({Quoted(Tables)}) AND (column_name ILIKE '%Statement%' OR column_name = 'Content' OR column_name ILIKE '%embedding%')"));
        Assert.Equal(0L, await env.Database.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_indexes WHERE tablename IN ({Quoted(Tables)}) AND (indexdef ILIKE '% USING gin %' OR indexdef ILIKE '% USING gist %' OR indexdef ILIKE '%tsvector%')"));
    }

    private static readonly string HierarchyInputHash = H('7');

    [Fact]
    public async Task Migration_creates_and_removes_only_the_projection_tables()
    {
        await using var database = await ParsingDatabase.CreateAsync();
        const string Previous = "20261102203332_ExecutionIntervals";
        Assert.Equal(Tables.Length, await Present(database));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%ReconciliationProjections'"));
        Assert.Equal((long)Tables.Length, await database.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'TR\\_Run%\\_AppendOnly' AND NOT tgisinternal"));

        await using (var down = database.NewContext())
        {
            await down.GetService<IMigrator>().MigrateAsync(Previous, Ct);
        }

        Assert.Equal(0, await Present(database));
        Assert.Equal(3L, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name IN ('WorkOutputs', 'DerivedBatches', 'AnalysisEvidenceRelations')"));   // 001/T040 intatte

        await using (var up = database.NewContext())
        {
            await up.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);
        }

        Assert.Equal(Tables.Length, await Present(database));
    }

    private static async Task<int> Present(ParsingDatabase database) =>
        (int)await database.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ({Quoted(Tables)})");

    private static string Quoted(IEnumerable<string> names) => string.Join(",", names.Select(n => $"'{n}'"));

    // ------------------------------------------------------------------ FK, progetto e unique

    [Fact]
    public async Task Foreign_keys_bind_rows_to_run_project_batch_node_and_evidence()
    {
        await using var env = await Env.CreateAsync(withOtherProject: true);
        var h = Cx.Build();
        var node = (RunCatalogNode)ReconciliationProjection.Catalog(env.Project, env.Run, "batch-1", h.Catalog).First(r => r is RunCatalogNode { Level: 0, Ordinal: 0 });

        await env.FailsAsync("23503", Mod(node, r => r.BatchId = "batch-ignoto"));   // batch d'origine inesistente
        await env.FailsAsync("23503", Mod(node, r => r.RunId = Guid.NewGuid()));   // run inesistente
        await env.FailsAsync("23503", Mod(node, r => r.ProjectId = env.OtherProject));   // progetto diverso da quello del run
        await env.FailsAsync("23514", Mod(node, r => r.MemberCount = 0));
        await env.FailsAsync("23514", Mod(node, r => r.NodeId = "x"));

        await env.AddAsync([node]);
        await env.FailsAsync("23503", new RunCatalogPage { RunId = env.Run, PageId = H('1'), NodeId = H('2'), Kind = CatalogPageKind.Members, Ordinal = 0, PageHash = H('3'), PayloadJson = "{}" });   // nodo inesistente
        var member = new RunCatalogMember { RunId = env.Run, NodeId = node.NodeId, EvidenceId = ReconciliationFixtures.E1, EvidenceVersionId = Guid.NewGuid(), ContentHash = H('1'), View = node.View, Level = node.Level };
        await env.FailsAsync("23503", Mod(member, r => r.EvidenceId = Guid.NewGuid()));   // evidenza inesistente
        await env.FailsAsync("23503", Mod(member, r => r.View = ReconcileView.B));   // vista incoerente con il nodo
        await env.FailsAsync("23503", Mod(member, r => r.Level = 3));   // livello incoerente con il nodo
        await env.FailsAsync("23503", Mod(member, r => r.NodeId = H('9')));
        await env.FailsAsync("23514", Mod(member, r => r.ContentHash = "nope"));

        await env.FailsAsync("23503", new RunCatalogSetNode { RunId = env.Run, SetId = H('4'), NodeId = node.NodeId });   // insieme inesistente
        await env.FailsAsync("23514", new RunCatalogSet { RunId = env.Run, SetId = H('4'), ProjectId = env.Project, View = ReconcileView.A, MemberSetHash = H('5'), MemberCount = 0 });
        await env.FailsAsync("23503", new RunCatalogSet { RunId = env.Run, SetId = H('4'), ProjectId = env.OtherProject, View = ReconcileView.A, MemberSetHash = H('5'), MemberCount = 1 });

        var candidate = Candidate(env, ReconciliationFixtures.Candidate());
        await env.FailsAsync("23503", Mod(candidate, r => r.ProjectId = env.OtherProject));
        await env.FailsAsync("23503", Mod(candidate, r => r.RunId = Guid.NewGuid()));
        await env.AddAsync([candidate]);
        await env.FailsAsync("23503", new RunCandidateOrigin { RunId = env.Run, CandidateId = candidate.CandidateId, BatchId = "batch-ignoto", OriginOutputHash = H('1'), Rationale = "r" });
        await env.FailsAsync("23503", new RunCandidateOrigin { RunId = env.Run, CandidateId = H('e'), BatchId = "batch-1", OriginOutputHash = H('1'), Rationale = "r" });
        await env.FailsAsync("23503", new RunCandidateDisposition { RunId = env.Run, CandidateId = H('e'), Version = 1, State = CandidateState.Pending, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = "[]" });
        await env.FailsAsync("23503", new RunPatchDisposition { RunId = env.Run, PatchId = H('e'), Version = 1, Kind = RunPatchDispositionKind.Applied });
        await env.FailsAsync("23503", PatchRow(env).With(r => { r.ProjectId = env.OtherProject; }));
    }

    [Fact]
    public async Task Unique_keys_reject_duplicates_and_different_payloads_are_integrity_conflicts()
    {
        await using var env = await Env.CreateAsync(withOtherProject: false);
        var h = Cx.Build();
        var rows = ReconciliationProjection.Catalog(env.Project, env.Run, "batch-1", h.Catalog);
        await env.AddAsync(rows);

        // Stessa posizione (vista, livello, ordinal) con un altro nodeId; stessa evidenza in un altro nodo dello stesso livello/vista.
        var leaf = rows.OfType<RunCatalogNode>().Single(n => n is { Level: 0, Ordinal: 0 });
        var other = new RunCatalogNode
        {
            RunId = env.Run, NodeId = H('c'), ProjectId = env.Project, BatchId = "batch-1", View = leaf.View, Level = 0, Ordinal = 0, MemberSetHash = H('1'), MemberCount = 1,
            DescriptorOutputHash = H('2'), PayloadJson = "{}",
        };
        await env.FailsAsync("23505", other);
        var moved = other.With(r => { r.Ordinal = 7; });
        await env.AddAsync([moved]);
        await env.FailsAsync("23505", new RunCatalogMember { RunId = env.Run, NodeId = moved.NodeId, EvidenceId = ReconciliationFixtures.E1, EvidenceVersionId = Guid.NewGuid(), ContentHash = H('1'), View = leaf.View, Level = 0 });

        // Pagina: stesso (nodo, kind, ordinal) con un altro pageId.
        var firstPage = rows.OfType<RunCatalogPage>().First();
        await env.FailsAsync("23505", firstPage.With(r => { r.PageId = H('d'); }));

        // unique(runId,candidateId): un duplicato è rifiutato, ma più provenienze (batch diversi) sono ammesse; la stessa provenienza due volte no.
        var candidate = Candidate(env, ReconciliationFixtures.Candidate());
        await env.AddAsync([candidate, Origin(env, candidate, "batch-1", H('1'))]);
        await env.FailsAsync("23505", Mod(candidate, r => r.PayloadJson = """{"altro":true}"""));
        await env.FailsAsync("23505", Origin(env, candidate, "batch-1", H('1')));
        await env.AddAsync([Origin(env, candidate, "batch-2", H('2'))]);
        Assert.Equal(2L, await env.Database.ScalarAsync<long>("SELECT count(*) FROM \"RunCandidateOrigins\""));
        // La stessa disposition versionata due volte è un conflitto; una nuova versione è una nuova riga.
        var disposition = new RunCandidateDisposition { RunId = env.Run, CandidateId = candidate.CandidateId, Version = 1, State = CandidateState.Pending, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = "[]" };
        await env.AddAsync([disposition]);
        await env.FailsAsync("23505", disposition.With(r => { r.State = CandidateState.NotProcessed; r.Reason = "errore"; }));
        await env.AddAsync([disposition.With(r => { r.Version = 2; r.State = CandidateState.NotProcessed; r.Reason = "errore"; })]);

        // unique(runId,patchId): stessa patch con payload diverso = integrity_conflict (mai last-write-wins); stesso patchId in un altro run è un'altra riga.
        var patch = PatchRow(env);
        await env.AddAsync([patch]);
        await env.FailsAsync("23505", patch.With(r => r.PayloadJson = """{"diverso":true}"""));
        await env.FailsAsync("23505", patch);
        Assert.Equal(1L, await env.Database.ScalarAsync<long>("SELECT count(*) FROM \"RunPatches\""));   // il payload originale non è stato sovrascritto
        var disp = new RunPatchDisposition { RunId = env.Run, PatchId = patch.PatchId, Version = 1, Kind = RunPatchDispositionKind.Stale, Reason = "base cambiata" };
        await env.AddAsync([disp]);
        await env.FailsAsync("23505", disp.With(r => { r.Kind = RunPatchDispositionKind.Invalid; }));
    }

    [Fact]
    public async Task Check_constraints_keep_dispositions_and_patches_well_formed()
    {
        await using var env = await Env.CreateAsync(withOtherProject: false);
        var candidate = Candidate(env, ReconciliationFixtures.Candidate());
        await env.AddAsync([candidate, PatchRow(env)]);
        RunCandidateDisposition D(CandidateState state, string children = "[]", string hashes = "[]", string? reason = null) => new()
        {
            RunId = env.Run, CandidateId = candidate.CandidateId, Version = Random.Shared.Next(1, 10_000), State = state, ChildCandidateIdsJson = children, ComparisonOutputHashesJson = hashes, Reason = reason,
        };

        await env.FailsAsync("23514", D(CandidateState.Expanded));   // Expanded richiede figli
        await env.FailsAsync("23514", D(CandidateState.Compared));   // Compared richiede il confronto
        await env.FailsAsync("23514", D(CandidateState.Open, hashes: $"[\"{H('1')}\"]"));   // Open richiede il motivo
        await env.FailsAsync("23514", D(CandidateState.Open, reason: "dubbio"));   // Open richiede il confronto
        await env.FailsAsync("23514", D(CandidateState.NotProcessed));   // NotProcessed richiede l'errore
        await env.FailsAsync("23514", D(CandidateState.Pending).With(r => { r.Version = 0; }));
        await env.AddAsync([D(CandidateState.Expanded, children: $"[\"{H('2')}\"]"), D(CandidateState.Compared, hashes: $"[\"{H('1')}\"]"), D(CandidateState.Open, hashes: $"[\"{H('1')}\"]", reason: "dubbio")]);

        var patch = PatchRow(env);
        await env.FailsAsync("23514", new RunPatchDisposition { RunId = env.Run, PatchId = patch.PatchId, Version = 1, Kind = RunPatchDispositionKind.Invalid });   // senza motivo
        await env.FailsAsync("23514", new RunPatchDisposition { RunId = env.Run, PatchId = patch.PatchId, Version = 1, Kind = RunPatchDispositionKind.Applied, ResultHash = "x" });
        await env.AddAsync([new RunPatchDisposition { RunId = env.Run, PatchId = patch.PatchId, Version = 1, Kind = RunPatchDispositionKind.PreservedAsConflict, ResultHash = H('5'), Reason = "alternative preservate" }]);
        await env.FailsAsync("23514", patch.With(r => { r.PatchId = H('b'); r.OriginOutputHashesJson = "[]"; }));   // origini non vuote
        await env.FailsAsync("23514", patch.With(r => { r.PatchId = H('b'); r.BaseHash = "x"; }));
    }

    // ------------------------------------------------------------------ pagine e membership senza buchi

    [Fact]
    public async Task Pages_form_a_gapless_hash_chain_and_members_are_disjoint_per_level()
    {
        await using var env = await Env.CreateAsync(withOtherProject: false);
        var s = ReconciliationFixtures.Snapshot(withThird: true);
        var members = new[] { ReconciliationFixtures.E1, ReconciliationFixtures.E2, ReconciliationFixtures.E3 }.Select(id => Cx.Member(s, id)).ToList();
        var node = new RunCatalogNode
        {
            RunId = env.Run, NodeId = H('a'), ProjectId = env.Project, BatchId = "batch-1", View = ReconcileView.A, Level = 0, Ordinal = 0, MemberSetHash = ReconciliationIds.MemberSetHash(members),
            MemberCount = 3, DescriptorOutputHash = H('2'), PayloadJson = "{}",
        };
        await env.AddAsync([node]);
        var chain = Cx.Chain(node.NodeId, CatalogPageKind.Members, [members[0]], [members[1]], [members[2]]);
        RunCatalogPage Row(CatalogPage page) => ReconciliationProjection.Page(env.Run, page);

        var rows = chain.Select(Row).ToList();
        // Un buco (ordinal 2 senza ordinal 1) e un hash discordante sono rifiutati dal database.
        await env.AddAsync([rows[0]]);
        await env.FailsAsync("23503", rows[2]);
        await env.FailsAsync("23503", rows[1].With(r => { r.PreviousPageHash = H('f'); }));
        await env.FailsAsync("23514", rows[0].With(r => { r.PageId = H('e'); r.Ordinal = 0; r.PreviousPageHash = H('f'); }));   // la prima pagina non ha precedente
        await env.FailsAsync("23514", rows[1].With(r => { r.PreviousPageHash = null; }));   // le successive hanno il precedente
        await env.AddAsync([rows[1]]);
        await env.AddAsync([rows[2]]);
        await using var read = env.Database.NewContext();
        Assert.Equal([0, 1, 2], await read.RunCatalogPages.Where(p => p.Kind == CatalogPageKind.Members).OrderBy(p => p.Ordinal).Select(p => p.Ordinal).ToListAsync(Ct));
        Assert.Equal(new int?[] { null, 0, 1 }, await read.RunCatalogPages.Where(p => p.Kind == CatalogPageKind.Members).OrderBy(p => p.Ordinal).Select(p => p.PreviousOrdinal).ToListAsync(Ct));   // colonna generata dal database

        // Le catene sono per (nodo, kind): una pagina Keys ordinal 0 non richiede nulla, ma ordinal 1 sì.
        var keys = Cx.Chain(node.NodeId, CatalogPageKind.Keys,
            [new KeyPageEntry { Key = new CatalogKey { View = ReconcileView.A, Kind = CatalogKeyKind.Subject, Value = "k", Origin = CatalogKeyOrigin.Literal, OriginOutputHash = null }, MemberIds = [ReconciliationFixtures.E1], MemberPageRefs = [] }],
            [new KeyPageEntry { Key = new CatalogKey { View = ReconcileView.A, Kind = CatalogKeyKind.Subject, Value = "l", Origin = CatalogKeyOrigin.Literal, OriginOutputHash = null }, MemberIds = [ReconciliationFixtures.E2], MemberPageRefs = [] }]);
        await env.FailsAsync("23503", Row(keys[1]));
        await env.AddAsync([Row(keys[0])]);
        await env.AddAsync([Row(keys[1])]);
        Assert.Equal(5L, await env.Database.ScalarAsync<long>("SELECT count(*) FROM \"RunCatalogPages\""));

        // Membership: ogni evidenza una volta per vista/livello; il database rifiuta l'overlap, la cardinalità si verifica sulle righe rilette.
        var memberRows = members.Select(m => new RunCatalogMember { RunId = env.Run, NodeId = node.NodeId, EvidenceId = m.EvidenceId, EvidenceVersionId = m.EvidenceVersionId, ContentHash = m.ContentHash, View = node.View, Level = 0 }).ToList();
        await env.AddAsync(memberRows);
        Assert.Equal(node.MemberCount, (int)await env.Database.ScalarAsync<long>($"SELECT count(*) FROM \"RunCatalogMembers\" WHERE \"NodeId\" = '{node.NodeId}'"));
        await env.FailsAsync("23505", memberRows[0]);
    }

    // ------------------------------------------------------------------ append-only

    [Fact]
    public async Task Projection_rows_are_append_only()
    {
        await using var env = await Env.CreateAsync(withOtherProject: false);
        var h = Cx.Build();
        var candidate = Candidate(env, ReconciliationFixtures.Candidate());
        var patch = PatchRow(env);
        await env.AddAsync(ReconciliationProjection.Catalog(env.Project, env.Run, "batch-1", h.Catalog));
        await env.AddAsync([candidate, Origin(env, candidate, "batch-1", H('1')), patch]);
        await env.AddAsync([new RunCandidateDisposition { RunId = env.Run, CandidateId = candidate.CandidateId, Version = 1, State = CandidateState.Pending, ChildCandidateIdsJson = "[]", ComparisonOutputHashesJson = "[]" },
            new RunPatchDisposition { RunId = env.Run, PatchId = patch.PatchId, Version = 1, Kind = RunPatchDispositionKind.Applied }]);

        foreach (var table in Tables)
        {
            var update = await Assert.ThrowsAsync<PostgresException>(() => env.Database.ExecuteAsync($"UPDATE \"{table}\" SET \"RunId\" = \"RunId\""));
            Assert.Equal("23001", update.SqlState);
            var delete = await Assert.ThrowsAsync<PostgresException>(() => env.Database.ExecuteAsync($"DELETE FROM \"{table}\""));
            Assert.Equal("23001", delete.SqlState);
        }
    }

    // ------------------------------------------------------------------ supporto

    private static T Mod<T>(T row, Action<T> change) where T : class => row.With(change);

    private static RunCandidate Candidate(Env env, ReconcileCandidate candidate) => (RunCandidate)ReconciliationProjection.Candidate(
        env.Project, env.Run, ReconciliationFixtures.SourceHash, H('d'), candidate with { OriginBatchIds = [] }, new Dictionary<string, string>()).Single();

    private static RunCandidateOrigin Origin(Env env, RunCandidate candidate, string batch, string hash) =>
        new() { RunId = env.Run, CandidateId = candidate.CandidateId, BatchId = batch, OriginOutputHash = hash, Rationale = "stessa regola" };

    private static RunPatch PatchRow(Env env) =>
        ReconciliationProjection.Patch(env.Project, env.Run, ReconciliationFixtures.SourceHash, ReconciliationFixtures.AddSupport(), [H('1')]);

    /// <summary>Progetto, run di analisi, tre evidenze documentali, manifesto e due batch derivati del run: le dipendenze minime delle FK.</summary>
    private sealed class Env : IAsyncDisposable
    {
        public required ParsingDatabase Database { get; init; }
        public Guid Project { get; init; }
        public Guid OtherProject { get; init; }
        public Guid Run { get; init; }

        public static async Task<Env> CreateAsync(bool withOtherProject)
        {
            var database = await ParsingDatabase.CreateAsync();
            var seed = await database.SeedParseAsync();
            var other = withOtherProject ? (await database.SeedProjectAsync()).Project : Guid.Empty;
            var (conversation, run, key) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await database.ExecuteAsync($"""
                INSERT INTO "Conversations" ("Id","ProjectId","CreatedAt","LastSequence","MafVersion","ContextThroughSequence","ChatSessionRevision")
                    VALUES ('{conversation}','{seed.Project}', now(), 0, '1.26.0', 0, 0);
                INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","RequestKey","RequestedBy","RequestedAt","Status","DeadlineAt","WorkflowDefinitionVersion","MafVersion",
                    "AttemptCount","MaxAttempts","FencingToken","UpdatedAt","ConversationId","SourceCutoffSequence")
                VALUES ('{run}','{seed.Project}','Analysis','{key}','{seed.User}',now(),'Running',now() + interval '3 minutes','project-analysis-v3','1.26.0',0,3,0,now(),'{conversation}',1);
                """);

            await using (var db = database.NewContext())
            {
                var (job, version, now) = (Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
                db.EvidenceExtractionJobs.Add(new EvidenceExtractionJob
                {
                    Id = job, ProjectId = seed.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = seed.Preparation, Reason = ExtractionJobReason.Initial,
                    Status = ExtractionJobStatus.Queued, RequestedAt = now, DeadlineAt = now.AddHours(1), MaxAttempts = 3, ExtractorConfig = "{}",
                });
                db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
                {
                    Id = version, ProjectId = seed.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = seed.Preparation, ManifestHash = H('1'), Version = 1, JobId = job,
                    SchemaVersion = "evidence-1.1.0", PromptVersion = "document-extraction-v2", ModelId = "m", Parameters = "{}", EvidenceCount = 3, CreatedAt = now,
                });
                await db.SaveChangesAsync(Ct);
                var ordinal = 0;
                foreach (var id in new[] { ReconciliationFixtures.E1, ReconciliationFixtures.E2, ReconciliationFixtures.E3 })
                {
                    db.Evidences.Add(new EvidenceItem
                    {
                        Id = id, VersionId = version, ProjectId = seed.Project, OriginKind = EvidenceOriginKind.Document, PreparationId = seed.Preparation, Ordinal = ++ordinal,
                        Key = $"e{ordinal}", Kind = EvidenceKind.Requirement, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = $"Enunciato {ordinal}",
                        Content = "{}",
                    });
                }

                db.Add(new WorkManifest
                {
                    Id = Guid.NewGuid(), ProjectId = seed.Project, OwnerKind = WorkOwnerKind.ProcessingRun, OwnerId = run, ConfigHash = H('a'), SourceHash = ReconciliationFixtures.SourceHash,
                    RuntimeFingerprint = "fp", DefinitionVersion = "def-1", AlgorithmVersion = "global-reconcile-v1", PolicyHash = H('d'), PlanJson = "{}", Hash = H('e'),
                });
                await db.SaveChangesAsync(Ct);
                foreach (var batch in new[] { "batch-1", "batch-2" })
                {
                    db.Add(new DerivedBatch
                    {
                        Id = Guid.NewGuid(), ProjectId = seed.Project, OwnerKind = WorkOwnerKind.ProcessingRun, OwnerId = run, BatchId = batch, Stage = "reconcile", View = "A", Pass = 0, Level = 0,
                        Ordinal = batch == "batch-1" ? 0 : 1, ParentOutputHashes = "[]", InputIdsJson = "{}", MeasuredTokens = 10, TokenProvenance = "measured", InputHash = H('d'),
                        SchemaVersion = "reconcile-1", PromptVersion = "p1",
                    });
                }

                await db.SaveChangesAsync(Ct);
            }

            return new() { Database = database, Project = seed.Project, OtherProject = other, Run = run };
        }

        public async Task AddAsync(IEnumerable<object> rows)
        {
            await using var db = Database.NewContext();
            foreach (var row in rows)
            {
                db.Add(row);
                await db.SaveChangesAsync(Ct);   // una riga per volta: l'ordine di inserimento è quello della catena di pagine
            }
        }

        public async Task FailsAsync(string sqlState, object row)
        {
            await using var db = Database.NewContext();
            db.Add(row);
            Assert.Equal(sqlState, (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct))).SqlState());
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}

internal static class ProjectionRowExtensions
{
    /// <summary>Copia superficiale di una riga di proiezione con una modifica (le entità sono classi EF, non record).</summary>
    public static T With<T>(this T row, Action<T> change) where T : class
    {
        var copy = (T)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(row, null)!;
        change(copy);
        return copy;
    }
}
