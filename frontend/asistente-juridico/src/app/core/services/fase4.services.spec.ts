import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ClienteService } from './cliente.service';
import { ExpedienteService } from './expediente.service';
import { ProcesoJudicialService } from './proceso-judicial.service';
import { TareaService } from './tarea.service';
import { AudienciaService } from './audiencia.service';
import { DocumentoService, nombreDescargaDocumento } from './documento.service';
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
  DocumentoDto,
  DocumentoErrorCode,
  EstadoProcesamientoIa
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
          procesosJudiciales: [],
          documentosCount: 0
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
    const documentosUrl = 'http://localhost:5270/api/v1/documentos';

    const documento = (overrides: Partial<DocumentoDto> = {}): DocumentoDto => ({
      id: 'doc-1',
      tenantId: 't-1',
      expedienteId: 'exp-1',
      expedienteNumero: 'EXP-2026-0001',
      titulo: 'Demanda Inicial',
      tipoDocumento: 'Demanda',
      descripcion: 'Escrito inicial',
      nombreArchivoOriginal: 'demanda.pdf',
      contentType: 'application/pdf',
      tamanioBytes: 14,
      hashSha256: 'a'.repeat(64),
      estadoIa: EstadoProcesamientoIa.Pendiente,
      estadoIaDescripcion: 'Pendiente',
      createdAt: new Date().toISOString(),
      createdBy: 'abogado@test.ec',
      updatedAt: null,
      version: 1234,
      ...overrides
    });

    const errorBody = (code: DocumentoErrorCode) => ({
      success: false,
      data: null,
      message: 'Error',
      errors: [code],
      timestamp: new Date().toISOString()
    });

    it('getDocumentos usa GET /api/v1/documentos con expedienteId, paginación y filtros informados', () => {
      documentoService.getDocumentos({
        expedienteId: 'exp-1',
        pageNumber: 2,
        pageSize: 25,
        tipoDocumento: 'Demanda',
        fechaDesde: '2026-01-01',
        fechaHasta: '2026-12-31',
        searchTerm: 'inicial'
      }).subscribe();

      const req = httpMock.expectOne(r => r.url === documentosUrl);
      expect(req.request.method).toBe('GET');
      expect(req.request.url).not.toContain('/documentos/expediente/');
      const p = req.request.params;
      expect(p.get('expedienteId')).toBe('exp-1');
      expect(p.get('pageNumber')).toBe('2');
      expect(p.get('pageSize')).toBe('25');
      expect(p.get('tipoDocumento')).toBe('Demanda');
      expect(p.get('fechaDesde')).toBe('2026-01-01');
      expect(p.get('fechaHasta')).toBe('2026-12-31');
      expect(p.get('searchTerm')).toBe('inicial');
      req.flush({ success: true, data: { items: [], pageNumber: 2, pageSize: 25, totalCount: 0, totalPages: 0, hasPreviousPage: true, hasNextPage: false }, message: '', errors: [], timestamp: '' });
    });

    it('getDocumentos solo envía los parámetros opcionales que tienen valor', () => {
      documentoService.getDocumentos({ expedienteId: 'exp-9' }).subscribe();

      const req = httpMock.expectOne(r => r.url === documentosUrl);
      expect(req.request.params.keys()).toEqual(['expedienteId']);
      expect(req.request.urlWithParams).toBe(`${documentosUrl}?expedienteId=exp-9`);
      req.flush({ success: true, data: { items: [], pageNumber: 1, pageSize: 20, totalCount: 0, totalPages: 0, hasPreviousPage: false, hasNextPage: false }, message: '', errors: [], timestamp: '' });
    });

    it('getDocumentos devuelve PagedResponse<DocumentoDto> con los campos del backend', () => {
      const historico = documento({ id: 'doc-2', nombreArchivoOriginal: null, hashSha256: null, estadoIa: EstadoProcesamientoIa.Procesado, estadoIaDescripcion: 'Procesado' });
      const respuesta: ApiResponse<PagedResponse<DocumentoDto>> = {
        success: true,
        data: { items: [documento(), historico], pageNumber: 1, pageSize: 20, totalCount: 2, totalPages: 1, hasPreviousPage: false, hasNextPage: false },
        message: '',
        errors: [],
        timestamp: new Date().toISOString()
      };
      let recibido: ApiResponse<PagedResponse<DocumentoDto>> | undefined;

      documentoService.getDocumentos({ expedienteId: 'exp-1' }).subscribe(res => (recibido = res));
      httpMock.expectOne(r => r.url === documentosUrl).flush(respuesta);

      expect(recibido).toEqual(respuesta);
      expect(recibido!.data.totalCount).toBe(2);
      expect(recibido!.data.items[0].tamanioBytes).toBe(14);
      expect(recibido!.data.items[0].tipoDocumento).toBe('Demanda');
      expect(recibido!.data.items[1].nombreArchivoOriginal).toBeNull();
      expect(recibido!.data.items[1].hashSha256).toBeNull();
      expect(recibido!.data.items[1].estadoIa).toBe(EstadoProcesamientoIa.Procesado);
    });

    it('getDocumentoById usa GET /api/v1/documentos/{id}', () => {
      documentoService.getDocumentoById('doc-7').subscribe();
      const req = httpMock.expectOne(`${documentosUrl}/doc-7`);
      expect(req.request.method).toBe('GET');
      req.flush({ success: true, data: documento({ id: 'doc-7' }), message: '', errors: [], timestamp: '' });
    });

    it('uploadDocumento envía FormData con expedienteId, titulo, tipoDocumento, file y descripcion (nunca "archivo")', () => {
      const dummyFile = new File(['%PDF-1.4 test'], 'demanda.pdf', { type: 'application/pdf' });

      documentoService.uploadDocumento('exp-1', 'Demanda Inicial', 'Demanda', dummyFile, 'Escrito inicial').subscribe(res => {
        expect(res.data.hashSha256).toBe('a'.repeat(64));
      });

      const req = httpMock.expectOne(`${documentosUrl}/upload`);
      expect(req.request.method).toBe('POST');
      const form = req.request.body as FormData;
      expect(form instanceof FormData).toBe(true);
      expect(form.get('expedienteId')).toBe('exp-1');
      expect(form.get('titulo')).toBe('Demanda Inicial');
      expect(form.get('tipoDocumento')).toBe('Demanda');
      expect(form.get('descripcion')).toBe('Escrito inicial');
      const enviado = form.get('file') as File;
      expect(enviado instanceof File).toBe(true);
      expect(enviado.name).toBe('demanda.pdf');
      expect(form.has('archivo')).toBe(false);
      expect(Array.from(form.keys()).sort()).toEqual(['descripcion', 'expedienteId', 'file', 'tipoDocumento', 'titulo']);
      req.flush({ success: true, data: documento(), message: '', errors: [], timestamp: '' });
    });

    it('uploadDocumento omite descripcion cuando no existe', () => {
      const dummyFile = new File(['%PDF-1.4 test'], 'demanda.pdf', { type: 'application/pdf' });

      documentoService.uploadDocumento('exp-1', 'Demanda Inicial', 'Demanda', dummyFile).subscribe();

      const form = httpMock.expectOne(`${documentosUrl}/upload`).request.body as FormData;
      expect(form.has('descripcion')).toBe(false);
      expect(form.has('archivo')).toBe(false);
      expect(Array.from(form.keys()).sort()).toEqual(['expedienteId', 'file', 'tipoDocumento', 'titulo']);
    });

    it('updateDocumento envía EXACTAMENTE titulo, tipoDocumento, descripcion y version aunque reciba un DocumentoDto completo', () => {
      // Origen con campos protegidos, incluidos los que no forman parte de DocumentoDto
      const origen = {
        ...documento({ titulo: 'Título nuevo', tipoDocumento: 'Contrato', descripcion: 'Desc nueva', version: 98765 }),
        rutaAlmacenamiento: 't-1/exp-1/x.pdf',
        metadatosJson: '{"resumen":"x"}'
      };

      documentoService.updateDocumento('doc-1', origen).subscribe();

      const req = httpMock.expectOne(`${documentosUrl}/doc-1`);
      expect(req.request.method).toBe('PUT');
      const body = req.request.body as Record<string, unknown>;
      expect(Object.keys(body).sort()).toEqual(['descripcion', 'tipoDocumento', 'titulo', 'version']);
      expect(body).toEqual({ titulo: 'Título nuevo', tipoDocumento: 'Contrato', descripcion: 'Desc nueva', version: 98765 });
      for (const prohibido of ['id', 'tenantId', 'expedienteId', 'rutaAlmacenamiento', 'hashSha256', 'estadoIa', 'metadatosJson', 'createdAt', 'updatedAt']) {
        expect(prohibido in body).toBe(false);
      }
      req.flush({ success: true, data: documento(), message: '', errors: [], timestamp: '' });
    });

    it('deleteDocumento usa el id y la version recibidos en cada llamada (sin versión fija)', () => {
      documentoService.deleteDocumento('doc-a', 4242).subscribe();
      documentoService.deleteDocumento('doc-b', 917).subscribe();

      const peticiones = httpMock.match(r => r.method === 'DELETE');
      expect(peticiones.length).toBe(2);
      expect(peticiones[0].request.urlWithParams).toBe(`${documentosUrl}/doc-a?version=4242`);
      expect(peticiones[0].request.params.get('version')).toBe('4242');
      expect(peticiones[1].request.urlWithParams).toBe(`${documentosUrl}/doc-b?version=917`);
      expect(peticiones[1].request.params.get('version')).toBe('917');
      peticiones.forEach(p => p.flush({ success: true, data: null, message: '', errors: [], timestamp: '' }));
    });

    it('downloadDocumento pide un blob y devuelve exactamente el Blob recibido, sin depender de Content-Disposition', () => {
      const blob = new Blob(['%PDF-1.4 contenido'], { type: 'application/pdf' });
      let recibido: unknown;

      documentoService.downloadDocumento('doc-1').subscribe(res => (recibido = res));

      const req = httpMock.expectOne(`${documentosUrl}/doc-1/download`);
      expect(req.request.method).toBe('GET');
      expect(req.request.responseType).toBe('blob');
      req.flush(blob, { headers: { 'Content-Disposition': 'attachment; filename="otro-nombre.pdf"' } });

      expect(recibido).toBe(blob);
    });

    it('nombreDescargaDocumento usa nombreArchivoOriginal y, si es null, el titulo', () => {
      expect(nombreDescargaDocumento(documento({ nombreArchivoOriginal: 'demanda.pdf', titulo: 'Demanda Inicial' }))).toBe('demanda.pdf');
      expect(nombreDescargaDocumento(documento({ nombreArchivoOriginal: null, titulo: 'Demanda Inicial' }))).toBe('Demanda Inicial');
    });

    it('preserva errors=[DOCUMENT_CONCURRENCY_CONFLICT] en un 409', () => {
      let error: HttpErrorResponse | undefined;

      documentoService.updateDocumento('doc-1', { titulo: 'T', tipoDocumento: 'Demanda', descripcion: null, version: 1 })
        .subscribe({ error: (e: HttpErrorResponse) => (error = e) });
      httpMock.expectOne(`${documentosUrl}/doc-1`).flush(errorBody('DOCUMENT_CONCURRENCY_CONFLICT'), { status: 409, statusText: 'Conflict' });

      expect(error!.status).toBe(409);
      expect(error!.error.errors).toEqual(['DOCUMENT_CONCURRENCY_CONFLICT']);
    });

    it('preserva errors=[DOCUMENT_PROCESSING] en un 409', () => {
      let error: HttpErrorResponse | undefined;

      documentoService.deleteDocumento('doc-1', 55).subscribe({ error: (e: HttpErrorResponse) => (error = e) });
      httpMock.expectOne(`${documentosUrl}/doc-1?version=55`).flush(errorBody('DOCUMENT_PROCESSING'), { status: 409, statusText: 'Conflict' });

      expect(error!.status).toBe(409);
      expect(error!.error.errors).toEqual(['DOCUMENT_PROCESSING']);
    });

    it('preserva errors=[DOCUMENT_FILE_NOT_FOUND] en el 404 de la descarga (cuerpo de error como Blob)', async () => {
      let error: HttpErrorResponse | undefined;

      documentoService.downloadDocumento('doc-1').subscribe({ error: (e: HttpErrorResponse) => (error = e) });
      const cuerpo = new Blob([JSON.stringify(errorBody('DOCUMENT_FILE_NOT_FOUND'))], { type: 'application/json' });
      httpMock.expectOne(`${documentosUrl}/doc-1/download`).flush(cuerpo, { status: 404, statusText: 'Not Found' });

      expect(error!.status).toBe(404);
      // Con responseType 'blob' Angular entrega el cuerpo de error como Blob; el código llega intacto
      const json = JSON.parse(await (error!.error as Blob).text());
      expect(json.errors).toEqual(['DOCUMENT_FILE_NOT_FOUND']);
    });
  });
});
