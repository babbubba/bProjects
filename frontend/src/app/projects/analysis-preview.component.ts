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
import { AuthService } from '../auth/auth.service';
import {
  AnalysisItem,
  AnalysisView,
  ProcessingRunView,
  ProjectApiService,
} from './project-api.service';
import { ProjectEventsService, ProjectStreamEvent } from './project-events.service';

/** Intervallo (ms) di riconciliazione del run formale attivo (fallback se SSE non arriva); 0 disattiva il polling. */
export const ANALYSIS_POLL_MS = new InjectionToken<number>('ANALYSIS_POLL_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

const timeFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'short', timeStyle: 'short' });

const errorLabels: Record<string, string> = {
  llm_unavailable: 'Il modello AI non è al momento raggiungibile.',
  context_overflow: 'La conversazione supera il contesto gestibile dal modello.',
  compaction_failed: 'Non è stato possibile condensare la conversazione.',
  invalid_agent_output: "Il risultato del modello non era un'analisi valida e non è stato salvato.",
  deadline_exceeded: "Il tempo massimo per l'analisi è scaduto.",
  attempts_exhausted: "I tentativi disponibili per l'analisi sono esauriti.",
  checkpoint_incompatible: "Lo stato dell'analisi non è compatibile con questa versione.",
  processing_failed: 'Elaborazione non riuscita.',
};

const classificationLabels = { Declared: 'Dichiarato', Inferred: 'Dedotto', Assumed: 'Ipotizzato' };

interface Section {
  key: string;
  title: string;
  items: AnalysisItem[];
}

/** Invio di Analizza non ancora confermato: la chiave resta stabile finché l'esito è incerto. */
type AnalyzeState = 'idle' | 'sending' | 'uncertain';

/**
 * Comando Analizza, stato del run formale e anteprima iniziale in sola lettura (T031). Nessun Markdown né documento:
 * l'anteprima è il JSON strutturato dell'ultima analisi riuscita, marcato «Non validata / in attesa del PM».
 * Un run fallito non produce mai un'anteprima di successo e non nasconde l'ultima analisi buona.
 */
@Component({
  selector: 'app-analysis-preview',
  templateUrl: './analysis-preview.component.html',
  styleUrl: './analysis-preview.component.scss',
})
export class AnalysisPreviewComponent {
  private readonly api = inject(ProjectApiService);
  private readonly events = inject(ProjectEventsService);
  private readonly auth = inject(AuthService);
  private readonly pollMs = inject(ANALYSIS_POLL_MS);

  readonly projectId = input.required<string>();

  protected readonly run = signal<ProcessingRunView | null>(null);
  protected readonly analysis = signal<AnalysisView | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly analyzeState = signal<AnalyzeState>('idle');
  protected readonly commandError = signal('');
  protected readonly previewError = signal('');

  protected readonly active = computed(() => {
    const status = this.run()?.status;
    return status === 'Queued' || status === 'Running';
  });
  protected readonly failed = computed(() => this.run()?.status === 'Failed');
  protected readonly canAnalyze = computed(
    () => !this.loading() && !this.active() && this.analyzeState() !== 'sending',
  );
  protected readonly runError = computed(() => {
    const run = this.run();
    if (run?.status !== 'Failed') return '';
    const known = run.error ? errorLabels[run.error.code] : undefined;
    return known ?? run.error?.message ?? "L'analisi non è riuscita.";
  });
  protected readonly stageLabel = computed(() => {
    const run = this.run();
    if (!run) return '';
    const state = run.status === 'Queued' ? 'In coda' : 'Analisi in corso';
    const stage = run.lastObservedStage;
    if (!stage) return state;
    if (/load/i.test(stage)) return `${state}: lettura del dialogo`;
    if (/validate|persist/i.test(stage)) return `${state}: validazione e salvataggio`;
    return `${state}: consolidamento`;
  });
  protected readonly sections = computed<Section[]>(() => {
    const c = this.analysis()?.content;
    if (!c) return [];
    return [
      { key: 'goals', title: 'Obiettivi', items: c.goals },
      { key: 'overview', title: 'Contesto', items: c.contextAndScope.overview },
      { key: 'included', title: 'In perimetro', items: c.contextAndScope.included },
      { key: 'excluded', title: 'Fuori perimetro', items: c.contextAndScope.excluded },
      { key: 'requirements', title: 'Requisiti', items: c.requirements },
      { key: 'flows', title: 'Flussi operativi', items: c.operationalFlows },
      { key: 'constraints', title: 'Vincoli', items: c.constraints },
      { key: 'dependencies', title: 'Integrazioni e dipendenze', items: c.dependencies },
      { key: 'assumptions', title: 'Assunzioni', items: c.assumptions },
      { key: 'questions', title: 'Punti aperti', items: c.openQuestions },
    ].filter((s) => s.items.length > 0);
  });
  private readonly sourceLabels = computed(
    () =>
      new Map((this.analysis()?.content.sources ?? []).map((s, i) => [s.id, `F${i + 1}`] as const)),
  );

  private idempotencyKey: string | null = null;
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
    // processing.state dal flusso SSE del progetto; il polling resta il fallback e la riconciliazione.
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

  protected classification(item: AnalysisItem): string {
    return classificationLabels[item.classification] ?? item.classification;
  }

  /** Etichette F1, F2… delle fonti citate dall'elemento (id fonte → posizione nell'elenco fonti). */
  protected refs(item: { evidenceRefs: string[] }): string {
    return item.evidenceRefs.map((r) => this.sourceLabels().get(r) ?? '?').join(', ');
  }

  protected sourceLabel(index: number, kind: string, role: string | null): string {
    if (kind === 'PmRevision') return `F${index + 1} · revisione del PM`;
    return `F${index + 1} · messaggio ${role === 'assistant' ? "dell'assistente (solo contesto)" : 'del PM'}`;
  }

  protected async analyze(): Promise<void> {
    if (!this.canAnalyze()) return;
    // Un invio incerto riusa la stessa chiave: nessun secondo run; un nuovo comando esplicito ne genera una nuova.
    this.idempotencyKey ??= crypto.randomUUID();
    const gen = this.generation;
    const projectId = this.projectId();
    this.analyzeState.set('sending');
    this.commandError.set('');
    try {
      const { run } = await this.api.analyze(projectId, this.idempotencyKey);
      if (gen !== this.generation) return;
      this.idempotencyKey = null;
      this.analyzeState.set('idle');
      this.track(projectId, run, gen);
    } catch (e) {
      if (gen !== this.generation) return;
      const uncertain = this.isUncertain(e);
      if (!uncertain) this.idempotencyKey = null;
      this.analyzeState.set(uncertain ? 'uncertain' : 'idle');
      this.commandError.set(this.commandMessage(e, uncertain, projectId, gen));
    }
  }

  private isUncertain(e: unknown): boolean {
    return (
      !(e instanceof HttpErrorResponse) || e.status === 0 || e.status >= 500 || e.status === 408
    );
  }

  private commandMessage(e: unknown, uncertain: boolean, projectId: string, gen: number): string {
    if (uncertain) {
      return 'Non sappiamo se la richiesta di analisi sia stata ricevuta (connessione o server non disponibili). Premi di nuovo Analizza: verrà ripetuta la stessa richiesta senza crearne una seconda.';
    }
    if (e instanceof HttpErrorResponse) {
      const code = (e.error as { code?: string } | null)?.code;
      if (e.status === 401) return 'Sessione scaduta: accedi di nuovo.';
      if (e.status === 404) return 'Progetto non disponibile.';
      if (e.status === 422 && code === 'no_pm_input') {
        return "Scrivi almeno un messaggio nella chat prima di avviare l'analisi.";
      }
      if (e.status === 409 && code === 'processing_active') {
        const active = (e.error as { processingRunId?: string } | null)?.processingRunId;
        if (active) void this.adopt(projectId, active, gen);
        return "Un'elaborazione è già in corso: attendi che termini.";
      }
      if (e.status === 409) {
        return 'Questa richiesta risulta già associata a un input diverso. Premi di nuovo Analizza per una nuova richiesta.';
      }
    }
    return 'Richiesta di analisi non riuscita. Riprova.';
  }

  /** Aggancia un run attivo indicato dal server (409 processing_active): da qui lo stato è quello reale. */
  private async adopt(projectId: string, runId: string, gen: number): Promise<void> {
    try {
      const run = await this.api.getRun(projectId, runId);
      if (gen === this.generation) this.track(projectId, run, gen);
    } catch {
      // Lo stato si riallinea alla prossima riconciliazione.
    }
  }

  private async reset(projectId: string): Promise<void> {
    const gen = ++this.generation;
    this.stopPolling();
    this.run.set(null);
    this.analysis.set(null);
    this.idempotencyKey = null;
    this.analyzeState.set('idle');
    this.commandError.set('');
    this.previewError.set('');
    this.loadError.set('');
    this.loading.set(true);
    try {
      await this.restore(projectId, gen);
    } catch {
      if (gen === this.generation) this.loadError.set("Impossibile caricare lo stato dell'analisi.");
    } finally {
      if (gen === this.generation) this.loading.set(false);
    }
  }

  /** Ricostruisce lo stato dalla ProjectView: run attivo, ultima analisi (anche fallita) e anteprima corrente. */
  private async restore(projectId: string, gen: number): Promise<void> {
    const project = await this.api.get(projectId);
    if (gen !== this.generation) return;
    const runId = project.activeProcessingRunId ?? project.latestAnalysisRunId;
    const currentId = project.currentAnalysisId ?? null;
    if (currentId) await this.loadAnalysis(projectId, currentId, gen);
    // Un'ultima analisi diversa dalla corrente è fallita o ancora in corso: serve lo stato del run, non un'anteprima.
    if (runId && (project.activeProcessingRunId || runId !== currentId)) {
      const run = await this.api.getRun(projectId, runId);
      if (gen === this.generation) this.track(projectId, run, gen, false);
    }
  }

  private track(projectId: string, run: ProcessingRunView, gen: number, loadResult = true): void {
    if (gen !== this.generation) return;
    const previous = this.run();
    this.run.set(run);
    if (run.status === 'Completed') {
      this.stopPolling();
      if (loadResult && previous?.status !== 'Completed') {
        void this.loadAnalysis(projectId, run.analysisId ?? run.id, gen);
      }
    } else if (run.status === 'Failed') {
      this.stopPolling();
    } else {
      this.schedulePoll();
    }
  }

  private async loadAnalysis(projectId: string, analysisId: string, gen: number): Promise<void> {
    try {
      const view = await this.api.getAnalysis(projectId, analysisId);
      if (gen !== this.generation) return;
      this.analysis.set(view);
      this.previewError.set('');
    } catch {
      // L'ultima anteprima buona resta visibile; nessuna anteprima viene inventata.
      if (gen === this.generation) this.previewError.set("Impossibile caricare l'anteprima.");
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
    const current = this.run();
    if (!data || typeof data.id !== 'string' || data.kind !== 'Analysis' || data.id !== current?.id) {
      return;
    }
    this.track(projectId, data, this.generation);
  }

  private async refresh(projectId: string, runId: string, gen: number): Promise<void> {
    try {
      const run = await this.api.getRun(projectId, runId);
      this.track(projectId, run, gen);
    } catch {
      // Errore di trasporto: la riconciliazione successiva riprova; lo stato non viene inventato.
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
