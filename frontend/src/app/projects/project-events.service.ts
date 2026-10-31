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

interface SharedStream {
  readonly controller: AbortController;
  readonly subscribers: Set<ProjectStreamHandlers>;
  connected: boolean;
}

/**
 * Stream SSE del progetto via fetch con bearer in memoria (mai EventSource né token nell'URL).
 * Una sola connessione fisica per progetto, condivisa dai subscriber (ref-count): si chiude
 * quando l'ultimo si disiscrive (cambio progetto/logout). Riconnette con backoff, solo GET.
 */
@Injectable({ providedIn: 'root' })
export class ProjectEventsService {
  private readonly auth = inject(AuthService);
  private readonly backoff = inject(SSE_BACKOFF_MS);
  private readonly streams = new Map<string, SharedStream>();

  /** Si sottoscrive allo stream del progetto; restituisce la funzione di disiscrizione (idempotente). */
  open(projectId: string, handlers: ProjectStreamHandlers): () => void {
    let shared = this.streams.get(projectId);
    if (!shared) {
      shared = { controller: new AbortController(), subscribers: new Set(), connected: false };
      this.streams.set(projectId, shared);
      shared.subscribers.add(handlers);
      void this.run(projectId, shared);
    } else {
      shared.subscribers.add(handlers);
      // Stream già aperto: il nuovo subscriber riconcilia subito come dopo una (ri)connessione.
      if (shared.connected) handlers.onConnected();
    }
    const current = shared;
    return () => {
      if (!current.subscribers.delete(handlers)) return;
      if (current.subscribers.size === 0) {
        current.controller.abort();
        if (this.streams.get(projectId) === current) this.streams.delete(projectId);
      }
    };
  }

  private async run(projectId: string, shared: SharedStream) {
    const signal = shared.controller.signal;
    const each = (fn: (h: ProjectStreamHandlers) => void) =>
      [...shared.subscribers].forEach((h) => (signal.aborted ? undefined : fn(h)));
    let failures = 0;
    try {
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
            shared.connected = true;
            failures = 0;
            each((h) => h.onConnected());
            await this.read(response.body, each, signal);
          }
        } catch {
          // Errore di trasporto o chiusura: si riconnette (se non annullato) dopo il backoff.
        }
        shared.connected = false;
        if (signal.aborted) return;
        if (connected) each((h) => h.onDisconnected());
        await this.sleep(this.delay(failures++), signal);
      }
    } finally {
      // Uscita definitiva (no token, 404, refresh fallito): un nuovo open() potrà riaprire.
      if (this.streams.get(projectId) === shared) this.streams.delete(projectId);
    }
  }

  private async read(
    body: ReadableStream<Uint8Array>,
    each: (fn: (h: ProjectStreamHandlers) => void) => void,
    signal: AbortSignal,
  ) {
    const decoder = new TextDecoder();
    const parser = new SseParser((frame) => {
      if (signal.aborted) return;
      let event: ProjectStreamEvent;
      try {
        event = { name: frame.event, data: JSON.parse(frame.data) };
      } catch {
        // Frame non JSON: è solo una notifica effimera, la riconciliazione via GET copre il resto.
        return;
      }
      each((h) => {
        try {
          h.onEvent(event);
        } catch {
          // Un subscriber che fallisce non deve impedire la consegna agli altri.
        }
      });
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
