import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { contextUsage } from './context-usage.fixtures';
import {
  CONTEXT_USAGE_DEBOUNCE_MS,
  CONTEXT_USAGE_POLL_MS,
  ContextUsageStore,
} from './context-usage.store';
import { ProjectEventsService, ProjectStreamHandlers } from './project-events.service';

const url = (id: string) => `/api/projects/${id}/context-usage`;

describe('ContextUsageStore', () => {
  let store: ContextUsageStore;
  let http: HttpTestingController;
  let handlers: Record<string, ProjectStreamHandlers>;
  let closes: Record<string, ReturnType<typeof vi.fn>>;
  const authed = signal(true);
  const open = vi.fn();

  /** Lascia scadere il debounce (0 ms nei test) e le promesse in sospeso. */
  const tick = async () => {
    await new Promise((resolve) => setTimeout(resolve, 5));
    await Promise.resolve();
  };

  beforeEach(() => {
    authed.set(true);
    handlers = {};
    closes = {};
    open.mockReset();
    open.mockImplementation((id: string, h: ProjectStreamHandlers) => {
      handlers[id] = h;
      closes[id] = vi.fn();
      return closes[id];
    });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: CONTEXT_USAGE_DEBOUNCE_MS, useValue: 0 },
        { provide: CONTEXT_USAGE_POLL_MS, useValue: 30 },
        { provide: ProjectEventsService, useValue: { open } },
        { provide: AuthService, useValue: { isAuthenticated: authed.asReadonly() } },
      ],
    });
    store = TestBed.inject(ContextUsageStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('apre il progetto con un solo GET anche se lo stream segnala subito la connessione', async () => {
    store.watch('p-1');
    handlers['p-1'].onConnected();
    await tick();

    http.expectOne(url('p-1')).flush(contextUsage());
    await tick();

    expect(store.usage()?.runtime.modelId).toBe('qwen3-32b');
    expect(store.failed()).toBe(false);
    expect(open).toHaveBeenCalledTimes(1); // nessuna connessione SSE aggiuntiva oltre a quella del subscriber
  });

  it('ricarica a ogni chat.state e processing.state, ignora i delta provvisori', async () => {
    store.watch('p-1');
    await tick();
    http.expectOne(url('p-1')).flush(contextUsage());
    await tick();

    handlers['p-1'].onEvent({ name: 'chat.delta', data: { text: 'x' } });
    await tick();
    http.expectNone(url('p-1'));

    handlers['p-1'].onEvent({ name: 'chat.state', data: { status: 'Running' } });
    await tick();
    http.expectOne(url('p-1')).flush(contextUsage());
    await tick();

    handlers['p-1'].onEvent({ name: 'processing.state', data: { status: 'Completed' } });
    await tick();
    http.expectOne(url('p-1')).flush(contextUsage());
  });

  it('riconnessione SSE: rifà il GET', async () => {
    store.watch('p-1');
    await tick();
    http.expectOne(url('p-1')).flush(contextUsage());
    await tick();

    handlers['p-1'].onDisconnected();
    handlers['p-1'].onConnected();
    await tick();

    http.expectOne(url('p-1')).flush(contextUsage());
  });

  it('raggruppa una raffica di eventi in un solo GET', async () => {
    store.watch('p-1');
    await tick();
    http.expectOne(url('p-1')).flush(contextUsage());
    await tick();

    for (let i = 0; i < 5; i++) handlers['p-1'].onEvent({ name: 'chat.state', data: {} });
    store.refresh();
    await tick();

    http.expectOne(url('p-1')).flush(contextUsage());
  });

  it('un evento durante un GET in corso produce un solo ricaricamento successivo', async () => {
    store.watch('p-1');
    await tick();
    const first = http.expectOne(url('p-1'));

    handlers['p-1'].onEvent({ name: 'chat.state', data: {} });
    await tick();
    http.expectNone(url('p-1')); // il GET è già in corso: nessuna richiesta parallela

    first.flush(contextUsage({ generatedAt: '2030-01-01T11:00:00Z' }));
    await tick();

    http.expectOne(url('p-1')).flush(contextUsage({ generatedAt: '2030-01-01T12:00:00Z' }));
    await tick();
    expect(store.usage()?.generatedAt).toBe('2030-01-01T12:00:00Z');
  });

  it('refresh senza progetto seguito non fa nulla', async () => {
    store.refresh();
    await tick();
    http.expectNone(url('p-1'));
  });

  it('cambio progetto: azzera lo stato e scarta la risposta tardiva del precedente', async () => {
    const closeA = store.watch('p-A');
    await tick();
    const lateA = http.expectOne(url('p-A'));

    closeA();
    expect(closes['p-A']).toHaveBeenCalled();
    store.watch('p-B');
    expect(store.usage()).toBeNull();
    await tick();
    http
      .expectOne(url('p-B'))
      .flush(contextUsage({ runtime: { ...contextUsage().runtime, modelId: 'modello-B' } }));
    await tick();
    expect(store.usage()?.runtime.modelId).toBe('modello-B');

    lateA.flush(contextUsage({ runtime: { ...contextUsage().runtime, modelId: 'modello-A' } }));
    await tick();

    expect(store.usage()?.runtime.modelId).toBe('modello-B'); // nessuna contaminazione fra progetti
  });

  it('un errore tardivo del progetto precedente non sporca lo stato del nuovo', async () => {
    const closeA = store.watch('p-A');
    await tick();
    const lateA = http.expectOne(url('p-A'));
    closeA();
    store.watch('p-B');
    await tick();
    http.expectOne(url('p-B')).flush(contextUsage());
    await tick();

    lateA.flush('errore', { status: 500, statusText: 'Server Error' });
    await tick();

    expect(store.failed()).toBe(false);
    expect(store.usage()).not.toBeNull();
  });

  it('GET fallito: nessun valore precedente presentato come attuale, poi si riprende', async () => {
    store.watch('p-1');
    await tick();
    http.expectOne(url('p-1')).flush(contextUsage());
    await tick();
    expect(store.usage()).not.toBeNull();

    handlers['p-1'].onEvent({ name: 'chat.state', data: {} });
    await tick();
    http.expectOne(url('p-1')).flush('offline', { status: 503, statusText: 'Service Unavailable' });
    await tick();

    expect(store.failed()).toBe(true);
    expect(store.usage()).toBeNull();

    handlers['p-1'].onEvent({ name: 'chat.state', data: {} });
    await tick();
    http.expectOne(url('p-1')).flush(contextUsage());
    await tick();

    expect(store.failed()).toBe(false);
    expect(store.usage()).not.toBeNull();
  });

  it('la chiusura ferma gli aggiornamenti e libera la sottoscrizione SSE', async () => {
    const close = store.watch('p-1');
    close();
    await tick();

    http.expectNone(url('p-1'));
    expect(closes['p-1']).toHaveBeenCalledTimes(1);
    store.refresh();
    await tick();
    http.expectNone(url('p-1'));
  });

  it('senza sessione non apre lo stream ma carica comunque il monitor', async () => {
    authed.set(false);
    store.watch('p-1');
    await tick();

    http.expectOne(url('p-1')).flush(contextUsage());
    expect(open).not.toHaveBeenCalled();
  });

  describe('riconciliazione dopo perdita eventi o logout (T065)', () => {
    const settle = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
    /** Risponde a tutti i GET aperti (anche quelli accodati dal polling) finché non ne restano. */
    const drain = async (id = 'p-1') => {
      for (let i = 0; i < 4; i++) {
        const open = http.match(url(id));
        open.forEach((r) => {
          expect(r.request.method).toBe('GET');
          r.flush(contextUsage());
        });
        await tick();
      }
    };

    it('document.state e execution.regime-changed fanno rifare il GET; mai un POST', async () => {
      store.watch('p-1');
      await tick();
      http.expectOne(url('p-1')).flush(contextUsage());
      await tick();

      handlers['p-1'].onEvent({ name: 'document.state', data: { state: 'Ready' } });
      await tick();
      http.expectOne(url('p-1')).flush(contextUsage());
      await tick();

      handlers['p-1'].onEvent({ name: 'execution.regime-changed', data: { rootId: 'r' } });
      await tick();
      const req = http.expectOne(url('p-1'));
      expect(req.request.method).toBe('GET');
      req.flush(contextUsage());
    });

    it('dopo la perdita dello stream sonda con GET periodici finché lo stream non torna', async () => {
      store.watch('p-1');
      await tick();
      http.expectOne(url('p-1')).flush(contextUsage());
      await tick();

      handlers['p-1'].onDisconnected();
      await settle(50);
      const polled = http.match(url('p-1'));
      expect(polled.length).toBeGreaterThanOrEqual(1);
      polled.forEach((r) => {
        expect(r.request.method).toBe('GET');
        r.flush(contextUsage());
      });
      await drain();

      handlers['p-1'].onConnected(); // riconnesso: un GET di riconciliazione, poi basta polling
      await tick();
      await drain();
      await settle(80);
      http.expectNone(url('p-1'));
    });

    it('primo tentativo dello stream fallito: il polling parte comunque', async () => {
      store.watch('p-1');
      await tick();
      http.expectOne(url('p-1')).flush(contextUsage());
      await tick();

      handlers['p-1'].onUnavailable?.();
      await settle(50);

      const polled = http.match(url('p-1'));
      expect(polled.length).toBeGreaterThanOrEqual(1);
      polled.forEach((r) => r.flush(contextUsage()));
      await drain();
      handlers['p-1'].onConnected(); // chiude il polling per non lasciare timer
    });

    it('stream terminato (logout): nessun GET finché la sessione manca, poi riapre lo stream e riconcilia', async () => {
      store.watch('p-1');
      await tick();
      http.expectOne(url('p-1')).flush(contextUsage());
      await tick();
      expect(open).toHaveBeenCalledTimes(1);

      authed.set(false);
      handlers['p-1'].onTerminated?.();
      await settle(80);
      http.expectNone(url('p-1'));
      expect(open).toHaveBeenCalledTimes(1);

      authed.set(true); // ritorno
      await settle(50);
      expect(open).toHaveBeenCalledTimes(2);
      await drain();
      handlers['p-1'].onConnected();
      await drain();
    });

    it('senza sessione al watch: nessuno stream, il ritorno della sessione apre lo stream', async () => {
      authed.set(false);
      store.watch('p-1');
      await tick();
      http.expectOne(url('p-1')).flush(contextUsage());
      expect(open).not.toHaveBeenCalled();

      authed.set(true);
      await settle(50);
      expect(open).toHaveBeenCalledTimes(1);
      await drain();
      handlers['p-1'].onConnected();
      await drain();
    });

    it('la chiusura ferma il polling', async () => {
      const close = store.watch('p-1');
      await tick();
      http.expectOne(url('p-1')).flush(contextUsage());
      await tick();
      handlers['p-1'].onDisconnected();
      close();
      await settle(80);

      http.expectNone(url('p-1'));
    });

    it('il cambio progetto non eredita il polling del precedente', async () => {
      const closeA = store.watch('p-A');
      await tick();
      http.expectOne(url('p-A')).flush(contextUsage());
      handlers['p-A'].onDisconnected();
      closeA();
      store.watch('p-B');
      await tick();
      http.expectOne(url('p-B')).flush(contextUsage());
      await settle(80);

      http.expectNone(url('p-A'));
      http.expectNone(url('p-B'));
    });
  });
});

describe('CONTEXT_USAGE_POLL_MS', () => {
  it('predefinito: 2 s', () => {
    TestBed.configureTestingModule({});
    expect(TestBed.inject(CONTEXT_USAGE_POLL_MS)).toBe(2000);
  });
});
