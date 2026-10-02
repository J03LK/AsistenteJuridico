using System.Collections.Generic;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Common.Interfaces.AI;

public record AIChatMessageDto(
    AIRolMensaje Role,
    string Content
);

public record AIChatCompletionRequest(
    string ModelId,
    IReadOnlyList<AIChatMessageDto> Messages,
    double Temperature = 0.2,
    int? MaxTokens = null,
    string? SystemPrompt = null
);

public record AIChatCompletionResponse(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    AIFinishReason FinishReason,
    string ModelId,
    string ProviderId
);

public record AIChatCompletionChunk(
    string? DeltaContent,
    AIFinishReason? FinishReason = null,
    int? PromptTokens = null,
    int? CompletionTokens = null
);
