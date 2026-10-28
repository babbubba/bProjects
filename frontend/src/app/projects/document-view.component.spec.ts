import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { ANALYSIS_POLL_MS } from './analysis-preview.component';
import { CurrentRevision, DocumentViewComponent } from './document-view.component';
import { DraftView, ProcessingRunView, ProjectView, RevisionView } from './project-api.service';
import { ProjectEventsService, ProjectStreamHandlers } from './project-events.service';

const P = 'p-1';
const BASE = `/api/projects/${P}`;

const project = (over: Partial<ProjectView> = {}): ProjectView => ({
  id: P,
  conversationId: 'c',
  name: 'A',
  description: null,
  createdAt: '2030-01-01T10:00:00Z',
  createdBy: 'pm',
  activeProcessingRunId: null,
  ...over,
});

const run = (over: Partial<ProcessingRunView> = {}): ProcessingRunView => ({
  id: 'd-run',
  kind: 'Document',
  analysisId: 'a-1',
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
});

const draft = (over: Partial<DraftView> = {}): DraftView => ({
  id: 'dr-1',
  projectId: P,
  processingRunId: 'd-run',
  analysisId: 'a-1',
  analysisRevisionId: 'rev-1',
  analysisVersion: 2,
  markdown: '# Portale\n\nLogin con email [fonte:s-1].',
  createdAt: '2030-01-01T10:05:00Z',
  status: 'Proposed',
  approvalStatus: 'NotApproved',
  hasNewNotes: false,
  hasNewRevision: false,
  label: 'Bozza AI — da revisionare',
  ...over,
});

const revision = (): RevisionView => ({
  analysisId: 'a-1',
  revisionId: 'rev-1',
  version: 2,
  isCurrent: true,
  validationState: 'PmConfirmed',
  content: {
    sources: [
      { id: 's-1', kind: 'Message', messageId: 'm-1', messageRole: 'user', revisionId: null },
    ],
  } as RevisionView['content'],
});

const confirmed: CurrentRevision = { analysisId: 'a-1', revisionId: 'rev-1', confirmed: true };

describe('DocumentViewComponent', () => {
  let fixture: ComponentFixture<DocumentViewComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let handlers: ProjectStreamHandlers;
  const open = vi.fn();

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid=${id}]`);

  async function setup(
    opts: {
      project?: ProjectView;
      draft?: DraftView | null;
      run?: ProcessingRunView;
      current?: CurrentRevision | null;
    } = {},
  ) {
    open.mockReset();
    open.mockImplementation((_id: string, h: ProjectStreamHandlers) => {
      handlers = h;
      return () => undefined;
    });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ANALYSIS_POLL_MS, useValue: 0 },
        { provide: ProjectEventsService, useValue: { open } },
        { provide: AuthService, useValue: { isAuthenticated: signal(true).asReadonly() } },
      ],
    });
    fixture = TestBed.createComponent(DocumentViewComponent);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement;
    fixture.componentRef.setInput('projectId', P);
    fixture.componentRef.setInput('current', opts.current === undefined ? confirmed : opts.current);
    fixture.detectChanges();
    await Promise.resolve();
    http.expectOne(BASE).flush(opts.project ?? project());
    await settle();
    if (opts.run) {
      http.expectOne(`${BASE}/processing-runs/${opts.run.id}`).flush(opts.run);
      await settle();
    }
    const req = http.expectOne(`${BASE}/draft`);
    if (opts.draft) {
      req.flush(opts.draft);
      await settle();
      http
        .expectOne(`${BASE}/analyses/a-1/revisions/${opts.draft.analysisVersion}`)
        .flush(revision());
    } else {
      req.flush({ code: 'draft_not_available' }, { status: 404, statusText: 'Not Found' });
    }
    await settle();
  }

  afterEach(() => {
    fixture.destroy();
    http.verify();
  });

  it('senza bozza mostra lo stato vuoto', async () => {
    await setup({ draft: null });
    expect(q('draft-empty')).not.toBeNull();
    expect(q('draft')).toBeNull();
  });

  it('mostra la bozza Markdown in sola lettura come «Proposta / non approvata» con fonti navigabili', async () => {
    await setup({ draft: draft() });
    expect(q('draft-status')!.textContent).toContain('Proposta / non approvata');
    expect(q('markdown')!.textContent).toContain('Login con email');
    expect(q('markdown')!.querySelector('h3')!.textContent).toContain('Portale');
    expect(el.querySelector('textarea, input')).toBeNull();
    expect(q('source-chip')!.textContent!.trim()).toBe('F1');
    expect(q('stale-banner')).toBeNull();
    expect(q('reconfirm-banner')).toBeNull();
  });

  it('il clic su una fonte evidenzia il messaggio nel dialogo', async () => {
    const msg = document.createElement('article');
    msg.id = 'message-m-1';
    document.body.appendChild(msg);
    try {
      await setup({ draft: draft() });
      q('source-chip')!.click();
      await settle();
      expect(msg.getAttribute('data-highlighted')).toBe('true');
      expect(q('source-notice')!.textContent).toContain('F1');
    } finally {
      msg.remove();
    }
  });

  it('una fonte non presente nel dialogo mostra un avviso', async () => {
    await setup({ draft: draft() });
    q('source-link')!.click();
    await settle();
    expect(q('source-notice')!.textContent).toContain('non è visibile');
  });

  it('sanifica script, HTML, immagini remote e link javascript:', async () => {
    const markdown =
      '<script>window.__x=1</script>\n\n<img src="https://evil.example/p.png" onerror="alert(1)">\n\n![t](https://evil.example/t.png)\n\n[clic](javascript:alert(1)) [ok](https://example.com)';
    await setup({ draft: draft({ markdown }) });
    const md = q('markdown')!;
    expect(md.querySelector('script, img, iframe')).toBeNull();
    expect(md.innerHTML).not.toMatch(/javascript:/i);
    expect((window as unknown as { __x?: number }).__x).toBeUndefined();
    expect(md.textContent).toContain('<script>');
    const links = Array.from(md.querySelectorAll('a')).map((a) => a.getAttribute('href'));
    expect(links).toEqual(['https://example.com/']);
  });

  it('avvisa quando la bozza è su una vecchia revisione', async () => {
    await setup({ draft: draft(), current: { ...confirmed, revisionId: 'rev-9' } });
    expect(q('stale-banner')!.textContent).toContain('vecchia revisione');
    fixture.componentRef.setInput('current', confirmed);
    fixture.detectChanges();
    expect(q('stale-banner')).toBeNull();
  });

  it('dopo una modifica richiede nuova conferma e mantiene la bozza visibile', async () => {
    await setup({ draft: draft(), current: { ...confirmed, confirmed: false } });
    expect(q('reconfirm-banner')!.textContent).toContain('Nuova conferma richiesta');
    expect(q('draft')).not.toBeNull();
  });

  it('segnala hasNewNotes e hasNewRevision dal server', async () => {
    await setup({ draft: draft({ hasNewNotes: true, hasNewRevision: true }), current: null });
    expect(q('stale-banner')).not.toBeNull();
    expect(q('draft-new-notes')).not.toBeNull();
  });

  it('Genera invia POST con Idempotency-Key e revisionId, segue SSE e carica la nuova bozza', async () => {
    await setup({ draft: draft() });
    void fixture.componentInstance.generate('rev-1');
    await settle();
    const post = http.expectOne(
      (r) => r.method === 'POST' && r.url === `${BASE}/analyses/a-1/document-generations`,
    );
    expect(post.request.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(post.request.body).toEqual({ revisionId: 'rev-1' });
    post.flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    expect(q('generation-state')!.textContent).toContain('In coda');
    expect(fixture.componentInstance.busy()).toBe(true);
    expect(q('draft')).not.toBeNull(); // l'ultima bozza resta visibile

    handlers.onEvent({
      name: 'processing.state',
      data: run({ status: 'Running', lastObservedStage: 'LoadAnalysis' }),
    });
    await settle();
    expect(q('generation-state')!.textContent).toContain("lettura dell'analisi confermata");
    handlers.onEvent({
      name: 'processing.state',
      data: { ...run(), id: 'altro', kind: 'Analysis', status: 'Failed' },
    });
    await settle();
    expect(q('generation-error')).toBeNull();

    handlers.onEvent({
      name: 'processing.state',
      data: run({ status: 'Completed', draftId: 'dr-2' }),
    });
    await settle();
    http.expectOne(`${BASE}/draft`).flush(draft({ id: 'dr-2', markdown: 'Nuova **bozza**' }));
    await settle();
    http.expectOne(`${BASE}/analyses/a-1/revisions/2`).flush(revision());
    await settle();
    expect(q('generation-state')).toBeNull();
    expect(q('markdown')!.textContent).toContain('Nuova bozza');
  });

  it("un run fallito mostra l'errore in italiano e non nasconde l'ultima bozza", async () => {
    await setup({ draft: draft() });
    void fixture.componentInstance.generate('rev-1');
    await settle();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(run(), { status: 202, statusText: 'Accepted' });
    await settle();
    handlers.onEvent({
      name: 'processing.state',
      data: run({ status: 'Failed', error: { code: 'invalid_agent_output', message: null } }),
    });
    await settle();
    expect(q('generation-error')!.textContent).toContain('non è stato salvato');
    expect(q('generation-error')!.textContent).toContain("L'ultima bozza valida resta");
    expect(q('markdown')!.textContent).toContain('Login con email');
    expect(fixture.componentInstance.busy()).toBe(false);
  });

  it('un invio incerto riusa la stessa Idempotency-Key; un nuovo comando ne usa una nuova', async () => {
    await setup({ draft: null });
    void fixture.componentInstance.generate('rev-1');
    await settle();
    const first = http.expectOne((r) => r.method === 'POST');
    const key = first.request.headers.get('Idempotency-Key');
    first.error(new ProgressEvent('error'), { status: 0 });
    await settle();
    expect(q('generate-error')!.textContent).toContain('Non sappiamo se');
    q('generate-retry')!.click();
    await settle();
    const second = http.expectOne((r) => r.method === 'POST');
    expect(second.request.headers.get('Idempotency-Key')).toBe(key);
    second.flush(run({ status: 'Completed' }), { status: 200, statusText: 'OK' });
    await settle();
    http
      .expectOne(`${BASE}/draft`)
      .flush({ code: 'draft_not_available' }, { status: 404, statusText: 'NF' });
    await settle();
    void fixture.componentInstance.generate('rev-1');
    await settle();
    const third = http.expectOne((r) => r.method === 'POST');
    expect(third.request.headers.get('Idempotency-Key')).not.toBe(key);
    third.flush({ code: 'processing_active' }, { status: 409, statusText: 'Conflict' });
    await settle();
    expect(q('generate-error')!.textContent).toContain('già in corso');
  });

  it('non genera senza revisione confermata corrente', async () => {
    await setup({ draft: null, current: { ...confirmed, confirmed: false } });
    await fixture.componentInstance.generate('rev-1');
    await fixture.componentInstance.generate('altra');
    http.expectNone((r) => r.method === 'POST');
    expect(q('generation-state')).toBeNull();
  });

  it('mappa i 409 del backend in messaggi italiani', async () => {
    await setup({ draft: null });
    const cases: [string, string][] = [
      ['analysis_not_current', "non è più l'analisi corrente"],
      ['analysis_not_confirmed', 'Nuova conferma richiesta'],
      ['idempotency_conflict', 'input diverso'],
    ];
    for (const [code, expected] of cases) {
      void fixture.componentInstance.generate('rev-1');
      await settle();
      http
        .expectOne((r) => r.method === 'POST')
        .flush({ code }, { status: 409, statusText: 'Conflict' });
      await settle();
      expect(q('generate-error')!.textContent).toContain(expected);
    }
  });

  it('riprende un run Document attivo dopo il ricaricamento (stato reale via GET)', async () => {
    await setup({
      project: project({ activeProcessingRunId: 'd-run' }),
      run: run({ status: 'Running', attemptCount: 1 }),
      draft: null,
    });
    expect(q('generation-state')!.textContent).toContain('Generazione in corso');
    expect(fixture.componentInstance.busy()).toBe(true);
  });
});
