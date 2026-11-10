import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Registro atomico delle evidenze: viste del contratto HTTP («Registro atomico delle evidenze»), campi camelCase. */

export type EvidenceState = 'none' | 'pending' | 'running' | 'extracted' | 'empty' | 'failed';
export type ExtractionJobStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';
export type ExtractionJobReason = 'Initial' | 'Reanalysis' | 'Backfill';

export type EvidenceKind =
  | 'Goal'
  | 'Context'
  | 'Requirement'
  | 'Action'
  | 'Constraint'
  | 'Exclusion'
  | 'Dependency'
  | 'Assumption'
  | 'Gap'
  | 'Alternative'
  | 'Decision'
  | 'Other';
export type EvidencePolarity = 'Affirmed' | 'Negated' | 'Excluded' | 'Prohibited' | 'Undefined';
export type EvidenceCertainty = 'Declared' | 'Inferred' | 'Hypothesized' | 'ToClarify';
export type SourceRole = 'Primary' | 'Context' | 'Referent';

export interface ExtractionJobView {
  id: string;
  messageId: string;
  messageSequence: number;
  reason: ExtractionJobReason;
  batchId: string | null;
  retryOfJobId: string | null;
  status: ExtractionJobStatus;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  deadlineAt: string;
  attemptCount: number;
  maxAttempts: number;
  nextAttemptAt: string | null;
  resultVersionId: string | null;
  error: { code: string; message: string | null } | null;
}

export interface CurrentVersionView {
  id: string;
  version: number;
  evidenceCount: number;
  noEvidenceReason: string | null;
  createdAt: string;
}

export interface MessageEvidenceStatus {
  messageId: string;
  sequence: number;
  createdAt: string;
  stateRevision: number;
  currentVersion: CurrentVersionView | null;
  candidateCount: number;
  latestJob: ExtractionJobView | null;
  state: EvidenceState;
}

export interface EvidenceStatusView {
  messages: MessageEvidenceStatus[];
  queue: { queued: number; running: number; failed: number };
}

export interface EvidenceQualifier {
  type: string;
  text: string;
}

export interface EvidenceSource {
  messageId: string;
  sequence: number;
  role: SourceRole;
  /** Offset UTF-16 (start incluso, end escluso): `text.slice(start, end)` in JavaScript. */
  start: number;
  end: number;
  quote: string;
}

export interface EvidenceView {
  id: string;
  versionId: string;
  version: number;
  isCurrent: boolean;
  messageId: string;
  messageSequence: number;
  ordinal: number;
  key: string;
  kind: EvidenceKind;
  kindRationale: string | null;
  statement: string;
  subject: string | null;
  action: string | null;
  object: string | null;
  scope: string | null;
  purpose: string | null;
  instrument: string | null;
  channel: string | null;
  recipient: string | null;
  qualifiers: EvidenceQualifier[];
  polarity: EvidencePolarity;
  certainty: EvidenceCertainty;
  sources: EvidenceSource[];
}

export interface EvidenceRelationDetail {
  id: string;
  type: string;
  basis: string;
  rationale: string | null;
  direction: 'outgoing' | 'incoming';
  otherEvidenceId: string;
  otherStatement: string;
  otherMessageSequence: number;
}

/** Versioni di vista del contratto 002: 1.0.0 = evidenza di messaggio (legacy), 1.1.0 = evidenza di documento. */
export const EVIDENCE_VIEW_LEGACY = '1.0.0';
export const EVIDENCE_VIEW_DOCUMENT = '1.1.0';

/** Una parte di citazione documentale; le parti di una citazione multi-blocco condividono `citationGroupId` e restano separate. */
export interface DocumentEvidenceSource {
  sourceId: string;
  kind: string;
  role: string;
  citationGroupId: string;
  part: number;
  documentId: string;
  documentVersionId: string;
  preparationId: string;
  artifactId: string;
  blockId: string;
  /** Offset UTF-16 nel testo del blocco (start incluso, end escluso). */
  start: number;
  end: number;
  quote: string;
}

export interface DocumentEvidenceView {
  id: string;
  versionId: string;
  version: number;
  isCurrent: boolean;
  projectId: string;
  ordinal: number;
  key: string;
  kind: EvidenceKind;
  polarity: EvidencePolarity;
  certainty: EvidenceCertainty;
  statement: string;
  origin: {
    kind: 'Document';
    documentId: string;
    documentVersionId: string;
    preparationId: string;
  };
  content: {
    schemaVersion: string;
    kindRationale: string | null;
    structure: {
      subject: string | null;
      action: string | null;
      object: string | null;
      scope: string | null;
      purpose: string | null;
      instrument: string | null;
      channel: string | null;
      recipient: string | null;
    };
    qualifiers: EvidenceQualifier[];
    sources: DocumentEvidenceSource[];
  };
}

/** Locator risolto dal server: i dati assenti sono null, mai una pagina presunta. */
export interface SourceDocumentView {
  sourceId: string;
  documentId: string;
  documentVersionId: string;
  documentVersion: number;
  originalName: string;
  preparationId: string;
  generation: number;
  artifactId: string;
  blockId: string;
  blockRef: string;
  locator: {
    page: number | null;
    headingPath: string[];
    bbox: { coordinates: number[]; coordinateSystem: string | null } | null;
    sourceLines: { start: number; end: number } | null;
    doclingRef: string | null;
  };
  passageUrl: string;
  downloadUrl: string;
}

/** Elemento di lista con envelope: la provenienza si legge da `evidenceViewVersion`, mai dal testo. */
export type EvidenceEnvelope =
  | { evidenceViewVersion: '1.0.0'; evidence: EvidenceView }
  | { evidenceViewVersion: '1.1.0'; evidence: DocumentEvidenceView };

export interface DocumentRelationDetail {
  id: string;
  type: string;
  basis: string;
  rationale: string | null;
  direction: 'outgoing' | 'incoming';
  otherEvidenceId: string;
  otherStatement: string;
}

export interface EvidenceDetailView {
  evidenceViewVersion: string;
  evidence: EvidenceView | DocumentEvidenceView;
  relations: (EvidenceRelationDetail | DocumentRelationDetail)[];
  sourceDocuments: SourceDocumentView[];
  sourceMessages: { id: string; sequence: number; role: 'user' | 'assistant'; text: string }[];
}

export interface EvidenceFilters {
  messageId?: string;
  kind?: string;
  polarity?: string;
  certainty?: string;
  originKind?: string;
  documentId?: string;
  documentVersionId?: string;
}

export interface VersionSummary {
  id: string;
  version: number;
  createdAt: string;
  evidenceCount: number;
  noEvidenceReason: string | null;
  promptVersion: string;
  schemaVersion: string;
  modelId: string;
  jobId: string;
  reason: ExtractionJobReason;
  isCurrent: boolean;
}

export interface PromotionRecord {
  id: string;
  fromVersionId: string | null;
  toVersionId: string;
  promotedBy: string | null;
  promotedAt: string;
  reason: string | null;
}

export interface MessageVersionsView {
  messageId: string;
  sequence: number;
  stateRevision: number;
  currentVersionId: string | null;
  versions: VersionSummary[];
  promotions: PromotionRecord[];
}

export type DiffStatus = 'Added' | 'Removed' | 'Unchanged' | 'Modified' | 'Ambiguous';

export interface DiffEntry {
  status: DiffStatus;
  from: EvidenceView | null;
  to: EvidenceView | null;
  changes: { field: string; from: string | null; to: string | null }[];
  candidates: string[];
  overlap: number;
}

export interface VersionDiffView {
  fromVersionId: string;
  toVersionId: string;
  messageId: string;
  summary: {
    added: number;
    removed: number;
    unchanged: number;
    modified: number;
    ambiguous: number;
  };
  entries: DiffEntry[];
}

export interface PromotionRequest {
  versionId: string;
  expectedStateRevision: number;
  reason: string | null;
}

export interface PromotionResultView {
  messageId: string;
  stateRevision: number;
  currentVersionId: string;
  /** null se la versione era già quella corrente (200 senza nuova promozione). */
  promotion: (PromotionRecord & { relationImpact: { remapped: number; stale: number } }) | null;
}

export interface EvidenceBatchView {
  id: string;
  kind: 'Reanalysis' | 'Backfill';
  requestedAt: string;
  throughSequence: number;
  jobCount: number;
  skippedCount: number;
  progress: { queued: number; running: number; completed: number; failed: number };
}

export interface EvidenceSelectionView {
  runId: string;
  createdAt: string;
  viewHash: string;
  items: { messageId: string; sequence: number; versionId: string; version: number }[];
  evidences: ({ alias: string; evidenceId?: string } & Partial<EvidenceView>)[];
  relations: {
    fromAlias: string;
    toAlias: string;
    type: string;
    basis: string;
    open: boolean;
    remapped: boolean;
  }[];
  staleRelations: unknown[];
  links: { itemId: string; evidenceIds: string[] }[];
}

export type FindingCategory = 'DeterministicError' | 'SemanticRisk' | 'PmAssessment';

export interface CoverageFinding {
  category: FindingCategory;
  code: string;
  message: string;
  evidenceId: string | null;
  alias: string | null;
  itemId: string | null;
  section: string | null;
  /** Solo per SemanticRisk: low | medium. */
  confidence: string | null;
  details: string | null;
  /** Solo coverage-2: errore deterministico che impedisce di considerare chiusa la verifica. */
  blocking?: boolean;
}

export interface CoverageCounts {
  deterministicErrors: number;
  semanticRisks: number;
  pmAssessments: number;
}

export interface CoverageReportSummary {
  id: string;
  analysisId: string;
  revisionId: string | null;
  draftId: string | null;
  checkerVersion: string;
  createdAt: string;
  counts: CoverageCounts;
}

/** Solo coverage-2: sintesi per ID e per porzioni. Non esiste un booleano di completezza: la copertura per ID non certifica la fedelta al testo. */
export interface CoverageDocumentSummary {
  expectedEvidence: number;
  representedEvidence: number;
  portionsExpected: number;
  portionsProcessed: number;
  portionsNoEvidence: number;
  portionsMissing: number;
  blockingFindings: number;
  /** Solo coverage-3 (letti solo se checkerVersion è «coverage-3»): contatori dei Gap, distinti dal supporto agli elementi. */
  expectedGaps?: number;
  assessedGaps?: number;
  answeredGaps?: number;
  openGaps?: number;
  unassessedGaps?: number;
  /** Solo revisioni con stadio Gap (definizione v6), derivati dal server: assenti nei report v5 e mai dedotti dalla loro assenza. */
  absenceProvenGaps?: number;
  absenceUnprovenGaps?: number;
  answeredWithOpenConflictGaps?: number;
}

export type GapOutcome = 'AnsweredInSnapshot' | 'Open';
export type GapReason = 'Answered' | 'NoAnswer' | 'Partial' | 'Ambiguous';

/** Solo coverage-3: valutazione AI di un Gap nello snapshot congelato. Non è un supporto di requisito né una conferma del PM. */
export interface GapAssessmentView {
  gapEvidenceId: string;
  gapEvidenceVersionId: string;
  sourceSnapshotHash: string;
  outcome: GapOutcome;
  reason: GapReason;
  rationale: string;
  supportingEvidenceIds: string[];
  openItemId: string | null;
  openConflictId: string | null;
  originBatchId: string;
  /** Solo stadio Gap (v6), derivati dal server e opzionali: assenti nei report v5. `absenceProven` ha senso per i soli esiti NoAnswer. */
  searchBasis?: string;
  retrievalComplete?: boolean;
  absenceProven?: boolean;
  answeredWithOpenConflict?: boolean;
}

export interface ReconciliationLevel {
  view: string;
  level: number;
  nodes: number;
  pages: number;
}

export interface ReconciliationCandidate {
  candidateId: string;
  kind: string;
  proposedType: string;
  state: string;
  reason: string | null;
  childCandidateIds: string[];
  terminal: boolean;
}

export interface ReconciliationPatch {
  patchId: string;
  targetKind: string;
  targetId: string;
  state: string;
  reason: string | null;
  blocking: boolean;
}

export interface ReconciliationOpenPoint {
  kind: 'conflict' | 'question';
  id: string;
  description: string;
  alternatives: { itemId: string; text: string; classification: string; evidenceRefs: string[] }[];
}

/** Residui della riconciliazione gerarchica letti dalle proiezioni del run; projected=false: nessuna proiezione per questa analisi. */
export interface ReconciliationCoverage {
  projected: boolean;
  levels: ReconciliationLevel[];
  candidates: ReconciliationCandidate[];
  patches: ReconciliationPatch[];
  frontier: { candidateId: string; reason: string }[];
  openPoints: ReconciliationOpenPoint[];
}

/** Solo revisioni v7 (sorgente `retrieval`): sintesi del registro dei candidati nel report di copertura. Mai dedotta dall'assenza del campo. */
export interface RetrievalCoverageSummary {
  searchBasis: string;
  retrievalComplete: boolean;
  degraded: boolean;
  policyVersion: string;
  gaps: number;
  candidates: number;
  inPackage: number;
  excludedForBudget: number;
  noEvidenceForBlock: number;
  alwaysIncluded: number;
  alwaysIncludedInPackage: number;
}

/** Un candidato recuperato per un Gap, nell'ordine finale. Nessun UUID/hash/offset tecnico: documento, versione, sezione e posizione (intervallo di caratteri). */
export interface RetrievalCandidateView {
  rank: number;
  documentName: string;
  documentVersion: number;
  section: string;
  rangeStart: number;
  rangeEnd: number;
  lexicalRank: number | null;
  denseRank: number | null;
  rrfRank: number;
  origin: 'ranked' | 'always_included';
  inPackage: boolean;
  excludedReason: string | null;
}

export interface RetrievalGapRegistry {
  gapEvidenceId: string;
  query: string;
  candidates: RetrievalCandidateView[];
}

/** Registro del recupero di un run (sola lettura). `outcome` diverso da Package = mancato completamento con le cause tipizzate: mai un risultato valido. */
export interface RetrievalRegistryView {
  analysisId: string;
  searchBasis: string;
  outcome: 'Package' | 'Diagnostic' | 'Failed';
  retrievalComplete: boolean;
  degraded: boolean;
  causes: string[];
  policyVersion: string;
  gaps: RetrievalGapRegistry[];
}

export interface CoverageReportView extends CoverageReportSummary {
  inputHash: string;
  createdBy: string | null;
  findings: CoverageFinding[];
  limits: string[];
  summary?: CoverageDocumentSummary;
  reconciliation?: ReconciliationCoverage;
  /** Solo coverage-3: revisione AI originaria cui appartengono le valutazioni (distinta da `revisionId`, la corrente). */
  aiRevisionId?: string;
  /** Solo coverage-3. Mai letti per altre versioni: un campo assente o ostile non è «zero Gap». */
  gapAssessmentsHash?: string;
  gapAssessments?: GapAssessmentView[];
  /** Solo revisioni v7 (sorgente `retrieval`): sintesi del registro dei candidati; assente per v5/v6. */
  retrieval?: RetrievalCoverageSummary;
}

const enc = encodeURIComponent;

/**
 * Client del registro atomico delle evidenze. I comandi che avviano lavori (rianalisi, retry, batch) inviano
 * `Idempotency-Key`: il chiamante genera una chiave nuova per ogni azione dell'utente e riusa la stessa solo per ripetere
 * un esito HTTP incerto. Bearer e gestione 401 sono dell'interceptor di autenticazione.
 */
@Injectable({ providedIn: 'root' })
export class EvidenceApiService {
  private readonly http = inject(HttpClient);

  private base(projectId: string): string {
    return `/api/projects/${enc(projectId)}`;
  }

  getStatus(projectId: string): Promise<EvidenceStatusView> {
    return firstValueFrom(
      this.http.get<EvidenceStatusView>(`${this.base(projectId)}/evidence/status`),
    );
  }

  /** Lista mista (messaggi e documenti) nel formato 1.1.0: ogni elemento porta il proprio `evidenceViewVersion`. */
  async listEvidence(
    projectId: string,
    filters: EvidenceFilters = {},
  ): Promise<EvidenceEnvelope[]> {
    let params = new HttpParams().set('viewVersion', EVIDENCE_VIEW_DOCUMENT);
    for (const [name, value] of Object.entries(filters)) {
      if (value) params = params.set(name, value);
    }
    const page = await firstValueFrom(
      this.http.get<{ items: EvidenceEnvelope[] }>(`${this.base(projectId)}/evidence`, { params }),
    );
    return page.items;
  }

  getEvidence(projectId: string, evidenceId: string): Promise<EvidenceDetailView> {
    return firstValueFrom(
      this.http.get<EvidenceDetailView>(`${this.base(projectId)}/evidence/${enc(evidenceId)}`, {
        params: { viewVersion: EVIDENCE_VIEW_DOCUMENT },
      }),
    );
  }

  getVersions(projectId: string, messageId: string): Promise<MessageVersionsView> {
    return firstValueFrom(
      this.http.get<MessageVersionsView>(
        `${this.base(projectId)}/messages/${enc(messageId)}/evidence/versions`,
      ),
    );
  }

  compareVersions(projectId: string, fromId: string, toId: string): Promise<VersionDiffView> {
    return firstValueFrom(
      this.http.get<VersionDiffView>(
        `${this.base(projectId)}/evidence/versions/${enc(fromId)}/compare/${enc(toId)}`,
      ),
    );
  }

  getVersion(projectId: string, versionId: string): Promise<unknown> {
    return firstValueFrom(
      this.http.get(`${this.base(projectId)}/evidence/versions/${enc(versionId)}`),
    );
  }

  /** Rianalisi di un messaggio PM dal suo testo originale; 409 extraction_active se c'è già un lavoro aperto. */
  reanalyzeMessage(
    projectId: string,
    messageId: string,
    idempotencyKey: string,
  ): Promise<ExtractionJobView> {
    return firstValueFrom(
      this.http.post<ExtractionJobView>(
        `${this.base(projectId)}/messages/${enc(messageId)}/evidence/reanalyses`,
        null,
        { headers: { 'Idempotency-Key': idempotencyKey } },
      ),
    );
  }

  /** Ritenta un lavoro fallito (nuovo lavoro); 409 job_not_failed / extraction_active. */
  retryJob(projectId: string, jobId: string, idempotencyKey: string): Promise<ExtractionJobView> {
    return firstValueFrom(
      this.http.post<ExtractionJobView>(
        `${this.base(projectId)}/evidence/jobs/${enc(jobId)}/retry`,
        null,
        { headers: { 'Idempotency-Key': idempotencyKey } },
      ),
    );
  }

  getJob(projectId: string, jobId: string): Promise<ExtractionJobView> {
    return firstValueFrom(
      this.http.get<ExtractionJobView>(`${this.base(projectId)}/evidence/jobs/${enc(jobId)}`),
    );
  }

  reanalyzeProject(projectId: string, idempotencyKey: string): Promise<EvidenceBatchView> {
    return firstValueFrom(
      this.http.post<EvidenceBatchView>(`${this.base(projectId)}/evidence/reanalyses`, null, {
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  startBackfill(projectId: string, idempotencyKey: string): Promise<EvidenceBatchView> {
    return firstValueFrom(
      this.http.post<EvidenceBatchView>(`${this.base(projectId)}/evidence/backfills`, null, {
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  getBatch(projectId: string, batchId: string): Promise<EvidenceBatchView> {
    return firstValueFrom(
      this.http.get<EvidenceBatchView>(`${this.base(projectId)}/evidence/batches/${enc(batchId)}`),
    );
  }

  /** Promozione esplicita con concorrenza ottimistica; 409 promotion_conflict se lo stato è cambiato. */
  promote(
    projectId: string,
    messageId: string,
    request: PromotionRequest,
  ): Promise<PromotionResultView> {
    return firstValueFrom(
      this.http.post<PromotionResultView>(
        `${this.base(projectId)}/messages/${enc(messageId)}/evidence/promotions`,
        request,
      ),
    );
  }

  /** Selezione di evidenze congelata dal run di analisi; 404 selection_not_available se assente. */
  getEvidenceSelection(projectId: string, analysisId: string): Promise<EvidenceSelectionView> {
    return firstValueFrom(
      this.http.get<EvidenceSelectionView>(
        `${this.base(projectId)}/analyses/${enc(analysisId)}/evidence-selection`,
      ),
    );
  }

  /** Calcola (o riusa, 200) il report di copertura per gli stessi input; mai modifica analisi o documento. */
  createCoverageReport(
    projectId: string,
    analysisId: string,
    request: { revisionId?: string; draftId?: string },
  ): Promise<CoverageReportView> {
    return firstValueFrom(
      this.http.post<CoverageReportView>(
        `${this.base(projectId)}/analyses/${enc(analysisId)}/coverage-reports`,
        request,
      ),
    );
  }

  async listCoverageReports(
    projectId: string,
    analysisId: string,
  ): Promise<CoverageReportSummary[]> {
    const page = await firstValueFrom(
      this.http.get<{ items: CoverageReportSummary[] }>(
        `${this.base(projectId)}/analyses/${enc(analysisId)}/coverage-reports`,
      ),
    );
    return page.items;
  }

  getCoverageReport(
    projectId: string,
    analysisId: string,
    reportId: string,
  ): Promise<CoverageReportView> {
    return firstValueFrom(
      this.http.get<CoverageReportView>(
        `${this.base(projectId)}/analyses/${enc(analysisId)}/coverage-reports/${enc(reportId)}`,
      ),
    );
  }

  /**
   * Registro del recupero del run (sola lettura, stessi permessi di progetto della copertura). 404 retrieval_registry_not_available: il run non usa la sorgente `retrieval`
   * (v5/v6, o ledger entro il budget). Un esito diverso da `Package` è un mancato completamento, mai un risultato valido.
   */
  getRetrievalRegistry(projectId: string, analysisId: string): Promise<RetrievalRegistryView> {
    return firstValueFrom(
      this.http.get<RetrievalRegistryView>(
        `${this.base(projectId)}/analyses/${enc(analysisId)}/retrieval-registry`,
      ),
    );
  }
}
