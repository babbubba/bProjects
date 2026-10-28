import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

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
