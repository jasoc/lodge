import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';

import { ViewJson } from '../../../domain';
import { shapeOf, toFields, toRecords } from './view-card.model';

/** How deep maps and lists nest as cards before the rest reads as compact JSON. */
const MAX_DEPTH = 3;

/**
 * One view value, drawn for its shape (see {@link shapeOf}): a scalar inline, a list of
 * scalars as chips, objects as small cards or a key/value grid — recursively.
 */
@Component({
  selector: 'lodge-view-value',
  standalone: true,
  templateUrl: './view-value.component.html',
  styleUrls: ['./view-value.component.scss'],
  imports: [MatChipsModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ViewValueComponent {
  readonly value = input<ViewJson>(null);
  readonly depth = input(0);

  readonly shape = computed(() => {
    const shape = shapeOf(this.value());
    const nested = shape === 'records' || shape === 'fields' || shape === 'list';
    return nested && this.depth() >= MAX_DEPTH ? 'json' : shape;
  });
  readonly items = computed(() => {
    const value = this.value();
    return Array.isArray(value) ? value : [];
  });
  readonly records = computed(() => toRecords(this.value()));
  readonly fields = computed(() => {
    const value = this.value();
    return value !== null && typeof value === 'object' && !Array.isArray(value)
      ? toFields(value)
      : [];
  });
  readonly json = computed(() => JSON.stringify(this.value()));
  /** A link shown without its scheme — the icon already says it's a link. */
  readonly linkText = computed(() => String(this.value()).replace(/^https?:\/\//i, ''));
}
