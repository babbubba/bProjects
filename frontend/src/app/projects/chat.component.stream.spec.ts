import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { CHAT_POLL_MS, ChatComponent } from './chat.component';
import { ChatTurnView, MessageView } from './project-api.service';
import { ProjectEventsService, ProjectStreamHandlers } from './project-events.service';

const user: MessageView = {
  id: 'm-1',
  sequence: 1,
  role: 'user',
  text: 'Ciao',
  createdAt: '2030-01-01T10:00:00Z',
  authorId: 'u-1',
  authorDisplayName: 'Pm',
  agentId: null,
  chatTurnId: 't-1',
  replyToMessageId: null,
};

const reply: MessageView = {
  ...user,
  id: 'm-2',
  sequence: 2,
  role: 'assistant',
  text: 'Risposta persistita',
  authorId: null,
  authorDisplayName: null,
  agentId: 'a',
  replyToMessageId: 'm-1',
};

function turn(over: Partial<ChatTurnView> = {}): ChatTurnView {
  return {
    id: 't-1',
    triggerMessageId: 'm-1',
    sourceCutoffSequence: 1,
    status: 'Running',
    requestedAt: '2030-01-01T10:00:00Z',
    startedAt: null,
    completedAt: null,
    deadlineAt: '2030-01-01T10:10:00Z',
    attemptCount: 1,
    maxAttempts: 3,
    nextAttemptAt: null,
    resultMessageId: null,
    error: null,
    ...over,
  };
}

describe('ChatComponent SSE', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let handlers: ProjectStreamHandlers;
  const authed = signal(true);
  const open = vi.fn();
  const close = vi.fn();
  let persisted: MessageView[];
  let turnState: ChatTurnView;
  let turnGets: number;

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  /** Risponde a ogni GET pendente (solo lettura) con lo stato corrente del "server". */
  const drain = async () => {
    for (let i = 0; i < 6; i++) {
      await settle();
      for (const r of http.match(() => true)) {
        expect(r.request.method).toBe('GET'); // nessun comando da apertura/riconnessione
        if (r.request.url.endsWith('/messages')) {
          const items = r.request.url.includes('/p-1/') ? persisted : [];
          r.flush({ items, nextAfterSequence: items.at(-1)?.sequence ?? null });
        } else {
          turnGets++;
          r.flush(turnState);
        }
      }
    }
  };

  const delta = (attemptCount: number, deltaIndex: number, text: string, chatTurnId = 't-1') =>
    handlers.onEvent({
      name: 'chat.delta',
      data: { chatTurnId, attemptCount, deltaIndex, text },
    });

  const streamed = () => el.querySelector('.msg-streaming .msg-text')?.textContent ?? null;

  async function setup(pollMs = 0) {
    authed.set(true);
    open.mockReset();
    close.mockReset();
    open.mockImplementation((_id: string, h: ProjectStreamHandlers) => {
      handlers = h;
      return close;
    });
    persisted = [user];
    turnState = turn();
    turnGets = 0;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: CHAT_POLL_MS, useValue: pollMs },
        { provide: ProjectEventsService, useValue: { open } },
        { provide: AuthService, useValue: { isAuthenticated: authed.asReadonly() } },
      ],
    });
    fixture = TestBed.createComponent(ChatComponent);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement;
    fixture.componentRef.setInput('projectId', 'p-1');
    fixture.detectChanges();
    await drain();
  }

  afterEach(() => {
    fixture.destroy();
    http.verify();
  });

  it('opens a single stream for the project', async () => {
    await setup();
    expect(open).toHaveBeenCalledTimes(1);
    expect(open.mock.calls[0][0]).toBe('p-1');
  });

  it('shows provisional text, ignoring duplicate and out-of-order deltas', async () => {
    await setup();
    delta(1, 0, 'Ciao ');
    delta(1, 0, 'Ciao '); // duplicato
    delta(1, 2, 'perso '); // fuori ordine (salto)
    delta(1, 1, 'mondo');
    await settle();
    expect(streamed()).toBe('Ciao mondo');
    expect(el.querySelector('.msg-streaming .ai-badge')?.textContent).toContain('non validata');
  });

  it('resets the buffer on a newer attempt and ignores an older one', async () => {
    await setup();
    delta(1, 0, 'vecchio');
    delta(2, 0, 'nuovo ');
    delta(1, 1, 'ritardatario');
    delta(2, 1, 'testo');
    await settle();
    expect(streamed()).toBe('nuovo testo');
  });

  it('discards the buffer on reconnection and reconciles via GET only', async () => {
    await setup();
    delta(1, 0, 'parziale');
    await settle();
    expect(streamed()).toBe('parziale');

    handlers.onConnected();
    await drain();
    expect(streamed()).toBeNull();
    // Dopo la riconnessione il flusso riparte dal primo delta del tentativo.
    delta(1, 0, 'da capo');
    await settle();
    expect(streamed()).toBe('da capo');
  });

  it('replaces the provisional text with the persisted message after Completed', async () => {
    await setup();
    delta(1, 0, 'provvisorio');
    await settle();
    expect(streamed()).toBe('provvisorio');

    persisted = [user, reply];
    handlers.onEvent({
      name: 'chat.state',
      data: turn({ status: 'Completed', resultMessageId: 'm-2' }),
    });
    await drain();

    expect(streamed()).toBeNull();
    const assistant = el.querySelector('[data-role=assistant] .msg-text');
    expect(assistant?.textContent).toBe('Risposta persistita');
    expect(el.querySelector('.turn-badge')?.textContent).toContain('Risposta ricevuta');
  });

  it('drops the provisional text when the turn fails', async () => {
    await setup();
    delta(1, 0, 'parziale');
    handlers.onEvent({
      name: 'chat.state',
      data: turn({ status: 'Failed', error: { code: 'llm_unavailable', message: null } }),
    });
    await settle();
    expect(streamed()).toBeNull();
    expect(el.querySelector('.turn-error')).toBeTruthy();
  });

  it('closes the stream and opens a new one when the project changes', async () => {
    await setup();
    fixture.componentRef.setInput('projectId', 'p-2');
    fixture.detectChanges();
    await drain();
    expect(close).toHaveBeenCalledTimes(1);
    expect(open).toHaveBeenCalledTimes(2);
    expect(open.mock.calls[1][0]).toBe('p-2');
  });

  it('closes the stream on logout and does not reopen it', async () => {
    await setup();
    authed.set(false);
    fixture.detectChanges();
    await drain();
    expect(close).toHaveBeenCalledTimes(1);
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('keeps polling every interval while the stream is down (fallback)', async () => {
    await setup(15);
    handlers.onDisconnected(); // stream non disponibile: resta solo la riconciliazione
    const before = turnGets;
    await new Promise((r) => setTimeout(r, 80));
    await drain();
    expect(turnGets).toBeGreaterThan(before);
  });
});
