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
using Microsoft.EntityFrameworkCore;
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
    private readonly IFileStorageService _fileStorageService;
    private readonly ILogger<AIService> _logger;

    public AIService(
        ApplicationDbContext context,
        IAIProvider aiProvider,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IExpedienteAccessService expedienteAccessService,
        IFileStorageService fileStorageService,
        ILogger<AIService> logger)
    {
        _context = context;
        _aiProvider = aiProvider;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _expedienteAccessService = expedienteAccessService;
        _fileStorageService = fileStorageService;
        _logger = logger;
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

        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId, false, cancellationToken);

        var expediente = await _context.Expedientes
            .Include(e => e.Cliente)
            .Include(e => e.Documentos.Where(d => !d.IsDeleted))
            .FirstOrDefaultAsync(e => e.Id == dto.ExpedienteId && !e.IsDeleted, cancellationToken)
            ?? throw new NotFoundException(nameof(Expediente), dto.ExpedienteId);

        var sbContexto = new System.Text.StringBuilder();
        sbContexto.AppendLine($"DATOS DEL EXPEDIENTE:");
        sbContexto.AppendLine($"Número: {expediente.NumeroExpediente}");
        sbContexto.AppendLine($"Título: {expediente.Titulo}");
        sbContexto.AppendLine($"Materia: {expediente.Materia}");
        sbContexto.AppendLine($"Cliente: {expediente.Cliente?.NombreRazonSocial}");
        sbContexto.AppendLine($"Descripción inicial: {expediente.Descripcion}");

        // Incorporar documentos filtrados si se especificaron
        var docs = expediente.Documentos.AsEnumerable();
        if (dto.DocumentoIds != null && dto.DocumentoIds.Count > 0)
        {
            docs = docs.Where(d => dto.DocumentoIds.Contains(d.Id));
        }

        foreach (var doc in docs)
        {
            var docText = await ReadDocumentTextSafelyAsync(doc, cancellationToken);
            ContextWindowValidator.ValidateDocumentLength(docText);

            if (!string.IsNullOrWhiteSpace(docText))
            {
                sbContexto.AppendLine();
                sbContexto.AppendLine(PromptSanitizer.WrapUntrustedContent(docText, $"Documento: {doc.Titulo} ({doc.TipoDocumento})"));
            }
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

        var doc = await _expedienteAccessService.EnsureCanAccessDocumentoAsync(dto.DocumentoId, false, cancellationToken);
        var docText = await ReadDocumentTextSafelyAsync(doc, cancellationToken);
        ContextWindowValidator.ValidateDocumentLength(docText);

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

        // 1. Validar acceso al documento conforme a reglas PBAC
        var doc = await _expedienteAccessService.EnsureCanAccessDocumentoAsync(dto.DocumentoId, false, cancellationToken);

        // 2. Control de exclusividad y concurrencia optimista vía xmin
        if (doc.EstadoIa == EstadoProcesamientoIa.Procesando)
        {
            throw new ConflictException("El documento ya se encuentra en procesamiento por otra operación IA.");
        }

        // Transicionar a Procesando y persistir inmediatamente con chequeo de xmin
        doc.EstadoIa = EstadoProcesamientoIa.Procesando;
        doc.UpdatedAt = DateTime.UtcNow;
        doc.UpdatedBy = _currentUserService.Email ?? currentUserId.ToString();

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Conflicto de concurrencia: El documento fue modificado simultáneamente por otra transacción.");
        }

        // 3. Extraer contenido del archivo y validar longitud estricta de 30.000 caracteres
        string rawDocumentText;
        try
        {
            rawDocumentText = await ReadDocumentTextSafelyAsync(doc, cancellationToken);
            ContextWindowValidator.ValidateDocumentLength(rawDocumentText);
        }
        catch (DocumentContextExceededException)
        {
            // Revertir a fallido y persistir
            doc.EstadoIa = EstadoProcesamientoIa.Fallido;
            await _context.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        // 4. Preparar petición IA
        var systemPrompt = LegalPromptBuilder.BuildSystemPrompt(AICasoUso.ExtraccionHechos);
        var wrappedContent = PromptSanitizer.WrapUntrustedContent(rawDocumentText, doc.Titulo);

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
            doc.EstadoIa = EstadoProcesamientoIa.Fallido;
            await _context.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        var sw = Stopwatch.StartNew();
        var modelId = _aiProvider.Capabilities.SupportedModels.FirstOrDefault() ?? "mock-legal-v1";
        var request = new AIChatCompletionRequest(
            ModelId: modelId,
            Messages: promptMessages,
            SystemPrompt: systemPrompt
        );

        // Ante cualquier fallo del proveedor el documento pasa a Fallido junto con el registro del intento.
        var response = await InvokeProviderAsync(
            request, sw, tenantId, currentUserId, null, AICasoUso.ExtraccionHechos, estimatedTokens, cancellationToken,
            onFailure: () => doc.EstadoIa = EstadoProcesamientoIa.Fallido);

        sw.Stop();

        // 5. Guardar propuesta estructurada EXCLUSIVAMENTE en doc.MetadatosJson (sin mutar Expediente)
        doc.EstadoIa = EstadoProcesamientoIa.Procesado;
        doc.MetadatosJson = response.Content;
        doc.UpdatedAt = DateTime.UtcNow;

        var usageLog = new AIUsageLog
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
        };
        _context.AIUsageLogs.Add(usageLog);

        await _context.SaveChangesAsync(cancellationToken);

        return new AIExtractResponseDto(
            doc.Id,
            doc.EstadoIa,
            doc.MetadatosJson,
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
                var doc = await _expedienteAccessService.EnsureCanAccessDocumentoAsync(docId, false, cancellationToken);
                var docText = await ReadDocumentTextSafelyAsync(doc, cancellationToken);
                ContextWindowValidator.ValidateDocumentLength(docText);

                if (!string.IsNullOrWhiteSpace(docText))
                {
                    sbContexto.AppendLine(PromptSanitizer.WrapUntrustedContent(docText, $"Documento: {doc.Titulo}"));
                }
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

    private async Task<string> ReadDocumentTextSafelyAsync(Documento doc, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrEmpty(doc.RutaAlmacenamiento))
            {
                using var stream = await _fileStorageService.OpenReadFileAsync(doc.RutaAlmacenamiento, cancellationToken);
                using var reader = new StreamReader(stream);
                // Leemos hasta 30005 caracteres para detectar exceso sin desbordar memoria
                var buffer = new char[30005];
                var charsRead = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                return new string(buffer, 0, charsRead);
            }
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Archivo físico no encontrado para Documento {DocId}", doc.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer archivo de documento {DocId}, usando metadatos", doc.Id);
        }

        // Si no hay archivo físico o está en prueba de integración rápida, usamos el título y tipo como texto
        return $"DOCUMENTO: {doc.Titulo}\nTIPO: {doc.TipoDocumento}\nDESCRIPCIÓN: Archivo procesado.";
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
