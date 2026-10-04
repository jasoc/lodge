import { NgTemplateOutlet } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

export interface TabBarItem {
  id: string;
  label: string;
  icon?: string;
  /** Set on every tab for a navigation bar (child routes); leave unset for in-page tabs. */
  link?: string | readonly unknown[];
}

/**
 * A thin row of tabs. Presentational: the owner says which one is `selected`, so it works
 * the same whether that comes from the router (tabs with a `link` render as anchors, and
 * the row is a navigation landmark) or from component state (buttons with the tablist
 * pattern: one tab stop, arrow keys move and select).
 */
@Component({
  selector: 'lodge-tab-bar',
  standalone: true,
  templateUrl: './tab-bar.component.html',
  styleUrls: ['./tab-bar.component.scss'],
  imports: [MatIconModule, NgTemplateOutlet, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TabBarComponent {
  readonly tabs = input.required<readonly TabBarItem[]>();
  readonly selected = input<string | null>(null);
  readonly label = input('Sections', { alias: 'aria-label' });
  readonly selectedChange = output<string>();

  private readonly bar = viewChild.required<ElementRef<HTMLElement>>('bar');
  private readonly indicator = viewChild.required<ElementRef<HTMLElement>>('indicator');

  constructor() {
    // One underline for the whole row: it slides to the selected tab and takes its width,
    // instead of every tab drawing its own. Placed by measuring, in the row's own scroll
    // coordinates, so it stays right when the row scrolls.
    afterRenderEffect(() => {
      this.selected();
      this.tabs();
      this.placeIndicator();
    });

    // The row's width changes (a collapsing sider), and so do the tabs' once the web font
    // has loaded.
    const observer = new ResizeObserver(() => this.placeIndicator());
    observer.observe(inject<ElementRef<HTMLElement>>(ElementRef).nativeElement);
    inject(DestroyRef).onDestroy(() => observer.disconnect());
    void document.fonts?.ready.then(() => this.placeIndicator());
  }

  private placeIndicator() {
    const bar = this.bar().nativeElement;
    const indicator = this.indicator().nativeElement;
    const active = bar.querySelector<HTMLElement>('.tab.active');
    if (!active) {
      indicator.style.opacity = '0';
      return;
    }
    // The underline spans the label, not the tab's padding.
    const inset = 12;
    indicator.style.width = `${active.offsetWidth - inset * 2}px`;
    indicator.style.transform = `translateX(${active.offsetLeft + inset}px)`;
    indicator.style.opacity = '1';
    // Bring a tab that is cut off by the row's edge into view.
    if (
      active.offsetLeft < bar.scrollLeft ||
      active.offsetLeft + active.offsetWidth > bar.scrollLeft + bar.clientWidth
    ) {
      bar.scrollTo({ left: active.offsetLeft - inset, behavior: 'smooth' });
    }
    // No slide from the origin on the very first placement.
    requestAnimationFrame(() => indicator.classList.add('ready'));
  }

  readonly isNav = computed(() => this.tabs().some((tab) => tab.link !== undefined));

  onKeydown(event: KeyboardEvent, index: number) {
    const count = this.tabs().length;
    const next =
      event.key === 'ArrowRight'
        ? (index + 1) % count
        : event.key === 'ArrowLeft'
          ? (index - 1 + count) % count
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? count - 1
              : -1;
    if (next < 0) {
      return;
    }
    event.preventDefault();
    this.selectedChange.emit(this.tabs()[next].id);
    const row = (event.currentTarget as HTMLElement).parentElement;
    (row?.children[next] as HTMLElement | undefined)?.focus();
  }
}
