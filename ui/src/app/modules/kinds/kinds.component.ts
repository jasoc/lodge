import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';

import { M3CardComponent } from '../../components/m3-card/m3-card.component';
import { KindModel } from '../../domain';
import { LodgeService } from '../../services/lodge.service';

/** Kind picker for the read-only capability catalog browser — one card per kind, each
 * linking to `/kinds/:kind/capabilities`. */
@Component({
  selector: 'lodge-kinds',
  standalone: true,
  templateUrl: './kinds.component.html',
  styleUrls: ['./kinds.component.scss'],
  imports: [M3CardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KindsComponent {
  private readonly lodgeService = inject(LodgeService);

  readonly loading = signal(true);
  readonly kinds = signal<KindModel[]>([]);

  constructor() {
    this.load();
  }

  async load() {
    this.loading.set(true);
    try {
      this.kinds.set(await this.lodgeService.getKinds());
    } finally {
      this.loading.set(false);
    }
  }
}
