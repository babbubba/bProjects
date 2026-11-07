import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { ANALYSIS_POLL_MS } from './analysis-preview.component';
import { CurrentRevision, DocumentViewComponent } from './document-view.component';
import { DraftView, ProcessingRunView, ProjectView, RevisionView } from './project-api.service';
import { measured, metrics } from './context-usage.fixtures';
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
      revision?: RevisionView;
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
        .flush(opts.revision ?? revision());
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

  it('context_overflow: indica il contenuto da ridurre e mostra i valori misurati', async () => {
    await setup({ draft: draft() });
    void fixture.componentInstance.generate('rev-1');
    await settle();
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
    const text = q('generation-error')!.textContent ?? '';
    expect(text).toContain('31000 token');
    expect(text).toContain('24000 token');
    expect(text).toContain("contenuto dell'anteprima");
    expect(text).not.toContain('rifiutato la richiesta');
    expect(text).not.toContain('%');
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

  describe('metriche di inferenza del run', () => {
    const line = () =>
      [...el.querySelectorAll('[data-testid=run-metrics] .inference-metrics > *')]
        .map(
          (n) =>
            n.querySelector('[aria-hidden=true]')?.textContent?.trim() ?? n.textContent?.trim(),
        )
        .join(' ')
        .replace(/\s+/g, ' ');

    async function generate() {
      await setup({ draft: draft() });
      void fixture.componentInstance.generate('rev-1');
      await settle();
      http
        .expectOne((r) => r.method === 'POST')
        .flush(run(), { status: 202, statusText: 'Accepted' });
      await settle();
    }

    it('in corso mostra la configurazione nota della richiesta, a fine run le metriche definitive', async () => {
      await generate();
      expect(q('run-metrics')).toBeNull();

      handlers.onEvent({
        name: 'processing.state',
        data: run({ status: 'Running', metrics: metrics({ state: 'running', phase: 'document' }) }),
      });
      await settle();
      expect(line()).toBe('CTX 49.152 · Max output 8.192 · generazione in corso…');

      handlers.onEvent({
        name: 'processing.state',
        data: run({
          status: 'Completed',
          draftId: 'dr-2',
          metrics: metrics({ phase: 'document', completionTokens: measured(3100) }),
        }),
      });
      await settle();
      http.expectOne(`${BASE}/draft`).flush(draft({ id: 'dr-2' }));
      await settle();
      http.expectOne(`${BASE}/analyses/a-1/revisions/2`).flush(revision());
      await settle();

      expect(line()).toBe('CTX 49.152 · Max output 8.192 · Generati 3.100 token');
    });

    it('run completato senza telemetria: «Metriche non disponibili»', async () => {
      await generate();
      handlers.onEvent({
        name: 'processing.state',
        data: run({ status: 'Completed', draftId: 'dr-2', metrics: null }),
      });
      await settle();
      http.expectOne(`${BASE}/draft`).flush(draft({ id: 'dr-2' }));
      await settle();
      http.expectOne(`${BASE}/analyses/a-1/revisions/2`).flush(revision());
      await settle();

      expect(q('run-metrics')!.querySelector('[data-testid=metrics-unavailable]')).not.toBeNull();
    });

    it('dopo un riavvio senza run noto usa le metriche del DraftView già letto, senza GET aggiuntivi', async () => {
      await setup({
        draft: draft({ metrics: metrics({ phase: 'document', completionTokens: measured(2100) }) }),
      });

      expect(line()).toBe('CTX 49.152 · Max output 8.192 · Generati 2.100 token');
    });

    it('una bozza storica senza metriche dichiara «Metriche non disponibili»', async () => {
      await setup({ draft: draft({ metrics: null }) });

      expect(q('run-metrics')!.querySelector('[data-testid=metrics-unavailable]')).not.toBeNull();
    });

    it('context_window_mismatch e compaction_failed hanno un testo in italiano senza cifre', async () => {
      await generate();
      handlers.onEvent({
        name: 'processing.state',
        data: run({
          status: 'Failed',
          error: {
            code: 'context_window_mismatch',
            message:
              'La finestra di contesto del runtime del modello (16384 token) è inferiore a quella configurata (32768 token).',
          },
        }),
      });
      await settle();
      const text = q('generation-error')!.textContent ?? '';
      expect(text).toContain('finestra di contesto del modello è inferiore a quella configurata');
      expect(text).not.toMatch(/[0-9]{3,}/);
    });

    it('run fallito: nessuna metrica attribuita', async () => {
      await generate();
      handlers.onEvent({
        name: 'processing.state',
        data: run({
          status: 'Failed',
          error: { code: 'llm_unavailable', message: null },
          metrics: metrics({ state: 'failed' }),
        }),
      });
      await settle();

      expect(q('generation-error')).not.toBeNull();
      expect(q('run-metrics')).toBeNull();
    });
  });

  describe('percorso documentale (002 T063)', () => {
    const docSource = {
      id: 'ds-1',
      kind: 'DocumentPassage' as const,
      messageId: null,
      messageRole: null,
      revisionId: null,
      documentId: 'doc-1',
      documentVersionId: 'ver-1',
      preparationId: 'prep-1',
      artifactId: 'art-1',
      blockId: 'b-1',
      start: 0,
      end: 12,
      quote: '<b>Il sistema</b>',
    };
    const prepUrl = `${BASE}/documents/doc-1/versions/ver-1/preparations/prep-1`;
    const docRevision = (): RevisionView => ({
      ...revision(),
      content: {
        schemaVersion: '1.1.0',
        sources: [docSource],
      } as unknown as RevisionView['content'],
    });

    it('una citazione DocumentPassage apre il passaggio nel documento e la citazione resta testo', async () => {
      await setup({
        draft: draft({ markdown: 'Esporta i dati [fonte:ds-1].' }),
        revision: docRevision(),
      });
      expect(q('source-chip')!.textContent!.trim()).toBe('F1');
      expect(q('draft-doc-sources')!.textContent).toContain('<b>Il sistema</b>');
      expect(el.querySelector('.document b')).toBeNull();
      http.expectNone(prepUrl);
      q('source-chip')!.click();
      await settle();
      expect(q('source-notice')!.textContent).toContain('F1');
      expect(q('focused-source')).not.toBeNull();
      http.expectOne(prepUrl);
    });

    describe('mappa storica delle fonti (002 T087)', () => {
      const part = (id: string, label: string) => ({
        ...docSource,
        id,
        blockId: `b-${id}`,
        quote: `quota ${label}`,
      });
      const multiRevision = (): RevisionView => ({
        ...revision(),
        content: {
          schemaVersion: '1.1.0',
          sources: [
            part('hdr', 'intestazione'),
            part('pg2', 'pagina 2'),
            part('pg3', 'pagina 3'),
            {
              id: 'pm-1',
              kind: 'PmRevision',
              messageId: null,
              messageRole: null,
              revisionId: 'rev-0',
            },
          ],
        } as unknown as RevisionView['content'],
      });
      const entry = (
        item: string,
        ev: string | null,
        group: string | null,
        p: number | null,
        source: string,
      ) => ({
        itemId: item,
        evidenceId: ev,
        citationGroupId: group,
        part: p,
        sourceId: source,
      });
      // Due gruppi con lo stesso header: g-1 (hdr, pg2) e g-2 (hdr, pg3); la sequenza globale dei sourceIds non ne e' la prova.
      const sourceMap = [
        entry('i-1', 'e-1', 'g-1', 1, 'pg2'),
        entry('i-1', 'e-1', 'g-1', 0, 'hdr'),
        entry('i-2', 'e-2', 'g-2', 0, 'hdr'),
        entry('i-2', 'e-2', 'g-2', 1, 'pg3'),
        entry('i-3', null, null, null, 'pm-1'),
      ];
      const parts = () => [
        ...el.querySelectorAll<HTMLElement>(
          '[data-testid=focused-source] [data-testid=source-part]',
        ),
      ];

      // I passaggi aperti leggono la preparazione dall'API (riuso T030): qui interessa solo quali parti si aprono.
      afterEach(() => http.match((r) => r.url.includes('/preparations/')));

      it("i marcatori atomici restano nell'ordine richiesto, uno per fonte, senza fonderli in una citazione", async () => {
        await setup({
          draft: draft({
            markdown: '- Req [fonte:pg2] [fonte:hdr]\n- Altro [fonte:hdr] [fonte:pg3]',
            sourceMap,
          }),
          revision: multiRevision(),
        });
        const chips = [...el.querySelectorAll<HTMLElement>('[data-testid=source-chip]')].map((c) =>
          c.textContent!.trim(),
        );
        expect(chips).toEqual(['F2', 'F1', 'F1', 'F3']);
      });

      it("una fonte condivisa da due gruppi li apre come due citazioni distinte, ciascuna con le proprie parti nell'ordine della mappa", async () => {
        await setup({
          draft: draft({ markdown: 'Req [fonte:hdr].', sourceMap }),
          revision: multiRevision(),
        });
        q('source-chip')!.click();
        await settle();
        const groups = el.querySelectorAll('[data-testid=focused-source]');
        expect(groups.length).toBe(2);
        expect(
          [...groups].map((g) =>
            [...g.querySelectorAll('[data-testid=part-quote]')].map((x) => x.textContent),
          ),
        ).toEqual([
          ['«quota intestazione»', '«quota pagina 2»'],
          ['«quota intestazione»', '«quota pagina 3»'],
        ]);
        expect(q('source-notice')!.textContent).toContain('2 citazioni distinte');
      });

      it("una pagina cita il proprio gruppo: header e pagina, non le parti dell'altro gruppo", async () => {
        await setup({
          draft: draft({ markdown: 'Req [fonte:pg3].', sourceMap }),
          revision: multiRevision(),
        });
        q('source-chip')!.click();
        await settle();
        expect(
          parts().map((p) => p.querySelector('[data-testid=part-quote]')!.textContent),
        ).toEqual(['«quota intestazione»', '«quota pagina 3»']);
      });

      it('senza mappa (bozza storica) non ricostruisce gruppi: si apre la sola parte della fonte', async () => {
        await setup({
          draft: draft({ markdown: 'Req [fonte:pg2] [fonte:hdr].', sourceMap: null }),
          revision: multiRevision(),
        });
        q('source-chip')!.click();
        await settle();
        expect(el.querySelectorAll('[data-testid=focused-source]').length).toBe(1);
        expect(parts().length).toBe(1);
        expect(parts()[0].textContent).toContain('quota pagina 2');
      });

      it('una voce senza evidenza (PmRevision) non ha gruppo: nessuna parte documentale inventata e la fonte storica resta apribile', async () => {
        await setup({
          draft: draft({ markdown: 'Nuovo [fonte:pm-1].', sourceMap }),
          revision: multiRevision(),
        });
        q('source-chip')!.click();
        await settle();
        expect(q('focused-source')).toBeNull();
        expect(q('source-notice')!.textContent).toContain('anteprima della revisione');
      });

      it('dopo un edit PM (revisione corrente diversa) i gruppi storici della bozza restano apribili con le parti della sua revisione', async () => {
        await setup({
          draft: draft({ markdown: 'Req [fonte:hdr].', sourceMap }),
          revision: multiRevision(),
          current: { analysisId: 'a-1', revisionId: 'rev-9', confirmed: true },
        });
        expect(q('stale-banner')).not.toBeNull();
        q('source-chip')!.click();
        await settle();
        expect(el.querySelectorAll('[data-testid=focused-source]').length).toBe(2);
        expect(parts().length).toBe(4);
      });

      it('le voci della mappa con fonti non più presenti nella revisione non producono parti', async () => {
        const map = [
          entry('i-1', 'e-1', 'g-1', 0, 'hdr'),
          entry('i-1', 'e-1', 'g-1', 1, 'rimossa'),
        ];
        await setup({
          draft: draft({ markdown: 'Req [fonte:hdr].', sourceMap: map }),
          revision: multiRevision(),
        });
        q('source-chip')!.click();
        await settle();
        expect(parts().length).toBe(1);
        expect(parts()[0].textContent).toContain('quota intestazione');
      });
    });

    it('il Markdown non carica risorse remote: nessuna richiesta oltre alle API del progetto', async () => {
      await setup({
        draft: draft({
          markdown: '![logo](https://evil.example/p.png)\n\n<img src="https://evil.example/x">',
        }),
      });
      expect(el.querySelector('img')).toBeNull();
      expect(http.match(() => true).length).toBe(0);
    });

    it('una bozza della revisione 1 resta apribile con le sue fonti dopo il caricamento della revisione 2', async () => {
      await setup({
        draft: draft({ analysisVersion: 1, analysisRevisionId: 'rev-1' }),
        current: { analysisId: 'a-1', revisionId: 'rev-2', confirmed: true },
      });
      expect(q('draft')).not.toBeNull();
      expect(q('stale-banner')).not.toBeNull();
      expect(q('source-chip')!.textContent!.trim()).toBe('F1');
      expect(q('draft-sources')!.textContent).toContain('F1 · messaggio del PM');
    });

    it('hasSourceChanges avvisa senza chiedere una nuova conferma', async () => {
      await setup({ draft: draft({ hasSourceChanges: true }) });
      expect(q('draft-source-changes')!.textContent).toContain('conferma');
      expect(q('reconfirm-banner')).toBeNull();
    });

    it('un run fallito per documento non pronto dichiara la causa e conserva la bozza', async () => {
      await setup({
        draft: draft(),
        project: project({ activeProcessingRunId: 'd-run' }),
        run: run({ status: 'Failed', error: { code: 'document_incomplete', message: null } }),
      });
      expect(q('generation-error')!.textContent).toContain('non è completo o non è pronto');
      expect(q('draft')).not.toBeNull();
    });

    it('conferma mancante: messaggio chiaro, nessuna generazione', async () => {
      await setup({ draft: null, current: { ...confirmed, confirmed: false } });
      await fixture.componentInstance.generate('rev-1');
      http.expectNone((r) => r.method === 'POST');
    });
  });
});
