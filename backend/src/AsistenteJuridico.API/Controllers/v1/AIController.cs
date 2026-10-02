using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.AI.DTOs;
using AsistenteJuridico.Application.Features.AI.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AsistenteJuridico.API.Controllers.v1;

/// <summary>
/// Controlador principal de Inteligencia Artificial para Asistente Jurídico.
/// Cumple estrictamente con el contrato v1.1.1 aprobado, multi-tenant isolation y PBAC.
/// </summary>
[ApiController]
[Route("api/v1/ai")]
[Authorize]
[EnableRateLimiting("AIRateLimit")]
public class AIController : ControllerBase
{
    private readonly IAIService _aiService;
    private readonly IAIProvider _aiProvider;

    public AIController(IAIService aiService, IAIProvider aiProvider)
    {
        _aiService = aiService;
        _aiProvider = aiProvider;
    }

    /// <summary>
    /// Crea una nueva conversación de IA asociada opcionalmente a un expediente.
    /// Contrato Canónico v1.1.1: POST /api/v1/ai/conversaciones
    /// </summary>
    [HttpPost("conversaciones")]
    [Authorize(Policy = Permissions.AIChat)]
    [ProducesResponseType(typeof(ApiResponse<AIConversationDetailDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateConversacion([FromBody] CreateAIConversationDto request, CancellationToken cancellationToken)
    {
        var result = await _aiService.CreateConversationAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<AIConversationDetailDto>.Ok(result, "Conversación creada exitosamente."));
    }

    /// <summary>
    /// Lista el historial de conversaciones accesibles según las reglas PBAC del usuario autenticado.
    /// Contrato Canónico v1.1.1: GET /api/v1/ai/conversaciones
    /// </summary>
    [HttpGet("conversaciones")]
    [Authorize(Policy = Permissions.AIChat)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AIConversationSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConversaciones([FromQuery] Guid? expedienteId, CancellationToken cancellationToken)
    {
        var result = await _aiService.GetConversationsAsync(expedienteId, cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<AIConversationSummaryDto>>.Ok(result));
    }

    /// <summary>
    /// Obtiene el detalle de una conversación y su historial completo de mensajes.
    /// Contrato Canónico v1.1.1: GET /api/v1/ai/conversaciones/{id}
    /// </summary>
    [HttpGet("conversaciones/{id:guid}")]
    [Authorize(Policy = Permissions.AIChat)]
    [ProducesResponseType(typeof(ApiResponse<AIConversationDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConversacion([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await _aiService.GetConversationAsync(id, cancellationToken);
        return Ok(ApiResponse<AIConversationDetailDto>.Ok(result));
    }

    /// <summary>
    /// Borrado lógico de una conversación asistida por IA.
    /// Contrato Canónico v1.1.1: DELETE /api/v1/ai/conversaciones/{id}
    /// </summary>
    [HttpDelete("conversaciones/{id:guid}")]
    [Authorize(Policy = Permissions.AIChat)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteConversacion([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        await _aiService.DeleteConversationAsync(id, cancellationToken);
        return Ok(ApiResponse<bool>.Ok(true, "Conversación eliminada exitosamente."));
    }

    /// <summary>
    /// Envía un mensaje a una conversación específica existente. Soporta JSON y streaming SSE.
    /// Contrato Canónico v1.1.1: POST /api/v1/ai/conversaciones/{id}/mensajes
    /// </summary>
    [HttpPost("conversaciones/{id:guid}/mensajes")]
    [Authorize(Policy = Permissions.AIChat)]
    public async Task<IActionResult> SendMensajeToConversacion([FromRoute] Guid id, [FromBody] SendMessageToConversationDto request, CancellationToken cancellationToken)
    {
        var chatRequest = new AIChatRequestDto(
            ConversationId: id,
            ExpedienteId: null,
            Mensaje: request.Mensaje,
            Streaming: request.Streaming
        );
        return await HandleChatMessageAsync(chatRequest, cancellationToken);
    }

    /// <summary>
    /// Envía un mensaje al Asistente Jurídico IA. Soporta respuestas completas (JSON) y streaming en tiempo real (SSE).
    /// Endpoint compatible para chat con o sin conversación previa.
    /// Alias compatible: POST /api/v1/ai/chat
    /// </summary>
    [HttpPost("chat")]
    [Authorize(Policy = Permissions.AIChat)]
    public async Task<IActionResult> Chat([FromBody] AIChatRequestDto request, CancellationToken cancellationToken)
    {
        return await HandleChatMessageAsync(request, cancellationToken);
    }

    /// <summary>
    /// Genera un resumen procesal estructurado de un expediente jurídico.
    /// Contrato Canónico v1.1.1: POST /api/v1/ai/resumir-expediente
    /// </summary>
    [HttpPost("resumir-expediente")]
    [Authorize(Policy = Permissions.AISummarize)]
    [ProducesResponseType(typeof(ApiResponse<AIChatResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResumirExpediente([FromBody] AISummarizeRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _aiService.SummarizeExpedienteAsync(request, cancellationToken);
        return Ok(ApiResponse<AIChatResponseDto>.Ok(result));
    }

    /// <summary>
    /// Genera un resumen procesal y jurídico de un documento específico.
    /// Contrato Canónico v1.1.1: POST /api/v1/ai/resumir-documento
    /// </summary>
    [HttpPost("resumir-documento")]
    [Authorize(Policy = Permissions.AISummarize)]
    [ProducesResponseType(typeof(ApiResponse<AIChatResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResumirDocumento([FromBody] AISummarizeDocumentoDto request, CancellationToken cancellationToken)
    {
        var result = await _aiService.SummarizeDocumentoAsync(request, cancellationToken);
        return Ok(ApiResponse<AIChatResponseDto>.Ok(result));
    }

    /// <summary>
    /// Analiza un documento y genera una propuesta estructurada de extracción de hechos procesales.
    /// El resultado se almacena exclusivamente en MetadatosJson del documento sin alterar el expediente.
    /// Contrato Canónico v1.1.1: POST /api/v1/ai/extraer-datos-documento (con alias compatible extraer-documento)
    /// </summary>
    [HttpPost("extraer-datos-documento")]
    [HttpPost("extraer-documento")]
    [Authorize(Policy = Permissions.AIExtract)]
    [ProducesResponseType(typeof(ApiResponse<AIExtractResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExtraerDocumento([FromBody] AIExtractRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _aiService.ExtractFromDocumentAsync(request, cancellationToken);
        return Ok(ApiResponse<AIExtractResponseDto>.Ok(result));
    }

    /// <summary>
    /// Genera un borrador formal de escrito judicial adaptado al estilo forense ecuatoriano.
    /// Contrato Canónico v1.1.1: POST /api/v1/ai/generar-borrador (con alias compatible redactar-escrito)
    /// </summary>
    [HttpPost("generar-borrador")]
    [HttpPost("redactar-escrito")]
    [Authorize(Policy = Permissions.AIDraft)]
    [ProducesResponseType(typeof(ApiResponse<AIChatResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RedactarEscrito([FromBody] AIDraftRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _aiService.DraftEscritoAsync(request, cancellationToken);
        return Ok(ApiResponse<AIChatResponseDto>.Ok(result));
    }

    /// <summary>
    /// Consulta agregada de consumo y métricas de invocaciones de IA (máximo 90 días).
    /// Contrato Canónico v1.1.1: GET /api/v1/ai/consumo
    /// </summary>
    [HttpGet("consumo")]
    [Authorize(Policy = Permissions.AIUsageRead)]
    [ProducesResponseType(typeof(ApiResponse<AIConsumoResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConsumo([FromQuery] DateTime fechaInicio, [FromQuery] DateTime fechaFin, [FromQuery] Guid? usuarioId, CancellationToken cancellationToken)
    {
        var query = new AIConsumoQueryDto(fechaInicio, fechaFin, usuarioId);
        var result = await _aiService.GetConsumoAsync(query, cancellationToken);
        return Ok(ApiResponse<AIConsumoResponseDto>.Ok(result));
    }

    /// <summary>
    /// Endpoint Auxiliar / Diagnóstico: Consulta las capacidades y límites del proveedor de IA activo.
    /// </summary>
    [HttpGet("capacidades")]
    [Authorize(Policy = Permissions.AIChat)]
    [ProducesResponseType(typeof(ApiResponse<AIProviderCapabilities>), StatusCodes.Status200OK)]
    public IActionResult GetCapacidades()
    {
        return Ok(ApiResponse<AIProviderCapabilities>.Ok(_aiProvider.Capabilities));
    }

    /// <summary>
    /// Endpoint Administrativo: Tarea administrativa de purga física de conversaciones que han superado el período de retención.
    /// Protegido estrictamente para Administrador de Estudio (AdminEstudio).
    /// NUNCA elimina ni muta registros de ai_usage_logs.
    /// </summary>
    [HttpPost("mantenimiento/purgar")]
    [Authorize(Roles = Roles.AdminEstudio)]
    [ProducesResponseType(typeof(ApiResponse<int>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PurgarExpiradas(CancellationToken cancellationToken)
    {
        var purged = await _aiService.PurgeExpiredConversationsAsync(cancellationToken);
        return Ok(ApiResponse<int>.Ok(purged, $"Se purgaron exitosamente {purged} registros de conversaciones."));
    }

    /// <summary>
    /// Protocolo de streaming (POST + SSE):
    /// - Cada fragmento se emite como <c>data: {json}</c> y el cierre normal como <c>data: [DONE]</c>.
    /// - Si el fallo ocurre ANTES del primer fragmento (validación, permisos, proveedor caído), la respuesta
    ///   aún no ha comenzado y se devuelve el error HTTP normal con el envelope ApiResponse (p. ej. 502).
    /// - Si el fallo ocurre DESPUÉS de iniciado el stream, el código HTTP 200 ya fue enviado: se emite
    ///   <c>event: error</c> con <c>data: {"code","message"}</c> y el stream termina SIN <c>[DONE]</c>.
    /// La cancelación del cliente detiene el envío local; no garantiza costo cero ante el proveedor.
    /// </summary>
    private async Task<IActionResult> HandleChatMessageAsync(AIChatRequestDto request, CancellationToken cancellationToken)
    {
        if (request.Streaming)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            try
            {
                await foreach (var chunk in _aiService.StreamMessageAsync(request, cancellationToken))
                {
                    PrepareEventStreamResponse();
                    var chunkJson = JsonSerializer.Serialize(chunk, jsonOptions);
                    await Response.WriteAsync($"data: {chunkJson}\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }

                PrepareEventStreamResponse();
                await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Conexión cancelada por el cliente (AbortController); no relanzar error HTTP
            }
            catch (Exception ex) when (Response.HasStarted)
            {
                var providerException = ex as AIProviderException;
                var errorJson = JsonSerializer.Serialize(new
                {
                    code = providerException?.ErrorCode ?? "STREAM_INTERRUPTED",
                    message = providerException?.Message ?? "La transmisión se interrumpió por un error interno del servidor."
                }, jsonOptions);

                await Response.WriteAsync($"event: error\ndata: {errorJson}\n\n", CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }

            return new EmptyResult();
        }

        var result = await _aiService.SendMessageAsync(request, cancellationToken);
        return Ok(ApiResponse<AIChatResponseDto>.Ok(result));
    }

    private void PrepareEventStreamResponse()
    {
        if (Response.HasStarted)
        {
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
    }
}
