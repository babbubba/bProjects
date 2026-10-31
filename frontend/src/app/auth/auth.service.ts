import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

interface TokenResponse {
  accessToken: string;
  expiresAt: string;
}

/**
 * Sessione locale: l'access token vive solo in memoria (mai in storage); il refresh token è un
 * cookie HttpOnly gestito dal browser. Il cookie XSRF-TOKEN viene rimandato nell'header
 * X-XSRF-TOKEN dall'interceptor di default di Angular sulle richieste mutanti.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly token = signal<string | null>(null);
  private refreshInFlight: Promise<boolean> | null = null;

  readonly accessToken = this.token.asReadonly();
  readonly isAuthenticated = computed(() => this.token() !== null);

  async login(username: string, password: string): Promise<void> {
    const response = await firstValueFrom(this.http.post<TokenResponse>('/api/auth/login', { username, password }));
    this.token.set(response.accessToken);
  }

  /** Rinnova l'access token; più chiamate simultanee condividono un'unica richiesta. */
  refresh(): Promise<boolean> {
    this.refreshInFlight ??= this.doRefresh().finally(() => (this.refreshInFlight = null));
    return this.refreshInFlight;
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post<void>('/api/auth/logout', {}));
    } finally {
      this.token.set(null);
    }
  }

  private async doRefresh(): Promise<boolean> {
    try {
      const response = await firstValueFrom(this.http.post<TokenResponse>('/api/auth/refresh', {}));
      this.token.set(response.accessToken);
      return true;
    } catch {
      this.token.set(null);
      return false;
    }
  }
}
