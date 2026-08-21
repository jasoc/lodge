import { BaseWidget, GridstackComponent } from 'gridstack/dist/angular';

import { inject, Injectable, reflectComponentType, Type } from '@angular/core';
import { FormGroup } from '@angular/forms';

import { LodgeWidgetInfo, DashboardModel, DashboardUpdateModel, WidgetMetadata } from '../domain';
import { BaseLodgeWidget } from '../modules/dashboards/widgets/BaseLodgeWidget';
import { BackendService } from './backend.service';
import { QuestionControlService } from './question-control.service';

/**
 * The dashboard/gridstack engine ported structurally from clip — composer, viewer, and
 * widget self-registration all work the same way — but it isn't wired to any Lodge
 * backend yet (there is no `/dashboards` persistence API on the server today). Plugging
 * it into the reconciliation data (capability/action status per instance) is future work;
 * for now these calls are reachable in the UI but will 404 until that backend exists.
 */
@Injectable({
  providedIn: 'root',
})
export class DashboardService extends BackendService {
  public onWidgetClickInComposerCallback?: (component: BaseLodgeWidget<any>) => void;

  private static lodgeWidgetsMapBySelector: {
    [id: string]: LodgeWidgetInfo;
  } = {};

  private readonly qcs = inject(QuestionControlService);

  getAllWidgetsSelector(): Array<string> {
    return Object.keys(GridstackComponent.selectorToType)
      .map((x) => reflectComponentType(GridstackComponent.selectorToType[x])?.selector)
      .filter((x) => x != undefined) as Array<string>;
  }

  static InitiateLodgeWidget(widgetType: Type<BaseWidget>, metadata: WidgetMetadata) {
    DashboardService.lodgeWidgetsMapBySelector[metadata.id] = {
      widgetType,
      metadata,
    };
  }

  getLodgeWidgetBySelector(selector: string): LodgeWidgetInfo {
    return DashboardService.lodgeWidgetsMapBySelector[selector];
  }

  /** A widget's default options, derived from its own `optionsForm` — moved here from
   * `QuestionControlService` so that service stays a generic dynamic-form helper with no
   * dashboard-domain knowledge. */
  getInitialOptions(selector: string): unknown {
    const widget = this.getLodgeWidgetBySelector(selector);
    if (!widget.metadata.optionsForm) {
      return null;
    }
    const form: FormGroup = this.qcs.toFormGroup(widget.metadata.optionsForm);
    return form.getRawValue();
  }

  async CreateDashboard(dashboard: DashboardModel): Promise<DashboardModel> {
    const res = await this.post<DashboardModel>('/dashboards', dashboard);
    return res.body!;
  }

  async GetDashboards(skip: number = 0, limit: number = 100): Promise<DashboardModel[]> {
    const res = await this.get<DashboardModel[]>('/dashboards', {
      skip,
      limit,
    });
    return res.body!;
  }

  async GetDashboard(dashboardId: string): Promise<DashboardModel> {
    const res = await this.get<DashboardModel>('/dashboards/' + dashboardId);
    return res.body!;
  }

  async UpdateDashboard(dashboardId: string, dashboard: DashboardModel): Promise<DashboardModel> {
    const res = await this.put<DashboardModel>('/dashboards/' + dashboardId, dashboard);
    return res.body!;
  }

  async DeleteDashboard(dashboardId: string): Promise<void> {
    await this.delete('/dashboards/' + dashboardId);
  }
}
