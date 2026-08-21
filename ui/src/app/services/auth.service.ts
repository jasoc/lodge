import { inject, Injectable, signal } from '@angular/core';

import { AuthConfigModel, LoginResponseModel } from '../domain';
import { BackendService } from './backend.service';
import { TokenStorageService } from './token-storage.service';

/**
 * The only place the two auth planes actually differ (see `POST /api/v1/auth/login` and
 * `GET /api/v1/auth/config` on the server): in the no-auth homelab profile, `login()`
 * succeeds unconditionally with no credentials and mints a token for an implicit
 * local-admin identity — so `ensureSession()` just silently establishes one. In the Oidc
 * profile there is no unconditional mint; `ensureSession()` leaves the caller logged out
 * and `PermissionsService`'s route guard sends them to `/auth/login` instead, which
 * navigates the whole browser to the server's own `/api/v1/auth/oidc/login` (the server
 * talks to the IdP, never this SPA directly). Everything downstream of a minted token —
 * the interceptor, every other component — stays the same either way.
 */
@Injectable({ providedIn: 'root' })
export class AuthService extends BackendService {
  private readonly tokenStorage = inject(TokenStorageService);

  readonly subjectId = signal<string | null>(this.tokenStorage.get()?.subjectId ?? null);

  private configPromise: Promise<AuthConfigModel> | null = null;

  userLogged(): boolean {
    return this.tokenStorage.get() !== null;
  }

  /** Cached for the tab's lifetime — the auth mode never changes without a server restart. */
  async getConfig(): Promise<AuthConfigModel> {
    this.configPromise ??= this.get<AuthConfigModel>('/auth/config').then((res) => res.body!);
    return this.configPromise;
  }

  async ensureSession(): Promise<void> {
    if (this.userLogged()) {
      return;
    }
    const config = await this.getConfig();
    if (config.mode === 'NoAuth') {
      await this.login();
    }
    // Oidc: nothing to do here silently — the route guard redirects to /auth/login.
  }

  async login(): Promise<void> {
    const res = await this.post<LoginResponseModel>('/auth/login');
    const body = res.body!;
    this.setSession(body.token, body.subject_id);
  }

  /** Called by the `/auth/callback` route after the server's OIDC redirect hands back a
   * token in the URL fragment (never a query string, so it's never sent over the network
   * again). */
  setSession(token: string, subjectId: string): void {
    this.tokenStorage.set({ token, subjectId });
    this.subjectId.set(subjectId);
  }

  ssoLoginUrl(): string {
    const redirect = `${location.origin}/auth/callback`;
    return `/api/v1/auth/oidc/login?ui_redirect=${encodeURIComponent(redirect)}`;
  }

  logout(): void {
    this.tokenStorage.clear();
    this.subjectId.set(null);
  }
}
