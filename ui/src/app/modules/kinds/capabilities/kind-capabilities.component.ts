import { ActivatedRoute } from '@angular/router';

import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';

import { CapabilityCatalogModel } from '../../../domain';
import { LodgeService } from '../../../services/lodge.service';

/** Read-only reference view of one kind's capability catalog — the canonical "what is
 * this capability" page, linked from both this browser and the instance detail page's
 * capability cards (Phase 5). Not interactive: no confirm/invalidate here. */
@Component({
  selector: 'lodge-kind-capabilities',
  standalone: true,
  templateUrl: './kind-capabilities.component.html',
  styleUrls: ['./kind-capabilities.component.scss'],
  imports: [MatIconModule, MatChipsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KindCapabilitiesComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly lodgeService = inject(LodgeService);

  readonly kindCode = this.route.snapshot.paramMap.get('kind')!;

  readonly loading = signal(true);
  readonly catalog = signal<CapabilityCatalogModel | null>(null);

  constructor() {
    this.load();
  }

  async load() {
    this.loading.set(true);
    try {
      this.catalog.set(await this.lodgeService.getCapabilities(this.kindCode));
    } finally {
      this.loading.set(false);
    }
  }
}
