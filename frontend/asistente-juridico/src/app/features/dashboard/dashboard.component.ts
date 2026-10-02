import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { DashboardService } from '../../core/services/dashboard.service';
import { DashboardResumenDto } from '../../core/models/fase5.models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterModule],
  template: `
    <div class="dashboard-wrapper">
      <!-- HEADER -->
      <header class="dashboard-header">
        <div>
          <h1 class="page-title">Panel Ejecutivo & Jurídico</h1>
          <p class="subtitle">Monitoreo operacional, métricas de cumplimiento y alertas procesales</p>
        </div>
        <div class="header-actions">
          <a routerLink="/agenda" class="btn btn-secondary">
            📅 Ver Agenda
          </a>
          <a routerLink="/alertas" class="btn btn-primary">
            🔔 Alertas Procesales
          </a>
        </div>
      </header>

      <!-- SPINNER / ERROR -->
      <div *ngIf="cargando()" class="loading-state">
        <div class="spinner"></div>
        <p>Cargando métricas ejecutivas...</p>
      </div>

      <div *ngIf="error()" class="alert alert-error">
        {{ error() }}
      </div>

      <!-- MAIN CONTENT -->
      <div *ngIf="resumen() as data" class="dashboard-content">
        <!-- 1. KPIS GENERALES -->
        <section class="kpi-grid">
          <div class="kpi-card card-primary">
            <div class="kpi-header">
              <span class="kpi-title">Expedientes Activos</span>
              <span class="kpi-icon">📁</span>
            </div>
            <div class="kpi-value">{{ data.kpis.totalExpedientesActivos }}</div>
            <div class="kpi-meta">En trámite o abiertos en el estudio</div>
          </div>

          <div class="kpi-card card-warning">
            <div class="kpi-header">
              <span class="kpi-title">Tareas Pendientes</span>
              <span class="kpi-icon">⚡</span>
            </div>
            <div class="kpi-value">{{ data.kpis.totalTareasPendientes }}</div>
            <div class="kpi-meta">Pendientes o en progreso</div>
          </div>

          <div class="kpi-card card-info">
            <div class="kpi-header">
              <span class="kpi-title">Audiencias (7 días)</span>
              <span class="kpi-icon">⚖️</span>
            </div>
            <div class="kpi-value">{{ data.kpis.totalAudienciasProximas7Dias }}</div>
            <div class="kpi-meta">Programadas en la próxima semana</div>
          </div>

          <div class="kpi-card" [ngClass]="data.kpis.totalAlertasAltaCriticasSinResolver > 0 ? 'card-danger' : 'card-success'">
            <div class="kpi-header">
              <span class="kpi-title">Alertas Críticas</span>
              <span class="kpi-icon">🚨</span>
            </div>
            <div class="kpi-value">{{ data.kpis.totalAlertasAltaCriticasSinResolver }}</div>
            <div class="kpi-meta">Alta o Crítica sin resolver</div>
          </div>
        </section>

        <!-- 2. INACTIVIDAD & EFICIENCIA -->
        <section class="metrics-split">
          <!-- Inactividad Operativa -->
          <div class="card inactividad-card">
            <div class="card-header">
              <h3>⏱️ Inactividad Operativa Interna</h3>
              <span class="badge badge-subtle">Regla 30/60 días</span>
            </div>
            <p class="card-desc">Expedientes activos sin tareas, audiencias o documentos recientes:</p>
            <div class="inactividad-pills">
              <div class="inactivity-box warning-box">
                <span class="inactivity-count">{{ data.inactividad.expedientesInactivos30Dias }}</span>
                <span class="inactivity-label">Inactivos &gt; 30 días</span>
              </div>
              <div class="inactivity-box danger-box">
                <span class="inactivity-count">{{ data.inactividad.expedientesInactivos60Dias }}</span>
                <span class="inactivity-label">Inactivos &gt; 60 días (Crítico)</span>
              </div>
            </div>
          </div>

          <!-- Eficiencia Operativa -->
          <div class="card eficiencia-card">
            <div class="card-header">
              <h3>🎯 Eficiencia de Cumplimiento</h3>
              <span class="badge badge-subtle">Cierres a Tiempo</span>
            </div>
            <p class="card-desc">Desempeño sobre expedientes cerrados con plazo estimado:</p>
            <div class="eficiencia-stats">
              <div class="eficiencia-rate">
                <span *ngIf="data.eficiencia.porcentajeCumplimiento !== null; else sinPlazo" class="rate-number">
                  {{ data.eficiencia.porcentajeCumplimiento }}%
                </span>
                <ng-template #sinPlazo>
                  <span class="rate-null">N/A</span>
                </ng-template>
                <span class="rate-label">Cumplimiento dentro de plazo</span>
              </div>
              <div class="eficiencia-details">
                <div>Evaluados: <strong>{{ data.eficiencia.totalExpedientesCerradosEvaluados }}</strong></div>
                <div>A tiempo: <strong>{{ data.eficiencia.expedientesCerradosATiempo }}</strong></div>
                <div>Con plazo estimado: <strong>{{ data.eficiencia.expedientesConPlazoEstimado }}</strong></div>
              </div>
            </div>
          </div>
        </section>

        <!-- 3. DISTRIBUCIÓN & TAREAS -->
        <section class="grid-two">
          <div class="card">
            <div class="card-header">
              <h3>📊 Estado de Expedientes</h3>
            </div>
            <div class="state-distribution">
              <div class="state-row">
                <span>Abiertos</span>
                <strong>{{ data.distribucionExpedientes.abiertos }}</strong>
              </div>
              <div class="state-row">
                <span>En Trámite</span>
                <strong>{{ data.distribucionExpedientes.enTramite }}</strong>
              </div>
              <div class="state-row">
                <span>Suspendidos</span>
                <strong>{{ data.distribucionExpedientes.suspendidos }}</strong>
              </div>
              <div class="state-row">
                <span>Cerrados</span>
                <strong>{{ data.distribucionExpedientes.cerrados }}</strong>
              </div>
              <div class="state-row">
                <span>Archivados</span>
                <strong>{{ data.distribucionExpedientes.archivados }}</strong>
              </div>
            </div>
          </div>

          <div class="card">
            <div class="card-header">
              <h3>🔥 Tareas por Prioridad</h3>
            </div>
            <div class="priority-list">
              <div class="priority-item priority-urgent">
                <span>Urgente</span>
                <span class="badge-count">{{ data.tareasPendientesPorPrioridad.urgente }}</span>
              </div>
              <div class="priority-item priority-high">
                <span>Alta</span>
                <span class="badge-count">{{ data.tareasPendientesPorPrioridad.alta }}</span>
              </div>
              <div class="priority-item priority-medium">
                <span>Media</span>
                <span class="badge-count">{{ data.tareasPendientesPorPrioridad.media }}</span>
              </div>
              <div class="priority-item priority-low">
                <span>Baja</span>
                <span class="badge-count">{{ data.tareasPendientesPorPrioridad.baja }}</span>
              </div>
            </div>
          </div>
        </section>

        <!-- 4. PRÓXIMAS AUDIENCIAS -->
        <section class="card audiencias-list-card">
          <div class="card-header">
            <h3>⚖️ Próximas Audiencias Convocadas</h3>
            <a routerLink="/agenda" class="view-all-link">Ver en Agenda &rarr;</a>
          </div>
          <div *ngIf="data.proximasAudiencias.length === 0" class="empty-state">
            No hay audiencias programadas en los próximos días.
          </div>
          <div *ngIf="data.proximasAudiencias.length > 0" class="table-responsive">
            <table class="data-table">
              <thead>
                <tr>
                  <th>Fecha y Hora</th>
                  <th>Audiencia</th>
                  <th>Expediente</th>
                  <th>Sala / Virtual</th>
                  <th>Abogado</th>
                </tr>
              </thead>
              <tbody>
                <tr *ngFor="let aud of data.proximasAudiencias">
                  <td>{{ aud.fechaHora | date:'dd/MM/yyyy HH:mm' }}</td>
                  <td><strong>{{ aud.titulo }}</strong></td>
                  <td>{{ aud.expedienteNumero || 'N/A' }}</td>
                  <td><span class="location-badge">{{ aud.salaOVirtual }}</span></td>
                  <td>{{ aud.abogadoNombre || 'No asignado' }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </div>
  `,
  styles: [`
    .dashboard-wrapper {
      padding: 24px;
      max-width: 1200px;
      margin: 0 auto;
      font-family: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      color: #1e293b;
    }
    .dashboard-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 28px;
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
      padding: 10px 18px;
      border-radius: 8px;
      font-size: 0.9rem;
      font-weight: 600;
      text-decoration: none;
      transition: all 0.2s ease;
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .btn-primary {
      background: #4f46e5;
      color: #fff;
    }
    .btn-primary:hover {
      background: #4338ca;
    }
    .btn-secondary {
      background: #f1f5f9;
      color: #334155;
      border: 1px solid #cbd5e1;
    }
    .btn-secondary:hover {
      background: #e2e8f0;
    }
    .kpi-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
      gap: 18px;
      margin-bottom: 24px;
    }
    .kpi-card {
      background: #ffffff;
      border-radius: 12px;
      padding: 20px;
      border: 1px solid #e2e8f0;
      box-shadow: 0 1px 3px rgba(0,0,0,0.05);
      transition: transform 0.2s;
    }
    .kpi-card:hover {
      transform: translateY(-2px);
    }
    .kpi-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 12px;
    }
    .kpi-title {
      font-size: 0.85rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: #64748b;
    }
    .kpi-icon {
      font-size: 1.25rem;
    }
    .kpi-value {
      font-size: 2.2rem;
      font-weight: 700;
      color: #0f172a;
      line-height: 1;
      margin-bottom: 8px;
    }
    .kpi-meta {
      font-size: 0.8rem;
      color: #94a3b8;
    }
    .card-primary { border-top: 4px solid #4f46e5; }
    .card-warning { border-top: 4px solid #f59e0b; }
    .card-info { border-top: 4px solid #0ea5e9; }
    .card-danger { border-top: 4px solid #ef4444; }
    .card-success { border-top: 4px solid #10b981; }

    .metrics-split {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 18px;
      margin-bottom: 24px;
    }
    .card {
      background: #ffffff;
      border-radius: 12px;
      border: 1px solid #e2e8f0;
      padding: 20px;
      box-shadow: 0 1px 3px rgba(0,0,0,0.04);
    }
    .card-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 8px;
    }
    .card-header h3 {
      font-size: 1.1rem;
      font-weight: 600;
      margin: 0;
      color: #1e293b;
    }
    .card-desc {
      font-size: 0.85rem;
      color: #64748b;
      margin-bottom: 16px;
    }
    .badge-subtle {
      background: #f1f5f9;
      color: #475569;
      padding: 4px 8px;
      border-radius: 6px;
      font-size: 0.75rem;
      font-weight: 500;
    }
    .inactividad-pills {
      display: flex;
      gap: 12px;
    }
    .inactivity-box {
      flex: 1;
      border-radius: 8px;
      padding: 14px;
      display: flex;
      flex-direction: column;
      align-items: center;
    }
    .warning-box {
      background: #fffbeb;
      border: 1px solid #fef3c7;
      color: #b45309;
    }
    .danger-box {
      background: #fef2f2;
      border: 1px solid #fee2e2;
      color: #b91c1c;
    }
    .inactivity-count {
      font-size: 1.8rem;
      font-weight: 700;
    }
    .inactivity-label {
      font-size: 0.8rem;
      font-weight: 500;
    }
    .eficiencia-stats {
      display: flex;
      gap: 20px;
      align-items: center;
    }
    .eficiencia-rate {
      display: flex;
      flex-direction: column;
      align-items: center;
      padding: 12px 20px;
      background: #f8fafc;
      border-radius: 8px;
      border: 1px solid #e2e8f0;
      min-width: 140px;
    }
    .rate-number {
      font-size: 2rem;
      font-weight: 800;
      color: #10b981;
    }
    .rate-null {
      font-size: 1.4rem;
      font-weight: 700;
      color: #94a3b8;
    }
    .rate-label {
      font-size: 0.75rem;
      color: #64748b;
      text-align: center;
    }
    .eficiencia-details {
      display: flex;
      flex-direction: column;
      gap: 6px;
      font-size: 0.85rem;
      color: #475569;
    }
    .grid-two {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 18px;
      margin-bottom: 24px;
    }
    .state-row {
      display: flex;
      justify-content: space-between;
      padding: 8px 0;
      border-bottom: 1px solid #f1f5f9;
      font-size: 0.9rem;
    }
    .priority-list {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .priority-item {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 10px 14px;
      border-radius: 6px;
      font-size: 0.9rem;
      font-weight: 500;
    }
    .priority-urgent { background: #fee2e2; color: #991b1b; }
    .priority-high { background: #ffedd5; color: #9a3412; }
    .priority-medium { background: #fef9c3; color: #854d0e; }
    .priority-low { background: #f1f5f9; color: #475569; }
    .badge-count {
      font-weight: 700;
    }
    .data-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.85rem;
      margin-top: 10px;
    }
    .data-table th, .data-table td {
      padding: 10px 12px;
      text-align: left;
      border-bottom: 1px solid #f1f5f9;
    }
    .data-table th {
      color: #64748b;
      font-weight: 600;
      background: #f8fafc;
    }
    .location-badge {
      background: #e0e7ff;
      color: #3730a3;
      padding: 2px 8px;
      border-radius: 4px;
      font-size: 0.75rem;
    }
    .view-all-link {
      color: #4f46e5;
      text-decoration: none;
      font-size: 0.85rem;
      font-weight: 600;
    }
    .empty-state {
      padding: 24px;
      text-align: center;
      color: #94a3b8;
      font-size: 0.9rem;
    }
    .loading-state {
      text-align: center;
      padding: 40px;
      color: #64748b;
    }
    .spinner {
      width: 36px;
      height: 36px;
      border: 3px solid #e2e8f0;
      border-top-color: #4f46e5;
      border-radius: 50%;
      animation: spin 0.8s linear infinite;
      margin: 0 auto 12px;
    }
    @keyframes spin {
      to { transform: rotate(360deg); }
    }
    @media (max-width: 768px) {
      .metrics-split, .grid-two { grid-template-columns: 1fr; }
    }
  `]
})
export class DashboardComponent implements OnInit {
  private readonly dashboardService = inject(DashboardService);

  resumen = signal<DashboardResumenDto | null>(null);
  cargando = signal<boolean>(true);
  error = signal<string | null>(null);

  ngOnInit(): void {
    this.cargarDashboard();
  }

  cargarDashboard(): void {
    this.cargando.set(true);
    this.error.set(null);

    this.dashboardService.getResumen().subscribe({
      next: (res) => {
        if (res.success) {
          this.resumen.set(res.data);
        } else {
          this.error.set(res.message || 'Error cargando resumen del dashboard.');
        }
        this.cargando.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.message || 'Error de conexión con el servidor.');
        this.cargando.set(false);
      }
    });
  }
}
