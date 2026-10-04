import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { SyncCycleModel, SyncCyclesModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';

/**
 * What the reconciliation loop has been doing: the latest cycles, what each one did, and —
 * front and center — the inventory/catalog validation errors the most recent cycle found.
 * Nothing else in the UI surfaces those, and an invalid file silently stops its instance
 * (or whole kind) from being reconciled.
 */
@Component({
  selector: 'lodge-reconciliation',
  standalone: true,
  templateUrl: './reconciliation.component.html',
  styleUrls: ['./reconciliation.component.scss'],
  imports: [
    MatButtonModule,
    MatChipsModule,
    MatExpansionModule,
    MatIconModule,
    MatSnackBarModule,
    DatePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReconciliationComponent {
  private readonly lodgeService = inject(LodgeService);
  private readonly snackBar = inject(MatSnackBar);

  readonly data = signal<SyncCyclesModel | null>(null);
  readonly reconciling = signal(false);

  /** The newest cycle that finished — its errors are the inventory's current state. */
  readonly latest = computed(
    () => this.data()?.cycles.find((c) => c.completed_at !== null) ?? null,
  );

  constructor() {
    this.load();
    autoRefresh(
      () => this.load(),
      () => 10000,
    );
  }

  async load() {
    this.data.set(await this.lodgeService.getCycles(25));
  }

  async reconcileNow() {
    this.reconciling.set(true);
    try {
      const summary = await this.lodgeService.reconcile();
      const errors = summary.validation_errors.length;
      this.snackBar.open(
        `Reconciled · ${summary.drift_count} drifting` +
          (errors ? ` · ${errors} validation error(s)` : ''),
        'Close',
        { duration: 3000 },
      );
    } catch {
      this.snackBar.open('Reconciliation failed — see the server log.', 'Close', {
        duration: 5000,
      });
    } finally {
      this.reconciling.set(false);
      await this.load();
    }
  }

  duration(cycle: SyncCycleModel): string {
    if (!cycle.completed_at) {
      return 'running';
    }
    const ms = new Date(cycle.completed_at).getTime() - new Date(cycle.started_at).getTime();
    return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
  }

  ago(iso: string): string {
    const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (seconds < 60) return 'just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
    return `${Math.floor(seconds / 86400)}d ago`;
  }

  /** "lab: something wrong" → ["lab", "something wrong"]; the source, when the message names one. */
  splitSource(message: string): { source: string | null; text: string } {
    const match = /^([^\s:]+): (.*)$/s.exec(message);
    return match ? { source: match[1], text: match[2] } : { source: null, text: message };
  }
}
