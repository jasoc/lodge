import { Router } from '@angular/router';

import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatSort, MatSortModule } from '@angular/material/sort';
import { MatTableDataSource, MatTableModule } from '@angular/material/table';

import { GlobalActionModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';

/** Filtered view over the same `/api/v1/actions` data the Actions page uses — "drift" is
 * not a new data model, just every live (QUEUED) action across every instance. */
@Component({
  selector: 'lodge-drift',
  standalone: true,
  templateUrl: './drift.component.html',
  styleUrls: ['./drift.component.scss'],
  imports: [
    MatTableModule,
    MatSortModule,
    MatIconModule,
    MatButtonModule,
    MatChipsModule,
    MatSnackBarModule,
    DatePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DriftComponent {
  private readonly lodgeService = inject(LodgeService);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  private readonly sort = viewChild(MatSort);

  readonly loading = signal(true);
  readonly reconciling = signal(false);
  readonly displayedColumns: string[] = [
    'kind_code',
    'instance_display_name',
    'capability_code',
    'label',
    'policy',
    'created_at',
  ];
  readonly dataSource = new MatTableDataSource<GlobalActionModel>();

  constructor() {
    this.load();
    autoRefresh(() => this.load({ silent: true }));

    effect(() => {
      const s = this.sort();
      if (s) {
        this.dataSource.sort = s;
      }
    });
  }

  async load(opts: { silent?: boolean } = {}) {
    if (!opts.silent) {
      this.loading.set(true);
    }
    try {
      const rows = await this.lodgeService.getAllActions({ status: 'QUEUED' });
      this.dataSource.data = rows;
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

  openInstance(row: GlobalActionModel) {
    this.router.navigateByUrl(`/instances/${row.kind_code}/${row.instance_code}`);
  }
}
