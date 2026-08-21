import { isPlatformBrowser } from '@angular/common';
import { inject, Injectable, PLATFORM_ID } from '@angular/core';

const STORAGE_KEY = 'lodge.session';

export interface StoredSession {
  token: string;
  subjectId: string;
}

/**
 * Per-viewer session storage — same idea as the CLI's `~/.config/lodge/credentials`,
 * just backed by localStorage instead of a file. There's no browser storage during SSR,
 * so an in-memory fallback holds the session for that render's lifetime instead — each
 * SSR request gets its own fresh instance of this service, so it can't leak between
 * requests. The browser mints its own separate session after hydration.
 */
@Injectable({ providedIn: 'root' })
export class TokenStorageService {
  private readonly platformId = inject(PLATFORM_ID);
  private inMemorySession: StoredSession | null = null;

  get(): StoredSession | null {
    if (!isPlatformBrowser(this.platformId)) {
      return this.inMemorySession;
    }
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as StoredSession) : null;
  }

  set(session: StoredSession): void {
    if (!isPlatformBrowser(this.platformId)) {
      this.inMemorySession = session;
      return;
    }
    localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  }

  clear(): void {
    if (!isPlatformBrowser(this.platformId)) {
      this.inMemorySession = null;
      return;
    }
    localStorage.removeItem(STORAGE_KEY);
  }
}
