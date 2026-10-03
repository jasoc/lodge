import { parse as parseYaml } from 'yaml';

import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { SignalValueOptions } from '../../../../domain';
import { LodgeService } from '../../../../services/lodge.service';
import { BaseLodgeWidget } from '../BaseLodgeWidget';

const selector = 'lodge-dashboards-widget-signal-value';

/** Current value of one dotted signal path, read client-side out of the instance's
 * already-fetched merged inventory YAML — no dedicated backend endpoint for this. */
@Component({
  selector,
  standalone: true,
  templateUrl: './dashboards-widget-signal-value.component.html',
  styleUrls: ['./dashboards-widget-signal-value.component.scss'],
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardsWidgetSignalValueComponent extends BaseLodgeWidget<SignalValueOptions> {
  private readonly lodgeService = inject(LodgeService);

  readonly loading = signal(true);
  readonly error = signal(false);
  readonly value = signal<string | null>(null);
  readonly found = signal(false);

  constructor() {
    super();
    this.load();
  }

  public override getSelector(): string {
    return selector;
  }

  async load() {
    const {
      kind_code: kindCode,
      instance_code: instanceCode,
      signal_path: signalPath,
    } = this.options ?? {};
    if (!kindCode || !instanceCode || !signalPath) {
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.error.set(false);
    try {
      const instance = await this.lodgeService.getInstance(kindCode, instanceCode);
      const inventory = instance.latest_yaml ? parseYaml(instance.latest_yaml) : null;
      const resolved = this.resolvePath(inventory, signalPath);
      this.found.set(resolved.found);
      this.value.set(resolved.found ? this.formatValue(resolved.value) : null);
    } catch {
      this.error.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  private resolvePath(root: unknown, path: string): { found: boolean; value: unknown } {
    let current = root;
    for (const segment of path.split('.')) {
      if (current == null || typeof current !== 'object' || !(segment in current)) {
        return { found: false, value: undefined };
      }
      current = (current as Record<string, unknown>)[segment];
    }
    return { found: true, value: current };
  }

  private formatValue(value: unknown): string {
    return typeof value === 'string' ? value : JSON.stringify(value);
  }
}
