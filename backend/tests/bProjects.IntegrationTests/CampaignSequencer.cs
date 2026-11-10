using System.Runtime.ExceptionServices;
using System.Text.Json;
using Xunit.Sdk;

namespace bProjects.IntegrationTests;

internal enum CampaignStepStatus
{
    /// <summary>Non eseguito: un blocco ha fermato la campagna prima di questo passo.</summary>
    NotRun,
    Passed,

    /// <summary>Fallito con una verifica di merito (criterio non soddisfatto): la campagna prosegue, il fallimento resta.</summary>
    Failed,

    /// <summary>Fallito con un blocco (STRUTTURALE/COMPLETAMENTO/COPERTURA, o errore non di verifica): i passi successivi non partono.</summary>
    Blocked,
}

internal sealed record CampaignStepResult(string Name, CampaignStepStatus Status, string Detail);

/// <summary>
/// Esecuzione ORDINATA di una campagna a passi (003 T040: Single poi Multi) in un solo punto d'ingresso: nessun ordine implicito del framework di test e nessuno stato statico condiviso. Ogni passo è eseguito
/// al più UNA volta (nessun retry); un blocco ferma i passi successivi (<see cref="CampaignStepStatus.NotRun"/>); i fallimenti restano tali (mai convertiti in skip o PASS) e lo stato di ogni passo eseguito
/// è persistito dopo ciascun passo. Un salto esplicito di xUnit (ambiente non disponibile) si propaga subito: nulla è stato eseguito.
/// </summary>
internal static class CampaignSequencer
{
    private static readonly string[] BlockingTags = ["[STRUTTURALE]", "[COMPLETAMENTO]", "[COPERTURA]"];

    /// <summary>Blocco = errore che non è una verifica di merito, oppure verifica fallita con uno dei tag che dichiarano la prova strutturalmente non valida.</summary>
    public static bool IsBlocking(Exception error) =>
        error is not XunitException || BlockingTags.Any(tag => error.Message.Contains(tag, StringComparison.Ordinal));

    /// <summary>Un salto dinamico di xUnit (ambiente senza opt-in o servizi): non è un esito della campagna e non va né nascosto né convertito.</summary>
    public static bool IsSkip(Exception error) => error.GetType().Name.Contains("Skip", StringComparison.Ordinal);

    public sealed record Step(string Name, Func<Task> Run);

    /// <summary>
    /// Esegue i passi nell'ordine dato. Se qualche passo è fallito rilancia: l'unico fallimento (senza passi non eseguiti) con la sua eccezione originale; altrimenti un riepilogo di tutti i passi con la prima
    /// eccezione come interna. Se nessuno è fallito restituisce i risultati.
    /// </summary>
    public static async Task<IReadOnlyList<CampaignStepResult>> RunAsync(IReadOnlyList<Step> steps, Action<IReadOnlyList<CampaignStepResult>>? persist = null)
    {
        var results = steps.Select(s => new CampaignStepResult(s.Name, CampaignStepStatus.NotRun, "non eseguito")).ToList();
        var errors = new Exception?[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            try
            {
                await steps[i].Run();
                results[i] = new(steps[i].Name, CampaignStepStatus.Passed, string.Empty);
            }
            catch (Exception error) when (IsSkip(error) && errors.Any(e => e is not null))
            {
                throw new XunitException("T040: campagna non superata (un passo successivo è stato saltato, il fallimento precedente resta). " + string.Join(" ## ", results.Select(r => $"{r.Name}: {r.Status} {r.Detail}")), errors.First(e => e is not null));
            }
            catch (Exception error) when (!IsSkip(error))
            {
                errors[i] = error;
                var blocking = IsBlocking(error);
                results[i] = new(steps[i].Name, blocking ? CampaignStepStatus.Blocked : CampaignStepStatus.Failed, error.GetType().Name + ": " + error.Message);
                if (blocking)
                {
                    for (var j = i + 1; j < steps.Count; j++)
                    {
                        results[j] = new(steps[j].Name, CampaignStepStatus.NotRun, $"non eseguito: {steps[i].Name} ha incontrato un blocco, nessun rilancio automatico né altro corpus");
                    }
                }
            }

            persist?.Invoke(results);
            if (results[i].Status == CampaignStepStatus.Blocked)
            {
                break;
            }
        }

        var firstFailure = errors.FirstOrDefault(e => e is not null);
        if (firstFailure is not null)
        {
            if (errors.Count(e => e is not null) == 1 && results.All(r => r.Status != CampaignStepStatus.NotRun))
            {
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }

            var summary = string.Join(" ## ", results.Select(r => $"{r.Name}: {r.Status}{(r.Detail.Length > 0 ? " — " + r.Detail : string.Empty)}"));
            throw new XunitException("T040: campagna non superata. " + summary, firstFailure);
        }

        return results;
    }

    /// <summary>Stato per passo, scritto dopo ogni passo in un file temporaneo (conserva le evidenze anche se il primo passo fallisce; non è un'evidenza di PASS).</summary>
    public static void WriteState(string path, string campaign, IReadOnlyList<CampaignStepResult> results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { campaign, steps = results.Select(r => new { r.Name, status = r.Status.ToString(), r.Detail }) }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
