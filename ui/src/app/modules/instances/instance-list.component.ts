import { ActivatedRoute } from '@angular/router';

import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { M3CardComponent } from '../../components/m3-card/m3-card.component';
import { InstanceModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';

/** Instances of one kind — `/instances/:kind`. Same card-grid presentation as Home, just
 * scoped to a single kind instead of every kind. */
@Component({
  selector: 'lodge-instance-list',
  standalone: true,
  templateUrl: './instance-list.component.html',
  styleUrls: ['./instance-list.component.scss'],
  imports: [MatIconModule, MatButtonModule, MatSnackBarModule, M3CardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InstanceListComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly lodgeService = inject(LodgeService);
  private readonly snackBar = inject(MatSnackBar);

  readonly kindCode = this.route.snapshot.paramMap.get('kind')!;

  readonly loading = signal(true);
  readonly reconciling = signal(false);
  readonly instances = signal<InstanceModel[]>([]);

  constructor() {
    this.load();
    autoRefresh(() => this.load({ silent: true }));
  }

  async load(opts: { silent?: boolean } = {}) {
    if (!opts.silent) {
      this.loading.set(true);
    }
    try {
      this.instances.set(await this.lodgeService.getInstances(this.kindCode));
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
