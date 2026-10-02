import { Routes } from '@angular/router';
import { AIChatComponent } from './ai-chat.component';
import { AIConsumoComponent } from './ai-consumo.component';

export const iaRoutes: Routes = [
  {
    path: '',
    component: AIChatComponent,
  },
  {
    path: 'consumo',
    component: AIConsumoComponent,
  },
];
