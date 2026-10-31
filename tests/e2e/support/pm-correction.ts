import { covers, matches, norm, type Fact, type ProjectCase } from './acceptance';

// T052 - scelta della voce da modificare nella correzione PM simulata (correction.editKey): specchio di
// backend/tests/bProjects.IntegrationTests/PmCorrection.cs, stesse regole e stessi messaggi. La voce si individua per contenuto
// (le fixture non conoscono gli id prodotti dal modello) e solo se: combacia con l'attesa da modificare, sostituirla non scopre altri
// fatti attesi (covers, la regola del verificatore) ed e' unica; una voce dedicata al fatto prevale su una che porta anche altri fatti.
// Altrimenti la correzione non si applica e il percorso lo segnala: mai un'altra voce riscritta al suo posto.

export type EditTargetKind = 'found' | 'absent' | 'ambiguous';

export interface EditTarget {
  kind: EditTargetKind;
  /** Sezione e posizione della voce (solo per 'found'). */
  section?: string;
  index?: number;
  /** Fatti che la sostituzione delle candidate cancellerebbe ('absent' con candidate). */
  lost: string[];
  /** Esito per il report; per 'absent' e 'ambiguous' e' il motivo del fallimento. */
  message: string;
}

/** Nome della sezione come lo scrive il backend (Sec): data-section/acceptance-cases.json -> enum C#. */
const SEC: Record<string, string> = {
  goals: 'Goals', overview: 'Overview', included: 'Included', excluded: 'Excluded', requirements: 'Requirements',
  flows: 'Flows', constraints: 'Constraints', dependencies: 'Dependencies', assumptions: 'Assumptions', questions: 'Open',
};

/** La voce soddisfa da sola un altro fatto atteso nella sua sezione (le espressioni intere o una parte atomica). */
const carries = (p: ProjectCase, edit: Fact, section: string, text: string) =>
  p.facts.some((f) => f.key !== edit.key && f.accept.includes(section) && (matches(norm(text), f.match) || (f.parts ?? []).some((part) => matches(norm(text), part))));

/** Fatti attesi (diversi da quello modificato) coperti prima della sostituzione e non piu' coperti dopo. */
function lost(p: ProjectCase, edit: Fact, texts: (section: string) => string[], section: string, index: number): string[] {
  const of = (f: Fact, replaced: boolean) =>
    f.accept.flatMap((s) => texts(s).map((t, n) => (replaced && s === section && n === index ? p.correction.editedText : t))).map(norm);
  return p.facts
    .filter((f) => f.key !== edit.key && f.accept.includes(section))
    .filter((f) => covers(f.match, f.parts, of(f, false)) && !covers(f.match, f.parts, of(f, true)))
    .map((f) => f.key);
}

/** Individua la voce da modificare; `texts` restituisce i testi di una sezione nell'ordine dell'anteprima. */
export function locateEdit(p: ProjectCase, texts: (section: string) => string[]): EditTarget {
  const edit = p.facts.find((f) => f.key === p.correction.editKey)!;
  const where = edit.accept.map((s) => SEC[s]).join('/');
  const candidates = edit.accept.flatMap((section) => texts(section).map((text, index) => ({ section, index, text })))
    .filter((x) => matches(norm(x.text), edit.match));
  if (!candidates.length) return { kind: 'absent', lost: [], message: `correzione non applicabile: la voce «${edit.key}» da modificare non e' nell'anteprima (${where})` };

  const assessed = candidates.map((c) => ({ c, lost: lost(p, edit, texts, c.section, c.index) }));
  const safe = assessed.filter((x) => !x.lost.length).map((x) => x.c);
  const dedicated = safe.filter((x) => !carries(p, edit, x.section, x.text));
  const pool = dedicated.length ? dedicated : safe;
  if (pool.length === 1) {
    const [t] = pool;
    return { kind: 'found', section: t.section, index: t.index, lost: [], message: `correzione: «${edit.key}» in ${SEC[t.section]} («${t.text}»)` };
  }
  if (pool.length > 1) {
    return {
      kind: 'ambiguous', lost: [],
      message: `correzione non applicabile: «${edit.key}» ambigua, ${pool.length} voci candidate (${pool.map((x) => `${SEC[x.section]}: «${x.text}»`).join('; ')}), nessuna scelta silenziosa`,
    };
  }
  const all = [...new Set(assessed.flatMap((x) => x.lost))];
  return {
    kind: 'absent', lost: all,
    message: `correzione non applicabile: nessuna voce dedicata a «${edit.key}» in ${where}; sostituire ${assessed.map((x) => `«${x.c.text}»`).join(', ')} `
      + `cancellerebbe ${all.map((k) => `«${k}»`).join(', ')} (fatti indipendenti dalla correzione)`,
  };
}
