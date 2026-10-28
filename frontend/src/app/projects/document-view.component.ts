import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { ANALYSIS_POLL_MS } from './analysis-preview.component';
import { MdBlock, parseMarkdown } from './markdown';
import {
  AnalysisSource,
  DraftView,
  ProcessingRunView,
  ProjectApiService,
} from './project-api.service';
import { ProjectEventsService, ProjectStreamEvent } from './project-events.service';

const timeFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'short', timeStyle: 'short' });

const errorLabels: Record<string, string> = {
  llm_unavailable: 'Il modello AI non è al momento raggiungibile.',
  context_overflow: "L'analisi supera il contesto gestibile dal modello.",
  invalid_agent_output:
    'Il risultato del modello non era un documento valido e non è stato salvato.',
  deadline_exceeded: 'Il tempo massimo per la generazione è scaduto.',
  attempts_exhausted: 'I tentativi disponibili per la generazione sono esauriti.',
  checkpoint_incompatible: 'Lo stato della generazione non è compatibile con questa versione.',
  processing_failed: 'Elaborazione non riuscita.',
};

/** Revisione corrente dell'analisi, comunicata dall'anteprima: serve a riconoscere una bozza vecchia. */
export interface CurrentRevision {
  analysisId: string;
  revisionId: string;
  /** true solo se la revisione corrente è confermata e non ci sono modifiche pendenti. */
  confirmed: boolean;
}

type GenerateState = 'idle' | 'sending' | 'uncertain';

/**
 * Generazione esplicita del documento e lettura in sola lettura dell'ultima bozza Markdown (T042).
 * La bozza è sempre «Proposta / non approvata»; un run fallito o in corso non nasconde l'ultima bozza riuscita.
 * Il Markdown è renderizzato da un parser a dati (nessun HTML, nessuna risorsa remota); le citazioni `[fonte:id]`
 * diventano pulsanti che portano al messaggio del dialogo o alla revisione citata.
 */
@Component({
  selector: 'app-document-view',
  imports: [NgTemplateOutlet],
  templateUrl: './document-view.component.html',
  styleUrl: './document-view.component.scss',
})
export class DocumentViewComponent {
  private readonly api = inject(ProjectApiService);
  private readonly events = inject(ProjectEventsService);
  private readonly auth = inject(AuthService);
  private readonly pollMs = inject(ANALYSIS_POLL_MS);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly projectId = input.required<string>();
  /** Revisione corrente dell'analisi (null se non nota). */
  readonly current = input<CurrentRevision | null>(null);

  protected readonly draft = signal<DraftView | null>(null);
  protected readonly run = signal<ProcessingRunView | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly generateState = signal<GenerateState>('idle');
  protected readonly commandError = signal('');
  protected readonly sources = signal<AnalysisSource[]>([]);
  protected readonly sourceNotice = signal('');

  /** Vero mentre un comando è in invio o un run di generazione è attivo (l'anteprima disabilita Genera). */
  readonly active = computed(() => {
    const status = this.run()?.status;
    return status === 'Queued' || status === 'Running';
  });
  readonly busy = computed(() => this.active() || this.generateState() === 'sending');
  protected readonly failed = computed(() => this.run()?.status === 'Failed');
  protected readonly blocks = computed<MdBlock[]>(() =>
    parseMarkdown(this.draft()?.markdown ?? ''),
  );
  protected readonly stale = computed(() => {
    const draft = this.draft();
    if (!draft) return false;
    const current = this.current();
    return draft.hasNewRevision || (!!current && current.revisionId !== draft.analysisRevisionId);
  });
  protected readonly needsReconfirm = computed(() => {
    const current = this.current();
    return !!this.draft() && !!current && !current.confirmed;
  });
  protected readonly runError = computed(() => {
    const run = this.run();
    if (run?.status !== 'Failed') return '';
    const known = run.error ? errorLabels[run.error.code] : undefined;
    return known ?? run.error?.message ?? 'La generazione non è riuscita.';
  });
  protected readonly stageLabel = computed(() => {
    const run = this.run();
    if (!run) return '';
    const state = run.status === 'Queued' ? 'In coda' : 'Generazione in corso';
    const stage = run.lastObservedStage;
    if (!stage) return state;
    if (/load/i.test(stage)) return `${state}: lettura dell'analisi confermata`;
    if (/validate|persist/i.test(stage)) return `${state}: validazione e salvataggio`;
    return `${state}: redazione della bozza`;
  });
  private readonly sourceIndex = computed(
    () => new Map(this.sources().map((s, i) => [s.id, { source: s, index: i }] as const)),
  );

  private idempotencyKey: string | null = null;
  private pendingRevisionId: string | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private generation = 0;
  private highlightTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.generation++;
      this.stopPolling();
      if (this.highlightTimer) clearTimeout(this.highlightTimer);
    });
    effect(() => {
      const id = this.projectId();
      untracked(() => void this.reset(id));
    });
    effect((onCleanup) => {
      const id = this.projectId();
      if (!this.auth.isAuthenticated()) return;
      untracked(() => {
        const close = this.events.open(id, {
          onConnected: () => this.reconcile(id),
          onEvent: (e) => this.onStreamEvent(e, id),
          onDisconnected: () => undefined,
        });
        onCleanup(close);
      });
    });
  }

  protected formatTime(iso: string): string {
    return timeFormat.format(new Date(iso));
  }

  protected sourceLabel(id: string): string {
    const entry = this.sourceIndex().get(id);
    return entry ? `F${entry.index + 1}` : '?';
  }

  protected sourceKnown(id: string): boolean {
    return this.sourceIndex().has(id);
  }

  protected sourceDescription(source: AnalysisSource, index: number): string {
    if (source.kind === 'PmRevision') return `F${index + 1} · revisione del PM`;
    return `F${index + 1} · messaggio ${source.messageRole === 'assistant' ? "dell'assistente (solo contesto)" : 'del PM'}`;
  }

  /** Apre la fonte citata: messaggio evidenziato nel dialogo oppure l'anteprima/revisione del PM. */
  protected openSource(id: string): void {
    const entry = this.sourceIndex().get(id);
    this.sourceNotice.set('');
    if (!entry) {
      this.sourceNotice.set('Fonte non più disponibile per questa revisione.');
      return;
    }
    const { source } = entry;
    const doc = this.host.nativeElement.ownerDocument;
    const target =
      source.kind === 'Message' && source.messageId
        ? doc.getElementById(`message-${source.messageId}`)
        : doc.getElementById('analysis-title');
    if (!target) {
      this.sourceNotice.set(
        source.kind === 'Message'
          ? 'Il messaggio citato non è visibile nel dialogo caricato.'
          : "L'anteprima della revisione citata non è visibile.",
      );
      return;
    }
    target.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
    target.setAttribute('data-highlighted', 'true');
    if (this.highlightTimer) clearTimeout(this.highlightTimer);
    this.highlightTimer = setTimeout(() => target.removeAttribute('data-highlighted'), 3000);
    this.sourceNotice.set(
      source.kind === 'Message'
        ? `Mostrato nel dialogo il messaggio della fonte ${this.sourceLabel(id)}.`
        : `Mostrata l'anteprima della revisione del PM (fonte ${this.sourceLabel(id)}).`,
    );
  }

  /**
   * Comando Genera sulla revisione confermata indicata dall'anteprima. Un invio incerto riusa la stessa chiave
   * (stessa revisione): nessun secondo run; mai chiamato automaticamente.
   */
  async generate(revisionId: string): Promise<void> {
    const current = this.current();
    if (this.busy() || !current || !current.confirmed || current.revisionId !== revisionId) return;
    if (this.idempotencyKey && this.pendingRevisionId !== revisionId) this.idempotencyKey = null;
    this.idempotencyKey ??= crypto.randomUUID();
    this.pendingRevisionId = revisionId;
    const gen = this.generation;
    const projectId = this.projectId();
    this.generateState.set('sending');
    this.commandError.set('');
    try {
      const { run } = await this.api.generateDocument(
        projectId,
        current.analysisId,
        revisionId,
        this.idempotencyKey,
      );
      if (gen !== this.generation) return;
      this.idempotencyKey = null;
      this.pendingRevisionId = null;
      this.generateState.set('idle');
      this.track(projectId, run, gen);
    } catch (e) {
      if (gen !== this.generation) return;
      const uncertain = this.isUncertain(e);
      if (!uncertain) {
        this.idempotencyKey = null;
        this.pendingRevisionId = null;
      }
      this.generateState.set(uncertain ? 'uncertain' : 'idle');
      this.commandError.set(this.commandMessage(e, uncertain, projectId, gen));
    }
  }

  protected retryUncertain(): void {
    if (this.pendingRevisionId) void this.generate(this.pendingRevisionId);
  }

  private isUncertain(e: unknown): boolean {
    return (
      !(e instanceof HttpErrorResponse) || e.status === 0 || e.status >= 500 || e.status === 408
    );
  }

  private commandMessage(e: unknown, uncertain: boolean, projectId: string, gen: number): string {
    if (uncertain) {
      return 'Non sappiamo se la richiesta di generazione sia stata ricevuta (connessione o server non disponibili). Premi di nuovo Genera: verrà ripetuta la stessa richiesta senza crearne una seconda.';
    }
    if (e instanceof HttpErrorResponse) {
      const body = (e.error ?? {}) as { code?: string; processingRunId?: string };
      if (e.status === 401) return 'Sessione scaduta: accedi di nuovo.';
      if (e.status === 404) return 'Analisi o progetto non disponibile.';
      if (e.status === 400)
        return 'Richiesta di generazione non valida: ricarica la pagina e riprova.';
      if (e.status === 409) {
        switch (body.code) {
          case 'processing_active':
            if (body.processingRunId) void this.adopt(projectId, body.processingRunId, gen);
            return "Un'elaborazione è già in corso: attendi che termini.";
          case 'analysis_not_current':
            return "Questa non è più l'analisi corrente del progetto: ricarica la pagina.";
          case 'revision_conflict':
            return 'La revisione è cambiata: conferma la revisione corrente prima di generare.';
          case 'analysis_not_confirmed':
            return 'Nuova conferma richiesta: la revisione corrente non è confermata dal PM.';
          case 'idempotency_conflict':
            return 'Questa richiesta risulta già associata a un input diverso. Premi di nuovo Genera per una nuova richiesta.';
          default:
            return 'Generazione non consentita in questo stato. Ricarica la pagina.';
        }
      }
    }
    return 'Richiesta di generazione non riuscita. Riprova.';
  }

  private async adopt(projectId: string, runId: string, gen: number): Promise<void> {
    try {
      const run = await this.api.getRun(projectId, runId);
      if (gen === this.generation && run.kind === 'Document') this.track(projectId, run, gen);
    } catch {
      // Lo stato si riallinea alla prossima riconciliazione.
    }
  }

  private async reset(projectId: string): Promise<void> {
    const gen = ++this.generation;
    this.stopPolling();
    this.draft.set(null);
    this.run.set(null);
    this.sources.set([]);
    this.sourceNotice.set('');
    this.idempotencyKey = null;
    this.pendingRevisionId = null;
    this.generateState.set('idle');
    this.commandError.set('');
    this.loadError.set('');
    this.loading.set(true);
    try {
      await this.restore(projectId, gen);
    } catch {
      if (gen === this.generation) this.loadError.set('Impossibile caricare la bozza.');
    } finally {
      if (gen === this.generation) this.loading.set(false);
    }
  }

  /** Ricostruisce lo stato dal server: eventuale run Document attivo e ultima bozza riuscita. */
  private async restore(projectId: string, gen: number): Promise<void> {
    const project = await this.api.get(projectId);
    if (gen !== this.generation) return;
    if (project.activeProcessingRunId) {
      const run = await this.api.getRun(projectId, project.activeProcessingRunId);
      if (gen === this.generation && run.kind === 'Document')
        this.track(projectId, run, gen, false);
    }
    await this.loadDraft(projectId, gen);
  }

  private async loadDraft(projectId: string, gen: number): Promise<void> {
    try {
      const draft = await this.api.getDraft(projectId);
      if (gen !== this.generation) return;
      this.loadError.set('');
      if (!draft) return; // nessuna bozza ancora: un'eventuale bozza precedente non viene toccata
      this.draft.set(draft);
      void this.loadSources(projectId, draft, gen);
    } catch {
      // L'ultima bozza buona resta visibile; nulla viene inventato.
      if (gen === this.generation) this.loadError.set('Impossibile caricare la bozza.');
    }
  }

  private async loadSources(projectId: string, draft: DraftView, gen: number): Promise<void> {
    try {
      const revision = await this.api.getRevision(
        projectId,
        draft.analysisId,
        draft.analysisVersion,
      );
      if (gen === this.generation && this.draft()?.id === draft.id) {
        this.sources.set(revision.content.sources ?? []);
      }
    } catch {
      // Le citazioni restano non navigabili finché le fonti non sono leggibili.
      if (gen === this.generation) this.sources.set([]);
    }
  }

  private track(projectId: string, run: ProcessingRunView, gen: number, loadResult = true): void {
    if (gen !== this.generation) return;
    const previous = this.run();
    this.run.set(run);
    if (run.status === 'Completed') {
      this.stopPolling();
      if (loadResult && previous?.status !== 'Completed') void this.loadDraft(projectId, gen);
    } else if (run.status === 'Failed') {
      this.stopPolling();
    } else {
      this.schedulePoll();
    }
  }

  private reconcile(projectId: string): void {
    const gen = this.generation;
    const run = this.run();
    if (!run || run.status === 'Completed' || run.status === 'Failed') return;
    void this.refresh(projectId, run.id, gen);
  }

  private onStreamEvent(event: ProjectStreamEvent, projectId: string): void {
    if (event.name !== 'processing.state') return;
    const data = event.data as ProcessingRunView | null;
    if (!data || typeof data.id !== 'string' || data.kind !== 'Document') return;
    const current = this.run();
    // Segue il run noto; un run Document attivo non ancora noto (avviato altrove) viene adottato.
    if (current && data.id !== current.id) {
      if (data.status === 'Queued' || data.status === 'Running') {
        if (current.status !== 'Queued' && current.status !== 'Running') {
          this.track(projectId, data, this.generation);
        }
      }
      return;
    }
    this.track(projectId, data, this.generation);
  }

  private async refresh(projectId: string, runId: string, gen: number): Promise<void> {
    try {
      const run = await this.api.getRun(projectId, runId);
      this.track(projectId, run, gen);
    } catch {
      if (gen === this.generation && this.active()) this.schedulePoll();
    }
  }

  private schedulePoll(): void {
    if (this.pollMs <= 0 || this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      const run = this.run();
      if (run && this.active()) void this.refresh(this.projectId(), run.id, this.generation);
    }, this.pollMs);
  }

  private stopPolling(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
}
