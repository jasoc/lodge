import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter, withViewTransitions } from '@angular/router';

import { authInterceptor } from './interceptors/auth.interceptor';
import { LODGE_WIDGETS } from './modules/dashboards/widgets';
import { LODGE_WIDGETS_TOKEN } from './modules/dashboards/widgets/lodge-widgets.token';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(
      routes,
      withViewTransitions({
        onViewTransitionCreated: ({ transition }) => {
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
