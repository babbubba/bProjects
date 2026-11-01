import { readFileSync } from 'node:fs';
import { join } from 'node:path';

// T052 - casi di accettazione e verificatore deterministico per il browser. I dati sono la proiezione di
// backend/tests/bProjects.IntegrationTests/AcceptanceFixtures.cs (acceptance-cases.json, generato e verificato dai test backend):
// un solo insieme di casi per backend e browser. Le regole sono la versione TypeScript di AcceptanceChecker: confronto lessicale
// su testo normalizzato, condizione NECESSARIA e non sufficiente (la qualita' la giudicano persone: specs/001-project-draft/acceptance/).

export interface Fact {
  key: string;
  section: string;
  accept: string[];
  text: string;
  declared: boolean;
  from: number[];
  match: string[];
  /** Scomposizione atomica alternativa a `match`: ogni parte combacia con una voce (AcceptanceFixtures.cs, Fact.Parts). */
  parts?: string[][];
}

export interface ConflictSpec {
  key: string;
  description: string;
  altText: string[];
  altFrom: number[][];
  altMatch: string[];
}

export interface Forbid {
  key: string;
  pattern: string;
  mode: 'affirmed' | 'anywhere';
  sample: string;
  conflictKey: string | null;
  /** Responsabilita' attribuita dal PM a un sistema esterno (Forbid.External). */
  external?: string;
}

export interface Addition {
  key: string;
  section: string;
  text: string;
  assumed: boolean;
  match: string[];
}

export interface ProjectCase {
  key: string;
  name: string;
  messages: string[];
  facts: Fact[];
  conflicts: ConflictSpec[];
  forbids: Forbid[];
  correction: {
    editKey: string;
    editedText: string;
    additions: Addition[];
    resolve: { conflictKey: string; alternative: number; reason: string; reasonMatch: string } | null;
  };
}

export interface AcceptanceCase {
  id: string;
  title: string;
  projects: ProjectCase[];
}

export const acceptanceCases: AcceptanceCase[] = JSON.parse(readFileSync(join(__dirname, 'acceptance-cases.json'), 'utf8')).cases;

// ---- contenuto dell'analisi come lo espone l'API (AnalysisContent 1.0.0)

export interface Item { id: string; text: string; classification: string; evidenceRefs: string[] }
export interface Content {
  validationState: string;
  sources: { id: string; kind: string; messageId: string | null }[];
  goals: Item[];
  contextAndScope: { overview: Item[]; included: Item[]; excluded: Item[] };
  requirements: Item[];
  operationalFlows: Item[];
  constraints: Item[];
  dependencies: Item[];
  assumptions: Item[];
  openQuestions: Item[];
  conflicts: { id: string; description: string; status: string; alternatives: Item[]; resolution: unknown }[];
}

export function items(c: Content, section: string): Item[] {
  switch (section) {
    case 'goals': return c.goals;
    case 'overview': return c.contextAndScope.overview;
    case 'included': return c.contextAndScope.included;
    case 'excluded': return c.contextAndScope.excluded;
    case 'requirements': return c.requirements;
    case 'flows': return c.operationalFlows;
    case 'constraints': return c.constraints;
    case 'dependencies': return c.dependencies;
    case 'assumptions': return c.assumptions;
    default: return c.openQuestions;
  }
}

const SECTIONS = ['goals', 'overview', 'included', 'excluded', 'requirements', 'flows', 'constraints', 'dependencies', 'assumptions', 'questions'];
const POSITIVE = ['goals', 'overview', 'included', 'requirements', 'flows', 'constraints', 'dependencies'];

export const norm = (text: string) =>
  text.normalize('NFD').replace(/\p{M}/gu, '').normalize('NFC').toLowerCase().replace(/’/g, "'");

const rx = new Map<string, RegExp>();
const re = (p: string) => {
  let r = rx.get(p);
  if (!r) rx.set(p, (r = new RegExp(p, 'i')));
  return r;
};
export const matches = (text: string, patterns: string[]) => patterns.every((p) => re(p).test(text));

const NEGATION = /\b(non|niente|nessun\w*|senza|esclus\w*|fuori|no|neanche|rimand\w*|rinvi\w*|differit\w*|altrove)\b/;
const OPEN_MARKER = /(da (definire|chiarire|verificare|decidere|confermare|chiedere)|non (e|sono|ancora|noto|nota|noti|note|definit\w*|disponibil\w*|chiar\w*|decis\w*)|ancora (da|non)|apert[oa]|domand\w*|incert\w*|\?)/;
const CONFLICT_MARKER = /(conflitt\w*|contrast\w*|discord\w*|alternativ\w*|non risolt\w*|apert[oa]|da (chiarire|decidere)|oppure|\bo\b)/;
const OPEN_HEADING = /apert|conflitt|assunzion|ipotes|da chiarire|da verificare|incertezz|lacun|domand|dubb/;
const EXCLUSION_HEADING = /esclus|fuori perimetro|non inclus|non previst|fuori ambito/;
const ESTIMATES = /ore[- ]uomo|giorni[- ]uomo|man[- ]?days?|\bstim\w+\s+(di\s+|del\s+|dei\s+)?(costi|ore|tempi|impegno|budget)|€|\beuro\b/;
const APPROVAL = /\b(documento|bozza|analisi|specifica)\b[^.]{0,30}\b(approvat[oa]|definitiv[oa]|validat[oa]|accettat[oa])\b|\b(approvat[oa]|validat[oa]|accettat[oa]) dal cliente\b|\bversione definitiva\b/;
const CITATION = /\[fonte:[^\]]*\]/gi;
// Polarita' per proposizione: punteggiatura forte, avversative, coordinata che si apre con una negazione (non la virgola semplice).
const CLAUSE_BREAK = /[.;:!?]|\s(?:ma|mentre|pero|invece|bensi|tuttavia)\s|(?:,|\s(?:e|ed|o|oppure))\s+(?=(?:non|niente|nessun\w*|senza|neanche|no)\b)/;
const OWN_SYSTEM = /\b(portale|sistema|applicazione|app|sito|piattaforma|soluzione)\b/;

/** Una voce soddisfa tutto `match`, oppure ogni parte atomica annotata e' soddisfatta da una voce (mai somma libera di frammenti). */
export const covers = (match: string[], parts: string[][] | undefined, texts: string[]) =>
  texts.some((t) => matches(t, match)) || (!!parts && parts.every((p) => texts.some((t) => matches(t, p))));

/** Il testo afferma il divieto: in una proposizione, termine vietato non negato e non attribuito al sistema esterno annotato. */
export function affirms(forbid: Forbid, text: string): boolean {
  return text.split(CLAUSE_BREAK).some((clause) => {
    if (!re(forbid.pattern).test(clause) || NEGATION.test(clause)) return false;
    if (forbid.external && re(forbid.external).test(clause) && !OWN_SYSTEM.test(clause.replace(new RegExp(forbid.external, 'gi'), ' '))) return false;
    return true;
  });
}

/** Diagnosi di un'omissione: voce piu' vicina e, per ogni espressione mancante, legame perso fra voci o dettaglio assente ovunque. */
function closest(f: Fact, c: Content): string {
  const all = SECTIONS.flatMap((s) => items(c, s).map((i) => ({ s, i, t: norm(i.text) })));
  const scored = all.map((x) => ({ ...x, hits: f.match.filter((p) => re(p).test(x.t)).length })).filter((x) => x.hits > 0)
    .sort((a, b) => b.hits - a.hits || Number(f.accept.includes(b.s)) - Number(f.accept.includes(a.s)));
  if (!scored.length) return ' (nessuna voce vicina: fatto assente)';
  const best = scored[0];
  const missing = f.match.filter((p) => !re(p).test(best.t))
    .map((p) => ({ p, where: [...new Set(all.filter((x) => x.i !== best.i && re(p).test(x.t)).map((x) => x.s))] }));
  const detail = missing.map((m) => (m.where.length ? `/${m.p}/ solo in un'altra voce (${m.where.join('/')})` : `/${m.p}/ assente ovunque`)).join(', ');
  return ` (voce piu' vicina in ${best.s}: «${best.i.text}»; manca ${detail}: ${missing.some((m) => !m.where.length) ? 'qualificatore o dettaglio assente' : 'legame perso fra voci'})`;
}

/** messageId -> indice (da 1) del messaggio PM. */
export type UserIndex = Map<string, number>;

export function checkAnalysis(p: ProjectCase, c: Content, userIndex: UserIndex, expectedState = 'Unvalidated'): string[] {
  const failures: string[] = [];
  if (c.validationState !== expectedState) failures.push(`stato di validazione ${c.validationState}, atteso ${expectedState}`);
  const projectMessages = new Set(userIndex.keys());
  const sourceIndex = new Map<string, number>();
  for (const s of c.sources.filter((x) => x.kind === 'Message')) {
    if (!s.messageId || !projectMessages.has(s.messageId)) failures.push(`fonte ${s.id}: il messaggio non appartiene al progetto (o non e' un messaggio PM)`);
    else sourceIndex.set(s.id, userIndex.get(s.messageId)!);
  }
  const cited = (i: Item) => i.evidenceRefs.filter((r) => sourceIndex.has(r)).map((r) => sourceIndex.get(r)!);

  for (const f of p.facts) {
    const accepted = f.accept.flatMap((s) => items(c, s));
    if (!covers(f.match, f.parts, accepted.map((i) => norm(i.text)))) {
      const elsewhere = SECTIONS.filter((s) => !f.accept.includes(s) && covers(f.match, f.parts, items(c, s).map((i) => norm(i.text))));
      failures.push(`omissione: «${f.key}» non presente in ${f.accept.join('/')}` + (elsewhere.length ? ` (trovata in ${elsewhere.join('/')}: sezione sbagliata)` : closest(f, c)));
      continue;
    }
    const whole = accepted.filter((i) => matches(norm(i.text), f.match));
    const groups = whole.length || !f.parts ? [whole] : f.parts.map((part) => accepted.filter((i) => matches(norm(i.text), part)));
    const candidates = groups.flat();
    if (f.declared) {
      const good = groups.every((g) => g.some((i) => i.classification === 'Declared' && cited(i).some((n) => f.from.includes(n))));
      if (!good) {
        failures.push(candidates.some((i) => i.classification === 'Declared')
          ? `fonte errata: «${f.key}» non cita il messaggio PM ${f.from.join('/')}`
          : `classificazione: «${f.key}» dichiarata dal PM ma non Declared`);
      }
    }
  }

  for (const forbid of p.forbids) {
    if (forbid.mode === 'affirmed') {
      for (const s of POSITIVE) {
        for (const i of items(c, s)) {
          if (matches(norm(i.text), [forbid.pattern]) && affirms(forbid, norm(i.text))) failures.push(`«${forbid.key}» affermata in ${s} (negazione invertita o informazione inventata)`);
        }
      }
    } else if (allTexts(c).some((t) => matches(norm(t), [forbid.pattern]))) {
      failures.push(`«${forbid.key}» presente nell'analisi (informazione inventata o di un altro progetto)`);
    }
  }

  for (const spec of p.conflicts) {
    const found = c.conflicts.find((k) => spec.altMatch.every((m) => k.alternatives.some((a) => matches(norm(a.text), [m]))));
    if (!found) {
      failures.push(`conflitto «${spec.key}»: alternative non riportate entrambe (omissione o scelta silenziosa)`);
      continue;
    }
    if (found.status !== 'Open' || found.resolution) failures.push(`conflitto «${spec.key}»: risolto dall'AI (deve restare Open)`);
    spec.altMatch.forEach((m, n) => {
      const alt = found.alternatives.find((a) => matches(norm(a.text), [m]))!;
      if (!cited(alt).some((i) => spec.altFrom[n].includes(i))) failures.push(`conflitto «${spec.key}»: l'alternativa ${n + 1} non cita il messaggio PM ${spec.altFrom[n].join('/')}`);
    });
  }

  for (const s of SECTIONS) {
    const seen = new Set<string>();
    for (const i of items(c, s)) {
      const t = norm(i.text).trim();
      if (seen.has(t)) failures.push(`ripetizione: voce duplicata in ${s}`);
      seen.add(t);
    }
  }
  if (allTexts(c).some((t) => ESTIMATES.test(norm(t)))) failures.push('stima di costi/ore/tempi non presente nel dialogo (FR-016)');
  return failures;
}

export function allTexts(c: Content): string[] {
  return [
    ...SECTIONS.flatMap((s) => items(c, s).map((i) => i.text)),
    ...c.conflicts.flatMap((k) => [k.description, ...k.alternatives.map((a) => a.text)]),
  ];
}

interface Unit { text: string; raw: string; region: 'normal' | 'excl' | 'open' }

function units(markdown: string): Unit[] {
  const list: Unit[] = [];
  let heading = '';
  let current = '';
  const flush = () => {
    if (!current) return;
    const plain = norm(current.replace(CITATION, ' ')).replace(/[*_`]/g, '').replace(/\s+/g, ' ').trim();
    const h = norm(heading);
    list.push({ text: plain, raw: current, region: EXCLUSION_HEADING.test(h) ? 'excl' : OPEN_HEADING.test(h) ? 'open' : 'normal' });
    current = '';
  };
  for (const line of markdown.replace(/\r/g, '').split('\n')) {
    const t = line.trim();
    if (!t) { flush(); continue; }
    if (t.startsWith('#')) { flush(); heading = t.replace(/^#+/, '').trim(); continue; }
    if (/^([-*+]|\d+[.)])\s/.test(t)) flush();
    current += (current ? ' ' : '') + t;
  }
  flush();
  return list;
}

/**
 * `sourceIds`: fonti della revisione confermata (citazioni valide). `contents` (facoltativo) attribuisce un'omissione: presente nella
 * revisione confermata = persa nella generazione; presente solo nell'analisi AI = tolta dalla correzione PM (non e' un errore della bozza).
 */
export function checkDraft(p: ProjectCase, markdown: string, sourceIds: Set<string>, correctionApplied: boolean, contents: { ai?: Content; confirmed?: Content } = {}): string[] {
  const failures: string[] = [];
  if (!markdown.trim()) return ['bozza vuota'];
  const us = units(markdown);
  const resolve = correctionApplied ? p.correction.resolve : null;
  const facts: { key: string; section: string; match: string[]; parts?: string[][] }[] = [
    ...p.facts.map((f) => ({ key: f.key, section: f.section, match: f.match, parts: f.parts })),
    ...(correctionApplied ? p.correction.additions.map((a) => ({ key: a.key, section: a.section, match: a.match })) : []),
  ];
  const texts = (c?: Content) => (c ? allTexts(c).map(norm) : undefined);
  const confirmedTexts = texts(contents.confirmed);
  const aiTexts = texts(contents.ai);
  for (const f of facts) {
    const okFor = (patterns: string[]) => {
      const found = us.filter((u) => matches(u.text, patterns));
      return f.section === 'excluded' ? found.filter((u) => u.region === 'excl' || NEGATION.test(u.text))
        : f.section === 'questions' ? found.filter((u) => u.region === 'open' || OPEN_MARKER.test(u.text))
          : found;
    };
    const hits = us.filter((u) => matches(u.text, f.match));
    if (okFor(f.match).length > 0 || (f.parts && f.parts.every((part) => okFor(part).length > 0))) continue;
    if (hits.length) failures.push(`omissione nella bozza: «${f.key}»` + (f.section === 'excluded' ? ' (presente ma non come esclusione: negazione invertita?)' : ' (presente ma non come punto aperto: lacuna trasformata in fatto?)'));
    else if (confirmedTexts && covers(f.match, f.parts, confirmedTexts)) failures.push(`omissione nella bozza: «${f.key}» (presente nella revisione confermata: persa nella generazione)`);
    else if (aiTexts && covers(f.match, f.parts, aiTexts)) continue; // tolta o riscritta dalla correzione PM: la bozza segue la revisione
    else failures.push(`omissione nella bozza: «${f.key}»` + (confirmedTexts ? " (assente gia' dalla revisione confermata: omissione ereditata dall'analisi)" : ''));
  }
  for (const spec of p.conflicts) {
    if (resolve && resolve.conflictKey === spec.key) {
      if (!us.some((u) => matches(u.text, [spec.altMatch[resolve.alternative]]))) failures.push(`conflitto «${spec.key}» risolto dal PM: alternativa scelta non riportata nella bozza`);
      if (!us.some((u) => matches(u.text, [resolve.reasonMatch]))) failures.push(`conflitto «${spec.key}» risolto dal PM: motivo non riportato nella bozza`);
      continue;
    }
    const found = spec.altMatch.map((m) => us.filter((u) => matches(u.text, [m])));
    if (found.some((x) => x.length === 0)) failures.push(`conflitto «${spec.key}» aperto: alternativa omessa nella bozza (scelta silenziosa)`);
    else if (!found.flat().some((u) => u.region === 'open' || CONFLICT_MARKER.test(u.text))) failures.push(`conflitto «${spec.key}» aperto: la bozza non lo presenta come aperto`);
  }
  for (const forbid of p.forbids) {
    if (forbid.conflictKey && resolve?.conflictKey === forbid.conflictKey) continue;
    for (const u of us.filter((x) => matches(x.text, [forbid.pattern]))) {
      if (forbid.mode === 'anywhere') { failures.push(`«${forbid.key}» presente nella bozza (informazione inventata o di un altro progetto)`); break; }
      const exempt = u.region === 'excl' || !affirms(forbid, u.text) || u.region === 'open' || OPEN_MARKER.test(u.text) || (forbid.conflictKey !== null && CONFLICT_MARKER.test(u.text));
      if (!exempt) { failures.push(`«${forbid.key}» affermata nella bozza (negazione invertita o scelta silenziosa)`); break; }
    }
  }
  const markers = [...markdown.matchAll(CITATION)].map((m) => m[0].slice(7, -1));
  if (markers.length === 0) failures.push('la bozza non cita alcuna fonte');
  for (const id of new Set(markers.filter((m) => ![...sourceIds].some((s) => s.toLowerCase() === m.toLowerCase())))) failures.push(`citazione ${id}: non e' una fonte della revisione confermata`);
  const counts = new Map<string, number>();
  for (const u of us.filter((x) => x.text.length >= 40)) counts.set(u.text, (counts.get(u.text) ?? 0) + 1);
  if ([...counts.values()].some((n) => n > 1)) failures.push('ripetizione: lo stesso punto compare piu\' volte nella bozza');
  if (us.some((u) => APPROVAL.test(u.text) && !NEGATION.test(u.text))) failures.push('la bozza si presenta come approvata/definitiva (conferma PM non e\' approvazione cliente)');
  if (us.some((u) => ESTIMATES.test(u.text))) failures.push('stima di costi/ore/tempi non presente nel dialogo (FR-016)');
  return failures;
}
