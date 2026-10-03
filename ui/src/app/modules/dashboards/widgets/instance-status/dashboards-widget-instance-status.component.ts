import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { InstanceStatusOptions } from '../../../../domain';
import { LodgeService } from '../../../../services/lodge.service';
import { BaseLodgeWidget } from '../BaseLodgeWidget';

const selector = 'lodge-dashboards-widget-instance-status';

/** Pending-action count and last-reconciled time for one instance. */
@Component({
  selector,
  standalone: true,
  templateUrl: './dashboards-widget-instance-status.component.html',
  styleUrls: ['./dashboards-widget-instance-status.component.scss'],
  imports: [DatePipe, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardsWidgetInstanceStatusComponent extends BaseLodgeWidget<InstanceStatusOptions> {
  private readonly lodgeService = inject(LodgeService);

  readonly loading = signal(true);
  readonly error = signal(false);
  readonly pendingCount = signal(0);
  readonly lastRevisionAt = signal<string | null>(null);

  constructor() {
    super();
    this.load();
  }

  public override getSelector(): string {
    return selector;
  }

  async load() {
    const { kind_code: kindCode, instance_code: instanceCode } = this.options ?? {};
    if (!kindCode || !instanceCode) {
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.error.set(false);
    try {
      const [instance, actions] = await Promise.all([
        this.lodgeService.getInstance(kindCode, instanceCode),
        this.lodgeService.getActions(kindCode, instanceCode),
      ]);
      this.lastRevisionAt.set(instance.instance.last_revision_at);
      this.pendingCount.set(
        actions.filter((a) => a.status === 'QUEUED' || a.status === 'FAILED').length,
      );
    } catch {
      this.error.set(true);
    } finally {
      this.loading.set(false);
    }
  }
}
