import { DestroyRef, inject } from '@angular/core';

import { EventsService, LodgeChangeEvent } from './events.service';

/** Which server events make a page reload. Without `types`, every event except the
 * reconciliation-cycle ones; without an instance, events about any instance. */
export interface RefreshScope {
  /** Only events about this instance (or about none in particular). May be a function, read per event. */
  instanceId?: string | undefined | (() => string | undefined);
  types?: string[];
}

/**
 * What triggered a reload: the types of the events that came in since the last one, so a
 * page can re-read only what those can have changed. Null for a poll or a reconnect, when
 * anything may have changed and everything should be re-read.
 */
export type RefreshCause = ReadonlySet<string> | null;

/** With the event stream open, polling is only a safety net, so it relaxes to this. */
const FALLBACK_POLL_MS = 30000;
/** Minimum gap between two event-driven reloads. The first event after a quiet spell
 * reloads at once; the burst that follows (one change writes several) folds into one. */
const EVENT_THROTTLE_MS = 300;

/**
 * Keeps a page fresh for as long as the calling component is alive: it reloads the moment
 * the server says something changed (the shared event stream, see `EventsService`), and
 * polls as a fallback. Nothing runs while the tab is hidden; coming back reloads at once.
 * While the stream is connected the poll relaxes to `FALLBACK_POLL_MS`, so a page with
 * an in-flight run doesn't hammer the server every couple of seconds. `intervalMs` may be
 * a function, re-read before every tick, so a page can poll fast while something is in
 * flight and back off once it's idle. Reloads never overlap: one that is requested while
 * another runs is made once the first ends. `reload` is told what triggered it
 * (`RefreshCause`) and may ignore it. Call from a component constructor (an active
 * injection context is required), typically right after the initial `load()` call.
 */
export function autoRefresh(
  reload: (cause: RefreshCause) => void | Promise<void>,
  intervalMs: number | (() => number) = 10000,
  scope?: RefreshScope,
): void {
  const destroyRef = inject(DestroyRef);
  const events = inject(EventsService);
  const nextDelay = () => {
    const base = typeof intervalMs === 'function' ? intervalMs() : intervalMs;
    return events.connected() ? Math.max(base, FALLBACK_POLL_MS) : base;
  };

  let timer: ReturnType<typeof setTimeout>;
  let throttle: ReturnType<typeof setTimeout> | undefined;
  let destroyed = false;
  let running = false;
  let again = false;
  let lastRefreshAt = 0;
  // What the events since the last reload were; a poll or reconnect makes it a full reload.
  const pendingTypes = new Set<string>();
  let pendingFull = false;

  const refresh = async () => {
    if (running) {
      again = true;
      return;
    }
    running = true;
    lastRefreshAt = Date.now();
    const cause: RefreshCause = pendingFull || pendingTypes.size === 0 ? null : new Set(pendingTypes);
    pendingTypes.clear();
    pendingFull = false;
    try {
      if (!document.hidden) {
        await reload(cause);
      }
    } catch {
      // A failed refresh is retried on the next tick; never let it stop the loop.
    } finally {
      running = false;
    }
    if (again && !destroyed) {
      again = false;
      void refresh();
    }
  };

  const tick = async () => {
    await refresh();
    if (!destroyed) {
      timer = setTimeout(tick, nextDelay());
    }
  };

  const matches = (event: LodgeChangeEvent) => {
    if (scope?.types ? !scope.types.includes(event.type) : event.type === 'sync.cycle') {
      return false;
    }
    const instanceId =
      typeof scope?.instanceId === 'function' ? scope.instanceId() : scope?.instanceId;
    return !instanceId || !event.instance_id || event.instance_id === instanceId;
  };

  const unsubscribe = events.subscribe((event) => {
    if (event === null) {
      pendingFull = true;
    } else if (matches(event)) {
      pendingTypes.add(event.type);
    } else {
      return;
    }
    if (throttle === undefined) {
      const wait = Math.max(0, lastRefreshAt + EVENT_THROTTLE_MS - Date.now());
      throttle = setTimeout(() => {
        throttle = undefined;
        void refresh();
      }, wait);
    }
  });

  timer = setTimeout(tick, nextDelay());
  destroyRef.onDestroy(() => {
    destroyed = true;
    clearTimeout(timer);
    clearTimeout(throttle);
    unsubscribe();
  });
}
