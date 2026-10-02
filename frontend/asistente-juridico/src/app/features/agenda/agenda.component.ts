import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { AgendaService } from '../../core/services/agenda.service';
import { EventoAgendaDto } from '../../core/models/fase5.models';

@Component({
  selector: 'app-agenda',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule],
  template: `
    <div class="agenda-wrapper">
      <header class="agenda-header">
        <div>
          <h1 class="page-title">Agenda Jurídica Unificada</h1>
          <p class="subtitle">Audiencias judiciales y vencimiento de tareas procesales</p>
        </div>
        <div class="header-actions">
          <button (click)="cargarHoy()" class="btn" [ngClass]="modoHoy() ? 'btn-primary' : 'btn-secondary'">
            ☀️ Eventos de Hoy
          </button>
          <button (click)="activarRango()" class="btn" [ngClass]="!modoHoy() ? 'btn-primary' : 'btn-secondary'">
            📅 Vista por Rango
          </button>
        </div>
      </header>

      <!-- FILTERS -->
      <section *ngIf="!modoHoy()" class="filters-card">
        <div class="filter-group">
          <label>Desde:</label>
          <input type="date" [(ngModel)]="fechaDesde" (change)="cargarEventos()" class="form-control" />
        </div>
        <div class="filter-group">
          <label>Hasta:</label>
          <input type="date" [(ngModel)]="fechaHasta" (change)="cargarEventos()" class="form-control" />
        </div>
        <div class="filter-group">
          <label>Tipo:</label>
          <select [(ngModel)]="tipoEventoSeleccionado" (change)="cargarEventos()" class="form-control">
            <option [ngValue]="null">Todos los eventos</option>
            <option [ngValue]="1">Solo Audiencias</option>
            <option [ngValue]="2">Solo Tareas</option>
          </select>
        </div>
        <div class="filter-group">
          <label>Materia:</label>
          <input type="text" [(ngModel)]="materiaFiltro" (keyup.enter)="cargarEventos()" placeholder="Ej: Civil, Penal..." class="form-control" />
        </div>
        <button (click)="cargarEventos()" class="btn btn-secondary filter-btn">Filtrar</button>
      </section>

      <!-- ALERTS / ERRORS -->
      <div *ngIf="error()" class="alert alert-error">
        <strong>Error:</strong> {{ error() }}
      </div>

      <!-- LOADING -->
      <div *ngIf="cargando()" class="loading-state">
        <div class="spinner"></div>
        <p>Cargando agenda...</p>
      </div>

      <!-- EVENTS TABLE / LIST -->
      <div *ngIf="!cargando()" class="card events-card">
        <div class="card-header">
          <h3>
            {{ modoHoy() ? 'Eventos Programados para Hoy' : 'Eventos en el Rango Seleccionado' }}
            <span class="count-badge">({{ eventos().length }})</span>
          </h3>
        </div>

        <div *ngIf="eventos().length === 0" class="empty-state">
          No hay eventos programados en este período.
        </div>

        <div *ngIf="eventos().length > 0" class="table-responsive">
          <table class="data-table">
            <thead>
              <tr>
                <th>Tipo</th>
                <th>Fecha / Hora (Inicio)</th>
                <th>Título / Detalle</th>
                <th>Expediente</th>
                <th>Sala / Ubicación</th>
                <th>Estado</th>
                <th>Responsable</th>
              </tr>
            </thead>
            <tbody>
              <tr *ngFor="let ev of eventos()">
                <td>
                  <span class="badge" [ngClass]="ev.tipoEvento === 1 ? 'badge-audiencia' : 'badge-tarea'">
                    {{ ev.tipoEvento === 1 ? '⚖️ Audiencia' : '⚡ Tarea' }}
                  </span>
                </td>
                <td class="cell-datetime">
                  {{ ev.fechaHoraInicioUtc | date:'dd/MM/yyyy HH:mm' }}
                </td>
                <td>
                  <strong>{{ ev.titulo }}</strong>
                  <div *ngIf="ev.descripcion" class="cell-desc">{{ ev.descripcion }}</div>
                </td>
                <td>
                  <span class="exp-code">{{ ev.expedienteNumero || 'N/A' }}</span>
                  <div *ngIf="ev.materia" class="cell-materia">{{ ev.materia }}</div>
                </td>
                <td>
                  <span class="location-tag">{{ ev.ubicacionOSala || 'Virtual' }}</span>
                </td>
                <td>
                  <span class="badge badge-subtle">
                    {{ ev.estado === 1 ? 'Programada' : ev.estado === 2 ? 'En Curso' : ev.estado === 3 ? 'Completada' : 'Otro' }}
                  </span>
                </td>
                <td>{{ ev.responsableNombre || 'No asignado' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .agenda-wrapper {
      padding: 24px;
      max-width: 1200px;
      margin: 0 auto;
      font-family: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      color: #1e293b;
    }
    .agenda-header {
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
      gap: 10px;
    }
    .btn {
      padding: 9px 16px;
      border-radius: 8px;
      font-size: 0.9rem;
      font-weight: 600;
      cursor: pointer;
      border: 1px solid transparent;
      transition: all 0.2s;
    }
    .btn-primary {
      background: #4f46e5;
      color: #fff;
    }
    .btn-primary:hover {
      background: #4338ca;
    }
    .btn-secondary {
      background: #ffffff;
      color: #334155;
      border-color: #cbd5e1;
    }
    .btn-secondary:hover {
      background: #f8fafc;
    }
    .filters-card {
      background: #ffffff;
      border-radius: 12px;
      border: 1px solid #e2e8f0;
      padding: 16px 20px;
      display: flex;
      flex-wrap: wrap;
      gap: 16px;
      align-items: flex-end;
      margin-bottom: 24px;
      box-shadow: 0 1px 3px rgba(0,0,0,0.04);
    }
    .filter-group {
      display: flex;
      flex-direction: column;
      gap: 6px;
      flex: 1;
      min-width: 150px;
    }
    .filter-group label {
      font-size: 0.8rem;
      font-weight: 600;
      color: #475569;
    }
    .form-control {
      padding: 8px 12px;
      border-radius: 6px;
      border: 1px solid #cbd5e1;
      font-size: 0.85rem;
      color: #1e293b;
      outline: none;
    }
    .form-control:focus {
      border-color: #4f46e5;
    }
    .filter-btn {
      align-self: flex-end;
      height: 38px;
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
      margin-bottom: 16px;
    }
    .card-header h3 {
      font-size: 1.15rem;
      font-weight: 600;
      margin: 0;
    }
    .count-badge {
      color: #64748b;
      font-size: 0.9rem;
      font-weight: 500;
    }
    .badge {
      display: inline-block;
      padding: 3px 8px;
      border-radius: 6px;
      font-size: 0.75rem;
      font-weight: 600;
    }
    .badge-audiencia { background: #ede9fe; color: #5b21b6; }
    .badge-tarea { background: #fef3c7; color: #92400e; }
    .badge-subtle { background: #f1f5f9; color: #475569; }
    .data-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.85rem;
    }
    .data-table th, .data-table td {
      padding: 12px;
      text-align: left;
      border-bottom: 1px solid #f1f5f9;
    }
    .data-table th {
      background: #f8fafc;
      color: #64748b;
      font-weight: 600;
    }
    .cell-datetime {
      font-weight: 600;
      color: #334155;
    }
    .cell-desc {
      font-size: 0.75rem;
      color: #64748b;
      margin-top: 4px;
    }
    .cell-materia {
      font-size: 0.75rem;
      color: #94a3b8;
    }
    .exp-code {
      font-weight: 600;
      color: #4f46e5;
    }
    .location-tag {
      background: #f1f5f9;
      padding: 2px 6px;
      border-radius: 4px;
      font-size: 0.75rem;
      color: #475569;
    }
    .empty-state {
      padding: 40px;
      text-align: center;
      color: #94a3b8;
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
    .alert-error {
      background: #fef2f2;
      border: 1px solid #fee2e2;
      color: #b91c1c;
      padding: 12px 16px;
      border-radius: 8px;
      margin-bottom: 20px;
      font-size: 0.9rem;
    }
  `]
})
export class AgendaComponent implements OnInit {
  private readonly agendaService = inject(AgendaService);

  eventos = signal<EventoAgendaDto[]>([]);
  cargando = signal<boolean>(false);
  error = signal<string | null>(null);
  modoHoy = signal<boolean>(true);

  fechaDesde = new Date().toISOString().substring(0, 10);
  fechaHasta = new Date(Date.now() + 7 * 86400000).toISOString().substring(0, 10);
  tipoEventoSeleccionado: number | null = null;
  materiaFiltro = '';

  ngOnInit(): void {
    this.cargarHoy();
  }

  cargarHoy(): void {
    this.modoHoy.set(true);
    this.cargando.set(true);
    this.error.set(null);

    this.agendaService.getHoy().subscribe({
      next: (res) => {
        if (res.success) {
          this.eventos.set(res.data);
        } else {
          this.error.set(res.message || 'Error cargando eventos de hoy.');
        }
        this.cargando.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.message || 'Error consultando agenda de hoy.');
        this.cargando.set(false);
      }
    });
  }

  activarRango(): void {
    this.modoHoy.set(false);
    this.cargarEventos();
  }

  cargarEventos(): void {
    if (this.modoHoy()) return;

    this.cargando.set(true);
    this.error.set(null);

    this.agendaService.getEventos({
      fechaDesdeUtc: new Date(this.fechaDesde).toISOString(),
      fechaHastaUtc: new Date(this.fechaHasta + 'T23:59:59Z').toISOString(),
      tipoEvento: this.tipoEventoSeleccionado ?? undefined,
      materia: this.materiaFiltro ? this.materiaFiltro : undefined,
      pageSize: 200
    }).subscribe({
      next: (res) => {
        if (res.success) {
          this.eventos.set(res.data.items);
        } else {
          this.error.set(res.message || 'Error consultando eventos.');
        }
        this.cargando.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.message || 'Error al obtener eventos.');
        this.cargando.set(false);
      }
    });
  }
}
