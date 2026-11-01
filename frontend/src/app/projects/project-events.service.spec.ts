import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import {
  ProjectEventsService,
  ProjectStreamEvent,
  SSE_BACKOFF_MS,
  SSE_IDLE_TIMEOUT_MS,
  SseFrame,
  SseParser,
} from './project-events.service';

function parse(chunks: string[]): SseFrame[] {
  const frames: SseFrame[] = [];
  const parser = new SseParser((f) => frames.push(f));
  chunks.forEach((c) => parser.push(c));
  return frames;
}

describe('SseParser', () => {
  it('parses event/data frames split across arbitrary chunks, ignoring comments', () => {
    const frames = parse([
      ': connected\n\nevent: chat.del',
      'ta\ndata: {"a":1}\n',
      '\n: keep-alive\n\nevent: chat.state\r\ndata: x\r\n',
      'data: y\r\n\r\n',
    ]);
    expect(frames).toEqual([
      { event: 'chat.delta', data: '{"a":1}' },
      { event: 'chat.state', data: 'x\ny' },
    ]);
  });

  it('defaults the event name and drops frames without data', () => {
    expect(parse(['data: 1\n\nevent: x\n\n'])).toEqual([{ event: 'message', data: '1' }]);
  });
});

describe('ProjectEventsService', () => {
  const token = signal<string | null>('tok-1');
  const refresh = vi.fn(async () => true);
  const originalFetch = globalThis.fetch;
  let service: ProjectEventsService;

  const sse = (...frames: string[]) =>
    new Response(
      new ReadableStream<Uint8Array>({
        start(c) {
          const enc = new TextEncoder();
          frames.forEach((f) => c.enqueue(enc.encode(f)));
          c.close();
        },
      }),
      { status: 200, headers: { 'Content-Type': 'text/event-stream' } },
    );

  /** Risposta che resta aperta finché la richiesta non viene annullata. */
  const hanging = (signal: AbortSignal) =>
    new Response(
      new ReadableStream<Uint8Array>({
        start(c) {
          signal.addEventListener('abort', () => c.error(new DOMException('abort', 'AbortError')));
        },
      }),
      { status: 200 },
    );

  const wait = () => new Promise((r) => setTimeout(r, 20));

  beforeEach(() => {
    token.set('tok-1');
    refresh.mockClear();
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { accessToken: token.asReadonly(), refresh } },
        { provide: SSE_BACKOFF_MS, useValue: [] },
      ],
    });
    service = TestBed.inject(ProjectEventsService);
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('uses fetch with the in-memory bearer and no token in the URL', async () => {
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => hanging(init.signal!));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const connected = vi.fn();
    const close = service.open('p 1', {
      onConnected: connected,
      onEvent: () => undefined,
      onDisconnected: () => undefined,
    });
    await wait();

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/projects/p%201/events');
    expect(url).not.toContain('tok-1');
    expect(init.method).toBe('GET');
    expect((init.headers as Record<string, string>)['Authorization']).toBe('Bearer tok-1');
    expect(connected).toHaveBeenCalledTimes(1);
    close();
  });

  it('emits decoded events and reconnects after the stream ends, with GET only', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(async () =>
        sse('event: chat.delta\ndata: {"deltaIndex":0}\n\n', ': keep-alive\n\n'),
      )
      .mockImplementation(async (_u: string, init: RequestInit) => hanging(init.signal!));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const events: ProjectStreamEvent[] = [];
    const connected = vi.fn();
    const disconnected = vi.fn();
    const close = service.open('p', {
      onConnected: connected,
      onEvent: (e) => events.push(e),
      onDisconnected: disconnected,
    });
    await wait();

    expect(events).toEqual([{ name: 'chat.delta', data: { deltaIndex: 0 } }]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(connected).toHaveBeenCalledTimes(2);
    expect(disconnected).toHaveBeenCalledTimes(1);
    for (const [, init] of fetchMock.mock.calls) expect(init.method).toBe('GET');
    close();
  });

  it('stops without fetching after close and when there is no token', async () => {
    const fetchMock = vi.fn(async (_u: string, init: RequestInit) => hanging(init.signal!));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const handlers = {
      onConnected: vi.fn(),
      onEvent: vi.fn(),
      onDisconnected: vi.fn(),
    };
    const close = service.open('p', handlers);
    await wait();
    close();
    close();
    await wait();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    token.set(null);
    service.open('p', handlers);
    await wait();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('refreshes the token on 401 and gives up when refresh fails', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 401 }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    refresh.mockResolvedValueOnce(false);
    service.open('p', { onConnected: vi.fn(), onEvent: vi.fn(), onDisconnected: vi.fn() });
    await wait();
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('shares one physical connection among three subscribers and closes at zero', async () => {
    let aborted = false;
    const fetchMock = vi.fn(async (_u: string, init: RequestInit) => {
      init.signal!.addEventListener('abort', () => (aborted = true));
      return hanging(init.signal!);
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const mk = () => ({ onConnected: vi.fn(), onEvent: vi.fn(), onDisconnected: vi.fn() });
    const [a, b, c] = [mk(), mk(), mk()];
    const closeA = service.open('p', a);
    const closeB = service.open('p', b);
    await wait();
    const closeC = service.open('p', c); // già connesso: riconcilia subito
    await wait();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    for (const h of [a, b, c]) expect(h.onConnected).toHaveBeenCalledTimes(1);

    closeA();
    closeA();
    closeB();
    await wait();
    expect(aborted).toBe(false);
    closeC();
    await wait();
    expect(aborted).toBe(true);

    // Dopo la chiusura a zero subscriber un nuovo open() apre una nuova connessione.
    const closeD = service.open('p', mk());
    await wait();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    closeD();
  });

  it('distributes each event to every subscriber and keeps projects separate', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(async (_u: string, init: RequestInit) => {
        // Resta aperta dopo l'evento: l'evento arriva a connessione già condivisa.
        const enc = new TextEncoder();
        return new Response(
          new ReadableStream<Uint8Array>({
            start(c) {
              setTimeout(() => c.enqueue(enc.encode('event: chat.state\ndata: {"x":1}\n\n')), 10);
              init.signal!.addEventListener('abort', () =>
                c.error(new DOMException('a', 'AbortError')),
              );
            },
          }),
          { status: 200 },
        );
      })
      .mockImplementation(async (_u: string, init: RequestInit) => hanging(init.signal!));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const mk = () => ({ onConnected: vi.fn(), onEvent: vi.fn(), onDisconnected: vi.fn() });
    const [a, b, c, other] = [mk(), mk(), mk(), mk()];
    const closers = [
      service.open('p', a),
      service.open('p', b),
      service.open('p', c),
      service.open('q', other),
    ];
    await wait();

    for (const h of [a, b, c]) {
      expect(h.onEvent).toHaveBeenCalledWith({ name: 'chat.state', data: { x: 1 } });
    }
    expect(other.onEvent).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(2); // una per progetto
    closers.forEach((close) => close());
  });

  it('reopens after a terminal exit (no token) once a token is available', async () => {
    const fetchMock = vi.fn(async (_u: string, init: RequestInit) => hanging(init.signal!));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const h = { onConnected: vi.fn(), onEvent: vi.fn(), onDisconnected: vi.fn() };
    token.set(null);
    const close1 = service.open('p', h);
    await wait();
    expect(fetchMock).not.toHaveBeenCalled();
    close1();
    token.set('tok-2');
    const close2 = service.open('p', h);
    await wait();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    close2();
  });
});

describe('ProjectEventsService idle watchdog (fake timers)', () => {
  const IDLE = 45_000;
  const token = signal<string | null>('tok-1');
  const originalFetch = globalThis.fetch;
  const enc = new TextEncoder();
  let service: ProjectEventsService;

  interface Conn {
    signal: AbortSignal;
    push(text: string): void;
    aborted: boolean;
  }
  let conns: Conn[];

  /**
   * Connessione muta che ignora l'abort della richiesta (come una rete caduta senza FIN/RST): si sblocca solo
   * se il servizio annulla il reader. Cosi' si prova che il servizio non dipende dall'errore di fetch.
   */
  const silentResponse = (init: RequestInit) => {
    let ctrl!: ReadableStreamDefaultController<Uint8Array>;
    const conn: Conn = {
      signal: init.signal!,
      aborted: false,
      push: (text) => {
        try {
          ctrl.enqueue(enc.encode(text));
        } catch {
          // stream gia' annullato dal client
        }
      },
    };
    init.signal!.addEventListener('abort', () => (conn.aborted = true));
    conns.push(conn);
    return new Response(
      new ReadableStream<Uint8Array>({
        start(c) {
          ctrl = c;
        },
      }),
      { status: 200 },
    );
  };

  const mk = () => ({ onConnected: vi.fn(), onEvent: vi.fn(), onDisconnected: vi.fn() });
  const tick = (ms: number) => vi.advanceTimersByTimeAsync(ms);
  const silentFetch = () =>
    vi.fn(async (_u: string, init: RequestInit) => silentResponse(init));

  beforeEach(() => {
    vi.useFakeTimers();
    conns = [];
    token.set('tok-1');
    TestBed.configureTestingModule({
      providers: [
        {
          provide: AuthService,
          useValue: { accessToken: token.asReadonly(), refresh: vi.fn(async () => true) },
        },
        { provide: SSE_BACKOFF_MS, useValue: [1000, 2000, 5000] },
        { provide: SSE_IDLE_TIMEOUT_MS, useValue: IDLE },
      ],
    });
    service = TestBed.inject(ProjectEventsService);
  });

  afterEach(() => {
    vi.useRealTimers();
    globalThis.fetch = originalFetch;
  });

  it('aborts a silent connection after the idle threshold and reopens it after the backoff', async () => {
    const fetchMock = silentFetch();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const h = mk();
    const close = service.open('p', h);
    await tick(0);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(h.onConnected).toHaveBeenCalledTimes(1);

    await tick(IDLE - 1);
    expect(conns[0].aborted).toBe(false);
    expect(h.onDisconnected).not.toHaveBeenCalled();

    await tick(1);
    expect(conns[0].aborted).toBe(true);
    expect(h.onDisconnected).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(1); // backoff in corso: nessuna riapertura immediata

    await tick(999);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await tick(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(conns[1].aborted).toBe(false);
    expect(h.onConnected).toHaveBeenCalledTimes(2);
    close();
  });

  it('treats any received bytes (keep-alive comments) as proof of life', async () => {
    const fetchMock = silentFetch();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const h = mk();
    const close = service.open('p', h);
    await tick(0);
    for (let i = 0; i < 8; i++) {
      await tick(15_000);
      conns[0].push(': keep-alive\n\n');
    }
    await tick(0);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(conns[0].aborted).toBe(false);
    expect(h.onDisconnected).not.toHaveBeenCalled();

    // Silenzio dopo l'ultimo keep-alive: scade dopo IDLE, non prima.
    await tick(IDLE - 1);
    expect(conns[0].aborted).toBe(false);
    await tick(1);
    expect(conns[0].aborted).toBe(true);
    close();
  });

  it('reopens when fetch never delivers the response headers', async () => {
    const calls: AbortSignal[] = [];
    const fetchMock = vi.fn((_u: string, init: RequestInit) => {
      calls.push(init.signal!);
      return new Promise<Response>((_resolve, reject) =>
        init.signal!.addEventListener('abort', () =>
          reject(new DOMException('abort', 'AbortError')),
        ),
      );
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const h = mk();
    const close = service.open('p', h);
    await tick(IDLE);
    expect(calls[0].aborted).toBe(true);
    expect(h.onConnected).not.toHaveBeenCalled();
    expect(h.onDisconnected).not.toHaveBeenCalled(); // mai connesso: nessun evento di disconnessione
    await tick(1000);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    close();
  });

  it('backs off with increasing waits on repeated failures instead of polling', async () => {
    const times: number[] = [];
    const fetchMock = vi.fn(async () => {
      times.push(Date.now());
      throw new TypeError('network down');
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const t0 = Date.now();
    const close = service.open('p', mk());
    await tick(60_000);
    close();
    // 0, +1000, +2000, +5000, +5000 ... (l'ultima attesa si ripete)
    expect(times.slice(0, 5).map((t) => t - t0)).toEqual([0, 1000, 3000, 8000, 13000]);
    expect(fetchMock.mock.calls.length).toBeLessThanOrEqual(14);
  });

  it('does not deliver late frames from an aborted connection (no duplicates)', async () => {
    globalThis.fetch = silentFetch() as unknown as typeof fetch;
    const h = mk();
    const close = service.open('p', h);
    await tick(0);
    conns[0].push('event: chat.state\ndata: {"n":1}\n\n');
    await tick(0);
    expect(h.onEvent).toHaveBeenCalledTimes(1);

    await tick(IDLE + 1000); // vecchia abortita, nuova aperta
    expect(conns).toHaveLength(2);
    conns[0].push('event: chat.state\ndata: {"n":1}\n\n'); // arrivo tardivo dalla vecchia connessione
    conns[1].push('event: chat.state\ndata: {"n":2}\n\n');
    await tick(0);
    expect(h.onEvent.mock.calls.map(([e]) => (e as ProjectStreamEvent).data)).toEqual([
      { n: 1 },
      { n: 2 },
    ]);
    close();
  });

  it('keeps one physical connection shared by all subscribers across an idle reconnect', async () => {
    const fetchMock = silentFetch();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const [a, b, c] = [mk(), mk(), mk()];
    const closers = [service.open('p', a), service.open('p', b), service.open('p', c)];
    await tick(0);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await tick(IDLE + 1000);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(conns.filter((x) => !x.aborted)).toHaveLength(1);
    for (const h of [a, b, c]) {
      expect(h.onDisconnected).toHaveBeenCalledTimes(1);
      expect(h.onConnected).toHaveBeenCalledTimes(2);
    }

    // Un evento sulla nuova connessione arriva una sola volta a ciascuno.
    conns[1].push('event: processing.state\ndata: {"s":1}\n\n');
    await tick(0);
    for (const h of [a, b, c]) expect(h.onEvent).toHaveBeenCalledTimes(1);
    closers.forEach((close) => close());
  });

  it('closes the connection and every timer when the last subscriber leaves (project switch)', async () => {
    const fetchMock = silentFetch();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const closeP = service.open('p', mk());
    await tick(0);
    closeP();
    await tick(0);
    expect(conns[0].aborted).toBe(true);
    expect(vi.getTimerCount()).toBe(0);

    const closeQ = service.open('q', mk());
    await tick(0);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][0]).toBe('/api/projects/q/events');
    closeQ();
    await tick(10 * IDLE);
    // Nessuna riapertura di 'p' o 'q' dopo la chiusura.
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(conns.every((x) => x.aborted)).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does not reopen during the backoff once the subscriber has left', async () => {
    const fetchMock = silentFetch();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const close = service.open('p', mk());
    await tick(IDLE); // idle: abortita, in attesa del backoff
    close();
    await tick(60_000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('stops for good after an idle timeout when the session is gone (logout)', async () => {
    const fetchMock = silentFetch();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const close = service.open('p', mk());
    await tick(0);
    token.set(null); // logout mentre lo stream e' muto
    await tick(IDLE + 1000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
    close();
    // Un nuovo login riapre normalmente.
    token.set('tok-2');
    const close2 = service.open('p', mk());
    await tick(0);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    close2();
  });

  it('retries at once when the browser comes back online during the backoff', async () => {
    const fetchMock = vi
      .fn()
      .mockRejectedValueOnce(new TypeError('offline'))
      .mockImplementation(async (_u: string, init: RequestInit) => silentResponse(init));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const close = service.open('p', mk());
    await tick(10);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    globalThis.dispatchEvent(new Event('online'));
    await tick(0);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    close();
  });
});
