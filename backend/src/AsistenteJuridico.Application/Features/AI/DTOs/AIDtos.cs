using System;
using System.Collections.Generic;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.AI.DTOs;

public record AIChatRequestDto(
    Guid? ConversationId,
    Guid? ExpedienteId,
    string Mensaje,
    AICasoUso CasoUso = AICasoUso.ChatLibre,
    bool Streaming = false
);

public record CreateAIConversationDto(
    Guid? ExpedienteId = null,
    string? Titulo = null,
    AICasoUso CasoUso = AICasoUso.ChatLibre
);

public record SendMessageToConversationDto(
    string Mensaje,
    bool Streaming = false
);

public record AISummarizeDocumentoDto(
    Guid DocumentoId,
    string? Enfoque = null
);

public record AIChatResponseDto(
    Guid ConversationId,
    Guid MessageId,
    string Contenido,
    string Disclaimer,
    int? TokensEntrada,
    int? TokensSalida,
    int? DuracionMs,
    string? ModelId,
    string? ProviderId,
    string? FinishReason,
    bool ContextoAutorizado
);

public record AIConversationSummaryDto(
    Guid Id,
    string Titulo,
    AICasoUso CasoUso,
    Guid? ExpedienteId,
    string? NumeroExpediente,
    Guid UsuarioId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int MensajesCount
);

public record AIConversationDetailDto(
    Guid Id,
    string Titulo,
    AICasoUso CasoUso,
    Guid? ExpedienteId,
    string? NumeroExpediente,
    Guid UsuarioId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AIMessageDto> Mensajes
);

public record AIMessageDto(
    Guid Id,
    AIRolMensaje Rol,
    string Contenido,
    DateTime CreatedAt,
    string Disclaimer,
    bool ContextoAutorizado,
    int? TokensEntrada,
    int? TokensSalida
);

public record AISummarizeRequestDto(
    Guid ExpedienteId,
    IReadOnlyList<Guid>? DocumentoIds = null,
    string? Enfoque = null
);

public record AIExtractRequestDto(
    Guid DocumentoId
);

public record AIExtractResponseDto(
    Guid DocumentoId,
    EstadoProcesamientoIa EstadoIa,
    string PropuestaExtraccionJson,
    string Advertencia
);

public record AIDraftRequestDto(
    Guid? ExpedienteId,
    string TipoEscrito,
    string Instrucciones,
    IReadOnlyList<Guid>? ContextoDocumentoIds = null
);

public record AIConsumoQueryDto(
    DateTime FechaInicio,
    DateTime FechaFin,
    Guid? UsuarioId = null
);

public record AIConsumoResponseDto(
    int TotalInvocaciones,
    int TotalTokensEntrada,
    int TotalTokensSalida,
    int TotalTokens,
    decimal CostoEstimadoUsdTotal,
    IReadOnlyDictionary<string, int> DesglosePorCasoUso,
    IReadOnlyList<AIConsumoUsuarioDto> DesglosePorUsuario,
    int PeriodoDias
);

public record AIConsumoUsuarioDto(
    Guid UsuarioId,
    string NombreUsuario,
    int Invocaciones,
    int TotalTokens,
    decimal CostoEstimadoUsd
);
