import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { AIService, AIStreamError } from './ai.service';
import { AuthService } from './auth.service';
import { AICasoUso, AIChatRequestDto, AIChatResponseDto } from '../models/ai.models';
import { ApiResponse } from '../models/auth.models';

describe('AIService (Fase 6)', () => {
  let service: AIService;
  let httpMock: HttpTestingController;

  const mockAuthService = {
    getAccessToken: () => 'mock-jwt-token-fase6'
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        AIService,
        { provide: AuthService, useValue: mockAuthService },
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    service = TestBed.inject(AIService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('debe crearse correctamente', () => {
    expect(service).toBeTruthy();
  });

  it('debe enviar mensaje al asistente jurídico mediante POST /api/v1/ai/chat', () => {
    const requestDto: AIChatRequestDto = {
      mensaje: '¿Cuáles son los requisitos de la demanda según el COGEP?',
      casoUso: AICasoUso.ChatLibre,
      streaming: false
    };

    const mockResponse: ApiResponse<AIChatResponseDto> = {
      success: true,
      data: {
        conversationId: 'c123',
        messageId: 'm456',
        contenido: 'El Art. 142 del COGEP establece los requisitos...',
        disclaimer: 'AVISO DEONTOLÓGICO: Orientativo y no vinculante.',
        contextoAutorizado: false
      },
      message: 'OK',
      errors: [],
      timestamp: new Date().toISOString()
    };

    service.sendMessage(requestDto).subscribe((res) => {
      expect(res.success).toBe(true);
      expect(res.data?.contenido).toContain('COGEP');
      expect(res.data?.disclaimer).toBeTruthy();
    });

    const req = httpMock.expectOne('http://localhost:5270/api/v1/ai/chat');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.streaming).toBe(false);
    req.flush(mockResponse);
  });

  it('debe listar conversaciones de IA con GET /api/v1/ai/conversaciones', () => {
    service.getConversaciones('exp-123').subscribe((res) => {
      expect(res.success).toBe(true);
      expect(res.data?.length).toBe(1);
      expect(res.data?.[0].titulo).toBe('Consulta COGEP');
    });

    const req = httpMock.expectOne('http://localhost:5270/api/v1/ai/conversaciones?expedienteId=exp-123');
    expect(req.request.method).toBe('GET');
    req.flush({
      success: true,
      data: [
        {
          id: 'c1',
          titulo: 'Consulta COGEP',
          casoUso: AICasoUso.ChatLibre,
          expedienteId: 'exp-123',
          usuarioId: 'u1',
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          mensajesCount: 2
        }
      ],
      message: 'OK',
      errors: [],
      timestamp: new Date().toISOString()
    });
  });

  describe('streamMessage (POST + SSE con fetch nativo)', () => {
    const sseResponse = (text: string, status = 200, contentType = 'text/event-stream'): Response => {
      const encoder = new TextEncoder();
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(encoder.encode(text));
          controller.close();
        }
      });
      return new Response(body, { status, headers: { 'Content-Type': contentType } });
    };

    const dto: AIChatRequestDto = { mensaje: 'Consulta', casoUso: AICasoUso.ChatLibre, streaming: true };

    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it('envía POST con streaming=true y AbortSignal, y entrega fragmentos hasta [DONE]', async () => {
      const fetchMock = vi.fn().mockResolvedValue(
        sseResponse('data: {"deltaContent":"Hola"}\n\ndata: {"deltaContent":" mundo"}\n\ndata: [DONE]\n\n')
      );
      vi.stubGlobal('fetch', fetchMock);
      const controller = new AbortController();
      const recibidos: string[] = [];

      await service.streamMessage(dto, (c) => recibidos.push(c.deltaContent ?? ''), controller.signal);

      expect(recibidos).toEqual(['Hola', ' mundo']);
      const [url, init] = fetchMock.mock.calls[0];
      expect(url).toBe('http://localhost:5270/api/v1/ai/chat');
      expect(init.method).toBe('POST');
      expect(JSON.parse(init.body).streaming).toBe(true);
      expect(init.signal).toBe(controller.signal);
      expect(init.headers['Authorization']).toBe('Bearer mock-jwt-token-fase6');
    });

    it('rechaza con AIStreamError cuando llega event: error a mitad del stream', async () => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
        sseResponse('data: {"deltaContent":"Parcial"}\n\nevent: error\ndata: {"code":"AI_PROVIDER_ERROR","message":"Proveedor caído"}\n\n')
      ));
      const recibidos: string[] = [];

      const error = await service.streamMessage(dto, (c) => recibidos.push(c.deltaContent ?? '')).catch((e) => e);

      expect(error).toBeInstanceOf(AIStreamError);
      expect(error.code).toBe('AI_PROVIDER_ERROR');
      expect(error.message).toBe('Proveedor caído');
      expect(recibidos).toEqual(['Parcial']);
    });

    it('rechaza con el código del envelope ApiResponse cuando el servidor responde 502 antes del stream', async () => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
        sseResponse(
          JSON.stringify({ success: false, message: 'Error del proveedor', errors: ['AI_PROVIDER_TIMEOUT'], timestamp: '' }),
          502,
          'application/json'
        )
      ));

      const error = await service.streamMessage(dto, () => {}).catch((e) => e);

      expect(error).toBeInstanceOf(AIStreamError);
      expect(error.code).toBe('AI_PROVIDER_TIMEOUT');
      expect(error.status).toBe(502);
    });

    it('rechaza como STREAM_INCOMPLETE si el stream se cierra sin [DONE]', async () => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(sseResponse('data: {"deltaContent":"Corte"}\n\n')));

      const error = await service.streamMessage(dto, () => {}).catch((e) => e);

      expect(error).toBeInstanceOf(AIStreamError);
      expect(error.code).toBe('STREAM_INCOMPLETE');
    });
  });

  it('debe consultar métricas de consumo con GET /api/v1/ai/consumo', () => {
    service.getConsumo('2026-09-01T00:00:00Z', '2026-09-15T00:00:00Z').subscribe((res) => {
      expect(res.success).toBe(true);
      expect(res.data?.totalInvocaciones).toBe(15);
      expect(res.data?.costoEstimadoUsdTotal).toBe(0.045);
    });

    const req = httpMock.expectOne((r) => r.url === 'http://localhost:5270/api/v1/ai/consumo');
    expect(req.request.method).toBe('GET');
    req.flush({
      success: true,
      data: {
        totalInvocaciones: 15,
        totalTokensEntrada: 12000,
        totalTokensSalida: 3500,
        totalTokens: 15500,
        costoEstimadoUsdTotal: 0.045,
        desglosePorCasoUso: { '1': 10, '2': 5 },
        desglosePorUsuario: [],
        periodoDias: 14
      },
      message: 'OK',
      errors: [],
      timestamp: new Date().toISOString()
    });
  });
});
