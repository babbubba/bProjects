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
}
