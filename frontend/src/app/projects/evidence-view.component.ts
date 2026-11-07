import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
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
  CoverageFinding,
  CoverageReportSummary,
  CoverageReportView,
  EvidenceBatchView,
  EvidenceDetailView,
  EvidenceStatusView,
  EvidenceApiService,
  EvidenceEnvelope,
  FindingCategory,
  GapAssessmentView,
  MessageVersionsView,
  VersionDiffView,
} from './evidence-api.service';
import {
  categoryHint,
  categoryLabel,
  certaintyLabel,
  certaintyLabels,
  confidenceLabel,
  diffFieldLabel,
  diffStatusLabel,
  evidenceRows,
  excerpt,
  highlightSegments,
  isDocumentEvidence,
  isPromotionConflict,
  isUncertainOutcome,
  jobErrorText,
  kindLabel,
  kindLabels,
  locatorText,
  plural,
  polarityLabel,
  polarityLabels,
  problemOf,
  qualifierLabel,
  relationLabel,
  sourceGroups,
  sourceRoleLabel,
  stateBadge,
  structureFields,
} from './evidence-format';
import { DocumentPassageComponent } from './document-passage.component';
import { ProjectApiService } from './project-api.service';
import { ProjectEventsService, ProjectStreamEvent } from './project-events.service';

/** Intervallo (ms) di riconciliazione mentre ci sono lavori aperti; 0 disattiva il polling. */
export type GapView =
  | { available: false }
  | {
      available: true;
      expected: number;
      assessed: number;
      answered: number;
      open: number;
      unassessed: number;
      items: GapAssessmentView[];
      aiRevisionChanged: boolean;
    };

const gapOutcomes = ['AnsweredInSnapshot', 'Open'];
const gapReasons = ['Answered', 'NoAnswer', 'Partial', 'Ambiguous'];

function validGapAssessment(g: GapAssessmentView): boolean {
  return (
    !!g &&
    typeof g.gapEvidenceId === 'string' &&
    gapOutcomes.includes(g.outcome) &&
    gapReasons.includes(g.reason) &&
    typeof g.rationale === 'string' &&
    Array.isArray(g.supportingEvidenceIds) &&
    g.supportingEvidenceIds.every((id) => typeof id === 'string')
  );
}

export const EVIDENCE_POLL_MS = new InjectionToken<number>('EVIDENCE_POLL_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

const timeFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'short', timeStyle: 'short' });
const categories: FindingCategory[] = ['DeterministicError', 'SemanticRisk', 'PmAssessment'];

interface Filters {
  messageId: string;
  kind: string;
  polarity: string;
  certainty: string;
  originKind: string;
}
const noFilters: Filters = { messageId: '', kind: '', polarity: '', certainty: '', originKind: '' };

/**
 * Vista leggera del registro delle evidenze: stato reale delle estrazioni, evidenze correnti con il testo originale
 * evidenziato, versioni (confronto e promozione esplicita) e verifica di copertura. Mostra solo dati strutturati del
 * server: mai prompt, ragionamento o output non validato del modello. Nessun comando parte senza un'azione del PM.
 */
@Component({
  selector: 'app-evidence-view',
  imports: [DocumentPassageComponent],
  templateUrl: './evidence-view.component.html',
  styleUrl: './evidence-view.component.scss',
})
export class EvidenceViewComponent {
  private readonly api = inject(EvidenceApiService);
  private readonly projects = inject(ProjectApiService);
  private readonly events = inject(ProjectEventsService);
  private readonly auth = inject(AuthService);
  private readonly pollMs = inject(EVIDENCE_POLL_MS);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly projectId = input.required<string>();
  /** Analisi e revisione correnti (null = nessuna analisi: la copertura non è disponibile). */
  readonly analysisId = input<string | null>(null);
  readonly revisionId = input<string | null>(null);

  // Etichette per il template.
  protected readonly kindLabel = kindLabel;
  protected readonly polarityLabel = polarityLabel;
  protected readonly certaintyLabel = certaintyLabel;
  protected readonly qualifierLabel = qualifierLabel;
  protected readonly relationLabel = relationLabel;
  protected readonly sourceRoleLabel = sourceRoleLabel;
  protected readonly diffStatusLabel = diffStatusLabel;
  protected readonly diffFieldLabel = diffFieldLabel;
  protected readonly categoryLabel = categoryLabel;
  protected readonly categoryHint = categoryHint;
  protected readonly confidenceLabel = confidenceLabel;
  protected readonly structureFields = structureFields;
  protected readonly locatorText = locatorText;
  protected readonly isDocument = isDocumentEvidence;

  protected kindRationale(e: EvidenceDetailView['evidence']): string | null {
    return isDocumentEvidence(e) ? e.content.kindRationale : e.kindRationale;
  }

  /** « (messaggio #N)» solo per relazioni con evidenze di messaggio: per i documenti il numero non esiste. */
  protected relationSequence(r: EvidenceDetailView['relations'][number]): string {
    return 'otherMessageSequence' in r ? ` (messaggio #${r.otherMessageSequence})` : '';
  }
  protected readonly plural = plural;
  protected readonly kindOptions = Object.entries(kindLabels);
  protected readonly polarityOptions = Object.entries(polarityLabels);
  protected readonly certaintyOptions = Object.entries(certaintyLabels);

  // Stato delle estrazioni.
  protected readonly status = signal<EvidenceStatusView | null>(null);
  protected readonly loading = signal(false);
  protected readonly statusError = signal('');
  protected readonly texts = signal<Record<string, string>>({});
  protected readonly batches = signal<EvidenceBatchView[]>([]);
  protected readonly busy = signal<ReadonlySet<string>>(new Set());
  protected readonly notice = signal('');
  protected readonly commandError = signal('');

  protected readonly rows = computed(() =>
    (this.status()?.messages ?? []).map((m) => {
      const job = m.latestJob;
      const active = m.state === 'pending' || m.state === 'running';
      return {
        m,
        badge: stateBadge(m.state),
        text: excerpt(this.texts()[m.messageId]),
        active,
        retryJobId: job?.status === 'Failed' ? job.id : null,
        error: job?.status === 'Failed' ? jobErrorText(job.error) : '',
        attempt:
          active && job && job.attemptCount > 1
            ? `tentativo ${job.attemptCount} di ${job.maxAttempts}`
            : '',
      };
    }),
  );
  protected readonly queueText = computed(() => {
    const q = this.status()?.queue;
    if (!q) return '';
    const parts = [
      q.queued ? `${q.queued} in coda` : '',
      q.running ? `${q.running} in elaborazione` : '',
      q.failed ? `${plural(q.failed, 'fallito', 'falliti')}` : '',
    ].filter(Boolean);
    return parts.length ? `Lavori: ${parts.join(', ')}.` : 'Nessun lavoro di estrazione aperto.';
  });

  // Evidenze correnti.
  protected readonly filters = signal<Filters>(noFilters);
  protected readonly items = signal<EvidenceEnvelope[]>([]);
  /** Righe di lista (messaggi e documenti) e numero di elementi di versione ignota, non mostrati ma dichiarati. */
  protected readonly list = computed(() => evidenceRows(this.items()));
  protected readonly itemsLoading = signal(false);
  protected readonly itemsError = signal('');
  protected readonly selectedId = signal<string | null>(null);
  protected readonly detail = signal<EvidenceDetailView | null>(null);
  protected readonly detailLoading = signal(false);
  protected readonly detailError = signal('');
  protected readonly messageOptions = computed(() =>
    (this.status()?.messages ?? []).map((m) => ({ id: m.messageId, sequence: m.sequence })),
  );
  protected readonly hasFilters = computed(() => Object.values(this.filters()).some(Boolean));
  /** Testi originali del dettaglio, con i passi citati evidenziati (segmenti, mai HTML). */
  protected readonly detailMessages = computed(() => {
    const d = this.detail();
    if (!d || isDocumentEvidence(d.evidence)) return [];
    const sources = d.evidence.sources;
    return d.sourceMessages.map((m) => ({
      id: m.id,
      sequence: m.sequence,
      assistant: m.role === 'assistant',
      segments: highlightSegments(
        m.text,
        sources.filter((s) => s.messageId === m.id),
      ),
    }));
  });
  /** Evidenza documentale aperta: parti citate raggruppate per citazione, con il locator risolto dal server. */
  protected readonly detailGroups = computed(() => {
    const d = this.detail();
    return d && isDocumentEvidence(d.evidence)
      ? sourceGroups(d.evidence.content.sources, d.sourceDocuments)
      : [];
  });
  /** Parti (sourceId) di cui è aperto il passaggio: una per volta oppure tutte le parti di un gruppo. */
  protected readonly openParts = signal<ReadonlySet<string>>(new Set());
  // Versioni di un messaggio.
  protected readonly versionsFor = signal<string | null>(null);
  protected readonly versions = signal<MessageVersionsView | null>(null);
  protected readonly versionsLoading = signal(false);
  protected readonly versionsError = signal('');
  protected readonly compareFrom = signal('');
  protected readonly compareTo = signal('');
  protected readonly diff = signal<VersionDiffView | null>(null);
  protected readonly diffLoading = signal(false);
  protected readonly diffError = signal('');
  protected readonly promoteTarget = signal<string | null>(null);
  protected readonly promoteReason = signal('');
  protected readonly versionsTitle = computed(() => {
    const v = this.versions();
    return v ? `Versioni del messaggio #${v.sequence}` : 'Versioni del messaggio';
  });

  // Copertura.
  protected readonly reports = signal<CoverageReportSummary[]>([]);
  protected readonly report = signal<CoverageReportView | null>(null);
  protected readonly coverageBusy = signal(false);
  protected readonly coverageError = signal('');
  protected readonly selectionInfo = signal('');
  protected readonly reconciliation = computed(() => this.report()?.reconciliation ?? null);
  protected readonly nonTerminal = computed(() =>
    (this.reconciliation()?.candidates ?? []).filter((c) => !c.terminal),
  );
  protected readonly openCandidates = computed(() =>
    (this.reconciliation()?.candidates ?? []).filter((c) => c.terminal && c.state === 'Open'),
  );
  /**
   * Valutazioni dei Gap: lette SOLO se il report è «coverage-3» (versione, mai presenza di campi). Per ogni altra versione, o se i campi di un
   * coverage-3 mancano o sono incoerenti, la valutazione è «non disponibile»: non è mai «zero Gap».
   */
  protected readonly gapView = computed<GapView>(() => {
    const r = this.report();
    if (!r || r.checkerVersion !== 'coverage-3') return { available: false };
    const s = r.summary;
    const list = r.gapAssessments;
    if (!s || !Array.isArray(list)) return { available: false };
    const counters = [s.expectedGaps, s.assessedGaps, s.answeredGaps, s.openGaps, s.unassessedGaps];
    if (!counters.every((n) => Number.isInteger(n) && (n as number) >= 0)) {
      return { available: false };
    }
    if (!list.every(validGapAssessment) || s.assessedGaps !== list.length) {
      return { available: false };
    }
    return {
      available: true,
      expected: s.expectedGaps!,
      assessed: s.assessedGaps!,
      answered: s.answeredGaps!,
      open: s.openGaps!,
      unassessed: s.unassessedGaps!,
      items: list,
      aiRevisionChanged: !!r.aiRevisionId && !!r.revisionId && r.aiRevisionId !== r.revisionId,
    };
  });
  private readonly listedIds = computed(() => new Set(this.list().rows.map((row) => row.id)));
  protected readonly groups = computed(() => {
    const findings = this.report()?.findings ?? [];
    return categories.map((category) => ({
      category,
      findings: findings.filter((f: CoverageFinding) => f.category === category),
    }));
  });

  private readonly keys = new Map<string, string>();
  private generation = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private syncing = false;
  private syncAgain = false;
  private signature: string | null = null;
  private loadingTexts = false;

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
      const id = this.projectId();
      const analysis = this.analysisId();
      untracked(() => void this.loadCoverage(id, analysis, this.generation));
    });
    // evidence.state: notifica effimera; la verità resta il GET di stato (riconciliato anche dal polling).
    effect((onCleanup) => {
      const id = this.projectId();
      if (!this.auth.isAuthenticated()) return;
      untracked(() => {
        const close = this.events.open(id, {
          onConnected: () => void this.sync(),
          onEvent: (e: ProjectStreamEvent) => {
            if (e.name === 'evidence.state') void this.sync();
          },
          onDisconnected: () => undefined,
        });
        onCleanup(close);
      });
    });
  }

  protected formatTime(iso: string | null | undefined): string {
    return iso ? timeFormat.format(new Date(iso)) : '';
  }

  protected isBusy(scope: string): boolean {
    return this.busy().has(scope);
  }

  protected batchText(b: EvidenceBatchView): string {
    const p = b.progress;
    const name = b.kind === 'Backfill' ? 'Backfill' : 'Rianalisi del progetto';
    if (b.jobCount === 0) {
      return `${name}: nessun messaggio da analizzare (${plural(b.skippedCount, 'saltato', 'saltati')}).`;
    }
    const done = p.completed + p.failed;
    const state =
      p.queued + p.running > 0 ? 'in corso' : p.failed > 0 ? 'terminato con errori' : 'completato';
    return `${name} ${state}: ${done} di ${b.jobCount} lavori terminati (${p.queued} in coda, ${p.running} in elaborazione, ${p.completed} completati, ${p.failed} falliti).`;
  }

  protected batchPercent(b: EvidenceBatchView): number {
    return b.jobCount > 0
      ? Math.round(((b.progress.completed + b.progress.failed) / b.jobCount) * 100)
      : 100;
  }

  // ---------------------------------------------------------------- comandi di estrazione

  protected async reanalyze(messageId: string, sequence: number): Promise<void> {
    const job = await this.command(`re:${messageId}`, (key) =>
      this.api.reanalyzeMessage(this.projectId(), messageId, key),
    );
    if (job) this.accepted(`Rianalisi del messaggio #${sequence} accodata.`);
  }

  protected async retry(jobId: string, sequence: number): Promise<void> {
    const job = await this.command(`retry:${jobId}`, (key) =>
      this.api.retryJob(this.projectId(), jobId, key),
    );
    if (job) this.accepted(`Nuovo tentativo per il messaggio #${sequence} accodato.`);
  }

  protected async reanalyzeProject(): Promise<void> {
    const batch = await this.command('batch:reanalysis', (key) =>
      this.api.reanalyzeProject(this.projectId(), key),
    );
    if (batch) this.trackBatch(batch, 'Rianalisi del progetto avviata.');
  }

  protected async backfill(): Promise<void> {
    const batch = await this.command('batch:backfill', (key) =>
      this.api.startBackfill(this.projectId(), key),
    );
    if (batch) this.trackBatch(batch, 'Backfill avviato.');
  }

  /**
   * Esegue un comando con Idempotency-Key: una chiave nuova per ogni azione dell'utente; dopo un esito incerto
   * (rete, timeout, 5xx) la chiave resta e il clic successivo ripete la stessa richiesta senza duplicarla.
   */
  private async command<T>(scope: string, call: (key: string) => Promise<T>): Promise<T | null> {
    if (this.isBusy(scope)) return null;
    const key = this.keys.get(scope) ?? crypto.randomUUID();
    this.keys.set(scope, key);
    this.setBusy(scope, true);
    const gen = this.generation;
    this.commandError.set('');
    this.notice.set('');
    try {
      const result = await call(key);
      this.keys.delete(scope);
      return gen === this.generation ? result : null;
    } catch (e) {
      const uncertain = isUncertainOutcome(e);
      if (!uncertain) this.keys.delete(scope);
      if (gen === this.generation) {
        this.commandError.set(this.commandMessage(e, uncertain));
        if (problemOf(e)?.status === 409) void this.sync();
      }
      return null;
    } finally {
      this.setBusy(scope, false);
    }
  }

  private setBusy(scope: string, on: boolean): void {
    this.busy.update((set) => {
      const next = new Set(set);
      if (on) next.add(scope);
      else next.delete(scope);
      return next;
    });
  }

  private accepted(message: string): void {
    this.notice.set(message);
    void this.sync();
  }

  private trackBatch(batch: EvidenceBatchView, message: string): void {
    this.batches.update((list) => [batch, ...list.filter((b) => b.kind !== batch.kind)]);
    this.accepted(message);
  }

  private commandMessage(e: unknown, uncertain: boolean): string {
    if (uncertain) {
      return 'Non sappiamo se la richiesta sia stata ricevuta (connessione o server non disponibili). Premi di nuovo il pulsante: verrà ripetuta la stessa richiesta senza crearne una seconda.';
    }
    const p = problemOf(e);
    if (p?.status === 401) return 'Sessione scaduta: accedi di nuovo.';
    if (p?.status === 404) return 'Messaggio o progetto non trovato: ricarica la pagina.';
    switch (p?.code) {
      case 'extraction_active':
        return "Un'estrazione di questo messaggio è già in corso: attendi che termini.";
      case 'job_not_failed':
        return 'Il lavoro non risulta più fallito: lo stato è stato aggiornato.';
      case 'no_pm_input':
        return 'Non ci sono messaggi del PM da analizzare.';
      case 'idempotency_key_required':
        return 'Richiesta non valida: ricarica la pagina e riprova.';
    }
    return 'Richiesta non riuscita. Riprova.';
  }

  // ---------------------------------------------------------------- caricamento e polling

  private async reset(projectId: string): Promise<void> {
    const gen = ++this.generation;
    this.stopPolling();
    this.keys.clear();
    this.busy.set(new Set());
    this.status.set(null);
    this.texts.set({});
    this.batches.set([]);
    this.items.set([]);
    this.filters.set(noFilters);
    this.selectedId.set(null);
    this.detail.set(null);
    this.versionsFor.set(null);
    this.versions.set(null);
    this.diff.set(null);
    this.notice.set('');
    this.commandError.set('');
    this.statusError.set('');
    this.signature = null;
    this.loading.set(true);
    try {
      await Promise.all([this.refreshStatus(projectId, gen), this.loadItems(gen)]);
    } finally {
      if (gen === this.generation) this.loading.set(false);
    }
    if (gen === this.generation) this.schedulePoll();
  }

  /** Riconcilia stato e batch con il server, poi pianifica il prossimo giro se ci sono lavori aperti. */
  private async sync(): Promise<void> {
    if (this.syncing) {
      this.syncAgain = true;
      return;
    }
    this.syncing = true;
    const gen = this.generation;
    try {
      do {
        this.syncAgain = false;
        const projectId = this.projectId();
        await Promise.all([
          this.refreshStatus(projectId, gen),
          this.refreshBatches(projectId, gen),
        ]);
      } while (this.syncAgain && gen === this.generation);
    } finally {
      this.syncing = false;
    }
    if (gen === this.generation) this.schedulePoll();
  }

  private async refreshStatus(projectId: string, gen: number): Promise<void> {
    try {
      const status = await this.api.getStatus(projectId);
      if (gen !== this.generation) return;
      this.status.set(status);
      this.statusError.set('');
      const signature = status.messages.map((m) => m.currentVersion?.id ?? '-').join(',');
      const changed = this.signature !== null && this.signature !== signature;
      this.signature = signature;
      if (changed) {
        void this.loadItems(gen);
        const open = this.versionsFor();
        if (open) void this.loadVersions(open, gen, true);
      }
      void this.ensureTexts(projectId, status, gen);
    } catch (e) {
      if (gen === this.generation) {
        this.statusError.set(
          problemOf(e)?.status === 404
            ? 'Progetto non trovato.'
            : 'Impossibile caricare lo stato delle evidenze.',
        );
      }
    }
  }

  private async refreshBatches(projectId: string, gen: number): Promise<void> {
    const open = this.batches().filter((b) => b.progress.queued + b.progress.running > 0);
    await Promise.all(
      open.map(async (b) => {
        try {
          const fresh = await this.api.getBatch(projectId, b.id);
          if (gen === this.generation) {
            this.batches.update((list) => list.map((x) => (x.id === fresh.id ? fresh : x)));
          }
        } catch {
          // Il prossimo giro riprova; lo stato globale arriva comunque da /evidence/status.
        }
      }),
    );
  }

  /** Testi dei messaggi (solo per l'estratto): letti una volta e quando compaiono messaggi nuovi. */
  private async ensureTexts(
    projectId: string,
    status: EvidenceStatusView,
    gen: number,
  ): Promise<void> {
    const known = this.texts();
    if (this.loadingTexts || status.messages.every((m) => m.messageId in known)) return;
    this.loadingTexts = true;
    try {
      const map: Record<string, string> = {};
      let after: number | null = null;
      for (let page = 0; page < 50; page++) {
        const result = await this.projects.listMessages(projectId, after, 100);
        for (const m of result.items) if (m.role === 'user') map[m.id] = m.text;
        after = result.nextAfterSequence;
        if (after === null) break;
      }
      if (gen === this.generation) this.texts.update((t) => ({ ...t, ...map }));
    } catch {
      // Senza testo la riga mostra solo il numero del messaggio: nulla viene inventato.
    } finally {
      this.loadingTexts = false;
    }
  }

  private needsPolling(): boolean {
    const status = this.status();
    const jobs = status?.messages.some(
      (m) => m.latestJob?.status === 'Queued' || m.latestJob?.status === 'Running',
    );
    const queue = !!status && status.queue.queued + status.queue.running > 0;
    return (
      !!jobs || queue || this.batches().some((b) => b.progress.queued + b.progress.running > 0)
    );
  }

  private schedulePoll(): void {
    if (this.pollMs <= 0 || this.timer || !this.needsPolling()) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.sync();
    }, this.pollMs);
  }

  private stopPolling(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  // ---------------------------------------------------------------- evidenze e dettaglio

  protected setFilter(field: keyof Filters, event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.filters.update((f) => ({ ...f, [field]: value }));
    this.selectedId.set(null);
    this.detail.set(null);
    void this.loadItems(this.generation);
  }

  protected clearFilters(): void {
    this.filters.set(noFilters);
    this.selectedId.set(null);
    this.detail.set(null);
    void this.loadItems(this.generation);
  }

  protected reloadItems(): void {
    void this.loadItems(this.generation);
  }

  private async loadItems(gen: number): Promise<void> {
    const projectId = this.projectId();
    this.itemsLoading.set(true);
    try {
      const items = await this.api.listEvidence(projectId, this.filters());
      if (gen !== this.generation) return;
      this.items.set(items);
      this.itemsError.set('');
      const selected = this.selectedId();
      if (selected && !items.some((i) => i.evidence.id === selected)) {
        this.selectedId.set(null);
        this.detail.set(null);
      }
    } catch (e) {
      if (gen === this.generation) {
        this.itemsError.set(
          problemOf(e)?.status === 422
            ? 'Filtri non validi.'
            : "Impossibile caricare l'elenco delle evidenze.",
        );
      }
    } finally {
      if (gen === this.generation) this.itemsLoading.set(false);
    }
  }

  protected togglePart(sourceId: string): void {
    this.openParts.update((set) => {
      const next = new Set(set);
      if (!next.delete(sourceId)) next.add(sourceId);
      return next;
    });
  }

  /** «Apri tutte le parti»: ogni parte di una citazione multi-blocco apre il proprio blocco, nessuna fusa con un'altra. */
  protected openGroup(group: ReturnType<typeof sourceGroups>[number]): void {
    this.openParts.update((set) => {
      const next = new Set(set);
      for (const p of group.parts) if (p.resolved) next.add(p.source.sourceId);
      return next;
    });
  }

  protected isListed(evidenceId: string): boolean {
    return this.listedIds().has(evidenceId);
  }

  /** Apre il dettaglio dell'evidenza nell'elenco esistente (nessun nuovo comando) e porta il focus sulla sua riga. */
  protected async openEvidenceRef(evidenceId: string): Promise<void> {
    if (this.selectedId() !== evidenceId) await this.toggleDetail(evidenceId);
    const row = this.host.nativeElement.querySelector<HTMLElement>(
      `[data-testid="evidence-${CSS.escape(evidenceId)}"]`,
    );
    if (!row) return;
    row.setAttribute('tabindex', '-1');
    row.scrollIntoView?.({ block: 'nearest' });
    row.focus();
  }

  protected gapOutcomeLabel(g: GapAssessmentView): string {
    return g.outcome === 'AnsweredInSnapshot' ? 'Risolto nello snapshot' : 'Ancora aperto';
  }

  protected gapReasonLabel(reason: GapAssessmentView['reason']): string {
    switch (reason) {
      case 'Answered':
        return 'Risposta presente';
      case 'NoAnswer':
        return 'Nessuna risposta';
      case 'Partial':
        return 'Risposta parziale';
      default:
        return 'Risposta ambigua';
    }
  }

  protected async toggleDetail(evidenceId: string): Promise<void> {
    this.openParts.set(new Set());
    if (this.selectedId() === evidenceId) {
      this.selectedId.set(null);
      this.detail.set(null);
      return;
    }
    const gen = this.generation;
    this.selectedId.set(evidenceId);
    this.detail.set(null);
    this.detailError.set('');
    this.detailLoading.set(true);
    try {
      const detail = await this.api.getEvidence(this.projectId(), evidenceId);
      if (gen === this.generation && this.selectedId() === evidenceId) this.detail.set(detail);
    } catch {
      if (gen === this.generation && this.selectedId() === evidenceId) {
        this.detailError.set("Impossibile caricare il dettaglio dell'evidenza.");
      }
    } finally {
      if (gen === this.generation && this.selectedId() === evidenceId)
        this.detailLoading.set(false);
    }
  }

  // ---------------------------------------------------------------- versioni, confronto, promozione

  protected openVersions(messageId: string): void {
    this.versionsFor.set(messageId);
    this.versions.set(null);
    this.diff.set(null);
    this.diffError.set('');
    this.promoteTarget.set(null);
    this.promoteReason.set('');
    void this.loadVersions(messageId, this.generation, false);
  }

  protected closeVersions(): void {
    this.versionsFor.set(null);
    this.versions.set(null);
    this.diff.set(null);
    this.promoteTarget.set(null);
  }

  /** Ricarica le versioni; `keep` conserva la scelta del confronto se le versioni scelte esistono ancora. */
  private async loadVersions(messageId: string, gen: number, keep: boolean): Promise<void> {
    this.versionsLoading.set(true);
    this.versionsError.set('');
    try {
      const view = await this.api.getVersions(this.projectId(), messageId);
      if (gen !== this.generation || this.versionsFor() !== messageId) return;
      this.versions.set(view);
      const ids = new Set(view.versions.map((v) => v.id));
      if (!keep || !ids.has(this.compareFrom()) || !ids.has(this.compareTo())) {
        const sorted = [...view.versions].sort((a, b) => b.version - a.version);
        const from = sorted.find((v) => v.isCurrent) ?? sorted[1];
        const to = sorted.find((v) => v.id !== from?.id);
        this.compareFrom.set(from?.id ?? '');
        this.compareTo.set(to?.id ?? '');
        this.diff.set(null);
      }
    } catch {
      if (gen === this.generation) {
        this.versionsError.set('Impossibile caricare le versioni del messaggio.');
      }
    } finally {
      if (gen === this.generation) this.versionsLoading.set(false);
    }
  }

  protected setCompare(side: 'from' | 'to', event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    (side === 'from' ? this.compareFrom : this.compareTo).set(value);
    this.diff.set(null);
    this.diffError.set('');
  }

  protected async compare(): Promise<void> {
    const from = this.compareFrom();
    const to = this.compareTo();
    if (!from || !to || from === to) {
      this.diffError.set('Scegli due versioni diverse da confrontare.');
      return;
    }
    const gen = this.generation;
    this.diffLoading.set(true);
    this.diffError.set('');
    try {
      const diff = await this.api.compareVersions(this.projectId(), from, to);
      if (gen === this.generation) this.diff.set(diff);
    } catch (e) {
      if (gen === this.generation) {
        this.diffError.set(
          problemOf(e)?.code === 'versions_not_comparable'
            ? 'Si confrontano solo versioni dello stesso messaggio.'
            : 'Impossibile confrontare le versioni.',
        );
      }
    } finally {
      if (gen === this.generation) this.diffLoading.set(false);
    }
  }

  protected askPromote(versionId: string): void {
    this.promoteTarget.set(versionId);
    this.promoteReason.set('');
  }

  protected cancelPromote(): void {
    this.promoteTarget.set(null);
  }

  protected setReason(event: Event): void {
    this.promoteReason.set((event.target as HTMLInputElement).value);
  }

  /**
   * Promozione esplicita della versione scelta, con `expectedStateRevision` letto dalle versioni caricate. Un 409
   * promotion_conflict significa che lo stato è cambiato: si ricarica tutto e si chiede al PM di ricontrollare.
   */
  protected async promote(): Promise<void> {
    const versionId = this.promoteTarget();
    const view = this.versions();
    const messageId = this.versionsFor();
    if (!versionId || !view || !messageId || this.isBusy('promote')) return;
    const gen = this.generation;
    this.setBusy('promote', true);
    this.commandError.set('');
    this.notice.set('');
    try {
      const result = await this.api.promote(this.projectId(), messageId, {
        versionId,
        expectedStateRevision: view.stateRevision,
        reason: this.promoteReason().trim() || null,
      });
      if (gen !== this.generation) return;
      const version = view.versions.find((v) => v.id === versionId)?.version;
      this.notice.set(
        result.promotion
          ? `Versione ${version} promossa a corrente. Le evidenze correnti sono state ricaricate.${this.impactText(result.promotion.relationImpact)}`
          : 'La versione scelta era già quella corrente.',
      );
      this.promoteTarget.set(null);
      await this.reloadAfterChange(messageId, gen);
    } catch (e) {
      if (gen !== this.generation) return;
      if (isPromotionConflict(e)) {
        this.commandError.set(
          'Lo stato è cambiato, ricarica: le versioni sono state aggiornate, controlla e riprova.',
        );
        this.promoteTarget.set(null);
        await this.reloadAfterChange(messageId, gen);
      } else if (problemOf(e)?.code === 'version_not_of_message') {
        this.commandError.set('La versione scelta non appartiene a questo messaggio.');
      } else {
        this.commandError.set('Promozione non riuscita. Riprova.');
      }
    } finally {
      this.setBusy('promote', false);
    }
  }

  /** Effetto della promozione sulle relazioni che altri messaggi puntavano alla versione uscente. */
  private impactText(impact: { remapped: number; stale: number } | null | undefined): string {
    if (!impact || (impact.remapped === 0 && impact.stale === 0)) return '';
    const parts: string[] = [];
    if (impact.remapped > 0) parts.push(`${impact.remapped} rimappate sulla nuova versione`);
    if (impact.stale > 0) parts.push(`${impact.stale} non più risolvibili (da verificare)`);
    return ` Relazioni da altri messaggi: ${parts.join(', ')}.`;
  }

  private async reloadAfterChange(messageId: string, gen: number): Promise<void> {
    this.diff.set(null);
    await Promise.all([
      this.loadVersions(messageId, gen, false),
      this.refreshStatus(this.projectId(), gen),
      this.loadItems(gen),
    ]);
  }

  // ---------------------------------------------------------------- copertura

  private async loadCoverage(
    projectId: string,
    analysisId: string | null,
    gen: number,
  ): Promise<void> {
    this.reports.set([]);
    this.report.set(null);
    this.coverageError.set('');
    this.selectionInfo.set('');
    if (!analysisId) return;
    try {
      const reports = await this.api.listCoverageReports(projectId, analysisId);
      if (gen === this.generation && this.analysisId() === analysisId) this.reports.set(reports);
    } catch {
      if (gen === this.generation)
        this.coverageError.set('Impossibile caricare i report di copertura.');
    }
    try {
      const selection = await this.api.getEvidenceSelection(projectId, analysisId);
      if (gen === this.generation && this.analysisId() === analysisId) {
        this.selectionInfo.set(
          `L'analisi usa ${plural(selection.evidences.length, 'evidenza', 'evidenze')} di ${plural(selection.items.length, 'messaggio', 'messaggi')}.`,
        );
      }
    } catch (e) {
      if (gen === this.generation && problemOf(e)?.code === 'selection_not_available') {
        this.selectionInfo.set(
          "L'analisi corrente non ha una selezione di evidenze (run precedente al registro o non ancora avviato).",
        );
      }
    }
  }

  protected async verifyCoverage(): Promise<void> {
    const analysisId = this.analysisId();
    const revisionId = this.revisionId();
    if (!analysisId || this.coverageBusy()) return;
    const projectId = this.projectId();
    const gen = this.generation;
    this.coverageBusy.set(true);
    this.coverageError.set('');
    try {
      const request: { revisionId?: string; draftId?: string } = {};
      if (revisionId) request.revisionId = revisionId;
      const draft = await this.projects.getDraft(projectId).catch(() => null);
      if (
        draft &&
        draft.analysisId === analysisId &&
        (!revisionId || draft.analysisRevisionId === revisionId)
      ) {
        request.draftId = draft.id;
      }
      const report = await this.api.createCoverageReport(projectId, analysisId, request);
      if (gen !== this.generation) return;
      this.report.set(report);
      this.reports.set(await this.api.listCoverageReports(projectId, analysisId));
    } catch (e) {
      if (gen === this.generation) this.coverageError.set(this.coverageMessage(e));
    } finally {
      if (gen === this.generation) this.coverageBusy.set(false);
    }
  }

  protected async openReport(reportId: string): Promise<void> {
    const analysisId = this.analysisId();
    if (!analysisId) return;
    const gen = this.generation;
    this.coverageError.set('');
    try {
      const report = await this.api.getCoverageReport(this.projectId(), analysisId, reportId);
      if (gen === this.generation) this.report.set(report);
    } catch {
      if (gen === this.generation) this.coverageError.set('Impossibile aprire il report.');
    }
  }

  private coverageMessage(e: unknown): string {
    const code = problemOf(e)?.code;
    if (code === 'selection_not_available') {
      return "Verifica non disponibile: l'analisi corrente non ha una selezione di evidenze (run precedente al registro). Avvia una nuova analisi.";
    }
    if (code === 'coverage_input_invalid') {
      return "Revisione o bozza non coerenti con l'analisi: ricarica la pagina e riprova.";
    }
    if (e instanceof HttpErrorResponse && e.status === 404) return 'Analisi non trovata.';
    return 'Verifica di copertura non riuscita. Riprova.';
  }

  protected reportLabel(r: CoverageReportSummary): string {
    const c = r.counts;
    return `${this.formatTime(r.createdAt)} · ${c.deterministicErrors} errori, ${c.semanticRisks} rischi, ${c.pmAssessments} da valutare${r.draftId ? ' · con bozza' : ''}`;
  }
}
