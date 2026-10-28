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
}
