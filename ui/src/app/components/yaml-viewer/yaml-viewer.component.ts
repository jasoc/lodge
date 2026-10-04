import loader from '@monaco-editor/loader';
import type * as Monaco from 'monaco-editor';

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';

import { ThemeService } from '../../services/theme.service';

// Monaco is served from the app's own assets (angular.json copies monaco-editor/min/vs
// there), never from a CDN, and only fetched the first time a viewer renders.
loader.config({ paths: { vs: new URL('assets/monaco/vs', document.baseURI).href } });

/**
 * Read-only YAML, rendered by Monaco: highlighting, folding, line numbers, search. Follows
 * the app theme (light themes → `vs`, dark → `vs-dark`). Falls back to a plain `<pre>` if
 * Monaco can't be loaded.
 */
@Component({
  selector: 'lodge-yaml-viewer',
  standalone: true,
  template: `
    @if (failed()) {
      <pre class="yaml-fallback">{{ value() }}</pre>
    } @else {
      <div #host class="yaml-host"></div>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .yaml-host {
      height: 70vh;
      min-height: 320px;
      border-radius: 12px;
      overflow: hidden;
      border: 1px solid var(--mat-sys-outline-variant);
    }
    .yaml-fallback {
      margin: 0;
      white-space: pre-wrap;
      font-family: 'Roboto Mono', monospace;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class YamlViewerComponent implements OnDestroy {
  readonly value = input<string>('');

  private readonly theme = inject(ThemeService);
  private readonly host = viewChild<ElementRef<HTMLElement>>('host');
  readonly failed = signal(false);

  private monaco: typeof Monaco | null = null;
  private editor: Monaco.editor.IStandaloneCodeEditor | null = null;

  constructor() {
    // Created only once the host is actually on screen with a size: content projected into
    // a hidden tab (m3-tab) is instantiated before it's ever attached, and Monaco needs a
    // laid-out element to measure.
    effect((onCleanup) => {
      const host = this.host()?.nativeElement;
      if (!host) {
        return;
      }
      const observer = new ResizeObserver(([entry]) => {
        if (!this.editor && entry.contentRect.width > 0 && entry.contentRect.height > 0) {
          void this.create(host);
        }
      });
      observer.observe(host);
      onCleanup(() => observer.disconnect());
    });

    effect(() => {
      const value = this.value() ?? '';
      if (this.editor && this.editor.getValue() !== value) {
        this.editor.setValue(value);
      }
    });

    effect(() => {
      const themeName = this.monacoTheme(this.theme.currentThemeStr());
      this.monaco?.editor.setTheme(themeName);
    });
  }

  ngOnDestroy() {
    this.destroyed = true;
    this.editor?.dispose();
    this.editor = null;
  }

  private creating = false;
  private destroyed = false;

  private async create(element: HTMLElement) {
    if (this.creating || this.editor) {
      return;
    }
    this.creating = true;
    try {
      const monaco: typeof Monaco = await loader.init();
      this.monaco = monaco;
      if (this.destroyed) {
        return;
      }
      this.editor = monaco.editor.create(element, {
        // The latest value, not the one current when loading started.
        value: this.value() ?? '',
        language: 'yaml',
        readOnly: true,
        domReadOnly: true,
        theme: this.monacoTheme(this.theme.currentThemeStr()),
        minimap: { enabled: false },
        automaticLayout: true,
        scrollBeyondLastLine: false,
        renderLineHighlight: 'none',
        folding: true,
        fontSize: 13,
      });
    } catch {
      this.failed.set(true);
    } finally {
      this.creating = false;
    }
  }

  private monacoTheme(appTheme: string): string {
    return appTheme.includes('light') ? 'vs' : 'vs-dark';
  }
}
