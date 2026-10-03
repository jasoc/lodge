import { GridstackComponent } from 'gridstack/dist/angular';

import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';
import { LODGE_WIDGETS } from './app/modules/dashboards/widgets';

// GridStack needs an explicit selector -> component Type mapping to instantiate widgets
// dynamically from saved grid JSON; LODGE_WIDGETS is the single source of truth for it.
GridstackComponent.addComponentToSelectorType(LODGE_WIDGETS.map((w) => w.component));

bootstrapApplication(AppComponent, appConfig).catch((err) => console.error(err));
