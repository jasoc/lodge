import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { ActivatedRouteSnapshot, provideRouter, withViewTransitions } from '@angular/router';

import { authInterceptor } from './interceptors/auth.interceptor';
import { LODGE_WIDGETS } from './modules/dashboards/widgets';
import { LODGE_WIDGETS_TOKEN } from './modules/dashboards/widgets/lodge-widgets.token';
import { routes } from './app.routes';

/** The deepest route that renders a component: the page itself, below which child routes
 * are only sections of it. */
function pageRoute(snapshot: ActivatedRouteSnapshot): ActivatedRouteSnapshot {
  let page = snapshot;
  for (let r: ActivatedRouteSnapshot | null = snapshot; r; r = r.firstChild) {
    if (r.routeConfig?.component || r.routeConfig?.loadComponent) {
      page = r;
    }
  }
  return page;
}

/** Same page, same parameters: only a section changed (an instance's tabs), which has its
 * own, local animation — sliding the whole page for it would move everything else too. */
function isSameDocument(from: ActivatedRouteSnapshot, to: ActivatedRouteSnapshot): boolean {
  const a = pageRoute(from);
  const b = pageRoute(to);
  const params = (r: ActivatedRouteSnapshot) =>
    JSON.stringify(r.pathFromRoot.map((route) => route.params));
  return a.routeConfig === b.routeConfig && params(a) === params(b);
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(
      routes,
      withViewTransitions({
        onViewTransitionCreated: ({ transition, from, to }) => {
          if (isSameDocument(from, to)) {
            transition.skipTransition();
            return;
          }
          (transition as any).types.add('count');
        },
      }),
    ),
    provideZonelessChangeDetection(),
    provideAnimationsAsync(),
    provideHttpClient(withFetch(), withInterceptors([authInterceptor])),
    { provide: LODGE_WIDGETS_TOKEN, useValue: LODGE_WIDGETS },
  ],
};
