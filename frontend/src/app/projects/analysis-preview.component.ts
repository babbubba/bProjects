import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { AuthService } from '../auth/auth.service';
import {
  AnalysisContent,
  AnalysisItem,
  AnalysisView,
  ProcessingRunView,
  ProjectApiService,
} from './project-api.service';
import { describeFailure } from './failure-text';
import { ProjectEventsService, ProjectStreamEvent } from './project-events.service';
import type { CurrentRevision } from './document-view.component';

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

type SectionKey =
  | 'goals'
  | 'overview'
  | 'included'
  | 'excluded'
  | 'requirements'
  | 'flows'
  | 'constraints'
  | 'dependencies'
  | 'assumptions'
  | 'questions';

interface SectionDef {
  key: SectionKey;
  /** Percorso della sezione nel contenuto (come negli errori di campo del server). */
  path: string;
  title: string;
  /** Classificazione delle voci aggiunte dal PM. */
  added: AnalysisItem['classification'];
  get: (c: AnalysisContent) => AnalysisItem[];
  set: (c: AnalysisContent, items: AnalysisItem[]) => AnalysisContent;
}

const sectionDefs: SectionDef[] = [
  {
    key: 'goals',
    path: 'goals',
    title: 'Obiettivi',
    added: 'Declared',
    get: (c) => c.goals,
    set: (c, goals) => ({ ...c, goals }),
  },
  {
    key: 'overview',
    path: 'contextAndScope.overview',
    title: 'Contesto',
    added: 'Declared',
    get: (c) => c.contextAndScope.overview,
    set: (c, overview) => ({ ...c, contextAndScope: { ...c.contextAndScope, overview } }),
  },
  {
    key: 'included',
    path: 'contextAndScope.included',
    title: 'In perimetro',
    added: 'Declared',
    get: (c) => c.contextAndScope.included,
    set: (c, included) => ({ ...c, contextAndScope: { ...c.contextAndScope, included } }),
  },
  {
    key: 'excluded',
    path: 'contextAndScope.excluded',
    title: 'Fuori perimetro',
    added: 'Declared',
    get: (c) => c.contextAndScope.excluded,
    set: (c, excluded) => ({ ...c, contextAndScope: { ...c.contextAndScope, excluded } }),
  },
  {
    key: 'requirements',
    path: 'requirements',
    title: 'Requisiti',
    added: 'Declared',
    get: (c) => c.requirements,
    set: (c, requirements) => ({ ...c, requirements }),
  },
  {
    key: 'flows',
    path: 'operationalFlows',
    title: 'Flussi operativi',
    added: 'Declared',
    get: (c) => c.operationalFlows,
    set: (c, operationalFlows) => ({ ...c, operationalFlows }),
  },
  {
    key: 'constraints',
    path: 'constraints',
    title: 'Vincoli',
    added: 'Declared',
    get: (c) => c.constraints,
    set: (c, constraints) => ({ ...c, constraints }),
  },
  {
    key: 'dependencies',
    path: 'dependencies',
    title: 'Integrazioni e dipendenze',
    added: 'Declared',
    get: (c) => c.dependencies,
    set: (c, dependencies) => ({ ...c, dependencies }),
  },
  {
    key: 'assumptions',
    path: 'assumptions',
    title: 'Assunzioni',
    added: 'Assumed',
    get: (c) => c.assumptions,
    set: (c, assumptions) => ({ ...c, assumptions }),
  },
  {
    key: 'questions',
    path: 'openQuestions',
    title: 'Punti aperti (lacune)',
    added: 'Assumed',
    get: (c) => c.openQuestions,
    set: (c, openQuestions) => ({ ...c, openQuestions }),
  },
];

const fieldWords: Record<string, string> = {
  text: 'testo',
  evidenceRefs: 'fonti',
  classification: 'classificazione',
  description: 'descrizione',
  resolution: 'risoluzione',
  reason: 'motivazione',
  selectedAlternativeIds: 'scelta delle alternative',
  alternatives: 'alternative',
  status: 'stato',
  sources: 'fonti',
};

/** Errore di campo restituito dal server in 422 preview_invalid. */
export interface FieldError {
  field: string;
  message: string;
}

/** Motivazione e scelta in corso di un conflitto (buffer di modifica prima di «Segna come risolto»). */
interface ResolutionDraft {
  selected: string[];
  reason: string;
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
    () =>
      !this.loading() &&
      !this.active() &&
      this.analyzeState() !== 'sending' &&
      !this.dirty() &&
      !this.busy(),
  );
  protected readonly runError = computed(() => {
    const run = this.run();
    if (run?.status !== 'Failed') return '';
    return describeFailure(run.error, errorLabels, "L'analisi non è riuscita.", 'analysis');
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
  protected readonly sectionDefs = sectionDefs;

  // Form semplice (T035): la bozza è una copia dell'ultima revisione caricata; il server resta la fonte di verità.
  protected readonly draft = signal<AnalysisContent | null>(null);
  protected readonly saving = signal(false);
  protected readonly confirming = signal(false);
  protected readonly saveError = signal('');
  protected readonly serverErrors = signal<FieldError[]>([]);
  /** Revisione più recente del server quando il salvataggio è rifiutato per base obsoleta (409): la bozza resta intatta. */
  protected readonly conflictView = signal<AnalysisView | null>(null);
  private readonly resolutions = signal<Record<string, ResolutionDraft>>({});

  protected readonly busy = computed(() => this.saving() || this.confirming());
  protected readonly canEdit = computed(() => !!this.analysis()?.canEdit && !this.active());
  protected readonly dirty = computed(() => {
    const base = this.analysis()?.content;
    const draft = this.draft();
    return !!base && !!draft && JSON.stringify(base) !== JSON.stringify(draft);
  });
  protected readonly clientErrors = computed<string[]>(() => {
    const draft = this.draft();
    if (!draft) return [];
    const errors: string[] = [];
    for (const def of sectionDefs) {
      def.get(draft).forEach((item, i) => {
        if (!item.text.trim()) errors.push(`${def.title}, voce ${i + 1}: il testo è obbligatorio.`);
      });
    }
    draft.conflicts.forEach((c, i) => {
      if (!c.description.trim()) errors.push(`Conflitto ${i + 1}: la descrizione è obbligatoria.`);
      c.alternatives.forEach((a, j) => {
        if (!a.text.trim()) {
          errors.push(`Conflitto ${i + 1}, alternativa ${j + 1}: il testo è obbligatorio.`);
        }
      });
    });
    return errors;
  });
  protected readonly canSave = computed(
    () =>
      this.canEdit() &&
      this.dirty() &&
      !this.busy() &&
      !this.conflictView() &&
      this.clientErrors().length === 0,
  );
  protected readonly canConfirm = computed(
    () =>
      this.canEdit() &&
      !this.dirty() &&
      !this.busy() &&
      !this.conflictView() &&
      !!this.analysis()?.canConfirm,
  );
  /** Genera è offerto soltanto sulla revisione confermata, corrente e senza modifiche pendenti. */
  protected readonly canGenerate = computed(() => {
    const view = this.analysis();
    return (
      !!view &&
      view.validationState === 'PmConfirmed' &&
      view.canGenerate &&
      !this.dirty() &&
      !this.active() &&
      !this.busy() &&
      !this.generationBusy()
    );
  });
  protected readonly primaryAction = computed<'save' | 'confirm' | 'generate' | null>(() => {
    if (this.dirty()) return 'save';
    if (this.canConfirm()) return 'confirm';
    if (this.canGenerate()) return 'generate';
    return null;
  });
  private readonly sourceLabels = computed(
    () =>
      new Map((this.analysis()?.content.sources ?? []).map((s, i) => [s.id, `F${i + 1}`] as const)),
  );

  /** Richiesta esplicita di generazione sulla revisione confermata: il comando è eseguito dalla vista documento (T042). */
  readonly generateRequested = output<string>();
  /** Una generazione è in invio o in corso (stato reale dalla vista documento): Genera resta disabilitato. */
  readonly generationBusy = input(false);
  /** Revisione corrente e conferma (null senza analisi): permette alla bozza di riconoscere revisioni più recenti. */
  readonly viewChanged = output<CurrentRevision | null>();

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
    effect(() => {
      const view = this.analysis();
      const confirmed = !!view && view.validationState === 'PmConfirmed' && !this.dirty();
      this.viewChanged.emit(
        view
          ? { analysisId: view.analysisId, revisionId: view.currentRevisionId, confirmed }
          : null,
      );
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

  protected itemsOf(def: SectionDef): AnalysisItem[] {
    const draft = this.draft();
    return draft ? def.get(draft) : [];
  }

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  /** Voce o campo con errore: testo vuoto (controllo locale) o segnalato dal server per quel percorso. */
  protected itemInvalid(def: SectionDef, index: number, item: AnalysisItem): boolean {
    const prefix = `${def.path}[${index}]`;
    return !item.text.trim() || this.serverErrors().some((e) => e.field.startsWith(prefix));
  }

  protected conflictInvalid(index: number): boolean {
    return this.serverErrors().some((e) => e.field.startsWith(`conflicts[${index}]`));
  }

  /** «goals[0].text» → «Obiettivi, voce 1 (testo)»: il percorso del server reso leggibile, mai il contenuto dell'utente. */
  protected fieldLabel(field: string): string {
    const rest = (tail: string) => {
      const word =
        tail
          .replace(/^\./, '')
          .split('.')
          .pop()
          ?.replace(/\[\d+\]$/, '') ?? '';
      return fieldWords[word] ? ` (${fieldWords[word]})` : '';
    };
    for (const def of sectionDefs) {
      const m = new RegExp(`^${def.path.replace('.', '\\.')}\\[(\\d+)\\](.*)$`).exec(field);
      if (m) return `${def.title}, voce ${Number(m[1]) + 1}${rest(m[2])}`;
    }
    const conflict = /^conflicts\[(\d+)\](.*)$/.exec(field);
    if (conflict) {
      const alt = /^\.alternatives\[(\d+)\](.*)$/.exec(conflict[2]);
      return alt
        ? `Conflitto ${Number(conflict[1]) + 1}, alternativa ${Number(alt[1]) + 1}${rest(alt[2])}`
        : `Conflitto ${Number(conflict[1]) + 1}${rest(conflict[2])}`;
    }
    return /^sources/.test(field) ? 'Fonti' : field;
  }

  private edit(change: (content: AnalysisContent) => AnalysisContent): void {
    const draft = this.draft();
    if (!draft || !this.canEdit() || this.busy()) return;
    this.draft.set(change(draft));
    this.serverErrors.set([]);
  }

  protected setItemText(def: SectionDef, index: number, text: string): void {
    this.edit((c) =>
      def.set(
        c,
        def.get(c).map((item, i) => (i === index ? { ...item, text } : item)),
      ),
    );
  }

  protected addItem(def: SectionDef): void {
    this.edit((c) =>
      def.set(c, [
        ...def.get(c),
        { id: crypto.randomUUID(), text: '', classification: def.added, evidenceRefs: [] },
      ]),
    );
  }

  protected removeItem(def: SectionDef, index: number): void {
    this.edit((c) =>
      def.set(
        c,
        def.get(c).filter((_, i) => i !== index),
      ),
    );
  }

  private editConflict(
    index: number,
    change: (
      conflict: AnalysisContent['conflicts'][number],
    ) => AnalysisContent['conflicts'][number],
  ): void {
    this.edit((c) => ({
      ...c,
      conflicts: c.conflicts.map((conflict, i) => (i === index ? change(conflict) : conflict)),
    }));
  }

  protected setConflictDescription(index: number, description: string): void {
    this.editConflict(index, (c) => ({ ...c, description }));
  }

  protected setAlternativeText(index: number, alt: number, text: string): void {
    this.editConflict(index, (c) => ({
      ...c,
      alternatives: c.alternatives.map((a, j) => (j === alt ? { ...a, text } : a)),
    }));
  }

  protected resolutionOf(conflict: AnalysisContent['conflicts'][number]): ResolutionDraft {
    return (
      this.resolutions()[conflict.id] ?? {
        selected: conflict.resolution?.selectedAlternativeIds ?? [],
        reason: conflict.resolution?.reason ?? '',
      }
    );
  }

  private patchResolution(
    conflict: AnalysisContent['conflicts'][number],
    patch: Partial<ResolutionDraft>,
  ): void {
    if (!this.canEdit() || this.busy()) return;
    this.resolutions.update((r) => ({
      ...r,
      [conflict.id]: { ...this.resolutionOf(conflict), ...patch },
    }));
  }

  protected toggleAlternative(
    conflict: AnalysisContent['conflicts'][number],
    altId: string,
    checked: boolean,
  ): void {
    const selected = this.resolutionOf(conflict).selected.filter((id) => id !== altId);
    this.patchResolution(conflict, { selected: checked ? [...selected, altId] : selected });
  }

  protected setResolutionReason(
    conflict: AnalysisContent['conflicts'][number],
    reason: string,
  ): void {
    this.patchResolution(conflict, { reason });
  }

  /** La risoluzione richiede almeno un'alternativa scelta e una motivazione non vuota. */
  protected canResolve(conflict: AnalysisContent['conflicts'][number]): boolean {
    const r = this.resolutionOf(conflict);
    return this.canEdit() && !this.busy() && r.selected.length > 0 && r.reason.trim().length > 0;
  }

  protected resolveConflict(index: number): void {
    const conflict = this.draft()?.conflicts[index];
    if (!conflict || !this.canResolve(conflict)) return;
    const r = this.resolutionOf(conflict);
    this.editConflict(index, (c) => ({
      ...c,
      status: 'Resolved',
      resolution: {
        selectedAlternativeIds: r.selected,
        reason: r.reason.trim(),
        evidenceRefs: c.resolution?.evidenceRefs ?? [],
      },
    }));
  }

  /** Rinvia: il conflitto resta (o torna) aperto, senza risoluzione. */
  protected deferConflict(index: number): void {
    const conflict = this.draft()?.conflicts[index];
    if (!conflict) return;
    this.resolutions.update((r) => {
      const { [conflict.id]: _, ...rest } = r;
      return rest;
    });
    this.editConflict(index, (c) => ({ ...c, status: 'Open', resolution: null }));
  }

  /** Scarta le modifiche non salvate e riparte dall'ultima revisione caricata. */
  protected discard(): void {
    const view = this.analysis();
    if (view && !this.busy()) this.applyView(view);
  }

  private applyView(view: AnalysisView): void {
    this.analysis.set(view);
    this.draft.set(JSON.parse(JSON.stringify(view.content)) as AnalysisContent);
    this.resolutions.set({});
    this.conflictView.set(null);
    this.serverErrors.set([]);
    this.saveError.set('');
  }

  protected async save(): Promise<void> {
    const view = this.analysis();
    const draft = this.draft();
    if (!view || !draft || !this.canSave()) return;
    const gen = this.generation;
    const projectId = this.projectId();
    this.saving.set(true);
    this.saveError.set('');
    this.serverErrors.set([]);
    try {
      const saved = await this.api.savePreview(
        projectId,
        view.analysisId,
        view.currentRevisionId,
        draft,
      );
      if (gen === this.generation) this.applyView(saved);
    } catch (e) {
      if (gen === this.generation)
        await this.onPreviewFailure(e, projectId, view.analysisId, gen, 'save');
    } finally {
      if (gen === this.generation) this.saving.set(false);
    }
  }

  protected async confirm(): Promise<void> {
    const view = this.analysis();
    if (!view || !this.canConfirm()) return;
    const gen = this.generation;
    const projectId = this.projectId();
    this.confirming.set(true);
    this.saveError.set('');
    try {
      const confirmed = await this.api.confirmPreview(
        projectId,
        view.analysisId,
        view.currentRevisionId,
      );
      if (gen === this.generation) this.applyView(confirmed);
    } catch (e) {
      if (gen === this.generation)
        await this.onPreviewFailure(e, projectId, view.analysisId, gen, 'confirm');
    } finally {
      if (gen === this.generation) this.confirming.set(false);
    }
  }

  /** Richiesta di generazione: solo sulla revisione confermata; il POST è eseguito dalla vista documento. */
  protected generate(): void {
    const view = this.analysis();
    if (view && this.canGenerate()) this.generateRequested.emit(view.currentRevisionId);
  }

  /** Esiti d'errore di PUT/confirm: la bozza dell'utente non viene mai sostituita da un errore. */
  private async onPreviewFailure(
    e: unknown,
    projectId: string,
    analysisId: string,
    gen: number,
    action: 'save' | 'confirm',
  ): Promise<void> {
    const verb = action === 'save' ? 'Il salvataggio' : 'La conferma';
    const mine = action === 'save' ? ' Le tue modifiche restano nel modulo.' : '';
    if (!(e instanceof HttpErrorResponse)) {
      this.saveError.set(`${verb} non è riuscito.${mine} Riprova.`);
      return;
    }
    const body = (e.error ?? {}) as {
      code?: string;
      errors?: FieldError[];
      processingRunId?: string;
    };
    if (e.status === 422) {
      const errors = (body.errors ?? []).filter((x) => typeof x?.field === 'string');
      this.serverErrors.set(errors);
      this.saveError.set(
        `${verb} è stato rifiutato: i campi indicati non sono validi. Nulla è stato salvato.${mine}`,
      );
    } else if (e.status === 409 && body.code === 'revision_conflict') {
      await this.onRevisionConflict(projectId, analysisId, gen, verb);
    } else if (e.status === 409 && body.code === 'processing_active') {
      this.saveError.set(
        `Un'analisi è in corso: l'anteprima è bloccata finché non termina.${mine}`,
      );
      if (body.processingRunId) void this.adopt(projectId, body.processingRunId, gen);
    } else if (e.status === 409 && body.code === 'analysis_not_current') {
      this.saveError.set(
        `Questa non è più l'analisi corrente del progetto: ricarica la pagina.${mine}`,
      );
    } else if (e.status === 401) {
      this.saveError.set('Sessione scaduta: accedi di nuovo.');
    } else {
      this.saveError.set(`${verb} non è riuscito.${mine} Riprova.`);
    }
  }

  /** Base obsoleta (409): legge la revisione corrente ma lascia la bozza intatta finché il PM non sceglie. */
  private async onRevisionConflict(
    projectId: string,
    analysisId: string,
    gen: number,
    verb: string,
  ): Promise<void> {
    try {
      const latest = await this.api.getAnalysis(projectId, analysisId);
      if (gen !== this.generation) return;
      if (this.dirty()) {
        this.conflictView.set(latest);
        this.saveError.set(
          `${verb} non è stato eseguito: l'anteprima è stata modificata da un'altra revisione (versione ${latest.version}). Le tue modifiche non sono state salvate né sovrascritte.`,
        );
      } else {
        this.applyView(latest);
        this.saveError.set(
          `L'anteprima era cambiata (versione ${latest.version}): ora vedi la versione corrente. Riprova.`,
        );
      }
    } catch {
      this.saveError.set(
        `${verb} non è stato eseguito: la revisione non è più la corrente e non è stato possibile rileggerla. Le tue modifiche restano nel modulo.`,
      );
    }
  }

  /** Conflitto di revisione: ricarica la versione corrente scartando le modifiche locali. */
  protected reloadLatest(): void {
    const latest = this.conflictView();
    if (latest) this.applyView(latest);
  }

  /** Conflitto di revisione: riapplica le modifiche locali come nuova revisione sopra quella corrente. */
  protected keepMine(): void {
    const latest = this.conflictView();
    const draft = this.draft();
    if (!latest || !draft) return;
    this.analysis.set(latest);
    this.draft.set({
      ...draft,
      sources: latest.content.sources,
      validationState: latest.content.validationState,
    });
    this.conflictView.set(null);
    this.saveError.set('');
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
    this.draft.set(null);
    this.resolutions.set({});
    this.conflictView.set(null);
    this.saveError.set('');
    this.serverErrors.set([]);
    this.saving.set(false);
    this.confirming.set(false);
    this.idempotencyKey = null;
    this.analyzeState.set('idle');
    this.commandError.set('');
    this.previewError.set('');
    this.loadError.set('');
    this.loading.set(true);
    try {
      await this.restore(projectId, gen);
    } catch {
      if (gen === this.generation)
        this.loadError.set("Impossibile caricare lo stato dell'analisi.");
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
      // Una nuova revisione non sovrascrive mai modifiche non salvate dello stesso documento.
      if (this.dirty() && this.analysis()?.analysisId === view.analysisId) {
        if (view.currentRevisionId !== this.analysis()?.currentRevisionId)
          this.conflictView.set(view);
        return;
      }
      this.applyView(view);
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
    if (
      !data ||
      typeof data.id !== 'string' ||
      data.kind !== 'Analysis' ||
      data.id !== current?.id
    ) {
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
