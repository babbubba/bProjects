import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Ripristina la sessione dal refresh cookie dopo un ricaricamento; altrimenti rimanda al login. */
export const authGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router); // inject() non è valido dopo un await
  if (auth.isAuthenticated() || (await auth.refresh())) {
    return true;
  }
  return router.createUrlTree(['/login']);
};
