import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from './auth.service';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  template: `
    <section class="login-card card mx-auto" aria-labelledby="login-title">
      <div class="card-body p-4">
        <h1 id="login-title" class="h4 mb-1">Accedi a bProjects</h1>
        <p class="text-secondary mb-4">Usa l'account PM fornito dal tuo amministratore.</p>

        <form (ngSubmit)="submit()" novalidate>
          <div class="mb-3">
            <label for="username" class="form-label">Nome utente</label>
            <input
              id="username"
              name="username"
              class="form-control"
              autocomplete="username"
              required
              [(ngModel)]="username"
              [attr.aria-describedby]="error() ? 'login-error' : null"
            />
          </div>
          <div class="mb-3">
            <label for="password" class="form-label">Password</label>
            <input
              id="password"
              name="password"
              type="password"
              class="form-control"
              autocomplete="current-password"
              required
              [(ngModel)]="password"
              [attr.aria-describedby]="error() ? 'login-error' : null"
            />
          </div>

          <div id="login-error" class="alert alert-danger" role="alert" [hidden]="!error()">
            {{ error() }}
          </div>

          <button type="submit" class="btn btn-primary w-100" [disabled]="busy()">
            {{ busy() ? 'Accesso in corso…' : 'Accedi' }}
          </button>
        </form>
      </div>
    </section>
  `,
  styles: `
    .login-card {
      max-width: 26rem;
      margin-top: 4rem;
    }
  `,
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected username = '';
  protected password = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected async submit(): Promise<void> {
    if (!this.username.trim() || !this.password) {
      this.error.set('Inserisci nome utente e password.');
      return;
    }

    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.login(this.username.trim(), this.password);
      await this.router.navigateByUrl('/');
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.busy.set(false);
    }
  }

  private message(e: unknown): string {
    const status = e instanceof HttpErrorResponse ? e.status : 0;
    if (status === 401) return 'Nome utente o password non validi.';
    if (status === 403) return "L'account non è abilitato come PM.";
    return 'Servizio non raggiungibile. Riprova tra poco.';
  }
}
