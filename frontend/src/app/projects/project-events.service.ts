import { Injectable, InjectionToken, inject } from '@angular/core';
import { AuthService } from '../auth/auth.service';

/** Attese (ms) tra i tentativi di riconnessione SSE; l'ultima si ripete. Array vuoto: attesa nulla. */
export const SSE_BACKOFF_MS = new InjectionToken<readonly number[]>('SSE_BACKOFF_MS', {
  providedIn: 'root',
  factory: () => [1000, 2000, 5000, 10000],
});

/**
 * Silenzio massimo (ms) di una connessione SSE prima di considerarla morta. Il server scrive almeno un commento
 * ": keep-alive" ogni 15 s (ProjectEventsEndpoint): 45 s = 3 intervalli. Una rete caduta senza FIN/RST lascia
 * reader.read() sospeso all'infinito, quindi senza questo watchdog lo stream non si riaprirebbe mai.
 */
export const SSE_IDLE_TIMEOUT_MS = new InjectionToken<number>('SSE_IDLE_TIMEOUT_MS', {
  providedIn: 'root',
  factory: () => 45_000,
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
 * quando l'ultimo si disiscrive (cambio progetto/logout). Riconnette con backoff, solo GET; una connessione
 * muta oltre SSE_IDLE_TIMEOUT_MS (rete caduta senza chiusura) viene abortita e riaperta.
 */
@Injectable({ providedIn: 'root' })
export class ProjectEventsService {
  private readonly auth = inject(AuthService);
  private readonly backoff = inject(SSE_BACKOFF_MS);
  private readonly idleMs = inject(SSE_IDLE_TIMEOUT_MS);
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
        // Ogni tentativo ha il proprio AbortController: il watchdog d'inattività chiude solo la connessione
        // vecchia (senza chiudere lo stream condiviso) e nessun suo frame tardivo viene più consegnato.
        const attempt = new AbortController();
        const stop = () => attempt.abort();
        signal.addEventListener('abort', stop, { once: true });
        let idleTimer: ReturnType<typeof setTimeout> | undefined;
        const touch = () => {
          clearTimeout(idleTimer);
          idleTimer = setTimeout(stop, this.idleMs);
        };
        try {
          const token = this.auth.accessToken();
          if (!token) return;
          touch(); // copre anche una fetch() che non riceve mai gli header
          const response = await fetch(`/api/projects/${encodeURIComponent(projectId)}/events`, {
            method: 'GET',
            headers: { Authorization: `Bearer ${token}`, Accept: 'text/event-stream' },
            cache: 'no-store',
            signal: attempt.signal,
          });
          if (response.status === 404) return;
          if (response.status === 401 && !(await this.auth.refresh())) return;
          if (response.ok && response.body) {
            connected = true;
            shared.connected = true;
            failures = 0;
            each((h) => h.onConnected());
            await this.read(response.body, each, attempt.signal, touch);
          }
        } catch {
          // Errore di trasporto o chiusura: si riconnette (se non annullato) dopo il backoff.
        } finally {
          clearTimeout(idleTimer);
          signal.removeEventListener('abort', stop);
          attempt.abort(); // rilascia la connessione (anche se già chiusa o scaduta per inattività)
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
    touch: () => void,
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
        touch(); // qualunque byte (anche un commento keep-alive) prova che la connessione è viva
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

  /** Attesa del backoff; si interrompe all'abort o quando il browser torna online (riprova subito). */
  private sleep(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve) => {
      const done = () => {
        clearTimeout(timer);
        signal.removeEventListener('abort', done);
        globalThis.removeEventListener?.('online', done);
        resolve();
      };
      const timer = setTimeout(done, ms);
      signal.addEventListener('abort', done, { once: true });
      globalThis.addEventListener?.('online', done, { once: true });
    });
  }
}
