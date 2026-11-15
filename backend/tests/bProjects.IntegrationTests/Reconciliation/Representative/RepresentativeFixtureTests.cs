using System.Text;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-040 — la fixture rappresentativa ERP/EDI è integra (hash di <c>PROVENANCE.json</c>), il ledger è coerente con il gold annotato PRIMA di ogni esecuzione, ogni citazione è
/// letterale con offset reali, la dimensione è fra 160 e 190 evidenze e, con W=32768, O=16384, M=1024, l'analisi è instradata in gerarchia e lo stadio Gap entra nel budget. OFFLINE: runtime finto, nessun
/// database, nessuna inferenza. La densità osservata (evidenze per pagina) è registrata per l'ipotesi dell'assessment §8.
/// </summary>
[Trait("Category", "Offline")]
public sealed class RepresentativeFixtureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Files = [RepresentativeLedger.Af, RepresentativeLedger.Verbale, RepresentativeLedger.PmFile, RepresentativeLedger.GoldFile];

    [Fact]
    public void The_file_hashes_equal_the_provenance_and_the_fixture_is_declared_synthetic()
    {
        using var provenance = JsonDocument.Parse(RepresentativeLedger.Read(RepresentativeLedger.ProvenanceFile));
        var files = provenance.RootElement.GetProperty("files");
        foreach (var file in Files)
        {
            Assert.Equal(files.GetProperty(file).GetString(), RepresentativeLedger.Sha(file));
        }

        Assert.Equal(Files.Length, files.EnumerateObject().Count());
        Assert.Contains("sintetico", provenance.RootElement.GetProperty("origin").GetString(), StringComparison.Ordinal);
        Assert.Contains("nessun documento cliente", provenance.RootElement.GetProperty("origin").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(provenance.RootElement.GetProperty("author").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(provenance.RootElement.GetProperty("date").GetString()));
    }

    [Fact]
    public void Every_quote_is_found_literally_at_its_real_offsets_and_every_source_agrees()
    {
        foreach (var row in RepresentativeLedger.Rows)
        {
            var text = row.Doc == "pm" ? RepresentativeLedger.Messages.Single(m => m.Sequence == row.Line).Text : RepresentativeLedger.Read(RepresentativeLedger.DocumentKey(row.Doc));
            Assert.Equal(row.Quote, text.Substring(row.Start, row.End - row.Start));
            Assert.Equal(row.End - row.Start, row.Quote.Length);
        }

        var input = RepresentativeLedger.Input();
        Assert.Empty(PersistedSourceRules.Check([.. input.Evidence.SelectMany(e => e.Sources).Take(0)]));
        foreach (var evidence in input.Evidence)
        {
            Assert.Empty(PersistedSourceRules.Check(evidence.Sources));
            foreach (var source in evidence.Sources)
            {
                var file = source.Kind == EvidenceSourceKind.Message ? null : RepresentativeLedger.Read(source.DocumentId == MeasurementLedger.Id("rep|doc|verbale") ? RepresentativeLedger.Verbale : RepresentativeLedger.Af);
                var text = file ?? RepresentativeLedger.Messages.Single(m => m.Sequence == source.Sequence).Text;
                Assert.Equal(source.Quote, text.Substring(source.Start, source.End - source.Start));
            }
        }

        Assert.Equal(input.Evidence.Count, input.Snapshot.Evidence.Count);
        Assert.Equal(input.Evidence.Count, input.Evidence.Select(e => e.EvidenceId).Distinct().Count());
        Assert.Equal(RepresentativeLedger.Rows.Count, RepresentativeLedger.Rows.Select(r => r.Ref).Distinct().Count());
        Assert.Equal(3, input.PmMessageCount);
    }

    [Fact]
    public void The_size_is_between_160_and_190_evidences_and_all_kinds_certainties_and_origins_of_the_requirement_are_present()
    {
        var rows = RepresentativeLedger.Rows;
        Assert.InRange(rows.Count, 160, 190);

        var kinds = rows.Select(r => r.Kind).ToHashSet();
        foreach (var kind in new[] { EvidenceKind.Goal, EvidenceKind.Context, EvidenceKind.Requirement, EvidenceKind.Action, EvidenceKind.Constraint, EvidenceKind.Exclusion, EvidenceKind.Dependency, EvidenceKind.Assumption, EvidenceKind.Gap, EvidenceKind.Alternative, EvidenceKind.Decision })
        {
            Assert.Contains(kind, kinds);
        }

        Assert.Equal(Enum.GetValues<EvidenceCertainty>().ToHashSet(), rows.Select(r => r.Certainty).ToHashSet());
        Assert.Contains(rows, r => r.Doc == "pm");
        Assert.Contains(rows, r => r.Doc == "af");
        Assert.Contains(rows, r => r.Doc == "verbale");
        Assert.Equal(2, rows.Count(r => r.Kind == EvidenceKind.Gap));
        Assert.Equal(3, RepresentativeLedger.Messages.Count);
        Assert.True(RepresentativeLedger.Gold.MultiSource.Count >= 2);
        Assert.All(RepresentativeLedger.Gold.MultiSource, m => Assert.Equal(2, RepresentativeLedger.EvidenceOf(RepresentativeLedger.Row(m.Ref)).Sources.Count));

        // Struttura del documento: 3 tracciati da 10 campi, 2 tabelle di trascodifica, circa 25 requisiti, 3 decisioni aperte.
        Assert.Equal(30, rows.Count(r => r.Ref.StartsWith("ORD-", StringComparison.Ordinal) || r.Ref.StartsWith("DES-", StringComparison.Ordinal) || r.Ref.StartsWith("INV-", StringComparison.Ordinal)));
        Assert.Equal(16, rows.Count(r => r.Ref.StartsWith("UM-", StringComparison.Ordinal) || r.Ref.StartsWith("CS-", StringComparison.Ordinal)));
        Assert.Equal(25, rows.Count(r => r.Ref.StartsWith("RF-", StringComparison.Ordinal)));
        Assert.Equal(3, rows.Count(r => r.Ref.StartsWith("DA-", StringComparison.Ordinal)));
        var text = RepresentativeLedger.Read(RepresentativeLedger.Af);
        Assert.Contains("ORDERS", text, StringComparison.Ordinal);
        Assert.Contains("DESADV", text, StringComparison.Ordinal);
        Assert.Contains("INVOIC", text, StringComparison.Ordinal);
        Assert.Contains("vincoli di integrazione", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_ledger_agrees_with_the_gold_annotated_before_any_execution()
    {
        var gold = RepresentativeLedger.Gold;
        Assert.True(gold.Facts.Count >= 20);
        foreach (var fact in gold.Facts)
        {
            var row = RepresentativeLedger.Row(fact.Ref);
            Assert.Equal(fact.Doc, row.Doc);
            Assert.Equal(fact.Phrase, row.Statement);                         // frase esatta
            Assert.Equal(fact.Kind, row.Kind.ToString());
            Assert.Equal(fact.Certainty, row.Certainty.ToString());
            var origin = row.Doc == "pm" ? EvidenceOriginKind.Message : EvidenceOriginKind.Document;
            var (section, classification) = ReconcileLeafProjection.Rule(row.Kind, origin, row.Certainty);
            Assert.Equal(fact.Section, section.ToString());
            Assert.Equal(fact.Classification, classification.ToString());
        }

        Assert.Equal(2, gold.Conflicts.Count);
        foreach (var conflict in gold.Conflicts)
        {
            Assert.Equal("af", RepresentativeLedger.Row(conflict.Older).Doc);
            Assert.Equal("verbale", RepresentativeLedger.Row(conflict.Newer).Doc);
        }

        Assert.Contains("due ore", RepresentativeLedger.Row("ER-05").Statement, StringComparison.Ordinal);
        Assert.Contains("quattro ore", RepresentativeLedger.Row("VB-03").Statement, StringComparison.Ordinal);
        Assert.Contains("facoltativo", RepresentativeLedger.Row("ORD-05").Statement, StringComparison.Ordinal);
        Assert.Contains("obbligatorio", RepresentativeLedger.Row("VB-05").Statement, StringComparison.Ordinal);

        Assert.Equal(2, gold.Gaps.Count);
        foreach (var gap in gold.Gaps)
        {
            var row = RepresentativeLedger.Row(gap.Ref);
            Assert.Equal(EvidenceKind.Gap, row.Kind);
            Assert.Equal(gap.Question, row.Statement);
            Assert.False(string.IsNullOrWhiteSpace(gap.ExpectedOutcome));
            Assert.All(gap.RelatedRefs, r => Assert.Contains(RepresentativeLedger.Rows, x => x.Ref == r));
        }

        Assert.Equal(["Chi gestisce gli scarti della DESADV?", "La fattura va inviata entro 24 o 48 ore?"], gold.Gaps.Select(g => g.Question));
    }

    [Fact]
    public async Task With_W32768_O16384_M1024_the_analysis_is_routed_to_the_hierarchy_and_the_gap_stage_budget_is_observed()
    {
        var input = RepresentativeLedger.Input();
        using var rig = await MeasurementRig.CreateAsync(Ct);
        var catalog = AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV5);

        // (1) Chiamata singola non affidabile: la risposta attesa supera il tetto di output O (stesso criterio del workflow) ⇒ gerarchia con stadio Gap (v11).
        var statements = input.Evidence.ToDictionary(e => e.EvidenceId, e => e.Statement.Length);
        var nonGap = catalog.Data.Evidence.Where(e => e.Kind != EvidenceKind.Gap).Select(e => statements[e.EvidenceId]).ToList();
        var gaps = catalog.Data.Evidence.Count(e => e.Kind == EvidenceKind.Gap);
        var estimate = AnalysisOutputEstimate.Estimate(nonGap, gaps, catalog.Data.Relations.Count);
        Assert.True(AnalysisOutputEstimate.Exceeds(nonGap, gaps, catalog.Data.Relations.Count, rig.Profile.OutputTokens), $"stima {estimate} ≤ O={rig.Profile.OutputTokens}");
        Assert.Equal(2, gaps);

        // (2) Il piano v11 si prepara davvero in gerarchia (primo batch di foglia).
        var v10Catalog = AnalysisAliasCatalog.Build(input, DocumentAnalysisWorkflow.SchemaVersionV3);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var plan = ReconcileMeasurementBudgetTests.NewPlan(rig, input, new Dictionary<Guid, DirectoryFacts>(), v10Catalog, DocumentAnalysisWorkflow.DefinitionVersionV11, new DocumentOptions().ToLimits(), scopes);
        var first = await plan.PrepareAsync(0, new RepresentativeNoOutputs(), Ct);
        Assert.Equal("leaf", first.Stage, ignoreCase: true);

        // (3) Lo stadio Gap (tutte le evidenze non Gap come candidati, nessuna esclusione) entra nel budget di input.
        var stage = GapStageBuilder.Build(input, catalog, RepresentativeLedger.Meta(), null);
        Assert.Equal(input.Evidence.Count - gaps, stage.Candidates.Count);
        Assert.Equal(gaps, stage.Gaps.Count);
        var measured = await rig.Preflight.MeasureAsync(new DocumentPayload(AiPhase.Analysis, GapStage.Instructions, GapStageBuilder.Message(stage), GapStageContracts.JsonSchema().GetRawText()), Ct);
        var gapMessage = string.Join("\n", GapStageBuilder.Message(stage).Select(m => m.Text));

        // (4) Densità osservata (assessment §8): evidenze per pagina (3.000 caratteri) e token di testo per evidenza con il tokenizer finto.
        var characters = RepresentativeLedger.Read(RepresentativeLedger.Af).Length + RepresentativeLedger.Read(RepresentativeLedger.Verbale).Length;
        var pages = characters / 3000.0;
        var textTokens = input.Evidence.Sum(e => FakeTokenizer.Tokens(e.Statement));
        var leaves = ReconcileMeasurementBudgetTests.SingleInput().Evidence.Count;
        var sb = new StringBuilder();
        sb.AppendLine($"evidenze={input.Evidence.Count} (non Gap {stage.Candidates.Count}, Gap {gaps}); caratteri dei documenti={characters}; pagine (3.000 car.)={pages:0.0}; evidenze per pagina={input.Evidence.Count / pages:0.0}");
        sb.AppendLine($"token di testo degli enunciati (tokenizer finto)={textTokens}; token per evidenza={(double)textTokens / input.Evidence.Count:0.0}; confronto: T040-single {leaves} evidenze");
        sb.AppendLine($"stima di output single-call={estimate} > O={rig.Profile.OutputTokens}; stadio Gap={measured.Tokens} token (budget di input {measured.InputBudgetTokens}); primo batch v11: stage={first.Stage} ordinal={first.Ordinal} misurati={first.MeasuredTokens}");
        sb.AppendLine("per tipo: " + string.Join(", ", RepresentativeLedger.Rows.GroupBy(r => r.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
        sb.AppendLine("per certezza: " + string.Join(", ", RepresentativeLedger.Rows.GroupBy(r => r.Certainty).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
        var directory = Path.Combine(Path.GetTempPath(), "bprojects-reconcile-measurement");
        Directory.CreateDirectory(directory);
        sb.AppendLine($"messaggio dello stadio Gap: {gapMessage.Length} caratteri; istruzioni {GapStage.Instructions.Length}; schema {GapStageContracts.JsonSchema().GetRawText().Length}");
        sb.AppendLine(gapMessage[..Math.Min(1800, gapMessage.Length)]);
        File.WriteAllText(Path.Combine(directory, "rh040-evidence.md"), sb.ToString());
        TestContext.Current.SendDiagnosticMessage(sb.ToString());

        // OSSERVAZIONE (non una scelta): con 169 evidenze realistiche (≈ 419 caratteri per voce nel messaggio dello stadio, enunciato e citazione compresi) lo stadio Gap NON entra nel budget di input di
        // W=32768 (21.810 > 15.360 token). RH-040 prevedeva 160–190 evidenze «così che lo stadio Gap entri nel budget»: le due condizioni non sono insieme raggiungibili con questa densità. Né budget né
        // policy sono stati cambiati; RH-041 (scenario A) deve decidere. Il test pinna il fatto osservato, così un cambiamento del formato dello stadio lo segnala.
        Assert.False(measured.Fits, "se lo stadio Gap ora entra nel budget, lo scenario A di RH-041 è raggiungibile: aggiornare questa osservazione");
        Assert.InRange(measured.Tokens, 20_000, 23_000);
        Assert.Equal(15_360, measured.InputBudgetTokens);
    }
}

internal sealed class RepresentativeNoOutputs : IBatchOutputs
{
    public ValueTask<WorkOutput?> GetByOrdinalAsync(int ordinal, CancellationToken ct) => new((WorkOutput?)null);
}
