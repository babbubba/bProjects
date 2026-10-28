import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AnalysisPreviewComponent } from './analysis-preview.component';
import { ChatComponent } from './chat.component';
import { ProjectApiService, ProjectView } from './project-api.service';

const dateFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'medium', timeStyle: 'short' });

/** Shell dell'applicazione: sidebar con il catalogo dei progetti e area del progetto aperto. */
@Component({
  selector: 'app-project-shell',
  imports: [AnalysisPreviewComponent, ChatComponent, FormsModule, RouterLink],
  templateUrl: './project-shell.component.html',
  styleUrl: './project-shell.component.scss',
})
export class ProjectShellComponent {
  private readonly api = inject(ProjectApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** Id del progetto dalla route /projects/:id (component input binding). */
  readonly id = input<string>();

  protected readonly projects = signal<ProjectView[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly listLoading = signal(true);
  protected readonly listError = signal('');

  protected readonly selected = signal<ProjectView | null>(null);
  protected readonly detailError = signal('');

  protected readonly sidebarOpen = signal(false);
  protected readonly formOpen = signal(false);
  protected readonly creating = signal(false);
  protected readonly createError = signal('');
  protected newName = '';
  protected newDescription = '';

  constructor() {
    void this.loadPage(null);
    effect(() => {
      const id = this.id();
      untracked(() => void this.open(id));
    });
  }

  protected formatDate(iso: string): string {
    return dateFormat.format(new Date(iso));
  }

  protected toggleSidebar(): void {
    this.sidebarOpen.update((open) => !open);
  }

  protected toggleForm(): void {
    this.formOpen.update((open) => !open);
    this.createError.set('');
  }

  protected async loadMore(): Promise<void> {
    await this.loadPage(this.nextCursor());
  }

  protected async create(): Promise<void> {
    if (!this.newName.trim()) {
      this.createError.set('Il nome del progetto è obbligatorio.');
      return;
    }

    this.creating.set(true);
    this.createError.set('');
    try {
      const project = await this.api.create(this.newName, this.newDescription.trim());
      this.projects.update((items) => [project, ...items]);
      this.newName = '';
      this.newDescription = '';
      this.formOpen.set(false);
      await this.router.navigate(['/projects', project.id]);
    } catch (e) {
      this.createError.set(this.createMessage(e));
    } finally {
      this.creating.set(false);
    }
  }

  protected async logout(): Promise<void> {
    try {
      await this.auth.logout();
    } finally {
      await this.router.navigateByUrl('/login');
    }
  }

  private async loadPage(cursor: string | null): Promise<void> {
    this.listLoading.set(true);
    this.listError.set('');
    try {
      const page = await this.api.list(cursor);
      const known = new Set(this.projects().map((p) => p.id));
      this.projects.update((items) => [...items, ...page.items.filter((p) => !known.has(p.id))]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.listError.set(this.failure(e, "Impossibile caricare l'elenco dei progetti."));
    } finally {
      this.listLoading.set(false);
    }
  }

  private async open(id: string | undefined): Promise<void> {
    this.detailError.set('');
    this.sidebarOpen.set(false);
    if (!id) {
      this.selected.set(null);
      return;
    }

    try {
      this.selected.set(await this.api.get(id));
    } catch (e) {
      this.selected.set(null);
      const notFound = e instanceof HttpErrorResponse && e.status === 404;
      this.detailError.set(
        notFound ? 'Progetto non trovato.' : this.failure(e, 'Impossibile aprire il progetto.'),
      );
    }
  }

  private failure(e: unknown, fallback: string): string {
    if (e instanceof HttpErrorResponse && e.status === 401) {
      void this.router.navigateByUrl('/login');
      return 'Sessione scaduta.';
    }
    return fallback;
  }

  private createMessage(e: unknown): string {
    if (e instanceof HttpErrorResponse && e.status === 422) {
      const errors = (e.error as { errors?: Record<string, string[]> } | null)?.errors;
      const first = errors ? Object.values(errors)[0]?.[0] : undefined;
      if (first) return first;
    }
    return this.failure(e, 'Impossibile creare il progetto. Riprova.');
  }
}
