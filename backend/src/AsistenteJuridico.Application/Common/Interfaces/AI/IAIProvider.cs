using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AsistenteJuridico.Application.Common.Interfaces.AI;

/// <summary>
/// Contrato agnóstico de proveedor de Inteligencia Artificial (Clean Architecture).
/// Desacopla la lógica de negocio de los proveedores comerciales específicos.
/// </summary>
public interface IAIProvider
{
    /// <summary>
    /// Identificador del proveedor (ej: "mock", "openai-compatible").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Capacidades dinámicas del proveedor.
    /// </summary>
    AIProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Ejecuta una inferencia completa de forma no sincrónica (bloqueante).
    /// </summary>
    Task<AIChatCompletionResponse> CompleteChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transmite la respuesta en tiempo real mediante Server-Sent Events / chunks.
    /// </summary>
    IAsyncEnumerable<AIChatCompletionChunk> StreamChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Estima la cantidad de tokens que ocupará un texto sin realizar una llamada externa.
    /// </summary>
    int EstimateTokens(string text);
}
