using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Features.AI.DTOs;

namespace AsistenteJuridico.Application.Features.AI.Interfaces;

/// <summary>
/// Contrato del servicio principal de orquestación de Asistente Jurídico IA.
/// </summary>
public interface IAIService
{
    Task<AIChatResponseDto> SendMessageAsync(AIChatRequestDto dto, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AIChatCompletionChunk> StreamMessageAsync(AIChatRequestDto dto, CancellationToken cancellationToken = default);

    Task<AIConversationDetailDto> CreateConversationAsync(CreateAIConversationDto dto, CancellationToken cancellationToken = default);

    Task<AIConversationDetailDto> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AIConversationSummaryDto>> GetConversationsAsync(Guid? expedienteId = null, CancellationToken cancellationToken = default);

    Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<AIChatResponseDto> SummarizeExpedienteAsync(AISummarizeRequestDto dto, CancellationToken cancellationToken = default);

    Task<AIChatResponseDto> SummarizeDocumentoAsync(AISummarizeDocumentoDto dto, CancellationToken cancellationToken = default);

    Task<AIExtractResponseDto> ExtractFromDocumentAsync(AIExtractRequestDto dto, CancellationToken cancellationToken = default);

    Task<AIChatResponseDto> DraftEscritoAsync(AIDraftRequestDto dto, CancellationToken cancellationToken = default);

    Task<AIConsumoResponseDto> GetConsumoAsync(AIConsumoQueryDto query, CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken = default);
}
