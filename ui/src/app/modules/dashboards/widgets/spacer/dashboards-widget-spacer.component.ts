import { ChangeDetectionStrategy, Component } from '@angular/core';

import { LodgeWidget } from '../base-widget.decorator';
import { BaseLodgeWidget } from '../BaseLodgeWidget';

const selector = 'lodge-dashboards-widget-spacer';

@LodgeWidget({
  id: selector,
  name: 'spacer',
  description: 'Card',
  minH: 2,
  minW: 4,
})
@Component({
  selector: selector,
  standalone: true,
  templateUrl: './dashboards-widget-spacer.component.html',
  styleUrls: ['./dashboards-widget-spacer.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardsWidgetSpacerComponent extends BaseLodgeWidget<any> {
  public override getSelector(): string {
    return selector;
  }
}
