using System.Text.Json;
using bProjects.App.Analysis;

namespace bProjects.App.AI;

// Validazione comune di batch/patch/completezza (contracts/ai-workflow.md §4.4–4.5, T121). Stessi invarianti per singola chiamata e gerarchico:
// il gerarchico (I5) fornisce le proprie obbligazioni e prove di membership senza duplicare questo validatore.

public enum ReconciliationOutcome
{
    /// <summary>Errore permanente: mai un WorkOutput Committed, mai noEvidence, mai riparazione AI.</summary>
    Invalid,

    /// <summary>Batch strutturalmente valido e riutilizzabile, ma con obbligazioni residue: non completa e giustifica additional_work.</summary>
    ValidIncomplete,

    /// <summary>Tutte le condizioni congiunte di §4.5 verificate dal server: revisione finale e Completed ammessi.</summary>
    Complete,
}

public sealed record ReconciliationValidation(
    ReconciliationOutcome Outcome, string? ErrorCode, IReadOnlyList<string> Violations, SingleCallOutput? Output, PatchComposition? Patches, CompletionReport? Completion)
{
    public bool IsReusableWorkOutput => Outcome != ReconciliationOutcome.Invalid;

    internal static ReconciliationValidation Fail(string code, IReadOnlyList<string> violations, SingleCallOutput? output = null, PatchComposition? patches = null) =>
        new(ReconciliationOutcome.Invalid, code, violations, output, patches, null);
}

public static class ReconciliationValidator
{
    /// <summary>
    /// Percorso singola chiamata: decodifica strict, regole strutturali, riferimenti allo snapshot, regole di AnalysisContent 1.1, composizione di
    /// tutte le patch contro la stessa base e completezza. Testo troncato o riferimenti non validi sono errore permanente.
    /// </summary>
    public static ReconciliationValidation ValidateSingleCall(string responseText, ReconciliationSnapshot snapshot, ReconciliationBase baseState, DocumentAnalysisFacts facts)
    {
        SingleCallOutput output;
        try
        {
            output = ReconciliationCodec.DecodeSingleCall(responseText);
        }
        catch (JsonException e)
        {
            return ReconciliationValidation.Fail(DocumentWorkException.WorkOutputInvalid, [$"JSON non valido o non conforme a reconcile-1: {e.Message}"]);
        }

        // Stesso patchId con payload diverso è un conflitto d'integrità: prevale sugli altri esiti e non è mai riparato.
        var composition = ReconciliationPatchComposer.Compose([.. output.Results.SelectMany(r => r.Patches)], baseState, snapshot);
        if (composition.HasIntegrityConflict)
        {
            return ReconciliationValidation.Fail(
                DocumentWorkException.IntegrityConflict,
                [.. composition.Dispositions.Where(d => d.Kind == PatchDispositionKind.IntegrityConflict).Select(d => $"patch {d.PatchId}: {d.Reason}")], output, composition);
        }

        var violations = new List<string>();
        violations.AddRange(ReconciliationContractRules.CheckSingleCall(output, snapshot.SourceHash));
        violations.AddRange(ReconciliationReferences.CheckSingleCall(output, snapshot));
        violations.AddRange(DocumentAnalysisContentRules.Check(output.Analysis, facts).Select(m => $"analysis.{m}"));
        violations.AddRange(composition.Dispositions.Where(d => d.Kind == PatchDispositionKind.Invalid).Select(d => $"patch {d.PatchId}: {d.Reason}"));
        if (violations.Count > 0)
        {
            return ReconciliationValidation.Fail(DocumentWorkException.WorkOutputInvalid, [.. violations.Distinct(StringComparer.Ordinal)], output, composition);
        }

        var completion = ReconciliationCompletion.EvaluateSingleCall(snapshot, output, composition);
        return new(completion.IsComplete ? ReconciliationOutcome.Complete : ReconciliationOutcome.ValidIncomplete, null, [], output, composition, completion);
    }
}
