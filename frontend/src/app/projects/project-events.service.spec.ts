import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import {
  ProjectEventsService,
  ProjectStreamEvent,
  SSE_BACKOFF_MS,
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
              init.signal!.addEventListener('abort', () => c.error(new DOMException('a', 'AbortError')));
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
