import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { ANALYSIS_POLL_MS, AnalysisPreviewComponent } from './analysis-preview.component';
import { AnalysisView, ProcessingRunView, ProjectView } from './project-api.service';
import { measured, metrics } from './context-usage.fixtures';
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
      sources: [
        { id: 's-1', kind: 'Message', messageId: 'm-1', messageRole: 'user', revisionId: null },
      ],
      goals: [
        {
          id: 'g-1',
          text: 'Realizzare un portale clienti',
          classification: 'Declared',
          evidenceRefs: ['s-1'],
        },
      ],
      contextAndScope: { overview: [], included: [], excluded: [] },
      requirements: [
        { id: 'q-1', text: 'Login con email', classification: 'Inferred', evidenceRefs: ['s-1'] },
      ],
      operationalFlows: [],
      constraints: [],
      dependencies: [],
      assumptions: [],
      openQuestions: [
        { id: 'o-1', text: 'Quanti utenti?', classification: 'Assumed', evidenceRefs: [] },
      ],
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
  /** I testi del form sono valori di textarea, non textContent. */
  const values = () =>
    Array.from(el.querySelectorAll('textarea'))
      .map((t) => t.value)
      .join(' | ');
  const button = () => el.querySelector<HTMLButtonElement>('[data-testid=analyze]')!;

  /** Apre il componente rispondendo alla ProjectView (e, se indicati, a run e anteprima). */
  async function setup(
    opts: {
      project?: ProjectView;
      run?: ProcessingRunView;
      analysis?: AnalysisView;
      pollMs?: number;
    } = {},
  ) {
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

  it("Analizza invia POST con Idempotency-Key UUID, segue processing.state e mostra l'anteprima non validata", async () => {
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

    handlers.onEvent({
      name: 'processing.state',
      data: run({ status: 'Running', attemptCount: 1, lastObservedStage: 'LoadDialog' }),
    });
    await settle();
    expect(el.querySelector('[data-testid=run-state]')?.textContent).toContain(
      'lettura del dialogo',
    );

    handlers.onEvent({
      name: 'processing.state',
      data: run({ status: 'Completed', analysisId: 'r-1', resultRevisionId: 'rev-1' }),
    });
    await settle();
    http.expectOne(`${BASE}/analyses/r-1`).flush(analysis());
    await settle();

    expect(el.querySelector('[data-testid=run-state]')).toBeNull();
    const badge = el.querySelector<HTMLElement>('[data-testid=validation-badge]')!;
    expect(badge.hidden).toBe(false);
    expect(badge.textContent).toContain('Non validata / in attesa del PM');
    expect(values()).toContain('Realizzare un portale clienti');
    expect(values()).toContain('Login con email');
    expect(values()).toContain('Quanti utenti?');
    expect(text()).toContain('Punti aperti');
    expect(text()).toContain('F1 · messaggio del PM');
    // Nessun Markdown/documento: nessuna azione di generazione sulla revisione non confermata.
    expect(el.querySelector('[data-testid=generate]')).toBeNull();
    expect(text().toLowerCase()).not.toContain('markdown');
    expect(el.querySelector('.analysis')!.innerHTML).not.toContain('<pre');
  });

  it('un evento di un altro run non cambia lo stato', async () => {
    await setup();
    await click();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    handlers.onEvent({ name: 'processing.state', data: run({ id: 'altro', status: 'Failed' }) });
    handlers.onEvent({
      name: 'processing.state',
      data: run({ kind: 'Document', status: 'Failed' }),
    });
    await settle();
    expect(el.querySelector('[data-testid=run-state]')).not.toBeNull();
    expect(el.querySelector('[data-testid=run-error]')).toBeNull();
  });

  it('senza SSE il polling riconcilia il run fino al completamento', async () => {
    await setup({ pollMs: 5 });
    await click();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    await new Promise((r) => setTimeout(r, 15));
    http
      .expectOne(`${BASE}/processing-runs/r-1`)
      .flush(run({ status: 'Completed', analysisId: 'r-1' }));
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
      .flush(
        { code: 'no_pm_input', title: 'Serve almeno un messaggio del project manager' },
        { status: 422, statusText: 'Unprocessable' },
      );
    await settle();
    expect(el.querySelector('[data-testid=command-error]')?.textContent).toContain(
      'almeno un messaggio nella chat',
    );
    expect(el.querySelector('[data-testid=run-state]')).toBeNull();
    expect(el.querySelector('[data-testid=preview]')).toBeNull();
  });

  it('processing_active: avvisa e si aggancia al run attivo indicato dal server', async () => {
    await setup();
    await click();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(
        { code: 'processing_active', processingRunId: 'r-9' },
        { status: 409, statusText: 'Conflict' },
      );
    await settle();
    http
      .expectOne(`${BASE}/processing-runs/r-9`)
      .flush(run({ id: 'r-9', status: 'Running', attemptCount: 1 }));
    await settle();
    expect(el.querySelector('[data-testid=command-error]')?.textContent).toContain('già in corso');
    expect(el.querySelector('[data-testid=run-state]')).not.toBeNull();
    expect(button().disabled).toBe(true);
  });

  it("esito incerto: la ripetizione riusa la stessa Idempotency-Key; dopo il successo una nuova richiesta ne usa un'altra", async () => {
    await setup();
    await click();
    const first = http.expectOne((r) => r.method === 'POST');
    const key = first.request.headers.get('Idempotency-Key');
    first.error(new ProgressEvent('error'), { status: 0 });
    await settle();
    expect(el.querySelector('[data-testid=command-error]')?.textContent).toContain(
      'Non sappiamo se la richiesta',
    );
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
    http
      .expectOne((r) => r.method === 'POST')
      .flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    handlers.onEvent({
      name: 'processing.state',
      data: run({ status: 'Failed', error: { code: 'invalid_agent_output', message: null } }),
    });
    await settle();
    expect(el.querySelector('[data-testid=run-error]')?.textContent).toContain(
      "non era un'analisi valida",
    );
    expect(el.querySelector('[data-testid=preview]')).toBeNull();
    expect(el.querySelector('[data-testid=empty]')).toBeNull();
    expect(button().disabled).toBe(false); // nuovo comando esplicito possibile
  });

  it('context_overflow: spiegazione, valori misurati solo se presenti e azione praticabile', async () => {
    await setup();
    await click();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    handlers.onEvent({
      name: 'processing.state',
      data: run({
        status: 'Failed',
        error: {
          code: 'context_overflow',
          message:
            'Contesto di 31000 token oltre il budget di input di 24000 token, non riducibile con la compattazione nativa. Gli originali non sono stati modificati.',
        },
      }),
    });
    await settle();
    const text = el.querySelector('[data-testid=run-error]')?.textContent ?? '';
    expect(text).toContain('supera il contesto gestibile');
    expect(text).toContain('31000 token');
    expect(text).toContain('24000 token');
    expect(text).toContain('messaggio più recente');
    expect(text).not.toContain('rifiutato la richiesta');
    expect(text).not.toContain('%');
    expect(el.querySelector('[data-testid=preview]')).toBeNull();
  });

  describe.each([
    [
      'context_window_mismatch',
      'finestra di contesto del modello è inferiore a quella configurata',
    ],
    ['compaction_failed', 'Non è stato possibile condensare la conversazione'],
  ])('%s', (code, expected) => {
    it('ha un testo in italiano senza cifre oltre a quelle del messaggio server', async () => {
      await setup();
      await click();
      http
        .expectOne((r) => r.method === 'POST')
        .flush(run(), { status: 202, statusText: 'Accepted' });
      await settle();
      handlers.onEvent({
        name: 'processing.state',
        data: run({
          status: 'Failed',
          error: {
            code,
            message: 'La finestra (16384 token) è inferiore a quella configurata (32768 token).',
          },
        }),
      });
      await settle();
      const text = el.querySelector('[data-testid=run-error]')?.textContent ?? '';
      expect(text).toContain(expected);
      expect(text).not.toMatch(/[0-9]{3,}/);
    });
  });

  it('context_overflow senza valori nei dati: nessun numero inventato', async () => {
    await setup();
    await click();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    handlers.onEvent({
      name: 'processing.state',
      data: run({
        status: 'Failed',
        error: {
          code: 'context_overflow',
          message: 'Il modello interno ha rifiutato la richiesta.',
        },
      }),
    });
    await settle();
    const text = el.querySelector('[data-testid=run-error]')?.textContent ?? '';
    expect(text).toContain('messaggio più recente');
    expect(text).not.toContain('rifiutato la richiesta');
    expect(text.replace('Analisi non riuscita.', '')).not.toMatch(/\d/);
  });

  it("run fallito con analisi precedente: l'ultima anteprima buona resta visibile insieme all'errore", async () => {
    await setup({
      project: project({
        latestAnalysisRunId: 'r-2',
        currentAnalysisId: 'r-1',
        currentRevisionId: 'rev-1',
        validationState: 'Unvalidated',
      }),
      analysis: analysis(),
      run: run({
        id: 'r-2',
        status: 'Failed',
        error: { code: 'deadline_exceeded', message: null },
      }),
    });
    expect(el.querySelector('[data-testid=preview]')).not.toBeNull();
    expect(values()).toContain('Realizzare un portale clienti');
    expect(el.querySelector('[data-testid=run-error]')?.textContent).toContain('tempo massimo');
    expect(el.querySelector('[data-testid=run-error]')?.textContent).toContain(
      'ultima anteprima valida',
    );
  });

  it('alla riapertura con un run attivo mostra lo stato e blocca Analizza senza inviare comandi', async () => {
    await setup({
      project: project({ activeProcessingRunId: 'r-1', latestAnalysisRunId: 'r-1' }),
      run: run({
        status: 'Running',
        attemptCount: 1,
        lastObservedStage: 'ValidatePersistAnalysis',
      }),
    });
    expect(el.querySelector('[data-testid=run-state]')?.textContent).toContain(
      'validazione e salvataggio',
    );
    expect(button().disabled).toBe(true);
  });

  it("segnala le nuove note PM successive all'analisi", async () => {
    await setup({
      project: project({
        latestAnalysisRunId: 'r-1',
        currentAnalysisId: 'r-1',
        currentRevisionId: 'rev-1',
      }),
      analysis: analysis({ hasNewNotes: true }),
    });
    expect(el.querySelector('[data-testid=new-notes]')).not.toBeNull();
    expect(button().textContent).toContain('Analizza di nuovo');
  });

  describe('form di modifica (T035)', () => {
    const openProject = project({
      latestAnalysisRunId: 'r-1',
      currentAnalysisId: 'r-1',
      currentRevisionId: 'rev-1',
    });
    const tid = (id: string) => el.querySelector<HTMLButtonElement>(`[data-testid=${id}]`)!;
    const reqText = () =>
      el.querySelector<HTMLTextAreaElement>('[data-section=requirements] [data-testid=item-text]')!;
    const typeIn = async (ta: HTMLTextAreaElement, value: string) => {
      ta.value = value;
      ta.dispatchEvent(new Event('input'));
      await settle();
    };
    const press = async (id: string) => {
      tid(id).click();
      await Promise.resolve();
      await settle();
    };
    const open1 = (over: Partial<AnalysisView> = {}) =>
      setup({ project: openProject, analysis: analysis(over) });

    /** Revisione v2 del PM come la restituisce PUT. */
    const edited = (over: Partial<AnalysisView> = {}, requirement = 'Login con SSO') =>
      analysis({
        currentRevisionId: 'rev-2',
        version: 2,
        origin: 'PmEdit',
        createdBy: 'Mario Rossi',
        content: {
          ...analysis().content,
          requirements: [
            {
              id: 'q-1',
              text: requirement,
              classification: 'Inferred',
              evidenceRefs: ['s-1', 's-pm'],
            },
          ],
          sources: [
            ...analysis().content.sources,
            {
              id: 's-pm',
              kind: 'PmRevision',
              messageId: null,
              messageRole: null,
              revisionId: 'rev-2',
            },
          ],
        },
        ...over,
      });

    const withConflict = () => {
      const base = analysis();
      return analysis({
        content: {
          ...base.content,
          conflicts: [
            {
              id: 'k-1',
              description: 'Hosting: cloud o on-premise?',
              status: 'Open',
              alternatives: [
                { id: 'a-1', text: 'Cloud', classification: 'Declared', evidenceRefs: ['s-1'] },
                {
                  id: 'a-2',
                  text: 'On-premise',
                  classification: 'Declared',
                  evidenceRefs: ['s-1'],
                },
              ],
              resolution: null,
            },
          ],
        },
      });
    };

    it('mostra origine, versione e fonti; a revisione pulita il pulsante primario è Conferma', async () => {
      await open1();
      expect(tid('revision-info').textContent).toContain('Versione 1');
      expect(tid('revision-info').textContent).toContain("generata dall'AI");
      expect(text()).toContain('F1 · messaggio del PM');
      expect(tid('save').disabled).toBe(true);
      expect(tid('confirm').disabled).toBe(false);
      expect(tid('confirm').classList).toContain('btn-primary');
      expect(tid('save').classList).not.toContain('btn-primary');
      expect(el.querySelector('[data-testid=generate]')).toBeNull();
      // Tutte le sezioni sono modificabili, comprese integrazioni, assunzioni e lacune.
      for (const key of ['dependencies', 'assumptions', 'questions', 'requirements', 'goals']) {
        expect(el.querySelector(`[data-testid=add-${key}]`)).not.toBeNull();
      }
    });

    it('dopo una modifica Salva diventa primario e Conferma è bloccata finché non si salva', async () => {
      await open1();
      await typeIn(reqText(), 'Login con SSO');
      expect(el.querySelector('[data-testid=dirty-badge]')).not.toBeNull();
      expect(tid('save').disabled).toBe(false);
      expect(tid('save').classList).toContain('btn-primary');
      expect(tid('confirm').disabled).toBe(true);
      expect(tid('confirm').classList).not.toContain('btn-primary');
      expect(tid('analyze').disabled).toBe(true);
      expect(text()).toContain('Salva le modifiche prima di confermare');
    });

    it('Salva invia PUT con If-Match quotato e contenuto completo; Conferma, poi Genera solo sulla revisione confermata', async () => {
      await open1();
      await typeIn(reqText(), 'Login con SSO');
      await press('save');
      const put = http.expectOne(
        (r) => r.method === 'PUT' && r.url === `${BASE}/analyses/r-1/preview`,
      );
      expect(put.request.headers.get('If-Match')).toBe('"rev-1"');
      expect(put.request.body.requirements[0].text).toBe('Login con SSO');
      expect(put.request.body.goals[0].text).toBe('Realizzare un portale clienti');
      expect(put.request.body.schemaVersion).toBe('1.0.0');
      put.flush(edited(), { headers: { ETag: '"rev-2"' } });
      await settle();

      expect(el.querySelector('[data-testid=dirty-badge]')).toBeNull();
      expect(tid('revision-info').textContent).toContain('Versione 2');
      expect(tid('revision-info').textContent).toContain('modificata dal PM');
      expect(tid('revision-info').textContent).toContain('Mario Rossi');
      expect(text()).toContain('F2 · revisione del PM');
      expect(tid('save').disabled).toBe(true);
      expect(tid('confirm').classList).toContain('btn-primary');
      expect(el.querySelector('[data-testid=generate]')).toBeNull();

      await press('confirm');
      const confirm = http.expectOne(
        (r) => r.method === 'POST' && r.url === `${BASE}/analyses/r-1/confirm`,
      );
      expect(confirm.request.headers.get('If-Match')).toBe('"rev-2"');
      expect(confirm.request.body).toBeNull();
      confirm.flush(
        edited({
          validationState: 'PmConfirmed',
          confirmedBy: 'Mario Rossi',
          confirmedAt: '2030-01-02T10:00:00Z',
          canConfirm: false,
          canGenerate: true,
        }),
      );
      await settle();

      expect(el.querySelector<HTMLElement>('[data-testid=confirmed-badge]')!.hidden).toBe(false);
      expect(tid('confirm').disabled).toBe(true);
      const generate = tid('generate');
      expect(generate.disabled).toBe(false);
      expect(generate.classList).toContain('btn-primary');
      const emitted: string[] = [];
      fixture.componentInstance.generateRequested.subscribe((id) => emitted.push(id));
      generate.click();
      expect(emitted).toEqual(['rev-2']);
      // Nessuna chiamata di generazione né altra richiesta HTTP (verificato da http.verify in afterEach).
    });

    it('Genera è disabilitato mentre una generazione è in corso e viewChanged riporta la revisione confermata', async () => {
      const changes: unknown[] = [];
      await open1({ validationState: 'PmConfirmed', canConfirm: false, canGenerate: true });
      fixture.componentInstance.viewChanged.subscribe((v) => changes.push(v));
      expect(tid('generate').disabled).toBe(false);
      fixture.componentRef.setInput('generationBusy', true);
      fixture.detectChanges();
      expect(tid('generate').disabled).toBe(true);
    });

    it('Genera è disabilitato se la revisione confermata non può generare', async () => {
      await open1({ validationState: 'PmConfirmed', canConfirm: false, canGenerate: false });
      expect(tid('generate').disabled).toBe(true);
    });

    it('422: mostra gli errori di campo e conserva il testo digitato', async () => {
      await open1();
      await typeIn(reqText(), 'Testo non accettato');
      await press('save');
      http
        .expectOne((r) => r.method === 'PUT')
        .flush(
          {
            code: 'preview_invalid',
            errors: [{ field: 'requirements[0].text', message: 'vuoto o solo spazi.' }],
          },
          { status: 422, statusText: 'Unprocessable' },
        );
      await settle();
      const errors = tid('field-errors').textContent ?? '';
      expect(errors).toContain('Requisiti, voce 1 (testo)');
      expect(errors).toContain('vuoto o solo spazi.');
      expect(tid('save-error').textContent).toContain('Nulla è stato salvato');
      expect(reqText().value).toBe('Testo non accettato');
      expect(reqText().getAttribute('aria-invalid')).toBe('true');
      expect(tid('save').disabled).toBe(false); // riprovabile dopo la correzione
    });

    it('un testo vuoto è segnalato subito e blocca Salva senza chiamare il server', async () => {
      await open1();
      await typeIn(reqText(), '   ');
      expect(tid('client-errors').textContent).toContain(
        'Requisiti, voce 1: il testo è obbligatorio',
      );
      expect(tid('save').disabled).toBe(true);
    });

    it('409: le modifiche dell’utente non vengono sovrascritte e il PM sceglie come procedere', async () => {
      await open1();
      await typeIn(reqText(), 'La mia modifica');
      await press('save');
      http
        .expectOne((r) => r.method === 'PUT')
        .flush(
          { code: 'revision_conflict', currentRevisionId: 'rev-2' },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();
      http.expectOne(`${BASE}/analyses/r-1`).flush(edited({}, 'Modifica di un altro PM'));
      await settle();

      expect(el.querySelector('[data-testid=conflict-banner]')?.textContent).toContain(
        'versione 2',
      );
      expect(reqText().value).toBe('La mia modifica'); // nessuna sovrascrittura
      expect(tid('save').disabled).toBe(true); // nessun invio finché non si sceglie
      expect(tid('confirm').disabled).toBe(true);

      await press('conflict-keep');
      expect(reqText().value).toBe('La mia modifica');
      expect(el.querySelector('[data-testid=conflict-banner]')).toBeNull();
      expect(tid('save').disabled).toBe(false);
      await press('save');
      const retry = http.expectOne((r) => r.method === 'PUT');
      expect(retry.request.headers.get('If-Match')).toBe('"rev-2"');
      expect(retry.request.body.requirements[0].text).toBe('La mia modifica');
      // Le fonti storiche della revisione corrente restano nel corpo (il server non le accetta mutate).
      expect(retry.request.body.sources.map((s: { id: string }) => s.id)).toEqual(['s-1', 's-pm']);
      retry.flush(edited({ currentRevisionId: 'rev-3', version: 3 }, 'La mia modifica'));
      await settle();
      expect(tid('revision-info').textContent).toContain('Versione 3');
    });

    it('409: «Scarta» mostra la versione corrente senza inviare nulla', async () => {
      await open1();
      await typeIn(reqText(), 'La mia modifica');
      await press('save');
      http
        .expectOne((r) => r.method === 'PUT')
        .flush({ code: 'revision_conflict' }, { status: 409, statusText: 'Conflict' });
      await settle();
      http.expectOne(`${BASE}/analyses/r-1`).flush(edited({}, 'Modifica di un altro PM'));
      await settle();
      await press('conflict-reload');
      expect(reqText().value).toBe('Modifica di un altro PM');
      expect(el.querySelector('[data-testid=dirty-badge]')).toBeNull();
    });

    it('risoluzione di un conflitto: serve almeno una scelta e la motivazione', async () => {
      await setup({ project: openProject, analysis: withConflict() });
      const reason = () =>
        el.querySelector<HTMLTextAreaElement>('[data-testid=resolution-reason]')!;
      const choice = () => el.querySelector<HTMLInputElement>('[data-testid=alt-choice]')!;
      expect(tid('resolve-conflict').disabled).toBe(true);
      expect(el.querySelector('[data-testid=resolve-hint]')).not.toBeNull();

      choice().click();
      await settle();
      expect(tid('resolve-conflict').disabled).toBe(true); // manca la motivazione
      await typeIn(reason(), '   ');
      expect(tid('resolve-conflict').disabled).toBe(true); // solo spazi
      await typeIn(reason(), 'Deciso con il cliente il 12/03');
      expect(tid('resolve-conflict').disabled).toBe(false);

      await press('resolve-conflict');
      expect(el.querySelector('[data-testid=conflict-status]')!.textContent).toContain('Risolto');
      expect(tid('save').classList).toContain('btn-primary');
      await press('save');
      const put = http.expectOne((r) => r.method === 'PUT');
      const conflict = put.request.body.conflicts[0];
      expect(conflict.status).toBe('Resolved');
      expect(conflict.resolution.selectedAlternativeIds).toEqual(['a-1']);
      expect(conflict.resolution.reason).toBe('Deciso con il cliente il 12/03');
      // Le alternative originali restano tutte nel corpo.
      expect(conflict.alternatives.map((a: { id: string }) => a.id)).toEqual(['a-1', 'a-2']);
      put.flush(withConflict());
      await settle();
    });

    it('rinvio: un conflitto risolto può tornare aperto senza risoluzione', async () => {
      const base = withConflict();
      const resolved = analysis({
        content: {
          ...base.content,
          conflicts: [
            {
              ...base.content.conflicts[0],
              status: 'Resolved',
              resolution: {
                selectedAlternativeIds: ['a-2'],
                reason: 'Scelta del PM',
                evidenceRefs: ['s-1'],
              },
            },
          ],
        },
      });
      await setup({ project: openProject, analysis: resolved });
      expect(el.querySelector('[data-testid=conflict-status]')!.textContent).toContain('Risolto');
      expect(el.querySelector<HTMLTextAreaElement>('[data-testid=resolution-reason]')!.value).toBe(
        'Scelta del PM',
      );
      await press('defer-conflict');
      expect(el.querySelector('[data-testid=conflict-status]')!.textContent).toContain('Aperto');
      await press('save');
      const put = http.expectOne((r) => r.method === 'PUT');
      expect(put.request.body.conflicts[0].status).toBe('Open');
      expect(put.request.body.conflicts[0].resolution).toBeNull();
      put.flush(base);
      await settle();
    });

    it('aggiunge voci con id nuovo e classificazione coerente con la sezione', async () => {
      await open1();
      tid('add-assumptions').click();
      await settle();
      const added = el.querySelector<HTMLTextAreaElement>(
        '[data-section=assumptions] [data-testid=item-text]',
      )!;
      expect(tid('save').disabled).toBe(true); // voce vuota: non salvabile
      await typeIn(added, 'Il cliente fornisce le API');
      await press('save');
      const put = http.expectOne((r) => r.method === 'PUT');
      expect(put.request.body.assumptions[0].classification).toBe('Assumed');
      expect(put.request.body.assumptions[0].id).toMatch(/^[0-9a-f-]{36}$/);
      expect(put.request.body.assumptions[0].evidenceRefs).toEqual([]);
      put.flush(analysis({ currentRevisionId: 'rev-2', version: 2 }));
      await settle();
    });

    it('con un’analisi attiva (canEdit=false) il form è di sola lettura e Salva/Conferma sono bloccati', async () => {
      await setup({
        project: project({
          activeProcessingRunId: 'r-2',
          latestAnalysisRunId: 'r-2',
          currentAnalysisId: 'r-1',
          currentRevisionId: 'rev-1',
        }),
        analysis: analysis({ canEdit: false, canConfirm: false }),
        run: run({ id: 'r-2', status: 'Running', attemptCount: 1 }),
      });
      expect(el.querySelector('[data-testid=readonly-note]')).not.toBeNull();
      expect(el.querySelector('fieldset')!.disabled).toBe(true);
      expect(tid('save').disabled).toBe(true);
      expect(tid('confirm').disabled).toBe(true);
      expect(el.querySelector('[data-testid=add-goals]')).toBeNull();
      expect(el.querySelector('[data-testid=remove-item]')).toBeNull();
      expect(values()).toContain('Realizzare un portale clienti'); // resta leggibile
    });
  });

  it('chiude lo stream alla distruzione', async () => {
    await setup();
    fixture.destroy();
    expect(close).toHaveBeenCalled();
  });

  describe('metriche di inferenza del run', () => {
    const line = () =>
      [...el.querySelectorAll('[data-testid=run-metrics] .inference-metrics > *')]
        .map(
          (n) =>
            n.querySelector('[aria-hidden=true]')?.textContent?.trim() ?? n.textContent?.trim(),
        )
        .join(' ')
        .replace(/\s+/g, ' ');

    async function startRun() {
      await setup();
      await click();
      http
        .expectOne((r) => r.method === 'POST' && r.url === `${BASE}/analyses`)
        .flush(run(), { status: 202, statusText: 'Accepted' });
      await settle();
    }

    it('run in corso senza telemetria: nessuna riga; con metriche running: configurazione e «generazione in corso…»', async () => {
      await startRun();
      expect(el.querySelector('[data-testid=run-metrics]')).toBeNull();

      handlers.onEvent({
        name: 'processing.state',
        data: run({
          status: 'Running',
          metrics: metrics({
            state: 'running',
            phase: 'analysis',
            maxOutputTokens: { value: 8192, confirmed: false },
          }),
        }),
      });
      await settle();

      expect(line()).toBe(
        'CTX 49.152 · Max output 8.192 (limite richiesto) · generazione in corso…',
      );
    });

    it('run completato: mostra le metriche definitive del run (stessa riga della chat)', async () => {
      await startRun();
      handlers.onEvent({
        name: 'processing.state',
        data: run({
          status: 'Completed',
          analysisId: 'r-1',
          metrics: metrics({
            phase: 'analysis',
            completionTokens: measured(2650),
            finishReason: 'length',
          }),
        }),
      });
      await settle();
      http.expectOne(`${BASE}/analyses/r-1`).flush(analysis());
      await settle();

      expect(line()).toBe(
        'CTX 49.152 · Max output 8.192 · Generati 2.650 token Risposta troncata dal limite di output',
      );
      expect(
        el.querySelector('[data-testid=run-metrics] [data-testid=metrics-truncated]'),
      ).not.toBeNull();
    });

    it('run completato senza telemetria (storico): «Metriche non disponibili»', async () => {
      await startRun();
      handlers.onEvent({
        name: 'processing.state',
        data: run({ status: 'Completed', analysisId: 'r-1', metrics: null }),
      });
      await settle();
      http.expectOne(`${BASE}/analyses/r-1`).flush(analysis());
      await settle();

      expect(
        el.querySelector('[data-testid=run-metrics] [data-testid=metrics-unavailable]'),
      ).not.toBeNull();
    });

    it("dopo un riavvio (nessun run noto) usa le metriche dell'AnalysisView già letta", async () => {
      await setup({
        project: project({ currentAnalysisId: 'r-1', latestAnalysisRunId: 'r-1' }),
        analysis: analysis({
          metrics: metrics({ phase: 'analysis', completionTokens: measured(1890) }),
        }),
      });

      expect(line()).toBe('CTX 49.152 · Max output 8.192 · Generati 1.890 token');
    });

    it("un'analisi storica senza metriche dichiara «Metriche non disponibili»", async () => {
      await setup({
        project: project({ currentAnalysisId: 'r-1', latestAnalysisRunId: 'r-1' }),
        analysis: analysis({ metrics: null }),
      });

      expect(
        el.querySelector('[data-testid=run-metrics] [data-testid=metrics-unavailable]'),
      ).not.toBeNull();
    });

    it('run fallito: nessuna metrica attribuita', async () => {
      await startRun();
      handlers.onEvent({
        name: 'processing.state',
        data: run({
          status: 'Failed',
          error: { code: 'llm_unavailable', message: null },
          metrics: metrics({ state: 'failed' }),
        }),
      });
      await settle();

      expect(el.querySelector('[data-testid=run-error]')).not.toBeNull();
      expect(el.querySelector('[data-testid=run-metrics]')).toBeNull();
    });
  });
});
