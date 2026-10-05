import { Router } from '@angular/router';

import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { GlobalActionModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';
import {
  RunLogDialogComponent,
  RunLogDialogData,
} from '../instances/run-log-dialog/run-log-dialog.component';

/**
 * The action pool: every action running right now, across every instance, with how long it
 * has been going and a Stop button. Stopping asks the executor to kill the run; it then
 * ends as FAILED ("stopped by an operator") like any failure, for a human to retry.
 */
@Component({
  selector: 'lodge-pool',
  standalone: true,
  templateUrl: './pool.component.html',
  styleUrls: ['./pool.component.scss'],
  imports: [MatButtonModule, MatIconModule, MatSnackBarModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PoolComponent {
  private readonly lodgeService = inject(LodgeService);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);

  readonly loading = signal(true);
  readonly running = signal<GlobalActionModel[]>([]);
  /** Ids with a stop request in flight, so the button can't be pressed twice. */
  readonly stopping = signal<ReadonlySet<string>>(new Set());
  /** Ticks every second, so the elapsed times move without refetching. */
  readonly now = signal(Date.now());

  constructor() {
    this.load();
    autoRefresh(() => this.load({ silent: true }), 10000);

    const clock = setInterval(() => this.now.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(clock));
  }

  async load(opts: { silent?: boolean } = {}) {
    if (!opts.silent) {
      this.loading.set(true);
    }
    try {
      const rows = await this.lodgeService.getAllActions({ status: 'RUNNING' });
      // Oldest first: the one that has been going longest is the one to look at.
      this.running.set(rows.sort((a, b) => (a.updated_at ?? '').localeCompare(b.updated_at ?? '')));
    } finally {
      this.loading.set(false);
    }
  }

  /** "m:ss" since the run started (a RUNNING row was last touched when its run began). */
  elapsed(row: GlobalActionModel): string {
    if (!row.updated_at) {
      return '';
    }
    const seconds = Math.max(0, Math.floor((this.now() - new Date(row.updated_at).getTime()) / 1000));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  }

  async stop(row: GlobalActionModel) {
    await this.stopMany([row]);
  }

  async stopAll() {
    const rows = this.running();
    if (rows.length > 0 && confirm(`Stop all ${rows.length} running action(s)?`)) {
      await this.stopMany(rows, false);
    }
  }

  private async stopMany(rows: GlobalActionModel[], ask = true) {
    if (ask && !confirm(`Stop "${rows[0].label}" on ${rows[0].instance_display_name}?`)) {
      return;
    }
    this.stopping.update((s) => new Set([...s, ...rows.map((r) => r.id)]));
    try {
      const results = await Promise.all(
        rows.map((r) =>
          this.lodgeService
            .stopAction(r.kind_code, r.instance_code, r.id)
            .catch(() => ({ message: `Could not stop "${r.label}".` })),
        ),
      );
      this.snackBar.open(
        rows.length === 1 ? (results[0].message ?? 'Stop requested.') : `Stop requested for ${rows.length} actions.`,
        'Close',
        { duration: 4000 },
      );
      await this.load({ silent: true });
    } finally {
      this.stopping.update((s) => {
        const next = new Set(s);
        rows.forEach((r) => next.delete(r.id));
        return next;
      });
    }
  }

  openLog(row: GlobalActionModel) {
    this.dialog.open<RunLogDialogComponent, RunLogDialogData>(RunLogDialogComponent, {
      data: { kindCode: row.kind_code, instanceCode: row.instance_code, action: row },
      width: '960px',
      maxWidth: '95vw',
    });
  }

  openInstance(row: GlobalActionModel) {
    this.router.navigateByUrl(`/instances/${row.kind_code}/${row.instance_code}/queue`);
  }
}
