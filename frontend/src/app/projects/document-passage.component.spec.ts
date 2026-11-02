import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BlockPage, BlockView, PassageView, PreparationView } from './documents-api.service';
import { DocumentPassageComponent } from './document-passage.component';

const BASE = '/api/projects/p-1/documents/d-1/versions/2/preparations/latest';

function block(over: Partial<BlockView> = {}): BlockView {
  return {
    id: 'b-0',
    ref: 'r0',
    ordinal: 0,
    parentId: null,
    type: 'Paragraph',
    text: 'Testo',
    headingPath: [],
    page: null,
    bbox: null,
    lines: null,
    tableCells: null,
    ...over,
  };
}

function prep(over: Partial<PreparationView> = {}): PreparationView {
  return {
    id: 'g-1',
    documentVersionId: 'v-2',
    documentId: 'd-1',
    version: 2,
    generation: 1,
    processingStatus: 'Extracting',
    isReady: false,
    contentState: 'Parsed',
    blockCount: 2,
    retryOfId: null,
    requestedAt: '2030-01-01T10:00:00Z',
    issuesSummary: null,
    issues: [],
    allowedActions: ['download'],
    ...over,
  };
}

function page(items: BlockView[], nextCursor: string | null = null, ready = false): BlockPage {
  return {
    preparationId: 'g-1',
    processingStatus: ready ? 'Ready' : 'Extracting',
    isReady: ready,
    items,
    nextCursor,
  };
}

describe('DocumentPassageComponent', () => {
  let fixture: ComponentFixture<DocumentPassageComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let downloads: number;

  function mount(highlight: { blockId: string; start: number; end: number } | null = null) {
    fixture = TestBed.createComponent(DocumentPassageComponent);
    fixture.componentRef.setInput('projectId', 'p-1');
    fixture.componentRef.setInput('documentId', 'd-1');
    fixture.componentRef.setInput('version', 2);
    fixture.componentRef.setInput('highlight', highlight);
    fixture.componentInstance.downloadRequested.subscribe(() => downloads++);
    el = fixture.nativeElement;
    fixture.detectChanges();
  }

  async function settle() {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** Le richieste partono in sequenza (preparazione, poi blocchi): si risponde man mano. */
  async function flush(
    items: BlockView[],
    p = prep(),
    nextCursor: string | null = null,
    ready = false,
  ) {
    http.expectOne(BASE).flush(p);
    await settle();
    http.expectOne((r) => r.url === BASE + '/blocks').flush(page(items, nextCursor, ready));
    await settle();
  }

  beforeEach(() => {
    downloads = 0;
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('mostra i blocchi in ordine con pagina, sezione e righe solo se disponibili', async () => {
    mount();
    await flush([
      block({ id: 'b-0', ordinal: 0, text: 'Intro' }),
      block({
        id: 'b-1',
        ordinal: 1,
        text: 'Clausola',
        page: 3,
        headingPath: ['Capitolo 1', 'Penali'],
        lines: { start: 4, end: 6 },
      }),
    ]);

    const items = el.querySelectorAll('[data-testid="passage-block"]');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('Intro');
    expect(items[0].textContent).not.toContain('Pagina');
    expect(items[0].textContent).toContain('Posizione non disponibile');
    expect(items[1].textContent).toContain('Pagina 3');
    expect(items[1].textContent).toContain('Capitolo 1 › Penali');
    expect(items[1].textContent).toContain('righe 4-6');
  });

  it('Parsing/Extracting non è presentato come Pronto', async () => {
    mount();
    await flush([block()]);

    const status = el.querySelector('[data-testid="passage-status"]')!;
    expect(status.textContent).toContain('Estrazione in corso');
    expect(status.textContent).not.toContain('Pronto');
    expect(el.querySelector('[data-testid="passage-notready"]')).not.toBeNull();
  });

  it('Ready è presentato come Pronto senza avviso', async () => {
    mount();
    await flush(
      [block()],
      prep({ processingStatus: 'Ready', isReady: true, contentState: 'Ready' }),
      null,
      true,
    );

    expect(el.querySelector('[data-testid="passage-status"]')!.textContent).toContain('Pronto');
    expect(el.querySelector('[data-testid="passage-notready"]')).toBeNull();
  });

  it('non rende HTML attivo: il testo del documento resta testo', async () => {
    mount();
    await flush([block({ text: '<img src=x onerror=alert(1)><b>grassetto</b>' })]);

    expect(el.querySelector('img')).toBeNull();
    expect(el.querySelector('[data-testid="passage-block"] b')).toBeNull();
    expect(el.querySelector('[data-testid="passage-block"]')!.textContent).toContain(
      '<b>grassetto</b>',
    );
  });

  it('pagina i blocchi con il cursore e non duplica', async () => {
    mount();
    await flush([block({ id: 'b-0', ordinal: 0, text: 'A' })], prep(), '0');
    (el.querySelector('[data-testid="passage-more"]') as HTMLButtonElement).click();
    const req = http.expectOne((r) => r.url === BASE + '/blocks');
    expect(req.request.params.get('cursor')).toBe('0');
    req.flush(page([block({ id: 'b-0', text: 'A' }), block({ id: 'b-1', ordinal: 1, text: 'B' })]));
    await settle();

    expect(el.querySelectorAll('[data-testid="passage-block"]').length).toBe(2);
    expect(el.querySelector('[data-testid="passage-more"]')).toBeNull();
  });

  it('evidenzia la citazione esatta del passaggio richiesto e collega l’originale', async () => {
    mount({ blockId: 'b-0', start: 6, end: 15 });
    await flush([block({ text: 'Primo paragrafo.' })]);
    const req = http.expectOne((r) => r.url === `${BASE}/passages/b-0`);
    expect(req.request.params.get('start')).toBe('6');
    expect(req.request.params.get('end')).toBe('15');
    const passage: PassageView = {
      block: block({ text: 'Primo paragrafo.', page: 2 }),
      preparationId: 'g-1',
      processingStatus: 'Extracting',
      isReady: false,
      quote: { start: 6, end: 15, before: 'Primo ', text: 'paragrafo', after: '.' },
      original: { originalName: 'cap.md', downloadUrl: '/x' },
    };
    req.flush(passage);
    await settle();

    const mark = el.querySelector('[data-testid="passage-quote"] mark')!;
    expect(mark.textContent).toBe('paragrafo');
    expect(el.querySelector('[data-testid="passage-quote"]')!.textContent).toContain(
      'Primo paragrafo.',
    );
    (el.querySelector('[data-testid="passage-download"]') as HTMLButtonElement).click();
    expect(downloads).toBe(1);
  });

  it('un range rifiutato (422) mostra un errore accessibile senza evidenziare nulla', async () => {
    mount({ blockId: 'b-0', start: 5, end: 3 });
    await flush([block()]);
    http
      .expectOne((r) => r.url === `${BASE}/passages/b-0`)
      .flush({ code: 'invalid_range' }, { status: 422, statusText: 'x' });
    await settle();

    expect(el.querySelector('[data-testid="passage-error"]')!.getAttribute('role')).toBe('alert');
    expect(el.querySelector('mark')).toBeNull();
  });

  it('contenuto non ancora letto (409) è dichiarato, non simulato', async () => {
    mount();
    http
      .expectOne(BASE)
      .flush(prep({ processingStatus: 'Parsing', contentState: 'NotParsed', blockCount: null }));
    await settle();
    http
      .expectOne((r) => r.url === BASE + '/blocks')
      .flush({ code: 'parsing_not_completed' }, { status: 409, statusText: 'x' });
    await settle();

    expect(el.querySelectorAll('[data-testid="passage-block"]').length).toBe(0);
    expect(el.querySelector('[data-testid="passage-unparsed"]')).not.toBeNull();
  });

  it('elenca le segnalazioni del parsing', async () => {
    mount();
    await flush(
      [block()],
      prep({
        issues: [
          {
            code: 'ocr_needed',
            severity: 'Warning',
            page: 2,
            blockId: null,
            reason: 'Pagina 2 senza testo',
            affectsCompleteness: true,
          },
        ],
      }),
    );

    const issue = el.querySelector('[data-testid="passage-issue"]')!;
    expect(issue.textContent).toContain('Pagina 2 senza testo');
    expect(issue.textContent).toContain('pagina 2');
  });

  describe('per formato (PDF, DOCX, Markdown, TXT)', () => {
    const cases = [
      {
        format: 'PDF',
        blocks: [
          block({ id: 'p-0', ordinal: 0, type: 'Heading', text: 'Capitolato', page: 1, bbox: { values: [72, 700.25, 300, 720], coordinateSystem: 'pdf-points' } }),
          block({ id: 'p-1', ordinal: 1, text: 'Penale giornaliera', page: 1, bbox: { values: [72, 650, 520, 690], coordinateSystem: 'pdf-points' } }),
          block({ id: 'p-2', ordinal: 2, text: 'Collaudo finale', page: 2, bbox: { values: [72, 600, 520, 640], coordinateSystem: 'pdf-points' } }),
        ],
        expected: [
          ['Pagina 1', 'area 72, 700.3, 300, 720'],
          ['Pagina 1', 'area 72, 650, 520, 690'],
          ['Pagina 2', 'area 72, 600, 520, 640'],
        ],
        absent: ['righe', 'Posizione non disponibile'],
      },
      {
        format: 'DOCX',
        blocks: [
          block({ id: 'w-0', ordinal: 0, type: 'Heading', text: 'Requisiti', headingPath: ['Requisiti'] }),
          block({ id: 'w-1', ordinal: 1, text: 'Il sistema deve esportare.', headingPath: ['Requisiti', 'Esportazione'] }),
        ],
        expected: [['Requisiti'], ['Requisiti › Esportazione']],
        absent: ['Pagina', 'area', 'righe'],
      },
      {
        format: 'Markdown',
        blocks: [
          block({ id: 'm-0', ordinal: 0, type: 'Heading', text: 'Scope', headingPath: ['Scope'], lines: { start: 1, end: 1 } }),
          block({ id: 'm-1', ordinal: 1, text: 'Elenco su due righe', headingPath: ['Scope'], lines: { start: 3, end: 4 } }),
        ],
        expected: [['Scope', 'riga 1'], ['Scope', 'righe 3-4']],
        absent: ['Pagina', 'area'],
      },
      {
        format: 'TXT',
        blocks: [
          block({ id: 't-0', ordinal: 0, text: 'Prima riga', lines: { start: 1, end: 1 } }),
          block({ id: 't-1', ordinal: 1, text: 'Senza posizione' }),
        ],
        expected: [['riga 1'], ['Posizione non disponibile']],
        absent: ['Pagina', 'area', '›'],
      },
    ];

    for (const c of cases) {
      it(`${c.format}: blocchi in ordine di lettura con i soli locator disponibili`, async () => {
        mount();
        await flush(c.blocks, prep({ blockCount: c.blocks.length }));
        const items = Array.from(el.querySelectorAll('[data-testid="passage-block"]'));
        expect(items.length).toBe(c.blocks.length);
        c.blocks.forEach((b, i) => {
          expect(items[i].textContent).toContain(b.text);
          for (const part of c.expected[i]) expect(items[i].textContent).toContain(part);
        });
        // nessun locator inventato: un blocco privo di un locator non lo mostra
        const lastText = items[items.length - 1].textContent!;
        if (c.format === 'TXT' || c.format === 'DOCX') {
          for (const part of c.absent) expect(lastText).not.toContain(part);
        }
      });
    }

    it('PDF multipagina: i blocchi di pagine diverse restano nell’ordine ricevuto, senza fusione', async () => {
      mount();
      await flush(cases[0].blocks, prep({ blockCount: 3 }));
      const texts = Array.from(el.querySelectorAll('[data-testid="passage-block"]')).map(
        (n) => n.textContent!,
      );
      expect(texts.length).toBe(3);
      expect(texts[0]).toContain('Capitolato');
      expect(texts[0]).toContain('Pagina 1');
      expect(texts[1]).toContain('Pagina 1');
      expect(texts[2]).toContain('Pagina 2');
      expect(texts.filter((t) => t.includes('Pagina 2')).length).toBe(1);
    });

    for (const c of cases) {
      it(`${c.format}: il passaggio aperto mostra la posizione disponibile e l’originale`, async () => {
        mount({ blockId: c.blocks[1].id, start: 0, end: 3 });
        await flush(c.blocks, prep());
        const req = http.expectOne((r) => r.url === `${BASE}/passages/${c.blocks[1].id}`);
        const view: PassageView = {
          block: c.blocks[1],
          preparationId: 'g-1',
          processingStatus: 'Ready',
          isReady: true,
          quote: {
            start: 0,
            end: 3,
            before: '',
            text: c.blocks[1].text.slice(0, 3),
            after: c.blocks[1].text.slice(3),
          },
          original: { originalName: `doc.${c.format.toLowerCase()}`, downloadUrl: '/x' },
        };
        req.flush(view);
        await settle();
        const open = el.querySelector('[data-testid="passage-open"]')!;
        for (const part of c.expected[1]) expect(open.textContent).toContain(part);
        expect(open.querySelector('mark')!.textContent).toBe(c.blocks[1].text.slice(0, 3));
        expect(el.querySelector('[data-testid="passage-download"]')!.textContent).toContain(
          `doc.${c.format.toLowerCase()}`,
        );
      });
    }

    it('una tabella non rompe il rendering e mostra tipo e pagina', async () => {
      mount();
      await flush([block({ id: 'x-0', type: 'Table', text: 'A | B', page: 4, tableCells: [[{ text: 'A' }]] })]);
      const text = el.querySelector('[data-testid="passage-block"]')!.textContent!;
      expect(text).toContain('Table');
      expect(text).toContain('Pagina 4');
    });
  });
});
