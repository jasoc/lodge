import { isPlatformBrowser } from '@angular/common';
import { inject, Injectable, PLATFORM_ID, signal } from '@angular/core';

import { AuthService } from './auth.service';
import { TokenStorageService } from './token-storage.service';

/** An invalidation pushed by the server: something changed, re-read what you show. It
 * carries no data on purpose — the page refetches through the normal endpoints. */
export interface LodgeChangeEvent {
  /** The audit event type (`action.generated`, ...) or `sync.cycle`. */
  type: string;
  kind_code: string | null;
  /** Null for changes that aren't about one instance. */
  instance_id: string | null;
}

/** `null` means "you may have missed events, re-read everything": sent on every (re)connect. */
export type ChangeListener =(event: LodgeChangeEvent | null) => void;

const EVENTS_URL = '/api/v1/live';
const MIN_BACKOFF_MS = 1000;
const MAX_BACKOFF_MS = 30000;

/**
 * One Server-Sent Events stream per tab, shared by every page that wants live updates.
 * Opens when the first listener subscribes and closes with the last. `fetch` rather than
 * `EventSource`, because the stream needs the bearer token in a header, which `EventSource`
 * can't send. It closes while the tab is hidden (nobody is looking) and reconnects with
 * backoff after any failure; `connected` tells pollers they can slow down meanwhile.
 */
@Injectable({ providedIn: 'root' })
export class EventsService {
  private readonly platformId = inject(PLATFORM_ID);
  private readonly tokenStorage = inject(TokenStorageService);
  private readonly authService = inject(AuthService);

  /** True while the stream is open: pages use it to relax their fallback polling. */
  readonly connected = signal(false);

  private readonly listeners = new Set<ChangeListener>();
  private abort: AbortController | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private backoffMs = MIN_BACKOFF_MS;
  private readonly onVisibility = () => (document.hidden ? this.disconnect() : this.connect());

  subscribe(listener: ChangeListener): () => void {
    this.listeners.add(listener);
    if (this.listeners.size === 1 && isPlatformBrowser(this.platformId)) {
      document.addEventListener('visibilitychange', this.onVisibility);
      this.connect();
    }
    return () => {
      this.listeners.delete(listener);
      if (this.listeners.size === 0) {
        document.removeEventListener('visibilitychange', this.onVisibility);
        this.disconnect();
      }
    };
  }

  private connect(): void {
    if (this.abort || document.hidden || this.listeners.size === 0) {
      return;
    }
    const abort = new AbortController();
    this.abort = abort;
    void this.run(abort);
  }

  private disconnect(): void {
    clearTimeout(this.retryTimer);
    this.abort?.abort();
    this.abort = null;
    this.connected.set(false);
  }

  private async run(abort: AbortController): Promise<void> {
    try {
      const token = this.tokenStorage.get()?.token;
      const response = await fetch(EVENTS_URL, {
        headers: { Accept: 'text/event-stream', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
        signal: abort.signal,
      });
      if (response.status === 401 && token) {
        await this.authService.renewSession(token);
      }
      if (!response.ok || !response.body) {
        throw new Error(`event stream refused (${response.status})`);
      }

      this.backoffMs = MIN_BACKOFF_MS;
      this.connected.set(true);
      this.emit(null); // whatever happened while we were away

      const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      let buffer = '';
      for (;;) {
        const { value, done } = await reader.read();
        if (done) {
          break;
        }
        buffer += value;
        let end: number;
        while ((end = buffer.indexOf('\n\n')) >= 0) {
          this.handleFrame(buffer.slice(0, end));
          buffer = buffer.slice(end + 2);
        }
      }
    } catch {
      // Fall through to the reconnect below (an abort ends up here too, and is ignored).
    }

    if (this.abort !== abort) {
      return; // closed on purpose
    }
    this.abort = null;
    this.connected.set(false);
    this.retryTimer = setTimeout(() => this.connect(), this.backoffMs);
    this.backoffMs = Math.min(this.backoffMs * 2, MAX_BACKOFF_MS);
  }

  private handleFrame(frame: string): void {
    for (const line of frame.split('\n')) {
      if (!line.startsWith('data:')) {
        continue; // comments (heartbeats) and unknown fields
      }
      try {
        this.emit(JSON.parse(line.slice(5)) as LodgeChangeEvent);
      } catch {
        // A malformed frame is skipped; the next one is independent.
      }
    }
  }

  private emit(event: LodgeChangeEvent | null): void {
    for (const listener of this.listeners) {
      listener(event);
    }
  }
}
