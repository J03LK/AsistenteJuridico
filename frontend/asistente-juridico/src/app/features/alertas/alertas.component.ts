import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { AlertasService } from '../../core/services/alertas.service';
import { AlertaProcesalDto } from '../../core/models/fase5.models';

@Component({
  selector: 'app-alertas',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule],
  template: `
    <div class="alertas-wrapper">
      <header class="alertas-header">
        <div>
          <h1 class="page-title">Centro de Alertas Procesales</h1>
          <p class="subtitle">Notificaciones operativas, plazos perentorios y eventos judiciales</p>
        </div>
        <div class="header-actions">
          <button (click)="marcarTodasComoLeidas()" class="btn btn-secondary">
            ✓ Marcar todas leídas
          </button>
          <a routerLink="/dashboard" class="btn btn-primary">
            📊 Ir al Dashboard
          </a>
        </div>
      </header>

      <!-- FILTERS -->
      <section class="filters-card">
        <label class="checkbox-label">
          <input type="checkbox" [(ngModel)]="soloNoLeidas" (change)="cargarAlertas()" />
          Solo no leídas
        </label>
        <label class="checkbox-label">
          <input type="checkbox" [(ngModel)]="incluirResueltas" (change)="cargarAlertas()" />
          Incluir resueltas / descartadas
        </label>
      </section>

      <!-- ERROR / MESSAGE -->
      <div *ngIf="mensajeExito()" class="alert alert-success">
        {{ mensajeExito() }}
      </div>

      <div *ngIf="error()" class="alert alert-error">
        {{ error() }}
      </div>

      <!-- LOADING -->
      <div *ngIf="cargando()" class="loading-state">
        <div class="spinner"></div>
        <p>Cargando alertas procesales...</p>
      </div>

      <!-- ALERTS LIST -->
      <div *ngIf="!cargando()" class="alerts-container">
        <div *ngIf="alertas().length === 0" class="empty-state">
          No hay alertas que coincidan con los filtros seleccionados.
        </div>

        <div *ngFor="let alerta of alertas()" class="alert-card" [ngClass]="getCardClass(alerta)">
          <div class="alert-left-bar"></div>
          <div class="alert-body">
            <div class="alert-top">
              <div class="alert-badges">
                <span class="badge" [ngClass]="getSeveridadBadgeClass(alerta.severidad)">
                  {{ getSeveridadNombre(alerta.severidad) }}
                </span>
                <span *ngIf="!alerta.usuarioId" class="badge badge-institucional">
                  🏛️ Estudio
                </span>
                <span *ngIf="!alerta.leida" class="badge badge-unread">
                  NUEVA
                </span>
                <span *ngIf="alerta.estadoResolucion !== 1" class="badge badge-resolved">
                  {{ getEstadoResolucionNombre(alerta.estadoResolucion) }}
                </span>
              </div>
              <span class="alert-time">{{ alerta.fechaDisparoUtc | date:'dd/MM/yyyy HH:mm' }}</span>
            </div>

            <h3 class="alert-title">{{ alerta.titulo }}</h3>
            <p class="alert-msg">{{ alerta.mensaje }}</p>

            <div class="alert-meta">
              <span *ngIf="alerta.expedienteNumero">📁 Expediente: <strong>{{ alerta.expedienteNumero }}</strong></span>
              <span *ngIf="alerta.usuarioNombre">👤 Destinatario: <strong>{{ alerta.usuarioNombre }}</strong></span>
              <span *ngIf="alerta.fechaObjetivoUtc">🎯 Fecha Límite: <strong>{{ alerta.fechaObjetivoUtc | date:'dd/MM/yyyy HH:mm' }}</strong></span>
            </div>

            <div *ngIf="alerta.motivoResolucion" class="resolution-info">
              💡 <strong>Resolución:</strong> {{ alerta.motivoResolucion }} ({{ alerta.resueltaUtc | date:'dd/MM/yyyy HH:mm' }})
            </div>
          </div>

          <div class="alert-actions">
            <button
              *ngIf="!alerta.leida"
              (click)="marcarLeida(alerta)"
              class="btn-icon"
              title="Marcar como leída">
              👁️ Leída
            </button>
            <button
              *ngIf="alerta.estadoResolucion === 1"
              (click)="descartar(alerta)"
              class="btn-icon btn-discard"
              title="Descartar alerta">
              ✕ Descartar
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .alertas-wrapper {
      padding: 24px;
      max-width: 1100px;
      margin: 0 auto;
      font-family: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      color: #1e293b;
    }
    .alertas-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 24px;
    }
    .page-title {
      font-size: 1.8rem;
      font-weight: 700;
      color: #0f172a;
      margin: 0 0 6px;
    }
    .subtitle {
      color: #64748b;
      margin: 0;
      font-size: 0.95rem;
    }
    .header-actions {
      display: flex;
      gap: 12px;
    }
    .btn {
      padding: 9px 16px;
      border-radius: 8px;
      font-size: 0.9rem;
      font-weight: 600;
      cursor: pointer;
      text-decoration: none;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      border: 1px solid transparent;
      transition: all 0.2s;
    }
    .btn-primary { background: #4f46e5; color: #fff; }
    .btn-primary:hover { background: #4338ca; }
    .btn-secondary { background: #ffffff; color: #334155; border-color: #cbd5e1; }
    .btn-secondary:hover { background: #f8fafc; }
    .filters-card {
      background: #ffffff;
      border-radius: 10px;
      border: 1px solid #e2e8f0;
      padding: 14px 20px;
      display: flex;
      gap: 24px;
      margin-bottom: 24px;
    }
    .checkbox-label {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 0.9rem;
      color: #475569;
      cursor: pointer;
      font-weight: 500;
    }
    .alerts-container {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .alert-card {
      background: #ffffff;
      border-radius: 10px;
      border: 1px solid #e2e8f0;
      display: flex;
      overflow: hidden;
      box-shadow: 0 1px 3px rgba(0,0,0,0.03);
      transition: all 0.2s;
    }
    .alert-card:hover {
      box-shadow: 0 4px 6px -1px rgba(0,0,0,0.08);
    }
    .alert-card-unread {
      background: #fafafa;
    }
    .alert-left-bar {
      width: 6px;
      flex-shrink: 0;
    }
    .bar-critica { background: #ef4444; }
    .bar-alta { background: #f97316; }
    .bar-media { background: #f59e0b; }
    .bar-baja { background: #3b82f6; }
    .alert-body {
      padding: 16px 20px;
      flex: 1;
    }
    .alert-top {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 8px;
    }
    .alert-badges {
      display: flex;
      gap: 8px;
      align-items: center;
    }
    .badge {
      padding: 2px 8px;
      border-radius: 4px;
      font-size: 0.75rem;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.03em;
    }
    .badge-critica { background: #fee2e2; color: #991b1b; }
    .badge-alta { background: #ffedd5; color: #9a3412; }
    .badge-media { background: #fef9c3; color: #854d0e; }
    .badge-baja { background: #dbeafe; color: #1e40af; }
    .badge-institucional { background: #f3e8ff; color: #6b21a8; }
    .badge-unread { background: #dcfce7; color: #166534; }
    .badge-resolved { background: #f1f5f9; color: #64748b; }
    .alert-time {
      font-size: 0.75rem;
      color: #94a3b8;
    }
    .alert-title {
      font-size: 1.05rem;
      font-weight: 600;
      color: #0f172a;
      margin: 0 0 6px;
    }
    .alert-msg {
      font-size: 0.9rem;
      color: #334155;
      margin: 0 0 10px;
      line-height: 1.4;
    }
    .alert-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 16px;
      font-size: 0.8rem;
      color: #64748b;
    }
    .resolution-info {
      margin-top: 8px;
      padding: 6px 10px;
      background: #f8fafc;
      border-radius: 6px;
      font-size: 0.8rem;
      color: #475569;
      border-left: 3px solid #cbd5e1;
    }
    .alert-actions {
      display: flex;
      flex-direction: column;
      justify-content: center;
      gap: 8px;
      padding: 16px;
      border-left: 1px solid #f1f5f9;
      background: #fcfcfd;
    }
    .btn-icon {
      padding: 6px 12px;
      border-radius: 6px;
      border: 1px solid #cbd5e1;
      background: #ffffff;
      color: #334155;
      font-size: 0.8rem;
      font-weight: 500;
      cursor: pointer;
      transition: all 0.2s;
      white-space: nowrap;
    }
    .btn-icon:hover {
      background: #f1f5f9;
    }
    .btn-discard {
      color: #b91c1c;
      border-color: #fecaca;
    }
    .btn-discard:hover {
      background: #fef2f2;
    }
    .empty-state {
      padding: 40px;
      text-align: center;
      color: #94a3b8;
      background: #ffffff;
      border-radius: 10px;
      border: 1px solid #e2e8f0;
    }
    .loading-state {
      text-align: center;
      padding: 40px;
      color: #64748b;
    }
    .spinner {
      width: 32px;
      height: 32px;
      border: 3px solid #e2e8f0;
      border-top-color: #4f46e5;
      border-radius: 50%;
      animation: spin 0.8s linear infinite;
      margin: 0 auto 10px;
    }
    @keyframes spin {
      to { transform: rotate(360deg); }
    }
    .alert-success {
      background: #f0fdf4;
      border: 1px solid #bbf7d0;
      color: #166534;
      padding: 12px 16px;
      border-radius: 8px;
      margin-bottom: 16px;
      font-size: 0.9rem;
    }
    .alert-error {
      background: #fef2f2;
      border: 1px solid #fee2e2;
      color: #b91c1c;
      padding: 12px 16px;
      border-radius: 8px;
      margin-bottom: 16px;
      font-size: 0.9rem;
    }
  `]
})
export class AlertasComponent implements OnInit {
  private readonly alertasService = inject(AlertasService);

  alertas = signal<AlertaProcesalDto[]>([]);
  cargando = signal<boolean>(false);
  error = signal<string | null>(null);
  mensajeExito = signal<string | null>(null);

  soloNoLeidas = true;
  incluirResueltas = false;

  ngOnInit(): void {
    this.cargarAlertas();
  }

  cargarAlertas(): void {
    this.cargando.set(true);
    this.error.set(null);

    this.alertasService.getAlertas({
      soloNoLeidas: this.soloNoLeidas,
      incluirResueltas: this.incluirResueltas,
      pageSize: 100
    }).subscribe({
      next: (res) => {
        if (res.success) {
          this.alertas.set(res.data);
        } else {
          this.error.set(res.message || 'Error cargando alertas.');
        }
        this.cargando.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.message || 'Error consultando alertas.');
        this.cargando.set(false);
      }
    });
  }

  marcarLeida(alerta: AlertaProcesalDto): void {
    this.alertasService.marcarLeida(alerta.id, alerta.version).subscribe({
      next: (res) => {
        if (res.success) {
          this.cargarAlertas();
        }
      },
      error: (err) => {
        this.error.set(err?.error?.message || 'Conflicto al actualizar alerta.');
      }
    });
  }

  descartar(alerta: AlertaProcesalDto): void {
    const motivo = prompt('Ingrese el motivo del descarte:');
    if (motivo === null) return;

    this.alertasService.descartar(alerta.id, motivo, alerta.version).subscribe({
      next: (res) => {
        if (res.success) {
          this.cargarAlertas();
        }
      },
      error: (err) => {
        this.error.set(err?.error?.message || 'Conflicto al descartar alerta.');
      }
    });
  }

  marcarTodasComoLeidas(): void {
    this.alertasService.marcarTodasLeidas(true).subscribe({
      next: (res) => {
        if (res.success) {
          this.mensajeExito.set(`Se marcaron ${res.data.totalAfectadas} alertas como leídas.`);
          setTimeout(() => this.mensajeExito.set(null), 3000);
          this.cargarAlertas();
        }
      },
      error: (err) => {
        this.error.set(err?.error?.message || 'Error al marcar todas como leídas.');
      }
    });
  }

  getCardClass(a: AlertaProcesalDto): string {
    return (!a.leida ? 'alert-card-unread ' : '') + this.getBarClass(a.severidad);
  }

  getBarClass(sev: number): string {
    switch (sev) {
      case 4: return 'bar-critica';
      case 3: return 'bar-alta';
      case 2: return 'bar-media';
      default: return 'bar-baja';
    }
  }

  getSeveridadBadgeClass(sev: number): string {
    switch (sev) {
      case 4: return 'badge-critica';
      case 3: return 'badge-alta';
      case 2: return 'badge-media';
      default: return 'badge-baja';
    }
  }

  getSeveridadNombre(sev: number): string {
    switch (sev) {
      case 4: return 'Crítica';
      case 3: return 'Alta';
      case 2: return 'Media';
      default: return 'Baja';
    }
  }

  getEstadoResolucionNombre(est: number): string {
    switch (est) {
      case 2: return 'Resuelta Automática';
      case 3: return 'Invalidada (Reprogramación)';
      case 4: return 'Descartada Manualmente';
      default: return 'Activa';
    }
  }
}
