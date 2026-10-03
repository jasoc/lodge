import { Routes } from '@angular/router';

import { dashboardResolver } from './modules/dashboards/dashboards.resolver';
import { PermissionsService } from './services/permissions.service';

export const routes: Routes = [
  // Pre-auth routes: reachable with no session, outside the guard below. Only ever
  // visited in the Oidc profile — NoAuth's guard mints a session silently and never
  // redirects here.
  {
    path: 'auth/login',
    loadComponent: () => import('./modules/auth/login.component').then((m) => m.LoginComponent),
  },
  {
    path: 'auth/callback',
    loadComponent: () =>
      import('./modules/auth/callback.component').then((m) => m.AuthCallbackComponent),
  },

  {
    path: '',
    canActivate: [PermissionsService.isUserLoggedFn],
    loadComponent: () => import('./shell/shell.component').then((m) => m.ShellComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'home' },

      // HOME
      {
        path: 'home',
        children: [
          {
            path: '',
            pathMatch: 'full',
            loadComponent: () =>
              import('./modules/home/home.component').then((m) => m.HomeMainComponent),
          },
          {
            path: 'about',
            loadComponent: () =>
              import('./modules/home/pages/about/home-about.component').then(
                (m) => m.HomeAboutComponent,
              ),
          },
        ],
      },

      // INSTANCES — three nested route-config levels (not one flat 'instances/:kind/:instance'
      // path) so each URL segment gets its own breadcrumb entry: Instances / acme / demo.
      {
        path: 'instances',
        children: [
          {
            path: ':kind',
            children: [
              {
                path: '',
                pathMatch: 'full',
                loadComponent: () =>
                  import('./modules/instances/instance-list.component').then(
                    (m) => m.InstanceListComponent,
                  ),
              },
              {
                path: ':instance',
                loadComponent: () =>
                  import('./modules/instances/instance-detail.component').then(
                    (m) => m.InstanceDetailComponent,
                  ),
              },
            ],
          },
        ],
      },

      // DRIFT — global filtered view over live (QUEUED) actions across every instance.
      {
        path: 'drift',
        loadComponent: () =>
          import('./modules/drift/drift.component').then((m) => m.DriftComponent),
      },

      // ACTIONS — global view over every action across every instance.
      {
        path: 'actions',
        loadComponent: () =>
          import('./modules/actions/actions.component').then((m) => m.ActionsComponent),
      },

      // AUDIT — global view over every audit event across every instance.
      {
        path: 'audit',
        loadComponent: () =>
          import('./modules/audit/audit.component').then((m) => m.AuditComponent),
      },

      // KINDS — read-only capability catalog browser (Phase 4).
      {
        path: 'kinds',
        children: [
          {
            path: '',
            pathMatch: 'full',
            loadComponent: () =>
              import('./modules/kinds/kinds.component').then((m) => m.KindsComponent),
          },
          {
            path: ':kind/capabilities',
            loadComponent: () =>
              import('./modules/kinds/capabilities/kind-capabilities.component').then(
                (m) => m.KindCapabilitiesComponent,
              ),
          },
        ],
      },

      // SETTINGS
      {
        path: 'settings',
        loadComponent: () =>
          import('./modules/settings/settings.component').then((m) => m.SettingsComponent),
      },

      // DASHBOARDS — structurally ported, not yet wired to reconciliation data
      {
        path: 'dashboards',
        children: [
          {
            path: '',
            pathMatch: 'full',
            loadComponent: () =>
              import('./modules/dashboards/dashboards.component').then(
                (m) => m.DashboardsComponent,
              ),
          },
          {
            path: ':id',
            resolve: { dashboard: dashboardResolver },
            loadComponent: () =>
              import('./modules/dashboards/pages/viewer/dashboards-viewer.component').then(
                (m) => m.DashboardsViewerComponent,
              ),
          },
          {
            path: 'composer',
            children: [
              {
                path: ':id',
                resolve: { dashboard: dashboardResolver },
                loadComponent: () =>
                  import('./modules/dashboards/pages/composer/dashboards-composer.component').then(
                    (m) => m.DashboardsComposerComponent,
                  ),
              },
            ],
          },
        ],
      },
    ],
  },
];
