import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';

import { M3IconComponent } from '../../m3-icon/m3-icon.component';
import { findActiveNavigation, NavigationElement, rememberedRedirect } from '../navigation-tree';
import { NavigationTreeService } from '../navigation-tree.service';

/** Width, from each edge of the bar, over which the items shrink and fade out. */
const EDGE_ZONE = 110;

/**
 * Mobile navigation: an M3 floating toolbar (https://m3.material.io/components/toolbars)
 * whose icons scroll like an M3 carousel (https://m3.material.io/components/carousel).
 * Holds the first-level entries of the drawer; a module with sub-pages (or kind instances)
 * opens them in a popup above the bar. Replaces the drawer below the shell's mobile
 * breakpoint.
 */
@Component({
  selector: 'lodge-bottom-toolbar',
  standalone: true,
  templateUrl: './bottom-toolbar.component.html',
  styleUrls: ['./bottom-toolbar.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [M3IconComponent],
})
export class BottomToolbarComponent {
  private readonly router = inject(Router);
  private readonly track = viewChild<ElementRef<HTMLElement>>('track');

  private readonly treeService = inject(NavigationTreeService);
  private readonly tree = this.treeService.tree;
  private readonly lastChild = this.treeService.lastChild;

  readonly items = computed(() => this.tree().filter((e) => e.type === 'button'));

  /** The module whose sub-pages are showing in the popup. */
  readonly openModule = signal<NavigationElement | null>(null);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );
  private readonly active = computed(() => findActiveNavigation(this.tree(), this.url()));

  /** Horizontal centre of the button that opened the popup, in viewport pixels. */
  readonly anchorX = signal(0);

  private frame = 0;

  constructor() {
    // Whatever page is shown, its module scrolls to the middle of the bar.
    effect(() => {
      this.active();
      this.items();
      untracked(() =>
        setTimeout(() => {
          this.applyCarousel();
          this.track()
            ?.nativeElement.querySelector('.active')
            ?.scrollIntoView({ inline: 'center', block: 'nearest', behavior: 'smooth' });
        }),
      );
    });
  }

  onScroll() {
    // The popup is anchored to a button that just moved.
    this.openModule.set(null);
    if (!this.frame) {
      this.frame = requestAnimationFrame(() => {
        this.frame = 0;
        this.applyCarousel();
      });
    }
  }

  /** The carousel effect: items keep full size in the middle and shrink and fade as they
   * slide off an edge, but only on a side that has more to scroll to, so the first and the
   * last item rest at full size against the ends. */
  private applyCarousel() {
    const track = this.track()?.nativeElement;
    if (!track) {
      return;
    }
    const bounds = track.getBoundingClientRect();
    const moreLeft = track.scrollLeft > 1;
    const moreRight = track.scrollLeft < track.scrollWidth - track.clientWidth - 1;
    track.querySelectorAll<HTMLElement>('.toolbar-item').forEach((el) => {
      const rect = el.getBoundingClientRect();
      const centre = rect.left + rect.width / 2;
      const fromLeft = moreLeft ? (centre - bounds.left) / EDGE_ZONE : 1;
      const fromRight = moreRight ? (bounds.right - centre) / EDGE_ZONE : 1;
      const t = Math.min(Math.max(Math.min(fromLeft, fromRight), 0), 1);
      el.style.setProperty('--scale', String(0.15 + 0.85 * t));
      el.style.setProperty('--fade', String(0.05 + 0.95 * t));
    });
  }

  /** Highlighted: the page being shown, or the module that page belongs to. */
  isActive(element: NavigationElement): boolean {
    const match = this.active();
    return !!match && (match.leaf === element || match.parent === element);
  }

  /** A page goes there. A module goes to the sub-page it was last left on, like the
   * collapsed drawer; clicked again while already in it, it opens its sub-pages. */
  onItemClick(element: NavigationElement, event: Event) {
    if (element.subElements.length === 0) {
      this.go(element.redirect);
    } else if (!this.isActive(element)) {
      this.go(rememberedRedirect(element, this.lastChild()));
    } else {
      const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
      this.anchorX.set(rect.left + rect.width / 2);
      this.openModule.update((open) => (open === element ? null : element));
    }
  }

  go(redirect: string) {
    this.openModule.set(null);
    this.router.navigateByUrl(redirect);
  }
}
