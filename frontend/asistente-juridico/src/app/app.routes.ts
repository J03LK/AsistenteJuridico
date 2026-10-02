import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    redirectTo: '/dashboard',
    pathMatch: 'full',
  },
  {
    path: 'auth',
    loadChildren: () =>
      import('./features/auth/auth.routes').then((m) => m.authRoutes),
  },
  {
    path: 'dashboard',
    loadChildren: () =>
      import('./features/dashboard/dashboard.routes').then((m) => m.dashboardRoutes),
  },
  {
    path: 'agenda',
    loadChildren: () =>
      import('./features/agenda/agenda.routes').then((m) => m.agendaRoutes),
  },
  {
    path: 'alertas',
    loadChildren: () =>
      import('./features/alertas/alertas.routes').then((m) => m.alertasRoutes),
  },
  {
    path: 'ia',
    loadChildren: () =>
      import('./features/ia/ia.routes').then((m) => m.iaRoutes),
  },
  {
    path: '**',
    redirectTo: '/dashboard',
  },
];
