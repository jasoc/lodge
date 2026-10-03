import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { KindSummaryOptions } from '../../../../domain';
import { LodgeService } from '../../../../services/lodge.service';
import { BaseLodgeWidget } from '../BaseLodgeWidget';

const selector = 'lodge-dashboards-widget-kind-summary';

/** Instance count and live drift count for one kind — picked by typing its code, since the
 * dashboard/GridStack widget options form has no live-populated dropdown. */
@Component({
  selector,
  standalone: true,
  templateUrl: './dashboards-widget-kind-summary.component.html',
  styleUrls: ['./dashboards-widget-kind-summary.component.scss'],
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardsWidgetKindSummaryComponent extends BaseLodgeWidget<KindSummaryOptions> {
  private readonly lodgeService = inject(LodgeService);

  readonly loading = signal(true);
  readonly error = signal(false);
  readonly instanceCount = signal(0);
  readonly driftCount = signal(0);

  constructor() {
    super();
    this.load();
  }

  public override getSelector(): string {
    return selector;
  }

  async load() {
    const kindCode = this.options?.kind_code;
    if (!kindCode) {
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.error.set(false);
    try {
      const [instances, drift] = await Promise.all([
        this.lodgeService.getInstances(kindCode),
        this.lodgeService.getAllActions({ kindCode, status: 'QUEUED' }),
      ]);
      this.instanceCount.set(instances.length);
      this.driftCount.set(drift.length);
    } catch {
      this.error.set(true);
    } finally {
      this.loading.set(false);
    }
  }
}
