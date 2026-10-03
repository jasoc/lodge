import { isPlatformBrowser } from '@angular/common';
import { inject, Injectable, PLATFORM_ID } from '@angular/core';
import { FormGroup } from '@angular/forms';

import { DashboardModel, WidgetDescriptor } from '../domain';
import type { BaseLodgeWidget } from '../modules/dashboards/widgets/BaseLodgeWidget';
import { LODGE_WIDGETS_TOKEN } from '../modules/dashboards/widgets/lodge-widgets.token';
import { QuestionControlService } from './question-control.service';

const STORAGE_KEY = 'lodge.dashboards';

/**
 * The dashboard/GridStack engine — composer and viewer work the same way they always
 * have. What changed: widget types are one static array (`LODGE_WIDGETS`, provided via
 * `LODGE_WIDGETS_TOKEN` — see that file for why not a direct import) instead of a
 * decorator that mutated a static map as a side effect of import order, and dashboards
 * persist to `localStorage` — same idea as `TokenStorageService` — instead of a
 * `/dashboards` REST API that never had a server-side implementation. This is a
 * single-operator homelab tool; a per-browser layout is enough.
 */
@Injectable({
  providedIn: 'root',
})
export class DashboardService {
  public onWidgetClickInComposerCallback?: (component: BaseLodgeWidget<any>) => void;

  private readonly platformId = inject(PLATFORM_ID);
  private readonly qcs = inject(QuestionControlService);
  private readonly widgets = inject(LODGE_WIDGETS_TOKEN);
  private inMemoryDashboards: DashboardModel[] | null = null;

  getAllWidgetsSelector(): string[] {
    return this.widgets.map((w) => w.selector);
  }

  getLodgeWidgetBySelector(selector: string): WidgetDescriptor {
    return this.widgets.find((w) => w.selector === selector)!;
  }

  /** A widget's default options, derived from its own `optionsForm`. */
  getInitialOptions(selector: string): unknown {
    const widget = this.getLodgeWidgetBySelector(selector);
    if (!widget.optionsForm) {
      return null;
    }
    const form: FormGroup = this.qcs.toFormGroup(widget.optionsForm);
    return form.getRawValue();
  }

  async CreateDashboard(dashboard: DashboardModel): Promise<DashboardModel> {
    const created: DashboardModel = { ...dashboard, id: crypto.randomUUID() };
    const all = this.readAll();
    all.push(created);
    this.writeAll(all);
    return created;
  }

  async GetDashboards(): Promise<DashboardModel[]> {
    return this.readAll();
  }

  async GetDashboard(dashboardId: string): Promise<DashboardModel> {
    const found = this.readAll().find((d) => d.id === dashboardId);
    if (!found) {
      throw new Error(`Dashboard '${dashboardId}' not found.`);
    }
    return found;
  }

  async UpdateDashboard(dashboardId: string, dashboard: DashboardModel): Promise<DashboardModel> {
    const all = this.readAll();
    const index = all.findIndex((d) => d.id === dashboardId);
    const updated: DashboardModel = { ...dashboard, id: dashboardId };
    if (index === -1) {
      all.push(updated);
    } else {
      all[index] = updated;
    }
    this.writeAll(all);
    return updated;
  }

  async DeleteDashboard(dashboardId: string): Promise<void> {
    this.writeAll(this.readAll().filter((d) => d.id !== dashboardId));
  }

  private readAll(): DashboardModel[] {
    if (!isPlatformBrowser(this.platformId)) {
      return this.inMemoryDashboards ?? [];
    }
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as DashboardModel[]) : [];
  }

  private writeAll(dashboards: DashboardModel[]): void {
    if (!isPlatformBrowser(this.platformId)) {
      this.inMemoryDashboards = dashboards;
      return;
    }
    localStorage.setItem(STORAGE_KEY, JSON.stringify(dashboards));
  }
}
