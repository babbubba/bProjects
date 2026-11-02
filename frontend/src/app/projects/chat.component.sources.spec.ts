import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CHAT_POLL_MS, ChatComponent } from './chat.component';
import { MessageView } from './project-api.service';

const P = 'p-1';
const MESSAGES = `/api/projects/${P}/messages`;
const EVIDENCE = `/api/projects/${P}/evidence`;
const PREP = `/api/projects/${P}/documents/doc-1/versions/ver-1/preparations/prep-1`;

const message = (over: Partial<MessageView>): MessageView => ({
  id: 'm-2',
  sequence: 2,
  role: 'assistant',
  text: '',
  createdAt: '2030-01-01T10:00:00Z',
  authorId: null,
  authorDisplayName: null,
  agentId: 'a',
  chatTurnId: null,
  replyToMessageId: null,
  ...over,
});

const docSource = (n: number, over: Record<string, unknown> = {}) => ({
  sourceId: `s-${n}`,
  kind: 'DocumentPassage',
  role: 'Quote',
  citationGroupId: 'g-1',
  part: n - 1,
  documentId: 'doc-1',
  documentVersionId: 'ver-1',
  preparationId: 'prep-1',
  artifactId: 'art-1',
  blockId: `b-${n}`,
  start: 0,
  end: 4,
  quote: `parte ${n}`,
  ...over,
});

const evidence = (sources: unknown[]) => ({
  evidenceViewVersion: '1.1.0',
  evidence: {
    id: 'e-1',
    versionId: 'ev-1',
    version: 1,
    isCurrent: true,
    projectId: P,
    ordinal: 1,
    key: 'e1',
    kind: 'Requirement',
    polarity: 'Affirmed',
    certainty: 'Stated',
    statement: 'x',
    origin: {
      kind: 'Document',
      documentId: 'doc-1',
      documentVersionId: 'ver-1',
      preparationId: 'prep-1',
    },
    content: {
      schemaVersion: 'evidence-1.1.0',
      kindRationale: null,
      structure: {},
      qualifiers: [],
      sources,
    },
  },
});

describe('ChatComponent: citazioni documentali (002 T063)', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  async function open(text: string) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: CHAT_POLL_MS, useValue: 0 },
      ],
    });
    fixture = TestBed.createComponent(ChatComponent);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement;
    fixture.componentRef.setInput('projectId', P);
    fixture.detectChanges();
    await Promise.resolve();
    http
      .expectOne((r) => r.url === MESSAGES && r.method === 'GET')
      .flush({ items: [message({ text })], nextAfterSequence: 2 });
    await settle();
    http
      .expectOne((r) => r.url === MESSAGES && r.method === 'GET')
      .flush({ items: [], nextAfterSequence: null });
    await settle();
  }

  const chips = () =>
    Array.from(el.querySelectorAll<HTMLButtonElement>('[data-testid=chat-source]'));
  /** L'API evidenze è caricata con import dinamico al primo clic: attende la richiesta senza fissare un ritardo. */
  const evidenceRequest = async () => {
    for (let i = 0; i < 50; i++) {
      const found = http.match((r) => r.url === EVIDENCE);
      if (found.length) return found[0];
      await settle();
    }
    throw new Error('richiesta evidenze non inviata');
  };
  const flushEvidence = async (items: unknown[]) => {
    const req = await evidenceRequest();
    expect(req.request.params.get('originKind')).toBe('Document');
    expect(req.request.params.get('viewVersion')).toBe('1.1.0');
    req.flush({ items });
    await settle();
  };

  afterEach(() => http.verify());

  it('rende i marcatori come pulsanti F1, F2 e il testo restante escapato', async () => {
    await open('Il <b>sistema</b> esporta [fonte:s-1] e invia [fonte:s-2][fonte:s-1].');
    expect(chips().map((c) => c.textContent!.trim())).toEqual(['F1', 'F2', 'F1']);
    expect(el.querySelector('.msg-ai b')).toBeNull();
    expect(el.querySelector('.msg-text')!.textContent).toContain('Il <b>sistema</b> esporta');
    expect(el.querySelector('.msg-text')!.textContent).not.toContain('[fonte:');
  });

  it('un messaggio senza marcatori resta identico', async () => {
    await open('Risposta semplice.');
    expect(chips().length).toBe(0);
    expect(el.querySelector('.msg-text')!.textContent!.trim()).toBe('Risposta semplice.');
  });

  it('il clic risolve la fonte e apre tutte le parti del suo gruppo di citazione', async () => {
    await open('Vale [fonte:s-2].');
    chips()[0].click();
    await settle();
    await flushEvidence([evidence([docSource(1), docSource(2)])]);
    const parts = el.querySelectorAll('[data-testid=source-part]');
    expect(parts.length).toBe(2);
    expect(parts[0].textContent).toContain('parte 1');
    expect(parts[1].textContent).toContain('parte 2');
    // Le parti sono aperte (passaggio per ciascun blocco), senza fonderle.
    expect(http.match((r) => r.url === PREP).length).toBe(2);
    http
      .match((r) => r.url.includes('/passages/'))
      .forEach((r) => r.flush({}, { status: 404, statusText: 'x' }));
  });

  it('fonte non risolvibile: avviso chiaro, nessun passaggio', async () => {
    await open('Vale [fonte:s-9].');
    chips()[0].click();
    await settle();
    await flushEvidence([evidence([docSource(1)])]);
    expect(el.querySelector('[data-testid=chat-source-notice]')!.textContent).toContain(
      'non è più consultabile',
    );
    expect(el.querySelector('app-document-passage')).toBeNull();
  });

  it('errore di rete nella lettura: avviso senza perdere la risposta', async () => {
    await open('Vale [fonte:s-1].');
    chips()[0].click();
    await settle();
    (await evidenceRequest()).flush('x', { status: 500, statusText: 'Err' });
    await settle();
    expect(el.querySelector('[data-testid=chat-source-notice]')!.textContent).toContain(
      'Impossibile leggere la fonte F1',
    );
    expect(el.querySelector('.msg-text')!.textContent).toContain('Vale');
  });
});
