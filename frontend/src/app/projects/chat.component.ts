import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../auth/auth.service';
import { ChatTurnView, MessageView, ProjectApiService } from './project-api.service';
import { describeFailure } from './failure-text';
import { ProjectEventsService, ProjectStreamEvent } from './project-events.service';

/** Intervallo (ms) di riconciliazione dei turni pendenti; 0 disattiva il polling. */
export const CHAT_POLL_MS = new InjectionToken<number>('CHAT_POLL_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

const timeFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'short', timeStyle: 'short' });

/** Buffer provvisorio della risposta in streaming per un turno: un solo tentativo, indici contigui da 0. */
interface StreamBuffer {
  attempt: number;
  next: number;
  text: string;
}

/** Invio PM non ancora confermato dal server: id e testo restano stabili tra i tentativi. */
interface PendingSend {
  clientMessageId: string;
  text: string;
}

type SendState = 'idle' | 'sending' | 'failed' | 'uncertain';

const errorLabels: Record<string, string> = {
  llm_unavailable: 'Il modello AI non è al momento raggiungibile.',
  context_overflow: 'La conversazione supera il contesto gestibile dal modello.',
  compaction_failed: 'Non è stato possibile condensare la conversazione.',
  invalid_agent_output: 'La risposta del modello non era utilizzabile.',
  deadline_exceeded: 'Il tempo massimo per la risposta è scaduto.',
  attempts_exhausted: 'I tentativi disponibili per rispondere sono esauriti.',
  checkpoint_incompatible: 'Lo stato della conversazione non è compatibile.',
  processing_failed: 'Elaborazione non riuscita.',
};

/** Chat centrale del progetto: la conferma di salvataggio PM è distinta dallo stato del turno AI. */
@Component({
  selector: 'app-chat',
  imports: [FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss',
})
export class ChatComponent {
  private readonly api = inject(ProjectApiService);
  private readonly pollMs = inject(CHAT_POLL_MS);
  private readonly events = inject(ProjectEventsService);
  private readonly auth = inject(AuthService);

  readonly projectId = input.required<string>();

  protected readonly messages = signal<MessageView[]>([]);
  protected readonly turns = signal<Record<string, ChatTurnView | null>>({});
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly draft = signal('');
  protected readonly sendState = signal<SendState>('idle');
  protected readonly sendError = signal('');
  /** Testo provvisorio per turno (solo UI): sostituito dal messaggio persistito dopo Completed. */
  protected readonly streams = signal<Record<string, StreamBuffer>>({});
  protected readonly streamingItems = computed(() =>
    Object.entries(this.streams())
      .filter(([id, b]) => {
        const turn = this.turns()[id];
        const open = turn !== undefined && (!turn || turn.status === 'Queued' || turn.status === 'Running');
        return open && b.text.length > 0 && !this.messages().some((m) => m.role === 'assistant' && m.chatTurnId === id);
      })
      .map(([id, b]) => ({ id, text: b.text })),
  );
  protected readonly canSend = computed(
    () => this.sendState() !== 'sending' && this.draft().trim().length > 0,
  );

  private pending: PendingSend | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.generation++;
      this.stopPolling();
    });
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.reset(id));
    });
    // Una connessione SSE per progetto aperto; chiusa al cambio progetto e al logout (cleanup).
    effect((onCleanup) => {
      const id = this.projectId();
      if (!this.auth.isAuthenticated()) return;
      untracked(() => {
        const close = this.events.open(id, {
          onConnected: () => this.onStreamConnected(id),
          onEvent: (e) => this.onStreamEvent(e, id),
          onDisconnected: () => this.streams.set({}),
        });
        onCleanup(() => {
          close();
          this.streams.set({});
        });
      });
    });
  }

  protected formatTime(iso: string): string {
    return timeFormat.format(new Date(iso));
  }

  /** Turno AI collegato a un messaggio PM (indipendente dal salvataggio). */
  protected turnOf(message: MessageView): ChatTurnView | null | undefined {
    return message.chatTurnId ? this.turns()[message.chatTurnId] : undefined;
  }

  protected turnLabel(message: MessageView): string {
    if (!message.chatTurnId) return '';
    if (this.hasReply(message)) return 'Risposta ricevuta';
    switch (this.turnOf(message)?.status) {
      case 'Queued':
        return 'In coda per la risposta AI';
      case 'Running':
        return 'AI al lavoro';
      case 'Completed':
        return 'Risposta ricevuta';
      case 'Failed':
        return 'Risposta AI non riuscita';
      default:
        return 'Stato della risposta AI in verifica';
    }
  }

  protected turnError(message: MessageView): string {
    const turn = this.turnOf(message);
    if (turn?.status !== 'Failed') return '';
    return describeFailure(turn.error, errorLabels, 'La risposta AI non è riuscita.', 'chat');
  }

  protected async send(): Promise<void> {
    if (!this.canSend()) return;
    const text = this.draft();
    // Un invio incerto con testo invariato riusa lo stesso clientMessageId: nessun duplicato.
    if (!this.pending || this.pending.text !== text) {
      this.pending = { clientMessageId: crypto.randomUUID(), text };
    }
    await this.submit(this.pending);
  }

  /** Verifica un invio incerto ripetendo il POST con lo stesso clientMessageId e testo. */
  protected async retryUncertain(): Promise<void> {
    if (this.pending && this.sendState() !== 'sending') await this.submit(this.pending);
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void this.send();
    }
  }

  private async submit(pending: PendingSend): Promise<void> {
    const gen = this.generation;
    const projectId = this.projectId();
    this.sendState.set('sending');
    this.sendError.set('');
    try {
      const result = await this.api.postMessage(projectId, pending.clientMessageId, pending.text);
      if (gen !== this.generation) return;
      this.mergeMessages([result.message]);
      this.turns.update((t) => ({ ...t, [result.chatTurnId]: t[result.chatTurnId] ?? null }));
      this.pending = null;
      this.sendState.set('idle');
      if (this.draft() === pending.text) this.draft.set('');
      void this.refreshTurn(projectId, result.chatTurnId, gen);
      this.schedulePoll();
    } catch (e) {
      if (gen !== this.generation) return;
      const uncertain = this.isUncertain(e);
      if (!uncertain) this.pending = null;
      this.sendState.set(uncertain ? 'uncertain' : 'failed');
      this.sendError.set(this.sendMessage(e, uncertain));
    }
  }

  /** Errore di trasporto o 5xx: il server potrebbe aver salvato il messaggio. */
  private isUncertain(e: unknown): boolean {
    return (
      !(e instanceof HttpErrorResponse) || e.status === 0 || e.status >= 500 || e.status === 408
    );
  }

  private sendMessage(e: unknown, uncertain: boolean): string {
    if (uncertain) {
      return 'Non sappiamo se il messaggio sia stato salvato (connessione o server non disponibili). Il testo è conservato: usa "Verifica e riprova" per ripetere l\'invio senza creare duplicati.';
    }
    if (e instanceof HttpErrorResponse) {
      if (e.status === 401)
        return 'Sessione scaduta: accedi di nuovo. Il testo non è stato inviato.';
      if (e.status === 404) return 'Progetto non disponibile. Il testo non è stato inviato.';
      if (e.status === 409) {
        return 'Questo invio risulta già associato a un testo diverso. Premi di nuovo Invia: verrà creato un nuovo invio.';
      }
      if (e.status === 422) {
        const errors = (e.error as { errors?: Record<string, string[]> } | null)?.errors;
        const first = errors ? Object.values(errors)[0]?.[0] : undefined;
        return first ?? 'Il messaggio non è valido.';
      }
    }
    return 'Invio non riuscito. Il testo è conservato nel campo.';
  }

  private hasReply(message: MessageView): boolean {
    return this.messages().some((m) => m.role === 'assistant' && m.replyToMessageId === message.id);
  }

  private async reset(projectId: string): Promise<void> {
    const gen = ++this.generation;
    this.stopPolling();
    this.messages.set([]);
    this.turns.set({});
    this.draft.set('');
    this.pending = null;
    this.sendState.set('idle');
    this.sendError.set('');
    await this.loadAll(projectId, gen);
  }

  private async loadAll(projectId: string, gen: number): Promise<void> {
    this.loading.set(true);
    this.loadError.set('');
    try {
      await this.fetchNewMessages(projectId, gen);
      if (gen !== this.generation) return;
      const open = this.messages().filter(
        (m) => m.role === 'user' && m.chatTurnId && !this.hasReply(m),
      );
      await Promise.all(open.map((m) => this.refreshTurn(projectId, m.chatTurnId!, gen)));
      this.schedulePoll();
    } catch {
      if (gen === this.generation) {
        this.loadError.set('Impossibile caricare il dialogo. Riprova più tardi.');
      }
    } finally {
      if (gen === this.generation) this.loading.set(false);
    }
  }

  private async fetchNewMessages(projectId: string, gen: number): Promise<void> {
    let after: number | null = this.messages().at(-1)?.sequence ?? null;
    for (;;) {
      const page = await this.api.listMessages(projectId, after);
      if (gen !== this.generation) return;
      this.mergeMessages(page.items);
      if (
        page.items.length === 0 ||
        page.nextAfterSequence === null ||
        page.nextAfterSequence === after
      ) {
        return;
      }
      after = page.nextAfterSequence;
    }
  }

  private async refreshTurn(projectId: string, turnId: string, gen: number): Promise<void> {
    try {
      const turn = await this.api.getChatTurn(projectId, turnId);
      if (gen !== this.generation) return;
      this.turns.update((t) => ({ ...t, [turnId]: turn }));
      if (turn.status === 'Completed') await this.fetchNewMessages(projectId, gen);
    } catch {
      // Errore di trasporto: lo stato resta "in verifica" e la riconciliazione successiva riprova.
    }
  }

  /** Apertura/riconnessione: scarta il buffer provvisorio e riconcilia solo con GET (nessun comando). */
  private onStreamConnected(projectId: string): void {
    this.streams.set({});
    const gen = this.generation;
    void (async () => {
      try {
        await this.fetchNewMessages(projectId, gen);
      } catch {
        return;
      }
      await Promise.all(this.pendingTurnIds().map((id) => this.refreshTurn(projectId, id, gen)));
      if (gen === this.generation) this.schedulePoll();
    })();
  }

  private onStreamEvent(event: ProjectStreamEvent, projectId: string): void {
    const data = event.data as Record<string, unknown> | null;
    if (!data || typeof data !== 'object') return;
    if (event.name === 'chat.delta') this.applyDelta(data);
    else if (event.name === 'chat.state') this.applyState(data as unknown as ChatTurnView, projectId);
  }

  /** Delta provvisorio: tentativo più vecchio, duplicato o fuori ordine vengono ignorati; uno più nuovo azzera il buffer. */
  private applyDelta(d: Record<string, unknown>): void {
    const { chatTurnId, attemptCount, deltaIndex, text } = d;
    if (typeof chatTurnId !== 'string' || typeof text !== 'string') return;
    if (typeof attemptCount !== 'number' || typeof deltaIndex !== 'number') return;
    this.streams.update((all) => {
      let buffer = all[chatTurnId];
      if (buffer && attemptCount < buffer.attempt) return all;
      if (!buffer || attemptCount > buffer.attempt) buffer = { attempt: attemptCount, next: 0, text: '' };
      if (deltaIndex !== buffer.next) return { ...all, [chatTurnId]: buffer };
      return {
        ...all,
        [chatTurnId]: { attempt: buffer.attempt, next: buffer.next + 1, text: buffer.text + text },
      };
    });
  }

  private applyState(turn: ChatTurnView, projectId: string): void {
    if (!turn || typeof turn.id !== 'string' || !(turn.id in this.turns())) return;
    const gen = this.generation;
    this.turns.update((t) => ({ ...t, [turn.id]: turn }));
    const buffer = this.streams()[turn.id];
    const terminal = turn.status === 'Completed' || turn.status === 'Failed';
    if (terminal || (buffer && turn.attemptCount > buffer.attempt)) {
      this.streams.update(({ [turn.id]: _drop, ...rest }) => rest);
    }
    if (turn.status === 'Completed') {
      void this.fetchNewMessages(projectId, gen).catch(() => undefined);
    }
  }

  private pendingTurnIds(): string[] {
    return Object.entries(this.turns())
      .filter(([, t]) => !t || t.status === 'Queued' || t.status === 'Running')
      .map(([id]) => id);
  }

  private schedulePoll(): void {
    if (this.pollMs <= 0 || this.timer || this.pendingTurnIds().length === 0) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.poll(this.projectId(), this.generation);
    }, this.pollMs);
  }

  private async poll(projectId: string, gen: number): Promise<void> {
    await Promise.all(this.pendingTurnIds().map((id) => this.refreshTurn(projectId, id, gen)));
    if (gen === this.generation) this.schedulePoll();
  }

  private stopPolling(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  private mergeMessages(incoming: MessageView[]): void {
    this.messages.update((current) => {
      const byId = new Map(current.map((m) => [m.id, m]));
      for (const m of incoming) byId.set(m.id, m);
      return [...byId.values()].sort((a, b) => a.sequence - b.sequence);
    });
  }
}
