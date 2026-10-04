import { Directive, ElementRef, inject } from '@angular/core';

/**
 * Text that does not fit its box is clipped with an ellipsis; while hovered it scrolls to its
 * end and back. The host must be `overflow: hidden` with a single inline child (see
 * `.marquee` in the page styles): the child is what moves, by `--marquee-shift`, which is
 * measured on mouseenter because it depends on the current width.
 */
@Directive({
  selector: '[lodgeMarquee]',
  standalone: true,
  host: {
    '(mouseenter)': 'measure()',
  },
})
export class MarqueeDirective {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  measure() {
    const shift = Math.max(0, this.host.scrollWidth - this.host.clientWidth);
    this.host.style.setProperty('--marquee-shift', `${shift}px`);
    this.host.style.setProperty('--marquee-duration', `${Math.max(1.2, shift / 40)}s`);
  }
}
