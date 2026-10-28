import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CHAT_POLL_MS, ChatComponent } from './chat.component';
import { ChatTurnView, MessageView } from './project-api.service';

const P = 'p-1';
const MESSAGES = `/api/projects/${P}/messages`;

function userMessage(over: Partial<MessageView> = {}): MessageView {
  return {
    id: 'm-1',
    sequence: 1,
    role: 'user',
    text: 'Ciao',
    createdAt: '2030-01-01T10:00:00Z',
    authorId: 'u-1',
    authorDisplayName: 'Pm Uno',
    agentId: null,
    chatTurnId: 't-1',
    replyToMessageId: null,
    ...over,
  };
}

function turn(over: Partial<ChatTurnView> = {}): ChatTurnView {
  return {
    id: 't-1',
    triggerMessageId: 'm-1',
    sourceCutoffSequence: 1,
    status: 'Queued',
    requestedAt: '2030-01-01T10:00:00Z',
    startedAt: null,
    completedAt: null,
    deadlineAt: '2030-01-01T10:10:00Z',
    attemptCount: 0,
    maxAttempts: 3,
    nextAttemptAt: null,
    resultMessageId: null,
    error: null,
    ...over,
  };
}

describe('ChatComponent', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  const type = async (text: string) => {
    const box = el.querySelector('textarea')!;
    box.value = text;
    box.dispatchEvent(new Event('input'));
    await settle();
  };

  const submit = async () => {
    el.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    await Promise.resolve();
    await settle();
  };

  beforeEach(async () => {
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
      .flush({
        items: [],
        nextAfterSequence: null,
      });
    await settle();
  });

  afterEach(() => http.verify());

  it('shows the saved confirmation separately from the AI state', async () => {
    await type('Ciao');
    await submit();
    const post = http.expectOne((r) => r.url === MESSAGES && r.method === 'POST');
    expect(post.request.body.text).toBe('Ciao');
    expect(post.request.body.clientMessageId).toBeTruthy();
    post.flush(
      { message: userMessage(), chatTurnId: 't-1' },
      { status: 201, statusText: 'Created' },
    );
    await settle();

    // Salvato subito, mentre lo stato AI non è ancora noto.
    expect(el.querySelector('.saved-badge')?.textContent).toContain('Salvato');
    expect(el.querySelector('.turn-badge')?.textContent).toContain('in verifica');

    http.expectOne(`/api/projects/${P}/chat-turns/t-1`).flush(turn({ status: 'Running' }));
    await settle();
    expect(el.querySelector('.saved-badge')).toBeTruthy();
    expect(el.querySelector('.turn-badge')?.textContent).toContain('AI al lavoro');
    expect((el.querySelector('textarea') as HTMLTextAreaElement).value).toBe('');
  });

  it('labels assistant replies as unvalidated AI', async () => {
    await type('Ciao');
    await submit();
    http
      .expectOne((r) => r.method === 'POST')
      .flush({ message: userMessage(), chatTurnId: 't-1' }, { status: 201, statusText: 'Created' });
    await settle();
    http
      .expectOne(`/api/projects/${P}/chat-turns/t-1`)
      .flush(turn({ status: 'Completed', resultMessageId: 'm-2' }));
    await Promise.resolve();
    http
      .expectOne((r) => r.url === MESSAGES && r.method === 'GET')
      .flush({
        items: [
          userMessage(),
          userMessage({
            id: 'm-2',
            sequence: 2,
            role: 'assistant',
            text: 'Risposta',
            authorId: null,
            authorDisplayName: null,
            agentId: 'a',
            chatTurnId: 't-1',
            replyToMessageId: 'm-1',
          }),
        ],
        nextAfterSequence: 2,
      });
    await settle();
    http
      .expectOne((r) => r.url === MESSAGES && r.method === 'GET')
      .flush({
        items: [],
        nextAfterSequence: null,
      });
    await settle();

    expect(el.querySelector('.ai-badge')?.textContent).toContain('non validata');
    expect(el.querySelectorAll('[data-role=user]').length).toBe(1);
    expect(el.querySelector('.turn-badge')?.textContent).toContain('Risposta ricevuta');
  });

  it('keeps the text when the POST fails definitively and explains why', async () => {
    await type('Testo lungo da non perdere');
    await submit();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(
        { errors: { text: ['Il messaggio non è valido (422).'] } },
        { status: 422, statusText: 'Unprocessable' },
      );
    await settle();

    expect((el.querySelector('textarea') as HTMLTextAreaElement).value).toBe(
      'Testo lungo da non perdere',
    );
    expect(el.querySelector('.send-error')?.textContent).toContain(
      'Il messaggio non è valido (422).',
    );
    expect(el.querySelectorAll('[data-role=user]').length).toBe(0);
  });

  it('reconciles an uncertain POST with the same clientMessageId (no duplicate)', async () => {
    await type('Messaggio');
    await submit();
    const first = http.expectOne((r) => r.method === 'POST');
    const id = first.request.body.clientMessageId;
    first.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown' });
    await settle();

    expect((el.querySelector('textarea') as HTMLTextAreaElement).value).toBe('Messaggio');
    expect(el.querySelector('.send-error')?.textContent).toContain('Non sappiamo se');
    expect(el.querySelector('.send-error button')).toBeTruthy();

    el.querySelector<HTMLButtonElement>('.send-error button')!.click();
    await Promise.resolve();
    const second = http.expectOne((r) => r.method === 'POST');
    expect(second.request.body.clientMessageId).toBe(id);
    expect(second.request.body.text).toBe('Messaggio');
    // 200: replay identico, stesso messaggio e stesso turno.
    second.flush(
      { message: userMessage({ text: 'Messaggio' }), chatTurnId: 't-1' },
      { status: 200, statusText: 'OK' },
    );
    await settle();
    http.expectOne(`/api/projects/${P}/chat-turns/t-1`).flush(turn());
    await settle();

    expect(el.querySelectorAll('[data-role=user]').length).toBe(1);
    expect(el.querySelector('.send-error')?.hasAttribute('hidden')).toBe(true);
  });

  it('does not resend automatically when the turn is Failed and shows an understandable error', async () => {
    await type('Ciao');
    await submit();
    http
      .expectOne((r) => r.method === 'POST')
      .flush({ message: userMessage(), chatTurnId: 't-1' }, { status: 201, statusText: 'Created' });
    await settle();
    http
      .expectOne(`/api/projects/${P}/chat-turns/t-1`)
      .flush(turn({ status: 'Failed', error: { code: 'llm_unavailable', message: null } }));
    await settle();

    expect(el.querySelector('.saved-badge')).toBeTruthy();
    expect(el.querySelector('.turn-error')?.textContent).toContain(
      'modello AI non è al momento raggiungibile',
    );
    expect(el.querySelector('.turn-error')?.textContent).toContain(
      'non verrà reinviato automaticamente',
    );
    // Nessun POST né ulteriore richiesta automatica: http.verify() in afterEach lo garantisce.
  });

  it('context_overflow: spiega il limite con i valori misurati e indica cosa fare, senza il generico rifiuto', async () => {
    await type('Ciao');
    await submit();
    http
      .expectOne((r) => r.method === 'POST')
      .flush({ message: userMessage(), chatTurnId: 't-1' }, { status: 201, statusText: 'Created' });
    await settle();
    http.expectOne(`/api/projects/${P}/chat-turns/t-1`).flush(
      turn({
        status: 'Failed',
        error: {
          code: 'context_overflow',
          message:
            'Contesto di 31000 token oltre il budget di input di 24000 token, non riducibile con la compattazione nativa. Gli originali non sono stati modificati.',
        },
      }),
    );
    await settle();

    const text = el.querySelector('.turn-error')?.textContent ?? '';
    expect(text).toContain('supera il contesto gestibile');
    expect(text).toContain('31000 token');
    expect(text).toContain('24000 token');
    expect(text).toContain('messaggio più recente');
    expect(text).not.toContain('rifiutato la richiesta');
    expect(text).not.toContain('%');
  });

  it('explains a server-side idempotency conflict and uses a new id on the next send', async () => {
    await type('A');
    await submit();
    const first = http.expectOne((r) => r.method === 'POST');
    first.flush({ code: 'idempotency_conflict' }, { status: 409, statusText: 'Conflict' });
    await settle();
    expect(el.querySelector('.send-error')?.textContent).toContain('nuovo invio');

    await submit();
    const second = http.expectOne((r) => r.method === 'POST');
    expect(second.request.body.clientMessageId).not.toBe(first.request.body.clientMessageId);
    second.flush(
      { message: userMessage({ text: 'A' }), chatTurnId: 't-1' },
      { status: 201, statusText: 'Created' },
    );
    await settle();
    http.expectOne(`/api/projects/${P}/chat-turns/t-1`).flush(turn());
  });
});
