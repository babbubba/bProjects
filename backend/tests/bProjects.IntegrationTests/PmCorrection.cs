using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

// T052 - scelta della voce da modificare nella correzione PM simulata (Correction.EditKey). Le fixture non conoscono gli id delle voci
// prodotte dal modello, quindi la voce si individua per contenuto, con due condizioni in piu' rispetto alla vecchia «prima voce che
// combacia»: la sostituzione non deve scoprire nessun altro fatto atteso (verificato con AcceptanceChecker.Covers, la stessa regola del
// verificatore) e la candidata deve essere unica. Se l'unica voce che esprime il fatto ne porta anche altri (voce con piu' fatti), la
// correzione la riscrive conservando il testo originale dopo quello corretto: nessun fatto indipendente si perde, nessuna parola nuova
// e' inventata. Se nemmeno cosi' la scelta e' univoca o la conservazione non e' verificabile, la correzione non si applica e il percorso
// lo segnala: nessuna voce sostituita al posto di un'altra, nessun fatto mancante dell'analisi AI trasformato in un successo. Specchio TypeScript:
// tests/e2e/support/pm-correction.ts (stesse regole, stessi messaggi).

internal enum EditTargetKind
{
    /// <summary>Una sola voce esprime il fatto da modificare; il nuovo testo (<see cref="EditTarget.NewText"/>) non toglie altri fatti attesi.</summary>
    Found,

    /// <summary>Nessuna voce esprime il fatto, oppure la conservazione degli altri fatti non e' verificabile (<see cref="EditTarget.Lost"/>).</summary>
    Absent,

    /// <summary>Piu' voci candidate equivalenti: nessuna scelta silenziosa.</summary>
    Ambiguous,
}

/// <param name="Lost">Fatti che la sostituzione semplice delle candidate cancellerebbe (Absent con candidate, o Found con voce a piu' fatti).</param>
/// <param name="Message">Esito per il report del percorso; per Absent e Ambiguous e' il motivo del fallimento.</param>
/// <param name="NewText">Testo della voce dopo la correzione (solo Found): il testo corretto, seguito dall'originale se la voce porta altri fatti.</param>
/// <param name="Preserved">Fatti indipendenti conservati dalla riscrittura (voce a piu' fatti); vuoto se la sostituzione semplice basta.</param>
internal sealed record EditTarget(EditTargetKind Kind, Sec Section, int Index, IReadOnlyList<string> Lost, string Message, string? NewText = null, IReadOnlyList<string>? Preserved = null);

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
        var assessed = candidates.Select(x => (Candidate: x, Lost: Lost(script, edit, texts, x.Section, x.Index, script.Correction.EditedText))).ToList();
        var safe = assessed.Where(x => x.Lost.Count == 0).Select(x => x.Candidate).ToList();
        var dedicated = safe.Where(x => !Carries(script, edit, x.Section, x.Text)).ToList();
        var pool = dedicated.Count > 0 ? dedicated : safe;
        if (pool.Count == 1)
        {
            return new(EditTargetKind.Found, pool[0].Section, pool[0].Index, [], $"correzione: «{edit.Key}» in {pool[0].Section} («{pool[0].Text}»)", script.Correction.EditedText, []);
        }

        if (pool.Count > 1)
        {
            return new(EditTargetKind.Ambiguous, default, -1, [],
                $"correzione non applicabile: «{edit.Key}» ambigua, {pool.Count} voci candidate ({string.Join("; ", pool.Select(x => $"{x.Section}: «{x.Text}»"))}), nessuna scelta silenziosa");
        }

        // Nessuna candidata puo' essere sostituita senza perdere altri fatti: la voce a piu' fatti si riscrive conservando solo i frammenti
        // dell'originale che servono a quei fatti (Preserve), mai l'intera voce. Se non e' determinabile in modo univoco e sicuro, non si applica.
        var attempts = assessed
            .Select(x => (x.Candidate, x.Lost, Result: Preserve(script, edit, texts, x.Candidate.Section, x.Candidate.Index, x.Candidate.Text)))
            .ToList();
        var repairable = attempts.Where(x => x.Result.NewText is not null).Select(x => (x.Candidate, x.Lost, NewText: x.Result.NewText!)).ToList();
        if (repairable.Count == 1)
        {
            var r = repairable[0];
            return new(EditTargetKind.Found, r.Candidate.Section, r.Candidate.Index, r.Lost, $"correzione: «{edit.Key}» in {r.Candidate.Section} («{r.Candidate.Text}»), "
                + $"voce con piu' fatti riscritta conservando {string.Join(", ", r.Lost.Select(k => $"«{k}»"))}", r.NewText, r.Lost);
        }

        if (repairable.Count > 1)
        {
            return new(EditTargetKind.Ambiguous, default, -1, [],
                $"correzione non applicabile: «{edit.Key}» ambigua, {repairable.Count} voci candidate a piu' fatti ({string.Join("; ", repairable.Select(x => $"{x.Candidate.Section}: «{x.Candidate.Text}»"))}), nessuna scelta silenziosa");
        }

        var lost = assessed.SelectMany(x => x.Lost).Distinct().ToList();
        return new(EditTargetKind.Absent, default, -1, lost,
            $"correzione non applicabile: nessuna voce di «{edit.Key}» in {where} puo' essere riscritta conservando gli altri fatti; sostituire "
            + string.Join(", ", assessed.Select(x => $"«{x.Candidate.Text}»"))
            + $" cancellerebbe {string.Join(", ", lost.Select(k => $"«{k}»"))} (fatti indipendenti dalla correzione); "
            + string.Join("; ", attempts.Select(x => x.Result.Reason)));
    }

    /// <summary>Segmenti di una voce: separati da punteggiatura forte o da «e»/«o». Sono le unita' minime che si possono conservare.</summary>
    private static readonly Regex SegmentSplit = new(@"\s*(?:[;,.:]|\s[eo]\s)\s*", RegexOptions.Compiled);

    /// <summary>Numeri (cifre o parole) di un testo: un valore conservato non deve contraddire quelli del testo corretto.</summary>
    private static readonly Regex Numbers = new(@"\b(\d+|uno|una|due|tre|quattro|cinque|sei|sette|otto|nove|dieci|undici|dodici|quindici|venti|trenta|cento|mille)\b", RegexOptions.Compiled);

    private static HashSet<string> NumbersOf(string text) =>
        [.. Numbers.Matches(AcceptanceChecker.Norm(text)).Select(m => m.Value)];

    /// <summary>
    /// Riscrittura circoscritta di una voce a piu' fatti: testo corretto, poi i soli segmenti dell'originale necessari a non perdere gli altri
    /// fatti attesi (il sottoinsieme piu' piccolo, verificato con la regola del verificatore). Rifiuta (testo nullo con motivo) se: nessun
    /// sottoinsieme conserva i fatti; piu' sottoinsiemi della stessa dimensione sono equivalenti; un segmento ripete il fatto modificato
    /// (duplicherebbe cio' che il PM ha riscritto); un segmento contiene numeri assenti dal testo corretto (potrebbe riportare una condizione
    /// superata, es. 5 giorni dopo la modifica a 10). Nessun testo inventato: solo frammenti verbatim dell'originale.
    /// </summary>
    internal static (string? NewText, string Reason) Preserve(ProjectScript script, Fact edit, Func<Sec, IReadOnlyList<string>> texts, Sec section, int index, string original)
    {
        var edited = script.Correction.EditedText;
        var segments = SegmentSplit.Split(original).Where(x => x.Length > 0).ToList();
        if (segments.Count < 2 || segments.Count > 10)
        {
            return (null, $"«{original}» non e' scomponibile in frammenti conservabili: trasformazione non determinabile");
        }

        var editedNumbers = NumbersOf(edited);
        var valid = new List<(int Count, string Text)>();
        var rejected = new List<string>();
        for (var mask = 1; mask < 1 << segments.Count; mask++)
        {
            var kept = Enumerable.Range(0, segments.Count).Where(i => (mask & 1 << i) != 0).Select(i => segments[i]).ToList();
            var fragment = string.Join("; ", kept);
            var text = $"{edited.TrimEnd('.', ' ')}; {fragment}";
            if (Lost(script, edit, texts, section, index, text).Count > 0)
            {
                continue;
            }

            if (NumbersOf(fragment).Except(editedNumbers).Any())
            {
                rejected.Add($"il frammento «{fragment}» contiene valori ({string.Join(", ", NumbersOf(fragment).Except(editedNumbers))}) assenti dal testo corretto e potrebbe riportare una condizione superata");
                continue;
            }

            if (AcceptanceChecker.Matches(AcceptanceChecker.Norm(fragment), edit.Match))
            {
                rejected.Add($"il frammento «{fragment}» ripete il fatto «{edit.Key}» gia' riscritto dal PM");
                continue;
            }

            valid.Add((kept.Count, text));
        }

        if (valid.Count == 0)
        {
            return (null, rejected.Count > 0 ? string.Join("; ", rejected.Distinct()) : "nessun frammento dell'originale conserva gli altri fatti");
        }

        var smallest = valid.Min(x => x.Count);
        var best = valid.Where(x => x.Count == smallest).Select(x => x.Text).Distinct().ToList();
        return best.Count == 1 ? (best[0], string.Empty) : (null, $"{best.Count} sottoinsiemi di frammenti equivalenti: trasformazione non determinabile");
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
    private static List<string> Lost(ProjectScript script, Fact edit, Func<Sec, IReadOnlyList<string>> texts, Sec section, int index, string replacement)
    {
        List<string> Texts(Fact f, bool replaced) => f.Accept
            .SelectMany(s => texts(s).Select((t, n) => replaced && s == section && n == index ? replacement : t))
            .Select(AcceptanceChecker.Norm)
            .ToList();

        return script.Facts
            .Where(f => f.Key != edit.Key && f.Accept.Contains(section))
            .Where(f => AcceptanceChecker.Covers(f.Match, f.Parts, Texts(f, false)) && !AcceptanceChecker.Covers(f.Match, f.Parts, Texts(f, true)))
            .Select(f => f.Key)
            .ToList();
    }
}
