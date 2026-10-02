import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AIService } from '../../core/services/ai.service';
import { AIConsumoResponseDto } from '../../core/models/ai.models';

@Component({
  selector: 'app-ai-consumo',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="consumo-container">
      <div class="header">
        <h2>Métricas de Consumo y Auditoría IA</h2>
        <p class="subtitle">Monitoreo deontológico de invocaciones, uso de tokens y estimación de costos (máx. 90 días).</p>
      </div>

      <div class="filter-card">
        <div class="form-group">
          <label>Fecha Inicio:</label>
          <input type="date" [(ngModel)]="fechaInicio" />
        </div>
        <div class="form-group">
          <label>Fecha Fin:</label>
          <input type="date" [(ngModel)]="fechaFin" />
        </div>
        <button class="btn-consultar" (click)="consultarConsumo()" [disabled]="cargando()">
          {{ cargando() ? 'Consultando...' : 'Consultar' }}
        </button>
      </div>

      <div *ngIf="errorMensaje()" class="error-banner">
        {{ errorMensaje() }}
      </div>

      <div *ngIf="datosConsumo()" class="metrics-grid">
        <div class="metric-card">
          <div class="metric-title">Total Invocaciones</div>
          <div class="metric-value">{{ datosConsumo()!.totalInvocaciones }}</div>
        </div>
        <div class="metric-card">
          <div class="metric-title">Tokens Entrada</div>
          <div class="metric-value">{{ datosConsumo()!.totalTokensEntrada | number }}</div>
        </div>
        <div class="metric-card">
          <div class="metric-title">Tokens Salida</div>
          <div class="metric-value">{{ datosConsumo()!.totalTokensSalida | number }}</div>
        </div>
        <div class="metric-card">
          <div class="metric-title">Costo Estimado</div>
          <div class="metric-value">\${{ datosConsumo()!.costoEstimadoUsdTotal | number:'1.2-4' }}</div>
        </div>
      </div>

      <div *ngIf="datosConsumo() && datosConsumo()!.desglosePorUsuario.length > 0" class="table-section">
        <h3>Consumo por Usuario</h3>
        <table class="data-table">
          <thead>
            <tr>
              <th>Usuario</th>
              <th>Invocaciones</th>
              <th>Total Tokens</th>
              <th>Costo Estimado (USD)</th>
            </tr>
          </thead>
          <tbody>
            <tr *ngFor="let u of datosConsumo()!.desglosePorUsuario">
              <td>{{ u.nombreUsuario }}</td>
              <td>{{ u.invocaciones }}</td>
              <td>{{ u.totalTokens | number }}</td>
              <td>\${{ u.costoEstimadoUsd | number:'1.2-4' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: [`
    .consumo-container {
      padding: 24px;
      max-width: 1000px;
      margin: 0 auto;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    }
    .header h2 { margin: 0 0 4px 0; color: #0f172a; }
    .subtitle { color: #64748b; margin: 0 0 20px 0; }
    .filter-card {
      display: flex;
      gap: 16px;
      align-items: flex-end;
      background: #fff;
      padding: 16px 20px;
      border-radius: 8px;
      border: 1px solid #e2e8f0;
      margin-bottom: 24px;
    }
    .form-group {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .form-group label {
      font-size: 0.85rem;
      font-weight: 500;
      color: #334155;
    }
    input[type="date"] {
      padding: 8px 12px;
      border: 1px solid #cbd5e1;
      border-radius: 6px;
    }
    .btn-consultar {
      padding: 9px 18px;
      background: #2563eb;
      color: white;
      border: none;
      border-radius: 6px;
      cursor: pointer;
      font-weight: 500;
    }
    .btn-consultar:hover { background: #1d4ed8; }
    .error-banner {
      background: #fee2e2;
      border: 1px solid #fca5a5;
      color: #b91c1c;
      padding: 12px 16px;
      border-radius: 6px;
      margin-bottom: 20px;
    }
    .metrics-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 16px;
      margin-bottom: 28px;
    }
    .metric-card {
      background: #ffffff;
      padding: 18px;
      border-radius: 8px;
      border: 1px solid #e2e8f0;
    }
    .metric-title { font-size: 0.85rem; color: #64748b; margin-bottom: 8px; }
    .metric-value { font-size: 1.5rem; font-weight: 700; color: #0f172a; }
    .table-section {
      background: #fff;
      padding: 20px;
      border-radius: 8px;
      border: 1px solid #e2e8f0;
    }
    .table-section h3 { margin: 0 0 16px 0; color: #1e293b; font-size: 1.1rem; }
    .data-table {
      width: 100%;
      border-collapse: collapse;
    }
    .data-table th, .data-table td {
      padding: 10px 14px;
      text-align: left;
      border-bottom: 1px solid #e2e8f0;
    }
    .data-table th { background: #f8fafc; font-weight: 600; color: #475569; }
  `]
})
export class AIConsumoComponent implements OnInit {
  private readonly aiService = inject(AIService);

  fechaInicio = '';
  fechaFin = '';
  readonly cargando = signal<boolean>(false);
  readonly errorMensaje = signal<string>('');
  readonly datosConsumo = signal<AIConsumoResponseDto | null>(null);

  ngOnInit(): void {
    const hoy = new Date();
    const hace30Dias = new Date();
    hace30Dias.setDate(hoy.getDate() - 30);

    this.fechaInicio = hace30Dias.toISOString().substring(0, 10);
    this.fechaFin = hoy.toISOString().substring(0, 10);

    this.consultarConsumo();
  }

  consultarConsumo(): void {
    this.errorMensaje.set('');

    const inicio = new Date(this.fechaInicio);
    const fin = new Date(this.fechaFin);

    if (fin < inicio) {
      this.errorMensaje.set('La fecha de fin debe ser posterior a la fecha de inicio.');
      return;
    }

    const diffDias = Math.ceil((fin.getTime() - inicio.getTime()) / (1000 * 3600 * 24));
    if (diffDias > 90) {
      this.errorMensaje.set('El rango de consulta no puede exceder los 90 días.');
      return;
    }

    this.cargando.set(true);
    this.aiService.getConsumo(inicio.toISOString(), fin.toISOString()).subscribe({
      next: (res) => {
        if (res.success && res.data) {
          this.datosConsumo.set(res.data);
        }
        this.cargando.set(false);
      },
      error: (err) => {
        this.errorMensaje.set(err?.error?.message || 'Error al consultar métricas de consumo.');
        this.cargando.set(false);
      }
    });
  }
}
