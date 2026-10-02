import { Routes } from '@angular/router';

export const authRoutes: Routes = [
  {
    path: 'login',
    // TODO Fase 3: loadComponent(() => import('./login/login.component').then(m => m.LoginComponent))
    loadComponent: () =>
      import('../dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
];
