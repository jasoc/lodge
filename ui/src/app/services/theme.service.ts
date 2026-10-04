import { isPlatformBrowser } from '@angular/common';
import { effect, inject, Injectable, PLATFORM_ID, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';

/** Every theme: `id` is the body's data-theme, defined in src/styles/theme/_<id>.scss
 * (Lodge's own tokens) and styles.scss (Material colors). */
export const THEMES = [
  { id: 'lodge-dark', label: 'Dark · amber' },
  { id: 'lodge-light-green', label: 'Light · green' },
  { id: 'lodge-dark-blue', label: 'Dark · blue' },
  { id: 'lodge-light-red', label: 'Light · red' },
] as const;

@Injectable({
  providedIn: 'root',
})
export class ThemeService {
  private readonly platformId = inject(PLATFORM_ID);
  private readonly router = inject(Router);

  readonly themes = THEMES;
  readonly currentThemeStr = signal<string>('lodge-dark');
  readonly currentWatermark = signal('');
  readonly watermarks: { [key: string]: string } = {};

  constructor() {
    if (isPlatformBrowser(this.platformId)) {
      // A theme that no longer exists (the retired grey one) falls back to the default.
      // The amber light theme became the green one.
      let localValue = localStorage.getItem('theme');
      if (localValue === 'lodge-light') {
        localValue = 'lodge-light-green';
      }
      if (localValue != null && THEMES.some((t) => t.id === localValue)) {
        this.currentThemeStr.set(localValue);
      }
    }

    // Sync data-theme attribute with signal
    effect(() => {
      const theme = this.currentThemeStr();
      if (isPlatformBrowser(this.platformId)) {
        document.body.setAttribute('data-theme', theme);
      }
    });

    this.router.events.subscribe((ev) => {
      if (ev instanceof NavigationEnd) {
        if (this.watermarks[ev.urlAfterRedirects]) {
          this.currentWatermark.set(this.watermarks[ev.url]);
        } else {
          this.currentWatermark.set('');
        }
      }
    });
  }

  public setThemeByString(theme: string) {
    this.currentThemeStr.set(theme);
    this.saveTheme();
  }

  private saveTheme() {
    if (isPlatformBrowser(this.platformId)) {
      localStorage.setItem('theme', this.currentThemeStr());
    }
  }
}
