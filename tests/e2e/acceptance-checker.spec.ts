import { expect, test } from '@playwright/test';
import { acceptanceCases, checkAnalysis, checkDraft, type Content, type Item, type ProjectCase, type UserIndex } from './support/acceptance';

// T052 - il verificatore TypeScript dei casi di accettazione (support/acceptance.ts) e' coerente con le fixture condivise e con quello
// backend: analisi/bozza ideali passano, i difetti tipici sono rilevati. Non richiede stack, modello ne' browser: prova le REGOLE,
// non la qualita' di un modello. Le prove con servizi reali sono in project-draft-success.spec.ts (ignorate senza stack).

const uuid = () => crypto.randomUUID();
const projects = acceptanceCases.flatMap((c) => c.projects.map((p) => ({ caseId: c.id, p })));

function ideal(p: ProjectCase) {
  const users = p.messages.map(() => uuid());
  const index: UserIndex = new Map(users.map((id, i) => [id, i + 1]));
  const sources = new Map<number, string>();
  const src = (from: number[]) => from.map((n) => { if (!sources.has(n)) sources.set(n, uuid()); return sources.get(n)!; });
  const item = (text: string, cls: string, from: number[]): Item => ({ id: uuid(), text, classification: cls, evidenceRefs: src(from) });
  const sec: Record<string, Item[]> = Object.fromEntries(['goals', 'overview', 'included', 'excluded', 'requirements', 'flows', 'constraints', 'dependencies', 'assumptions', 'questions'].map((k) => [k, []]));
  for (const f of p.facts) sec[f.section].push(item(f.text, f.declared ? 'Declared' : 'Inferred', f.from));
  const conflicts = p.conflicts.map((k) => ({
    id: uuid(), description: k.description, status: 'Open', resolution: null,
    alternatives: k.altText.map((t, n) => item(t, 'Declared', k.altFrom[n])),
  }));
  const content: Content = {
    validationState: 'Unvalidated',
    sources: [...sources].map(([n, id]) => ({ id, kind: 'Message', messageId: users[n - 1] })),
    goals: sec['goals'],
    contextAndScope: { overview: sec['overview'], included: sec['included'], excluded: sec['excluded'] },
    requirements: sec['requirements'], operationalFlows: sec['flows'], constraints: sec['constraints'], dependencies: sec['dependencies'],
    assumptions: sec['assumptions'], openQuestions: sec['questions'], conflicts,
  };
  return { content, index };
}

/** Bozza ideale dopo la correzione del PM: ogni voce con le sue fonti, aperti e conflitti dichiarati tali. */
function idealDraft(p: ProjectCase, content: Content) {
  const pm = uuid();
  const c = p.correction;
  const cite = (refs: string[]) => refs.map((r) => ` [fonte:${r}]`).join('');
  const line = (t: string, refs: string[] = []) => `- ${t}${cite(refs)}`;
  const edited = (i: Item) => (p.facts.find((f) => f.key === c.editKey)!.match.every((m) => new RegExp(m, 'i').test(i.text.toLowerCase())) ? { ...i, text: c.editedText, evidenceRefs: [...i.evidenceRefs, pm] } : i);
  const out: string[] = [`# ${p.name} - bozza da revisionare`, ''];
  const sect = (title: string, lines: string[], level = 2) => { if (lines.length) out.push(`${'#'.repeat(level)} ${title}`, '', ...lines, ''); };
  const all = (xs: Item[]) => xs.map(edited).map((i) => line(i.text, i.evidenceRefs));
  const add = (section: string) => c.additions.filter((a) => a.section === section).map((a) => line(a.text, [pm]));
  sect('Sintesi e obiettivi', [...all(content.goals), ...add('goals')]);
  out.push('## Contesto e perimetro', '');
  sect('Panoramica', [...all(content.contextAndScope.overview), ...add('overview')], 3);
  sect('Incluso', [...all(content.contextAndScope.included), ...add('included')], 3);
  sect('Esclusioni dal perimetro', [...all(content.contextAndScope.excluded), ...add('excluded')], 3);
  sect('Requisiti', [...all(content.requirements), ...add('requirements')]);
  sect('Flussi operativi', [...all(content.operationalFlows), ...add('flows')]);
  sect('Vincoli e dipendenze', [...all(content.constraints), ...add('constraints'), ...all(content.dependencies), ...add('dependencies')]);
  sect('Assunzioni da verificare', [...content.assumptions.map((i) => line('Ipotesi da verificare: ' + i.text, i.evidenceRefs)), ...c.additions.filter((a) => a.section === 'assumptions').map((a) => line('Ipotesi da verificare: ' + a.text, [pm]))]);
  const open = content.openQuestions.map((i) => line('Punto aperto: ' + i.text, i.evidenceRefs));
  for (const [n, k] of content.conflicts.entries()) {
    const spec = p.conflicts[n];
    if (c.resolve?.conflictKey === spec.key) open.push(line(`Conflitto risolto dal PM: ${k.description} Scelta: ${k.alternatives[c.resolve.alternative].text}. Motivo: ${c.resolve.reason}`, [pm]));
    else open.push(`- Conflitto aperto, non risolto: ${k.description} ` + k.alternatives.map((a) => a.text + cite(a.evidenceRefs)).join(' Alternativa: '));
  }
  sect('Punti aperti e conflitti', open);
  const sourceIds = new Set([...content.sources.map((s) => s.id), pm]);
  return { markdown: out.join('\n'), sourceIds };
}

test.describe('verificatore dei casi di accettazione (senza stack)', () => {
  test('ci sono cinque casi nominali di cinque messaggi PM e il caso dei progetti distinti ha due progetti', () => {
    expect(acceptanceCases.map((c) => c.id)).toEqual(['N1-frammenti', 'N2-negazioni', 'N3-lacune', 'N4-conflitti', 'N5-progetti-distinti']);
    for (const c of acceptanceCases) for (const p of c.projects) expect(p.messages).toHaveLength(5);
    expect(acceptanceCases[4].projects).toHaveLength(2);
  });

  for (const { caseId, p } of projects) {
    test(`${caseId}/${p.key}: analisi e bozza ideali rispettano tutte le attese`, () => {
      const { content, index } = ideal(p);
      expect(checkAnalysis(p, content, index)).toEqual([]);
      const { markdown, sourceIds } = idealDraft(p, content);
      expect(checkDraft(p, markdown, sourceIds, true)).toEqual([]);
    });

    test(`${caseId}/${p.key}: omissioni, negazioni invertite, invenzioni e conflitti scelti in silenzio sono rilevati`, () => {
      // analisi: omissione di una voce attesa
      const missing = ideal(p);
      const target = p.facts.find((f) => f.section === 'requirements' && f.key !== p.correction.editKey)!;
      missing.content.requirements = missing.content.requirements.filter((i) => i.text !== target.text);
      expect(checkAnalysis(p, missing.content, missing.index).some((f) => f.startsWith('omissione'))).toBe(true);

      // analisi: informazione vietata affermata, fonte di un altro progetto
      const invented = ideal(p);
      const forbid = p.forbids.find((f) => !(f.conflictKey && f.conflictKey === p.correction.resolve?.conflictKey))!;
      invented.content.requirements.push({ id: uuid(), text: forbid.sample, classification: 'Declared', evidenceRefs: [invented.content.sources[0].id] });
      expect(checkAnalysis(p, invented.content, invented.index).some((f) => f.includes(`«${forbid.key}»`))).toBe(true);
      const foreign = ideal(p);
      foreign.content.sources.push({ id: uuid(), kind: 'Message', messageId: uuid() });
      expect(checkAnalysis(p, foreign.content, foreign.index).some((f) => f.includes('non appartiene al progetto'))).toBe(true);

      // bozza: voce omessa, divieto affermato, citazione inventata, presentata come approvata
      const base = ideal(p);
      const draft = idealDraft(p, base.content);
      const dropped = draft.markdown.split('\n').filter((l) => !l.includes(target.text)).join('\n');
      expect(checkDraft(p, dropped, draft.sourceIds, true).some((f) => f.startsWith('omissione nella bozza'))).toBe(true);
      expect(checkDraft(p, `${draft.markdown}\n## Aggiunte\n\n- ${forbid.sample}\n`, draft.sourceIds, true).some((f) => f.includes(`«${forbid.key}»`))).toBe(true);
      expect(checkDraft(p, `${draft.markdown}\n## Note\n\n- Un fatto [fonte:${uuid()}]\n`, draft.sourceIds, true).some((f) => f.includes("non e' una fonte"))).toBe(true);
      expect(checkDraft(p, `${draft.markdown}\n## Stato\n\n- Il documento è approvato dal cliente\n`, draft.sourceIds, true).some((f) => f.includes('approvata'))).toBe(true);
      if (p.conflicts.some((k) => k.key !== p.correction.resolve?.conflictKey)) {
        const silent = draft.markdown.split('\n').filter((l) => !l.includes('Conflitto aperto')).join('\n');
        expect(checkDraft(p, silent, draft.sourceIds, true).some((f) => f.includes('conflitto'))).toBe(true);
      }
    });
  }

  // Regressioni dell'audit V7 (stesse regole di AcceptanceCheckerSemanticsTests): soggetto, parti atomiche, qualificatori.
  const project = (key: string) => acceptanceCases.flatMap((c) => c.projects).find((x) => x.key === key)!;
  const withItems = (p: ProjectCase, drop: string[], add: [keyof Content, string, number][]) => {
    const r = ideal(p);
    const texts = drop.map((k) => p.facts.find((f) => f.key === k)!.text);
    for (const s of ['goals', 'requirements', 'operationalFlows', 'constraints', 'dependencies'] as const) r.content[s] = r.content[s].filter((i) => !texts.includes(i.text));
    const sourceOf = (n: number) => {
      const id = [...r.index].find(([, i]) => i === n)![0];
      let s = r.content.sources.find((x) => x.messageId === id);
      if (!s) r.content.sources.push((s = { id: uuid(), kind: 'Message', messageId: id }));
      return s.id;
    };
    for (const [s, text, from] of add) (r.content[s] as Item[]).push({ id: uuid(), text, classification: 'Declared', evidenceRefs: [sourceOf(from)] });
    return r;
  };
  const flagged = (failures: string[], key: string) => failures.some((f) => f.includes(`«${key}»`));

  test('paghe e ratei: errore solo se il nostro sistema ne e\' il soggetto affermato', () => {
    const ferie = project('ferie');
    const run = (text: string) => { const r = withItems(ferie, [], [['dependencies', text, 2]]); return checkAnalysis(ferie, r.content, r.index); };
    expect(flagged(run('Il calcolo di buste paga e ratei di ferie resta nel software paghe'), 'paghe')).toBe(false);
    expect(flagged(run('Il software paghe calcola le buste paga e i ratei'), 'ratei')).toBe(false);
    expect(flagged(run('Il portale calcola le buste paga'), 'paghe')).toBe(true);
    expect(flagged(run('Il PORTALE gestisce i ratei di ferie maturati'), 'ratei')).toBe(true);
    expect(flagged(run('Il portale calcola le buste paga, che restano nel software paghe'), 'paghe')).toBe(true);
    expect(flagged(run('Il portale calcola le buste paga e non le trasferte'), 'paghe')).toBe(true);
    expect(flagged(run('Integrazione con il software paghe per importare i dati'), 'integrazione-paghe')).toBe(true);
  });

  test('richiesta/approvazione e ordini/spedizione in voci distinte coprono l\'attesa; un soggetto diverso no', () => {
    const ferie = project('ferie');
    const split = withItems(ferie, ['richiesta'], [['requirements', 'Il dipendente invia la richiesta di ferie o permessi', 1], ['requirements', 'Il responsabile diretto approva o rifiuta', 1]]);
    expect(flagged(checkAnalysis(ferie, split.content, split.index), 'richiesta')).toBe(false);
    const wrong = withItems(ferie, ['richiesta'], [['requirements', 'Il dipendente invia la richiesta di ferie o permessi', 1], ['requirements', 'Il sistema approva o rifiuta automaticamente', 1]]);
    expect(flagged(checkAnalysis(ferie, wrong.content, wrong.index), 'richiesta')).toBe(true);

    const boreale = project('boreale');
    const orders = withItems(boreale, ['ordini'], [['requirements', 'I clienti inseriscono gli ordini', 1], ['requirements', 'I clienti seguono la spedizione', 1]]);
    expect(flagged(checkAnalysis(boreale, orders.content, orders.index), 'ordini')).toBe(false);
    const courier = withItems(boreale, ['ordini'], [['requirements', 'I clienti inseriscono gli ordini', 1], ['requirements', 'Il corriere gestisce la spedizione', 1]]);
    expect(flagged(checkAnalysis(boreale, courier.content, courier.index), 'ordini')).toBe(true);
  });

  test('Colorweb senza qualificatori e\' un dettaglio assente, barcode separato e\' un legame perso; Colorweb indebolita fallisce', () => {
    const boreale = project('boreale');
    const bare = withItems(boreale, ['integrazione'], [['dependencies', 'Integrazione con il gestionale ordini Colorweb', 3]]);
    const colorweb = checkAnalysis(boreale, bare.content, bare.index).find((f) => f.startsWith('omissione: «integrazione»'))!;
    expect(colorweb).toContain('qualificatore o dettaglio assente');
    const weak = withItems(boreale, [], [['dependencies', 'Integrazione obbligatoria con Colorweb, ma non in tempo reale', 3]]);
    expect(flagged(checkAnalysis(boreale, weak.content, weak.index), 'colorweb-indebolita')).toBe(true);

    const magazzino = project('magazzino');
    const split = withItems(magazzino, ['barcode'], [['goals', 'Gestire le scorte del magazzino ricambi con un lettore di codici a barre', 1], ['requirements', 'I magazzinieri registrano carichi e scarichi', 1]]);
    const barcode = checkAnalysis(magazzino, split.content, split.index).find((f) => f.startsWith('omissione: «barcode»'))!;
    expect(barcode).toContain('legame perso fra voci');
    expect(barcode).not.toContain('qualificatore');
  });

  test('un fatto tolto dalla correzione PM non e\' un\'omissione della bozza, uno perso dal generatore si', () => {
    const ferie = project('ferie');
    const ai = withItems(ferie, ['richiesta'], [['requirements', 'Il dipendente invia la richiesta di ferie o permessi', 1], ['requirements', 'Il responsabile diretto approva o rifiuta la richiesta', 1]]);
    const confirmed = withItems(ferie, ['richiesta'], [['requirements', 'Il dipendente invia la richiesta di ferie o permessi', 1]]);
    const { markdown, sourceIds } = idealDraft(ferie, confirmed.content);
    expect(flagged(checkDraft(ferie, markdown, sourceIds, true, { ai: ai.content, confirmed: confirmed.content }), 'richiesta')).toBe(false);
    expect(checkDraft(ferie, markdown, sourceIds, true, { confirmed: confirmed.content }).some((f) => f.includes("«richiesta» (assente gia'"))).toBe(true);
    // stessa bozza, ma la revisione confermata conteneva ancora il fatto: lo ha perso la generazione
    expect(checkDraft(ferie, markdown, sourceIds, true, { confirmed: ai.content }).some((f) => f.includes('«richiesta» (presente nella revisione confermata: persa nella generazione)'))).toBe(true);
  });

  test('un testo di un altro progetto nell\'analisi e nella bozza e\' rilevato', () => {
    const aurora = acceptanceCases[4].projects[0];
    const a = ideal(aurora);
    a.content.goals[0] = { ...a.content.goals[0], text: `${a.content.goals[0].text} (integrazione Colorweb per gli ordini)` };
    expect(checkAnalysis(aurora, a.content, a.index).some((f) => f.includes('progetto-boreale'))).toBe(true);
  });
});
