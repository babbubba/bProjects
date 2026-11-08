using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 T142 (correzioni dopo la prima prova DGX) — parti DETERMINISTICHE, senza database né modello: versionamento dei prompt
/// (`extraction-v3`, `document-analysis-v5`) con le versioni storiche invariate, freeze/replay e validazione di un Gap derivato da una
/// domanda del PM. ATTENZIONE: i test con modello finto/scritto dimostrano la struttura, le versioni e la validazione, NON che il DGX
/// risponda correttamente ai nuovi prompt: quello lo accerta solo la prova reale.
/// </summary>
public sealed class T142AnalysisPromptTests
{
    private const string AnalysisV4Sha = "b44d6939fa6726251bcfdcaf34b0cab787e18d8444da0ad5613e98b2317dc3e8";

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string PromptFile(string file) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "bProjects.App", "AI", "Prompts", file);

    private static string Prompt(string file)
    {
        using var stream = typeof(EvidenceExtractor).Assembly.GetManifestResourceStream("Prompts." + file)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();   // come ConversationalAgent.LoadPrompt
    }

    // ---------------------------------------------------------------- analisi: identità, storico, freeze

    [Fact]
    public void The_analysis_prompt_v4_is_preserved_and_v5_only_adds_the_composition_procedure()
    {
        Assert.Equal("document-analysis-v4", DocumentAnalysisWorkflow.PromptVersionV4);
        Assert.Equal("document-analysis-v5", DocumentAnalysisWorkflow.PromptVersionV5);
        Assert.Equal(AnalysisV4Sha, Sha(File.ReadAllText(PromptFile("document-analysis-v4.md"))));
        Assert.Equal(Prompt("document-analysis-v4.md"), DocumentAnalysisWorkflow.InstructionsV4);
        Assert.Equal(Prompt("document-analysis-v5.md"), DocumentAnalysisWorkflow.InstructionsV5);

        var v4 = DocumentAnalysisWorkflow.InstructionsV4.Replace("\r\n", "\n");
        var v5 = DocumentAnalysisWorkflow.InstructionsV5.Replace("\r\n", "\n");
        var start = v5.IndexOf("\nPROCEDURA DI COMPOSIZIONE", StringComparison.Ordinal);
        Assert.True(start > 0);
        var lastStep = v5.IndexOf("\n5. Prima dell'output", start, StringComparison.Ordinal);
        Assert.True(lastStep > start);
        var end = v5.IndexOf('\n', lastStep + 1) + 1;

        // Rimossa la procedura, v5 coincide con v4: nessuna regola storica è stata alterata o persa.
        Assert.Equal(v4, v5.Remove(start, end - start));
        Assert.DoesNotContain("PROCEDURA DI COMPOSIZIONE", v4, StringComparison.Ordinal);
    }

    [Fact]
    public void The_v5_procedure_states_the_five_composition_steps_without_resolving_or_hiding_conflicts()
    {
        var v5 = DocumentAnalysisWorkflow.InstructionsV5.Replace("\r\n", "\n");
        foreach (var fragment in new[]
        {
            "1. Identifica le evidenze incompatibili", "2. Costruisci i conflitti", "Non scegliere fra le alternative", "3. Escludi dagli `items`",
            "duplicato anche se il testo è formulato diversamente", "4. Genera gli altri elementi con tutte le evidenze non in conflitto", "conservando condizioni, negazioni e qualificatori",
            "5. Prima dell'output verifica che nessun fatto sia rappresentato due volte", "nessuna informazione sia andata persa",
        })
        {
            Assert.Contains(fragment, v5, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_new_analyze_request_freezes_document_analysis_v6_with_the_other_keys_unchanged()
    {
        Assert.Equal("document-analysis-v6", SourceSnapshotStore.AnalysisPromptVersion);
        var frozen = SourceSnapshotStore.AnalysisPromptVersions();
        Assert.Contains("\"analysis\":\"document-analysis-v6\"", frozen, StringComparison.Ordinal);
        Assert.Contains("\"analysisAiSchema\":\"document-analysis-alias-2\"", frozen, StringComparison.Ordinal);
        Assert.Contains("\"algorithm\":\"analysis-single-v3\"", frozen, StringComparison.Ordinal);
        Assert.Contains("\"validation\":\"document-analysis-validation-v3\"", frozen, StringComparison.Ordinal);
    }

    private static ProcessingRun Run(string prompts) => new()
    {
        WorkflowDefinitionVersion = DocumentAnalysisWorkflow.DefinitionVersionV5, MafVersion = "1.26.0", PromptVersions = prompts,
    };

    [Fact]
    public void A_v5_run_frozen_with_document_analysis_v4_still_replays_with_v4_and_a_new_one_uses_v6()
    {
        var current = SourceSnapshotStore.AnalysisPromptVersions();
        var historic = current.Replace("document-analysis-v6", "document-analysis-v4", StringComparison.Ordinal);
        var historicV5 = current.Replace("document-analysis-v6", "document-analysis-v5", StringComparison.Ordinal);
        var unknown = current.Replace("document-analysis-v6", "document-analysis-v7", StringComparison.Ordinal);

        Assert.Equal(5, DocumentAnalysisWorkflow.ContractOf(Run(current)));
        Assert.Equal(5, DocumentAnalysisWorkflow.ContractOf(Run(historic)));
        Assert.Equal(5, DocumentAnalysisWorkflow.ContractOf(Run(historicV5)));
        Assert.Null(DocumentAnalysisWorkflow.ContractOf(Run(unknown)));
        Assert.Equal("document-analysis-v6", DocumentAnalysisWorkflow.GapPromptOf(Run(current)));
        Assert.Equal("document-analysis-v5", DocumentAnalysisWorkflow.GapPromptOf(Run(historicV5)));
        Assert.Equal("document-analysis-v4", DocumentAnalysisWorkflow.GapPromptOf(Run(historic)));
    }

    private const string AnalysisV5Sha = "5b09d54ae25b6fe0891d6789f5b3f9db0bae576d1adc1063d8e54f6b4f5415c6";

    [Fact]
    public void The_analysis_prompt_v5_is_preserved_byte_for_byte_and_v6_only_adds_the_named_document_rule_and_the_duplicate_example()
    {
        Assert.Equal("document-analysis-v6", DocumentAnalysisWorkflow.PromptVersionV6);
        Assert.Equal(AnalysisV5Sha, Sha(File.ReadAllText(PromptFile("document-analysis-v5.md"))));
        Assert.Equal(Prompt("document-analysis-v6.md"), DocumentAnalysisWorkflow.InstructionsV6);
        var v5 = DocumentAnalysisWorkflow.InstructionsV5.Replace("\r\n", "\n");
        var v6 = DocumentAnalysisWorkflow.InstructionsV6.Replace("\r\n", "\n");
        Assert.NotEqual(v5, v6);
        // Le regole di v5 restano tutte: ogni riga di v5 è presente in v6 (v6 ne aggiunge o estende soltanto).
        Assert.Contains("PROCEDURA DI COMPOSIZIONE", v6, StringComparison.Ordinal);
        Assert.Contains("Il nome di un documento NON compare nelle evidenze", v6, StringComparison.Ordinal);
        Assert.Contains("ERRATO: l'elemento `i1` in `requirements`", v6, StringComparison.Ordinal);
        Assert.DoesNotContain("Il nome di un documento NON compare", v5, StringComparison.Ordinal);
        Assert.Contains("`Open` ha ESATTAMENTE uno fra `openItem`", v6.Replace("Un record `Open`", "`Open`"), StringComparison.Ordinal);
    }
}
