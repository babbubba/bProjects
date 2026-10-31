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

export interface EvidenceDetailView {
  evidence: EvidenceView;
  messages: { id: string; sequence: number; role: 'user' | 'assistant'; text: string }[];
  relations: EvidenceRelationDetail[];
}

export interface EvidenceFilters {
  messageId?: string;
  kind?: string;
  polarity?: string;
  certainty?: string;
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

export interface CoverageReportView extends CoverageReportSummary {
  inputHash: string;
  createdBy: string | null;
  findings: CoverageFinding[];
  limits: string[];
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

  async listEvidence(projectId: string, filters: EvidenceFilters = {}): Promise<EvidenceView[]> {
    let params = new HttpParams();
    for (const [name, value] of Object.entries(filters)) {
      if (value) params = params.set(name, value);
    }
    const page = await firstValueFrom(
      this.http.get<{ items: EvidenceView[] }>(`${this.base(projectId)}/evidence`, { params }),
    );
    return page.items;
  }

  getEvidence(projectId: string, evidenceId: string): Promise<EvidenceDetailView> {
    return firstValueFrom(
      this.http.get<EvidenceDetailView>(`${this.base(projectId)}/evidence/${enc(evidenceId)}`),
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
}
