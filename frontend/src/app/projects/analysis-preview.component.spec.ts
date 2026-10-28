import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { ANALYSIS_POLL_MS, AnalysisPreviewComponent } from './analysis-preview.component';
import { AnalysisView, ProcessingRunView, ProjectView } from './project-api.service';
import { ProjectEventsService, ProjectStreamHandlers } from './project-events.service';

const P = 'p-1';
const BASE = `/api/projects/${P}`;

function project(over: Partial<ProjectView> = {}): ProjectView {
  return {
    id: P,
    conversationId: 'c-1',
    name: 'Aurora',
    description: null,
    createdAt: '2030-01-01T10:00:00Z',
    createdBy: 'pm',
    activeProcessingRunId: null,
    latestAnalysisRunId: null,
    currentAnalysisId: null,
    currentRevisionId: null,
    validationState: null,
    ...over,
  };
}

function run(over: Partial<ProcessingRunView> = {}): ProcessingRunView {
  return {
    id: 'r-1',
    kind: 'Analysis',
    analysisId: null,
    sourceCutoffSequence: 1,
    status: 'Queued',
    requestedAt: '2030-01-01T10:00:00Z',
    startedAt: null,
    completedAt: null,
    deadlineAt: '2030-01-01T10:03:00Z',
    attemptCount: 0,
    maxAttempts: 3,
    nextAttemptAt: null,
    lastObservedStage: null,
    resultRevisionId: null,
    draftId: null,
    error: null,
    ...over,
  };
}

function analysis(over: Partial<AnalysisView> = {}): AnalysisView {
  return {
    analysisId: 'r-1',
    projectId: P,
    currentRevisionId: 'rev-1',
    version: 1,
    schemaVersion: '1.0.0',
    content: {
      schemaVersion: '1.0.0',
      analysisId: 'r-1',
      projectId: P,
      sourceCutoffSequence: 1,
      validationState: 'Unvalidated',
      sources: [{ id: 's-1', kind: 'Message', messageId: 'm-1', messageRole: 'user', revisionId: null }],
      goals: [{ id: 'g-1', text: 'Realizzare un portale clienti', classification: 'Declared', evidenceRefs: ['s-1'] }],
      contextAndScope: { overview: [], included: [], excluded: [] },
      requirements: [{ id: 'q-1', text: 'Login con email', classification: 'Inferred', evidenceRefs: ['s-1'] }],
      operationalFlows: [],
      constraints: [],
      dependencies: [],
      assumptions: [],
      openQuestions: [{ id: 'o-1', text: 'Quanti utenti?', classification: 'Assumed', evidenceRefs: [] }],
      conflicts: [],
    },
    validationState: 'Unvalidated',
    origin: 'Ai',
    createdBy: null,
    createdAt: '2030-01-01T10:01:00Z',
    confirmedBy: null,
    confirmedAt: null,
    sourceCutoffSequence: 1,
    hasNewNotes: false,
    canEdit: true,
    canConfirm: true,
    canGenerate: false,
    ...over,
  };
}

describe('AnalysisPreviewComponent', () => {
  let fixture: ComponentFixture<AnalysisPreviewComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let handlers: ProjectStreamHandlers;
  const open = vi.fn();
  const close = vi.fn();

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  const text = () => el.textContent ?? '';
  const button = () => el.querySelector<HTMLButtonElement>('[data-testid=analyze]')!;

  /** Apre il componente rispondendo alla ProjectView (e, se indicati, a run e anteprima). */
  async function setup(opts: { project?: ProjectView; run?: ProcessingRunView; analysis?: AnalysisView; pollMs?: number } = {}) {
    open.mockReset();
    close.mockReset();
    open.mockImplementation((_id: string, h: ProjectStreamHandlers) => {
      handlers = h;
      return close;
    });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ANALYSIS_POLL_MS, useValue: opts.pollMs ?? 0 },
        { provide: ProjectEventsService, useValue: { open } },
        { provide: AuthService, useValue: { isAuthenticated: signal(true).asReadonly() } },
      ],
    });
    fixture = TestBed.createComponent(AnalysisPreviewComponent);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement;
    fixture.componentRef.setInput('projectId', P);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(BASE).flush(opts.project ?? project());
    await settle();
    if (opts.analysis) {
      http.expectOne(`${BASE}/analyses/${opts.analysis.analysisId}`).flush(opts.analysis);
      await settle();
    }
    if (opts.run) {
      http.expectOne(`${BASE}/processing-runs/${opts.run.id}`).flush(opts.run);
      await settle();
    }
  }

  const click = async () => {
    button().click();
    await Promise.resolve();
    await settle();
  };

  afterEach(() => {
    fixture.destroy();
    http.verify();
  });

  it('con progetto senza analisi mostra lo stato vuoto e nessuna anteprima', async () => {
    await setup();
    expect(el.querySelector('[data-testid=empty]')).not.toBeNull();
    expect(el.querySelector('[data-testid=preview]')).toBeNull();
    expect(button().disabled).toBe(false);
  });

  it('Analizza invia POST con Idempotency-Key UUID, segue processing.state e mostra l\'anteprima non validata', async () => {
    await setup();
    await click();
    const post = http.expectOne((r) => r.method === 'POST' && r.url === `${BASE}/analyses`);
    const key = post.request.headers.get('Idempotency-Key')!;
    expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    post.flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    expect(el.querySelector('[data-testid=run-state]')?.textContent).toContain('In coda');
    expect(button().disabled).toBe(true);
    expect(el.querySelector('[data-testid=preview]')).toBeNull();

    handlers.onEvent({ name: 'processing.state', data: run({ status: 'Running', attemptCount: 1, lastObservedStage: 'LoadDialog' }) });
    await settle();
    expect(el.querySelector('[data-testid=run-state]')?.textContent).toContain('lettura del dialogo');

    handlers.onEvent({ name: 'processing.state', data: run({ status: 'Completed', analysisId: 'r-1', resultRevisionId: 'rev-1' }) });
    await settle();
    http.expectOne(`${BASE}/analyses/r-1`).flush(analysis());
    await settle();

    expect(el.querySelector('[data-testid=run-state]')).toBeNull();
    const badge = el.querySelector<HTMLElement>('[data-testid=validation-badge]')!;
    expect(badge.hidden).toBe(false);
    expect(badge.textContent).toContain('Non validata / in attesa del PM');
    expect(text()).toContain('Realizzare un portale clienti');
    expect(text()).toContain('Login con email');
    expect(text()).toContain('Quanti utenti?');
    expect(text()).toContain('Punti aperti');
    expect(text()).toContain('F1 · messaggio del PM');
    // Solo lettura e nessun Markdown/documento: nessun campo modificabile né azioni di generazione.
    expect(el.querySelectorAll('textarea, input').length).toBe(0);
    expect(text().toLowerCase()).not.toContain('markdown');
    expect(el.querySelector('.analysis')!.innerHTML).not.toContain('<pre');
  });

  it('un evento di un altro run non cambia lo stato', async () => {
    await setup();
    await click();
    http.expectOne((r) => r.method === 'POST').flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    handlers.onEvent({ name: 'processing.state', data: run({ id: 'altro', status: 'Failed' }) });
    handlers.onEvent({ name: 'processing.state', data: run({ kind: 'Document', status: 'Failed' }) });
    await settle();
    expect(el.querySelector('[data-testid=run-state]')).not.toBeNull();
    expect(el.querySelector('[data-testid=run-error]')).toBeNull();
  });

  it('senza SSE il polling riconcilia il run fino al completamento', async () => {
    await setup({ pollMs: 5 });
    await click();
    http.expectOne((r) => r.method === 'POST').flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    await new Promise((r) => setTimeout(r, 15));
    http.expectOne(`${BASE}/processing-runs/r-1`).flush(run({ status: 'Completed', analysisId: 'r-1' }));
    await settle();
    http.expectOne(`${BASE}/analyses/r-1`).flush(analysis());
    await settle();
    expect(el.querySelector('[data-testid=preview]')).not.toBeNull();
    expect(button().disabled).toBe(false);
  });

  it('no_pm_input: messaggio chiaro, nessun run e nessuna anteprima', async () => {
    await setup();
    await click();
    http
      .expectOne((r) => r.method === 'POST')
      .flush({ code: 'no_pm_input', title: 'Serve almeno un messaggio del project manager' }, { status: 422, statusText: 'Unprocessable' });
    await settle();
    expect(el.querySelector('[data-testid=command-error]')?.textContent).toContain('almeno un messaggio nella chat');
    expect(el.querySelector('[data-testid=run-state]')).toBeNull();
    expect(el.querySelector('[data-testid=preview]')).toBeNull();
  });

  it('processing_active: avvisa e si aggancia al run attivo indicato dal server', async () => {
    await setup();
    await click();
    http
      .expectOne((r) => r.method === 'POST')
      .flush({ code: 'processing_active', processingRunId: 'r-9' }, { status: 409, statusText: 'Conflict' });
    await settle();
    http.expectOne(`${BASE}/processing-runs/r-9`).flush(run({ id: 'r-9', status: 'Running', attemptCount: 1 }));
    await settle();
    expect(el.querySelector('[data-testid=command-error]')?.textContent).toContain('già in corso');
    expect(el.querySelector('[data-testid=run-state]')).not.toBeNull();
    expect(button().disabled).toBe(true);
  });

  it('esito incerto: la ripetizione riusa la stessa Idempotency-Key; dopo il successo una nuova richiesta ne usa un\'altra', async () => {
    await setup();
    await click();
    const first = http.expectOne((r) => r.method === 'POST');
    const key = first.request.headers.get('Idempotency-Key');
    first.error(new ProgressEvent('error'), { status: 0 });
    await settle();
    expect(el.querySelector('[data-testid=command-error]')?.textContent).toContain('Non sappiamo se la richiesta');
    expect(button().disabled).toBe(false);

    await click();
    const retry = http.expectOne((r) => r.method === 'POST');
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush(run({ status: 'Completed', analysisId: 'r-1' }), { status: 200, statusText: 'OK' });
    await settle();
    http.expectOne(`${BASE}/analyses/r-1`).flush(analysis());
    await settle();

    await click();
    const again = http.expectOne((r) => r.method === 'POST');
    expect(again.request.headers.get('Idempotency-Key')).not.toBe(key);
    again.flush(run({ id: 'r-2' }), { status: 202, statusText: 'Accepted' });
    await settle();
  });

  it('run fallito: errore chiaro e nessuna anteprima di successo', async () => {
    await setup();
    await click();
    http.expectOne((r) => r.method === 'POST').flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    handlers.onEvent({
      name: 'processing.state',
      data: run({ status: 'Failed', error: { code: 'invalid_agent_output', message: null } }),
    });
    await settle();
    expect(el.querySelector('[data-testid=run-error]')?.textContent).toContain('non era un\'analisi valida');
    expect(el.querySelector('[data-testid=preview]')).toBeNull();
    expect(el.querySelector('[data-testid=empty]')).toBeNull();
    expect(button().disabled).toBe(false); // nuovo comando esplicito possibile
  });

  it('run fallito con analisi precedente: l\'ultima anteprima buona resta visibile insieme all\'errore', async () => {
    await setup({
      project: project({ latestAnalysisRunId: 'r-2', currentAnalysisId: 'r-1', currentRevisionId: 'rev-1', validationState: 'Unvalidated' }),
      analysis: analysis(),
      run: run({ id: 'r-2', status: 'Failed', error: { code: 'deadline_exceeded', message: null } }),
    });
    expect(el.querySelector('[data-testid=preview]')).not.toBeNull();
    expect(text()).toContain('Realizzare un portale clienti');
    expect(el.querySelector('[data-testid=run-error]')?.textContent).toContain('tempo massimo');
    expect(el.querySelector('[data-testid=run-error]')?.textContent).toContain('ultima anteprima valida');
  });

  it('alla riapertura con un run attivo mostra lo stato e blocca Analizza senza inviare comandi', async () => {
    await setup({
      project: project({ activeProcessingRunId: 'r-1', latestAnalysisRunId: 'r-1' }),
      run: run({ status: 'Running', attemptCount: 1, lastObservedStage: 'ValidatePersistAnalysis' }),
    });
    expect(el.querySelector('[data-testid=run-state]')?.textContent).toContain('validazione e salvataggio');
    expect(button().disabled).toBe(true);
  });

  it('segnala le nuove note PM successive all\'analisi', async () => {
    await setup({
      project: project({ latestAnalysisRunId: 'r-1', currentAnalysisId: 'r-1', currentRevisionId: 'rev-1' }),
      analysis: analysis({ hasNewNotes: true }),
    });
    expect(el.querySelector('[data-testid=new-notes]')).not.toBeNull();
    expect(button().textContent).toContain('Analizza di nuovo');
  });

  it('chiude lo stream alla distruzione', async () => {
    await setup();
    fixture.destroy();
    expect(close).toHaveBeenCalled();
  });
});
