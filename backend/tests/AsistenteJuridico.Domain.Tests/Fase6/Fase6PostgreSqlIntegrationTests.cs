using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.AI.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase6;

/// <summary>
/// Pruebas de integración de Fase 6 ejecutadas contra una base de datos real PostgreSQL 16.
/// Valida triggers nativos de inmutabilidad (SQLSTATE 55000), aislamiento multi-tenant,
/// concurrencia optimista xmin en AIConversation y Documento, exclusividad de EstadoIa,
/// y la inmutabilidad de AIUsageLog ante purga física de conversaciones.
/// </summary>
public class Fase6PostgreSqlIntegrationTests
{
    private const string PostgresConnectionString =
        "Host=localhost;Port=5433;Database=asistente_juridico;Username=aj_user;Password=REMOVED_SECRET";

    private class MockCurrentTenantService : ICurrentTenantService
    {
        public Guid? TenantId { get; set; }
        public string? TenantSlug => "tenant-test";
        public bool IsMultiTenantContext => TenantId.HasValue;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
    }

    private class MockCurrentUserService : ICurrentUserService
    {
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public Guid? TenantId { get; set; }
        public string? Email { get; set; } = "abogado@estudio.com";
        public string? Role { get; set; } = Roles.AdminEstudio;
        public bool IsAuthenticated => true;
        public IEnumerable<string> Permissions => [
            Application.Common.Security.Permissions.AIChat,
            Application.Common.Security.Permissions.AISummarize,
            Application.Common.Security.Permissions.AIExtract,
            Application.Common.Security.Permissions.AIDraft,
            Application.Common.Security.Permissions.AIUsageRead
        ];
        public bool HasPermission(string permission) => true;
    }

    private class MockFileStorageService : IFileStorageService
    {
        public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<System.IO.Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("Contenido procesal de prueba para extracción y resumen jurídico.");
            return Task.FromResult<System.IO.Stream>(new System.IO.MemoryStream(bytes));
        }
        public Task<(string PhysicalFileName, string RelativeFilePath, string ContentType, long FileSizeBytes, string Sha256Hash)> SaveFileAsync(Guid tenantId, System.IO.Stream fileStream, string originalFileName, string declaredContentType, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(("f.pdf", "tenant/f.pdf", "application/pdf", 100L, "hash"));
        }
    }

    private ApplicationDbContext CreatePostgresDbContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        return new ApplicationDbContext(options, tenantService);
    }

    private async Task<(Tenant tenant, Usuario user, Expediente expediente, Documento documento)> SeedPostgresTestDataAsync(ApplicationDbContext context, Guid tenantId, Guid userId)
    {
        var tenant = await context.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            tenant = new Tenant
            {
                Id = tenantId,
                Nombre = "Estudio Jurídico Integración F6",
                IdentificadorUrl = "fase6-" + tenantId.ToString("N").Substring(0, 8),
                Plan = "Profesional",
                Activo = true,
                ZonaHorariaId = "America/Guayaquil",
                CreatedAt = DateTime.UtcNow
            };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        var user = await context.Usuarios.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            user = new Usuario
            {
                Id = userId,
                TenantId = tenantId,
                Email = $"abogado_{userId.ToString("N").Substring(0, 6)}@estudio.com",
                NormalizedEmail = $"ABOGADO_{userId.ToString("N").Substring(0, 6)}@ESTUDIO.COM",
                UserName = $"abogado_{userId.ToString("N").Substring(0, 6)}",
                NormalizedUserName = $"ABOGADO_{userId.ToString("N").Substring(0, 6)}",
                NombreCompleto = "Dr. Integración Fase 6",
                Rol = Roles.AdminEstudio,
                SecurityStamp = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.Usuarios.Add(user);
            await context.SaveChangesAsync();
        }

        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Identificacion = "1799999999001",
            NombreRazonSocial = "Compañía de Pruebas F6 S.A.",
            TipoIdentificacion = TipoIdentificacion.Ruc,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Clientes.Add(cliente);

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = "EXP-F6-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            Titulo = "Litigio Mercantil de Prueba F6",
            Materia = "Civil",
            Estado = EstadoExpediente.Abierto,
            ClienteId = cliente.Id,
            AbogadoResponsableId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Expedientes.Add(expediente);

        var documento = new Documento
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Contrato Mercantil.pdf",
            TipoDocumento = "Contrato",
            RutaAlmacenamiento = "tenant/contrato.pdf",
            ContentType = "application/pdf",
            EstadoIa = EstadoProcesamientoIa.Pendiente,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Documentos.Add(documento);

        await context.SaveChangesAsync();
        return (tenant, user, expediente, documento);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 01: Aislamiento multi-tenant de conversaciones
    // Un tenant no puede consultar conversaciones de otro tenant.
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test01_MultiTenancy_AislamientoConversaciones()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var contextA = CreatePostgresDbContext(tenantA);
        await SeedPostgresTestDataAsync(contextA, tenantA, userA);

        await using var contextB = CreatePostgresDbContext(tenantB);
        await SeedPostgresTestDataAsync(contextB, tenantB, userB);

        var convA = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            UsuarioId = userA,
            Titulo = "Consulta Confidencial Tenant A",
            CasoUso = AICasoUso.ChatLibre,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        contextA.AIConversations.Add(convA);
        await contextA.SaveChangesAsync();

        // Verificar que Tenant B NUNCA puede consultar ni listar la conversación de Tenant A
        var conversacionEnTenantB = await contextB.AIConversations
            .FirstOrDefaultAsync(c => c.Id == convA.Id);

        Assert.Null(conversacionEnTenantB);

        var listadoTenantB = await contextB.AIConversations.ToListAsync();
        Assert.DoesNotContain(listadoTenantB, c => c.Id == convA.Id);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 14: Concurrencia optimista de AIConversation
    // Conflicto xmin debe producir HTTP 409 (DbUpdateConcurrencyException).
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test14_ConcurrenciaOptimista_AIConversation_ConflictoXminDevuelve409()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var contextInit = CreatePostgresDbContext(tenantId);
        await SeedPostgresTestDataAsync(contextInit, tenantId, userId);

        var conv = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = userId,
            Titulo = "Conversación Concurrente",
            CasoUso = AICasoUso.ChatLibre,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        contextInit.AIConversations.Add(conv);
        await contextInit.SaveChangesAsync();

        // Cargar en dos contextos separados
        await using var context1 = CreatePostgresDbContext(tenantId);
        await using var context2 = CreatePostgresDbContext(tenantId);

        var c1 = await context1.AIConversations.FirstAsync(c => c.Id == conv.Id);
        var c2 = await context2.AIConversations.FirstAsync(c => c.Id == conv.Id);

        c1.Titulo = "Modificación por Transacción 1";
        await context1.SaveChangesAsync();

        c2.Titulo = "Modificación por Transacción 2";
        // Conflicto por xmin desactualizado en PostgreSQL
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context2.SaveChangesAsync());
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 20: QueryFilters multi-tenant
    // Los filtros globales deben aplicarse a todas las entidades IA correspondientes.
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test20_QueryFilters_MultiTenant_EntidadesIA()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var contextA = CreatePostgresDbContext(tenantA);
        await SeedPostgresTestDataAsync(contextA, tenantA, userA);

        await using var contextB = CreatePostgresDbContext(tenantB);
        await SeedPostgresTestDataAsync(contextB, tenantB, userB);

        var convA = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            UsuarioId = userA,
            Titulo = "Conversación Tenant A",
            CasoUso = AICasoUso.ChatLibre,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        contextA.AIConversations.Add(convA);

        var msgA = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            ConversationId = convA.Id,
            Rol = AIRolMensaje.User,
            Contenido = "Mensaje secreto Tenant A",
            SystemPromptVersion = "v1",
            CreatedAt = DateTime.UtcNow
        };
        contextA.AIMessages.Add(msgA);

        var logA = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            UsuarioId = userA,
            ConversationId = convA.Id,
            CasoUso = AICasoUso.ChatLibre,
            ProviderId = "mock",
            ModelId = "mock-v1",
            TokensEntrada = 10,
            TokensSalida = 10,
            TotalTokens = 20,
            DuracionMs = 50,
            CreatedAt = DateTime.UtcNow
        };
        contextA.AIUsageLogs.Add(logA);

        await contextA.SaveChangesAsync();

        // Desde contextB (Tenant B), los QueryFilters globales no deben retornar ninguna entidad IA de Tenant A
        var convBQuery = await contextB.AIConversations.FirstOrDefaultAsync(c => c.Id == convA.Id);
        var msgBQuery = await contextB.AIMessages.FirstOrDefaultAsync(m => m.Id == msgA.Id);
        var logBQuery = await contextB.AIUsageLogs.FirstOrDefaultAsync(l => l.Id == logA.Id);

        Assert.Null(convBQuery);
        Assert.Null(msgBQuery);
        Assert.Null(logBQuery);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 24: Extracción IA no modifica automáticamente Expediente
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test24_ExtraccionIA_NoModificaExpediente()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreatePostgresDbContext(tenantId);
        var (_, _, exp, doc) = await SeedPostgresTestDataAsync(context, tenantId, userId);

        var estadoOriginal = exp.Estado;
        var materiaOriginal = exp.Materia;
        var abogadoOriginal = exp.AbogadoResponsableId;

        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        var userService = new MockCurrentUserService { UserId = userId, TenantId = tenantId };
        var expAccessService = new ExpedienteAccessService(context, userService, tenantService);
        var mockAiProvider = new MockAIProvider(NullLogger<MockAIProvider>.Instance);

        var aiService = new AIService(
            context,
            mockAiProvider,
            tenantService,
            userService,
            expAccessService,
            new MockFileStorageService(),
            NullLogger<AIService>.Instance
        );

        var res = await aiService.ExtractFromDocumentAsync(new AIExtractRequestDto(doc.Id));

        Assert.Equal(EstadoProcesamientoIa.Procesado, res.EstadoIa);
        Assert.NotNull(res.PropuestaExtraccionJson);

        // Verificar en PostgreSQL que el Expediente NO fue mutado autónomamente
        var expEnDb = await context.Expedientes.FirstAsync(e => e.Id == exp.Id);
        Assert.Equal(estadoOriginal, expEnDb.Estado);
        Assert.Equal(materiaOriginal, expEnDb.Materia);
        Assert.Equal(abogadoOriginal, expEnDb.AbogadoResponsableId);

        // Verificar que los datos se guardaron EXCLUSIVAMENTE en MetadatosJson de Documento
        var docEnDb = await context.Documentos.FirstAsync(d => d.Id == doc.Id);
        Assert.Equal(res.PropuestaExtraccionJson, docEnDb.MetadatosJson);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 31: Exclusividad de procesamiento Documento
    // Si EstadoIa = Procesando: segunda operación → HTTP 409 vía xmin.
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test31_ExclusividadProcesamientoDocumento_EstadoIaProcesando_Conflicto409()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreatePostgresDbContext(tenantId);
        var (_, _, _, doc) = await SeedPostgresTestDataAsync(context, tenantId, userId);

        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        var userService = new MockCurrentUserService { UserId = userId, TenantId = tenantId };
        var expAccessService = new ExpedienteAccessService(context, userService, tenantService);
        var mockAiProvider = new MockAIProvider(NullLogger<MockAIProvider>.Instance, simulateLatencyMs: 0);

        var aiService = new AIService(
            context,
            mockAiProvider,
            tenantService,
            userService,
            expAccessService,
            new MockFileStorageService(),
            NullLogger<AIService>.Instance
        );

        // Documento en estado Procesando
        doc.EstadoIa = EstadoProcesamientoIa.Procesando;
        await context.SaveChangesAsync();

        // Segunda operación simultánea debe devolver ConflictException (HTTP 409)
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            aiService.ExtractFromDocumentAsync(new AIExtractRequestDto(doc.Id)));

        Assert.Contains("procesamiento", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 33: AIMessage — No debe persistir el contenido del System Prompt
    // Solo mensajes User/Assistant y SystemPromptVersion cuando corresponda.
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test33_AIMessage_NoPersisteSystemPrompt_SoloSystemPromptVersion()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreatePostgresDbContext(tenantId);
        await SeedPostgresTestDataAsync(context, tenantId, userId);

        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        var userService = new MockCurrentUserService { UserId = userId, TenantId = tenantId };
        var expAccessService = new ExpedienteAccessService(context, userService, tenantService);
        var mockAiProvider = new MockAIProvider(NullLogger<MockAIProvider>.Instance);

        var aiService = new AIService(
            context,
            mockAiProvider,
            tenantService,
            userService,
            expAccessService,
            new MockFileStorageService(),
            NullLogger<AIService>.Instance
        );

        var response = await aiService.SendMessageAsync(new AIChatRequestDto(
            ConversationId: null,
            ExpedienteId: null,
            Mensaje: "¿Qué es la prescripción adquisitiva de dominio?"
        ));

        var mensajesEnDb = await context.AIMessages
            .Where(m => m.ConversationId == response.ConversationId)
            .ToListAsync();

        Assert.NotEmpty(mensajesEnDb);
        foreach (var m in mensajesEnDb)
        {
            // El System Prompt completo NUNCA se persiste en la tabla ai_messages
            Assert.DoesNotContain("DIRECTIVAS INTERNAS DEL SISTEMA", m.Contenido);
            Assert.DoesNotContain("REGLAS DE SEGURIDAD Y CONTROL DE INTEGRIDAD", m.Contenido);
            Assert.DoesNotContain("Eres un Asistente Jurídico", m.Contenido);
            Assert.NotEmpty(m.SystemPromptVersion); // Solo se persiste la versión
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 35: Triggers PostgreSQL
    // UPDATE ai_usage_logs y DELETE FROM ai_usage_logs lanzan excepción 55000.
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test35_PostgreSQL_Triggers_InmutabilidadDirecta_UpdateDeleteLanzaExcepcion()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreatePostgresDbContext(tenantId);
        await SeedPostgresTestDataAsync(context, tenantId, userId);

        var log = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = userId,
            CasoUso = AICasoUso.ChatLibre,
            ProviderId = "mock-ai-provider",
            ModelId = "mock-legal-v1",
            TokensEntrada = 100,
            TokensSalida = 50,
            TotalTokens = 150,
            DuracionMs = 120,
            CostoEstimadoUsd = 0.0001m,
            Exitoso = true,
            CreatedAt = DateTime.UtcNow
        };
        context.AIUsageLogs.Add(log);
        await context.SaveChangesAsync();

        await using var conn = new NpgsqlConnection(PostgresConnectionString);
        await conn.OpenAsync();

        // 1. UPDATE directo contra PostgreSQL 16 -> excepción 55000
        await using var cmdUpdate = new NpgsqlCommand(
            $"UPDATE ai_usage_logs SET \"TokensEntrada\" = 999 WHERE \"Id\" = '{log.Id}'",
            conn
        );

        var updateEx = await Assert.ThrowsAsync<PostgresException>(() => cmdUpdate.ExecuteNonQueryAsync());
        Assert.Equal("55000", updateEx.SqlState);
        Assert.Contains("inmutable", updateEx.Message, StringComparison.OrdinalIgnoreCase);

        // 2. DELETE directo contra PostgreSQL 16 -> excepción 55000
        await using var cmdDelete = new NpgsqlCommand(
            $"DELETE FROM ai_usage_logs WHERE \"Id\" = '{log.Id}'",
            conn
        );

        var deleteEx = await Assert.ThrowsAsync<PostgresException>(() => cmdDelete.ExecuteNonQueryAsync());
        Assert.Equal("55000", deleteEx.SqlState);
        Assert.Contains("inmutable", deleteEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────
    // PRUEBA ESPECIAL DE AIUsageLog (Sección 11)
    // Demuestra que la purga física de conversación conserva intacto AIUsageLog
    // con su ConversationId histórico sin actualizarlo, sin eliminarlo,
    // y con triggers bloqueando UPDATE y DELETE.
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test_Special_AIUsageLog_PurgaPreservaHistoricoYTriggersBloquean()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = CreatePostgresDbContext(tenantId);
        await SeedPostgresTestDataAsync(context, tenantId, userId);

        // 1. Crear AIConversation
        var conv = new AIConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = userId,
            Titulo = "Conversación Para Purga Física Especial",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-45),
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            UpdatedAt = DateTime.UtcNow.AddDays(-45)
        };
        context.AIConversations.Add(conv);

        // 2. Crear AIUsageLog con ConversationId
        var createdAtOriginal = DateTime.UtcNow.AddDays(-45);
        var tokensEntradaOriginal = 150;
        var tokensSalidaOriginal = 75;
        var totalTokensOriginal = 225;
        var duracionMsOriginal = 200;

        var log = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = userId,
            ConversationId = conv.Id,
            CasoUso = AICasoUso.ChatLibre,
            ProviderId = "mock-provider-special",
            ModelId = "mock-model-special",
            TokensEntrada = tokensEntradaOriginal,
            TokensSalida = tokensSalidaOriginal,
            TotalTokens = totalTokensOriginal,
            DuracionMs = duracionMsOriginal,
            CostoEstimadoUsd = 0.0005m,
            Exitoso = true,
            CreatedAt = createdAtOriginal
        };
        context.AIUsageLogs.Add(log);

        // 3. Confirmar persistencia
        await context.SaveChangesAsync();

        var convGuardada = await context.AIConversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conv.Id);
        Assert.NotNull(convGuardada);

        var logGuardado = await context.AIUsageLogs.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.Id == log.Id);
        Assert.NotNull(logGuardado);
        Assert.Equal(conv.Id, logGuardado.ConversationId);

        // 4. Ejecutar purga de conversación
        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        var userService = new MockCurrentUserService { UserId = userId, TenantId = tenantId };
        var expAccessService = new ExpedienteAccessService(context, userService, tenantService);
        var mockAiProvider = new MockAIProvider(NullLogger<MockAIProvider>.Instance);

        var aiService = new AIService(
            context,
            mockAiProvider,
            tenantService,
            userService,
            expAccessService,
            new MockFileStorageService(),
            NullLogger<AIService>.Instance
        );

        var purgedCount = await aiService.PurgeExpiredConversationsAsync();
        Assert.True(purgedCount > 0);

        // 5. Confirmar que AIConversation desapareció físicamente de PostgreSQL
        var convDesaparecida = await context.AIConversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conv.Id);
        Assert.Null(convDesaparecida);

        // 6. Confirmar que AIUsageLog sigue existiendo
        var logPostPurga = await context.AIUsageLogs.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.Id == log.Id);
        Assert.NotNull(logPostPurga);

        // 7. Confirmar que sus valores no cambiaron
        Assert.Equal(log.Id, logPostPurga.Id);
        Assert.Equal(tenantId, logPostPurga.TenantId);
        Assert.Equal(userId, logPostPurga.UsuarioId);
        Assert.Equal(AICasoUso.ChatLibre, logPostPurga.CasoUso);
        Assert.Equal("mock-provider-special", logPostPurga.ProviderId);
        Assert.Equal("mock-model-special", logPostPurga.ModelId);
        Assert.True(logPostPurga.Exitoso);

        // 8. Confirmar que CreatedAt no cambió
        Assert.Equal(createdAtOriginal.ToString("yyyy-MM-dd HH:mm:ss"), logPostPurga.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        // 9. Confirmar que Tokens no cambiaron
        Assert.Equal(tokensEntradaOriginal, logPostPurga.TokensEntrada);
        Assert.Equal(tokensSalidaOriginal, logPostPurga.TokensSalida);
        Assert.Equal(totalTokensOriginal, logPostPurga.TotalTokens);
        Assert.Equal(duracionMsOriginal, logPostPurga.DuracionMs);

        // 10. Confirmar que ConversationId histórico no fue actualizado por la purga (permanece intacto)
        Assert.Equal(conv.Id, logPostPurga.ConversationId);

        // 11. Ejecutar UPDATE directo sobre AIUsageLog -> excepción 55000
        await using var conn = new NpgsqlConnection(PostgresConnectionString);
        await conn.OpenAsync();

        await using var cmdUpdate = new NpgsqlCommand(
            $"UPDATE ai_usage_logs SET \"TokensEntrada\" = 999 WHERE \"Id\" = '{log.Id}'",
            conn
        );
        var updateEx = await Assert.ThrowsAsync<PostgresException>(() => cmdUpdate.ExecuteNonQueryAsync());
        Assert.Equal("55000", updateEx.SqlState);
        Assert.Contains("inmutable", updateEx.Message, StringComparison.OrdinalIgnoreCase);

        // 12. Ejecutar DELETE directo sobre AIUsageLog -> excepción 55000
        await using var cmdDelete = new NpgsqlCommand(
            $"DELETE FROM ai_usage_logs WHERE \"Id\" = '{log.Id}'",
            conn
        );
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(() => cmdDelete.ExecuteNonQueryAsync());
        Assert.Equal("55000", deleteEx.SqlState);
        Assert.Contains("inmutable", deleteEx.Message, StringComparison.OrdinalIgnoreCase);
    }
}
