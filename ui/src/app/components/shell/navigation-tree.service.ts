import { computed, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';

import { LodgeService } from '../../services/lodge.service';
import { findActiveNavigation, NavigationElement, navigationElementsTree } from './navigation-tree';

const LAST_CHILD_KEY = 'navigation-drawer-last-child';

export function readStored<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

export function writeStored(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage unavailable (private mode, quota): the drawer just forgets next time.
  }
}

/**
 * The navigation tree both the drawer and the bottom toolbar render: the static entries
 * plus one expandable entry per kind, listing its instances, injected right after the
 * "Lodge" separator so instances are reachable without going through Home first. It also
 * remembers, per module, the sub-page it was last left on.
 */
@Injectable({ providedIn: 'root' })
export class NavigationTreeService {
  private readonly lodgeService = inject(LodgeService);
  private readonly router = inject(Router);

  readonly tree = signal<NavigationElement[]>(navigationElementsTree);

  /** Module name -> redirect of the sub-page it was last left on. */
  readonly lastChild = signal(readStored<Record<string, string>>(LAST_CHILD_KEY, {}));

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  constructor() {
    this.loadKindInstanceNodes();

    // Landing on a sub-page remembers it for its module.
    effect(() => {
      const match = findActiveNavigation(this.tree(), this.url());
      const parent = match?.parent;
      if (!match || !parent) {
        return;
      }
      untracked(() =>
        this.lastChild.update((m) =>
          m[parent.name] === match.leaf.redirect
            ? m
            : { ...m, [parent.name]: match.leaf.redirect },
        ),
      );
    });
    effect(() => writeStored(LAST_CHILD_KEY, this.lastChild()));
  }

  private async loadKindInstanceNodes() {
    const kinds = await this.lodgeService.getKinds();
    const kindNodes: NavigationElement[] = [];
    for (const kind of kinds) {
      const instances = await this.lodgeService.getInstances(kind.code);
      kindNodes.push(
        new NavigationElement({
          name: kind.name,
          icon: 'dns',
          redirect: `instances/${kind.code}`,
          subElements: [
            new NavigationElement({
              name: 'Overview',
              icon: 'navigate_next',
              redirect: `instances/${kind.code}`,
            }),
            ...instances.map(
              (instance) =>
                new NavigationElement({
                  name: instance.display_name,
                  icon: 'dns',
                  redirect: `instances/${kind.code}/${instance.instance_code}`,
                }),
            ),
          ],
        }),
      );
    }
    if (kindNodes.length === 0) {
      return;
    }
    this.tree.update((tree) => {
      const updated = [...tree];
      const tagIndex = updated.findIndex((el) => el.type === 'tag');
      const insertAt = tagIndex === -1 ? updated.length : tagIndex + 1;
      updated.splice(insertAt, 0, ...kindNodes);
      return updated;
    });
  }
}
