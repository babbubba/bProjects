import {
  Component,
  ElementRef,
  InjectionToken,
  OnDestroy,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DocumentPassageComponent } from './document-passage.component';
import { Observable } from 'rxjs';
import {
  CandidatePreparation,
  DocumentDetailView,
  DocumentFailure,
  DocumentListItem,
  DocumentVersionView,
  DocumentsApiService,
  ParseRetryView,
  PreparationView,
  SelectionItem,
  SourceSelectionSnapshot,
  UploadEvent,
  documentFailure,
  newIdempotencyKey,
} from './documents-api.service';

const dateFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'medium', timeStyle: 'short' });

const processingLabels: Record<string, string> = {
  NotPrepared: 'Non ancora preparato',
  Queued: 'In coda',
  Parsing: 'Lettura in corso',
  Extracting: 'Estrazione in corso',
  Ready: 'Pronto',
  Incomplete: 'Incompleto',
  Failed: 'Preparazione non riuscita',
};

const availabilityLabels: Record<string, string> = {
  Ready: 'Utilizzabile come fonte',
  Unavailable: 'Non disponibile come fonte',
  Excluded: 'Esclusa dalle fonti',
};

const reasonLabels: Record<string, string> = {
  excluded_by_pm: 'esclusa da te',
  no_preparation: 'preparazione non ancora avviata',
  preparation_queued: 'in attesa di preparazione',
  preparation_parsing: 'lettura in corso',
  preparation_extracting: 'estrazione in corso',
  preparation_incomplete: 'preparazione incompleta',
  preparation_failed: 'preparazione non riuscita',
};

/** Stato in lavorazione (non terminale): il documento non è ancora una fonte utilizzabile. */
const inProgress = new Set(['Queued', 'Parsing', 'Extracting']);

/** Refresh automatico degli stati in lavorazione: leggero e limitato, mai un trigger di lavoro (solo GET). */
export interface DocumentsPollConfig {
  intervalMs: number;
  maxPolls: number;
}

export const DOCUMENTS_POLL = new InjectionToken<DocumentsPollConfig>('DOCUMENTS_POLL', {
  providedIn: 'root',
  factory: () => ({ intervalMs: 5000, maxPolls: 60 }),
});

/** Terminali recuperabili dal PM: il retry è offerto solo se la preparazione lo dichiara in allowedActions. */
const retryCandidates = new Set(['Failed', 'Incomplete']);

interface PendingUpload {
  key: string;
  file: File;
  name: string;
}

/**
 * Archivio dei documenti del progetto (002 US1): upload, versioni, inclusione/esclusione come fonte, scelta LatestReady/Pinned e
 * download degli originali. Non mostra funzioni AI: lo stato «In coda» o l'errore sono quelli reali del server, e i controlli
 * dell'archivio non dipendono dai servizi AI. Distinto dalla bozza `DocumentView` generata dall'analisi.
 */
@Component({
  selector: 'app-project-documents',
  imports: [FormsModule, DocumentPassageComponent],
  templateUrl: './project-documents.component.html',
  styleUrl: './project-documents.component.scss',
})
export class ProjectDocumentsComponent implements OnDestroy {
  private readonly api = inject(DocumentsApiService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly pollConfig = inject(DOCUMENTS_POLL);

  readonly projectId = input.required<string>();

  protected readonly documents = signal<DocumentListItem[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly selection = signal<SourceSelectionSnapshot | null>(null);

  protected readonly expanded = signal<string | null>(null);
  protected readonly detail = signal<DocumentDetailView | null>(null);
  protected readonly detailError = signal('');

  /** Versione di cui si sta leggendo il contenuto preparato (T030); una sola alla volta. */
  protected readonly reading = signal<string | null>(null);

  /** Destinazione dell'upload in corso: 'new' oppure l'id del documento che riceve una nuova versione. */
  protected readonly uploading = signal<string | null>(null);
  protected readonly progress = signal<{ loaded: number; total: number | null } | null>(null);
  protected readonly uploadError = signal<DocumentFailure | null>(null);
  protected readonly uploadErrorTarget = signal<string | null>(null);
  protected readonly notice = signal('');
  protected readonly actionError = signal('');
  protected readonly busy = signal(false);

  /** Preparazione corrente delle versioni terminali dell'elenco aperto (da GET): fonte di parseJobId e allowedActions. */
  protected readonly preparations = signal<Record<string, PreparationView>>({});
  /** Versione per cui un retry è in corso (un solo retry alla volta: il doppio click non invia due richieste). */
  protected readonly retrying = signal<string | null>(null);
  /** Nuovo job collegato creato dal retry, per versione: resta visibile dopo il refresh. */
  protected readonly retryLinks = signal<Record<string, ParseRetryView>>({});
  protected readonly retryError = signal<{ versionId: string; message: string } | null>(null);
  /** Promozione/rielaborazione in corso (una per volta: il doppio click non invia due richieste) e relativi errori. */
  protected readonly promoting = signal<string | null>(null);
  protected readonly promoteError = signal<{ versionId: string; message: string } | null>(null);
  protected readonly reprocessing = signal<string | null>(null);
  protected readonly reprocessError = signal<{ versionId: string; message: string } | null>(null);
  /** true se l'aggiornamento automatico si è fermato con documenti ancora in lavorazione. */
  protected readonly pollStopped = signal(false);

  protected newFile: File | null = null;
  protected newName = '';
  protected readonly fileInputKey = signal(0);

  private pending = new Map<string, PendingUpload>();
  /** Idempotency-Key del retry per job: conservata finché l'esito non è certo, riusata nei replay. */
  private retryKeys = new Map<string, string>();
  /** Idempotency-Key della rielaborazione per versione: conservata finché l'esito non è certo. */
  private reprocessKeys = new Map<string, string>();
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private pollCount = 0;

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => {
        this.expanded.set(null);
        this.reading.set(null);
        this.detail.set(null);
        this.pending.clear();
        this.retryKeys.clear();
        this.reprocessKeys.clear();
        this.promoteError.set(null);
        this.reprocessError.set(null);
        this.preparations.set({});
        this.retryLinks.set({});
        this.retryError.set(null);
        this.pollCount = 0;
        void this.reload(id);
      });
    });
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  // ---------------------------------------------------------------- etichette

  protected formatDate(iso: string): string {
    return dateFormat.format(new Date(iso));
  }

  protected processingLabel(status: string): string {
    return processingLabels[status] ?? status;
  }

  protected isInProgress(status: string): boolean {
    return inProgress.has(status);
  }

  protected badge(status: string): string {
    if (status === 'Ready') return 'text-bg-success';
    if (status === 'Failed' || status === 'Incomplete') return 'text-bg-danger';
    if (inProgress.has(status)) return 'text-bg-warning';
    return 'text-bg-secondary';
  }

  protected availabilityLabel(item: DocumentListItem): string {
    const base = availabilityLabels[item.availability] ?? item.availability;
    const reason = item.reason ? (reasonLabels[item.reason] ?? item.reason) : '';
    return reason ? `${base} (${reason})` : base;
  }

  protected modeLabel(item: DocumentListItem): string {
    if (item.selectionMode === 'Pinned') {
      return 'Versione fissata';
    }
    return 'Ultima versione pronta';
  }

  protected sizeLabel(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  protected progressPercent(): number | null {
    const p = this.progress();
    return p && p.total ? Math.min(100, Math.round((p.loaded / p.total) * 100)) : null;
  }

  protected allSent(): boolean {
    const p = this.progress();
    return !!p && p.total !== null && p.loaded >= p.total;
  }

  protected issueText(v: DocumentVersionView): string {
    return v.issuesSummary ? (v.issuesSummary.message ?? 'La preparazione non è riuscita.') : '';
  }

  protected isPinnedTo(documentId: string, versionId: string): boolean {
    const item = this.selection()?.view.items.find((i) => i.documentId === documentId);
    return item?.mode === 'Pinned' && item.versionId === versionId;
  }

  protected selectionRevision(): number | null {
    return this.selection()?.view.revision ?? null;
  }

  protected blockingCount(): number {
    return this.selection()?.view.blockingSources.length ?? 0;
  }

  // ---------------------------------------------------------------- lettura

  protected async refresh(): Promise<void> {
    this.pollCount = 0;
    this.pollStopped.set(false);
    await this.reload(this.projectId());
    const open = this.expanded();
    if (open) await this.loadDetail(open);
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor) return;
    await this.loadList(this.projectId(), cursor);
  }

  private async reload(projectId: string): Promise<void> {
    this.documents.set([]);
    this.nextCursor.set(null);
    await Promise.all([this.loadList(projectId, null), this.loadSelection(projectId)]);
  }

  private async loadList(projectId: string, cursor: string | null): Promise<void> {
    this.loading.set(true);
    this.loadError.set('');
    try {
      const page = await this.api.list(projectId, cursor);
      if (projectId !== this.projectId()) return;
      const known = new Set(this.documents().map((d) => d.documentId));
      this.documents.update((items) => [
        ...items,
        ...page.items.filter((d) => !known.has(d.documentId)),
      ]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.loadError.set(documentFailure(e).message);
    } finally {
      this.loading.set(false);
      this.schedulePoll();
    }
  }

  // ---------------------------------------------------------------- aggiornamento automatico (solo lettura)

  private hasInProgress(): boolean {
    return (
      this.documents().some(
        (d) => !!d.latestVersion && inProgress.has(d.latestVersion.processingStatus),
      ) ||
      // Una candidata in lavorazione non cambia lo stato della fonte corrente: si osserva dal dettaglio aperto (solo GET).
      !!this.detail()?.versions.some((v) =>
        (v.candidatePreparations ?? []).some((c) => inProgress.has(c.processingStatus)),
      )
    );
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  /** Rilegge gli stati finché qualcosa è Queued/Parsing/Extracting, per un numero limitato di giri. Non invia mai POST. */
  private schedulePoll(): void {
    this.stopPolling();
    if (!this.hasInProgress()) {
      this.pollStopped.set(false);
      return;
    }
    if (this.pollCount >= this.pollConfig.maxPolls) {
      this.pollStopped.set(true);
      return;
    }
    this.pollStopped.set(false);
    this.pollTimer = setTimeout(() => void this.poll(), this.pollConfig.intervalMs);
  }

  private async poll(): Promise<void> {
    this.pollTimer = null;
    this.pollCount++;
    const projectId = this.projectId();
    try {
      const [page, snapshot] = await Promise.all([
        this.api.list(projectId, null),
        this.api.getSelection(projectId),
      ]);
      if (projectId !== this.projectId()) return;
      const fresh = new Map(page.items.map((d) => [d.documentId, d]));
      const known = new Set(this.documents().map((d) => d.documentId));
      this.documents.update((items) => [
        ...items.map((d) => fresh.get(d.documentId) ?? d),
        ...page.items.filter((d) => !known.has(d.documentId)),
      ]);
      this.selection.set(snapshot);
      const open = this.expanded();
      if (open) await this.loadDetail(open);
    } catch {
      // errore transitorio: il prossimo giro (limitato) riprova, nessuna azione con effetti
    }
    this.schedulePoll();
  }

  private async loadSelection(projectId: string): Promise<void> {
    try {
      const snapshot = await this.api.getSelection(projectId);
      if (projectId === this.projectId()) this.selection.set(snapshot);
    } catch (e) {
      this.selection.set(null);
      this.loadError.set(documentFailure(e).message);
    }
  }

  protected toggleReading(version: DocumentVersionView): void {
    this.reading.set(this.reading() === version.id ? null : version.id);
  }

  protected readingPreparation(version: DocumentVersionView): string {
    return version.currentPreparationId ?? 'latest';
  }

  protected async toggleVersions(documentId: string): Promise<void> {
    if (this.expanded() === documentId) {
      this.expanded.set(null);
      this.detail.set(null);
      return;
    }
    this.expanded.set(documentId);
    this.detail.set(null);
    await this.loadDetail(documentId);
  }

  private async loadDetail(documentId: string): Promise<void> {
    this.detailError.set('');
    try {
      const detail = await this.api.get(this.projectId(), documentId);
      if (this.expanded() !== documentId) return;
      this.detail.set(detail);
      this.schedulePoll();
      await this.loadTerminalPreparations(detail);
    } catch (e) {
      this.detailError.set(documentFailure(e).message);
    }
  }

  /** Solo GET: per le versioni terminali recuperabili legge la preparazione corrente (parseJobId, allowedActions). */
  private async loadTerminalPreparations(detail: DocumentDetailView): Promise<void> {
    const projectId = this.projectId();
    const entries = await Promise.all(
      detail.versions
        .filter((v) => retryCandidates.has(v.processingStatus))
        .map(async (v) => {
          try {
            const p = await this.api.getPreparation(
              projectId,
              detail.documentId,
              v.version,
              'latest',
            );
            return [v.id, p] as const;
          } catch {
            return null;
          }
        }),
    );
    if (projectId !== this.projectId() || this.expanded() !== detail.documentId) return;
    const next: Record<string, PreparationView> = {};
    for (const e of entries) if (e) next[e[0]] = e[1];
    this.preparations.set(next);
  }

  // ---------------------------------------------------------------- retry parsing (T117)

  /** Il pulsante compare solo se la preparazione corrente dichiara `retry` tra le azioni ammesse e indica il job. */
  protected retryJobId(version: DocumentVersionView): string | null {
    if (!retryCandidates.has(version.processingStatus)) return null;
    const p = this.preparations()[version.id];
    return p && p.allowedActions.includes('retry') && p.parseJobId ? p.parseJobId : null;
  }

  protected retryLink(version: DocumentVersionView): ParseRetryView | null {
    return this.retryLinks()[version.id] ?? null;
  }

  protected retryErrorFor(version: DocumentVersionView): string {
    const e = this.retryError();
    return e && e.versionId === version.id ? e.message : '';
  }

  protected async retryParse(version: DocumentVersionView): Promise<void> {
    const jobId = this.retryJobId(version);
    if (!jobId || this.retrying() !== null) return;
    let key = this.retryKeys.get(jobId);
    if (!key) {
      key = newIdempotencyKey();
      this.retryKeys.set(jobId, key);
    }
    this.retrying.set(version.id);
    this.retryError.set(null);
    this.notice.set('');
    try {
      const result = await this.api.retryParse(this.projectId(), jobId, key);
      this.retryKeys.delete(jobId);
      this.retryLinks.update((m) => ({ ...m, [version.id]: result }));
      this.notice.set(
        `Nuovo tentativo di lettura avviato per la versione ${version.version} (generazione ${result.generation}), ` +
          `collegato al tentativo precedente. Stato: in coda.`,
      );
      this.pollCount = 0;
      await this.reloadList();
      this.focusSoon('[data-focus="notice"]');
    } catch (e) {
      const failure = documentFailure(e);
      if (!failure.retrySameKey) this.retryKeys.delete(jobId);
      const message =
        failure.kind === 'network' || failure.kind === 'unavailable'
          ? 'Esito del nuovo tentativo incerto: premi di nuovo «Riprova la lettura», la richiesta viene riconciliata senza creare un secondo tentativo.'
          : failure.message;
      this.retryError.set({ versionId: version.id, message });
      this.focusSoon('[data-focus="retry-error"]');
      // Il conflitto indica che lo stato è cambiato: si rilegge (solo GET) perché l'azione sparisca se non più ammessa.
      if (
        failure.kind === 'job_active' ||
        failure.kind === 'job_superseded' ||
        failure.kind === 'conflict'
      ) {
        await this.reloadList();
      }
    } finally {
      this.retrying.set(null);
    }
  }

  // ---------------------------------------------------------------- upload

  protected onNewFile(event: Event): void {
    this.newFile = (event.target as HTMLInputElement).files?.[0] ?? null;
  }

  protected async uploadNew(): Promise<void> {
    const file = this.newFile;
    if (!file) {
      this.fail('new', {
        kind: 'invalid_input',
        status: 0,
        code: null,
        message: 'Scegli un file da caricare.',
        currentRevision: null,
        retrySameKey: false,
      });
      return;
    }
    const name = this.newName.trim();
    const upload = this.pendingFor('new', file, name);
    const ok = await this.runUpload('new', upload, (key) =>
      this.api.uploadDocument(this.projectId(), file, name || null, key),
    );
    if (ok) {
      this.newFile = null;
      this.newName = '';
      this.fileInputKey.update((n) => n + 1);
    }
  }

  protected async uploadVersion(documentId: string, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    const upload = this.pendingFor(documentId, file, '');
    const ok = await this.runUpload(documentId, upload, (key) =>
      this.api.uploadVersion(this.projectId(), documentId, file, key),
    );
    if (ok || !this.uploadError()?.retrySameKey) input.value = '';
  }

  /** Stessa chiave solo se è lo stesso upload (stesso file e nome) dopo un esito incerto; altrimenti chiave nuova. */
  private pendingFor(target: string, file: File, name: string): PendingUpload {
    const previous = this.pending.get(target);
    const same =
      previous &&
      previous.name === name &&
      previous.file.name === file.name &&
      previous.file.size === file.size &&
      previous.file.lastModified === file.lastModified;
    const upload = same ? previous : { key: newIdempotencyKey(), file, name };
    this.pending.set(target, upload);
    return upload;
  }

  private async runUpload(
    target: string,
    upload: PendingUpload,
    start: (key: string) => Observable<UploadEvent>,
  ): Promise<boolean> {
    this.uploading.set(target);
    this.progress.set(null);
    this.uploadError.set(null);
    this.notice.set('');
    try {
      const done = await new Promise<{ version: DocumentVersionView; replayed: boolean }>(
        (resolve, reject) => {
          start(upload.key).subscribe({
            next: (event) => {
              if (event.kind === 'progress') {
                this.progress.set({ loaded: event.loaded, total: event.total });
              } else {
                resolve(event);
              }
            },
            error: reject,
          });
        },
      );
      this.pending.delete(target);
      this.notice.set(
        done.replayed
          ? `«${done.version.originalName}» era già stato caricato: nessuna nuova versione creata.`
          : `«${done.version.originalName}» caricato come versione ${done.version.version}. ` +
              `Stato: ${this.processingLabel(done.version.processingStatus).toLowerCase()}.`,
      );
      this.pollCount = 0;
      await this.reload(this.projectId());
      if (this.expanded()) await this.loadDetail(this.expanded()!);
      this.focusSoon('[data-focus="notice"]');
      return true;
    } catch (e) {
      const failure = documentFailure(e);
      if (!failure.retrySameKey) this.pending.delete(target);
      this.fail(target, failure);
      return false;
    } finally {
      this.uploading.set(null);
      this.progress.set(null);
    }
  }

  private fail(target: string, failure: DocumentFailure): void {
    this.uploadError.set(failure);
    this.uploadErrorTarget.set(target);
    this.focusSoon('[data-focus="upload-error"]');
  }

  // ---------------------------------------------------------------- selezione

  protected async setIncluded(item: DocumentListItem, included: boolean): Promise<void> {
    await this.applySelection(item.documentId, (current) => ({ ...current, included }));
  }

  protected async useLatestReady(documentId: string): Promise<void> {
    await this.applySelection(documentId, (current) => ({
      ...current,
      mode: 'LatestReady',
      versionId: null,
    }));
  }

  /** Il bundle corrente (parsing + estrazione) è stato consolidato dal server: entrambi gli id sono presenti. */
  protected hasCommittedBundle(version: DocumentVersionView): boolean {
    return !!version.currentPreparationId && !!version.currentEvidenceVersionId;
  }

  /** Pronto solo con il commit aggregato: uno stato Ready senza bundle corrente non è una fonte utilizzabile. */
  protected isReady(version: DocumentVersionView): boolean {
    return version.processingStatus === 'Ready' && this.hasCommittedBundle(version);
  }

  protected versionLabel(version: DocumentVersionView): string {
    return version.processingStatus === 'Ready' && !this.hasCommittedBundle(version)
      ? 'Estrazione non consolidata'
      : this.processingLabel(version.processingStatus);
  }

  protected versionBadge(version: DocumentVersionView): string {
    return version.processingStatus === 'Ready' && !this.hasCommittedBundle(version)
      ? 'text-bg-warning'
      : this.badge(version.processingStatus);
  }

  /** Preparazioni candidate (non correnti): distinte dal bundle corrente, con il loro stato reale. */
  protected candidates(version: DocumentVersionView): CandidatePreparation[] {
    return version.candidatePreparations ?? [];
  }

  /** Una candidata Ready e promovibile non è ancora la fonte: lo dichiara il server con canPromote. */
  protected canPromote(c: CandidatePreparation): boolean {
    return c.canPromote && c.processingStatus === 'Ready';
  }

  /** Versione pronta con bundle corrente ma non quella in uso come fonte (altra versione fissata o più recente). */
  protected isNotInUse(version: DocumentVersionView): boolean {
    return this.isReady(version) && version.applicationStatus === 'NotApplicable';
  }

  /** Rielaborazione offerta solo per una fonte pronta e senza altre preparazioni in lavorazione. */
  protected canReprocess(version: DocumentVersionView): boolean {
    return (
      version.processingStatus === 'Ready' &&
      !this.candidates(version).some((c) => inProgress.has(c.processingStatus))
    );
  }

  protected promoteErrorFor(version: DocumentVersionView): string {
    const e = this.promoteError();
    return e && e.versionId === version.id ? e.message : '';
  }

  protected reprocessErrorFor(version: DocumentVersionView): string {
    const e = this.reprocessError();
    return e && e.versionId === version.id ? e.message : '';
  }

  /** Promozione CAS: l'unica via per cambiare il bundle corrente; mai da GET, polling o riapertura. */
  protected async promote(
    documentId: string,
    version: DocumentVersionView,
    c: CandidatePreparation,
  ): Promise<void> {
    if (this.promoting() !== null) return;
    this.promoting.set(version.id);
    this.promoteError.set(null);
    this.notice.set('');
    try {
      const result = await this.api.promoteBundle(
        this.projectId(),
        documentId,
        version.version,
        c.generation,
        version.bundleRevision,
      );
      this.notice.set(
        `La candidata (generazione ${c.generation}) della versione ${version.version} è ora la fonte corrente (revisione ${result.bundleRevision}).`,
      );
      this.pollCount = 0;
      await this.reloadList();
      this.focusSoon('[data-focus="notice"]');
    } catch (e) {
      const failure = documentFailure(e);
      const rev = failure.currentRevision !== null ? ` (revisione ${failure.currentRevision})` : '';
      this.promoteError.set({ versionId: version.id, message: failure.message + rev });
      this.focusSoon('[data-focus="promote-error"]');
      // Il conflitto indica che il bundle è cambiato: si rilegge (solo GET), senza ripetere la promozione.
      if (failure.kind === 'promotion_conflict' || failure.kind === 'not_promotable') {
        await this.reloadList();
      }
    } finally {
      this.promoting.set(null);
    }
  }

  protected async reprocess(documentId: string, version: DocumentVersionView): Promise<void> {
    if (this.reprocessing() !== null) return;
    let key = this.reprocessKeys.get(version.id);
    if (!key) {
      key = newIdempotencyKey();
      this.reprocessKeys.set(version.id, key);
    }
    this.reprocessing.set(version.id);
    this.reprocessError.set(null);
    this.notice.set('');
    try {
      const result = await this.api.reprocess(this.projectId(), documentId, version.version, key);
      this.reprocessKeys.delete(version.id);
      this.notice.set(
        `Rielaborazione avviata per la versione ${version.version}: nasce una candidata (generazione ${result.generation}). ` +
          `La versione corrente resta quella in uso finché non la promuovi.`,
      );
      this.pollCount = 0;
      await this.reloadList();
      this.focusSoon('[data-focus="notice"]');
    } catch (e) {
      const failure = documentFailure(e);
      if (!failure.retrySameKey) this.reprocessKeys.delete(version.id);
      const message =
        failure.kind === 'network' || failure.kind === 'unavailable'
          ? 'Esito della rielaborazione incerto: premi di nuovo «Rielabora», la richiesta viene riconciliata senza creare una seconda candidata.'
          : failure.message;
      this.reprocessError.set({ versionId: version.id, message });
      this.focusSoon('[data-focus="reprocess-error"]');
      if (failure.kind === 'job_active' || failure.kind === 'conflict') await this.reloadList();
    } finally {
      this.reprocessing.set(null);
    }
  }

  /** Il contratto ammette di fissare anche una versione non pronta: resta indisponibile (e blocca Analizza) finché non lo è. */
  protected async pin(documentId: string, version: DocumentVersionView): Promise<void> {
    await this.applySelection(documentId, (current) => ({
      ...current,
      mode: 'Pinned',
      versionId: version.id,
    }));
  }

  private async applySelection(
    documentId: string,
    change: (current: SelectionItem) => SelectionItem,
  ): Promise<void> {
    const snapshot = this.selection();
    if (!snapshot || this.busy()) return;
    const items: SelectionItem[] = this.documents().map((doc) => {
      const known = snapshot.view.items.find((i) => i.documentId === doc.documentId);
      const current: SelectionItem = known
        ? {
            documentId: known.documentId,
            included: known.included,
            mode: known.mode,
            versionId: known.versionId,
          }
        : {
            documentId: doc.documentId,
            included: doc.included,
            mode: doc.selectionMode,
            versionId: null,
          };
      return doc.documentId === documentId ? change(current) : current;
    });
    // Documenti oltre la pagina caricata: restano come sono (il PUT sostituisce l'elenco completo).
    for (const known of snapshot.view.items) {
      if (!items.some((i) => i.documentId === known.documentId)) {
        items.push({
          documentId: known.documentId,
          included: known.included,
          mode: known.mode,
          versionId: known.versionId,
        });
      }
    }

    this.busy.set(true);
    this.actionError.set('');
    this.notice.set('');
    try {
      this.selection.set(await this.api.putSelection(this.projectId(), snapshot.etag, items));
      await this.reloadList();
      this.notice.set('Preferenze di fonte salvate.');
    } catch (e) {
      const failure = documentFailure(e);
      this.actionError.set(failure.message);
      if (failure.kind === 'selection_conflict') {
        await this.reload(this.projectId());
      }
      this.focusSoon('[data-focus="action-error"]');
    } finally {
      this.busy.set(false);
    }
  }

  /** Ricarica la lista dalla prima pagina mantenendo quelle già caricate non necessarie: l'elenco è breve, si riparte da capo. */
  private async reloadList(): Promise<void> {
    this.documents.set([]);
    this.nextCursor.set(null);
    await this.loadList(this.projectId(), null);
    if (this.expanded()) await this.loadDetail(this.expanded()!);
  }

  // ---------------------------------------------------------------- download

  protected async download(documentId: string, version: DocumentVersionView): Promise<void> {
    this.actionError.set('');
    try {
      const file = await this.api.download(
        this.projectId(),
        documentId,
        version.version,
        version.originalName,
      );
      const url = URL.createObjectURL(file.blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = file.fileName;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 0);
    } catch (e) {
      this.actionError.set(documentFailure(e).message);
      this.focusSoon('[data-focus="action-error"]');
    }
  }

  private focusSoon(selector: string): void {
    setTimeout(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), 0);
  }
}
