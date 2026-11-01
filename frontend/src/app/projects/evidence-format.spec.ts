import { HttpErrorResponse } from '@angular/common/http';
import { EvidenceView } from './evidence-api.service';
import {
  excerpt,
  highlightSegments,
  isPromotionConflict,
  isUncertainOutcome,
  jobErrorText,
  problemOf,
  stateBadge,
  structureFields,
} from './evidence-format';

const http = (status: number, body: unknown = null) =>
  new HttpErrorResponse({ status, error: body });

describe('highlightSegments', () => {
  it('marks the cited span and keeps the surrounding text', () => {
    const text = 'Niente notifiche push, solo email.';
    const start = text.indexOf('push');
    expect(highlightSegments(text, [{ start, end: start + 4, role: 'Primary' }])).toEqual([
      { text: 'Niente notifiche ', role: null },
      { text: 'push', role: 'Primary' },
      { text: ', solo email.', role: null },
    ]);
  });

  it('uses UTF-16 offsets: a surrogate-pair emoji before the span counts as two units', () => {
    const text = 'Ciao 😀 invia la conferma via email';
    const quote = 'conferma via email';
    const start = text.indexOf(quote);
    // L'emoji occupa 2 unità UTF-16: gli indici .NET e JS coincidono, un conteggio per punti di codice no.
    expect(start).toBe(Array.from(text.slice(0, start)).length + 1);
    const segments = highlightSegments(text, [{ start, end: start + quote.length }]);
    const marked = segments.filter((s) => s.role);
    expect(marked).toHaveLength(1);
    expect(marked[0].text).toBe(quote);
    expect(segments.map((s) => s.text).join('')).toBe(text);
    expect(segments[0].text).toBe('Ciao 😀 invia la ');
  });

  it('highlights several sources and lets Primary win over Context where they overlap', () => {
    const text = 'abcdefghij';
    const segments = highlightSegments(text, [
      { start: 0, end: 6, role: 'Context' },
      { start: 4, end: 8, role: 'Primary' },
    ]);
    expect(segments).toEqual([
      { text: 'abcd', role: 'Context' },
      { text: 'efgh', role: 'Primary' },
      { text: 'ij', role: null },
    ]);
  });

  it('clips out-of-range spans and drops empty or non numeric ones', () => {
    const text = 'abc';
    expect(
      highlightSegments(text, [
        { start: -5, end: 2 },
        { start: 2, end: 99 },
        { start: 1, end: 1 },
        { start: Number.NaN, end: 2 },
      ]),
    ).toEqual([{ text: 'abc', role: 'Primary' }]);
    expect(highlightSegments('', [{ start: 0, end: 1 }])).toEqual([]);
    expect(highlightSegments('abc', [])).toEqual([{ text: 'abc', role: null }]);
  });

  it('never interprets markup: it only returns slices of the original text', () => {
    const text = '<img src=x onerror=alert(1)> ok';
    const segments = highlightSegments(text, [{ start: 0, end: 4 }]);
    expect(segments.map((s) => s.text).join('')).toBe(text);
  });
});

describe('stateBadge', () => {
  it.each([
    ['none', 'Non analizzato'],
    ['pending', 'In coda'],
    ['running', 'In elaborazione'],
    ['extracted', 'Estratto'],
    ['empty', 'Nessuna evidenza'],
    ['failed', 'Non riuscito'],
  ])('%s has the text label %s', (state, label) => {
    const badge = stateBadge(state);
    expect(badge.label).toBe(label);
    expect(badge.cls).toMatch(/text-bg-/);
  });

  it('falls back to the raw state for unknown values', () => {
    expect(stateBadge('weird').label).toBe('weird');
    expect(stateBadge(null).label).toBe('Sconosciuto');
  });

  it('gives each state a distinct label so meaning never depends on colour only', () => {
    const labels = ['none', 'pending', 'running', 'extracted', 'empty', 'failed'].map(
      (s) => stateBadge(s).label,
    );
    expect(new Set(labels).size).toBe(labels.length);
  });
});

describe('text helpers', () => {
  it('builds a one-line excerpt without splitting surrogate pairs', () => {
    expect(excerpt('a\n\n  b   c')).toBe('a b c');
    expect(excerpt('😀😀😀😀', 3)).toBe('😀😀…');
    expect(excerpt(null)).toBe('');
  });

  it('lists only non null structure fields', () => {
    const e = {
      subject: 'Addetti',
      action: null,
      object: 'carichi',
      channel: 'email',
    } as EvidenceView;
    expect(structureFields(e)).toEqual([
      { label: 'Soggetto', value: 'Addetti' },
      { label: 'Oggetto', value: 'carichi' },
      { label: 'Canale', value: 'email' },
    ]);
  });

  it('describes a job error by code, then by server message, then generically', () => {
    expect(jobErrorText({ code: 'model_timeout', message: null })).toMatch(/tempo previsto/);
    expect(jobErrorText({ code: 'x', message: 'Dettaglio redatto' })).toBe('Dettaglio redatto');
    expect(jobErrorText({ code: 'x', message: null })).toBe('Estrazione non riuscita.');
    expect(jobErrorText(null)).toBe('Estrazione non riuscita.');
  });
});

describe('HTTP problem helpers', () => {
  it('extracts status, code and jobId from a problem response', () => {
    expect(problemOf(http(409, { code: 'extraction_active', jobId: 'j-1' }))).toEqual({
      status: 409,
      code: 'extraction_active',
      jobId: 'j-1',
    });
    expect(problemOf(new Error('x'))).toBeNull();
  });

  it('recognizes only 409 promotion_conflict as a promotion conflict', () => {
    expect(isPromotionConflict(http(409, { code: 'promotion_conflict' }))).toBe(true);
    expect(isPromotionConflict(http(409, { code: 'extraction_active' }))).toBe(false);
    expect(isPromotionConflict(http(422, { code: 'promotion_conflict' }))).toBe(false);
  });

  it('treats network errors, timeouts and 5xx as uncertain outcomes', () => {
    expect(isUncertainOutcome(http(0))).toBe(true);
    expect(isUncertainOutcome(http(503))).toBe(true);
    expect(isUncertainOutcome(http(408))).toBe(true);
    expect(isUncertainOutcome(new Error('boom'))).toBe(true);
    expect(isUncertainOutcome(http(409, { code: 'extraction_active' }))).toBe(false);
    expect(isUncertainOutcome(http(404))).toBe(false);
  });
});
