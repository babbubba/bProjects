import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Provenienza di un valore in token (contratto «Metriche di inferenza»): mai ricavata da lunghezza / 4. */
export type TokenSource = 'measured' | 'calculated' | 'estimated' | 'unavailable';

export interface TokenValue {
  value: number | null;
  source: TokenSource;
}

export type InferencePhase = 'chat' | 'analysis' | 'document' | 'compaction';

/** Telemetria tecnica reale di una richiesta AI; mai il contenuto del ragionamento. */
export interface InferenceMetrics {
  state: 'running' | 'completed' | 'failed' | 'interrupted';
  phase: InferencePhase;
  modelId: string;
  startedAt: string;
  durationMs: number | null;
  contextWindowTokens: TokenValue;
  maxOutputTokens: { value: number; confirmed: boolean };
  serverDefaultNPredict: number | null;
  reasoning: { requested: boolean; effective: 'on' | 'off' | 'unknown' };
  promptTokens: TokenValue;
  completionTokens: TokenValue;
  totalTokens: TokenValue;
  cachedTokens: TokenValue;
  finishReason: string | null;
  calls: number;
  compaction: null | {
    summaryCalls: number;
    summaryPromptTokens: TokenValue;
    summaryCompletionTokens: TokenValue;
  };
}

export type ContextUsageKind = 'chat' | 'analysis' | 'document';

export interface ContextCompositionItem {
  key: string;
  label: string;
  tokens: TokenValue;
  messageCount: number | null;
}

/** ContextUsage del contratto HTTP: monitor del contesto del progetto (GET context-usage). */
export interface ContextUsage {
  generatedAt: string;
  runtime: {
    status: 'online' | 'offline' | 'unverified';
    checkedAt: string | null;
    modelId: string;
    contextWindowTokens: TokenValue;
    configuredContextWindowTokens: number;
    serverDefaultNPredict: number | null;
    windowMismatch: boolean;
  };
  limits: {
    safetyMarginTokens: number;
    phases: Record<
      InferencePhase,
      { maxOutputTokens: number; enableThinking: boolean; inputBudgetTokens: number }
    >;
  };
  last: null | {
    kind: ContextUsageKind;
    refId: string;
    at: string;
    metrics: InferenceMetrics;
    usedPercent: number | null;
    outputReservedTokens: number;
    safetyMarginTokens: number;
    remainingTokens: TokenValue;
    staleReason: null | 'newer_messages';
  };
  composition: null | {
    promptTokens: TokenValue;
    items: ContextCompositionItem[];
    attributedTokens: number;
    unattributedTokens: TokenValue;
  };
  history: { messageCount: number; originalTokens: TokenValue };
  compaction: null | {
    at: string;
    tokensBefore: TokenValue;
    tokensAfter: TokenValue;
    tokensSaved: TokenValue;
    messagesSummarized: number;
  };
}

/** Sottoinsieme di ProjectView (contratto HTTP) usato dalla UI. */
export interface ProjectView {
  id: string;
  conversationId: string;
  name: string;
  description: string | null;
  createdAt: string;
  createdBy: string;
  /** Stato formale (opzionale per compatibilità): run attivo, ultima analisi e revisione corrente. */
  activeProcessingRunId?: string | null;
  latestAnalysisRunId?: string | null;
  currentAnalysisId?: string | null;
  currentRevisionId?: string | null;
  validationState?: string | null;
}

export interface ProjectPage {
  items: ProjectView[];
  nextCursor: string | null;
}

export interface MessageView {
  id: string;
  sequence: number;
  role: 'user' | 'assistant';
  text: string;
  createdAt: string;
  authorId: string | null;
  authorDisplayName: string | null;
  agentId: string | null;
  chatTurnId: string | null;
  replyToMessageId: string | null;
  /** Solo per i messaggi assistant; assente o null = record storico senza telemetria. */
  metrics?: InferenceMetrics | null;
}

export interface MessagePage {
  items: MessageView[];
  nextAfterSequence: number | null;
}

export interface PostMessageResult {
  message: MessageView;
  chatTurnId: string;
  /** true se il server ha riconosciuto lo stesso clientMessageId (200) invece di creare (201). */
  replayed: boolean;
}

export type ChatTurnStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';

export interface ChatTurnView {
  id: string;
  triggerMessageId: string;
  sourceCutoffSequence: number;
  status: ChatTurnStatus;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  deadlineAt: string;
  attemptCount: number;
  maxAttempts: number;
  nextAttemptAt: string | null;
  resultMessageId: string | null;
  error: { code: string; message: string | null } | null;
}

export type ProcessingRunStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';

/** ProcessingRunView del contratto HTTP: stato del run formale, mai un risultato parziale. */
export interface ProcessingRunView {
  id: string;
  kind: 'Analysis' | 'Document';
  analysisId: string | null;
  sourceCutoffSequence: number | null;
  status: ProcessingRunStatus;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  deadlineAt: string;
  attemptCount: number;
  maxAttempts: number;
  nextAttemptAt: string | null;
  lastObservedStage: string | null;
  resultRevisionId: string | null;
  draftId: string | null;
  error: { code: string; message: string | null } | null;
  /** Telemetria della richiesta AI del run; assente o null = nessuna telemetria disponibile. */
  metrics?: InferenceMetrics | null;
}

export interface AnalyzeResult {
  run: ProcessingRunView;
  /** true se il server ha riconosciuto la stessa Idempotency-Key (replay) invece di creare un run. */
  replayed: boolean;
}

export type AnalysisClassification = 'Declared' | 'Inferred' | 'Assumed';

export interface AnalysisItem {
  id: string;
  text: string;
  classification: AnalysisClassification;
  evidenceRefs: string[];
}

export interface AnalysisSource {
  id: string;
  kind: 'Message' | 'PmRevision';
  messageId: string | null;
  messageRole: 'user' | 'assistant' | null;
  revisionId: string | null;
}

export interface AnalysisConflict {
  id: string;
  description: string;
  status: 'Open' | 'Resolved';
  alternatives: AnalysisItem[];
  resolution: { selectedAlternativeIds: string[]; reason: string; evidenceRefs: string[] } | null;
}

/** AnalysisContent 1.0.0 (contracts/ai-workflow.md §4), modificabile dal PM tramite form semplice (T035). */
export interface AnalysisContent {
  schemaVersion: string;
  analysisId: string;
  projectId: string;
  sourceCutoffSequence: number;
  validationState: 'Unvalidated' | 'PmConfirmed';
  sources: AnalysisSource[];
  goals: AnalysisItem[];
  contextAndScope: { overview: AnalysisItem[]; included: AnalysisItem[]; excluded: AnalysisItem[] };
  requirements: AnalysisItem[];
  operationalFlows: AnalysisItem[];
  constraints: AnalysisItem[];
  dependencies: AnalysisItem[];
  assumptions: AnalysisItem[];
  openQuestions: AnalysisItem[];
  conflicts: AnalysisConflict[];
}

/** AnalysisView del contratto HTTP: anteprima e revisione corrente. */
export interface AnalysisView {
  analysisId: string;
  projectId: string;
  currentRevisionId: string;
  version: number;
  schemaVersion: string;
  content: AnalysisContent;
  validationState: 'Unvalidated' | 'PmConfirmed';
  origin: string;
  createdBy: string | null;
  createdAt: string;
  confirmedBy: string | null;
  confirmedAt: string | null;
  sourceCutoffSequence: number;
  hasNewNotes: boolean;
  canEdit: boolean;
  canConfirm: boolean;
  canGenerate: boolean;
}

/** DraftView del contratto HTTP: bozza Markdown proposta, mai approvata, con la revisione usata. */
export interface DraftView {
  id: string;
  projectId: string;
  processingRunId: string;
  analysisId: string;
  analysisRevisionId: string;
  analysisVersion: number;
  markdown: string;
  createdAt: string;
  status: string;
  approvalStatus: string;
  hasNewNotes: boolean;
  hasNewRevision: boolean;
  label: string;
}

/** RevisionView del contratto HTTP (sottoinsieme): revisione specifica con le fonti del suo contenuto. */
export interface RevisionView {
  analysisId: string;
  revisionId: string;
  version: number;
  isCurrent: boolean;
  content: AnalysisContent;
  validationState: 'Unvalidated' | 'PmConfirmed';
}

@Injectable({ providedIn: 'root' })
export class ProjectApiService {
  private readonly http = inject(HttpClient);

  list(cursor: string | null = null, limit = 25): Promise<ProjectPage> {
    let params = new HttpParams().set('limit', limit);
    if (cursor) {
      params = params.set('cursor', cursor);
    }
    return firstValueFrom(this.http.get<ProjectPage>('/api/projects', { params }));
  }

  get(id: string): Promise<ProjectView> {
    return firstValueFrom(this.http.get<ProjectView>(`/api/projects/${encodeURIComponent(id)}`));
  }

  create(name: string, description: string): Promise<ProjectView> {
    return firstValueFrom(
      this.http.post<ProjectView>('/api/projects', { name, description: description || null }),
    );
  }

  listMessages(
    projectId: string,
    afterSequence: number | null = null,
    limit = 100,
  ): Promise<MessagePage> {
    let params = new HttpParams().set('limit', limit);
    if (afterSequence !== null) {
      params = params.set('afterSequence', afterSequence);
    }
    return firstValueFrom(
      this.http.get<MessagePage>(`/api/projects/${encodeURIComponent(projectId)}/messages`, {
        params,
      }),
    );
  }

  /** Invia un messaggio PM; lo stesso clientMessageId ripete il salvataggio senza duplicarlo. */
  async postMessage(
    projectId: string,
    clientMessageId: string,
    text: string,
  ): Promise<PostMessageResult> {
    const response = await firstValueFrom(
      this.http.post<{ message: MessageView; chatTurnId: string }>(
        `/api/projects/${encodeURIComponent(projectId)}/messages`,
        { clientMessageId, text },
        { observe: 'response' },
      ),
    );
    const body = response.body!;
    return {
      message: body.message,
      chatTurnId: body.chatTurnId,
      replayed: response.status === 200,
    };
  }

  getChatTurn(projectId: string, turnId: string): Promise<ChatTurnView> {
    return firstValueFrom(
      this.http.get<ChatTurnView>(
        `/api/projects/${encodeURIComponent(projectId)}/chat-turns/${encodeURIComponent(turnId)}`,
      ),
    );
  }

  /** Comando esplicito Analizza: la stessa Idempotency-Key ripete la richiesta senza creare un secondo run. */
  async analyze(projectId: string, idempotencyKey: string): Promise<AnalyzeResult> {
    const response = await firstValueFrom(
      this.http.post<ProcessingRunView>(
        `/api/projects/${encodeURIComponent(projectId)}/analyses`,
        null,
        { headers: { 'Idempotency-Key': idempotencyKey }, observe: 'response' },
      ),
    );
    return { run: response.body!, replayed: response.status === 200 };
  }

  /** Monitor del contesto del progetto: ultima richiesta AI, composizione, storico e compattazione. */
  getContextUsage(projectId: string): Promise<ContextUsage> {
    return firstValueFrom(
      this.http.get<ContextUsage>(`/api/projects/${encodeURIComponent(projectId)}/context-usage`),
    );
  }

  getRun(projectId: string, runId: string): Promise<ProcessingRunView> {
    return firstValueFrom(
      this.http.get<ProcessingRunView>(
        `/api/projects/${encodeURIComponent(projectId)}/processing-runs/${encodeURIComponent(runId)}`,
      ),
    );
  }

  getAnalysis(projectId: string, analysisId: string): Promise<AnalysisView> {
    return firstValueFrom(
      this.http.get<AnalysisView>(
        `/api/projects/${encodeURIComponent(projectId)}/analyses/${encodeURIComponent(analysisId)}`,
      ),
    );
  }

  /** Salva l'anteprima come nuova revisione del PM; If-Match è l'ID della revisione corrente in forma quotata. */
  savePreview(
    projectId: string,
    analysisId: string,
    revisionId: string,
    content: AnalysisContent,
  ): Promise<AnalysisView> {
    return firstValueFrom(
      this.http.put<AnalysisView>(
        `/api/projects/${encodeURIComponent(projectId)}/analyses/${encodeURIComponent(analysisId)}/preview`,
        content,
        { headers: { 'If-Match': `"${revisionId}"` } },
      ),
    );
  }

  /**
   * Comando esplicito Genera documento sulla revisione confermata: la stessa Idempotency-Key ripete la richiesta
   * senza creare un secondo run (202 run attivo, 200 replay terminale).
   */
  async generateDocument(
    projectId: string,
    analysisId: string,
    revisionId: string,
    idempotencyKey: string,
  ): Promise<AnalyzeResult> {
    const response = await firstValueFrom(
      this.http.post<ProcessingRunView>(
        `/api/projects/${encodeURIComponent(projectId)}/analyses/${encodeURIComponent(analysisId)}/document-generations`,
        { revisionId },
        { headers: { 'Idempotency-Key': idempotencyKey }, observe: 'response' },
      ),
    );
    return { run: response.body!, replayed: response.status === 200 };
  }

  /** Ultima bozza riuscita; null se il progetto non ne ha ancora (404 draft_not_available). */
  async getDraft(projectId: string): Promise<DraftView | null> {
    try {
      return await firstValueFrom(
        this.http.get<DraftView>(`/api/projects/${encodeURIComponent(projectId)}/draft`),
      );
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 404) return null;
      throw e;
    }
  }

  getRevision(projectId: string, analysisId: string, version: number): Promise<RevisionView> {
    return firstValueFrom(
      this.http.get<RevisionView>(
        `/api/projects/${encodeURIComponent(projectId)}/analyses/${encodeURIComponent(analysisId)}/revisions/${version}`,
      ),
    );
  }

  /** Conferma la revisione corrente (ripetuta sulla stessa revisione è idempotente); non avvia alcuna generazione. */
  confirmPreview(projectId: string, analysisId: string, revisionId: string): Promise<AnalysisView> {
    return firstValueFrom(
      this.http.post<AnalysisView>(
        `/api/projects/${encodeURIComponent(projectId)}/analyses/${encodeURIComponent(analysisId)}/confirm`,
        null,
        { headers: { 'If-Match': `"${revisionId}"` } },
      ),
    );
  }
}
