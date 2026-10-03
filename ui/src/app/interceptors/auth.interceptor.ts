import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, from, switchMap, throwError } from 'rxjs';

import { AuthService } from '../services/auth.service';
import { TokenStorageService } from '../services/token-storage.service';

/**
 * Attaches the stored bearer token to every API call, and recovers from a token the
 * server no longer accepts (revoked, expired, or minted by a database that's since been
 * reset): on a 401 the stale session is dropped and `AuthService.renewSession` takes over —
 * a fresh silent login and one retry in the NoAuth profile, the login page under Oidc.
 * `/auth/*` calls are never retried, so a failing login can't loop.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const tokenStorage = inject(TokenStorageService);
  const auth = inject(AuthService);
  const router = inject(Router);

  const withToken = (request: HttpRequest<unknown>, token: string | undefined) =>
    token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;

  const sentToken = tokenStorage.get()?.token;

  return next(withToken(req, sentToken)).pipe(
    catchError((error: unknown) => {
      const recoverable =
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        sentToken !== undefined &&
        !req.url.includes('/auth/');
      if (!recoverable) {
        return throwError(() => error);
      }

      return from(auth.renewSession(sentToken)).pipe(
        switchMap((renewed) => {
          if (renewed) {
            return next(withToken(req, tokenStorage.get()?.token));
          }
          void router.navigate(['/auth/login']);
          return throwError(() => error);
        }),
      );
    }),
  );
};
