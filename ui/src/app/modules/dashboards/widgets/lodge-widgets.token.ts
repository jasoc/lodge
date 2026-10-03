import { InjectionToken } from '@angular/core';

import { WidgetDescriptor } from '../../../domain';

/**
 * Separate from `widgets/index.ts` on purpose. That module imports the widget component
 * classes, which extend `BaseLodgeWidget`, which injects `DashboardService` — if
 * `DashboardService` imported the widget array directly, it would close an import cycle
 * back through the very class it's injected into: `class X extends BaseLodgeWidget` would
 * then evaluate before `BaseLodgeWidget`'s own module finished loading, surfacing at
 * runtime as "Class extends value undefined is not a constructor or null". Routing the
 * value through a DI token instead means `DashboardService` only needs this token file,
 * never the widget components themselves — the concrete array is provided once, at the
 * app root (see `app.config.ts`), which nothing downstream imports back into.
 */
export const LODGE_WIDGETS_TOKEN = new InjectionToken<WidgetDescriptor[]>('LODGE_WIDGETS');
