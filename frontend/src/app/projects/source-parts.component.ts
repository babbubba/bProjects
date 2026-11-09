import { Component, effect, input, signal, untracked } from '@angular/core';
import { DocumentPassageComponent } from './document-passage.component';
import { AnalysisSource } from './project-api.service';

/** Una parte citata di un documento: le parti di una citazione multi-blocco restano separate, mai fuse (contracts §3). */
export interface SourcePart {
  sourceId: string;
  label: string;
  documentId: string;
  documentVersionId: string;
  preparationId: string;
  blockId: string;
  start: number;
  end: number;
  quote: string;
}

/** Parte documentale di una fonte AnalysisContent 1.1; null per fonti di messaggio/PM e per record 1.0 o incompleti. */
export function partOfSource(source: AnalysisSource, label: string): SourcePart | null {
  if (
    source.kind !== 'DocumentPassage' ||
    !source.documentId ||
    !source.documentVersionId ||
    !source.preparationId ||
    !source.blockId ||
    source.start == null ||
    source.end == null
  ) {
    return null;
  }
  return {
    sourceId: source.id,
    label,
    documentId: source.documentId,
    documentVersionId: source.documentVersionId,
    preparationId: source.preparationId,
    blockId: source.blockId,
    start: source.start,
    end: source.end,
    quote: source.quote ?? '',
  };
}

/**
 * Parti citate con il passaggio nel documento (riuso di T030): ogni parte apre il proprio blocco, «Apri tutte le parti» le apre
 * tutte. La citazione è sempre interpolata come testo; il passaggio è letto dall'API, mai da un URL del documento.
 */
@Component({
  selector: 'app-source-parts',
  imports: [DocumentPassageComponent],
  template: `
    @if (parts().length > 1) {
      <p class="small mb-1" data-testid="parts-group">
        Citazione in {{ parts().length }} parti (blocchi distinti, non fuse).
        <button
          type="button"
          class="btn btn-sm btn-outline-primary ms-1"
          data-testid="open-all-parts"
          (click)="openAll()"
        >
          Apri tutte le parti
        </button>
      </p>
    }
    <ol class="small ps-3 mb-2">
      @for (p of parts(); track p.sourceId) {
        <li data-testid="source-part">
          <span class="text-secondary">{{ p.label }}</span>
          @if (p.quote) {
            <span class="d-block" data-testid="part-quote">«{{ excerpt(p.quote, 240) }}»</span>
          }
          <button
            type="button"
            class="btn btn-sm btn-link p-0"
            data-testid="open-part"
            [attr.aria-expanded]="isOpen(p.sourceId)"
            (click)="toggle(p.sourceId)"
          >
            {{ isOpen(p.sourceId) ? 'Chiudi passaggio' : 'Apri passaggio' }}
          </button>
          @if (isOpen(p.sourceId)) {
            @defer (on immediate) {
              <app-document-passage
                [projectId]="projectId()"
                [documentId]="p.documentId"
                [version]="p.documentVersionId"
                [preparation]="p.preparationId"
                [highlight]="{ blockId: p.blockId, start: p.start, end: p.end }"
                [showBlocks]="false"
              />
            }
          }
        </li>
      }
    </ol>
  `,
})
export class SourcePartsComponent {
  readonly projectId = input.required<string>();
  readonly parts = input.required<SourcePart[]>();
  /** Apre subito tutte le parti (richiesta esplicita dell'utente su una fonte). */
  readonly expanded = input(false);

  /** Citazione su una riga, troncata per punti di codice senza spezzare coppie surrogate. */
  protected excerpt(text: string, max: number): string {
    const chars = Array.from(text.replace(/\s+/g, ' ').trim());
    return chars.length > max ? `${chars.slice(0, max - 1).join('')}…` : chars.join('');
  }
  private readonly open = signal<ReadonlySet<string>>(new Set());

  constructor() {
    effect(() => {
      const parts = this.parts();
      if (this.expanded()) {
        untracked(() => this.open.set(new Set(parts.map((p) => p.sourceId))));
      }
    });
  }

  protected isOpen(id: string): boolean {
    return this.open().has(id);
  }

  protected toggle(id: string): void {
    this.open.update((set) => {
      const next = new Set(set);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  protected openAll(): void {
    this.open.set(new Set(this.parts().map((p) => p.sourceId)));
  }
}
