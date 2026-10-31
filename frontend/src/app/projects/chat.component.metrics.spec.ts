import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { CHAT_POLL_MS, ChatComponent } from './chat.component';
import { contextUsage, metrics } from './context-usage.fixtures';
import { ContextUsageStore } from './context-usage.store';
import { ChatTurnView, InferenceMetrics, MessageView } from './project-api.service';
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

const reply = (m?: InferenceMetrics | null): MessageView => ({
  ...user,
  id: 'm-2',
  sequence: 2,
  role: 'assistant',
  text: 'Risposta persistita',
  authorId: null,
  authorDisplayName: null,
  agentId: 'a',
  replyToMessageId: 'm-1',
  ...(m === undefined ? {} : { metrics: m }),
});

const turn = (over: Partial<ChatTurnView> = {}): ChatTurnView => ({
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
});

describe('ChatComponent metriche di inferenza', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let handlers: ProjectStreamHandlers;
  let persisted: MessageView[];
  let turnState: ChatTurnView;
  let store: ContextUsageStore;
  const open = vi.fn();

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  /** Risponde a ogni GET pendente con lo stato corrente del «server». */
  const drain = async () => {
    for (let i = 0; i < 6; i++) {
      await settle();
      for (const r of http.match(() => true)) {
        if (r.request.url.endsWith('/messages')) {
          r.flush({ items: persisted, nextAfterSequence: persisted.at(-1)?.sequence ?? null });
        } else {
          r.flush(turnState);
        }
      }
    }
  };

  const line = (selector: string) =>
    [...el.querySelectorAll(`${selector} app-inference-metrics .inference-metrics > *`)]
      .map(
        (n) => n.querySelector('[aria-hidden=true]')?.textContent?.trim() ?? n.textContent?.trim(),
      )
      .join(' ')
      .replace(/\s+/g, ' ');

  async function setup(
    messages: MessageView[],
    state: ChatTurnView = turn({ status: 'Completed' }),
  ) {
    open.mockReset();
    open.mockImplementation((_id: string, h: ProjectStreamHandlers) => {
      handlers = h;
      return () => undefined;
    });
    persisted = messages;
    turnState = state;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: CHAT_POLL_MS, useValue: 0 },
        { provide: ProjectEventsService, useValue: { open } },
        { provide: AuthService, useValue: { isAuthenticated: signal(true).asReadonly() } },
      ],
    });
    store = TestBed.inject(ContextUsageStore);
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

  it('sotto ogni messaggio assistant mostra le metriche misurate, non sotto quelli del PM', async () => {
    await setup([user, reply(metrics())]);

    expect(el.querySelectorAll('[data-role=assistant] app-inference-metrics')).toHaveLength(1);
    expect(el.querySelector('[data-role=user] app-inference-metrics')).toBeNull();
    expect(line('[data-role=assistant]')).toBe(
      'CTX 49.152 · Max output 8.192 · Generati 1.347 token',
    );
  });

  it('un messaggio storico con metrics null dichiara «Metriche non disponibili»', async () => {
    await setup([user, reply(null)]);

    expect(
      el.querySelector('[data-role=assistant] [data-testid=metrics-unavailable]'),
    ).not.toBeNull();
    expect(el.querySelector('[data-role=assistant]')!.textContent).not.toMatch(/Generati/);
  });

  it('un messaggio senza il campo metrics (record storico) è trattato allo stesso modo', async () => {
    await setup([user, reply(undefined)]);

    expect(
      el.querySelector('[data-role=assistant] [data-testid=metrics-unavailable]'),
    ).not.toBeNull();
  });

  it('segnala la risposta troncata dal limite di output', async () => {
    await setup([user, reply(metrics({ finishReason: 'length' }))]);
    expect(
      el.querySelector('[data-role=assistant] [data-testid=metrics-truncated]'),
    ).not.toBeNull();
  });

  it('la risposta in streaming mostra la configurazione nota e «generazione in corso…»; poi le metriche definitive', async () => {
    await setup([user], turn());
    store.usage.set(contextUsage());
    handlers.onEvent({
      name: 'chat.delta',
      data: { chatTurnId: 't-1', attemptCount: 1, deltaIndex: 0, text: 'provvisorio' },
    });
    await settle();

    expect(line('[data-role=assistant-streaming]')).toBe(
      'CTX 32.768 · Max output 8.192 (limite richiesto) · generazione in corso…',
    );
    expect(el.querySelector('[data-role=assistant-streaming] [data-testid=metric-gen]')).toBeNull();

    persisted = [
      user,
      reply(metrics({ contextWindowTokens: { value: 32768, source: 'measured' } })),
    ];
    handlers.onEvent({
      name: 'chat.state',
      data: turn({ status: 'Completed', resultMessageId: 'm-2' }),
    });
    turnState = turn({ status: 'Completed', resultMessageId: 'm-2' });
    await drain();

    expect(el.querySelector('[data-role=assistant-streaming]')).toBeNull();
    expect(line('[data-role=assistant]')).toBe(
      'CTX 32.768 · Max output 8.192 · Generati 1.347 token',
    );
  });

  it('un turno fallito non ha messaggio né metriche attribuite', async () => {
    await setup(
      [user],
      turn({ status: 'Failed', error: { code: 'llm_unavailable', message: null } }),
    );

    expect(el.querySelector('.turn-error')).toBeTruthy();
    expect(el.querySelector('app-inference-metrics')).toBeNull();
  });

  it("chiede un nuovo monitor del contesto dopo l'invio e a turno terminato", async () => {
    await setup([user], turn({ status: 'Completed' }));
    const refresh = vi.spyOn(store, 'refresh');

    const box = el.querySelector('textarea')!;
    box.value = 'Nuovo messaggio';
    box.dispatchEvent(new Event('input'));
    await settle();
    el.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    await Promise.resolve();
    await settle();
    http
      .expectOne((r) => r.method === 'POST')
      .flush(
        { message: { ...user, id: 'm-3', sequence: 3, chatTurnId: 't-2' }, chatTurnId: 't-2' },
        { status: 201, statusText: 'Created' },
      );
    await settle();
    expect(refresh).toHaveBeenCalledTimes(1); // subito dopo l'invio

    turnState = turn({ id: 't-2', triggerMessageId: 'm-3', status: 'Completed' });
    await drain();
    expect(refresh).toHaveBeenCalledTimes(2); // turno concluso (riconciliazione senza SSE)
  });
});
