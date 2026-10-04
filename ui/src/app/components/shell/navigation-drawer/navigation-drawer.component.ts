import { CdkObserveContent } from '@angular/cdk/observers';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';

import { AuthService } from '../../../services/auth.service';
import { LodgeService } from '../../../services/lodge.service';
import { ThemeService } from '../../../services/theme.service';
import { M3ButtonComponent } from '../m3-button/m3-button.component';
import {
  findActiveNavigation,
  NavigationElement,
  navigationElementsTree,
  rememberedRedirect,
} from '../navigation-tree';

const COLLAPSED_KEY = 'navigation-drawer-collapsed';
const LAST_CHILD_KEY = 'navigation-drawer-last-child';
const EXPANDED_KEY = 'navigation-drawer-expanded';

function readStored<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

function writeStored(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage unavailable (private mode, quota): the drawer just forgets next time.
  }
}

@Component({
  standalone: true,
  selector: 'app-navigation-drawer',
  templateUrl: './navigation-drawer.component.html',
  styleUrls: ['./navigation-drawer.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatChipsModule,
    ReactiveFormsModule,
    M3ButtonComponent,
    MatIconModule,
    MatButtonModule,
    MatInputModule,
    MatSelectModule,
    CdkObserveContent,
  ],
  providers: [
    {
      provide: MAT_FORM_FIELD_DEFAULT_OPTIONS,
      useValue: {
        appearance: 'outline',
      },
    },
    ThemeService,
  ],
})
export class NavigationDrawerComponent {
  readonly router = inject(Router);
  readonly authService = inject(AuthService);
  readonly themeService = inject(ThemeService);
  private readonly lodgeService = inject(LodgeService);

  readonly collapsed = signal(readStored<boolean>(COLLAPSED_KEY, false));
  readonly userWidgetCollapsed = signal(true);
  readonly navigationElementsTree = signal<NavigationElement[]>(navigationElementsTree);

  /** Modules whose sub-entries are open. Kept apart from `collapsed`, so collapsing the
   * drawer and opening it again brings back what was open. */
  private readonly expanded = signal(new Set(readStored<string[]>(EXPANDED_KEY, [])));
  /** Module name -> redirect of the sub-page it was last left on. */
  private readonly lastChild = signal(readStored<Record<string, string>>(LAST_CHILD_KEY, {}));

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );
  /** What the router is showing, as a drawer entry. Derived, so it is right after a
   * refresh or a deep link, and for instances that finish loading later. */
  private readonly active = computed(() =>
    findActiveNavigation(this.navigationElementsTree(), this.url()),
  );

  readonly themeForm = new FormGroup({
    theme: new FormControl(this.themeService.currentThemeStr()),
  });

  constructor() {
    this.loadKindInstanceNodes();

    // Landing on a sub-page remembers it for its module and opens that module.
    effect(() => {
      const match = this.active();
      const parent = match?.parent;
      if (!match || !parent) {
        return;
      }
      untracked(() => {
        const { leaf } = match;
        this.lastChild.update((m) =>
          m[parent.name] === leaf.redirect ? m : { ...m, [parent.name]: leaf.redirect },
        );
        this.expanded.update((set) => (set.has(parent.name) ? set : new Set(set).add(parent.name)));
      });
    });
    effect(() => writeStored(LAST_CHILD_KEY, this.lastChild()));
    effect(() => writeStored(EXPANDED_KEY, [...this.expanded()]));
    effect(() => writeStored(COLLAPSED_KEY, this.collapsed()));
  }

  /** Injects one expandable nav entry per kind, each listing its instances as
   * sub-elements, right after the "Lodge" separator — so instances are reachable from the
   * drawer without going through Home first. */
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
    this.navigationElementsTree.update((tree) => {
      const updated = [...tree];
      const tagIndex = updated.findIndex((el) => el.type === 'tag');
      const insertAt = tagIndex === -1 ? updated.length : tagIndex + 1;
      updated.splice(insertAt, 0, ...kindNodes);
      return updated;
    });
  }

  logout() {
    this.authService.logout();
    // Re-run the route guard, which re-establishes a session (a no-op ceremony in the
    // no-auth profile; an OIDC profile would redirect to sign-in here instead).
    this.router.navigateByUrl('/home');
  }

  ToggleCollapse() {
    if (!this.collapsed()) {
      this.userWidgetCollapsed.set(true);
    }
    this.collapsed.update((collapsed) => !collapsed);
  }

  isExpanded(element: NavigationElement): boolean {
    return this.expanded().has(element.name);
  }

  /** Highlighted: the page being shown, or the module that page belongs to. */
  isActive(element: NavigationElement): boolean {
    const match = this.active();
    return !!match && (match.leaf === element || match.parent === element);
  }

  forceUserWidgetExpand() {
    if (this.collapsed()) {
      this.collapsed.set(false);
    } else {
      return;
    }
    if (this.userWidgetCollapsed()) {
      this.userWidgetCollapsed.set(false);
    }
  }

  /** A first-level entry. Open drawer: a module toggles its sub-entries. Collapsed rail
   * (no room for them): a module goes straight to the sub-page it was last left on. */
  onEntryClick(element: NavigationElement) {
    if (element.subElements.length === 0) {
      this.router.navigateByUrl(element.redirect);
    } else if (this.collapsed()) {
      this.router.navigateByUrl(rememberedRedirect(element, this.lastChild()));
    } else {
      this.expanded.update((set) => {
        const next = new Set(set);
        if (!next.delete(element.name)) {
          next.add(element.name);
        }
        return next;
      });
    }
  }

  /** Collapsed rail only: a double click skips the remembered page for the module's root. */
  onEntryDoubleClick(element: NavigationElement) {
    if (this.collapsed() && element.subElements.length > 0) {
      this.router.navigateByUrl(element.redirect);
    }
  }
}
