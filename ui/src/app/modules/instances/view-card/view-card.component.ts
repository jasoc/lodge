import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { CapabilityViewModel } from '../../../domain';
import { layoutTable, splitValues } from './view-card.model';
import { ViewValueComponent } from './view-value.component';

/**
 * A view capability's card: a named slice of the inventory, nothing to run. Each collection
 * gets the layout its content needs (see {@link layoutTable}); plain values read as
 * headline stats, or as blocks when they hold lists or maps.
 */
@Component({
  selector: 'lodge-view-card',
  standalone: true,
  templateUrl: './view-card.component.html',
  styleUrls: ['./view-card.component.scss'],
  imports: [MatIconModule, ViewValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ViewCardComponent {
  readonly view = input.required<CapabilityViewModel>();

  readonly tables = computed(() =>
    this.view().tables.map((table) => ({ table, layout: layoutTable(table) })),
  );
  readonly values = computed(() => splitValues(this.view()));
}
