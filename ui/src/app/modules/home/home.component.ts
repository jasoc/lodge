import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { M3CardComponent } from '../../components/m3-card/m3-card.component';
import { KindModel, InstanceModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';

interface InstanceRow {
  kind: KindModel;
  instance: InstanceModel;
}

@Component({
  selector: 'lodge-home',
  standalone: true,
  templateUrl: './home.component.html',
  styleUrls: ['./home.component.scss'],
  imports: [MatIconModule, MatButtonModule, MatSnackBarModule, M3CardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HomeMainComponent {
  private readonly lodgeService = inject(LodgeService);
  private readonly snackBar = inject(MatSnackBar);

  readonly loading = signal(true);
  readonly reconciling = signal(false);
  readonly rows = signal<InstanceRow[]>([]);

  constructor() {
    this.load();
    autoRefresh(() => this.load({ silent: true }));
  }

  async load(opts: { silent?: boolean } = {}) {
    if (!opts.silent) {
      this.loading.set(true);
    }
    try {
      const kinds = await this.lodgeService.getKinds();
      const rows: InstanceRow[] = [];
      for (const kind of kinds) {
        const instances = await this.lodgeService.getInstances(kind.code);
        for (const instance of instances) {
          rows.push({ kind, instance });
        }
      }
      this.rows.set(rows);
    } finally {
      this.loading.set(false);
    }
  }

  async reconcileNow() {
    this.reconciling.set(true);
    try {
      const summary = await this.lodgeService.reconcile();
      this.snackBar.open(
        `Reconciled ${summary.instances_reconciled} instance(s), ${summary.drift_count} drifting.`,
        'Close',
        { duration: 3000 },
      );
      await this.load();
    } finally {
      this.reconciling.set(false);
    }
  }
}
