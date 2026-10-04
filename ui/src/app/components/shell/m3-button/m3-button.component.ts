import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatRipple, MatRippleModule, RippleRef } from '@angular/material/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterModule } from '@angular/router';

import { ThemeService } from '../../../services/theme.service';
import { M3IconComponent } from '../../m3-icon/m3-icon.component';

/** How long the old glyph takes to slide out; it is removed from the DOM after this. */
const GLYPH_SWAP_MS = 450;

interface Glyph {
  id: number;
  name: string;
  state: 'idle' | 'enter' | 'leave';
}

@Component({
  selector: 'm3-button',
  standalone: true,
  templateUrl: './m3-button.component.html',
  styleUrls: ['./m3-button.component.scss'],
  providers: [ThemeService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatRippleModule, M3IconComponent, MatIconModule, RouterModule],
})
export class M3ButtonComponent {
  readonly rippleDir = viewChild(MatRipple);

  private rippleRef: RippleRef | undefined;

  readonly Text = input<string | null>(null);
  readonly Icon = input<string | null>(null);
  /** Shown on hover when the button has no text (a collapsed drawer). */
  readonly Tooltip = input<string | null>(null);
  readonly Ripple = input(true);
  readonly Type = input<'fab' | 'thin' | 'sidenav-left'>('fab');
  readonly iconFilled = input(false, { alias: 'icon-filled' });
  readonly Rippled = input(false);
  /** Icon-only look with the text kept in the DOM: it fades (and is clipped by the width
   * transition of the container) instead of being added and removed. */
  readonly Compact = input(false);

  readonly hasText = computed(() => {
    const t = this.Text();
    return t != null && t !== '';
  });

  /** The icon currently shown, plus the one sliding out while the icon is being swapped. */
  readonly glyphs = signal<Glyph[]>([]);
  private nextGlyphId = 0;

  constructor() {
    effect(() => {
      const name = this.Icon();
      untracked(() => this.swapGlyph(name));
    });

    effect(() => {
      const rippled = this.Rippled();
      rippled ? this.launchRipple() : this.fadeOutRipple();
    });
  }

  private swapGlyph(name: string | null) {
    const current = this.glyphs().find((g) => g.state !== 'leave');
    if (current?.name === name) {
      return;
    }
    const next: Glyph[] = this.glyphs()
      .filter((g) => g.state !== 'leave')
      .map((g) => ({ ...g, state: 'leave' as const }));
    if (name != null) {
      // The very first icon is just there; only a change slides.
      next.push({ id: this.nextGlyphId++, name, state: current ? 'enter' : 'idle' });
    }
    this.glyphs.set(next);
    setTimeout(
      () => this.glyphs.update((all) => all.filter((g) => g.state !== 'leave')),
      GLYPH_SWAP_MS,
    );
  }

  private launchRipple() {
    const ripple = this.rippleDir();
    if (ripple) {
      this.rippleRef = ripple.launch({
        persistent: true,
        centered: true,
      });
    }
  }

  private fadeOutRipple() {
    if (this.rippleRef) {
      this.rippleRef.fadeOut();
    }
  }
}
