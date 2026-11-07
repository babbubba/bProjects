import {
  HttpClient,
  HttpErrorResponse,
  HttpEvent,
  HttpEventType,
  HttpParams,
  HttpResponse,
} from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, firstValueFrom, map } from 'rxjs';

/**
 * Archivio dei documenti caricati dal PM (contratto 002 §Documenti e §Selezione persistente). Non riguarda la bozza
 * `DocumentView` prodotta dall'analisi: qui ci sono solo i file originali, le loro versioni e le preferenze di fonte.
 * Il client non invia mai prompt, regime o timeout.
 */

export type PreparationState = 'NotPrepared' | 'Queued' | 'Running' | 'Ready' | 'Failed' | string;
export type SourceModeName = 'LatestReady' | 'Pinned';
export type SourceAvailabilityName = 'Ready' | 'Unavailable' | 'Excluded';

export interface IssuesSummary {
  code: string;
  message: string | null;
}

export interface CandidatePreparation {
  preparationId: string;
  generation: number;
  processingStatus: PreparationState;
  evidenceVersionId: string | null;
  createdAt: string;
  canPromote: boolean;
}

export interface DocumentVersionView {
  id: string;
  documentId: string;
  projectId: string;
  version: number;
  originalName: string;
  mediaType: string;
  byteLength: number;
  sha256: string;
  uploadedBy: string;
  uploadedAt: string;
  processingStatus: PreparationState;
  applicationStatus: 'Applicable' | 'NotApplicable' | 'Excluded' | string;
  currentPreparationId: string | null;
  currentEvidenceVersionId: string | null;
  bundleRevision: number;
  /** Preparazioni complete o fallite non ancora promosse: distinte dal bundle corrente, mai fonte di analisi. */
  candidatePreparations?: CandidatePreparation[];
  latestJobId: string | null;
  included: boolean;
  selectionMode: SourceModeName;
  isApplicable: boolean;
  previousReadyVersionId: string | null;
  issuesSummary: IssuesSummary | null;
  allowedActions: string[];
}

export interface DocumentListItem {
  documentId: string;
  displayName: string;
  createdAt: string;
  versionCount: number;
  latestVersion: {
    id: string;
    version: number;
    processingStatus: PreparationState;
    uploadedAt: string;
  } | null;
  applicableVersionId: string | null;
  included: boolean;
  selectionMode: SourceModeName;
  availability: SourceAvailabilityName;
  reason: string | null;
}

export interface DocumentListPage {
  items: DocumentListItem[];
  nextCursor: string | null;
}

export interface DocumentDetailView {
  documentId: string;
  projectId: string;
  displayName: string;
  createdBy: string;
  createdAt: string;
  versions: DocumentVersionView[];
  sourcePreference: { included: boolean; mode: SourceModeName; versionId: string | null };
  availability: SourceAvailabilityName;
  applicableVersionId: string | null;
}

export interface SelectionItem {
  documentId: string;
  included: boolean;
  mode: SourceModeName;
  versionId: string | null;
}

export interface SourceSelectionItemView extends SelectionItem {
  resolvedVersionId: string | null;
  preparationId: string | null;
  availability: SourceAvailabilityName;
}

export interface BlockingSourceView {
  documentId: string;
  displayName: string;
  versionId: string | null;
  version: number | null;
  status: string;
  allowedActions: string[];
}

export interface SourceSelectionView {
  revision: number;
  items: SourceSelectionItemView[];
  blockingSources: BlockingSourceView[];
}

export interface SourceSelectionSnapshot {
  view: SourceSelectionView;
  /** ETag quotato ricevuto dal server, da rimandare tale e quale in If-Match. */
  etag: string;
}

/** Evento di un upload: avanzamento reale dei byte inviati, poi l'esito del server. */
export type UploadEvent =
  | { kind: 'progress'; loaded: number; total: number | null }
  | { kind: 'done'; version: DocumentVersionView; replayed: boolean };

export interface DownloadedFile {
  blob: Blob;
  fileName: string;
}

export type DocumentFailureKind =
  | 'unauthorized'
  | 'not_found'
  | 'idempotency_conflict'
  | 'upload_in_progress'
  | 'selection_conflict'
  | 'job_active'
  | 'job_superseded'
  | 'promotion_conflict'
  | 'not_promotable'
  | 'conflict'
  | 'too_large'
  | 'unsupported_format'
  | 'invalid_input'
  | 'quota'
  | 'timeout'
  | 'unavailable'
  | 'network'
  | 'unknown';

export interface DocumentFailure {
  kind: DocumentFailureKind;
  status: number;
  code: string | null;
  /** Testo italiano, pronto per il PM. */
  message: string;
  /** Per source_selection_conflict: revisione corrente del server. */
  currentRevision: number | null;
  /**
   * true se la stessa richiesta con la stessa Idempotency-Key è sicura da ripetere (esito incerto o transitorio):
   * il server riconcilia la stessa ricevuta, mai una seconda versione.
   */
  retrySameKey: boolean;
}

// ---- Contenuto preparato (T030): testo strutturato, mai HTML ------------------------------------------------------------------------

export interface PreparationIssueView {
  code: string;
  severity: string;
  page: number | null;
  blockId: string | null;
  reason: string;
  affectsCompleteness: boolean;
}

/** `processingStatus` è lo stato reale; `isReady` è vero solo per Ready (Parsing/Extracting non lo sono mai). */
export interface PreparationView {
  id: string;
  documentVersionId: string;
  documentId: string;
  version: number;
  generation: number;
  processingStatus: PreparationState;
  isReady: boolean;
  contentState: 'NotParsed' | 'Parsed' | 'Ready';
  blockCount: number | null;
  retryOfId: string | null;
  requestedAt: string;
  issuesSummary: IssuesSummary | null;
  issues: PreparationIssueView[];
  allowedActions: string[];
  /** Job di parsing della preparazione: è il bersaglio dell'azione `retry`. */
  parseJobId?: string | null;
}

/** Esito 202 del retry manuale: job e preparazione nuovi, collegati ai precedenti. */
export interface ParseRetryView {
  jobId: string;
  preparationId: string;
  generation: number;
  retryOfJobId: string;
  retryOfPreparationId: string;
  status: string;
  requestedAt: string;
  deadlineAt: string;
}

/** Esito della promozione (o del replay, 200): puntatori e revisione del bundle corrente sempre insieme. */
export interface PromotedBundleView {
  documentId: string;
  documentVersionId: string;
  bundleRevision: number;
  currentPreparationId: string | null;
  currentEvidenceVersionId: string | null;
  generation: number;
  promotion: unknown;
}

export interface BlockView {
  id: string;
  ref: string;
  ordinal: number;
  parentId: string | null;
  type: string;
  text: string;
  headingPath: string[];
  page: number | null;
  bbox: { values: number[]; coordinateSystem: string | null } | null;
  lines: { start: number; end: number } | null;
  tableCells: unknown;
}

export interface BlockPage {
  preparationId: string;
  processingStatus: PreparationState;
  isReady: boolean;
  items: BlockView[];
  nextCursor: string | null;
}

export interface PassageView {
  block: BlockView;
  preparationId: string;
  processingStatus: PreparationState;
  isReady: boolean;
  quote: { start: number; end: number; before: string; text: string; after: string } | null;
  original: { originalName: string; downloadUrl: string };
}

const enc = encodeURIComponent;

/** UUID v4 per Idempotency-Key. Una chiave per ogni azione dell'utente; si riusa solo per ripetere lo stesso upload. */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

function failure(
  kind: DocumentFailureKind,
  status: number,
  code: string | null,
  message: string,
  extra: { currentRevision?: number | null; retrySameKey?: boolean } = {},
): DocumentFailure {
  return {
    kind,
    status,
    code,
    message,
    currentRevision: extra.currentRevision ?? null,
    retrySameKey: extra.retrySameKey ?? false,
  };
}

/** Trasforma un errore HTTP in un esito leggibile; del corpo del server si usa solo il campo `code`. */
export function documentFailure(e: unknown): DocumentFailure {
  if (!(e instanceof HttpErrorResponse)) {
    return failure('unknown', 0, null, 'Operazione non riuscita. Riprova.');
  }
  const body = (e.error ?? null) as { code?: unknown; currentRevision?: unknown } | null;
  const code = typeof body?.code === 'string' ? body.code : null;
  const status = e.status;
  switch (status) {
    case 0:
      return failure(
        'network',
        0,
        code,
        'Server non raggiungibile. Controlla la connessione e riprova: ripetere lo stesso caricamento non crea duplicati.',
        { retrySameKey: true },
      );
    case 401:
      return failure('unauthorized', status, code, 'Sessione scaduta.');
    case 404:
      return failure('not_found', status, code, 'Il progetto o il documento non esiste più.');
    case 408:
      return failure(
        'timeout',
        status,
        code,
        'La ricezione del file ha superato il tempo consentito e nulla è stato acquisito. Puoi riprovare.',
        { retrySameKey: true },
      );
    case 409:
      switch (code) {
        case 'idempotency_conflict':
          return failure(
            'idempotency_conflict',
            status,
            code,
            'Questo caricamento è già stato usato con un contenuto diverso. Avvia un nuovo caricamento.',
          );
        case 'upload_in_progress':
          return failure(
            'upload_in_progress',
            status,
            code,
            'Il caricamento è ancora in corso sul server. Attendi qualche istante e riprova.',
            { retrySameKey: true },
          );
        case 'source_selection_conflict':
          return failure(
            'selection_conflict',
            status,
            code,
            'Le preferenze di fonte sono cambiate nel frattempo: sono state ricaricate, riapplica la modifica.',
            {
              currentRevision:
                typeof body?.currentRevision === 'number' ? body.currentRevision : null,
            },
          );
        case 'job_active':
        case 'preparation_active':
          return failure(
            'job_active',
            status,
            code,
            'Esiste già una preparazione in corso per questa versione: attendi che termini, non serve ritentare.',
          );
        case 'bundle_revision_conflict':
        case 'promotion_conflict':
          return failure(
            'promotion_conflict',
            status,
            code,
            'Il bundle corrente è cambiato nel frattempo: la vista è stata riletta, verifica lo stato e ripeti la promozione solo se serve.',
            {
              currentRevision:
                typeof body?.currentRevision === 'number' ? body.currentRevision : null,
            },
          );
        case 'job_superseded':
          return failure(
            'job_superseded',
            status,
            code,
            'Una preparazione più recente ha già sostituito questo tentativo: aggiorna la vista.',
          );
        default:
          return failure(
            'conflict',
            status,
            code,
            'Conflitto con lo stato corrente. Aggiorna e riprova.',
          );
      }
    case 413:
      return failure('too_large', status, code, 'Il file supera la dimensione massima consentita.');
    case 415:
      return failure(
        'unsupported_format',
        status,
        code,
        'Formato non supportato. Sono accettati PDF testuali, DOCX, Markdown e testo semplice.',
      );
    case 422:
      if (code === 'preparation_not_promotable') {
        return failure(
          'not_promotable',
          status,
          code,
          'La preparazione candidata non è completa: non può diventare la fonte corrente.',
        );
      }
      return failure(
        'invalid_input',
        status,
        code,
        'Il file non è valido (vuoto, protetto o con dati non ammessi).',
      );
    case 507:
      return failure(
        'quota',
        status,
        code,
        'Spazio o quota dei documenti del progetto esauriti: il file non è stato caricato.',
      );
    case 503:
      return failure(
        'unavailable',
        status,
        code,
        'Archiviazione temporaneamente non disponibile. Riprova tra poco.',
        { retrySameKey: true },
      );
    default:
      return failure('unknown', status, code, 'Operazione non riuscita. Riprova.');
  }
}

/** Nome file da Content-Disposition (filename* UTF-8 con precedenza), con ripiego sul nome indicato. */
export function fileNameFromDisposition(header: string | null, fallback: string): string {
  if (header) {
    const star = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(header);
    if (star) {
      try {
        const name = decodeURIComponent(star[2].trim());
        if (name) return name;
      } catch {
        // codifica non valida: si passa a filename=
      }
    }
    const plain = /filename\s*=\s*("([^"]*)"|[^;]+)/i.exec(header);
    const value = plain ? (plain[2] ?? plain[1]).trim() : '';
    if (value) return value;
  }
  return fallback;
}

@Injectable({ providedIn: 'root' })
export class DocumentsApiService {
  private readonly http = inject(HttpClient);

  private base(projectId: string): string {
    return `/api/projects/${enc(projectId)}`;
  }

  list(projectId: string, cursor: string | null = null, limit?: number): Promise<DocumentListPage> {
    let params = new HttpParams();
    if (cursor) params = params.set('cursor', cursor);
    if (limit) params = params.set('limit', limit);
    return firstValueFrom(
      this.http.get<DocumentListPage>(`${this.base(projectId)}/documents`, { params }),
    );
  }

  get(projectId: string, documentId: string): Promise<DocumentDetailView> {
    return firstValueFrom(
      this.http.get<DocumentDetailView>(`${this.base(projectId)}/documents/${enc(documentId)}`),
    );
  }

  getVersion(
    projectId: string,
    documentId: string,
    version: number | string,
  ): Promise<DocumentVersionView> {
    return firstValueFrom(
      this.http.get<DocumentVersionView>(
        `${this.base(projectId)}/documents/${enc(documentId)}/versions/${enc(String(version))}`,
      ),
    );
  }

  /**
   * Carica un nuovo documento. `idempotencyKey` è scelta dal chiamante (newIdempotencyKey) e va conservata per ripetere
   * lo stesso upload dopo un esito incerto: il server risponde 200 con la stessa versione, senza lavoro nuovo.
   */
  uploadDocument(
    projectId: string,
    file: File,
    displayName: string | null,
    idempotencyKey: string,
  ): Observable<UploadEvent> {
    const form = new FormData();
    form.append('file', file, file.name);
    const name = displayName?.trim();
    if (name) form.append('displayName', name);
    return this.upload(`${this.base(projectId)}/documents`, form, idempotencyKey);
  }

  /** Nuova versione dello stesso documento; i byte precedenti non vengono mai sostituiti. */
  uploadVersion(
    projectId: string,
    documentId: string,
    file: File,
    idempotencyKey: string,
  ): Observable<UploadEvent> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.upload(
      `${this.base(projectId)}/documents/${enc(documentId)}/versions`,
      form,
      idempotencyKey,
    );
  }

  private upload(url: string, form: FormData, idempotencyKey: string): Observable<UploadEvent> {
    return this.http
      .post<DocumentVersionView>(url, form, {
        headers: { 'Idempotency-Key': idempotencyKey },
        observe: 'events',
        reportProgress: true,
      })
      .pipe(
        filter(
          (e: HttpEvent<DocumentVersionView>) =>
            e.type === HttpEventType.UploadProgress || e.type === HttpEventType.Response,
        ),
        map((e): UploadEvent => {
          if (e.type === HttpEventType.UploadProgress) {
            return { kind: 'progress', loaded: e.loaded, total: e.total ?? null };
          }
          const response = e as HttpResponse<DocumentVersionView>;
          return { kind: 'done', version: response.body!, replayed: response.status === 200 };
        }),
      );
  }

  /** `preparation`: generazione, id della preparazione o `latest`. Sola lettura: non avvia lavori. */
  getPreparation(
    projectId: string,
    documentId: string,
    version: number | string,
    preparation: number | string,
  ): Promise<PreparationView> {
    return firstValueFrom(
      this.http.get<PreparationView>(
        this.preparationUrl(projectId, documentId, version, preparation),
      ),
    );
  }

  listBlocks(
    projectId: string,
    documentId: string,
    version: number | string,
    preparation: number | string,
    cursor: string | null = null,
    limit?: number,
  ): Promise<BlockPage> {
    let params = new HttpParams();
    if (cursor) params = params.set('cursor', cursor);
    if (limit) params = params.set('limit', limit);
    return firstValueFrom(
      this.http.get<BlockPage>(
        `${this.preparationUrl(projectId, documentId, version, preparation)}/blocks`,
        { params },
      ),
    );
  }

  getPassage(
    projectId: string,
    documentId: string,
    version: number | string,
    preparation: number | string,
    blockId: string,
    range: { start: number; end: number } | null = null,
  ): Promise<PassageView> {
    let params = new HttpParams();
    if (range) params = params.set('start', range.start).set('end', range.end);
    return firstValueFrom(
      this.http.get<PassageView>(
        `${this.preparationUrl(projectId, documentId, version, preparation)}/passages/${enc(blockId)}`,
        { params },
      ),
    );
  }

  /**
   * Retry manuale del parsing (T116/T117): unica porta per ripartire, mai da GET o polling. `idempotencyKey` va conservata dal
   * chiamante e riusata in caso di esito incerto: il server restituisce lo stesso nuovo job.
   */
  retryParse(projectId: string, jobId: string, idempotencyKey: string): Promise<ParseRetryView> {
    return firstValueFrom(
      this.http.post<ParseRetryView>(
        `${this.base(projectId)}/document-jobs/${enc(jobId)}/retry`,
        null,
        { headers: { 'Idempotency-Key': idempotencyKey } },
      ),
    );
  }

  /**
   * Rielaborazione candidata della stessa versione (202): non tocca il bundle corrente. `idempotencyKey` va conservata e riusata
   * dopo un esito incerto; 409 se esiste già una preparazione attiva.
   */
  reprocess(
    projectId: string,
    documentId: string,
    version: number | string,
    idempotencyKey: string,
  ): Promise<ParseRetryView> {
    return firstValueFrom(
      this.http.post<ParseRetryView>(
        `${this.base(projectId)}/documents/${enc(documentId)}/versions/${enc(String(version))}/preparations`,
        null,
        { headers: { 'Idempotency-Key': idempotencyKey } },
      ),
    );
  }

  /** Promozione CAS: `expectedRevision` è la `bundleRevision` letta dal client (distinta dall'ETag della selezione). */
  promoteBundle(
    projectId: string,
    documentId: string,
    version: number | string,
    generation: number | string,
    expectedRevision: number,
    reason?: string,
  ): Promise<PromotedBundleView> {
    const body: { expectedRevision: number; reason?: string } = { expectedRevision };
    if (reason) body.reason = reason;
    return firstValueFrom(
      this.http.post<PromotedBundleView>(
        `${this.preparationUrl(projectId, documentId, version, generation)}/promote`,
        body,
      ),
    );
  }

  private preparationUrl(
    projectId: string,
    documentId: string,
    version: number | string,
    preparation: number | string,
  ): string {
    return `${this.base(projectId)}/documents/${enc(documentId)}/versions/${enc(String(version))}/preparations/${enc(String(preparation))}`;
  }

  /** Scarica i byte originali verificati; il nome arriva da Content-Disposition. */
  async download(
    projectId: string,
    documentId: string,
    version: number,
    fallbackName: string,
  ): Promise<DownloadedFile> {
    const response = await firstValueFrom(
      this.http.get(
        `${this.base(projectId)}/documents/${enc(documentId)}/versions/${enc(String(version))}/download`,
        { responseType: 'blob', observe: 'response' },
      ),
    );
    return {
      blob: response.body as Blob,
      fileName: fileNameFromDisposition(response.headers.get('Content-Disposition'), fallbackName),
    };
  }

  async getSelection(projectId: string): Promise<SourceSelectionSnapshot> {
    const response = await firstValueFrom(
      this.http.get<SourceSelectionView>(`${this.base(projectId)}/source-selection`, {
        observe: 'response',
      }),
    );
    const view = response.body!;
    return { view, etag: response.headers.get('ETag') ?? `"${view.revision}"` };
  }

  /** Sostituisce l'intero elenco di preferenze; If-Match obbligatorio, 409 source_selection_conflict se obsoleta. */
  async putSelection(
    projectId: string,
    etag: string,
    items: SelectionItem[],
  ): Promise<SourceSelectionSnapshot> {
    const response = await firstValueFrom(
      this.http.put<SourceSelectionView>(
        `${this.base(projectId)}/source-selection`,
        { items },
        { headers: { 'If-Match': etag }, observe: 'response' },
      ),
    );
    const view = response.body!;
    return { view, etag: response.headers.get('ETag') ?? `"${view.revision}"` };
  }
}
