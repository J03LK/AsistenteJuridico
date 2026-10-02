using System.Collections.Generic;

namespace AsistenteJuridico.Application.Common.Interfaces.AI;

/// <summary>
/// Capacidades y límites declarados por un proveedor de IA.
/// Permite validar las ventanas de contexto y restricciones sin acoplarse a un modelo comercial específico.
/// </summary>
public record AIProviderCapabilities(
    int MaxContextTokens,
    int MaxOutputTokens,
    bool SupportsStreaming,
    bool SupportsSystemPrompt,
    IReadOnlyList<string> SupportedModels,
    bool SupportsStructuredOutput = true
);
