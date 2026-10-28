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
import { ChatTurnView, MessageView, ProjectApiService } from './project-api.service';

/** Intervallo (ms) di riconciliazione dei turni pendenti; 0 disattiva il polling. */
export const CHAT_POLL_MS = new InjectionToken<number>('CHAT_POLL_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

const timeFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'short', timeStyle: 'short' });

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

  readonly projectId = input.required<string>();

  protected readonly messages = signal<MessageView[]>([]);
  protected readonly turns = signal<Record<string, ChatTurnView | null>>({});
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly draft = signal('');
  protected readonly sendState = signal<SendState>('idle');
  protected readonly sendError = signal('');
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
    const known = turn.error ? errorLabels[turn.error.code] : undefined;
    return known ?? turn.error?.message ?? 'La risposta AI non è riuscita.';
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
      if (e.status === 401) return 'Sessione scaduta: accedi di nuovo. Il testo non è stato inviato.';
      if (e.status === 404) return 'Progetto non disponibile. Il testo non è stato inviato.';
      if (e.status === 409) {
        return "Questo invio risulta già associato a un testo diverso. Premi di nuovo Invia: verrà creato un nuovo invio.";
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
      if (page.items.length === 0 || page.nextAfterSequence === null || page.nextAfterSequence === after) {
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
