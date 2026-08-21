import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';

import { AuthService } from './auth.service';

export class PermissionsService {
  /** Establishes a session before any guarded route activates. In the no-auth profile
   * this always succeeds with no user interaction. In the Oidc profile there's no silent
   * mint, so a missing session sends the browser to `/auth/login` (outside this guard's
   * own route tree) instead of activating the shell. */
  static async isUserLoggedFn(
    _route: ActivatedRouteSnapshot,
    _state: RouterStateSnapshot,
  ): Promise<boolean | UrlTree> {
    const authService = inject(AuthService);
    const router = inject(Router);
    await authService.ensureSession();
    if (authService.userLogged()) {
      return true;
    }
    return router.parseUrl('/auth/login');
  }
}
