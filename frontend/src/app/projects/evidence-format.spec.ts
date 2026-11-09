import { HttpErrorResponse } from '@angular/common/http';
import {
  DocumentEvidenceSource,
  EvidenceEnvelope,
  EvidenceView,
  SourceDocumentView,
} from './evidence-api.service';
import {
  evidenceRows,
  excerpt,
  highlightSegments,
  isPromotionConflict,
  isUncertainOutcome,
  jobErrorText,
  locatorText,
  problemOf,
  sourceGroups,
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

const part = (over: Partial<DocumentEvidenceSource>): DocumentEvidenceSource => ({
  sourceId: 's-1',
  kind: 'Document',
  role: 'Primary',
  citationGroupId: 'g-1',
  part: 0,
  documentId: 'd-1',
  documentVersionId: 'dv-1',
  preparationId: 'p-1',
  artifactId: 'a-1',
  blockId: 'b-1',
  start: 0,
  end: 3,
  quote: 'abc',
  ...over,
});

const resolved = (over: Partial<SourceDocumentView> = {}): SourceDocumentView => ({
  sourceId: 's-1',
  documentId: 'd-1',
  documentVersionId: 'dv-1',
  documentVersion: 2,
  originalName: 'spec.pdf',
  preparationId: 'p-1',
  generation: 1,
  artifactId: 'a-1',
  blockId: 'b-1',
  blockRef: 'p1',
  locator: { page: null, headingPath: [], bbox: null, sourceLines: null, doclingRef: null },
  passageUrl: '',
  downloadUrl: '',
  ...over,
});

describe('sourceGroups', () => {
  it('groups parts by citation, orders them and never merges their quotes', () => {
    const groups = sourceGroups(
      [
        part({ sourceId: 's-2', part: 1, blockId: 'b-2', quote: 'due' }),
        part({ sourceId: 's-1', part: 0, quote: 'uno' }),
        part({ sourceId: 's-3', citationGroupId: 'g-2', quote: 'altro' }),
      ],
      [resolved({ sourceId: 's-1' })],
    );
    expect(groups.map((g) => g.parts.map((p) => p.source.quote))).toEqual([
      ['uno', 'due'],
      ['altro'],
    ]);
    expect(groups[0].parts[0].resolved).not.toBeNull();
    expect(groups[0].parts[1].resolved).toBeNull();
  });
});

describe('locatorText', () => {
  it('reports only what the server resolved, never a presumed page', () => {
    expect(locatorText(null)).toBe('Posizione non risolvibile');
    expect(locatorText(resolved())).toBe('spec.pdf v2');
    expect(
      locatorText(
        resolved({
          locator: {
            page: 4,
            headingPath: ['A', 'B'],
            bbox: null,
            sourceLines: { start: 3, end: 5 },
            doclingRef: null,
          },
        }),
      ),
    ).toBe('spec.pdf v2 · pagina 4 · A › B · righe 3-5');
  });
});

describe('evidenceRows', () => {
  it('discriminates by evidenceViewVersion, gives documents no message and counts unknown versions', () => {
    const message = {
      id: 'e-1',
      messageSequence: 7,
      qualifiers: [],
      kind: 'Goal',
      polarity: 'Affirmed',
      certainty: 'Declared',
      statement: 's',
      subject: 'x',
      action: null,
      object: null,
      scope: null,
      purpose: null,
      instrument: null,
      channel: null,
      recipient: null,
    };
    const doc = {
      id: 'e-2',
      kind: 'Goal',
      polarity: 'Affirmed',
      certainty: 'Inferred',
      statement: 'd',
      origin: {
        kind: 'Document',
        documentId: 'd-1',
        documentVersionId: 'dv-1',
        preparationId: 'p-1',
      },
      content: {
        schemaVersion: '1.1.0',
        kindRationale: null,
        qualifiers: [{ type: 'Quantity', text: '3' }],
        structure: {
          subject: null,
          action: 'fa',
          object: null,
          scope: null,
          purpose: null,
          instrument: null,
          channel: null,
          recipient: null,
        },
        sources: [part({})],
      },
    };
    const { rows, unsupported } = evidenceRows([
      { evidenceViewVersion: '1.0.0', evidence: message },
      { evidenceViewVersion: '1.1.0', evidence: doc },
      { evidenceViewVersion: '9.9.9', evidence: {} },
      { evidenceViewVersion: '1.1.0', evidence: message },
    ] as unknown as EvidenceEnvelope[]);
    expect(unsupported).toBe(2);
    expect(rows.map((r) => [r.origin, r.messageSequence])).toEqual([
      ['Message', 7],
      ['Document', null],
    ]);
    expect(rows[1].fields).toEqual([{ label: 'Azione', value: 'fa' }]);
    expect(rows[1].groups).toHaveLength(1);
    expect(rows[1].document).toEqual({
      documentId: 'd-1',
      documentVersionId: 'dv-1',
      preparationId: 'p-1',
    });
    expect(rows[0].document).toBeNull();
  });
});
