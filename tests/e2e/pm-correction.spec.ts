import { expect, test } from '@playwright/test';
import { acceptanceCases, covers, norm, type ProjectCase } from './support/acceptance';
import { locateEdit } from './support/pm-correction';

// T052 - scelta della voce nella correzione PM simulata (support/pm-correction.ts), specchio di PmCorrectionTests.cs con gli stessi
// messaggi. Non richiede stack, modello ne' browser: prova l'harness, non il modello ne' il verificatore.

const projects = acceptanceCases.flatMap((c) => c.projects.map((p) => ({ caseId: c.id, p })));
const project = (key: string) => projects.find((x) => x.p.key === key)!.p;
const APPROVES = 'Il responsabile diretto approva o rifiuta la richiesta';
const SENDS = 'Il dipendente invia la richiesta di ferie o permessi';

/** Testi dell'anteprima ideale per sezione, senza i fatti `remove` e con le voci `add` inserite in posizione. */
function preview(p: ProjectCase, remove: string[] = [], add: [section: string, at: number, text: string][] = []) {
  const sections = new Map<string, string[]>();
  for (const f of p.facts.filter((x) => !remove.includes(x.key))) sections.set(f.section, [...(sections.get(f.section) ?? []), f.text]);
  for (const [section, at, text] of add) {
    const list = sections.get(section) ?? [];
    list.splice(at, 0, text);
    sections.set(section, list);
  }
  return (section: string) => sections.get(section) ?? [];
}

test.describe('correzione PM simulata: scelta della voce (senza stack)', () => {
  test('la voce presente e\' trovata', () => {
    const ferie = project('ferie');
    const texts = preview(ferie);
    const target = locateEdit(ferie, texts);
    expect(target.kind).toBe('found');
    expect(texts(target.section!)[target.index!]).toBe(ferie.facts.find((f) => f.key === 'solo-responsabile')!.text);
  });

  test('due voci con parole simili non dirottano la modifica sulla voce sbagliata', () => {
    const ferie = project('ferie');
    const texts = preview(ferie, ['richiesta'], [['requirements', 0, SENDS], ['requirements', 1, APPROVES]]);
    const target = locateEdit(ferie, texts);
    expect(target.kind).toBe('found');
    expect(texts(target.section!)[target.index!]).toBe(ferie.facts.find((f) => f.key === 'solo-responsabile')!.text);
    // anche con la voce sull'approvazione ridondante (fatto coperto altrove), la voce dedicata all'esclusivita' prevale
    const redundant = preview(ferie, [], [['requirements', 0, APPROVES]]);
    const again = locateEdit(ferie, redundant);
    expect(redundant(again.section!)[again.index!]).toBe(ferie.facts.find((f) => f.key === 'solo-responsabile')!.text);
  });

  test('una voce assente non fa sostituire un\'altra voce', () => {
    const target = locateEdit(project('ferie'), preview(project('ferie'), ['solo-responsabile']));
    expect(target).toEqual({ kind: 'absent', lost: [], message: "correzione non applicabile: la voce «solo-responsabile» da modificare non e' nell'anteprima (Requirements/Constraints)" });
  });

  test('una voce ambigua non e\' scelta in silenzio', () => {
    const target = locateEdit(project('ferie'), preview(project('ferie'), [], [['constraints', 0, 'Solo il responsabile diretto approva le richieste di ferie']]));
    expect(target.kind).toBe('ambiguous');
    expect(target.message).toContain('2 voci candidate');
    expect(target.section).toBeUndefined();
  });

  test('forma V7 di ferie: correzione segnalata, «rifiuta» conservato', () => {
    const ferie = project('ferie');
    const target = locateEdit(ferie, preview(ferie, ['richiesta', 'solo-responsabile'], [['requirements', 0, SENDS], ['requirements', 1, APPROVES]]));
    expect(target).toEqual({
      kind: 'absent', lost: ['richiesta'],
      message: `correzione non applicabile: nessuna voce dedicata a «solo-responsabile» in Requirements/Constraints; sostituire «${APPROVES}» cancellerebbe «richiesta» (fatti indipendenti dalla correzione)`,
    });
  });

  for (const { caseId, p } of projects) {
    test(`${caseId}/${p.key}: la correzione nominale trova la sua voce e conserva gli altri fatti`, () => {
      const edit = p.facts.find((f) => f.key === p.correction.editKey)!;
      const texts = preview(p);
      const target = locateEdit(p, texts);
      expect(target.kind, target.message).toBe('found');
      expect(texts(target.section!)[target.index!]).toBe(edit.text);
      const after = (s: string) => texts(s).map((t, n) => (s === target.section && n === target.index ? p.correction.editedText : t));
      for (const f of p.facts) expect(covers(f.match, f.parts, f.accept.flatMap(after).map(norm)), f.key).toBe(true);
    });
  }
});
