import { Type } from '@angular/core';
import { BaseWidget } from 'gridstack/dist/angular';

import { DynamicFormRoot } from '../components/dynamic-form/types/dynamic-form';

export interface DashboardModel {
  id?: string;
  name?: string;
  json_grid?: string;
  user_id?: string;
}

export interface DashboardUpdateModel {
  name?: string;
  json_grid?: string;
}

/** Static descriptor for one widget type — the single source of truth both GridStack's own
 * selector registry (main.ts) and the composer's widget palette read from. */
export interface WidgetDescriptor {
  selector: string;
  name: string;
  description: string;
  component: Type<BaseWidget>;
  icon?: string;
  minH?: number;
  minW?: number;
  optionsForm?: DynamicFormRoot;
}

export interface KindSummaryOptions {
  kind_code: string;
}

export interface InstanceStatusOptions {
  kind_code: string;
  instance_code: string;
}

export interface SignalValueOptions {
  kind_code: string;
  instance_code: string;
  signal_path: string;
}
