import { ChangeDetectionStrategy, Component } from '@angular/core';

import { LodgeWidget } from '../base-widget.decorator';
import { BaseLodgeWidget } from '../BaseLodgeWidget';

const selector = 'lodge-dashboards-widget-card';

@LodgeWidget({
  id: selector,
  name: 'Card',
  description: 'Card',
  minH: 1,
  minW: 1,
})
@Component({
  selector: selector,
  standalone: true,
  templateUrl: './dashboards-widget-card.component.html',
  styleUrls: ['./dashboards-widget-card.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardsWidgetCardComponent extends BaseLodgeWidget<any> {
  public override getSelector(): string {
    return selector;
  }
}
