import { Injectable, InjectionToken, OnDestroy, inject, signal } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { ContextUsage, ProjectApiService } from './project-api.service';
import { ProjectEventsService, ProjectStreamEvent } from './project-events.service';

/** Attesa (ms) che raggruppa in un solo GET le richieste di aggiornamento ravvicinate; 0 = al primo turno. */
export const CONTEXT_USAGE_DEBOUNCE_MS = new InjectionToken<number>('CONTEXT_USAGE_DEBOUNCE_MS', {
  providedIn: 'root',
  factory: () => 250,
});

/** Intervallo (ms) del polling GET di riconciliazione quando lo stream eventi è perso o la sessione è assente. */
export const CONTEXT_USAGE_POLL_MS = new InjectionToken<number>('CONTEXT_USAGE_POLL_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

/** Eventi che cambiano ciò che il monitor mostra: il GET resta la fonte, l'evento fa solo rileggerlo. */
const REFRESH_EVENTS = new Set([
  'chat.state',
  'processing.state',
  'document.state',
  'execution.regime-changed',
]);

/**
 * Stato del contesto del progetto aperto, condiviso da pannello e chat.
 * Riusa lo stream SSE già aperto (ref-count di ProjectEventsService): chat.state, processing.state e la
 * riconnessione fanno solo rifare il GET context-usage. Le risposte di un progetto precedente sono scartate.
 * Senza stream (primo tentativo fallito, caduta, logout) sonda ogni CONTEXT_USAGE_POLL_MS con soli GET, mai POST,
 * finché lo stream non torna; al ritorno della sessione riapre lo stream. Gli eventi persi si riconciliano così.
 */
@Injectable({ providedIn: 'root' })
export class ContextUsageStore implements OnDestroy {
  private readonly api = inject(ProjectApiService);
  private readonly events = inject(ProjectEventsService);
  private readonly auth = inject(AuthService);
  private readonly debounceMs = inject(CONTEXT_USAGE_DEBOUNCE_MS);
  private readonly pollMs = inject(CONTEXT_USAGE_POLL_MS);

  readonly usage = signal<ContextUsage | null>(null);
  /** Vero dopo un GET fallito: nessun valore precedente viene mostrato come attuale. */
  readonly failed = signal(false);

  private projectId: string | null = null;
  private generation = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private inFlight: number | null = null;
  private again = false;
  private closeStream: () => void = () => undefined;
  private streamOpen = false;
  private poller: ReturnType<typeof setInterval> | null = null;

  /** Inizia a seguire il progetto (azzera lo stato precedente); restituisce la funzione di chiusura. */
  watch(projectId: string): () => void {
    this.disconnect();
    this.stop();
    const gen = ++this.generation;
    this.projectId = projectId;
    this.usage.set(null);
    this.failed.set(false);
    if (this.auth.isAuthenticated()) this.connect(projectId);
    else this.startPolling(); // senza sessione: nessuno stream, il ritorno della sessione lo apre
    this.refresh();
    return () => {
      if (gen === this.generation) {
        this.disconnect();
        this.stop();
        this.generation++;
        this.projectId = null;
      }
    };
  }

  /** Richiede un aggiornamento (coalescente); non fa nulla se nessun progetto è seguito. */
  refresh(): void {
    if (this.projectId === null || this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.load();
    }, this.debounceMs);
  }

  private onEvent(event: ProjectStreamEvent): void {
    if (REFRESH_EVENTS.has(event.name)) this.refresh();
  }

  private connect(projectId: string): void {
    const gen = this.generation;
    const current = () => gen === this.generation; // callback tardive di un progetto precedente: ignorate
    this.streamOpen = true;
    this.closeStream = this.events.open(projectId, {
      onConnected: () => {
        if (!current()) return;
        this.stopPolling();
        this.refresh();
      },
      onEvent: (e) => current() && this.onEvent(e),
      onDisconnected: () => current() && this.startPolling(),
      onUnavailable: () => current() && this.startPolling(),
      onTerminated: () => {
        if (!current()) return;
        this.streamOpen = false;
        this.startPolling();
      },
    });
  }

  private disconnect(): void {
    this.closeStream();
    this.closeStream = () => undefined;
    this.streamOpen = false;
  }

  /** Un tick ogni pollMs: con sessione riapre lo stream se terminato e rilegge con GET; senza sessione non fa nulla. */
  private startPolling(): void {
    if (this.poller !== null) return;
    this.poller = setInterval(() => {
      const projectId = this.projectId;
      if (projectId === null || !this.auth.isAuthenticated()) return;
      if (!this.streamOpen) {
        this.closeStream();
        this.connect(projectId);
      }
      this.refresh();
    }, this.pollMs);
  }

  private stopPolling(): void {
    if (this.poller !== null) clearInterval(this.poller);
    this.poller = null;
  }

  ngOnDestroy(): void {
    this.disconnect();
    this.stop();
  }

  private stop(): void {
    this.stopPolling();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.inFlight = null;
    this.again = false;
  }

  private async load(): Promise<void> {
    const gen = this.generation;
    const projectId = this.projectId;
    if (projectId === null) return;
    if (this.inFlight === gen) {
      this.again = true;
      return;
    }
    this.inFlight = gen;
    try {
      const usage = await this.api.getContextUsage(projectId);
      if (gen !== this.generation) return;
      this.usage.set(usage);
      this.failed.set(false);
    } catch {
      if (gen !== this.generation) return;
      this.usage.set(null);
      this.failed.set(true);
    } finally {
      if (this.inFlight === gen) {
        this.inFlight = null;
        if (this.again) {
          this.again = false;
          void this.load();
        }
      }
    }
  }
}
