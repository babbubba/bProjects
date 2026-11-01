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
  async function flush(items: BlockView[], p = prep(), nextCursor: string | null = null, ready = false) {
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
    await flush([block()], prep({ processingStatus: 'Ready', isReady: true, contentState: 'Ready' }), null, true);

    expect(el.querySelector('[data-testid="passage-status"]')!.textContent).toContain('Pronto');
    expect(el.querySelector('[data-testid="passage-notready"]')).toBeNull();
  });

  it('non rende HTML attivo: il testo del documento resta testo', async () => {
    mount();
    await flush([block({ text: '<img src=x onerror=alert(1)><b>grassetto</b>' })]);

    expect(el.querySelector('img')).toBeNull();
    expect(el.querySelector('[data-testid="passage-block"] b')).toBeNull();
    expect(el.querySelector('[data-testid="passage-block"]')!.textContent).toContain('<b>grassetto</b>');
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
    expect(el.querySelector('[data-testid="passage-quote"]')!.textContent).toContain('Primo paragrafo.');
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
});
