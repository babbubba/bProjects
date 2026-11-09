using bProjects.App.Evidence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>
/// 003 T016 (PG): isolamento. Due progetti con corpora simili nello stesso database, una versione esclusa dal PM, una versione non inclusa dallo snapshot: ricerca lessicale,
/// densa e registro non restituiscono mai passaggi di altri progetti o di versioni escluse, nemmeno con la query identica al testo escluso, e l'esclusione precede il ranking
/// (i ranghi dei restanti non cambiano).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class RetrievalIsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Query = "Il termine di pagamento riservato è di novanta giorni";

    private static readonly string[] Corpus =
    [
        "Il fornitore garantisce la disponibilità del servizio per tutta la durata.",
        "La penale per ritardo è del cinque per cento del corrispettivo.",
        "Il termine di pagamento è di trenta giorni dalla data fattura.",
    ];

    // Il testo escluso è identico alla query: sarebbe il primo in entrambi i segnali se competesse.
    private static string[] WithSecret(string[] corpus) => [.. corpus, Query];

    [Fact]
    public async Task Search_and_registry_never_return_other_projects_excluded_versions_or_versions_outside_the_snapshot_and_the_remaining_ranks_do_not_change()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync("Progetto 1");
        var other = await database.SeedProjectAsync("Progetto 2");

        var included = await database.AddDocumentAsync(project, "Contratto", Corpus);
        await using var db = database.NewContext();
        var hash = (await rig.Builder(db).BuildAsync(included.PreparationId, Ct)).IndexConfigHash;
        var (includedEvidenceVersion, includedEvidence) = await database.AddEvidenceAsync(included,
            (EvidenceKind.Requirement, "Termine di pagamento trenta giorni", [2]), (EvidenceKind.Constraint, "Penale cinque per cento", [1]));
        var baselineSnapshot = await database.AddSnapshotAsync(project, [included], evidenceVersions: new Dictionary<Guid, Guid> { [included.PreparationId] = includedEvidenceVersion });
        var baselineScope = await RetrievalScope.FromSnapshotAsync(db, baselineSnapshot, hash, Ct);
        var baseline = await rig.Search(db).SearchAsync(baselineScope, Query, 50, Ct);
        Assert.NotEmpty(baseline.Lexical.Blocks);
        Assert.NotEmpty(baseline.Dense.Blocks);

        // Escluso dal PM (stesso progetto), altra versione dello stesso documento non inclusa dallo snapshot, e un altro progetto con corpora simili.
        var excludedByPm = await database.AddDocumentAsync(project, "Allegato riservato", WithSecret(Corpus));
        var notInSnapshot = await database.AddDocumentAsync(project, "Contratto", WithSecret(Corpus), version: 2, documentId: included.DocumentId);
        var foreign = await database.AddDocumentAsync(other, "Contratto", WithSecret(Corpus));
        var foreignOther = await database.AddDocumentAsync(other, "Allegato riservato", WithSecret(Corpus));
        foreach (var d in new[] { excludedByPm, notInSnapshot, foreign, foreignOther })
        {
            await rig.Builder(db).BuildAsync(d.PreparationId, Ct);
        }

        var (_, excludedEvidence) = await database.AddEvidenceAsync(excludedByPm, (EvidenceKind.Requirement, "Termine di pagamento novanta giorni", [3]));
        var (_, foreignEvidence) = await database.AddEvidenceAsync(foreign, (EvidenceKind.Requirement, "Termine di pagamento novanta giorni", [3]));
        var snapshot = await database.AddSnapshotAsync(project, [included], excluded: [excludedByPm], evidenceVersions: new Dictionary<Guid, Guid> { [included.PreparationId] = includedEvidenceVersion });
        var scope = await RetrievalScope.FromSnapshotAsync(db, snapshot, hash, Ct);
        Assert.Equal([included.VersionId], scope.DocumentVersionIds);

        var secret = new HashSet<Guid>(new[] { excludedByPm, notInSnapshot, foreign, foreignOther }.SelectMany(d => d.BlockIds));
        var result = await rig.Search(db).SearchAsync(scope, Query, 50, Ct);

        // Nessun blocco estraneo in nessun segnale, nemmeno con la query identica al testo escluso.
        Assert.Empty(result.Lexical.Blocks.Select(b => b.BlockId).Intersect(secret));
        Assert.Empty(result.Dense.Blocks.Select(b => b.BlockId).Intersect(secret));
        Assert.Empty(result.Fused.Select(b => b.BlockId).Intersect(secret));
        Assert.All(result.Fused, f => Assert.Contains(f.BlockId, included.BlockIds));

        // L'esclusione precede il ranking: ranghi dei restanti identici al riferimento senza i documenti estranei.
        Assert.Equal(baseline.Lexical.Blocks, result.Lexical.Blocks);
        Assert.Equal(baseline.Dense.Blocks, result.Dense.Blocks);
        Assert.Equal(baseline.Fused, result.Fused);

        // Registro: provenienza e pacchetto solo dei documenti inclusi.
        var evidences = await BlockEvidenceMapper.LoadDocumentEvidenceAsync(db, [includedEvidenceVersion], Ct);
        var provenance = await BlockEvidenceMapper.LoadProvenanceAsync(db, [.. result.Fused.Select(f => f.BlockId)], Ct);
        var registry = CandidateRegistry.Build("snap", hash, "retrieval-policy-1", new string('2', 64), [new(Guid.NewGuid(), Query, result.Fused)], provenance, evidences);
        Assert.All(registry.Gaps.SelectMany(g => g.Candidates), c => Assert.DoesNotContain(c.BlockId, secret));
        Assert.All(registry.Gaps.SelectMany(g => g.Candidates), c => Assert.Equal(("Contratto", 1), (c.DocumentName, c.DocumentVersion)));
        Assert.Empty(registry.PackageEvidence.Intersect(excludedEvidence.Concat(foreignEvidence)));
        Assert.Subset(includedEvidence.ToHashSet(), registry.PackageEvidence.ToHashSet());
        Assert.Equal(includedEvidence.Count, registry.PackageEvidence.Count);   // le sole evidenze incluse con un blocco recuperato

        // Contro-prova: con il perimetro allargato al documento escluso il testo identico alla query compete (e vince): era il filtro a tenerlo fuori.
        var widened = scope with { DocumentVersionIds = [included.VersionId, excludedByPm.VersionId], PreparationIds = [included.PreparationId, excludedByPm.PreparationId] };
        var withExcluded = await rig.Search(db).SearchAsync(widened, Query, 50, Ct);
        Assert.Equal(excludedByPm.BlockIds[3], withExcluded.Lexical.Blocks[0].BlockId);
        Assert.Equal(excludedByPm.BlockIds[3], withExcluded.Dense.Blocks[0].BlockId);
    }

    [Fact]
    public async Task A_scope_with_another_project_id_or_no_included_versions_returns_nothing()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync("Progetto 1");
        var other = await database.SeedProjectAsync("Progetto 2");
        var document = await database.AddDocumentAsync(project, "Contratto", Corpus);
        await using var db = database.NewContext();
        var hash = (await rig.Builder(db).BuildAsync(document.PreparationId, Ct)).IndexConfigHash;

        var wrongProject = new RetrievalScope(other, hash, [document.VersionId], [document.PreparationId]);
        var empty = new RetrievalScope(project, hash, [], []);
        var wrongHash = new RetrievalScope(project, new string('0', 64), [document.VersionId], [document.PreparationId]);

        foreach (var scope in new[] { wrongProject, empty, wrongHash })
        {
            var result = await rig.Search(db).SearchAsync(scope, Corpus[2], 50, Ct);
            Assert.Empty(result.Lexical.Blocks);
            Assert.Empty(result.Dense.Blocks);
            Assert.Empty(result.Fused);
        }
    }
}
