using System.Text.Json.Nodes;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

// T052 - scelta della voce da modificare nella correzione PM simulata (Correction.EditKey). Le fixture non conoscono gli id delle voci
// prodotte dal modello, quindi la voce si individua per contenuto, con due condizioni in piu' rispetto alla vecchia «prima voce che
// combacia»: la sostituzione non deve scoprire nessun altro fatto atteso (verificato con AcceptanceChecker.Covers, la stessa regola del
// verificatore) e la candidata deve essere unica. Altrimenti la correzione non si applica e il percorso lo segnala: nessuna voce
// sostituita al posto di un'altra, nessun fatto mancante dell'analisi AI trasformato in un successo. Specchio TypeScript:
// tests/e2e/support/pm-correction.ts (stesse regole, stessi messaggi).

internal enum EditTargetKind
{
    /// <summary>Una sola voce esprime il fatto da modificare e sostituirla non toglie altri fatti attesi.</summary>
    Found,

    /// <summary>Nessuna voce esprime il fatto, oppure ogni voce che lo esprime porta anche altri fatti (<see cref="EditTarget.Lost"/>).</summary>
    Absent,

    /// <summary>Piu' voci candidate equivalenti: nessuna scelta silenziosa.</summary>
    Ambiguous,
}

/// <param name="Lost">Fatti che la sostituzione delle candidate cancellerebbe (solo per <see cref="EditTargetKind.Absent"/> con candidate).</param>
/// <param name="Message">Esito per il report del percorso; per Absent e Ambiguous e' il motivo del fallimento.</param>
internal sealed record EditTarget(EditTargetKind Kind, Sec Section, int Index, IReadOnlyList<string> Lost, string Message);

internal static class PmCorrection
{
    /// <summary>Individua la voce da modificare; <paramref name="texts"/> restituisce i testi di una sezione nell'ordine dell'anteprima.</summary>
    public static EditTarget Locate(ProjectScript script, Func<Sec, IReadOnlyList<string>> texts)
    {
        var edit = script.Facts.Single(f => f.Key == script.Correction.EditKey);
        var where = string.Join("/", edit.Accept);
        var candidates = edit.Accept
            .SelectMany(s => texts(s).Select((t, n) => (Section: s, Index: n, Text: t)))
            .Where(x => AcceptanceChecker.Matches(AcceptanceChecker.Norm(x.Text), edit.Match))
            .ToList();
        if (candidates.Count == 0)
        {
            return new(EditTargetKind.Absent, default, -1, [], $"correzione non applicabile: la voce «{edit.Key}» da modificare non e' nell'anteprima ({where})");
        }

        // Una voce dedicata al fatto (nessun'altra attesa vi combacia) prevale su una che porta anche altri fatti, coperti altrove.
        var assessed = candidates.Select(x => (Candidate: x, Lost: Lost(script, edit, texts, x.Section, x.Index))).ToList();
        var safe = assessed.Where(x => x.Lost.Count == 0).Select(x => x.Candidate).ToList();
        var dedicated = safe.Where(x => !Carries(script, edit, x.Section, x.Text)).ToList();
        var pool = dedicated.Count > 0 ? dedicated : safe;
        if (pool.Count == 1)
        {
            return new(EditTargetKind.Found, pool[0].Section, pool[0].Index, [], $"correzione: «{edit.Key}» in {pool[0].Section} («{pool[0].Text}»)");
        }

        if (pool.Count > 1)
        {
            return new(EditTargetKind.Ambiguous, default, -1, [],
                $"correzione non applicabile: «{edit.Key}» ambigua, {pool.Count} voci candidate ({string.Join("; ", pool.Select(x => $"{x.Section}: «{x.Text}»"))}), nessuna scelta silenziosa");
        }

        var lost = assessed.SelectMany(x => x.Lost).Distinct().ToList();
        return new(EditTargetKind.Absent, default, -1, lost,
            $"correzione non applicabile: nessuna voce dedicata a «{edit.Key}» in {where}; sostituire "
            + string.Join(", ", assessed.Select(x => $"«{x.Candidate.Text}»"))
            + $" cancellerebbe {string.Join(", ", lost.Select(k => $"«{k}»"))} (fatti indipendenti dalla correzione)");
    }

    public static EditTarget Locate(ProjectScript script, AnalysisContent content) =>
        Locate(script, s => AcceptanceChecker.Items(content, s).Select(i => i.Text).ToList());

    public static EditTarget Locate(ProjectScript script, JsonNode content) =>
        Locate(script, s => Items(content, s).Select(i => i!["text"]!.GetValue<string>()).ToList());

    /// <summary>Le voci di una sezione nel corpo JSON dell'AnalysisContent (il PUT dell'anteprima).</summary>
    public static JsonArray Items(JsonNode content, Sec section) => (section switch
    {
        Sec.Goals => content["goals"],
        Sec.Overview => content["contextAndScope"]!["overview"],
        Sec.Included => content["contextAndScope"]!["included"],
        Sec.Excluded => content["contextAndScope"]!["excluded"],
        Sec.Requirements => content["requirements"],
        Sec.Flows => content["operationalFlows"],
        Sec.Constraints => content["constraints"],
        Sec.Dependencies => content["dependencies"],
        Sec.Assumptions => content["assumptions"],
        _ => content["openQuestions"],
    })!.AsArray();

    /// <summary>La voce soddisfa da sola un altro fatto atteso nella sua sezione (le espressioni intere o una parte atomica).</summary>
    private static bool Carries(ProjectScript script, Fact edit, Sec section, string text) =>
        script.Facts.Any(f => f.Key != edit.Key && f.Accept.Contains(section)
            && (AcceptanceChecker.Matches(AcceptanceChecker.Norm(text), f.Match) || (f.Parts ?? []).Any(p => AcceptanceChecker.Matches(AcceptanceChecker.Norm(text), p))));

    /// <summary>Fatti attesi (diversi da quello modificato) coperti prima della sostituzione e non piu' coperti dopo.</summary>
    private static List<string> Lost(ProjectScript script, Fact edit, Func<Sec, IReadOnlyList<string>> texts, Sec section, int index)
    {
        List<string> Texts(Fact f, bool replaced) => f.Accept
            .SelectMany(s => texts(s).Select((t, n) => replaced && s == section && n == index ? script.Correction.EditedText : t))
            .Select(AcceptanceChecker.Norm)
            .ToList();

        return script.Facts
            .Where(f => f.Key != edit.Key && f.Accept.Contains(section))
            .Where(f => AcceptanceChecker.Covers(f.Match, f.Parts, Texts(f, false)) && !AcceptanceChecker.Covers(f.Match, f.Parts, Texts(f, true)))
            .Select(f => f.Key)
            .ToList();
    }
}
