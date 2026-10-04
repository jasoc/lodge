import { DestroyRef, inject } from '@angular/core';

/**
 * Polls `reload` for as long as the calling component is alive, and skips a tick while
 * the tab is hidden (no point re-fetching data nobody's looking at). A homelab tool with
 * one operator doesn't need push infrastructure (WebSockets/SignalR) to avoid manual
 * refreshes — a modest interval is enough, and it's a one-line opt-in rather than new
 * backend surface. `intervalMs` may be a function, re-read before every tick, so a page
 * can poll fast while something is in flight and back off once it's idle. Call from a
 * component constructor (an active injection context is required), typically right
 * after the initial `load()` call.
 */
export function autoRefresh(
  reload: () => void | Promise<void>,
  intervalMs: number | (() => number) = 10000,
): void {
  const destroyRef = inject(DestroyRef);
  const nextDelay = () => (typeof intervalMs === 'function' ? intervalMs() : intervalMs);

  let timer: ReturnType<typeof setTimeout>;
  let destroyed = false;
  const tick = async () => {
    if (!document.hidden) {
      try {
        await reload();
      } catch {
        // A failed refresh is retried on the next tick; never let it stop the loop.
      }
    }
    if (!destroyed) {
      timer = setTimeout(tick, nextDelay());
    }
  };

  timer = setTimeout(tick, nextDelay());
  destroyRef.onDestroy(() => {
    destroyed = true;
    clearTimeout(timer);
  });
}
