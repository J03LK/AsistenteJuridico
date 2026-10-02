import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DashboardService } from './dashboard.service';
import { AgendaService } from './agenda.service';
import { AlertasService } from './alertas.service';
import { ApiResponse } from '../models/auth.models';
import {
  AlertaProcesalDto,
  ConteoNoLeidasDto,
  DashboardResumenDto,
  EventoAgendaDto,
  MetricasEficienciaDto,
  AgendaPaginadaDto,
  MarcarTodasLeidasResponseDto
} from '../models/fase5.models';

function createApiResponse<T>(data: T, message = 'OK'): ApiResponse<T> {
  return {
    success: true,
    data,
    message,
    errors: [],
    timestamp: new Date().toISOString()
  };
}

describe('Phase 5 Services Tests (Dashboard, Agenda, Alertas)', () => {
  let httpMock: HttpTestingController;
  let dashboardService: DashboardService;
  let agendaService: AgendaService;
  let alertasService: AlertasService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        DashboardService,
        AgendaService,
        AlertasService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    httpMock = TestBed.inject(HttpTestingController);
    dashboardService = TestBed.inject(DashboardService);
    agendaService = TestBed.inject(AgendaService);
    alertasService = TestBed.inject(AlertasService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('DashboardService', () => {
    it('should fetch dashboard resumen with all KPIs and metrics', () => {
      const mockResumen = createApiResponse<DashboardResumenDto>({
        kpis: {
          totalExpedientesActivos: 12,
          totalTareasPendientes: 5,
          totalAudienciasProximas7Dias: 2,
          totalAlertasAltaCriticasSinResolver: 1
        },
        distribucionExpedientes: {
          abiertos: 8,
          enTramite: 4,
          suspendidos: 0,
          cerrados: 20,
          archivados: 2
        },
        tareasPendientesPorPrioridad: {
          baja: 1,
          media: 2,
          alta: 1,
          urgente: 1
        },
        proximasAudiencias: [],
        inactividad: {
          expedientesInactivos30Dias: 3,
          expedientesInactivos60Dias: 1
        },
        eficiencia: {
          totalExpedientesCerradosEvaluados: 10,
          expedientesCerradosATiempo: 8,
          expedientesConPlazoEstimado: 10,
          porcentajeCumplimiento: 80.0
        }
      });

      dashboardService.getResumen().subscribe((res) => {
        expect(res.success).toBe(true);
        expect(res.data.kpis.totalExpedientesActivos).toBe(12);
        expect(res.data.inactividad.expedientesInactivos30Dias).toBe(3);
        expect(res.data.eficiencia.porcentajeCumplimiento).toBe(80.0);
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/dashboard/resumen');
      expect(req.request.method).toBe('GET');
      req.flush(mockResumen);
    });

    it('should fetch dashboard eficiencia with null percentage on zero denominator', () => {
      const mockEficiencia = createApiResponse<MetricasEficienciaDto>({
        totalExpedientesCerradosEvaluados: 0,
        expedientesCerradosATiempo: 0,
        expedientesConPlazoEstimado: 0,
        porcentajeCumplimiento: null
      });

      dashboardService.getEficiencia().subscribe((res) => {
        expect(res.success).toBe(true);
        expect(res.data.porcentajeCumplimiento).toBeNull();
      });

      const req = httpMock.expectOne((r) => r.url.includes('/api/v1/dashboard/eficiencia'));
      expect(req.request.method).toBe('GET');
      req.flush(mockEficiencia);
    });
  });

  describe('AgendaService', () => {
    it('should fetch events with date range and page size filters', () => {
      const mockAgenda = createApiResponse<AgendaPaginadaDto>({
        items: [
          {
            id: 'ev-1',
            tipoEvento: 1,
            titulo: 'Audiencia de Juicio',
            fechaHoraInicioUtc: new Date().toISOString(),
            estado: 1
          }
        ],
        totalCount: 1,
        pageNumber: 1,
        pageSize: 200
      });

      agendaService
        .getEventos({
          fechaDesdeUtc: '2026-10-01T00:00:00Z',
          fechaHastaUtc: '2026-10-07T23:59:59Z',
          pageSize: 200
        })
        .subscribe((res) => {
          expect(res.success).toBe(true);
          expect(res.data.items.length).toBe(1);
          expect(res.data.items[0].titulo).toBe('Audiencia de Juicio');
        });

      const req = httpMock.expectOne((r) => r.url.includes('/api/v1/agenda/eventos'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('pageSize')).toBe('200');
      req.flush(mockAgenda);
    });

    it('should fetch today events via /hoy endpoint', () => {
      const mockHoy = createApiResponse<EventoAgendaDto[]>([
        {
          id: 'ev-hoy-1',
          tipoEvento: 2,
          titulo: 'Presentar escrito',
          fechaHoraInicioUtc: new Date().toISOString(),
          estado: 1
        }
      ]);

      agendaService.getHoy().subscribe((res) => {
        expect(res.success).toBe(true);
        expect(res.data.length).toBe(1);
        expect(res.data[0].id).toBe('ev-hoy-1');
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/agenda/hoy');
      expect(req.request.method).toBe('GET');
      req.flush(mockHoy);
    });
  });

  describe('AlertasService', () => {
    it('should fetch unread alerts list and count', () => {
      const mockAlertas = createApiResponse<AlertaProcesalDto[]>([
        {
          id: 'al-1',
          tenantId: 't-1',
          tipoOrigen: 1,
          origenId: 'orig-1',
          reglaAlerta: 2,
          fechaObjetivoUtc: new Date().toISOString(),
          fechaDisparoUtc: new Date().toISOString(),
          titulo: 'Audiencia en 48 Horas',
          mensaje: 'Alerta prioritaria',
          severidad: 3,
          estadoResolucion: 1,
          leida: false,
          version: 1
        }
      ]);

      alertasService.getAlertas({ soloNoLeidas: true }).subscribe((res) => {
        expect(res.success).toBe(true);
        expect(res.data.length).toBe(1);
        expect(res.data[0].severidad).toBe(3);
      });

      const req = httpMock.expectOne((r) => r.url.includes('/api/v1/alertas'));
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('soloNoLeidas')).toBe('true');
      req.flush(mockAlertas);

      const mockConteo = createApiResponse<ConteoNoLeidasDto>({ totalNoLeidas: 3 });

      alertasService.getConteoNoLeidas().subscribe((res) => {
        expect(res.data.totalNoLeidas).toBe(3);
      });

      const reqConteo = httpMock.expectOne('http://localhost:5270/api/v1/alertas/conteo-no-leidas');
      expect(reqConteo.request.method).toBe('GET');
      reqConteo.flush(mockConteo);
    });

    it('should mark single alert as read with concurrency token', () => {
      const mockRes = createApiResponse<AlertaProcesalDto>({
        id: 'al-1',
        tenantId: 't-1',
        tipoOrigen: 1,
        origenId: 'orig-1',
        reglaAlerta: 2,
        fechaObjetivoUtc: new Date().toISOString(),
        fechaDisparoUtc: new Date().toISOString(),
        titulo: 'Audiencia en 48 Horas',
        mensaje: 'Alerta prioritaria',
        severidad: 3,
        estadoResolucion: 1,
        leida: true,
        version: 2
      });

      alertasService.marcarLeida('al-1', 1).subscribe((res) => {
        expect(res.success).toBe(true);
        expect(res.data.leida).toBe(true);
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/alertas/al-1/marcar-leida');
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ version: 1 });
      req.flush(mockRes);
    });

    it('should mark all alerts as read in bulk', () => {
      const mockRes = createApiResponse<MarcarTodasLeidasResponseDto>({ totalAfectadas: 5 });

      alertasService.marcarTodasLeidas(true).subscribe((res) => {
        expect(res.data.totalAfectadas).toBe(5);
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/alertas/marcar-todas-leidas');
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ incluirInstitucionales: true });
      req.flush(mockRes);
    });

    it('should discard alert with reason and version', () => {
      const mockRes = createApiResponse<AlertaProcesalDto>({
        id: 'al-1',
        tenantId: 't-1',
        tipoOrigen: 1,
        origenId: 'orig-1',
        reglaAlerta: 2,
        fechaObjetivoUtc: new Date().toISOString(),
        fechaDisparoUtc: new Date().toISOString(),
        titulo: 'Alerta Descartada',
        mensaje: 'Descarte',
        severidad: 2,
        estadoResolucion: 4,
        motivoResolucion: 'Ya resuelto',
        leida: true,
        version: 2
      });

      alertasService.descartar('al-1', 'Ya resuelto', 1).subscribe((res) => {
        expect(res.success).toBe(true);
        expect(res.data.estadoResolucion).toBe(4);
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/alertas/al-1/descartar');
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ motivo: 'Ya resuelto', version: 1 });
      req.flush(mockRes);
    });
  });
});
