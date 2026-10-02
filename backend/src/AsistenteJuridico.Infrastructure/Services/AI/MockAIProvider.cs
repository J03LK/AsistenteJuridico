using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Proveedor de IA Mock determinista para pruebas de conformidad, integración y entornos locales.
/// </summary>
public class MockAIProvider : IAIProvider
{
    private readonly ILogger<MockAIProvider> _logger;
    private readonly int _maxContextTokens;
    private readonly int _simulateLatencyMs;
    private readonly bool _supportsStructuredOutput;

    public MockAIProvider(ILogger<MockAIProvider> logger, int maxContextTokens = 16384, int simulateLatencyMs = 10, bool supportsStructuredOutput = true)
    {
        _logger = logger;
        _maxContextTokens = maxContextTokens;
        _simulateLatencyMs = simulateLatencyMs;
        _supportsStructuredOutput = supportsStructuredOutput;
    }

    public string ProviderId => "mock-ai-provider";

    public AIProviderCapabilities Capabilities => new(
        MaxContextTokens: _maxContextTokens,
        MaxOutputTokens: 4096,
        SupportsStreaming: true,
        SupportsSystemPrompt: true,
        SupportedModels: new[] { "mock-legal-v1", "mock-legal-fast" },
        SupportsStructuredOutput: _supportsStructuredOutput
    );

    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // Regla heurística de tokenización para lenguas romances / español: ~4 caracteres por token
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    public async Task<AIChatCompletionResponse> CompleteChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_simulateLatencyMs > 0)
        {
            await Task.Delay(_simulateLatencyMs, cancellationToken);
        }

        var (content, finishReason) = GenerateDeterministicResponse(request);

        var promptTokens = 0;
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            promptTokens += EstimateTokens(request.SystemPrompt);

        foreach (var msg in request.Messages)
        {
            promptTokens += EstimateTokens(msg.Content);
        }

        var completionTokens = EstimateTokens(content);
        var totalTokens = promptTokens + completionTokens;

        return new AIChatCompletionResponse(
            Content: content,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            TotalTokens: totalTokens,
            FinishReason: finishReason,
            ModelId: request.ModelId ?? "mock-legal-v1",
            ProviderId: ProviderId
        );
    }

    public async IAsyncEnumerable<AIChatCompletionChunk> StreamChatAsync(
        AIChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (fullResponse, finishReason) = GenerateDeterministicResponse(request);

        var promptTokens = 0;
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            promptTokens += EstimateTokens(request.SystemPrompt);

        foreach (var msg in request.Messages)
        {
            promptTokens += EstimateTokens(msg.Content);
        }

        var words = fullResponse.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var accumulatedLength = 0;

        for (int i = 0; i < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_simulateLatencyMs > 0)
            {
                await Task.Delay(Math.Min(15, _simulateLatencyMs), cancellationToken);
            }

            var chunkText = (i == 0 ? "" : " ") + words[i];
            accumulatedLength += chunkText.Length;

            var isLast = i == words.Length - 1;
            yield return new AIChatCompletionChunk(
                DeltaContent: chunkText,
                FinishReason: isLast ? finishReason : null,
                PromptTokens: isLast ? promptTokens : null,
                CompletionTokens: isLast ? EstimateTokens(fullResponse) : null
            );
        }
    }

    private (string Content, AIFinishReason FinishReason) GenerateDeterministicResponse(AIChatCompletionRequest request)
    {
        var lastMessage = request.Messages.Count > 0 ? request.Messages[^1].Content : "";

        // Detección de intenciones basada en la directiva específica
        if (request.SystemPrompt != null && request.SystemPrompt.Contains("INSTRUCCIÓN ESPECÍFICA (Extracción de Hechos Procesales)", StringComparison.OrdinalIgnoreCase))
        {
            var extractionJson = JsonSerializer.Serialize(new
            {
                tipo_propuesta = "extraccion_hechos_procesales",
                fecha_propuesta_utc = "2026-10-01T00:00:00.0000000Z",
                partes_identificadas = new[]
                {
                    new { rol = "Actor", nombre = "Corporación Andina de Comercio S.A." },
                    new { rol = "Demandado", nombre = "Constructora Pacífico Cía. Ltda." }
                },
                hechos_relevantes = new[]
                {
                    "Celebración de contrato de obra material el 15 de marzo de 2024.",
                    "Incumplimiento de entrega en el plazo estipulado (30 de junio de 2024).",
                    "Notificación de requerimiento de mora el 10 de julio de 2024."
                },
                cuantia_estimada_usd = 45000.00,
                normativa_citada = new[] { "Art. 1561 Código Civil", "Art. 142 COGEP" },
                plazo_observaciones_dias = 10,
                advertencia = "Esta es una propuesta estructurada generada por IA. No modifica automáticamente el expediente hasta su confirmación por el abogado responsable."
            }, new JsonSerializerOptions { WriteIndented = true });

            return (extractionJson, AIFinishReason.Stop);
        }

        if (request.SystemPrompt != null && request.SystemPrompt.Contains("INSTRUCCIÓN ESPECÍFICA (Resumen de Expediente)", StringComparison.OrdinalIgnoreCase))
        {
            var resumen = "SÍNTESIS PROCESAL:\n\n" +
                          "1. PARTES: Actor: Corporación Andina de Comercio S.A. | Demandado: Constructora Pacífico Cía. Ltda.\n" +
                          "2. OBJETO DE LA CONTROVERSIA: Demanda ordinaria por incumplimiento contractual y cobro de cláusula penal.\n" +
                          "3. ANTECEDENTES RELEVANTES: Contrato suscrito en Guayaquil, obra inconclusa al término del plazo.\n" +
                          "4. ESTADO PROCESAL ACTUAL: Calificación de demanda admitida a trámite, pendiente citación.\n" +
                          "5. PRÓXIMOS TÉRMINOS: Término de 30 días para contestación a la demanda conforme al Art. 291 del COGEP.";
            return (resumen, AIFinishReason.Stop);
        }

        if (request.SystemPrompt != null && request.SystemPrompt.Contains("INSTRUCCIÓN ESPECÍFICA (Redacción de Escrito)", StringComparison.OrdinalIgnoreCase))
        {
            var escrito = "SEÑOR JUEZ DE LA UNIDAD JUDICIAL CIVIL CON SEDE EN LA CIUDAD DE QUITO, PROVINCIA DE PICHINCHA\n\n" +
                          "JUICIO No.: [POR ASIGNAR]\n\n" +
                          "Comparezco respetuosamente y expongo:\n\n" +
                          "I. GENERALES DE LEY\n" +
                          "Comparece el Dr./Ab. en calidad de procurador judicial...\n\n" +
                          "II. FUNDAMENTOS DE HECHO\n" +
                          "Conforme a los antecedentes documentales adjuntos al proceso...\n\n" +
                          "III. FUNDAMENTOS DE DERECHO\n" +
                          "Fundamento la presente petición en lo dispuesto en los Artículos 75 y 76 de la Constitución de la República del Ecuador, en concordancia con el Art. 142 del Código Orgánico General de Procesos (COGEP).\n\n" +
                          "IV. PETICIÓN CONCRETA\n" +
                          "Por lo expuesto, solicito se sirva calificar el presente escrito y proveer lo que en derecho corresponda.\n\n" +
                          "Notificaciones que me correspondan las recibiré en la casilla judicial y correo electrónico señalados.";
            return (escrito, AIFinishReason.Stop);
        }

        // Respuesta estándar jurídica
        var respuesta = $"Con base en la consulta jurídica planteada y en conformidad con el ordenamiento jurídico ecuatoriano:\n\n" +
                        $"Respecto a '{lastMessage.Trim()}': " +
                        $"El Artículo 76 numeral 7 de la Constitución de la República del Ecuador garantiza el derecho a la defensa y a la debida motivación judicial. " +
                        $"En el ámbito adjetivo, el COGEP establece las reglas procesales pertinentes para la sustanciación oportuna de los actos y términos judiciales.";

        return (respuesta, AIFinishReason.Stop);
    }
}
