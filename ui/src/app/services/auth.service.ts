import { inject, Injectable, signal } from '@angular/core';

import { AuthConfigModel, GroupModel, LoginResponseModel, MeModel, UserModel } from '../domain';
import { BackendService } from './backend.service';
import { TokenStorageService } from './token-storage.service';

/**
 * The only place the two auth planes actually differ (see `POST /api/v1/auth/login` and
 * `GET /api/v1/auth/config` on the server): in the no-auth homelab profile, `login()`
 * succeeds with no credentials and mints a token for an implicit local admin — so
 * `ensureSession()` just silently establishes one. In the Oidc
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
  /** Who the current session belongs to and their groups; null until loaded. */
  readonly me = signal<MeModel | null>(null);

  private configPromise: Promise<AuthConfigModel> | null = null;
  private renewal: Promise<boolean> | null = null;

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

  /**
   * Called by `authInterceptor` when the server rejected `staleToken`. Single-flighted, so
   * a page firing many requests at once mints one replacement session, not one each.
   * Resolves true when a fresh session is in place (NoAuth: silent re-login; or another
   * request already renewed it), false when the user has to log in again (Oidc).
   */
  renewSession(staleToken: string): Promise<boolean> {
    if (this.tokenStorage.get()?.token !== staleToken && this.userLogged()) {
      return Promise.resolve(true);
    }

    this.renewal ??= (async () => {
      try {
        this.logout();
        const config = await this.getConfig();
        if (config.mode !== 'NoAuth') {
          return false;
        }
        await this.login();
        return true;
      } catch {
        return false;
      } finally {
        this.renewal = null;
      }
    })();
    return this.renewal;
  }

  async login(): Promise<void> {
    const res = await this.post<LoginResponseModel>('/auth/login');
    const body = res.body!;
    this.setSession(body.token, body.subject_id);
    // Identity details only decide which buttons are live; a failure here must not
    // block signing in.
    await this.loadMe().catch(() => null);
  }

  async loadMe(): Promise<MeModel | null> {
    if (!this.userLogged()) {
      this.me.set(null);
      return null;
    }
    const res = await this.get<MeModel>('/auth/me');
    this.me.set(res.body!);
    return res.body!;
  }

  /** True when the current user may run an action that `requires` this group. */
  canRun(requires: string | null): boolean {
    const me = this.me();
    return !requires || !!me?.is_admin || (me?.groups.includes(requires) ?? false);
  }

  async getUsers(): Promise<UserModel[]> {
    return (await this.get<UserModel[]>('/users')).body!;
  }

  async getGroups(): Promise<GroupModel[]> {
    return (await this.get<GroupModel[]>('/groups')).body!;
  }

  /** Called by the `/auth/callback` route after the server's OIDC redirect hands back a
   * token in the URL fragment (never a query string, so it's never sent over the network
   * again). */
  setSession(token: string, subjectId: string): void {
    this.tokenStorage.set({ token, subjectId });
    this.subjectId.set(subjectId);
    this.me.set(null);
  }

  ssoLoginUrl(): string {
    const redirect = `${location.origin}/auth/callback`;
    return `/api/v1/auth/oidc/login?ui_redirect=${encodeURIComponent(redirect)}`;
  }

  logout(): void {
    this.tokenStorage.clear();
    this.subjectId.set(null);
    this.me.set(null);
  }
}
