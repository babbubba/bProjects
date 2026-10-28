import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { ContextUsage, ProjectApiService } from './project-api.service';
import { ProjectEventsService, ProjectStreamEvent } from './project-events.service';

/** Attesa (ms) che raggruppa in un solo GET le richieste di aggiornamento ravvicinate; 0 = al primo turno. */
export const CONTEXT_USAGE_DEBOUNCE_MS = new InjectionToken<number>('CONTEXT_USAGE_DEBOUNCE_MS', {
  providedIn: 'root',
  factory: () => 250,
});

/**
 * Stato del monitor del contesto del progetto aperto, condiviso da pannello e chat.
 * Riusa lo stream SSE già aperto (ref-count di ProjectEventsService): chat.state, processing.state e la
 * riconnessione fanno solo rifare il GET context-usage. Le risposte di un progetto precedente sono scartate.
 */
@Injectable({ providedIn: 'root' })
export class ContextUsageStore {
  private readonly api = inject(ProjectApiService);
  private readonly events = inject(ProjectEventsService);
  private readonly auth = inject(AuthService);
  private readonly debounceMs = inject(CONTEXT_USAGE_DEBOUNCE_MS);

  readonly usage = signal<ContextUsage | null>(null);
  /** Vero dopo un GET fallito: nessun valore precedente viene mostrato come attuale. */
  readonly failed = signal(false);

  private projectId: string | null = null;
  private generation = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private inFlight: number | null = null;
  private again = false;

  /** Inizia a seguire il progetto (azzera lo stato precedente); restituisce la funzione di chiusura. */
  watch(projectId: string): () => void {
    this.stop();
    const gen = ++this.generation;
    this.projectId = projectId;
    this.usage.set(null);
    this.failed.set(false);
    let close: () => void = () => undefined;
    if (this.auth.isAuthenticated()) {
      close = this.events.open(projectId, {
        onConnected: () => this.refresh(),
        onEvent: (e) => this.onEvent(e),
        onDisconnected: () => undefined,
      });
    }
    this.refresh();
    return () => {
      close();
      if (gen === this.generation) {
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
    if (event.name === 'chat.state' || event.name === 'processing.state') this.refresh();
  }

  private stop(): void {
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
