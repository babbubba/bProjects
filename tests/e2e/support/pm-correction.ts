import { covers, matches, norm, type Fact, type ProjectCase } from './acceptance';

// T052 - scelta della voce da modificare nella correzione PM simulata (correction.editKey): specchio di
// backend/tests/bProjects.IntegrationTests/PmCorrection.cs, stesse regole e stessi messaggi. La voce si individua per contenuto
// (le fixture non conoscono gli id prodotti dal modello) e solo se: combacia con l'attesa da modificare, sostituirla non scopre altri
// fatti attesi (covers, la regola del verificatore) ed e' unica; una voce dedicata al fatto prevale su una che porta anche altri fatti.
// Se l'unica voce che esprime il fatto ne porta anche altri, e' riscritta conservando il testo originale dopo quello corretto. Se la
// scelta non e' univoca o la conservazione non e' verificabile la correzione non si applica e il percorso lo segnala: mai un'altra voce
// riscritta al suo posto.

export type EditTargetKind = 'found' | 'absent' | 'ambiguous';

export interface EditTarget {
  kind: EditTargetKind;
  /** Sezione e posizione della voce (solo per 'found'). */
  section?: string;
  index?: number;
  /** Fatti che la sostituzione semplice delle candidate cancellerebbe ('absent' con candidate, o 'found' con voce a piu' fatti). */
  lost: string[];
  /** Testo della voce dopo la correzione (solo 'found'): il testo corretto, seguito dall'originale se la voce porta altri fatti. */
  newText?: string;
  /** Fatti indipendenti conservati dalla riscrittura (voce a piu' fatti); vuoto se la sostituzione semplice basta. */
  preserved: string[];
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
function lost(p: ProjectCase, edit: Fact, texts: (section: string) => string[], section: string, index: number, replacement: string): string[] {
  const of = (f: Fact, replaced: boolean) =>
    f.accept.flatMap((s) => texts(s).map((t, n) => (replaced && s === section && n === index ? replacement : t))).map(norm);
  return p.facts
    .filter((f) => f.key !== edit.key && f.accept.includes(section))
    .filter((f) => covers(f.match, f.parts, of(f, false)) && !covers(f.match, f.parts, of(f, true)))
    .map((f) => f.key);
}

const SEGMENT_SPLIT = /\s*(?:[;,.:]|\s[eo]\s)\s*/;
const NUMBERS = /\b(\d+|uno|una|due|tre|quattro|cinque|sei|sette|otto|nove|dieci|undici|dodici|quindici|venti|trenta|cento|mille)\b/g;
const numbersOf = (text: string) => new Set(norm(text).match(NUMBERS) ?? []);

/**
 * Riscrittura circoscritta di una voce a piu' fatti: testo corretto, poi i soli segmenti dell'originale necessari a non perdere gli altri
 * fatti attesi (il sottoinsieme piu' piccolo, verificato con `covers`). Rifiuta (newText assente, con motivo) se: nessun sottoinsieme
 * conserva i fatti; piu' sottoinsiemi della stessa dimensione sono equivalenti; un segmento ripete il fatto modificato; un segmento
 * contiene numeri assenti dal testo corretto (potrebbe riportare una condizione superata). Nessun testo inventato. Specchio di Preserve in C#.
 */
export function preserve(p: ProjectCase, edit: Fact, texts: (section: string) => string[], section: string, index: number, original: string): { newText?: string; reason: string } {
  const edited = p.correction.editedText;
  const segments = original.split(SEGMENT_SPLIT).filter((x) => x.length > 0);
  if (segments.length < 2 || segments.length > 10) return { reason: `«${original}» non e' scomponibile in frammenti conservabili: trasformazione non determinabile` };
  const editedNumbers = numbersOf(edited);
  const valid: { count: number; text: string }[] = [];
  const rejected: string[] = [];
  for (let mask = 1; mask < 1 << segments.length; mask++) {
    const kept = segments.filter((_, i) => (mask & (1 << i)) !== 0);
    const fragment = kept.join('; ');
    const text = `${edited.replace(/[. ]+$/, '')}; ${fragment}`;
    if (lost(p, edit, texts, section, index, text).length) continue;
    const extra = [...numbersOf(fragment)].filter((n) => !editedNumbers.has(n));
    if (extra.length) { rejected.push(`il frammento «${fragment}» contiene valori (${extra.join(', ')}) assenti dal testo corretto e potrebbe riportare una condizione superata`); continue; }
    if (matches(norm(fragment), edit.match)) { rejected.push(`il frammento «${fragment}» ripete il fatto «${edit.key}» gia' riscritto dal PM`); continue; }
    valid.push({ count: kept.length, text });
  }
  if (!valid.length) return { reason: rejected.length ? [...new Set(rejected)].join('; ') : "nessun frammento dell'originale conserva gli altri fatti" };
  const smallest = Math.min(...valid.map((x) => x.count));
  const best = [...new Set(valid.filter((x) => x.count === smallest).map((x) => x.text))];
  return best.length === 1 ? { newText: best[0], reason: '' } : { reason: `${best.length} sottoinsiemi di frammenti equivalenti: trasformazione non determinabile` };
}

/** Individua la voce da modificare; `texts` restituisce i testi di una sezione nell'ordine dell'anteprima. */
export function locateEdit(p: ProjectCase, texts: (section: string) => string[]): EditTarget {
  const edit = p.facts.find((f) => f.key === p.correction.editKey)!;
  const where = edit.accept.map((s) => SEC[s]).join('/');
  const candidates = edit.accept.flatMap((section) => texts(section).map((text, index) => ({ section, index, text })))
    .filter((x) => matches(norm(x.text), edit.match));
  if (!candidates.length) return { kind: 'absent', lost: [], preserved: [], message: `correzione non applicabile: la voce «${edit.key}» da modificare non e' nell'anteprima (${where})` };

  const assessed = candidates.map((c) => ({ c, lost: lost(p, edit, texts, c.section, c.index, p.correction.editedText) }));
  const safe = assessed.filter((x) => !x.lost.length).map((x) => x.c);
  const dedicated = safe.filter((x) => !carries(p, edit, x.section, x.text));
  const pool = dedicated.length ? dedicated : safe;
  if (pool.length === 1) {
    const [t] = pool;
    return { kind: 'found', section: t.section, index: t.index, lost: [], preserved: [], newText: p.correction.editedText, message: `correzione: «${edit.key}» in ${SEC[t.section]} («${t.text}»)` };
  }
  if (pool.length > 1) {
    return {
      kind: 'ambiguous', lost: [], preserved: [],
      message: `correzione non applicabile: «${edit.key}» ambigua, ${pool.length} voci candidate (${pool.map((x) => `${SEC[x.section]}: «${x.text}»`).join('; ')}), nessuna scelta silenziosa`,
    };
  }

  // Nessuna candidata e' sostituibile senza perdere altri fatti: voce a piu' fatti riscritta conservando solo i frammenti necessari, se univoco e sicuro.
  const attempts = assessed.map((x) => ({ ...x, result: preserve(p, edit, texts, x.c.section, x.c.index, x.c.text) }));
  const repairable = attempts.filter((x) => x.result.newText !== undefined).map((x) => ({ ...x, newText: x.result.newText! }));
  if (repairable.length === 1) {
    const [r] = repairable;
    return {
      kind: 'found', section: r.c.section, index: r.c.index, lost: r.lost, preserved: r.lost, newText: r.newText,
      message: `correzione: «${edit.key}» in ${SEC[r.c.section]} («${r.c.text}»), voce con piu' fatti riscritta conservando ${r.lost.map((k) => `«${k}»`).join(', ')}`,
    };
  }
  if (repairable.length > 1) {
    return {
      kind: 'ambiguous', lost: [], preserved: [],
      message: `correzione non applicabile: «${edit.key}» ambigua, ${repairable.length} voci candidate a piu' fatti (${repairable.map((x) => `${SEC[x.c.section]}: «${x.c.text}»`).join('; ')}), nessuna scelta silenziosa`,
    };
  }
  const all = [...new Set(assessed.flatMap((x) => x.lost))];
  return {
    kind: 'absent', lost: all, preserved: [],
    message: `correzione non applicabile: nessuna voce di «${edit.key}» in ${where} puo' essere riscritta conservando gli altri fatti; sostituire ${assessed.map((x) => `«${x.c.text}»`).join(', ')} `
      + `cancellerebbe ${all.map((k) => `«${k}»`).join(', ')} (fatti indipendenti dalla correzione); `
      + attempts.map((x) => x.result.reason).join('; '),
  };
}
