import { Injectable, InjectionToken, inject } from '@angular/core';
import { AuthService } from '../auth/auth.service';

/** Attese (ms) tra i tentativi di riconnessione SSE; l'ultima si ripete. Array vuoto: attesa nulla. */
export const SSE_BACKOFF_MS = new InjectionToken<readonly number[]>('SSE_BACKOFF_MS', {
  providedIn: 'root',
  factory: () => [1000, 2000, 5000, 10000],
});

export interface SseFrame {
  event: string;
  data: string;
}

/** Evento del progetto già decodificato (payload JSON). */
export interface ProjectStreamEvent {
  name: string;
  data: unknown;
}

export interface ProjectStreamHandlers {
  /** Stream (ri)aperto: il chiamante scarta i buffer provvisori e riconcilia con i GET. */
  onConnected(): void;
  onEvent(event: ProjectStreamEvent): void;
  /** Stream chiuso o caduto (prima di un'eventuale riconnessione). */
  onDisconnected(): void;
}

/** Parser incrementale del formato text/event-stream: commenti ignorati, data multi-riga unito con \n. */
export class SseParser {
  private buffer = '';
  private event = '';
  private data: string[] = [];

  constructor(private readonly onFrame: (frame: SseFrame) => void) {}

  push(chunk: string): void {
    this.buffer += chunk;
    for (;;) {
      const match = /\r\n|\n|\r/.exec(this.buffer);
      if (!match) return;
      // Un "\r" finale potrebbe essere la prima metà di "\r\n": si attende il byte successivo.
      if (match[0] === '\r' && match.index + 1 === this.buffer.length) return;
      const line = this.buffer.slice(0, match.index);
      this.buffer = this.buffer.slice(match.index + match[0].length);
      this.line(line);
    }
  }

  private line(line: string): void {
    if (line === '') {
      if (this.data.length > 0) {
        this.onFrame({ event: this.event || 'message', data: this.data.join('\n') });
      }
      this.event = '';
      this.data = [];
      return;
    }
    if (line.startsWith(':')) return;
    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'event') this.event = value;
    else if (field === 'data') this.data.push(value);
  }
}

/**
 * Stream SSE del progetto via fetch con bearer in memoria (mai EventSource né token nell'URL).
 * Una connessione per chiamata a open(); riconnette con backoff e non emette mai comandi (solo GET).
 */
@Injectable({ providedIn: 'root' })
export class ProjectEventsService {
  private readonly auth = inject(AuthService);
  private readonly backoff = inject(SSE_BACKOFF_MS);

  /** Apre lo stream del progetto; restituisce la funzione di chiusura (idempotente). */
  open(projectId: string, handlers: ProjectStreamHandlers): () => void {
    const controller = new AbortController();
    void this.run(projectId, handlers, controller.signal);
    return () => controller.abort();
  }

  private async run(projectId: string, h: ProjectStreamHandlers, signal: AbortSignal) {
    let failures = 0;
    while (!signal.aborted) {
      let connected = false;
      try {
        const token = this.auth.accessToken();
        if (!token) return;
        const response = await fetch(`/api/projects/${encodeURIComponent(projectId)}/events`, {
          method: 'GET',
          headers: { Authorization: `Bearer ${token}`, Accept: 'text/event-stream' },
          cache: 'no-store',
          signal,
        });
        if (response.status === 404) return;
        if (response.status === 401 && !(await this.auth.refresh())) return;
        if (response.ok && response.body) {
          connected = true;
          failures = 0;
          if (!signal.aborted) h.onConnected();
          await this.read(response.body, h, signal);
        }
      } catch {
        // Errore di trasporto o chiusura: si riconnette (se non annullato) dopo il backoff.
      }
      if (signal.aborted) return;
      if (connected) h.onDisconnected();
      await this.sleep(this.delay(failures++), signal);
    }
  }

  private async read(body: ReadableStream<Uint8Array>, h: ProjectStreamHandlers, signal: AbortSignal) {
    const decoder = new TextDecoder();
    const parser = new SseParser((frame) => {
      if (signal.aborted) return;
      try {
        h.onEvent({ name: frame.event, data: JSON.parse(frame.data) });
      } catch {
        // Frame non JSON: è solo una notifica effimera, la riconciliazione via GET copre il resto.
      }
    });
    const reader = body.getReader();
    const onAbort = () => void reader.cancel().catch(() => undefined);
    signal.addEventListener('abort', onAbort, { once: true });
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done || signal.aborted) return;
        parser.push(decoder.decode(value, { stream: true }));
      }
    } finally {
      signal.removeEventListener('abort', onAbort);
    }
  }

  private delay(failures: number): number {
    if (this.backoff.length === 0) return 0;
    return this.backoff[Math.min(failures, this.backoff.length - 1)];
  }

  private sleep(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve) => {
      const done = () => {
        clearTimeout(timer);
        signal.removeEventListener('abort', done);
        resolve();
      };
      const timer = setTimeout(done, ms);
      signal.addEventListener('abort', done, { once: true });
    });
  }
}
