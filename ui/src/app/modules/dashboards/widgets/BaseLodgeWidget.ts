import { BaseWidget, NgCompInputs, NgGridStackWidget } from 'gridstack/dist/angular';

import { Component, ElementRef, inject, Renderer2 } from '@angular/core';

import { DashboardService } from '../../../services/dashboard.service';

@Component({
  selector: 'lodge-dashboards-widget-base',
  standalone: true,
  template: '',
  host: {
    '(mousedown)': 'handleClick($event)',
  },
})
export abstract class BaseLodgeWidget<OptionsType> extends BaseWidget {
  static currentlyHighlighted: BaseLodgeWidget<any> | null = null;

  public options: OptionsType;

  readonly elRef = inject(ElementRef);
  readonly renderer = inject(Renderer2);
  readonly dashboardService = inject(DashboardService);

  constructor() {
    super();
    this.options = this.dashboardService.getInitialOptions(this.getSelector()) as OptionsType;
  }

  handleClick(_: MouseEvent): void {
    if (this.dashboardService.onWidgetClickInComposerCallback) {
      this.dashboardService.onWidgetClickInComposerCallback(this);
    }
  }

  highlight() {
    if (BaseLodgeWidget.currentlyHighlighted) {
      BaseLodgeWidget.currentlyHighlighted.removeHighlight();
    }
    BaseLodgeWidget.currentlyHighlighted = this;
    this.renderer.addClass(this.elRef.nativeElement, 'highlighted');
  }

  removeHighlight() {
    this.renderer.removeClass(this.elRef.nativeElement, 'highlighted');
  }

  override serialize(): NgCompInputs | undefined {
    return this.options as NgCompInputs;
  }

  override deserialize(w: NgGridStackWidget): void {
    if (w.input) {
      this.options = w.input as OptionsType;
    }
  }

  public abstract getSelector(): string;
}
