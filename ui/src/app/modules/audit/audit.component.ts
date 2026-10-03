import { ActivatedRoute, Router } from '@angular/router';

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
import { MatSort, MatSortModule } from '@angular/material/sort';
import { MatTableDataSource, MatTableModule } from '@angular/material/table';

import { GlobalAuditEventModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';

/** Every audit event across every kind/instance — one canonical audit UI, optionally
 * pre-filtered to a single instance via `?instance_id=`, e.g. linked from the instance
 * detail page. */
@Component({
  selector: 'lodge-audit',
  standalone: true,
  templateUrl: './audit.component.html',
  styleUrls: ['./audit.component.scss'],
  imports: [MatTableModule, MatSortModule, MatButtonModule, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuditComponent {
  private readonly lodgeService = inject(LodgeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sort = viewChild(MatSort);

  readonly loading = signal(true);
  readonly instanceIdFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('instance_id'),
  );
  readonly displayedColumns: string[] = [
    'kind_code',
    'instance_display_name',
    'event_type',
    'actor',
    'created_at',
  ];
  readonly dataSource = new MatTableDataSource<GlobalAuditEventModel>();

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
      const rows = await this.lodgeService.getAllEvents({
        instanceId: this.instanceIdFilter() ?? undefined,
      });
      this.dataSource.data = rows;
    } finally {
      this.loading.set(false);
    }
  }

  clearInstanceFilter() {
    this.instanceIdFilter.set(null);
    this.router.navigate([], { queryParams: {} });
    this.load();
  }

  openInstance(row: GlobalAuditEventModel) {
    if (row.instance_code) {
      this.router.navigateByUrl(`/instances/${row.kind_code}/${row.instance_code}`);
    }
  }
}
