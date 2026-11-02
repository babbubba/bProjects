import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import {
  BlockView,
  DocumentsApiService,
  PassageView,
  PreparationView,
  documentFailure,
} from './documents-api.service';

const statusLabels: Record<string, string> = {
  Queued: 'In coda',
  Parsing: 'Lettura in corso',
  Extracting: 'Estrazione in corso',
  Ready: 'Pronto',
  Incomplete: 'Incompleto',
  Failed: 'Preparazione non riuscita',
};

export interface PassageHighlight {
  blockId: string;
  start: number;
  end: number;
}

/**
 * Contenuto preparato di una versione (002 T030): blocchi in ordine di lettura con pagina, sezione e righe solo se il parser le ha
 * fornite, e un passaggio con la citazione evidenziata. Il testo è sempre interpolato (mai HTML): l'escaping è del template.
 * «Pronto» compare solo per lo stato Ready; un contenuto già letto ma in Parsing/Extracting è dichiarato non ancora pronto.
 */
@Component({
  selector: 'app-document-passage',
  templateUrl: './document-passage.component.html',
  styleUrl: './document-passage.component.scss',
})
export class DocumentPassageComponent {
  private readonly api = inject(DocumentsApiService);

  readonly projectId = input.required<string>();
  readonly documentId = input.required<string>();
  readonly version = input.required<number>();
  /** Generazione, id della preparazione o `latest`. */
  readonly preparation = input<string>('latest');
  readonly highlight = input<PassageHighlight | null>(null);
  /** false = solo il passaggio citato (link da un'evidenza), senza elenco dei blocchi del documento. */
  readonly showBlocks = input(true);
  readonly downloadRequested = output<void>();

  protected readonly prep = signal<PreparationView | null>(null);
  protected readonly blocks = signal<BlockView[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly unparsed = signal(false);
  protected readonly error = signal('');
  protected readonly passage = signal<PassageView | null>(null);
  protected readonly passageError = signal('');

  constructor() {
    effect(() => {
      this.projectId();
      this.documentId();
      this.version();
      this.preparation();
      const highlight = this.highlight();
      untracked(() => void this.load(highlight));
    });
  }

  protected statusLabel(status: string): string {
    return statusLabels[status] ?? status;
  }

  protected badge(p: PreparationView): string {
    if (p.isReady) return 'text-bg-success';
    if (p.processingStatus === 'Failed' || p.processingStatus === 'Incomplete') {
      return 'text-bg-danger';
    }
    return 'text-bg-warning';
  }

  protected location(b: BlockView): string {
    const parts: string[] = [];
    if (b.page !== null) parts.push(`Pagina ${b.page}`);
    if (b.headingPath.length > 0) parts.push(b.headingPath.join(' › '));
    if (b.lines) {
      parts.push(
        b.lines.start === b.lines.end
          ? `riga ${b.lines.start}`
          : `righe ${b.lines.start}-${b.lines.end}`,
      );
    }
    if (b.bbox) parts.push(`area ${b.bbox.values.map((n) => Math.round(n * 10) / 10).join(', ')}`);
    return parts.length > 0 ? parts.join(' · ') : 'Posizione non disponibile';
  }

  private async load(highlight: PassageHighlight | null): Promise<void> {
    const key = this.key();
    this.prep.set(null);
    this.blocks.set([]);
    this.nextCursor.set(null);
    this.unparsed.set(false);
    this.error.set('');
    this.passage.set(null);
    this.passageError.set('');
    this.loading.set(true);
    try {
      const prep = await this.api.getPreparation(
        this.projectId(),
        this.documentId(),
        this.version(),
        this.preparation(),
      );
      if (key !== this.key()) return;
      this.prep.set(prep);
      if (this.showBlocks()) await this.loadBlocks(null, key);
      if (highlight && key === this.key()) {
        await this.openPassage(highlight.blockId, { start: highlight.start, end: highlight.end });
      }
    } catch (e) {
      if (key === this.key()) this.error.set(documentFailure(e).message);
    } finally {
      if (key === this.key()) this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor) return;
    this.loading.set(true);
    try {
      await this.loadBlocks(cursor, this.key());
    } finally {
      this.loading.set(false);
    }
  }

  private async loadBlocks(cursor: string | null, key: string): Promise<void> {
    try {
      const page = await this.api.listBlocks(
        this.projectId(),
        this.documentId(),
        this.version(),
        this.preparation(),
        cursor,
      );
      if (key !== this.key()) return;
      const known = new Set(this.blocks().map((b) => b.id));
      this.blocks.update((items) => [...items, ...page.items.filter((b) => !known.has(b.id))]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      if (key !== this.key()) return;
      const failure = documentFailure(e);
      if (failure.status === 409) this.unparsed.set(true);
      else this.error.set(failure.message);
    }
  }

  protected async openPassage(
    blockId: string,
    range: { start: number; end: number } | null = null,
  ): Promise<void> {
    this.passageError.set('');
    this.passage.set(null);
    try {
      this.passage.set(
        await this.api.getPassage(
          this.projectId(),
          this.documentId(),
          this.version(),
          this.preparation(),
          blockId,
          range,
        ),
      );
    } catch (e) {
      const failure = documentFailure(e);
      this.passageError.set(
        failure.kind === 'invalid_input'
          ? 'L’intervallo richiesto non corrisponde al testo del blocco.'
          : failure.message,
      );
    }
  }

  protected closePassage(): void {
    this.passage.set(null);
    this.passageError.set('');
  }

  private key(): string {
    return `${this.projectId()}|${this.documentId()}|${this.version()}|${this.preparation()}`;
  }
}
