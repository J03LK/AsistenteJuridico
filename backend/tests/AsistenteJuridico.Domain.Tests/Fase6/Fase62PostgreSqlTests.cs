using AsistenteJuridico.Application.Common.Exceptions;
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

namespace AsistenteJuridico.Domain.Tests.Fase6;

/// <summary>
/// Fase 6.2 — Pruebas contra PostgreSQL 16 real: purga estrictamente tenant-aware, esquema construido
/// desde cero únicamente con migraciones y exclusividad de EstadoIa con concurrencia real por xmin.
/// </summary>
public class Fase62PostgreSqlTests
{
    private static string DevConnectionString => TestConfiguration.PostgresConnectionString;

    private static ApplicationDbContext CreateContext(string connectionString, Guid? tenantId) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
        new TestTenantService { TenantId = tenantId });

    private static AIService CreateAIService(ApplicationDbContext context, Guid tenantId, Guid userId, string role = Roles.AdminEstudio)
    {
        var tenantService = new TestTenantService { TenantId = tenantId };
        var userService = new TestUserService { UserId = userId, TenantId = tenantId, Role = role };
        return new AIService(
            context,
            new MockAIProvider(NullLogger<MockAIProvider>.Instance, simulateLatencyMs: 0),
            tenantService,
            userService,
            new ExpedienteAccessService(context, userService, tenantService),
            new TestFileStorageService(),
            NullLogger<AIService>.Instance);
    }

    private static async Task SeedTenantAndUserAsync(ApplicationDbContext context, Guid tenantId, Guid userId)
    {
        context.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Nombre = "Estudio Fase 6.2 " + tenantId.ToString("N")[..6],
            IdentificadorUrl = "fase62-" + tenantId.ToString("N")[..12],
            Plan = "Profesional",
            Activo = true,
            ZonaHorariaId = "America/Guayaquil",
            CreatedAt = DateTime.UtcNow
        });
        context.Usuarios.Add(new Usuario
        {
            Id = userId,
            TenantId = tenantId,
            Email = $"admin_{userId:N}@fase62.test",
            NormalizedEmail = $"ADMIN_{userId:N}@FASE62.TEST",
            UserName = $"admin_{userId:N}",
            NormalizedUserName = $"ADMIN_{userId:N}",
            NombreCompleto = "Administrador Fase 6.2",
            Rol = Roles.AdminEstudio,
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private sealed record EscenarioRetencion(Guid PurgableId, Guid ExpiradaActivaId, Guid RecienteId, Guid MensajeId, Guid UsageLogId);

    /// <summary>
    /// Crea en un tenant: una conversación con borrado lógico de hace 45 días (purgable), una activa de
    /// hace 400 días (expirada), una reciente, un mensaje y un AIUsageLog que referencia a la purgable.
    /// </summary>
    private static async Task<EscenarioRetencion> SeedEscenarioRetencionAsync(ApplicationDbContext context, Guid tenantId, Guid userId)
    {
        AIConversation Conversacion(string titulo, DateTime createdAt, bool isDeleted, DateTime? deletedAt) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = userId,
            Titulo = titulo,
            CasoUso = AICasoUso.ChatLibre,
            CreatedAt = createdAt,
            UpdatedAt = deletedAt ?? createdAt,
            IsDeleted = isDeleted,
            DeletedAt = deletedAt,
            DeletedBy = isDeleted ? "usuario" : null
        };

        var purgable = Conversacion("Purgable", DateTime.UtcNow.AddDays(-60), true, DateTime.UtcNow.AddDays(-45));
        var expirada = Conversacion("Expirada activa", DateTime.UtcNow.AddDays(-400), false, null);
        var reciente = Conversacion("Reciente", DateTime.UtcNow.AddDays(-1), false, null);
        context.AIConversations.AddRange(purgable, expirada, reciente);

        var mensaje = new AIMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = purgable.Id,
            Rol = AIRolMensaje.User,
            Contenido = "Mensaje de la conversación purgable",
            SystemPromptVersion = "v1",
            CreatedAt = DateTime.UtcNow.AddDays(-60)
        };
        context.AIMessages.Add(mensaje);

        var log = new AIUsageLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = userId,
            ConversationId = purgable.Id,
            CasoUso = AICasoUso.ChatLibre,
            ProviderId = "mock-ai-provider",
            ModelId = "mock-legal-v1",
            TokensEntrada = 120,
            TokensSalida = 80,
            TotalTokens = 200,
            DuracionMs = 150,
            CostoEstimadoUsd = 0.0001m,
            Exitoso = true,
            CreatedAt = DateTime.UtcNow.AddDays(-60)
        };
        context.AIUsageLogs.Add(log);

        await context.SaveChangesAsync();
        return new EscenarioRetencion(purgable.Id, expirada.Id, reciente.Id, mensaje.Id, log.Id);
    }

    private static Task<List<AIConversation>> ConversacionesAsync(ApplicationDbContext context, Guid tenantId) =>
        context.AIConversations.IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync();

    // ─────────────────────────────────────────────────────────────
    // CORRECCIÓN 1: Purga tenant-aware con dos tenants
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Purga_ComoTenantA_SoloAfectaTenantA_TenantBIntacto()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        EscenarioRetencion escenarioA, escenarioB;
        List<AIConversation> antesB;
        AIUsageLog logBAntes, logAAntes;
        await using (var seed = CreateContext(DevConnectionString, null))
        {
            await SeedTenantAndUserAsync(seed, tenantA, userA);
            await SeedTenantAndUserAsync(seed, tenantB, userB);
            escenarioA = await SeedEscenarioRetencionAsync(seed, tenantA, userA);
            escenarioB = await SeedEscenarioRetencionAsync(seed, tenantB, userB);
            antesB = await ConversacionesAsync(seed, tenantB);
            logAAntes = await seed.AIUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == escenarioA.UsageLogId);
            logBAntes = await seed.AIUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == escenarioB.UsageLogId);
        }

        // Purga ejecutada en el contexto autenticado del Tenant A
        int purgadas;
        await using (var contextA = CreateContext(DevConnectionString, tenantA))
        {
            purgadas = await CreateAIService(contextA, tenantA, userA).PurgeExpiredConversationsAsync();
        }

        Assert.Equal(1, purgadas);

        await using var verify = CreateContext(DevConnectionString, null);

        // Tenant A: purgable eliminada físicamente (con sus mensajes), expirada pasa a borrado lógico, reciente intacta
        var despuesA = await ConversacionesAsync(verify, tenantA);
        Assert.DoesNotContain(despuesA, c => c.Id == escenarioA.PurgableId);
        Assert.False(await verify.AIMessages.IgnoreQueryFilters().AnyAsync(m => m.Id == escenarioA.MensajeId));
        var expiradaA = Assert.Single(despuesA, c => c.Id == escenarioA.ExpiradaActivaId);
        Assert.True(expiradaA.IsDeleted);
        Assert.Equal("SystemRetentionPolicy", expiradaA.DeletedBy);
        Assert.False(Assert.Single(despuesA, c => c.Id == escenarioA.RecienteId).IsDeleted);

        // Tenant B: exactamente igual que antes de la purga
        var despuesB = await ConversacionesAsync(verify, tenantB);
        Assert.Equal(antesB.Count, despuesB.Count);
        foreach (var antes in antesB)
        {
            var despues = Assert.Single(despuesB, c => c.Id == antes.Id);
            Assert.Equal(antes.IsDeleted, despues.IsDeleted);
            Assert.Equal(antes.DeletedAt, despues.DeletedAt);
            Assert.Equal(antes.DeletedBy, despues.DeletedBy);
            Assert.Equal(antes.UpdatedAt, despues.UpdatedAt);
        }
        Assert.True(await verify.AIMessages.IgnoreQueryFilters().AnyAsync(m => m.Id == escenarioB.MensajeId));

        // AIUsageLog de ambos tenants: sigue existiendo y sin ningún cambio (ConversationId histórico incluido)
        foreach (var antes in new[] { logAAntes, logBAntes })
        {
            var despues = await verify.AIUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == antes.Id);
            Assert.Equal(antes.ConversationId, despues.ConversationId);
            Assert.Equal(antes.TokensEntrada, despues.TokensEntrada);
            Assert.Equal(antes.TotalTokens, despues.TotalTokens);
            Assert.Equal(antes.CreatedAt, despues.CreatedAt);
        }
    }

    [Fact]
    public async Task Purga_SinContextoDeTenant_EsRechazada()
    {
        await using var context = CreateContext(DevConnectionString, null);
        var userService = new TestUserService { Role = Roles.AdminEstudio };
        var tenantService = new TestTenantService();
        var aiService = new AIService(
            context, new MockAIProvider(NullLogger<MockAIProvider>.Instance), tenantService, userService,
            new ExpedienteAccessService(context, userService, tenantService), new TestFileStorageService(), NullLogger<AIService>.Instance);

        await Assert.ThrowsAsync<ForbiddenException>(() => aiService.PurgeExpiredConversationsAsync());
    }

    // ─────────────────────────────────────────────────────────────
    // CORRECCIONES 2 y 3: base PostgreSQL 16 creada desde cero solo con migraciones
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task BaseDesdeCero_Migraciones_DejanAIUsageLogSinFkYConTriggers()
    {
        var dbName = $"aj_fresh_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(DevConnectionString) { Database = "postgres" }.ConnectionString;
        var freshConnectionString = new NpgsqlConnectionStringBuilder(DevConnectionString) { Database = dbName }.ConnectionString;

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            // 1. Ejecutar TODAS las migraciones sobre la base vacía
            await using (var context = CreateContext(freshConnectionString, null))
            {
                await context.Database.MigrateAsync();

                var aplicadas = (await context.Database.GetAppliedMigrationsAsync()).ToList();
                Assert.Contains(aplicadas, m => m.EndsWith("_AddFase6AILegalAssistant"));
                Assert.Contains(aplicadas, m => m.EndsWith("_Fase62DropAIUsageLogConversationForeignKey"));
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }

            await using var conn = new NpgsqlConnection(freshConnectionString);
            await conn.OpenAsync();

            async Task<List<string>> ConsultarAsync(string sql)
            {
                var filas = new List<string>();
                await using var cmd = new NpgsqlCommand(sql, conn);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    filas.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i)?.ToString())));
                }
                return filas;
            }

            // 2. Tablas de IA
            var tablas = await ConsultarAsync(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' " +
                "AND table_name IN ('ai_conversations','ai_messages','ai_usage_logs') ORDER BY 1");
            Assert.Equal(["ai_conversations", "ai_messages", "ai_usage_logs"], tablas);

            // 3. AIUsageLog: sin FK hacia ai_conversations; conserva FKs tenant-aware a tenants y usuarios
            var fks = await ConsultarAsync(
                "SELECT conname || '|' || confrelid::regclass::text FROM pg_constraint " +
                "WHERE conrelid = 'ai_usage_logs'::regclass AND contype = 'f' ORDER BY 1");
            Assert.DoesNotContain(fks, f => f.EndsWith("|ai_conversations"));
            Assert.Contains("FK_ai_usage_logs_tenants_TenantId|tenants", fks);
            Assert.Contains("FK_ai_usage_logs_usuarios_TenantId_UsuarioId|usuarios", fks);

            // 4. Triggers de inmutabilidad BEFORE UPDATE / BEFORE DELETE
            var triggers = await ConsultarAsync(
                "SELECT trigger_name || '|' || action_timing || '|' || event_manipulation FROM information_schema.triggers " +
                "WHERE event_object_table = 'ai_usage_logs' ORDER BY 1");
            Assert.Contains("trg_ai_usage_logs_prevent_update|BEFORE|UPDATE", triggers);
            Assert.Contains("trg_ai_usage_logs_prevent_delete|BEFORE|DELETE", triggers);

            // 5. Columnas TenantId obligatorias y ConversationId histórico opcional
            var columnas = await ConsultarAsync(
                "SELECT table_name || '.' || column_name || '|' || is_nullable FROM information_schema.columns " +
                "WHERE table_name IN ('ai_conversations','ai_messages','ai_usage_logs') AND column_name IN ('TenantId','ConversationId')");
            Assert.Contains("ai_conversations.TenantId|NO", columnas);
            Assert.Contains("ai_messages.TenantId|NO", columnas);
            Assert.Contains("ai_usage_logs.TenantId|NO", columnas);
            Assert.Contains("ai_usage_logs.ConversationId|YES", columnas);

            // 6. Índices y restricciones relevantes
            var indices = await ConsultarAsync(
                "SELECT indexname FROM pg_indexes WHERE tablename IN ('ai_conversations','ai_messages','ai_usage_logs')");
            Assert.Contains("IX_ai_usage_logs_TenantId_ConversationId", indices);
            Assert.Contains("IX_ai_usage_logs_TenantId_UsuarioId_CreatedAt", indices);
            Assert.Contains("IX_ai_messages_TenantId_ConversationId_CreatedAt", indices);
            Assert.Contains("AK_ai_conversations_TenantId_Id", indices);
            var fkMensajes = await ConsultarAsync(
                "SELECT confdeltype::text FROM pg_constraint WHERE conname = 'FK_ai_messages_ai_conversations_TenantId_ConversationId'");
            Assert.Equal(["c"], fkMensajes); // ON DELETE CASCADE: los mensajes se purgan con su conversación

            // 7. Purga en la base nueva: la conversación se elimina y el AIUsageLog que la referencia sobrevive
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            EscenarioRetencion escenario;
            await using (var seed = CreateContext(freshConnectionString, null))
            {
                await SeedTenantAndUserAsync(seed, tenantId, userId);
                escenario = await SeedEscenarioRetencionAsync(seed, tenantId, userId);
            }

            await using (var context = CreateContext(freshConnectionString, tenantId))
            {
                Assert.Equal(1, await CreateAIService(context, tenantId, userId).PurgeExpiredConversationsAsync());
            }

            await using (var verify = CreateContext(freshConnectionString, null))
            {
                Assert.False(await verify.AIConversations.IgnoreQueryFilters().AnyAsync(c => c.Id == escenario.PurgableId));
                var log = await verify.AIUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == escenario.UsageLogId);
                Assert.Equal(escenario.PurgableId, log.ConversationId);
            }

            // 8. UPDATE y DELETE directos rechazados por los triggers
            foreach (var sql in new[]
            {
                $"UPDATE ai_usage_logs SET \"TokensEntrada\" = 999 WHERE \"Id\" = '{escenario.UsageLogId}'",
                $"DELETE FROM ai_usage_logs WHERE \"Id\" = '{escenario.UsageLogId}'"
            })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Equal("55000", ex.SqlState);
                Assert.Equal(
                    "AIUsageLog es un registro inmutable. Operaciones de UPDATE y DELETE están prohibidas por auditoría.",
                    ex.MessageText);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 31 (refuerzo): exclusividad de EstadoIa con concurrencia real por xmin
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExclusividadDocumento_DosOperacionesConcurrentes_LaSegundaRecibeConflictoPorXmin()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        Guid documentoId;

        await using (var seed = CreateContext(DevConnectionString, null))
        {
            await SeedTenantAndUserAsync(seed, tenantId, userId);
            var cliente = new Cliente
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Identificacion = "1799999999001",
                NombreRazonSocial = "Cliente xmin F6.2",
                TipoIdentificacion = TipoIdentificacion.Ruc,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var expediente = new Expediente
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                NumeroExpediente = "EXP-X62-" + Guid.NewGuid().ToString("N")[..6],
                Titulo = "Expediente xmin",
                Materia = "Civil",
                Estado = EstadoExpediente.Abierto,
                ClienteId = cliente.Id,
                AbogadoResponsableId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var documento = new Documento
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ExpedienteId = expediente.Id,
                Titulo = "Documento concurrente.pdf",
                TipoDocumento = "Contrato",
                RutaAlmacenamiento = "tenant/concurrente.pdf",
                ContentType = "application/pdf",
                EstadoIa = EstadoProcesamientoIa.Pendiente,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            seed.AddRange(cliente, expediente, documento);
            await seed.SaveChangesAsync();
            documentoId = documento.Id;
        }

        // La operación 2 ya leyó el documento en estado Pendiente (versión xmin original)...
        await using var context2 = CreateContext(DevConnectionString, tenantId);
        var lecturaObsoleta = await context2.Documentos.FirstAsync(d => d.Id == documentoId);
        Assert.Equal(EstadoProcesamientoIa.Pendiente, lecturaObsoleta.EstadoIa);

        // ...mientras la operación 1 toma el documento y lo pasa a Procesando
        await using (var context1 = CreateContext(DevConnectionString, tenantId))
        {
            var doc = await context1.Documentos.FirstAsync(d => d.Id == documentoId);
            doc.EstadoIa = EstadoProcesamientoIa.Procesando;
            await context1.SaveChangesAsync();
        }

        // La operación 2 intenta la misma transición con su versión obsoleta: xmin la rechaza (HTTP 409)
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateAIService(context2, tenantId, userId).ExtractFromDocumentAsync(new AIExtractRequestDto(documentoId)));
        Assert.Contains("concurrencia", ex.Message, StringComparison.OrdinalIgnoreCase);

        // El estado y los metadatos de la operación 1 no fueron sobrescritos
        await using var verify = CreateContext(DevConnectionString, tenantId);
        var final = await verify.Documentos.AsNoTracking().FirstAsync(d => d.Id == documentoId);
        Assert.Equal(EstadoProcesamientoIa.Procesando, final.EstadoIa);
        Assert.Null(final.MetadatosJson);
    }
}
