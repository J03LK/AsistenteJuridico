export enum AICasoUso {
  ChatLibre = 1,
  ResumenExpediente = 2,
  ResumenDocumento = 3,
  ExtraccionMetadatos = 4,
  ExtraccionHechos = 4,
  GeneracionBorrador = 5,
  RedaccionEscrito = 5
}

export enum AIRolMensaje {
  System = 1,
  User = 2,
  Assistant = 3
}

export interface AIChatRequestDto {
  conversationId?: string;
  expedienteId?: string;
  mensaje: string;
  casoUso?: AICasoUso;
  streaming?: boolean;
}

export interface AIChatResponseDto {
  conversationId: string;
  messageId: string;
  contenido: string;
  disclaimer: string;
  tokensEntrada?: number;
  tokensSalida?: number;
  duracionMs?: number;
  modelId?: string;
  providerId?: string;
  finishReason?: string;
  contextoAutorizado: boolean;
}

export interface AIConversationSummaryDto {
  id: string;
  titulo: string;
  casoUso: AICasoUso;
  expedienteId?: string;
  numeroExpediente?: string;
  usuarioId: string;
  createdAt: string;
  updatedAt: string;
  mensajesCount: number;
}

export interface AIConversationDetailDto {
  id: string;
  titulo: string;
  casoUso: AICasoUso;
  expedienteId?: string;
  numeroExpediente?: string;
  usuarioId: string;
  createdAt: string;
  updatedAt: string;
  mensajes: AIMessageDto[];
}

export interface AIMessageDto {
  id: string;
  rol: AIRolMensaje;
  contenido: string;
  createdAt: string;
  disclaimer: string;
  contextoAutorizado: boolean;
  tokensEntrada?: number;
  tokensSalida?: number;
}

export interface AISummarizeRequestDto {
  expedienteId: string;
  documentoIds?: string[];
  enfoque?: string;
}

export interface AIExtractRequestDto {
  documentoId: string;
}

export interface AIExtractResponseDto {
  documentoId: string;
  estadoIa: number;
  propuestaExtraccionJson: string;
  advertencia: string;
}

export interface AIDraftRequestDto {
  expedienteId?: string;
  tipoEscrito: string;
  instrucciones: string;
  contextoDocumentoIds?: string[];
}

export interface AIConsumoQueryDto {
  fechaInicio: string;
  fechaFin: string;
  usuarioId?: string;
}

export interface AIConsumoResponseDto {
  totalInvocaciones: number;
  totalTokensEntrada: number;
  totalTokensSalida: number;
  totalTokens: number;
  costoEstimadoUsdTotal: number;
  desglosePorCasoUso: Record<string, number>;
  desglosePorUsuario: AIConsumoUsuarioDto[];
  periodoDias: number;
}

export interface AIConsumoUsuarioDto {
  usuarioId: string;
  nombreUsuario: string;
  invocaciones: number;
  totalTokens: number;
  costoEstimadoUsd: number;
}

export interface AIProviderCapabilities {
  maxContextTokens: number;
  maxOutputTokens: number;
  supportsStreaming: boolean;
  supportsSystemPrompt: boolean;
  supportedModels: string[];
}

export interface AIChatCompletionChunk {
  deltaContent?: string;
  finishReason?: string;
  promptTokens?: number;
  completionTokens?: number;
}
