import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import { AuthService } from './auth.service';
import {
  AIChatCompletionChunk,
  AIChatRequestDto,
  AIChatResponseDto,
  AIConsumoResponseDto,
  AIConversationDetailDto,
  AIConversationSummaryDto,
  AIExtractRequestDto,
  AIExtractResponseDto,
  AIDraftRequestDto,
  AIProviderCapabilities,
  AISummarizeRequestDto
} from '../models/ai.models';

/**
 * Error del streaming de IA con el código devuelto por el backend
 * (p. ej. AI_PROVIDER_ERROR, AI_PROVIDER_TIMEOUT, TOO_MANY_REQUESTS, STREAM_INCOMPLETE).
 */
export class AIStreamError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly status?: number
  ) {
    super(message);
    this.name = 'AIStreamError';
  }
}

@Injectable({
  providedIn: 'root'
})
export class AIService {
  private readonly http = inject(HttpClient);
  private readonly authService = inject(AuthService);
  private readonly apiUrl = 'http://localhost:5270/api/v1/ai';

  /**
   * Envía un mensaje no bloqueante esperando la respuesta completa en formato JSON.
   */
  sendMessage(dto: AIChatRequestDto): Observable<ApiResponse<AIChatResponseDto>> {
    return this.http.post<ApiResponse<AIChatResponseDto>>(`${this.apiUrl}/chat`, {
      ...dto,
      streaming: false
    });
  }

  /**
   * Transmite la respuesta del asistente en tiempo real mediante Server-Sent Events (SSE).
   * Implementado exclusivamente con fetch() nativo + ReadableStream + AbortController sin dependencias externas.
   *
   * Protocolo de errores (Fase 6.2):
   * - Error antes del primer fragmento: respuesta HTTP no exitosa con envelope ApiResponse.
   * - Error a mitad del stream: bloque `event: error` con `{ code, message }` y sin `[DONE]`.
   * - Stream cerrado sin `[DONE]`: transmisión incompleta.
   * En los tres casos se rechaza con AIStreamError. Abortar con el AbortController detiene la recepción
   * local, pero no garantiza que el proveedor deje de facturar lo ya procesado.
   */
  async streamMessage(
    dto: AIChatRequestDto,
    onChunk: (chunk: AIChatCompletionChunk) => void,
    signal?: AbortSignal
  ): Promise<void> {
    const token = this.authService.getAccessToken();
    const headers: Record<string, string> = {
      'Content-Type': 'application/json'
    };

    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    const response = await fetch(`${this.apiUrl}/chat`, {
      method: 'POST',
      headers,
      body: JSON.stringify({
        ...dto,
        streaming: true
      }),
      signal
    });

    if (!response.ok) {
      throw await this.toStreamError(response);
    }

    if (!response.body) {
      throw new AIStreamError('STREAM_INCOMPLETE', 'La respuesta HTTP no contiene un cuerpo de streaming legible.');
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let buffer = '';

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n\n');
        buffer = lines.pop() ?? '';

        for (const block of lines) {
          const trimmed = block.trim();

          if (trimmed.startsWith('event: error')) {
            const dataLine = trimmed.split('\n').find((l) => l.startsWith('data: '));
            let code = 'AI_PROVIDER_ERROR';
            let message = 'La transmisión de la respuesta se interrumpió.';
            try {
              const payload = JSON.parse(dataLine?.substring(6) ?? '{}') as { code?: string; message?: string };
              code = payload.code ?? code;
              message = payload.message ?? message;
            } catch {
              // Se conserva el error genérico
            }
            throw new AIStreamError(code, message);
          }

          if (!trimmed.startsWith('data: ')) continue;

          const dataContent = trimmed.substring(6).trim();
          if (dataContent === '[DONE]') {
            return;
          }

          try {
            const parsedChunk: AIChatCompletionChunk = JSON.parse(dataContent);
            onChunk(parsedChunk);
          } catch {
            // Ignorar bloques no parseables
          }
        }
      }
    } finally {
      reader.releaseLock();
    }

    throw new AIStreamError('STREAM_INCOMPLETE', 'La transmisión terminó antes de completarse.');
  }

  private async toStreamError(response: Response): Promise<AIStreamError> {
    try {
      const body = (await response.json()) as ApiResponse<unknown>;
      return new AIStreamError(body.errors?.[0] ?? `HTTP_${response.status}`, body.message || response.statusText, response.status);
    } catch {
      return new AIStreamError(`HTTP_${response.status}`, `Error en streaming de IA (${response.status}): ${response.statusText}`, response.status);
    }
  }

  /**
   * Obtiene la lista de conversaciones según los permisos del usuario.
   */
  getConversaciones(expedienteId?: string): Observable<ApiResponse<AIConversationSummaryDto[]>> {
    let params = new HttpParams();
    if (expedienteId) {
      params = params.set('expedienteId', expedienteId);
    }
    return this.http.get<ApiResponse<AIConversationSummaryDto[]>>(`${this.apiUrl}/conversaciones`, { params });
  }

  /**
   * Obtiene el detalle completo y mensajes de una conversación.
   */
  getConversacion(id: string): Observable<ApiResponse<AIConversationDetailDto>> {
    return this.http.get<ApiResponse<AIConversationDetailDto>>(`${this.apiUrl}/conversaciones/${id}`);
  }

  /**
   * Elimina lógicamente una conversación.
   */
  deleteConversacion(id: string): Observable<ApiResponse<boolean>> {
    return this.http.delete<ApiResponse<boolean>>(`${this.apiUrl}/conversaciones/${id}`);
  }

  /**
   * Solicita el resumen de un expediente procesal.
   */
  summarizeExpediente(dto: AISummarizeRequestDto): Observable<ApiResponse<AIChatResponseDto>> {
    return this.http.post<ApiResponse<AIChatResponseDto>>(`${this.apiUrl}/resumir-expediente`, dto);
  }

  /**
   * Solicita la extracción de hechos procesales sobre un documento.
   * La propuesta se persiste en MetadatosJson sin modificar autónomamente el expediente.
   */
  extractFromDocument(dto: AIExtractRequestDto): Observable<ApiResponse<AIExtractResponseDto>> {
    return this.http.post<ApiResponse<AIExtractResponseDto>>(`${this.apiUrl}/extraer-documento`, dto);
  }

  /**
   * Genera un borrador formal de escrito judicial conforme al estilo forense ecuatoriano.
   */
  draftEscrito(dto: AIDraftRequestDto): Observable<ApiResponse<AIChatResponseDto>> {
    return this.http.post<ApiResponse<AIChatResponseDto>>(`${this.apiUrl}/redactar-escrito`, dto);
  }

  /**
   * Consulta agregada de consumo y auditoría de IA (máximo 90 días).
   */
  getConsumo(fechaInicio: string, fechaFin: string, usuarioId?: string): Observable<ApiResponse<AIConsumoResponseDto>> {
    let params = new HttpParams()
      .set('fechaInicio', fechaInicio)
      .set('fechaFin', fechaFin);

    if (usuarioId) {
      params = params.set('usuarioId', usuarioId);
    }

    return this.http.get<ApiResponse<AIConsumoResponseDto>>(`${this.apiUrl}/consumo`, { params });
  }

  /**
   * Consulta las capacidades y límites del modelo activo.
   */
  getCapacidades(): Observable<ApiResponse<AIProviderCapabilities>> {
    return this.http.get<ApiResponse<AIProviderCapabilities>>(`${this.apiUrl}/capacidades`);
  }
}
