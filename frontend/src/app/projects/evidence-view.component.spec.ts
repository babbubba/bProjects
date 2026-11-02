import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import {
  CoverageReportView,
  EvidenceDetailView,
  EvidenceEnvelope,
  EvidenceStatusView,
  EvidenceView,
  ExtractionJobView,
  MessageEvidenceStatus,
  MessageVersionsView,
} from './evidence-api.service';
import { EVIDENCE_POLL_MS, EvidenceViewComponent } from './evidence-view.component';
import { ProjectEventsService, ProjectStreamHandlers } from './project-events.service';

const P = 'p-1';
const BASE = `/api/projects/${P}`;
const listUrl = (r: { url: string }) => r.url === `${BASE}/evidence`;
const detailUrl = (r: { url: string }) => r.url === `${BASE}/evidence/e-1`;
const detailUrl_doc = (r: { url: string }) => r.url === `${BASE}/evidence/e-doc`;
const envelope = (e: EvidenceView): EvidenceEnvelope => ({
  evidenceViewVersion: '1.0.0',
  evidence: e,
});

const job = (over: Partial<ExtractionJobView> = {}): ExtractionJobView => ({
  id: 'j-1',
  messageId: 'm-1',
  messageSequence: 1,
  reason: 'Initial',
  batchId: null,
  retryOfJobId: null,
  status: 'Completed',
  requestedAt: '2030-01-01T10:00:00Z',
  startedAt: null,
  completedAt: null,
  deadlineAt: '2030-01-01T10:10:00Z',
  attemptCount: 1,
  maxAttempts: 3,
  nextAttemptAt: null,
  resultVersionId: null,
  error: null,
  ...over,
});

const message = (over: Partial<MessageEvidenceStatus> = {}): MessageEvidenceStatus => ({
  messageId: 'm-1',
  sequence: 1,
  createdAt: '2030-01-01T10:00:00Z',
  stateRevision: 2,
  currentVersion: {
    id: 'v-1',
    version: 1,
    evidenceCount: 1,
    noEvidenceReason: null,
    createdAt: '2030-01-01T10:00:00Z',
  },
  candidateCount: 0,
  latestJob: job(),
  state: 'extracted',
  ...over,
});

const status = (messages: MessageEvidenceStatus[]): EvidenceStatusView => ({
  messages,
  queue: { queued: 0, running: 0, failed: 0 },
});

const TEXT = 'Ciao 😀 niente notifiche push, solo email';
const evidence = (over: Partial<EvidenceView> = {}): EvidenceView => {
  const start = TEXT.indexOf('niente notifiche push');
  return {
    id: 'e-1',
    versionId: 'v-1',
    version: 1,
    isCurrent: true,
    messageId: 'm-1',
    messageSequence: 1,
    ordinal: 0,
    key: 'k',
    kind: 'Constraint',
    kindRationale: null,
    statement: 'Nessuna notifica push',
    subject: null,
    action: 'notificare',
    object: null,
    scope: 'notifiche',
    purpose: null,
    instrument: null,
    channel: 'push',
    recipient: null,
    qualifiers: [{ type: 'Exclusivity', text: 'solo email' }],
    polarity: 'Prohibited',
    certainty: 'Declared',
    sources: [
      {
        messageId: 'm-1',
        sequence: 1,
        role: 'Primary',
        start,
        end: start + 21,
        quote: 'niente notifiche push',
      },
    ],
    ...over,
  };
};

const versions = (over: Partial<MessageVersionsView> = {}): MessageVersionsView => ({
  messageId: 'm-1',
  sequence: 1,
  stateRevision: 5,
  currentVersionId: 'v-1',
  versions: [
    {
      id: 'v-2',
      version: 2,
      createdAt: '2030-01-02T10:00:00Z',
      evidenceCount: 2,
      noEvidenceReason: null,
      promptVersion: 'p',
      schemaVersion: 's',
      modelId: 'm',
      jobId: 'j-2',
      reason: 'Reanalysis',
      isCurrent: false,
    },
    {
      id: 'v-1',
      version: 1,
      createdAt: '2030-01-01T10:00:00Z',
      evidenceCount: 1,
      noEvidenceReason: null,
      promptVersion: 'p',
      schemaVersion: 's',
      modelId: 'm',
      jobId: 'j-1',
      reason: 'Initial',
      isCurrent: true,
    },
  ],
  promotions: [],
  ...over,
});

describe('EvidenceViewComponent', () => {
  let fixture: ComponentFixture<EvidenceViewComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let handlers: ProjectStreamHandlers;
  const closeStream = vi.fn();
  const open = vi.fn();

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid=${id}]`);
  const qa = (id: string) => Array.from(el.querySelectorAll<HTMLElement>(`[data-testid=${id}]`));
  const click = async (id: string, index = 0) => {
    qa(id)[index].click();
    await settle();
  };

  async function setup(
    opts: {
      messages?: MessageEvidenceStatus[];
      items?: EvidenceView[];
      /** Elenco già in formato envelope (liste miste, versioni ignote). */
      envelopes?: unknown[];
      analysisId?: string | null;
      pollMs?: number;
    } = {},
  ) {
    open.mockReset();
    closeStream.mockReset();
    open.mockImplementation((_id: string, h: ProjectStreamHandlers) => {
      handlers = h;
      return closeStream;
    });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: EVIDENCE_POLL_MS, useValue: opts.pollMs ?? 0 },
        { provide: ProjectEventsService, useValue: { open } },
        { provide: AuthService, useValue: { isAuthenticated: signal(true).asReadonly() } },
      ],
    });
    fixture = TestBed.createComponent(EvidenceViewComponent);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement;
    fixture.componentRef.setInput('projectId', P);
    fixture.componentRef.setInput('analysisId', opts.analysisId ?? null);
    fixture.componentRef.setInput('revisionId', opts.analysisId ? 'rev-1' : null);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(`${BASE}/evidence/status`).flush(status(opts.messages ?? [message()]));
    http
      .expectOne(listUrl)
      .flush({ items: opts.envelopes ?? (opts.items ?? [evidence()]).map(envelope) });
    if (opts.analysisId) {
      http.expectOne(`${BASE}/analyses/${opts.analysisId}/coverage-reports`).flush({ items: [] });
    }
    await settle();
    http.match(`${BASE}/messages?limit=100`).forEach((r) =>
      r.flush({
        items: [
          {
            id: 'm-1',
            sequence: 1,
            role: 'user',
            text: TEXT,
            createdAt: '',
            authorId: null,
            authorDisplayName: null,
            agentId: null,
            chatTurnId: null,
            replyToMessageId: null,
          },
        ],
        nextAfterSequence: null,
      }),
    );
    if (opts.analysisId) {
      http
        .expectOne(`${BASE}/analyses/${opts.analysisId}/evidence-selection`)
        .flush({ code: 'selection_not_available' }, { status: 404, statusText: 'Not Found' });
    }
    await settle();
  }

  afterEach(() => {
    try {
      http.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  it('shows each message with a textual state badge and the excerpt', async () => {
    await setup({
      messages: [
        message(),
        message({
          messageId: 'm-2',
          sequence: 2,
          currentVersion: null,
          latestJob: null,
          state: 'none',
        }),
        message({
          messageId: 'm-3',
          sequence: 3,
          currentVersion: null,
          latestJob: job({ status: 'Running' }),
          state: 'running',
        }),
      ],
    });
    const badges = qa('state-badge').map((b) => b.textContent!.trim());
    expect(badges).toEqual(['Estratto', 'Non analizzato', 'In elaborazione']);
    expect(q('msg-1')!.textContent).toContain('Ciao 😀 niente notifiche push');
    expect(q('msg-1')!.textContent).toContain('versione 1 · 1 evidenza');
    expect(q('queue-text')!.textContent).toContain('Nessun lavoro');
  });

  it('shows the failed job error and retries it with a fresh Idempotency-Key, then reconciles', async () => {
    const failed = message({
      currentVersion: null,
      state: 'failed',
      latestJob: job({
        id: 'j-9',
        status: 'Failed',
        error: { code: 'model_timeout', message: null },
      }),
    });
    await setup({ messages: [failed], items: [] });
    expect(q('job-error')!.textContent).toContain('tempo previsto');

    await click('retry');
    const req = http.expectOne(`${BASE}/evidence/jobs/j-9/retry`);
    const key = req.request.headers.get('Idempotency-Key')!;
    expect(key).toMatch(/^[0-9a-f-]{36}$/);
    req.flush(job({ id: 'j-10', status: 'Queued', retryOfJobId: 'j-9' }), {
      status: 202,
      statusText: 'Accepted',
    });
    await settle();
    http.expectOne(`${BASE}/evidence/status`).flush(
      status([
        message({
          currentVersion: null,
          state: 'pending',
          latestJob: job({ id: 'j-10', status: 'Queued' }),
        }),
      ]),
    );
    await settle();
    expect(q('evidence-live')!.textContent).toContain('accodato');
    expect(q('state-badge')!.textContent).toContain('In coda');
    expect(q('retry')).toBeNull();
  });

  it('reuses the same Idempotency-Key after an uncertain outcome and creates a new one after success', async () => {
    await setup();
    await click('reanalyze');
    const first = http.expectOne(`${BASE}/messages/m-1/evidence/reanalyses`);
    const key = first.request.headers.get('Idempotency-Key')!;
    first.flush({}, { status: 503, statusText: 'Unavailable' });
    await settle();
    expect(q('evidence-command-error')!.textContent).toContain('stessa richiesta');

    await click('reanalyze');
    const second = http.expectOne(`${BASE}/messages/m-1/evidence/reanalyses`);
    expect(second.request.headers.get('Idempotency-Key')).toBe(key);
    second.flush(job({ id: 'j-2', status: 'Queued' }), { status: 202, statusText: 'Accepted' });
    await settle();
    http.expectOne(`${BASE}/evidence/status`).flush(status([message()]));
    await settle();

    await click('reanalyze');
    const third = http.expectOne(`${BASE}/messages/m-1/evidence/reanalyses`);
    expect(third.request.headers.get('Idempotency-Key')).not.toBe(key);
    third.flush(
      { code: 'extraction_active', jobId: 'j-2' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();
    expect(q('evidence-command-error')!.textContent).toContain('già in corso');
    http.expectOne(`${BASE}/evidence/status`).flush(status([message()]));
  });

  it('starts the project reanalysis and tracks the batch progress', async () => {
    await setup();
    await click('reanalyze-project');
    const req = http.expectOne(`${BASE}/evidence/reanalyses`);
    expect(req.request.headers.has('Idempotency-Key')).toBe(true);
    req.flush(
      {
        id: 'b-1',
        kind: 'Reanalysis',
        requestedAt: '2030-01-01T10:00:00Z',
        throughSequence: 1,
        jobCount: 4,
        skippedCount: 0,
        progress: { queued: 3, running: 1, completed: 0, failed: 0 },
      },
      { status: 202, statusText: 'Accepted' },
    );
    await settle();
    const running = {
      id: 'b-1',
      kind: 'Reanalysis',
      requestedAt: '',
      throughSequence: 1,
      jobCount: 4,
      skippedCount: 0,
      progress: { queued: 3, running: 1, completed: 0, failed: 0 },
    };
    http.expectOne(`${BASE}/evidence/status`).flush(status([message()]));
    http.expectOne(`${BASE}/evidence/batches/b-1`).flush(running);
    await settle();
    expect(q('batch')!.textContent).toContain('in corso');
    expect(q('batch')!.textContent).toContain('0 di 4');
    // Il giro di riconciliazione successivo (SSE o polling) rilegge il batch aperto.
    handlers.onEvent({ name: 'evidence.state', data: {} });
    await settle();
    http.expectOne(`${BASE}/evidence/status`).flush(status([message()]));
    http.expectOne(`${BASE}/evidence/batches/b-1`).flush({
      ...running,
      progress: { queued: 0, running: 0, completed: 3, failed: 1 },
    });
    await settle();
    expect(q('batch')!.textContent).toContain('terminato con errori');
  });

  it('polls every interval while a job is open and stops after the view is destroyed', async () => {
    await setup({
      pollMs: 15,
      messages: [message({ state: 'running', latestJob: job({ status: 'Running' }) })],
    });
    await new Promise((r) => setTimeout(r, 40));
    const first = http.match(`${BASE}/evidence/status`);
    expect(first.length).toBeGreaterThanOrEqual(1);
    first.forEach((r) =>
      r.flush(status([message({ state: 'running', latestJob: job({ status: 'Running' }) })])),
    );
    await settle();

    fixture.destroy();
    await new Promise((r) => setTimeout(r, 40));
    expect(http.match(`${BASE}/evidence/status`)).toHaveLength(0);
    expect(closeStream).toHaveBeenCalled();
  });

  it('does not poll when everything is settled', async () => {
    await setup({ pollMs: 15 });
    await new Promise((r) => setTimeout(r, 40));
    expect(http.match(`${BASE}/evidence/status`)).toHaveLength(0);
  });

  it('lists the evidence with its structure and qualifiers, and filters through the API', async () => {
    await setup();
    expect(q('ev-kind')!.textContent).toContain('Vincolo');
    expect(q('ev-polarity')!.textContent).toContain('Vietata');
    expect(q('ev-certainty')!.textContent).toContain('Dichiarata dal PM');
    expect(q('ev-statement')!.textContent).toContain('Nessuna notifica push');
    expect(el.querySelector('.ev-fields')!.textContent).toContain('Canale');
    expect(el.querySelector('.ev-fields')!.textContent).not.toContain('Soggetto');
    expect(q('ev-qualifier')!.textContent).toContain('Esclusività: solo email');

    const select = el.querySelector<HTMLSelectElement>('#ev-f-kind')!;
    select.value = 'Goal';
    select.dispatchEvent(new Event('change'));
    await settle();
    const req = http.expectOne(
      (r) => r.url === `${BASE}/evidence` && r.params.get('kind') === 'Goal',
    );
    req.flush({ items: [] });
    await settle();
    expect(q('no-items')!.textContent).toContain('filtri');
  });

  it('shows the original message with the cited span in <mark>, after a surrogate-pair emoji', async () => {
    await setup();
    await click('ev-detail');
    const detail: EvidenceDetailView = {
      evidence: evidence(),
      evidenceViewVersion: '1.1.0',
      sourceDocuments: [],
      sourceMessages: [{ id: 'm-1', sequence: 1, role: 'user', text: TEXT }],
      relations: [
        {
          id: 'r-1',
          type: 'Qualifies',
          basis: 'Declared',
          rationale: null,
          direction: 'outgoing',
          otherEvidenceId: 'e-2',
          otherStatement: 'Solo email',
          otherMessageSequence: 1,
        },
      ],
    };
    http.expectOne(detailUrl).flush(detail);
    await settle();
    const marks = el.querySelectorAll('[data-testid=original] mark');
    expect(marks).toHaveLength(1);
    expect(marks[0].textContent).toBe('niente notifiche push');
    expect(q('original')!.textContent!.replace(/\s+/g, ' ').trim()).toBe(TEXT);
    expect(q('relations')!.textContent).toContain('qualifica');
    expect(q('relations')!.textContent).toContain('Solo email');
  });

  it('never renders message text as HTML', async () => {
    const text = '<b id="injected">x</b> vincolo';
    await setup();
    await click('ev-detail');
    http.expectOne(detailUrl).flush({
      evidence: evidence({
        sources: [
          { messageId: 'm-1', sequence: 1, role: 'Primary', start: 0, end: 6, quote: '<b id="' },
        ],
      }),
      evidenceViewVersion: '1.1.0',
      sourceDocuments: [],
      sourceMessages: [{ id: 'm-1', sequence: 1, role: 'user', text }],
      relations: [],
    });
    await settle();
    expect(el.querySelector('#injected')).toBeNull();
    expect(q('original')!.textContent).toContain('<b id="injected">');
  });

  async function openVersions() {
    await click('open-versions');
    http.expectOne(`${BASE}/messages/m-1/evidence/versions`).flush(versions());
    await settle();
  }

  it('lists versions, compares two of them and shows summary and field changes', async () => {
    await setup();
    await openVersions();
    const rows = qa('version').map((v) => v.textContent!.replace(/\s+/g, ' '));
    expect(rows[0]).toContain('Versione 2');
    expect(rows[0]).toContain('Candidata');
    expect(rows[1]).toContain('Corrente');
    expect(
      el.querySelector<HTMLSelectElement>('#ev-cmp-from')!.selectedOptions[0].textContent,
    ).toContain('Versione 1');

    await click('compare');
    http.expectOne(`${BASE}/evidence/versions/v-1/compare/v-2`).flush({
      fromVersionId: 'v-1',
      toVersionId: 'v-2',
      messageId: 'm-1',
      summary: { added: 1, removed: 0, unchanged: 0, modified: 1, ambiguous: 1 },
      entries: [
        {
          status: 'Modified',
          from: evidence(),
          to: evidence({ statement: 'Nessuna push' }),
          changes: [{ field: 'channel', from: 'push', to: null }],
          candidates: [],
          overlap: 1,
        },
        {
          status: 'Ambiguous',
          from: evidence({ id: 'e-3' }),
          to: null,
          changes: [],
          candidates: ['e-4'],
          overlap: 0.6,
        },
        {
          status: 'Added',
          from: null,
          to: evidence({ id: 'e-4' }),
          changes: [],
          candidates: [],
          overlap: 0,
        },
      ],
    });
    await settle();
    expect(q('diff-summary')!.textContent!.replace(/\s+/g, ' ')).toContain(
      '1 aggiunte · 0 rimosse · 0 invariate · 1 modificate · 1 ambigue',
    );
    expect(qa('diff-status').map((s) => s.textContent!.trim())).toEqual([
      'Modificata',
      'Ambigua',
      'Aggiunta',
    ]);
    expect(q('diff-changes')!.textContent).toContain('Canale');
  });

  it('promotes a candidate with expectedStateRevision and reason, then reloads', async () => {
    await setup();
    await openVersions();
    await click('promote');
    const reason = q('promote-reason') as HTMLInputElement;
    reason.value = 'più completa';
    reason.dispatchEvent(new Event('input'));
    await click('promote-confirm');
    const req = http.expectOne(`${BASE}/messages/m-1/evidence/promotions`);
    expect(req.request.body).toEqual({
      versionId: 'v-2',
      expectedStateRevision: 5,
      reason: 'più completa',
    });
    req.flush({
      messageId: 'm-1',
      stateRevision: 6,
      currentVersionId: 'v-2',
      promotion: {
        id: 'pr',
        fromVersionId: 'v-1',
        toVersionId: 'v-2',
        promotedBy: 'pm',
        promotedAt: '2030-01-02T10:00:00Z',
        reason: 'più completa',
        relationImpact: { remapped: 2, stale: 1 },
      },
    });
    await settle();
    http
      .expectOne(`${BASE}/messages/m-1/evidence/versions`)
      .flush(versions({ stateRevision: 6, currentVersionId: 'v-2' }));
    http.expectOne(`${BASE}/evidence/status`).flush(status([message()]));
    http.expectOne(listUrl).flush({ items: [envelope(evidence())] });
    await settle();
    expect(q('evidence-live')!.textContent).toContain('Versione 2 promossa');
    expect(q('evidence-live')!.textContent).toContain('2 rimappate');
    expect(q('evidence-live')!.textContent).toContain('1 non più risolvibili');
  });

  it('shows "lo stato è cambiato, ricarica" on promotion_conflict and reloads versions, status and evidence', async () => {
    await setup();
    await openVersions();
    await click('promote');
    await click('promote-confirm');
    http
      .expectOne(`${BASE}/messages/m-1/evidence/promotions`)
      .flush(
        { code: 'promotion_conflict', currentVersionId: 'v-3', stateRevision: 7 },
        { status: 409, statusText: 'Conflict' },
      );
    await settle();
    expect(q('evidence-command-error')!.textContent!.toLowerCase()).toContain(
      'lo stato è cambiato, ricarica',
    );
    http.expectOne(`${BASE}/messages/m-1/evidence/versions`).flush(versions({ stateRevision: 7 }));
    http.expectOne(`${BASE}/evidence/status`).flush(status([message()]));
    http.expectOne(listUrl).flush({ items: [envelope(evidence())] });
    await settle();
    // Dopo il ricaricamento la richiesta di conferma è chiusa: nessuna promozione parte da sola.
    expect(q('promote-confirm')).toBeNull();
  });

  it('disables coverage without an analysis and explains why', async () => {
    await setup();
    expect(q('no-analysis')).not.toBeNull();
    expect((q('verify-coverage') as HTMLButtonElement).disabled).toBe(true);
  });

  it('computes the coverage report and groups findings by category with confidence and limits', async () => {
    await setup({ analysisId: 'a-1' });
    await click('verify-coverage');
    http
      .expectOne(`${BASE}/draft`)
      .flush({ code: 'draft_not_available' }, { status: 404, statusText: 'Not Found' });
    await settle();
    const create = http.expectOne(`${BASE}/analyses/a-1/coverage-reports`);
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ revisionId: 'rev-1' });
    const report: CoverageReportView = {
      id: 'c-1',
      analysisId: 'a-1',
      revisionId: 'rev-1',
      draftId: null,
      checkerVersion: 'coverage-v1',
      createdAt: '2030-01-03T10:00:00Z',
      counts: { deterministicErrors: 1, semanticRisks: 1, pmAssessments: 1 },
      inputHash: 'h',
      createdBy: 'pm',
      findings: [
        {
          category: 'PmAssessment',
          code: 'x',
          message: 'Decisione del PM',
          evidenceId: null,
          alias: null,
          itemId: null,
          section: null,
          confidence: null,
          details: null,
        },
        {
          category: 'SemanticRisk',
          code: 'y',
          message: 'Possibile qualificatore perso',
          evidenceId: null,
          alias: 'E3',
          itemId: null,
          section: 'requirements',
          confidence: 'low',
          details: null,
        },
        {
          category: 'DeterministicError',
          code: 'z',
          message: 'Fonte non valida',
          evidenceId: null,
          alias: null,
          itemId: null,
          section: null,
          confidence: null,
          details: null,
        },
      ],
      limits: ['Controllo lessicale, non semantico.'],
    };
    create.flush(report, { status: 201, statusText: 'Created' });
    await settle();
    http.expectOne(`${BASE}/analyses/a-1/coverage-reports`).flush({
      items: [
        {
          id: 'c-1',
          analysisId: 'a-1',
          revisionId: 'rev-1',
          draftId: null,
          checkerVersion: 'coverage-v1',
          createdAt: '2030-01-03T10:00:00Z',
          counts: report.counts,
        },
      ],
    });
    await settle();

    const text = (id: string) => q(id)!.textContent!.replace(/\s+/g, ' ');
    expect(text('group-DeterministicError')).toContain('Errore deterministico (1)');
    expect(text('group-DeterministicError')).toContain('Fonte non valida');
    expect(text('group-SemanticRisk')).toContain('Rischio stimato (1)');
    expect(text('group-SemanticRisk')).toContain('Confidenza bassa');
    expect(text('group-PmAssessment')).toContain('Valutazione PM (1)');
    expect(q('limits')!.textContent).toContain('Controllo lessicale');
    expect(q('reports')!.textContent).toContain('1 errori, 1 rischi, 1 da valutare');
  });

  it('never presents an empty semantic group as a confirmation of meaning', async () => {
    await setup({ analysisId: 'a-1' });
    await click('verify-coverage');
    http
      .expectOne(`${BASE}/draft`)
      .flush({ code: 'draft_not_available' }, { status: 404, statusText: 'Not Found' });
    await settle();
    http.expectOne(`${BASE}/analyses/a-1/coverage-reports`).flush(
      {
        id: 'c-2',
        analysisId: 'a-1',
        revisionId: 'rev-1',
        draftId: null,
        checkerVersion: 'coverage-v1',
        createdAt: '2030-01-03T10:00:00Z',
        counts: { deterministicErrors: 0, semanticRisks: 0, pmAssessments: 0 },
        inputHash: 'h',
        createdBy: 'pm',
        findings: [],
        limits: ['Nessun modello linguistico valuta la copertura.'],
      },
      { status: 201, statusText: 'Created' },
    );
    await settle();
    http.expectOne(`${BASE}/analyses/a-1/coverage-reports`).flush({ items: [] });
    await settle();
    const semantic = q('empty-SemanticRisk')!.textContent!.replace(/\s+/g, ' ');
    expect(semantic).toContain('euristiche lessicali');
    expect(semantic).toContain('Non è una conferma');
    expect(q('empty-DeterministicError')!.textContent).toContain('Nessuna segnalazione');
  });

  it('includes the draft only when it belongs to the current revision', async () => {
    await setup({ analysisId: 'a-1' });
    await click('verify-coverage');
    http
      .expectOne(`${BASE}/draft`)
      .flush({ id: 'd-1', analysisId: 'a-1', analysisRevisionId: 'rev-1' });
    await settle();
    const create = http.expectOne(`${BASE}/analyses/a-1/coverage-reports`);
    expect(create.request.body).toEqual({ revisionId: 'rev-1', draftId: 'd-1' });
    create.flush({ code: 'selection_not_available' }, { status: 409, statusText: 'Conflict' });
    await settle();
    expect(q('coverage-error')!.textContent).toContain('selezione di evidenze');
  });

  describe('evidence from documents (envelope 1.1.0)', () => {
    const src = (over: Record<string, unknown> = {}) => ({
      sourceId: 's-1',
      kind: 'Document',
      role: 'Primary',
      citationGroupId: 'g-1',
      part: 0,
      documentId: 'd-1',
      documentVersionId: 'dv-1',
      preparationId: 'prep-1',
      artifactId: 'a-1',
      blockId: 'b-1',
      start: 4,
      end: 12,
      quote: 'Requisito',
      ...over,
    });
    const docEvidence = (sources = [src()]) => ({
      id: 'e-doc',
      versionId: 'ev-1',
      version: 1,
      isCurrent: true,
      projectId: P,
      ordinal: 0,
      key: 'k-doc',
      kind: 'Requirement',
      polarity: 'Affirmed',
      certainty: 'Inferred',
      statement: 'Il sistema esporta in CSV',
      origin: {
        kind: 'Document',
        documentId: 'd-1',
        documentVersionId: 'dv-1',
        preparationId: 'prep-1',
      },
      content: {
        schemaVersion: '1.1.0',
        kindRationale: 'Obbligo esplicito',
        structure: {
          subject: 'sistema',
          action: 'esporta',
          object: null,
          scope: null,
          purpose: null,
          instrument: null,
          channel: null,
          recipient: null,
        },
        qualifiers: [],
        sources,
      },
    });
    const resolved = (sourceId: string, blockId: string, page: number | null) => ({
      sourceId,
      documentId: 'd-1',
      documentVersionId: 'dv-1',
      documentVersion: 2,
      originalName: 'spec.pdf',
      preparationId: 'prep-1',
      generation: 1,
      artifactId: 'a-1',
      blockId,
      blockRef: 'p1',
      locator: { page, headingPath: ['Export'], bbox: null, sourceLines: null, doclingRef: null },
      passageUrl: '',
      downloadUrl: '',
    });
    const mixed = () => [
      { evidenceViewVersion: '1.0.0', evidence: evidence() },
      { evidenceViewVersion: '1.1.0', evidence: docEvidence() },
    ];

    it('lists messages and documents together, each with its own origin and no invented message', async () => {
      await setup({ envelopes: mixed() });
      expect(qa('ev-origin').map((b) => b.textContent!.trim())).toEqual(['Messaggio', 'Documento']);
      const docRow = q('evidence-e-doc')!;
      expect(docRow.textContent).toContain('Il sistema esporta in CSV');
      expect(docRow.textContent).toContain('Soggetto');
      expect(docRow.textContent).not.toContain('Messaggio #');
      expect(q('evidence-e-1')!.textContent).toContain('Messaggio #1');
    });

    it('filters by origin through originKind', async () => {
      await setup({ envelopes: mixed() });
      const select = el.querySelector<HTMLSelectElement>('#ev-f-origin')!;
      select.value = 'Document';
      select.dispatchEvent(new Event('change'));
      await settle();
      http
        .expectOne((r) => listUrl(r) && true)
        .flush({ items: [{ evidenceViewVersion: '1.1.0', evidence: docEvidence() }] });
      await settle();
      expect(qa('ev-origin')).toHaveLength(1);
    });

    it('declares unknown envelope versions instead of rendering them deformed', async () => {
      await setup({
        envelopes: [...mixed(), { evidenceViewVersion: '2.0.0', evidence: { id: 'x' } }],
      });
      expect(qa('ev-origin')).toHaveLength(2);
      expect(q('items-unsupported')!.textContent).toContain('1 elemento');
    });

    it('keeps the parts of a multi-block quote separate and opens every exact range', async () => {
      await setup({
        envelopes: [{ evidenceViewVersion: '1.1.0', evidence: docEvidence() }],
      });
      await click('ev-detail');
      const parts = [
        src({ sourceId: 's-2', part: 1, blockId: 'b-2', start: 0, end: 5, quote: 'secondo' }),
        src({ sourceId: 's-1', part: 0, blockId: 'b-1', start: 4, end: 12, quote: 'primo' }),
      ];
      http.expectOne(detailUrl_doc).flush({
        evidenceViewVersion: '1.1.0',
        evidence: docEvidence(parts),
        relations: [],
        sourceDocuments: [resolved('s-1', 'b-1', 3), resolved('s-2', 'b-2', 4)],
        sourceMessages: [],
      });
      await settle();
      expect(qa('source-group')).toHaveLength(1);
      const shown = qa('source-part').map((p) => p.textContent!.replace(/\s+/g, ' ').trim());
      expect(shown[0]).toContain('«primo»');
      expect(shown[0]).toContain('pagina 3');
      expect(shown[1]).toContain('«secondo»');
      expect(shown[1]).toContain('pagina 4');
      expect(el.textContent).toContain('Obbligo esplicito');

      await click('open-group');
      const prep = http.match((r) => r.url.endsWith('/preparations/prep-1'));
      expect(prep).toHaveLength(2);
      prep.forEach((r) =>
        r.flush({
          id: 'prep-1',
          processingStatus: 'Ready',
          isReady: true,
          issues: [],
          blockCount: 2,
        }),
      );
      await settle();
      const passages = http.match((r) => r.url.includes('/preparations/prep-1/passages/'));
      expect(
        passages.map(
          (r) =>
            `${r.request.url.split('/').pop()}:${r.request.params.get('start')}-${r.request.params.get('end')}`,
        ),
      ).toEqual(['b-1:4-12', 'b-2:0-5']);
      expect(http.match((r) => r.url.endsWith('/blocks'))).toHaveLength(0);
      passages.forEach((r) => r.flush({ code: 'x' }, { status: 404, statusText: 'Not Found' }));
      await settle();
    });

    it('does not offer to open a part whose block cannot be resolved by the server', async () => {
      await setup({ envelopes: [{ evidenceViewVersion: '1.1.0', evidence: docEvidence() }] });
      await click('ev-detail');
      http.expectOne(detailUrl_doc).flush({
        evidenceViewVersion: '1.1.0',
        evidence: docEvidence(),
        relations: [
          {
            id: 'r',
            type: 'Confirms',
            basis: 'Inferred',
            rationale: null,
            direction: 'outgoing',
            otherEvidenceId: 'e-9',
            otherStatement: 'Altro',
          },
        ],
        sourceDocuments: [],
        sourceMessages: [],
      });
      await settle();
      expect(q('source-locator')!.textContent).toContain('Posizione non risolvibile');
      expect(q('open-part')!.hasAttribute('disabled')).toBe(true);
      expect(q('relations')!.textContent).toContain('Altro');
      expect(q('relations')!.textContent).not.toContain('messaggio #');
    });
  });
});
