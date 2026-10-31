import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * Aggiunge il bearer in memoria alle chiamate API e, su 401, tenta una sola volta il refresh
 * (cookie HttpOnly) prima di ripetere la richiesta. Le rotte /api/auth non passano di qui.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  if (!request.url.startsWith('/api/') || request.url.startsWith('/api/auth/')) {
    return next(request);
  }

  const withToken = (req: HttpRequest<unknown>) => {
    const token = auth.accessToken();
    return token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  };

  return next(withToken(request)).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        return from(auth.refresh()).pipe(
          switchMap((ok) => (ok ? next(withToken(request)) : throwError(() => error))),
        );
      }
      return throwError(() => error);
    }),
  );
};
