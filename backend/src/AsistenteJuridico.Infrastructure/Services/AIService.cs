using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Helpers;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.AI.DTOs;
using AsistenteJuridico.Application.Features.AI.Interfaces;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Orquestador central de las capacidades de Asistente Jurídico IA.
/// Cumple estrictamente con Clean Architecture, multi-tenant isolation, PBAC, inmutabilidad de AIUsageLog
/// y concurrencia optimista mediante xmin en Documento.
/// </summary>
public class AIService : IAIService
{
    private readonly ApplicationDbContext _context;
    private readonly IAIProvider _aiProvider;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IExpedienteAccessService _expedienteAccessService;
    private readonly ILogger<AIService> _logger;
    private readonly IDocumentTextExtractor _textExtractor;
    private readonly IAuditService _auditService;

    /// <param name="textExtractor">Fase 6.X (H10). Si no se indica, el extractor real sobre <paramref name="fileStorageService"/>.</param>
    /// <param name="auditService">Fase 6.X (D12). Si no se indica, AuditService sobre el mismo contexto.</param>
    public AIService(
        ApplicationDbContext context,
        IAIProvider aiProvider,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IExpedienteAccessService expedienteAccessService,
        IFileStorageService fileStorageService,
        ILogger<AIService> logger,
        IDocumentTextExtractor? textExtractor = null,
        IAuditService? auditService = null)
    {
        _context = context;
        _aiProvider = aiProvider;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _expedienteAccessService = expedienteAccessService;
        _logger = logger;
        _textExtractor = textExtractor ?? new DocumentTextExtractor(fileStorageService);
        _auditService = auditService ?? new AuditService(context, currentUserService, currentTenantService, new HttpContextAccessor());
    }

    public async Task<AIChatResponseDto> SendMessageAsync(AIChatRequestDto dto, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        ContextWindowValidator.ValidateUserInputLength(dto.Mensaje);
        var tenantId = GetCurrentTenantId();
        var currentUserId = GetCurrentUserId();

        AIConversation conversation;
        if (dto.ConversationId.HasValue)
        {
            conversation = await _context.AIConversations
                .Include(c => c.Mensajes.OrderBy(m => m.CreatedAt))
                .FirstOrDefaultAsync(c => c.Id == dto.ConversationId.Value && !c.IsDeleted, cancellationToken)
                ?? throw new NotFoundException(nameof(AIConversation), dto.ConversationId.Value);

            EnsureConversationAccess(conversation, currentUserId);

            if (conversation.ExpedienteId.HasValue)
            {
                await _expedienteAccessService.EnsureCanAccessExpedienteAsync(conversation.ExpedienteId.Value, false, cancellationToken);
            }
        }
        else
        {
            if (dto.ExpedienteId.HasValue)
            {
                await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId.Value, false, cancellationToken);
            }

            conversation = new AIConversation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UsuarioId = currentUserId,
                ExpedienteId = dto.ExpedienteId,
                Titulo = GenerateConversationTitle(dto.Mensaje),
                CasoUso = dto.CasoUso,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.AIConversations.Add(conversation);
        }

        var sanitizedUserText = PromptSanitizer.SanitizeUserInput(dto.Mensaje);
        var systemPrompt = LegalPromptBuilder.BuildSystemPrompt(conversation.CasoUso);

        // Estimar tokens y validar ventana de contexto total sin truncamiento silencioso
        var promptMessages = new List<AIChatMessageDto>();
        foreach (var msg in conversation.Mensajes)
        {
            promptMessages.Add(new AIChatMessageDto(msg.Rol, msg.Contenido));
        }
        promptMessages.Add(new AIChatMessageDto(AIRolMensaje.User, sanitizedUserText));

        var estimatedTokens = _aiProvider.EstimateTokens(systemPrompt);
        foreach (var msg in promptMessages)
        {
            estimatedTokens += _aiProvider.EstimateTokens(msg.Content);
        }

        ContextWindowValidator.ValidateContextTokens(estimatedTokens, _aiProvider.Capabilities.MaxContextTokens);

        // Registrar mensaje de usuario
        var userMessage = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversation.Id,
            Rol = AIRolMensaje.User,
            Contenido = sanitizedUserText,
            SystemPromptVersion = LegalPromptBuilder.CurrentSystemPromptVersion,
            TokensEntrada = _aiProvider.EstimateTokens(sanitizedUserText),
            CreatedAt = DateTime.UtcNow
        };
        _context.AIMessages.Add(userMessage);
        await _context.SaveChangesAsync(cancellationToken);

        // Invocar proveedor IA
        var sw = Stopwatch.StartNew();
        var request = new AIChatCompletionRequest(
            ModelId: _aiProvider.Capabilities.SupportedModels.FirstOrDefault() ?? "mock-legal-v1",
            Messages: promptMessages,
            SystemPrompt: systemPrompt
        );

        var response = await InvokeProviderAsync(
            request, sw, tenantId, currentUserId, conversation.Id, conversation.CasoUso, estimatedTokens, cancellationToken);

        sw.Stop();

        // Registrar mensaje de asistente
        var assistantMessage = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversation.Id,
            Rol = AIRolMensaje.Assistant,
            Contenido = response.Content,
            SystemPromptVersion = LegalPromptBuilder.CurrentSystemPromptVersion,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            ModelId = response.ModelId,
            ProviderId = response.ProviderId,
            FinishReason = response.FinishReason.ToString(),
            ContextoAutorizado = conversation.ExpedienteId.HasValue,
            Disclaimer = LegalPromptBuilder.DeontologicalDisclaimer,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIMessages.Add(assistantMessage);

        // Registrar consumo en AIUsageLog inmutable
        var usageLog = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ConversationId = conversation.Id,
            CasoUso = conversation.CasoUso,
            ProviderId = response.ProviderId,
            ModelId = response.ModelId,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            TotalTokens = response.TotalTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            CostoEstimadoUsd = CalculateCost(response.PromptTokens, response.CompletionTokens),
            Exitoso = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIUsageLogs.Add(usageLog);

        conversation.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new AIChatResponseDto(
            ConversationId: conversation.Id,
            MessageId: assistantMessage.Id,
            Contenido: assistantMessage.Contenido,
            Disclaimer: assistantMessage.Disclaimer,
            TokensEntrada: assistantMessage.TokensEntrada,
            TokensSalida: assistantMessage.TokensSalida,
            DuracionMs: assistantMessage.DuracionMs,
            ModelId: assistantMessage.ModelId,
            ProviderId: assistantMessage.ProviderId,
            FinishReason: assistantMessage.FinishReason,
            ContextoAutorizado: assistantMessage.ContextoAutorizado
        );
    }

    public async IAsyncEnumerable<AIChatCompletionChunk> StreamMessageAsync(
        AIChatRequestDto dto,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        ContextWindowValidator.ValidateUserInputLength(dto.Mensaje);
        var tenantId = GetCurrentTenantId();
        var currentUserId = GetCurrentUserId();

        AIConversation conversation;
        if (dto.ConversationId.HasValue)
        {
            conversation = await _context.AIConversations
                .Include(c => c.Mensajes.OrderBy(m => m.CreatedAt))
                .FirstOrDefaultAsync(c => c.Id == dto.ConversationId.Value && !c.IsDeleted, cancellationToken)
                ?? throw new NotFoundException(nameof(AIConversation), dto.ConversationId.Value);

            EnsureConversationAccess(conversation, currentUserId);

            if (conversation.ExpedienteId.HasValue)
            {
                await _expedienteAccessService.EnsureCanAccessExpedienteAsync(conversation.ExpedienteId.Value, false, cancellationToken);
            }
        }
        else
        {
            if (dto.ExpedienteId.HasValue)
            {
                await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId.Value, false, cancellationToken);
            }

            conversation = new AIConversation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UsuarioId = currentUserId,
                ExpedienteId = dto.ExpedienteId,
                Titulo = GenerateConversationTitle(dto.Mensaje),
                CasoUso = dto.CasoUso,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.AIConversations.Add(conversation);
        }

        var sanitizedUserText = PromptSanitizer.SanitizeUserInput(dto.Mensaje);
        var systemPrompt = LegalPromptBuilder.BuildSystemPrompt(conversation.CasoUso);

        var promptMessages = new List<AIChatMessageDto>();
        foreach (var msg in conversation.Mensajes)
        {
            promptMessages.Add(new AIChatMessageDto(msg.Rol, msg.Contenido));
        }
        promptMessages.Add(new AIChatMessageDto(AIRolMensaje.User, sanitizedUserText));

        var estimatedTokens = _aiProvider.EstimateTokens(systemPrompt);
        foreach (var msg in promptMessages)
        {
            estimatedTokens += _aiProvider.EstimateTokens(msg.Content);
        }

        ContextWindowValidator.ValidateContextTokens(estimatedTokens, _aiProvider.Capabilities.MaxContextTokens);

        // Guardar mensaje del usuario antes de iniciar streaming
        var userMessage = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversation.Id,
            Rol = AIRolMensaje.User,
            Contenido = sanitizedUserText,
            SystemPromptVersion = LegalPromptBuilder.CurrentSystemPromptVersion,
            TokensEntrada = _aiProvider.EstimateTokens(sanitizedUserText),
            CreatedAt = DateTime.UtcNow
        };
        _context.AIMessages.Add(userMessage);
        await _context.SaveChangesAsync(cancellationToken);

        var sw = Stopwatch.StartNew();
        var modelId = _aiProvider.Capabilities.SupportedModels.FirstOrDefault() ?? "mock-legal-v1";
        var request = new AIChatCompletionRequest(
            ModelId: modelId,
            Messages: promptMessages,
            SystemPrompt: systemPrompt
        );

        var fullResponseBuilder = new System.Text.StringBuilder();
        int? finalPromptTokens = null;
        int? finalCompletionTokens = null;
        AIFinishReason finalFinishReason = AIFinishReason.Stop;

        // El fallo del proveedor puede ocurrir al abrir el stream o a mitad de la transmisión:
        // en ambos casos se registra el intento fallido y se normaliza a AIProviderException.
        IAsyncEnumerator<AIChatCompletionChunk>? enumerator = null;
        try
        {
            while (true)
            {
                AIChatCompletionChunk chunk;
                try
                {
                    enumerator ??= _aiProvider.StreamChatAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }
                    chunk = enumerator.Current;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    await RegisterFailedUsageAsync(
                        tenantId, currentUserId, conversation.Id, conversation.CasoUso, modelId,
                        estimatedTokens, _aiProvider.EstimateTokens(fullResponseBuilder.ToString()), (int)sw.ElapsedMilliseconds, ex);

                    if (TryNormalizeProviderFailure(ex, conversation.CasoUso, cancellationToken, out var providerException))
                    {
                        throw providerException;
                    }
                    throw;
                }

                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                {
                    fullResponseBuilder.Append(chunk.DeltaContent);
                }
                if (chunk.FinishReason.HasValue)
                {
                    finalFinishReason = chunk.FinishReason.Value;
                }
                if (chunk.PromptTokens.HasValue)
                {
                    finalPromptTokens = chunk.PromptTokens.Value;
                }
                if (chunk.CompletionTokens.HasValue)
                {
                    finalCompletionTokens = chunk.CompletionTokens.Value;
                }

                yield return chunk;
            }
        }
        finally
        {
            if (enumerator != null)
            {
                await enumerator.DisposeAsync();
            }
        }

        sw.Stop();
        var fullText = fullResponseBuilder.ToString();
        var promptTok = finalPromptTokens ?? estimatedTokens;
        var completionTok = finalCompletionTokens ?? _aiProvider.EstimateTokens(fullText);
        var totalTok = promptTok + completionTok;

        // Persistir mensaje del asistente y log de uso al completar el stream
        var assistantMessage = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversation.Id,
            Rol = AIRolMensaje.Assistant,
            Contenido = fullText,
            SystemPromptVersion = LegalPromptBuilder.CurrentSystemPromptVersion,
            TokensEntrada = promptTok,
            TokensSalida = completionTok,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            ModelId = modelId,
            ProviderId = _aiProvider.ProviderId,
            FinishReason = finalFinishReason.ToString(),
            ContextoAutorizado = conversation.ExpedienteId.HasValue,
            Disclaimer = LegalPromptBuilder.DeontologicalDisclaimer,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIMessages.Add(assistantMessage);

        var usageLog = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ConversationId = conversation.Id,
            CasoUso = conversation.CasoUso,
            ProviderId = _aiProvider.ProviderId,
            ModelId = modelId,
            TokensEntrada = promptTok,
            TokensSalida = completionTok,
            TotalTokens = totalTok,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            CostoEstimadoUsd = CalculateCost(promptTok, completionTok),
            Exitoso = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIUsageLogs.Add(usageLog);

        conversation.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<AIConversationDetailDto> CreateConversationAsync(CreateAIConversationDto dto, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var tenantId = GetCurrentTenantId();
        var currentUserId = GetCurrentUserId();

        if (dto.ExpedienteId.HasValue)
        {
            await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId.Value, false, cancellationToken);
        }

        var conv = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ExpedienteId = dto.ExpedienteId,
            Titulo = !string.IsNullOrWhiteSpace(dto.Titulo) ? dto.Titulo.Trim() : "Nueva Conversación",
            CasoUso = dto.CasoUso,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.AIConversations.Add(conv);
        await _context.SaveChangesAsync(cancellationToken);

        return new AIConversationDetailDto(
            conv.Id,
            conv.Titulo,
            conv.CasoUso,
            conv.ExpedienteId,
            null,
            conv.UsuarioId,
            conv.CreatedAt,
            conv.UpdatedAt,
            Array.Empty<AIMessageDto>()
        );
    }

    public async Task<AIConversationDetailDto> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var currentUserId = GetCurrentUserId();

        var conv = await _context.AIConversations
            .Include(c => c.Expediente)
            .Include(c => c.Mensajes.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == conversationId && !c.IsDeleted, cancellationToken)
            ?? throw new NotFoundException(nameof(AIConversation), conversationId);

        EnsureConversationAccess(conv, currentUserId);

        var mensajesDto = conv.Mensajes.Select(m => new AIMessageDto(
            m.Id,
            m.Rol,
            m.Contenido,
            m.CreatedAt,
            m.Disclaimer,
            m.ContextoAutorizado,
            m.TokensEntrada,
            m.TokensSalida
        )).ToList();

        return new AIConversationDetailDto(
            conv.Id,
            conv.Titulo,
            conv.CasoUso,
            conv.ExpedienteId,
            conv.Expediente?.NumeroExpediente,
            conv.UsuarioId,
            conv.CreatedAt,
            conv.UpdatedAt,
            mensajesDto
        );
    }

    public async Task<IReadOnlyList<AIConversationSummaryDto>> GetConversationsAsync(Guid? expedienteId = null, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var currentUserId = GetCurrentUserId();
        var role = _currentUserService.Role;

        var query = _context.AIConversations
            .Include(c => c.Expediente)
            .Include(c => c.Mensajes)
            .Where(c => !c.IsDeleted);

        // Regla PBAC: Junior y Assistant ven ÚNICAMENTE sus propias conversaciones
        if (role == Roles.AbogadoJunior || role == Roles.AsistenteLegal)
        {
            query = query.Where(c => c.UsuarioId == currentUserId);
        }

        if (expedienteId.HasValue)
        {
            await _expedienteAccessService.EnsureCanAccessExpedienteAsync(expedienteId.Value, false, cancellationToken);
            query = query.Where(c => c.ExpedienteId == expedienteId.Value);
        }

        var list = await query
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new AIConversationSummaryDto(
                c.Id,
                c.Titulo,
                c.CasoUso,
                c.ExpedienteId,
                c.Expediente != null ? c.Expediente.NumeroExpediente : null,
                c.UsuarioId,
                c.CreatedAt,
                c.UpdatedAt,
                c.Mensajes.Count
            ))
            .ToListAsync(cancellationToken);

        return list;
    }

    public async Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var currentUserId = GetCurrentUserId();

        var conv = await _context.AIConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && !c.IsDeleted, cancellationToken)
            ?? throw new NotFoundException(nameof(AIConversation), conversationId);

        EnsureConversationAccess(conv, currentUserId);

        conv.IsDeleted = true;
        conv.DeletedAt = DateTime.UtcNow;
        conv.DeletedBy = _currentUserService.Email ?? currentUserId.ToString();

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AIChatResponseDto> SummarizeExpedienteAsync(AISummarizeRequestDto dto, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var tenantId = GetCurrentTenantId();
        var currentUserId = GetCurrentUserId();

        // 1. Fase 6.X (D-2): autorización DOCUMENTAL del expediente (incluida la tarea vigente del AsistenteLegal),
        // antes de cualquier lectura de documentos.
        await AutorizarDocumentosDeExpedienteAsync(dto.ExpedienteId, cancellationToken);

        var expediente = await _context.Expedientes
            .AsNoTracking()
            .Include(e => e.Cliente)
            .FirstOrDefaultAsync(e => e.Id == dto.ExpedienteId && !e.IsDeleted, cancellationToken)
            ?? throw new NotFoundException(nameof(Expediente), dto.ExpedienteId);

        // 2-3. Validación de DocumentoIds (400) y carga de los documentos activos del expediente autorizado.
        var documentos = await CargarDocumentosParaResumenAsync(tenantId, expediente.Id, dto.DocumentoIds, cancellationToken);

        var sbContexto = new System.Text.StringBuilder();
        sbContexto.AppendLine($"DATOS DEL EXPEDIENTE:");
        sbContexto.AppendLine($"Número: {expediente.NumeroExpediente}");
        sbContexto.AppendLine($"Título: {expediente.Titulo}");
        sbContexto.AppendLine($"Materia: {expediente.Materia}");
        sbContexto.AppendLine($"Cliente: {expediente.Cliente?.NombreRazonSocial}");
        sbContexto.AppendLine($"Descripción inicial: {expediente.Descripcion}");

        // 5-6. Extracción por documento, en secuencia y fuera de cualquier transacción. El primer resultado distinto
        // de Success hace fallar el resumen completo sin llamar al proveedor (DA-9). El resumen NO modifica
        // EstadoIa ni MetadatosJson (DA-13): los documentos se leen sin seguimiento.
        foreach (var doc in documentos)
        {
            var docText = await ObtenerTextoAsync(doc, cancellationToken);
            sbContexto.AppendLine();
            sbContexto.AppendLine(PromptSanitizer.WrapUntrustedContent(docText, $"Documento: {doc.Titulo} ({doc.TipoDocumento})"));
        }

        var systemPrompt = LegalPromptBuilder.BuildSystemPrompt(AICasoUso.ResumenExpediente);
        var promptMessages = new List<AIChatMessageDto>
        {
            new(AIRolMensaje.User, $"Genera el resumen estructurado del siguiente expediente legal. Enfoque prioritario: {dto.Enfoque ?? "Integral"}.\n\n{sbContexto}")
        };

        var estimatedTokens = _aiProvider.EstimateTokens(systemPrompt) + _aiProvider.EstimateTokens(promptMessages[0].Content);
        ContextWindowValidator.ValidateContextTokens(estimatedTokens, _aiProvider.Capabilities.MaxContextTokens);

        var sw = Stopwatch.StartNew();
        var modelId = _aiProvider.Capabilities.SupportedModels.FirstOrDefault() ?? "mock-legal-v1";
        var request = new AIChatCompletionRequest(
            ModelId: modelId,
            Messages: promptMessages,
            SystemPrompt: systemPrompt
        );

        var response = await InvokeProviderAsync(
            request, sw, tenantId, currentUserId, null, AICasoUso.ResumenExpediente, estimatedTokens, cancellationToken);
        sw.Stop();

        // Crear conversación asociada al expediente
        var conv = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ExpedienteId = expediente.Id,
            Titulo = $"Resumen: {expediente.NumeroExpediente}",
            CasoUso = AICasoUso.ResumenExpediente,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.AIConversations.Add(conv);

        var assistantMsg = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conv.Id,
            Rol = AIRolMensaje.Assistant,
            Contenido = response.Content,
            SystemPromptVersion = LegalPromptBuilder.CurrentSystemPromptVersion,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            ModelId = response.ModelId,
            ProviderId = response.ProviderId,
            FinishReason = response.FinishReason.ToString(),
            ContextoAutorizado = true,
            Disclaimer = LegalPromptBuilder.DeontologicalDisclaimer,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIMessages.Add(assistantMsg);

        var usageLog = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ConversationId = conv.Id,
            CasoUso = AICasoUso.ResumenExpediente,
            ProviderId = response.ProviderId,
            ModelId = response.ModelId,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            TotalTokens = response.TotalTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            CostoEstimadoUsd = CalculateCost(response.PromptTokens, response.CompletionTokens),
            Exitoso = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIUsageLogs.Add(usageLog);

        await _context.SaveChangesAsync(cancellationToken);

        return new AIChatResponseDto(
            conv.Id,
            assistantMsg.Id,
            assistantMsg.Contenido,
            assistantMsg.Disclaimer,
            assistantMsg.TokensEntrada,
            assistantMsg.TokensSalida,
            assistantMsg.DuracionMs,
            assistantMsg.ModelId,
            assistantMsg.ProviderId,
            assistantMsg.FinishReason,
            assistantMsg.ContextoAutorizado
        );
    }

    public async Task<AIChatResponseDto> SummarizeDocumentoAsync(AISummarizeDocumentoDto dto, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var tenantId = GetCurrentTenantId();
        var currentUserId = GetCurrentUserId();

        var doc = await AutorizarDocumentoAsync(dto.DocumentoId, cancellationToken);
        var docText = await ObtenerTextoAsync(doc, cancellationToken);

        var systemPrompt = LegalPromptBuilder.BuildSystemPrompt(AICasoUso.ResumenExpediente);
        var wrappedContent = PromptSanitizer.WrapUntrustedContent(docText, doc.Titulo);

        var promptMessages = new List<AIChatMessageDto>
        {
            new(AIRolMensaje.User, $"Genera un resumen procesal y jurídico detallado del siguiente documento. Enfoque: {dto.Enfoque ?? "General"}.\n\n{wrappedContent}")
        };

        var estimatedTokens = _aiProvider.EstimateTokens(systemPrompt) + _aiProvider.EstimateTokens(promptMessages[0].Content);
        ContextWindowValidator.ValidateContextTokens(estimatedTokens, _aiProvider.Capabilities.MaxContextTokens);

        var sw = Stopwatch.StartNew();
        var modelId = _aiProvider.Capabilities.SupportedModels.FirstOrDefault() ?? "mock-legal-v1";
        var request = new AIChatCompletionRequest(
            ModelId: modelId,
            Messages: promptMessages,
            SystemPrompt: systemPrompt
        );

        var response = await InvokeProviderAsync(
            request, sw, tenantId, currentUserId, null, AICasoUso.ResumenExpediente, estimatedTokens, cancellationToken);

        sw.Stop();

        var conv = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ExpedienteId = doc.ExpedienteId,
            Titulo = $"Resumen: {doc.Titulo}",
            CasoUso = AICasoUso.ResumenExpediente,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.AIConversations.Add(conv);

        var assistantMsg = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conv.Id,
            Rol = AIRolMensaje.Assistant,
            Contenido = response.Content,
            SystemPromptVersion = LegalPromptBuilder.CurrentSystemPromptVersion,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            ModelId = response.ModelId,
            ProviderId = response.ProviderId,
            FinishReason = response.FinishReason.ToString(),
            ContextoAutorizado = true,
            Disclaimer = LegalPromptBuilder.DeontologicalDisclaimer,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIMessages.Add(assistantMsg);

        var usageLog = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ConversationId = conv.Id,
            CasoUso = AICasoUso.ResumenExpediente,
            ProviderId = response.ProviderId,
            ModelId = response.ModelId,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            TotalTokens = response.TotalTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            CostoEstimadoUsd = CalculateCost(response.PromptTokens, response.CompletionTokens),
            Exitoso = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIUsageLogs.Add(usageLog);

        await _context.SaveChangesAsync(cancellationToken);

        return new AIChatResponseDto(
            conv.Id,
            assistantMsg.Id,
            assistantMsg.Contenido,
            assistantMsg.Disclaimer,
            assistantMsg.TokensEntrada,
            assistantMsg.TokensSalida,
            assistantMsg.DuracionMs,
            assistantMsg.ModelId,
            assistantMsg.ProviderId,
            assistantMsg.FinishReason,
            assistantMsg.ContextoAutorizado
        );
    }

    public async Task<AIExtractResponseDto> ExtractFromDocumentAsync(AIExtractRequestDto dto, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        if (!_aiProvider.Capabilities.SupportsStructuredOutput)
        {
            throw new BusinessRuleException("El proveedor de IA configurado no soporta salida estructurada (Structured Output) requerida para la extracción de datos.");
        }
        var tenantId = GetCurrentTenantId();
        var currentUserId = GetCurrentUserId();

        // 1. Autorización documental (incluida la tarea vigente del AsistenteLegal), con códigos DOCUMENT_*.
        var doc = await AutorizarDocumentoAsync(dto.DocumentoId, cancellationToken);

        // 2. Formato sin extracción de texto: 422 SIN cambiar el estado (no se intentó nada).
        if (!_textExtractor.IsSupported(doc.ContentType))
        {
            throw ExcepcionDeExtraccion(new ExtractionResult(doc.Id, ExtractionStatus.UnsupportedFormat));
        }

        // 3. Exclusividad: un documento en Procesando no admite otra extracción.
        if (doc.EstadoIa == EstadoProcesamientoIa.Procesando)
        {
            throw new ConflictException("El documento ya se encuentra en procesamiento por otra operación IA.")
            {
                ErrorCode = DocumentoErrorCodes.Processing
            };
        }

        // 4. Transición a Procesando con inicio del lease (X1) en el MISMO UPDATE, con chequeo de xmin.
        doc.EstadoIa = EstadoProcesamientoIa.Procesando;
        doc.IaProcesandoDesde = DateTime.UtcNow;
        doc.UpdatedAt = DateTime.UtcNow;
        doc.UpdatedBy = _currentUserService.Email ?? currentUserId.ToString();

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Conflicto de concurrencia: El documento fue modificado simultáneamente por otra transacción.")
            {
                ErrorCode = DocumentoErrorCodes.ConcurrencyConflict
            };
        }

        var documentoId = doc.Id;
        var expedienteId = doc.ExpedienteId;
        var versionProcesando = doc.Version;
        var sw = new Stopwatch();
        AIChatCompletionResponse? response = null;

        try
        {
            // 5. Extracción real (H10). Cualquier resultado distinto de Success -> Fallido; nunca Procesado.
            var extraccion = await _textExtractor.ExtractAsync(documentoId, doc.RutaAlmacenamiento, doc.ContentType, cancellationToken);
            if (!extraccion.IsSuccess)
            {
                await MarcarFallidoAsync(documentoId, versionProcesando, expedienteId, extraccion.Status.ToString());
                throw ExcepcionDeExtraccion(extraccion);
            }

            // 6. Preparar petición IA
            var systemPrompt = LegalPromptBuilder.BuildSystemPrompt(AICasoUso.ExtraccionHechos);
            var wrappedContent = PromptSanitizer.WrapUntrustedContent(extraccion.Text!, doc.Titulo);

            var promptMessages = new List<AIChatMessageDto>
            {
                new(AIRolMensaje.User, $"Analiza el siguiente documento y genera la propuesta estructurada de extracción de hechos procesales en formato JSON:\n\n{wrappedContent}")
            };

            var estimatedTokens = _aiProvider.EstimateTokens(systemPrompt) + _aiProvider.EstimateTokens(promptMessages[0].Content);
            try
            {
                ContextWindowValidator.ValidateContextTokens(estimatedTokens, _aiProvider.Capabilities.MaxContextTokens);
            }
            catch (AIContextWindowExceededException)
            {
                await MarcarFallidoAsync(documentoId, versionProcesando, expedienteId, nameof(ExtractionStatus.ContextExceeded));
                throw;
            }

            var modelId = _aiProvider.Capabilities.SupportedModels.FirstOrDefault() ?? "mock-legal-v1";
            var request = new AIChatCompletionRequest(
                ModelId: modelId,
                Messages: promptMessages,
                SystemPrompt: systemPrompt
            );

            // 7. Proveedor. Un fallo (incluido su timeout) deja el documento en Fallido y registra el intento.
            sw.Start();
            try
            {
                response = await _aiProvider.CompleteChatAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                await MarcarFallidoAsync(documentoId, versionProcesando, expedienteId,
                    ex is AIProviderTimeoutException ? "PROVIDER_TIMEOUT" : "PROVIDER_ERROR");
                await RegisterFailedUsageAsync(
                    tenantId, currentUserId, null, AICasoUso.ExtraccionHechos, request.ModelId, estimatedTokens, 0, (int)sw.ElapsedMilliseconds, ex);

                if (TryNormalizeProviderFailure(ex, AICasoUso.ExtraccionHechos, cancellationToken, out var providerException))
                {
                    throw providerException;
                }
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Cancelación del llamador durante la llamada: se registra el intento; el estado lo resuelve la
                // captura externa (Fallido por CANCELLED).
                sw.Stop();
                await RegisterFailedUsageAsync(
                    tenantId, currentUserId, null, AICasoUso.ExtraccionHechos, request.ModelId, estimatedTokens, 0, (int)sw.ElapsedMilliseconds, ex);
                throw;
            }

            sw.Stop();

            // 8. Éxito: Procesado SOLO con extracción real y respuesta del proveedor. Fin del lease.
            doc.EstadoIa = EstadoProcesamientoIa.Procesado;
            doc.IaProcesandoDesde = null;
            doc.MetadatosJson = response.Content;
            doc.UpdatedAt = DateTime.UtcNow;

            _context.AIUsageLogs.Add(new AIUsageLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UsuarioId = currentUserId,
                CasoUso = AICasoUso.ExtraccionHechos,
                ProviderId = response.ProviderId,
                ModelId = response.ModelId,
                TokensEntrada = response.PromptTokens,
                TokensSalida = response.CompletionTokens,
                TotalTokens = response.TotalTokens,
                DuracionMs = (int)sw.ElapsedMilliseconds,
                CostoEstimadoUsd = CalculateCost(response.PromptTokens, response.CompletionTokens),
                Exitoso = true,
                CreatedAt = DateTime.UtcNow
            });

            await _auditService.LogInTransactionAsync("Documento", documentoId.ToString(), "AI_EXTRACTION_COMPLETED", null, new
            {
                ExpedienteId = expedienteId,
                response.ModelId
            }, cancellationToken);

            try
            {
                // UPDATE ... WHERE xmin = versión de la transición a Procesando.
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // X1: otra operación (p. ej., la recuperación por lease) cambió el documento. Se descarta el resultado
                // de la IA y NO se sobrescribe su estado. El consumo del proveedor se registra en un contexto propio.
                _context.ChangeTracker.Clear();
                await RegistrarUsoConsumidoFallidoAsync(
                    response, documentoId, tenantId, currentUserId, AICasoUso.ExtraccionHechos, (int)sw.ElapsedMilliseconds,
                    DocumentoErrorCodes.ConcurrencyConflict);
                throw new ConflictException("El documento fue modificado por otra operación mientras se procesaba. El resultado de la IA se descartó.")
                {
                    ErrorCode = DocumentoErrorCodes.ConcurrencyConflict
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fallo de persistencia: el resultado no se guardó, pero el proveedor sí se consumió. El documento queda
                // en Procesando y la recuperación por lease lo resuelve.
                _context.ChangeTracker.Clear();
                await RegistrarUsoConsumidoFallidoAsync(
                    response, documentoId, tenantId, currentUserId, AICasoUso.ExtraccionHechos, (int)sw.ElapsedMilliseconds,
                    ex.GetType().Name);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // D9: la cancelación del llamador se propaga; antes, se intenta una vez dejar el documento en Fallido.
            _context.ChangeTracker.Clear();
            if (response != null)
            {
                await RegistrarUsoConsumidoFallidoAsync(
                    response, documentoId, tenantId, currentUserId, AICasoUso.ExtraccionHechos, (int)sw.ElapsedMilliseconds, "CANCELLED");
            }

            await MarcarFallidoAsync(documentoId, versionProcesando, expedienteId, "CANCELLED");
            throw;
        }

        return new AIExtractResponseDto(
            documentoId,
            EstadoProcesamientoIa.Procesado,
            response!.Content,
            "Propuesta estructurada de extracción generada satisfactoriamente. Los datos NO han modificado el expediente y requieren aprobación expresa del abogado."
        );
    }

    public async Task<AIChatResponseDto> DraftEscritoAsync(AIDraftRequestDto dto, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        ContextWindowValidator.ValidateUserInputLength(dto.Instrucciones);
        var tenantId = GetCurrentTenantId();
        var currentUserId = GetCurrentUserId();

        if (dto.ExpedienteId.HasValue)
        {
            await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId.Value, false, cancellationToken);
        }

        var sbContexto = new System.Text.StringBuilder();
        sbContexto.AppendLine($"SOLICITUD DE REDACCIÓN DE ESCRITO JUDICIAL:");
        sbContexto.AppendLine($"Tipo de escrito requerido: {dto.TipoEscrito}");
        sbContexto.AppendLine($"Instrucciones específicas del abogado:\n{PromptSanitizer.SanitizeUserInput(dto.Instrucciones)}");

        if (dto.ContextoDocumentoIds != null && dto.ContextoDocumentoIds.Count > 0)
        {
            sbContexto.AppendLine("\nDOCUMENTOS Y ANTECEDENTES ADJUNTOS:");
            foreach (var docId in dto.ContextoDocumentoIds)
            {
                var doc = await AutorizarDocumentoAsync(docId, cancellationToken);
                var docText = await ObtenerTextoAsync(doc, cancellationToken);
                sbContexto.AppendLine(PromptSanitizer.WrapUntrustedContent(docText, $"Documento: {doc.Titulo}"));
            }
        }

        var systemPrompt = LegalPromptBuilder.BuildSystemPrompt(AICasoUso.RedaccionEscrito);
        var promptMessages = new List<AIChatMessageDto>
        {
            new(AIRolMensaje.User, sbContexto.ToString())
        };

        var estimatedTokens = _aiProvider.EstimateTokens(systemPrompt) + _aiProvider.EstimateTokens(promptMessages[0].Content);
        ContextWindowValidator.ValidateContextTokens(estimatedTokens, _aiProvider.Capabilities.MaxContextTokens);

        var sw = Stopwatch.StartNew();
        var modelId = _aiProvider.Capabilities.SupportedModels.FirstOrDefault() ?? "mock-legal-v1";
        var request = new AIChatCompletionRequest(
            ModelId: modelId,
            Messages: promptMessages,
            SystemPrompt: systemPrompt
        );

        var response = await InvokeProviderAsync(
            request, sw, tenantId, currentUserId, null, AICasoUso.RedaccionEscrito, estimatedTokens, cancellationToken);
        sw.Stop();

        var conv = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ExpedienteId = dto.ExpedienteId,
            Titulo = $"Borrador: {dto.TipoEscrito}",
            CasoUso = AICasoUso.RedaccionEscrito,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.AIConversations.Add(conv);

        var assistantMsg = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conv.Id,
            Rol = AIRolMensaje.Assistant,
            Contenido = response.Content,
            SystemPromptVersion = LegalPromptBuilder.CurrentSystemPromptVersion,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            ModelId = response.ModelId,
            ProviderId = response.ProviderId,
            FinishReason = response.FinishReason.ToString(),
            ContextoAutorizado = dto.ExpedienteId.HasValue,
            Disclaimer = LegalPromptBuilder.DeontologicalDisclaimer,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIMessages.Add(assistantMsg);

        var usageLog = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = currentUserId,
            ConversationId = conv.Id,
            CasoUso = AICasoUso.RedaccionEscrito,
            ProviderId = response.ProviderId,
            ModelId = response.ModelId,
            TokensEntrada = response.PromptTokens,
            TokensSalida = response.CompletionTokens,
            TotalTokens = response.TotalTokens,
            DuracionMs = (int)sw.ElapsedMilliseconds,
            CostoEstimadoUsd = CalculateCost(response.PromptTokens, response.CompletionTokens),
            Exitoso = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.AIUsageLogs.Add(usageLog);

        await _context.SaveChangesAsync(cancellationToken);

        return new AIChatResponseDto(
            conv.Id,
            assistantMsg.Id,
            assistantMsg.Contenido,
            assistantMsg.Disclaimer,
            assistantMsg.TokensEntrada,
            assistantMsg.TokensSalida,
            assistantMsg.DuracionMs,
            assistantMsg.ModelId,
            assistantMsg.ProviderId,
            assistantMsg.FinishReason,
            assistantMsg.ContextoAutorizado
        );
    }

    public async Task<AIConsumoResponseDto> GetConsumoAsync(AIConsumoQueryDto query, CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var currentUserId = GetCurrentUserId();
        var role = _currentUserService.Role;

        // Validación de rango de fechas (máximo 90 días)
        if (query.FechaFin < query.FechaInicio)
        {
            throw new ValidationException(new[] { "La fecha de fin debe ser posterior o igual a la fecha de inicio." });
        }

        var dias = (int)Math.Ceiling((query.FechaFin - query.FechaInicio).TotalDays);
        if (dias > 90)
        {
            throw new ValidationException(new[] { "El rango de consulta no puede exceder 90 días." });
        }

        var logsQuery = _context.AIUsageLogs
            .Include(l => l.Usuario)
            .Where(l => l.CreatedAt >= query.FechaInicio && l.CreatedAt <= query.FechaFin);

        // PBAC: Junior y Assistant ven ÚNICAMENTE su propio consumo
        if (role == Roles.AbogadoJunior || role == Roles.AsistenteLegal)
        {
            logsQuery = logsQuery.Where(l => l.UsuarioId == currentUserId);
        }
        else if (query.UsuarioId.HasValue)
        {
            // Admin y Senior pueden filtrar por un usuario específico
            logsQuery = logsQuery.Where(l => l.UsuarioId == query.UsuarioId.Value);
        }

        var logs = await logsQuery.ToListAsync(cancellationToken);

        var totalInvocaciones = logs.Count;
        var totalTokensEntrada = logs.Sum(l => l.TokensEntrada);
        var totalTokensSalida = logs.Sum(l => l.TokensSalida);
        var totalTokens = logs.Sum(l => l.TotalTokens);
        var costoTotal = logs.Sum(l => l.CostoEstimadoUsd ?? 0m);

        var desglosePorCasoUso = logs
            .GroupBy(l => l.CasoUso.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var desglosePorUsuario = logs
            .GroupBy(l => new { l.UsuarioId, Nombre = l.Usuario?.NombreCompleto ?? "Usuario Desconocido" })
            .Select(g => new AIConsumoUsuarioDto(
                g.Key.UsuarioId,
                g.Key.Nombre,
                g.Count(),
                g.Sum(l => l.TotalTokens),
                g.Sum(l => l.CostoEstimadoUsd ?? 0m)
            ))
            .ToList();

        return new AIConsumoResponseDto(
            TotalInvocaciones: totalInvocaciones,
            TotalTokensEntrada: totalTokensEntrada,
            TotalTokensSalida: totalTokensSalida,
            TotalTokens: totalTokens,
            CostoEstimadoUsdTotal: costoTotal,
            DesglosePorCasoUso: desglosePorCasoUso,
            DesglosePorUsuario: desglosePorUsuario,
            PeriodoDias: dias
        );
    }

    /// <summary>
    /// Aplica la política de retención de conversaciones ÚNICAMENTE sobre el tenant del contexto actual.
    /// Devuelve el número de conversaciones eliminadas físicamente. Nunca toca ai_usage_logs.
    /// </summary>
    public async Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotSuperAdmin();
        var tenantId = GetCurrentTenantId();

        // Política de retención:
        // 1. Conversaciones activas de más de 365 días pasan a borrado lógico
        var fechaLimiteActivas = DateTime.UtcNow.AddDays(-365);
        var conversacionesExpiradasActivas = await _context.AIConversations
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && c.CreatedAt < fechaLimiteActivas)
            .ToListAsync(cancellationToken);

        foreach (var c in conversacionesExpiradasActivas)
        {
            c.IsDeleted = true;
            c.DeletedAt = DateTime.UtcNow;
            c.DeletedBy = "SystemRetentionPolicy";
        }

        // 2. Conversaciones con borrado lógico de más de 30 días se purgan físicamente.
        // IgnoreQueryFilters es necesario para ver las filas con borrado lógico; como también desactiva
        // el filtro global de tenant, el TenantId del contexto se aplica de forma explícita.
        var fechaLimiteSoftDeleted = DateTime.UtcNow.AddDays(-30);
        var conversacionesParaPurga = await _context.AIConversations
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.IsDeleted && c.DeletedAt < fechaLimiteSoftDeleted)
            .ToListAsync(cancellationToken);

        _context.AIConversations.RemoveRange(conversacionesParaPurga);
        await _context.SaveChangesAsync(cancellationToken);

        return conversacionesParaPurga.Count;
    }

    /// <summary>
    /// Invoca al proveedor de IA. Ante cualquier fallo registra el intento en AIUsageLog (sin prompt ni
    /// respuesta) y lo normaliza a <see cref="AIProviderException"/>; solo la cancelación solicitada por el
    /// llamador se propaga sin convertir.
    /// </summary>
    private async Task<AIChatCompletionResponse> InvokeProviderAsync(
        AIChatCompletionRequest request,
        Stopwatch sw,
        Guid tenantId,
        Guid userId,
        Guid? conversationId,
        AICasoUso casoUso,
        int estimatedTokens,
        CancellationToken cancellationToken,
        Action? onFailure = null)
    {
        try
        {
            return await _aiProvider.CompleteChatAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            sw.Stop();
            onFailure?.Invoke();
            await RegisterFailedUsageAsync(
                tenantId, userId, conversationId, casoUso, request.ModelId, estimatedTokens, 0, (int)sw.ElapsedMilliseconds, ex);

            if (TryNormalizeProviderFailure(ex, casoUso, cancellationToken, out var providerException))
            {
                throw providerException;
            }
            throw;
        }
    }

    /// <summary>
    /// Devuelve false cuando la excepción debe propagarse tal cual: ya es una <see cref="AIProviderException"/>
    /// o es una cancelación solicitada por el llamador (cliente desconectado).
    /// </summary>
    private bool TryNormalizeProviderFailure(Exception ex, AICasoUso casoUso, CancellationToken callerToken, out AIProviderException providerException)
    {
        providerException = null!;

        if (ex is AIProviderException)
            return false;

        if (ex is OperationCanceledException && callerToken.IsCancellationRequested)
            return false;

        // Solo el tipo: el mensaje original puede contener URL, credenciales o fragmentos del prompt.
        _logger.LogError(
            "Fallo del proveedor de IA {ProviderId} en {CasoUso}: {ExceptionType}",
            _aiProvider.ProviderId, casoUso, ex.GetType().Name);

        providerException = new AIProviderException("Error en la comunicación con el proveedor de IA externo. Intente nuevamente más tarde.");
        return true;
    }

    private async Task RegisterFailedUsageAsync(
        Guid tenantId,
        Guid userId,
        Guid? conversationId,
        AICasoUso casoUso,
        string modelId,
        int tokensEntrada,
        int tokensSalida,
        int duracionMs,
        Exception ex)
    {
        _context.AIUsageLogs.Add(new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = userId,
            ConversationId = conversationId,
            CasoUso = casoUso,
            ProviderId = _aiProvider.ProviderId,
            ModelId = modelId,
            TokensEntrada = tokensEntrada,
            TokensSalida = tokensSalida,
            TotalTokens = tokensEntrada + tokensSalida,
            DuracionMs = duracionMs,
            CostoEstimadoUsd = 0,
            Exitoso = false,
            CodigoError = ex is AIProviderException providerException ? providerException.ErrorCode : ex.GetType().Name,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Fase 6.X (H10) — Texto real del documento o la excepción del contrato (§8). Nunca hay texto de respaldo.
    /// La cancelación del llamador se propaga sin alterar.
    /// </summary>
    private async Task<string> ObtenerTextoAsync(Documento doc, CancellationToken cancellationToken)
    {
        var resultado = await _textExtractor.ExtractAsync(doc.Id, doc.RutaAlmacenamiento, doc.ContentType, cancellationToken);
        return resultado.IsSuccess ? resultado.Text! : throw ExcepcionDeExtraccion(resultado);
    }

    /// <summary>Mapeo del contrato §8: cada resultado distinto de Success a su HTTP y código.</summary>
    private static Exception ExcepcionDeExtraccion(ExtractionResult resultado) => resultado.Status switch
    {
        ExtractionStatus.FileNotFound => new NotFoundException("El archivo del documento no está disponible.")
        {
            ErrorCode = DocumentoErrorCodes.FileNotFound
        },
        // DA-6: 403 sin código, igual que la descarga de la Fase 7.
        ExtractionStatus.Forbidden => new ForbiddenException("No se pudo acceder de forma segura al archivo del documento."),
        ExtractionStatus.Empty => new BusinessRuleException("El documento no contiene texto extraíble.")
        {
            ErrorCode = AIDocumentErrorCodes.TextEmpty
        },
        ExtractionStatus.UnsupportedFormat => new BusinessRuleException("El formato del documento no admite extracción de texto.")
        {
            ErrorCode = AIDocumentErrorCodes.TextUnsupported
        },
        ExtractionStatus.InvalidContent => new BusinessRuleException("El documento está dañado, cifrado o no se puede leer.")
        {
            ErrorCode = AIDocumentErrorCodes.TextInvalid
        },
        ExtractionStatus.ContextExceeded => DocumentContextExceededException.SuperaLimite(ContextWindowValidator.MaxDocumentCharacters),
        _ => new BusinessRuleException("No se pudo extraer el texto del documento.")
        {
            ErrorCode = AIDocumentErrorCodes.TextExtractionFailed
        }
    };

    /// <summary>
    /// Autoriza un documento (tenant, expediente activo, rol y tarea vigente del AsistenteLegal) y traduce los 404/403
    /// sin código a DOCUMENT_NOT_FOUND / DOCUMENT_ACCESS_DENIED, como hace DocumentoService (Fase 7).
    /// </summary>
    private async Task<Documento> AutorizarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken)
    {
        try
        {
            return await _expedienteAccessService.EnsureCanAccessDocumentoAsync(documentoId, false, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.ErrorCode == null)
        {
            throw new NotFoundException(ex.Message) { ErrorCode = DocumentoErrorCodes.NotFound };
        }
        catch (ForbiddenException ex) when (ex.ErrorCode == null)
        {
            throw new ForbiddenException(ex.Message) { ErrorCode = DocumentoErrorCodes.AccessDenied };
        }
    }

    /// <summary>
    /// Fase 6.X (D-2) — Autorización documental de un expediente completo. El 404 del expediente va sin código
    /// (como el listado de la Fase 7.3); el 403 se traduce a DOCUMENT_ACCESS_DENIED.
    /// </summary>
    private async Task AutorizarDocumentosDeExpedienteAsync(Guid expedienteId, CancellationToken cancellationToken)
    {
        try
        {
            await _expedienteAccessService.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId, cancellationToken);
        }
        catch (ForbiddenException ex) when (ex.ErrorCode == null)
        {
            throw new ForbiddenException(ex.Message) { ErrorCode = DocumentoErrorCodes.AccessDenied };
        }
    }

    /// <summary>
    /// Fase 6.X (D2) — Documentos activos del expediente ya autorizado. Con DocumentoIds explícitos, todos deben
    /// existir, estar activos y pertenecer al expediente; si no, 400 DOCUMENT_DOCUMENTS_INVALID con un mensaje
    /// idéntico por id (no revela si el id existe en otro expediente o tenant). Los duplicados se eliminan.
    /// </summary>
    private async Task<List<Documento>> CargarDocumentosParaResumenAsync(
        Guid tenantId, Guid expedienteId, IReadOnlyList<Guid>? documentoIds, CancellationToken cancellationToken)
    {
        var activos = await _context.Documentos
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.ExpedienteId == expedienteId && !d.IsDeleted)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);

        if (documentoIds == null || documentoIds.Count == 0)
        {
            return activos;
        }

        var solicitados = documentoIds.Distinct().ToList();
        var idsActivos = activos.Select(d => d.Id).ToHashSet();
        var invalidos = solicitados.Where(id => id == Guid.Empty || !idsActivos.Contains(id)).ToList();
        if (invalidos.Count > 0)
        {
            throw new ValidationException(invalidos.Select(id => $"El documento {id} no es válido para este expediente."))
            {
                ErrorCode = AIDocumentErrorCodes.DocumentsInvalid
            };
        }

        var seleccion = solicitados.ToHashSet();
        return activos.Where(d => seleccion.Contains(d.Id)).ToList();
    }

    /// <summary>
    /// Fase 6.X (D8/D9) — Procesando -> Fallido de ESTA operación, con auditoría AI_EXTRACTION_FAILED en el mismo
    /// SaveChanges y WHERE xmin = versión de la transición. Si otra operación ya cambió el documento (recuperación,
    /// cancelación...), no se sobrescribe. Nunca lanza: si no puede guardar, la recuperación por lease lo resuelve.
    /// </summary>
    private async Task MarcarFallidoAsync(Guid documentoId, uint versionProcesando, Guid expedienteId, string causa)
    {
        try
        {
            _context.ChangeTracker.Clear();
            var doc = await _context.Documentos.FirstOrDefaultAsync(d => d.Id == documentoId, CancellationToken.None);
            if (doc == null || doc.EstadoIa != EstadoProcesamientoIa.Procesando || doc.Version != versionProcesando)
            {
                _logger.LogWarning(
                    "[AI_ESTADO_NO_ACTUALIZADO] El documento {DocumentoId} ya no está en el Procesando de esta operación; no se sobrescribe su estado.",
                    documentoId);
                return;
            }

            doc.EstadoIa = EstadoProcesamientoIa.Fallido;
            doc.IaProcesandoDesde = null;
            doc.UpdatedAt = DateTime.UtcNow;
            doc.UpdatedBy = _currentUserService.Email ?? _currentUserService.UserId?.ToString();

            await _auditService.LogInTransactionAsync("Documento", documentoId.ToString(), "AI_EXTRACTION_FAILED", null, new
            {
                ExpedienteId = expedienteId,
                Causa = causa
            }, CancellationToken.None);

            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _context.ChangeTracker.Clear();
            _logger.LogError(
                "[AI_ESTADO_NO_ACTUALIZADO] No se pudo marcar como Fallido el documento {DocumentoId} (causa {Causa}). Error: {TipoError}. La recuperación por lease lo resolverá.",
                documentoId, causa, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Fase 6.X (X1) — Registra un uso del proveedor que SÍ se consumió pero cuyo resultado no se guardó (conflicto de
    /// concurrencia, cancelación o fallo de persistencia). En un DbContext independiente, nunca dentro del contexto ni
    /// de la transacción que acaban de fallar. Tokens y coste reales de la respuesta.
    /// </summary>
    private async Task RegistrarUsoConsumidoFallidoAsync(
        AIChatCompletionResponse response,
        Guid documentoId,
        Guid tenantId,
        Guid userId,
        AICasoUso casoUso,
        int duracionMs,
        string codigoError)
    {
        try
        {
            // Mismas opciones que el contexto compartido (conexión, reintentos e interceptores), como AuditService.LogAsync.
            var opciones = (DbContextOptions<ApplicationDbContext>)_context.GetService<IDbContextOptions>();
            await using var contextoUso = new ApplicationDbContext(opciones, _currentTenantService);
            contextoUso.AIUsageLogs.Add(new AIUsageLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UsuarioId = userId,
                CasoUso = casoUso,
                ProviderId = response.ProviderId,
                ModelId = response.ModelId,
                TokensEntrada = response.PromptTokens,
                TokensSalida = response.CompletionTokens,
                TotalTokens = response.TotalTokens,
                DuracionMs = duracionMs,
                CostoEstimadoUsd = CalculateCost(response.PromptTokens, response.CompletionTokens),
                Exitoso = false,
                CodigoError = codigoError,
                CreatedAt = DateTime.UtcNow
            });
            await contextoUso.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "[AI_USAGE_NO_REGISTRADO] No se pudo registrar el consumo del proveedor del documento {DocumentoId} (tenant {TenantId}). Error: {TipoError}.",
                documentoId, tenantId, ex.GetType().Name);
        }
    }

    private void EnsureNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) tienen prohibido el acceso a capacidades y datos de Inteligencia Artificial.");
        }
    }

    private void EnsureConversationAccess(AIConversation conv, Guid currentUserId)
    {
        var role = _currentUserService.Role;
        if (role == Roles.AbogadoJunior || role == Roles.AsistenteLegal)
        {
            if (conv.UsuarioId != currentUserId)
            {
                throw new ForbiddenException("No tiene permisos para acceder a esta conversación.");
            }
        }
    }

    private Guid GetCurrentTenantId()
    {
        return _currentTenantService.TenantId
            ?? throw new ForbiddenException("Contexto de Tenant no especificado.");
    }

    private Guid GetCurrentUserId()
    {
        return _currentUserService.UserId
            ?? throw new UnauthorizedException("Usuario no autenticado.");
    }

    private static string GenerateConversationTitle(string mensaje)
    {
        if (string.IsNullOrWhiteSpace(mensaje))
            return "Nueva Conversación";

        var clean = mensaje.Trim().Replace("\r", " ").Replace("\n", " ");
        return clean.Length > 50 ? clean.Substring(0, 47) + "..." : clean;
    }

    private static decimal CalculateCost(int tokensEntrada, int tokensSalida)
    {
        var costoEntrada = (tokensEntrada / 1_000_000m) * 0.15m;
        var costoSalida = (tokensSalida / 1_000_000m) * 0.60m;
        return Math.Round(costoEntrada + costoSalida, 6);
    }
}
