import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ClienteService } from './cliente.service';
import { ExpedienteService } from './expediente.service';
import { ProcesoJudicialService } from './proceso-judicial.service';
import { TareaService } from './tarea.service';
import { AudienciaService } from './audiencia.service';
import { DocumentoService } from './documento.service';
import {
  ClienteDto,
  CreateClienteDto,
  CreateExpedienteDto,
  ExpedienteDetailDto,
  ExpedienteDto,
  PagedResponse,
  ProcesoJudicialDto,
  TareaDto,
  AudienciaDto,
  DocumentoDto
} from '../models/fase4.models';
import { ApiResponse } from '../models/auth.models';

describe('Phase 4 Services Tests', () => {
  let httpMock: HttpTestingController;
  let clienteService: ClienteService;
  let expedienteService: ExpedienteService;
  let procesoService: ProcesoJudicialService;
  let tareaService: TareaService;
  let audienciaService: AudienciaService;
  let documentoService: DocumentoService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ClienteService,
        ExpedienteService,
        ProcesoJudicialService,
        TareaService,
        AudienciaService,
        DocumentoService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    httpMock = TestBed.inject(HttpTestingController);
    clienteService = TestBed.inject(ClienteService);
    expedienteService = TestBed.inject(ExpedienteService);
    procesoService = TestBed.inject(ProcesoJudicialService);
    tareaService = TestBed.inject(TareaService);
    audienciaService = TestBed.inject(AudienciaService);
    documentoService = TestBed.inject(DocumentoService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('ClienteService', () => {
    it('should fetch paged clientes with query parameters', () => {
      const mockPagedResponse: ApiResponse<PagedResponse<ClienteDto>> = {
        success: true,
        data: {
          items: [
            {
              id: 'c-1',
              tenantId: 't-1',
              tipoIdentificacion: 'Cedula',
              identificacion: '1710034065',
              nombreRazonSocial: 'Juan Pérez',
              esPersonaJuridica: false,
              activo: true,
              createdAt: new Date().toISOString(),
              version: 1
            }
          ],
          pageNumber: 1,
          pageSize: 10,
          totalCount: 1,
          totalPages: 1,
          hasPreviousPage: false,
          hasNextPage: false
        },
        message: 'Clientes obtenidos correctamente',
        errors: [],
        timestamp: new Date().toISOString()
      };

      clienteService.getClientes(1, 10, 'Juan', true).subscribe(res => {
        expect(res.success).toBe(true);
        expect(res.data.items.length).toBe(1);
        expect(res.data.items[0].nombreRazonSocial).toBe('Juan Pérez');
      });

      const req = httpMock.expectOne(req =>
        req.url === 'http://localhost:5270/api/v1/clientes' &&
        req.params.get('search') === 'Juan' &&
        req.params.get('activo') === 'true'
      );
      expect(req.request.method).toBe('GET');
      req.flush(mockPagedResponse);
    });

    it('should create a new cliente', () => {
      const createDto: CreateClienteDto = {
        tipoIdentificacion: 'Ruc',
        identificacion: '1790016919001',
        nombreRazonSocial: 'Corporación Favorita C.A.',
        email: 'info@cfavorita.com',
        esPersonaJuridica: true
      };

      const mockResponse: ApiResponse<ClienteDto> = {
        success: true,
        data: {
          id: 'c-2',
          tenantId: 't-1',
          ...createDto,
          activo: true,
          createdAt: new Date().toISOString(),
          version: 1
        },
        message: 'Cliente creado correctamente',
        errors: [],
        timestamp: new Date().toISOString()
      };

      clienteService.createCliente(createDto).subscribe(res => {
        expect(res.success).toBe(true);
        expect(res.data.id).toBe('c-2');
        expect(res.data.esPersonaJuridica).toBe(true);
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/clientes');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual(createDto);
      req.flush(mockResponse);
    });
  });

  describe('ExpedienteService', () => {
    it('should fetch expediente detail with all child collections', () => {
      const mockDetail: ApiResponse<ExpedienteDetailDto> = {
        success: true,
        data: {
          id: 'exp-1',
          numeroExpediente: 'EXP-2026-0001',
          titulo: 'Demanda Laboral',
          materia: 'Laboral',
          estado: 'EnTramite',
          prioridad: 'Alta',
          clienteId: 'c-1',
          clienteNombre: 'Juan Pérez',
          fechaApertura: new Date().toISOString(),
          createdAt: new Date().toISOString(),
          version: 1,
          tareas: [],
          audiencias: [],
          documentos: [],
          procesosJudiciales: []
        },
        message: 'Detalle de expediente',
        errors: [],
        timestamp: new Date().toISOString()
      };

      expedienteService.getExpedienteById('exp-1').subscribe(res => {
        expect(res.success).toBe(true);
        expect(res.data.numeroExpediente).toBe('EXP-2026-0001');
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/expedientes/exp-1');
      expect(req.request.method).toBe('GET');
      req.flush(mockDetail);
    });

    it('should change expediente state with force close parameters', () => {
      const cambioDto = {
        nuevoEstado: 'Cerrado' as const,
        confirmarCierreConTareasPendientes: true,
        motivoCierreForzado: 'Transacción extrajudicial aprobada por las partes',
        version: 2
      };

      const mockResponse: ApiResponse<ExpedienteDto> = {
        success: true,
        data: {
          id: 'exp-1',
          numeroExpediente: 'EXP-2026-0001',
          titulo: 'Demanda Laboral',
          materia: 'Laboral',
          estado: 'Cerrado',
          prioridad: 'Alta',
          clienteId: 'c-1',
          clienteNombre: 'Juan Pérez',
          fechaApertura: new Date().toISOString(),
          createdAt: new Date().toISOString(),
          version: 3
        },
        message: 'Estado cambiado',
        errors: [],
        timestamp: new Date().toISOString()
      };

      expedienteService.cambiarEstado('exp-1', cambioDto).subscribe(res => {
        expect(res.success).toBe(true);
        expect(res.data.estado).toBe('Cerrado');
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/expedientes/exp-1/estado');
      expect(req.request.method).toBe('PATCH');
      expect(req.request.body).toEqual(cambioDto);
      req.flush(mockResponse);
    });
  });

  describe('ProcesoJudicialService', () => {
    it('should search mock processes from MockProcesoJudicialProvider', () => {
      const mockResult: ApiResponse<PagedResponse<ProcesoJudicialDto>> = {
        success: true,
        data: {
          items: [
            {
              id: 'p-1',
              numeroProceso: '17230202400012',
              judicatura: 'Unidad Judicial Civil de Iñaquito',
              materia: 'Civil',
              esMock: true,
              ultimaSincronizacionMock: new Date().toISOString(),
              version: 1
            }
          ],
          pageNumber: 1,
          pageSize: 10,
          totalCount: 1,
          totalPages: 1,
          hasPreviousPage: false,
          hasNextPage: false
        },
        message: 'Causas judiciales encontradas',
        errors: [],
        timestamp: new Date().toISOString()
      };

      procesoService.buscarCausas(1, 10, '17230202400012').subscribe(res => {
        expect(res.success).toBe(true);
        expect(res.data.items[0].esMock).toBe(true);
      });

      const req = httpMock.expectOne(req =>
        req.url === 'http://localhost:5270/api/v1/procesos-judiciales' &&
        req.params.get('numeroProceso') === '17230202400012'
      );
      expect(req.request.method).toBe('GET');
      req.flush(mockResult);
    });
  });

  describe('DocumentoService', () => {
    it('should upload a document with FormData', () => {
      const dummyFile = new File(['%PDF-1.4 test'], 'demanda.pdf', { type: 'application/pdf' });
      const mockDocResponse: ApiResponse<DocumentoDto> = {
        success: true,
        data: {
          id: 'doc-1',
          expedienteId: 'exp-1',
          titulo: 'Demanda Inicial',
          nombreArchivoOriginal: 'demanda.pdf',
          contentType: 'application/pdf',
          tamanoBytes: 14,
          sha256Hash: 'abc123hash',
          versionDocumento: 1,
          uploadedBy: 'abogado@test.ec',
          createdAt: new Date().toISOString(),
          version: 1
        },
        message: 'Documento subido exitosamente',
        errors: [],
        timestamp: new Date().toISOString()
      };

      documentoService.uploadDocumento('exp-1', 'Demanda Inicial', dummyFile).subscribe(res => {
        expect(res.success).toBe(true);
        expect(res.data.contentType).toBe('application/pdf');
        expect(res.data.sha256Hash).toBe('abc123hash');
      });

      const req = httpMock.expectOne('http://localhost:5270/api/v1/documentos/upload');
      expect(req.request.method).toBe('POST');
      expect(req.request.body instanceof FormData).toBe(true);
      req.flush(mockDocResponse);
    });
  });
});
