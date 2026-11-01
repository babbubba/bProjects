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
    expect(target).toEqual({ kind: 'absent', lost: [], preserved: [], message: "correzione non applicabile: la voce «solo-responsabile» da modificare non e' nell'anteprima (Requirements/Constraints)" });
  });

  test('una voce ambigua non e\' scelta in silenzio', () => {
    const target = locateEdit(project('ferie'), preview(project('ferie'), [], [['constraints', 0, 'Solo il responsabile diretto approva le richieste di ferie']]));
    expect(target.kind).toBe('ambiguous');
    expect(target.message).toContain('2 voci candidate');
    expect(target.section).toBeUndefined();
  });

  test("voce a piu' fatti: riscritta conservando i fatti indipendenti (forma V7 di ferie)", () => {
    const ferie = project('ferie');
    const texts = preview(ferie, ['richiesta', 'solo-responsabile'], [['requirements', 0, SENDS], ['requirements', 1, APPROVES]]);
    const target = locateEdit(ferie, texts);
    expect(target.kind).toBe('found');
    expect(target.preserved).toEqual(['richiesta']);
    // solo il frammento necessario: nessuna approvazione ripetuta
    expect(target.newText).toBe(`${ferie.correction.editedText}; rifiuta la richiesta`);
    expect(norm(target.newText!).match(/approv/g)).toHaveLength(1);
    expect(target.newText!.startsWith(ferie.correction.editedText)).toBe(true);
    expect(texts(target.section!)[target.index!]).toBe(APPROVES);
    // dopo la riscrittura «richiesta» (con «rifiuta») e «solo-responsabile» sono entrambi coperti
    const after = (s: string) => texts(s).map((t, n) => (s === target.section && n === target.index ? target.newText! : t));
    for (const key of ['richiesta', 'solo-responsabile']) {
      const f = ferie.facts.find((x) => x.key === key)!;
      expect(covers(f.match, f.parts, f.accept.flatMap(after).map(norm)), key).toBe(true);
    }
  });

  // Ferie con una correzione che cambia il limite da 5 a 10 giorni (solo questo script di prova; la fixture originale resta intatta).
  const anticipo10 = (): ProjectCase => {
    const f = project('ferie');
    return {
      ...f,
      facts: f.facts.map((x) => (x.key === 'anticipo' ? { ...x, match: ['giorni', 'anticip'] } : x)),
      correction: { ...f.correction, editKey: 'anticipo', editedText: 'Le ferie vanno richieste con almeno 10 giorni lavorativi di anticipo', additions: [] },
    };
  };

  test('una modifica che cambia un limite non ripristina il valore superato', () => {
    const s = anticipo10();
    const texts = preview(s, ['anticipo', 'urgenze'], [['constraints', 0, 'Le ferie vanno richieste con almeno 5 giorni lavorativi di anticipo, salvo urgenze decise dal responsabile']]);
    const target = locateEdit(s, texts);
    expect(target.kind).toBe('found');
    expect(target.preserved).toEqual(['urgenze']);
    expect(target.newText).toBe(`${s.correction.editedText}; salvo urgenze decise dal responsabile`);
    expect(norm(target.newText!)).toContain('10 giorni');
    expect(norm(target.newText!)).not.toMatch(/\b5\b/);
  });

  test('un frammento che riporterebbe un valore superato fa fallire la correzione in modo esplicito', () => {
    const s = anticipo10();
    const target = locateEdit(s, preview(s, ['anticipo', 'urgenze'], [['constraints', 0, 'Le ferie vanno richieste con almeno 5 giorni lavorativi di anticipo; le urgenze sono decise dal responsabile entro 5 giorni']]));
    expect(target.kind).toBe('absent');
    expect(target.newText).toBeUndefined();
    expect(target.message).toContain('assenti dal testo corretto e potrebbe riportare una condizione superata');
  });

  test("una voce a piu' fatti non scomponibile fallisce invece di inventare testo", () => {
    const s = anticipo10();
    const target = locateEdit(s, preview(s, ['anticipo', 'urgenze'], [['constraints', 0, 'Le ferie vanno richieste con almeno 5 giorni di anticipo salvo urgenze']]));
    expect(target.kind).toBe('absent');
    expect(target.message).toContain('trasformazione non determinabile');
  });

  test("due voci a piu' fatti equivalenti: segnalate, non scelte in silenzio", () => {
    const ferie = project('ferie');
    const target = locateEdit(ferie, preview(ferie, ['richiesta', 'solo-responsabile', 'ufficio-riepilogo'], [
      ['requirements', 0, APPROVES], ['requirements', 1, "L'ufficio personale vede soltanto il riepilogo, non il responsabile diretto"],
    ]));
    expect(target.kind).toBe('ambiguous');
    expect(target.message).toContain("voci candidate a piu' fatti");
    expect(target.newText).toBeUndefined();
    expect(target.section).toBeUndefined();
  });

  test('senza fatti da conservare resta la sostituzione semplice', () => {
    const ferie = project('ferie');
    const target = locateEdit(ferie, preview(ferie));
    expect(target.kind).toBe('found');
    expect(target.preserved).toEqual([]);
    expect(target.newText).toBe(ferie.correction.editedText);
  });

  test("un'omissione reale del modello non viene aggiunta dalla correzione", () => {
    const ferie = project('ferie');
    const texts = preview(ferie, ['richiesta'], [['requirements', 0, SENDS]]);
    const target = locateEdit(ferie, texts);
    expect(target.kind).toBe('found');
    expect(target.preserved).toEqual([]);
    const after = (s: string) => texts(s).map((t, n) => (s === target.section && n === target.index ? target.newText! : t));
    const f = ferie.facts.find((x) => x.key === 'richiesta')!;
    expect(covers(f.match, f.parts, f.accept.flatMap(after).map(norm))).toBe(false);
  });

  for (const { caseId, p } of projects) {
    test(`${caseId}/${p.key}: la correzione nominale trova la sua voce e conserva gli altri fatti`, () => {
      const edit = p.facts.find((f) => f.key === p.correction.editKey)!;
      const texts = preview(p);
      const target = locateEdit(p, texts);
      expect(target.kind, target.message).toBe('found');
      expect(texts(target.section!)[target.index!]).toBe(edit.text);
      const after = (s: string) => texts(s).map((t, n) => (s === target.section && n === target.index ? target.newText! : t));
      for (const f of p.facts) expect(covers(f.match, f.parts, f.accept.flatMap(after).map(norm)), f.key).toBe(true);
    });
  }
});
