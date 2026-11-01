import { Component, ElementRef, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import {
  DocumentDetailView,
  DocumentFailure,
  DocumentListItem,
  DocumentVersionView,
  DocumentsApiService,
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
  imports: [FormsModule],
  templateUrl: './project-documents.component.html',
  styleUrl: './project-documents.component.scss',
})
export class ProjectDocumentsComponent {
  private readonly api = inject(DocumentsApiService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly projectId = input.required<string>();

  protected readonly documents = signal<DocumentListItem[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly selection = signal<SourceSelectionSnapshot | null>(null);

  protected readonly expanded = signal<string | null>(null);
  protected readonly detail = signal<DocumentDetailView | null>(null);
  protected readonly detailError = signal('');

  /** Destinazione dell'upload in corso: 'new' oppure l'id del documento che riceve una nuova versione. */
  protected readonly uploading = signal<string | null>(null);
  protected readonly progress = signal<{ loaded: number; total: number | null } | null>(null);
  protected readonly uploadError = signal<DocumentFailure | null>(null);
  protected readonly uploadErrorTarget = signal<string | null>(null);
  protected readonly notice = signal('');
  protected readonly actionError = signal('');
  protected readonly busy = signal(false);

  protected newFile: File | null = null;
  protected newName = '';
  protected readonly fileInputKey = signal(0);

  private pending = new Map<string, PendingUpload>();

  constructor() {
    effect(() => {
      const id = this.projectId();
      untracked(() => {
        this.expanded.set(null);
        this.detail.set(null);
        this.pending.clear();
        void this.reload(id);
      });
    });
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
    }
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
      if (this.expanded() === documentId) this.detail.set(detail);
    } catch (e) {
      this.detailError.set(documentFailure(e).message);
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

  protected canPin(version: DocumentVersionView): boolean {
    return version.processingStatus === 'Ready';
  }

  protected async pin(documentId: string, version: DocumentVersionView): Promise<void> {
    if (!this.canPin(version)) return;
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
