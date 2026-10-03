import { GridStack, GridStackOptions } from 'gridstack';
import {
  GridstackComponent,
  GridstackModule,
  gsCreateNgComponents,
  NgGridStackOptions,
  NgGridStackWidget,
  nodesCB,
} from 'gridstack/dist/angular';

import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  inject,
  signal,
  TemplateRef,
  ViewContainerRef,
  viewChild,
  viewChildren,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute } from '@angular/router';

import { DynamicFormComponent } from '../../../../components/dynamic-form/dynamic-form.component';
import { DynamicFormRoot } from '../../../../components/dynamic-form/types/dynamic-form';
import { M3IconComponent } from '../../../../components/m3-icon/m3-icon.component';
import { M3TabComponent } from '../../../../components/m3-tabs/m3-tab/m3-tab.component';
import { M3TabsComponent } from '../../../../components/m3-tabs/m3-tabs.component';
import { DashboardModel } from '../../../../domain';
import { DashboardService } from '../../../../services/dashboard.service';
import { QuestionControlService } from '../../../../services/question-control.service';
import { BaseLodgeWidget } from '../../widgets/BaseLodgeWidget';

@Component({
  selector: 'lodge-dashboards-composer',
  standalone: true,
  templateUrl: './dashboards-composer.component.html',
  styleUrls: ['./dashboards-composer.component.scss'],
  imports: [
    NgTemplateOutlet,
    GridstackModule,
    MatButtonModule,
    MatIconModule,
    M3TabsComponent,
    M3TabComponent,
    M3IconComponent,
    DynamicFormComponent,
  ],
  providers: [DashboardService],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardsComposerComponent {
  private readonly gridComps = viewChildren(GridstackComponent);
  private readonly widgets = viewChildren(BaseLodgeWidget);

  private readonly currentOptionFormOutlet = viewChild('currentOptionFormOutlet', {
    read: ViewContainerRef,
  });
  private readonly currentOptionFormContent = viewChild('currentOptionFormContent', {
    read: TemplateRef,
  });

  readonly siderCollapsed = signal(false);
  readonly dashboard = signal<DashboardModel | undefined>(undefined);

  readonly gsWidgetGridBySelector: {
    [id: string]: GridstackComponent;
  } = {};
  readonly allWidgetsSelector: Array<string>;

  /**
   * Precomputed once, not called from the template: `[options]` on a `<gridstack>` element
   * re-initializes the grid whenever it sees a new object reference. Calling
   * `getSelectorGridOptions(selector)` directly from the template returned a fresh object
   * (with a fresh `children` array) on every change-detection pass, so each preview grid in
   * the sidebar kept re-adding its widget on every CD tick — visible as the sidebar
   * previews multiplying without end. A stable Map computed once keeps the same object
   * reference across renders, so the grid only (re)initializes when it actually should.
   */
  readonly widgetPreviewOptions = new Map<string, NgGridStackOptions>();

  readonly subOptions: NgGridStackOptions = {
    cellHeight: 50,
    column: 'auto',
    acceptWidgets: true,
  };

  readonly gridOptions: NgGridStackOptions = {
    cellHeight: 50,
    margin: 4,
    minRow: 2,
    acceptWidgets: true,
    subGridDynamic: false,
    subGridOpts: this.subOptions,
  };

  currentOptionForm?: DynamicFormRoot;
  currentOptionDefaultValues?: object;

  readonly dashboardService = inject(DashboardService);
  private readonly route = inject(ActivatedRoute);
  private readonly qcs = inject(QuestionControlService);

  constructor() {
    this.allWidgetsSelector = this.dashboardService.getAllWidgetsSelector();
    for (const selector of this.allWidgetsSelector) {
      this.widgetPreviewOptions.set(selector, this.getSelectorGridOptions(selector));
    }
    this.dashboardService.onWidgetClickInComposerCallback = (w) => this.onWidgetSelectedCallBack(w);

    GridStack.addRemoveCB = gsCreateNgComponents;

    afterNextRender(async () => {
      const id = this.route.snapshot.params['id'];
      if (id) {
        this.dashboard.set(await this.dashboardService.GetDashboard(id));
      }
      const db = this.dashboard();
      if (db && this.getMainGridComponent()) {
        GridStack.addGrid(this.getMainGridComponent()!.el, JSON.parse(db.json_grid!));
      }
    });
  }

  onGridChangeEvent(event: nodesCB) {
    this.saveDashboard();
  }

  onWidgetOptionChanges(widgetOptions: object) {
    if (BaseLodgeWidget.currentlyHighlighted) {
      BaseLodgeWidget.currentlyHighlighted.options = widgetOptions;
    }
  }

  getMainGridComponent(): GridstackComponent | undefined {
    const comps = this.gridComps();
    if (!comps || comps.length < 1) {
      return;
    }
    return comps[0];
  }

  getSelectorGridOptions(selector: string): NgGridStackOptions {
    const widget = this.dashboardService.getLodgeWidgetBySelector(selector);
    const minH = widget.minH ?? 1;
    const minW = widget.minW ?? 1;
    return {
      margin: 5,
      minRow: minH,
      column: minW,
      acceptWidgets: false,
      cellHeight: 40,
      children: [
        {
          w: minW,
          h: minH,
          noMove: true,
          noResize: true,
          selector,
        },
      ],
    };
  }

  addSubGridToDashboard() {
    if (!this.getMainGridComponent()?.el) return;
    this.getMainGridComponent()?.grid?.addWidget({
      h: 2,
      subGridOpts: this.gridOptions,
    } as NgGridStackWidget);
    this.getMainGridComponent()?.grid?.save();
  }

  addToDashboard(selector: string) {
    if (!this.getMainGridComponent()?.el) return;
    const widget = this.dashboardService.getLodgeWidgetBySelector(selector);
    this.getMainGridComponent()?.grid?.addWidget({
      h: widget.minH,
      w: widget.minW,
      selector,
    } as NgGridStackWidget);
    this.getMainGridComponent()?.grid?.save();
  }

  onWidgetSelectedCallBack(widget: BaseLodgeWidget<any>) {
    widget.highlight();
    const widgetDescriptor = this.dashboardService.getLodgeWidgetBySelector(widget.getSelector());
    this.currentOptionForm = widgetDescriptor.optionsForm;
    this.currentOptionDefaultValues = widget.options;
    const outlet = this.currentOptionFormOutlet();
    const content = this.currentOptionFormContent();
    if (outlet && content) {
      outlet.clear();
      outlet.createEmbeddedView(content);
    }
  }

  toggleSiderCollapse() {
    this.siderCollapsed.update((v) => !v);
  }

  saveDashboard() {
    const serializedData = this.getMainGridComponent()?.grid?.save(false, true) as GridStackOptions;
    const db = this.dashboard();
    if (db) {
      db.json_grid = JSON.stringify(serializedData);
      this.dashboardService.UpdateDashboard(db.id!, db);
    }
  }
}
