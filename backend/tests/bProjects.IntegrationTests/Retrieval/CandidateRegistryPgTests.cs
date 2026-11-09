using bProjects.App.Evidence;
using bProjects.App.Retrieval;

namespace bProjects.IntegrationTests;

/// <summary>003 T015 (PG): mappatura blocco→evidenze su snapshot reale con documenti sintetici (N4 conflitto + capitolato T082): ogni evidenza raggiunta ha sorgente nel blocco recuperato.</summary>
[Trait("Category", TestCategories.Integration)]
public sealed class CandidateRegistryPgTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Paragraphs(params string[] path) => File.ReadAllText(RetrievalGold.PathOf(path)).Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

    private static string[] Lines(params string[] path) => File.ReadAllText(RetrievalGold.PathOf(path)).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Both_sides_of_a_document_conflict_are_retrieved_mapped_to_their_evidences_and_every_package_evidence_cites_a_retrieved_block()
    {
        await using var database = await RetrievalDb.CreateAsync();
        using var rig = new RetrievalRig();
        var project = await database.SeedProjectAsync();
        var a = await database.AddDocumentAsync(project, "Scheda A", Lines("N4-fixtures", "txt-conflict-a.txt"), headings: [["Scheda A"], ["Scheda A"]]);
        var b = await database.AddDocumentAsync(project, "Scheda B", Lines("N4-fixtures", "txt-conflict-b.txt"), headings: [["Scheda B"], ["Scheda B"]]);
        var capitolato = await database.AddDocumentAsync(project, "Capitolato Aurora", Paragraphs("T082-single", "capitolato-aurora.txt"), headings: null);
        var (versionA, evA) = await database.AddEvidenceAsync(a, (EvidenceKind.Requirement, "Termine di pagamento 30 giorni", [1]));
        var (versionB, evB) = await database.AddEvidenceAsync(b, (EvidenceKind.Requirement, "Termine di pagamento 60 giorni", [1]));
        var (versionC, evC) = await database.AddEvidenceAsync(capitolato, (EvidenceKind.Context, "Cenni storici", [1]), (EvidenceKind.Gap, "Manca il termine di pagamento", [2]));
        var snapshot = await database.AddSnapshotAsync(project, [a, b, capitolato], evidenceVersions: new Dictionary<Guid, Guid> { [a.PreparationId] = versionA, [b.PreparationId] = versionB, [capitolato.PreparationId] = versionC });

        await using var db = database.NewContext();
        string hash = string.Empty;
        foreach (var d in new[] { a, b, capitolato })
        {
            hash = (await rig.Builder(db).BuildAsync(d.PreparationId, Ct)).IndexConfigHash;
        }

        var scope = await RetrievalScope.FromSnapshotAsync(db, snapshot, hash, Ct);
        const string query = "termine di pagamento";
        var result = await rig.Search(db).SearchAsync(scope, query, pool: 50, Ct);
        var evidences = await BlockEvidenceMapper.LoadDocumentEvidenceAsync(db, [versionA, versionB, versionC], Ct);
        var provenance = await BlockEvidenceMapper.LoadProvenanceAsync(db, [.. result.Fused.Select(f => f.BlockId)], Ct);
        var registry = CandidateRegistry.Build(
            "snap", hash, "retrieval-policy-1", new string('2', 64),
            [new(evC[1], query, result.Fused)], provenance, evidences);

        // Entrambi i lati del conflitto sono candidati e nel pacchetto, senza arbitraggio.
        Assert.Contains(evA[0], registry.PackageEvidence);
        Assert.Contains(evB[0], registry.PackageEvidence);
        Assert.DoesNotContain(evC[1], registry.PackageEvidence);   // l'evidenza Gap è la domanda, non un candidato
        Assert.Equal((CandidateRegistry.OutcomePackage, true), (registry.Outcome, registry.RetrievalComplete));

        // Ogni evidenza del pacchetto ha una fonte su un blocco effettivamente recuperato dal ranking.
        var retrieved = result.Fused.Select(f => f.BlockId).ToHashSet();
        var byId = evidences.ToDictionary(e => e.EvidenceId);
        Assert.All(registry.PackageEvidence, id => Assert.Contains(byId[id].BlockIds, retrieved.Contains));

        // Provenienza completa e non inventata: documento, versione, sezione dai titoli, intervallo del blocco, hash originale.
        var candidate = registry.Gaps[0].Candidates.First(c => c.EvidenceIds.Contains(evA[0]));
        Assert.Equal(("Scheda A", 1, "Scheda A"), (candidate.DocumentName, candidate.DocumentVersion, candidate.Section));
        Assert.Equal([0, a.Texts[1].Length], candidate.Range);
        Assert.Equal(DocumentTestFixtures.Sha256(a.Texts[1]), candidate.TextHash);
        var unsectioned = registry.Gaps[0].Candidates.FirstOrDefault(c => c.DocumentName == "Capitolato Aurora");
        if (unsectioned is not null)
        {
            Assert.Equal(string.Empty, unsectioned.Section);   // sezione vuota se assente, mai inventata
        }

        // Un blocco recuperato senza evidenza è registrato con il suo motivo e non è un candidato dello stadio.
        Assert.Contains(registry.Gaps[0].Candidates, c => c.EvidenceIds.Count == 0 && c.ExcludedReason == CandidateRegistry.ReasonNoEvidence && !c.InPackage);
        Assert.Equal(registry.Hash(), CandidateRegistry.Build(
            "snap", hash, "retrieval-policy-1", new string('2', 64), [new(evC[1], query, result.Fused)], provenance, evidences).Hash());
    }
}
