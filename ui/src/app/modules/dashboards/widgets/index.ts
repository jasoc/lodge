import { WidgetDescriptor } from '../../../domain';
import { TextboxElement } from '../../../components/dynamic-form/types/dynamic-form-element-textbox';
import { DashboardsWidgetInstanceStatusComponent } from './instance-status/dashboards-widget-instance-status.component';
import { DashboardsWidgetKindSummaryComponent } from './kind-summary/dashboards-widget-kind-summary.component';
import { DashboardsWidgetSignalValueComponent } from './signal-value/dashboards-widget-signal-value.component';

/** Every widget type the dashboard composer/viewer knows about — the single source of
 * truth for both the composer's widget palette and GridStack's own selector→Type registry
 * (see main.ts). Adding a widget means adding one entry here, nothing else. */
export const LODGE_WIDGETS: WidgetDescriptor[] = [
  {
    selector: 'lodge-dashboards-widget-kind-summary',
    name: 'Kind summary',
    description: 'Instance count and live drift count for a kind.',
    component: DashboardsWidgetKindSummaryComponent,
    icon: 'category',
    minH: 2,
    minW: 3,
    optionsForm: [
      new TextboxElement({
        key: 'kind_code',
        label: 'Kind code',
        icon: 'category',
        defaultValue: 'acme',
        required: true,
        order: 1,
      }),
    ],
  },
  {
    selector: 'lodge-dashboards-widget-instance-status',
    name: 'Instance status',
    description: 'Pending-action count and last-reconciled time for one instance.',
    component: DashboardsWidgetInstanceStatusComponent,
    icon: 'dns',
    minH: 2,
    minW: 3,
    optionsForm: [
      new TextboxElement({
        key: 'kind_code',
        label: 'Kind code',
        icon: 'category',
        defaultValue: 'acme',
        required: true,
        order: 1,
      }),
      new TextboxElement({
        key: 'instance_code',
        label: 'Instance code',
        icon: 'dns',
        defaultValue: 'demo',
        required: true,
        order: 2,
      }),
    ],
  },
  {
    selector: 'lodge-dashboards-widget-signal-value',
    name: 'Signal value',
    description: "One instance's current value for a dotted signal path.",
    component: DashboardsWidgetSignalValueComponent,
    icon: 'sensors',
    minH: 2,
    minW: 3,
    optionsForm: [
      new TextboxElement({
        key: 'kind_code',
        label: 'Kind code',
        icon: 'category',
        defaultValue: 'acme',
        required: true,
        order: 1,
      }),
      new TextboxElement({
        key: 'instance_code',
        label: 'Instance code',
        icon: 'dns',
        defaultValue: 'demo',
        required: true,
        order: 2,
      }),
      new TextboxElement({
        key: 'signal_path',
        label: 'Signal path',
        icon: 'sensors',
        defaultValue: 'services.web.enabled',
        required: true,
        order: 3,
      }),
    ],
  },
];
