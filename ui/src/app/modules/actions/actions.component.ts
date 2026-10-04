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
import { FormsModule } from '@angular/forms';
import { MatChipsModule } from '@angular/material/chips';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS, MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatSort, MatSortModule } from '@angular/material/sort';
import { MatTableDataSource, MatTableModule } from '@angular/material/table';

import { GlobalActionModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';

const STATUSES = ['BLOCKED', 'QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'SUPERSEDED'] as const;
const POLICIES = ['AUTO', 'MANUAL_REQUIRED', 'OPTIONAL'] as const;

/** Every action across every kind/instance, with status/policy/kind filters over the same
 * `/api/v1/actions` query params the Drift page uses unfiltered-by-status. */
@Component({
  selector: 'lodge-actions',
  standalone: true,
  templateUrl: './actions.component.html',
  styleUrls: ['./actions.component.scss'],
  imports: [
    MatTableModule,
    MatSortModule,
    MatChipsModule,
    MatFormFieldModule,
    MatSelectModule,
    FormsModule,
    DatePipe,
  ],
  providers: [{ provide: MAT_FORM_FIELD_DEFAULT_OPTIONS, useValue: { appearance: 'outline' } }],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ActionsComponent {
  private readonly lodgeService = inject(LodgeService);
  private readonly router = inject(Router);
  private readonly sort = viewChild(MatSort);

  readonly statuses = STATUSES;
  readonly policies = POLICIES;

  readonly loading = signal(true);
  readonly statusFilter = signal<string | null>(null);
  readonly policyFilter = signal<string | null>(null);
  readonly displayedColumns: string[] = [
    'kind_code',
    'instance_display_name',
    'capability_code',
    'label',
    'policy',
    'status',
    'created_at',
    'completed_at',
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
      const rows = await this.lodgeService.getAllActions({
        status: this.statusFilter() ?? undefined,
        policy: this.policyFilter() ?? undefined,
      });
      this.dataSource.data = rows;
    } finally {
      this.loading.set(false);
    }
  }

  onStatusChange(status: string | null) {
    this.statusFilter.set(status);
    this.load();
  }

  onPolicyChange(policy: string | null) {
    this.policyFilter.set(policy);
    this.load();
  }

  statusColor(status: string): 'primary' | 'accent' | 'warn' {
    if (status === 'SUCCEEDED') return 'primary';
    if (status === 'FAILED') return 'warn';
    return 'accent';
  }

  openInstance(row: GlobalActionModel) {
    this.router.navigateByUrl(`/instances/${row.kind_code}/${row.instance_code}`);
  }
}
