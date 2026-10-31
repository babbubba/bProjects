import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';
import { LoginComponent } from './auth/login.component';
import { ProjectShellComponent } from './projects/project-shell.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Accedi · bProjects' },
  { path: '', component: ProjectShellComponent, canActivate: [authGuard], title: 'bProjects' },
  {
    path: 'projects/:id',
    component: ProjectShellComponent,
    canActivate: [authGuard],
    title: 'bProjects',
  },
  { path: '**', redirectTo: '' },
];
