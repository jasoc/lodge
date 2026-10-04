import { signal } from '@angular/core';

/**
 * One drawer entry. Pure data: what is active or expanded is derived from the router and
 * kept by the drawer, never stored on the nodes, so the tree can be shared and rebuilt
 * (instances load asynchronously) without losing state.
 */
export class NavigationElement {
  public subElements: NavigationElement[] = [];
  public name: string = '';
  public icon: string = '';
  public redirect: string = '';
  public type: 'button' | 'tag' = 'button';
  public constructor(init?: Partial<NavigationElement>) {
    Object.assign(this, init);
  }
}

/** The entry a URL belongs to: a leaf (a top-level entry without children, or a sub-entry)
 * and, for a sub-entry, the module it hangs off. */
export interface ActiveNavigation {
  leaf: NavigationElement;
  parent: NavigationElement | null;
}

/**
 * Finds the entry for `url` by longest redirect prefix, so `instances/k/i/anything` lights
 * up that instance rather than its kind's Overview, and `home/about` wins over `home`.
 * A module itself is never a candidate: its `redirect` is the same as its Overview's.
 */
export function findActiveNavigation(
  tree: readonly NavigationElement[],
  url: string,
): ActiveNavigation | null {
  const path = url.split(/[?#]/)[0].replace(/^\/+|\/+$/g, '');
  const candidates: ActiveNavigation[] = [];
  const consider = (leaf: NavigationElement, parent: NavigationElement | null) => {
    if (leaf.type !== 'button' || !leaf.redirect) {
      return;
    }
    if (path === leaf.redirect || path.startsWith(leaf.redirect + '/')) {
      candidates.push({ leaf, parent });
    }
  };
  for (const element of tree) {
    if (element.subElements.length === 0) {
      consider(element, null);
    } else {
      element.subElements.forEach((sub) => consider(sub, element));
    }
  }
  return candidates.reduce<ActiveNavigation | null>(
    (best, c) => (!best || c.leaf.redirect.length > best.leaf.redirect.length ? c : best),
    null,
  );
}

/** Where a click on a collapsed module goes: the sub-page it was last left on, or the
 * module's own root when there is none (or that page no longer exists). */
export function rememberedRedirect(
  module: NavigationElement,
  lastChild: Readonly<Record<string, string>>,
): string {
  const remembered = lastChild[module.name];
  return module.subElements.find((sub) => sub.redirect === remembered)?.redirect ?? module.redirect;
}

/** Icons the Home entry cycles through, one every `HOME_ICON_INTERVAL_MS`. */
const HOME_ICONS = [
  'home',
  'mosque',
  'temple_hindu',
  'temple_buddhist',
  'church',
  'synagogue',
  'globe',
  'planet',
  'flare',
  'psychiatry',
  'cruelty_free',
  'sailing',
  'rocket_launch',
  'auto_awesome',
  'forest',
  'ev_shadow',
  'raven',
  'wind_power',
  'nest_eco_leaf',
  'sprint',
  'sports_and_outdoors',
  'token',
  'orbit',
  'watch_vibration',
  'deployed_code',
  'diamond',
];

const HOME_ICON_INTERVAL_MS = 5000;

const homeIconIndex = signal(0);
setInterval(() => homeIconIndex.update((i) => (i + 1) % HOME_ICONS.length), HOME_ICON_INTERVAL_MS);

// A getter over a signal, so OnPush templates reading `icon` re-render when it rotates.
const homeElement = new NavigationElement({
  name: 'Home',
  redirect: 'home',
  subElements: [
    new NavigationElement({
      name: 'Overview',
      icon: 'navigate_next',
      redirect: 'home',
    }),
    new NavigationElement({
      name: 'About',
      icon: 'info',
      redirect: 'home/about',
    }),
  ],
});
Object.defineProperty(homeElement, 'icon', { get: () => HOME_ICONS[homeIconIndex()] });

export const navigationElementsTree: NavigationElement[] = [
  homeElement,
  new NavigationElement({
    type: 'tag',
    name: 'Lodge',
    icon: 'token',
  }),
  new NavigationElement({
    name: 'Reconciliation',
    icon: 'sync',
    redirect: 'reconciliation',
  }),
  new NavigationElement({
    name: 'Drift',
    icon: 'sync_problem',
    redirect: 'drift',
  }),
  new NavigationElement({
    name: 'Actions',
    icon: 'bolt',
    redirect: 'actions',
  }),
  new NavigationElement({
    name: 'Audit Log',
    icon: 'history',
    redirect: 'audit',
  }),
  new NavigationElement({
    name: 'Kinds',
    icon: 'category',
    redirect: 'kinds',
  }),
  new NavigationElement({
    name: 'Dashboards',
    icon: 'dataset',
    redirect: 'dashboards',
  }),
  new NavigationElement({
    name: 'Users & groups',
    icon: 'group',
    redirect: 'users',
  }),
  new NavigationElement({
    name: 'Settings',
    icon: 'settings',
    redirect: 'settings',
  }),
];
