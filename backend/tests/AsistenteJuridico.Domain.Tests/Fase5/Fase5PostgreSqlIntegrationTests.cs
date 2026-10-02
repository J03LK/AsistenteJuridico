using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Alertas.DTOs;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using AsistenteJuridico.Application.Features.Tareas.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Common;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase5;

public class Fase5PostgreSqlIntegrationTests
{
    private const string PostgresConnectionString =
        "Host=localhost;Port=5433;Database=asistente_juridico;Username=aj_user;Password=REMOVED_SECRET";

    private class MockCurrentTenantService : ICurrentTenantService
    {
        public Guid? TenantId { get; set; }
        public string? TenantSlug => "test";
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
            Application.Common.Security.Permissions.AlertasRead,
            Application.Common.Security.Permissions.AlertasManage,
            Application.Common.Security.Permissions.AudienciasManage,
            Application.Common.Security.Permissions.TareasManage,
            Application.Common.Security.Permissions.ExpedientesRead
        ];
        public bool HasPermission(string permission) => true;
    }

    private class MockAuditService : IAuditService
    {
        public List<(string Entidad, string EntidadId, string Accion)> LoggedEvents { get; } = new();

        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
        {
            LoggedEvents.Add((entidad, entidadId, accion));
            return Task.CompletedTask;
        }
    }

    private ApplicationDbContext CreateRealDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var tenantService = tenantId.HasValue ? new MockCurrentTenantService { TenantId = tenantId } : null;
        return new ApplicationDbContext(options, tenantService);
    }

    private async Task<Tenant> SeedTenantAsync(string zonaHoraria = "America/Guayaquil")
    {
        using var context = CreateRealDbContext();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Nombre = "Estudio Fase 5 " + Guid.NewGuid().ToString("N")[..8],
            IdentificadorUrl = "tenant-" + Guid.NewGuid().ToString("N")[..8],
            ZonaHorariaId = zonaHoraria,
            Activo = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        return tenant;
    }

    private async Task<Usuario> SeedUsuarioAsync(Guid tenantId, string email, string rol)
    {
        using var context = CreateRealDbContext(tenantId);
        var uniqueEmail = $"{Guid.NewGuid():N}_{email}";
        var user = new Usuario
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = uniqueEmail,
            Email = uniqueEmail,
            NormalizedEmail = uniqueEmail.ToUpperInvariant(),
            NormalizedUserName = uniqueEmail.ToUpperInvariant(),
            NombreCompleto = "Abogado " + email,
            Rol = rol,
            Activo = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Usuarios.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<Expediente> SeedExpedienteAsync(Guid tenantId, Guid? abogadoId = null)
    {
        using var context = CreateRealDbContext(tenantId);
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8],
            NombreRazonSocial = "Cliente Test " + Guid.NewGuid().ToString("N")[..8],
            Activo = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Clientes.Add(cliente);

        var exp = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClienteId = cliente.Id,
            NumeroExpediente = "EXP-" + Guid.NewGuid().ToString("N")[..8],
            Titulo = "Caso Test " + Guid.NewGuid().ToString("N")[..8],
            Materia = "Civil",
            AbogadoResponsableId = abogadoId,
            Estado = EstadoExpediente.Abierto,
            CreatedAt = DateTime.UtcNow
        };
        context.Expedientes.Add(exp);
        await context.SaveChangesAsync();
        return exp;
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 1: Aislamiento Multi-Tenant
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test01_AislamientoMultiTenant_NoPermiteConsultarAlertasNiDashboardDeOtroTenant()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();

        var userA = await SeedUsuarioAsync(tenantA.Id, "userA@test.com", Roles.AdminEstudio);
        var userB = await SeedUsuarioAsync(tenantB.Id, "userB@test.com", Roles.AdminEstudio);

        var expA = await SeedExpedienteAsync(tenantA.Id, userA.Id);

        // Crear alerta en Tenant A
        using (var contextA = CreateRealDbContext(tenantA.Id))
        {
            contextA.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA.Id,
                UsuarioId = userA.Id,
                ExpedienteId = expA.Id,
                TipoOrigen = TipoOrigenAlerta.Audiencia,
                OrigenId = Guid.NewGuid(),
                ReglaAlerta = ReglaAlertaCodigo.Audiencia48Horas,
                Severidad = SeveridadAlerta.Alta,
                Titulo = "Alerta Confidencial Tenant A",
                Mensaje = "Mensaje exclusivo de Tenant A",
                FechaObjetivoUtc = DateTime.UtcNow.AddHours(48),
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
            await contextA.SaveChangesAsync();
        }

        // Consultar con servicio configurado para Tenant B
        using (var contextB = CreateRealDbContext(tenantB.Id))
        {
            var currentTenantB = new MockCurrentTenantService { TenantId = tenantB.Id };
            var currentUserB = new MockCurrentUserService { UserId = userB.Id, TenantId = tenantB.Id, Role = Roles.AdminEstudio };
            var auditService = new MockAuditService();

            var alertasServiceB = new AlertasService(
                contextB,
                currentUserB,
                currentTenantB,
                auditService,
                NullLogger<AlertasService>.Instance);

            var alertasTenantB = await alertasServiceB.GetAlertasAsync(new AlertasFilterRequest());

            // Aislamiento: Tenant B no ve absolutamente ninguna alerta de Tenant A
            Assert.Empty(alertasTenantB);
        }

        // Probar restricción de Foreign Key Compuesta cross-tenant
        using (var contextCross = CreateRealDbContext())
        {
            // Intentar insertar alerta en Tenant B apuntando a Expediente de Tenant A
            var alertaInvalida = new AlertaProcesal
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB.Id,
                UsuarioId = userB.Id,
                ExpedienteId = expA.Id, // EXPEDIENTE DE OTRO TENANT!
                TipoOrigen = TipoOrigenAlerta.Audiencia,
                OrigenId = Guid.NewGuid(),
                ReglaAlerta = ReglaAlertaCodigo.Audiencia48Horas,
                Severidad = SeveridadAlerta.Alta,
                Titulo = "Intrusión Cross-Tenant",
                Mensaje = "No debe permitirse",
                FechaObjetivoUtc = DateTime.UtcNow.AddHours(48),
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            };
            contextCross.AlertasProcesales.Add(alertaInvalida);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => contextCross.SaveChangesAsync());
            Assert.Contains("FK_alertas_procesales_expedientes", ex.InnerException?.Message ?? ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 2: Workers Concurrentes con Advisory Lock
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test02_WorkersConcurrentes_AdvisoryLock_SoloUnoProcesaPorTenant()
    {
        var tenant = await SeedTenantAsync();

        using var conn1 = new NpgsqlConnection(PostgresConnectionString);
        using var conn2 = new NpgsqlConnection(PostgresConnectionString);
        await conn1.OpenAsync();
        await conn2.OpenAsync();

        using var tx1 = await conn1.BeginTransactionAsync();
        using var tx2 = await conn2.BeginTransactionAsync();

        // Worker 1 toma el lock
        var lockAdquirido1 = await AdvisoryLockHelper.ObtenerLockTenantAsync(conn1, tenant.Id, tx1);
        Assert.True(lockAdquirido1, "El worker 1 debió obtener el lock.");

        // Worker 2 intenta tomar el lock concurrentemente para el mismo tenant
        var lockAdquirido2 = await AdvisoryLockHelper.ObtenerLockTenantAsync(conn2, tenant.Id, tx2);
        Assert.False(lockAdquirido2, "El worker 2 debió ser rechazado por lock ocupado.");

        // Worker 1 completa y commitea la transacción liberando el advisory lock transaccional
        await tx1.CommitAsync();
        await tx2.RollbackAsync();

        // Ahora worker 2 abre nueva transacción e intenta de nuevo: debe tener éxito
        using var tx2Nuevo = await conn2.BeginTransactionAsync();
        var lockAdquirido2Reintento = await AdvisoryLockHelper.ObtenerLockTenantAsync(conn2, tenant.Id, tx2Nuevo);
        Assert.True(lockAdquirido2Reintento, "Al liberarse el lock de worker 1, worker 2 debió adquirirlo.");
        await tx2Nuevo.CommitAsync();
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 3: Reprogramación Audiencia
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test03_ReprogramacionAudiencia_InvalidaAlertasAnterioresYGeneraNueva()
    {
        var tenant = await SeedTenantAsync();
        var user = await SeedUsuarioAsync(tenant.Id, "senior@estudio.com", Roles.AbogadoSenior);
        var exp = await SeedExpedienteAsync(tenant.Id, user.Id);

        var fechaOriginal = DateTime.UtcNow.AddDays(2); // 48 horas
        var audienciaId = Guid.NewGuid();

        // 1. Crear Audiencia
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var aud = new Audiencia
            {
                Id = audienciaId,
                TenantId = tenant.Id,
                ExpedienteId = exp.Id,
                FechaHora = fechaOriginal,
                SalaOVirtual = "Sala 101",
                TipoAudiencia = TipoAudiencia.Juicio,
                Estado = EstadoAudiencia.Programada,
                CreatedAt = DateTime.UtcNow
            };
            context.Audiencias.Add(aud);

            // Alerta activa generada para esta audiencia
            context.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UsuarioId = user.Id,
                ExpedienteId = exp.Id,
                TipoOrigen = TipoOrigenAlerta.Audiencia,
                OrigenId = audienciaId,
                ReglaAlerta = ReglaAlertaCodigo.Audiencia48Horas,
                Severidad = SeveridadAlerta.Alta,
                Titulo = "Audiencia próxima en 48 horas",
                Mensaje = "Preparar pruebas",
                FechaObjetivoUtc = fechaOriginal,
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
            await context.SaveChangesAsync();
        }

        // 2. Reprogramar Audiencia a 7 días en el futuro mediante AudienciaService
        var nuevaFecha = DateTime.UtcNow.AddDays(7);
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
            var currentUser = new MockCurrentUserService { UserId = user.Id, TenantId = tenant.Id, Role = Roles.AbogadoSenior };
            var auditService = new MockAuditService();
            var accessService = new ExpedienteAccessService(context, currentUser, currentTenant);

            var audienciaService = new AudienciaService(
                context,
                currentTenant,
                currentUser,
                accessService,
                auditService,
                new InlineValidator<CreateAudienciaDto>(),
                new InlineValidator<UpdateAudienciaDto>(),
                new InlineValidator<CambiarEstadoAudienciaDto>());

            var audActual = await context.Audiencias.AsNoTracking().FirstAsync(a => a.Id == audienciaId);

            await audienciaService.UpdateAudienciaAsync(audienciaId, new UpdateAudienciaDto(
                null,
                nuevaFecha,
                "Sala 102",
                TipoAudiencia.Juicio,
                "Reprogramación por motivos de salud del juez",
                audActual.Version));
        }

        // 3. Verificaciones de la transacción ACID
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var alertas = await context.AlertasProcesales
                .Where(a => a.OrigenId == audienciaId)
                .OrderBy(a => a.FechaDisparoUtc)
                .ToListAsync();

            // Debe existir la alerta original invalidada
            var alertaInvalidada = alertas.FirstOrDefault(a => a.EstadoResolucion == EstadoAlertaResolucion.InvalidaPorReprogramacion);
            Assert.NotNull(alertaInvalidada);
            Assert.NotNull(alertaInvalidada.ResueltaUtc);
            Assert.Contains("Reprogramación", alertaInvalidada.MotivoResolucion);

            // Y debe existir la nueva alerta activa correspondiente a la nueva fecha
            var nuevaAlerta = alertas.FirstOrDefault(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa);
            Assert.NotNull(nuevaAlerta);
            Assert.True(Math.Abs((nuevaAlerta.FechaObjetivoUtc - nuevaFecha).TotalMilliseconds) < 1000);
            Assert.Equal(ReglaAlertaCodigo.Audiencia7Dias, nuevaAlerta.ReglaAlerta);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 4: Idempotencia Histórica
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test04_IdempotenciaHistorica_AlertaPreviamenteResueltaNoSeDuplica()
    {
        var tenant = await SeedTenantAsync();
        var user = await SeedUsuarioAsync(tenant.Id, "abogado@estudio.com", Roles.AbogadoSenior);
        var exp = await SeedExpedienteAsync(tenant.Id, user.Id);

        var fechaAudiencia = DateTime.UtcNow.AddDays(2);
        var audId = Guid.NewGuid();

        using (var context = CreateRealDbContext(tenant.Id))
        {
            context.Audiencias.Add(new Audiencia
            {
                Id = audId,
                TenantId = tenant.Id,
                ExpedienteId = exp.Id,
                FechaHora = fechaAudiencia,
                SalaOVirtual = "Virtual",
                TipoAudiencia = TipoAudiencia.Preliminar,
                Estado = EstadoAudiencia.Programada,
                CreatedAt = DateTime.UtcNow
            });

            // Insertar alerta ya resuelta automáticamente en el pasado
            context.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UsuarioId = user.Id,
                ExpedienteId = exp.Id,
                TipoOrigen = TipoOrigenAlerta.Audiencia,
                OrigenId = audId,
                ReglaAlerta = ReglaAlertaCodigo.Audiencia48Horas,
                Severidad = SeveridadAlerta.Alta,
                Titulo = "Alerta pasada ya resuelta",
                Mensaje = "Mensaje",
                FechaObjetivoUtc = fechaAudiencia,
                FechaDisparoUtc = DateTime.UtcNow.AddHours(-1),
                EstadoResolucion = EstadoAlertaResolucion.ResueltaAutomaticamente,
                ResueltaUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Ejecutar el motor de alertas (Worker)
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
            var currentUser = new MockCurrentUserService { UserId = user.Id, TenantId = tenant.Id };
            var auditService = new MockAuditService();

            var alertasService = new AlertasService(
                context,
                currentUser,
                currentTenant,
                auditService,
                NullLogger<AlertasService>.Instance);

            await alertasService.ProcesarReglasAlertasTenantAsync(tenant.Id);

            // Verificar que la alerta asignada al usuario NO se duplicó
            var totalAlertasUsuario = await context.AlertasProcesales
                .CountAsync(a => a.OrigenId == audId && a.ReglaAlerta == ReglaAlertaCodigo.Audiencia48Horas && a.UsuarioId == user.Id);

            Assert.Equal(1, totalAlertasUsuario);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 5: Múltiples Destinatarios
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test05_MultiplesDestinatarios_ResponsableYSupervisorRecibenAlertaIndependiente()
    {
        var tenant = await SeedTenantAsync();
        var junior = await SeedUsuarioAsync(tenant.Id, "junior@estudio.com", Roles.AbogadoJunior);
        var exp = await SeedExpedienteAsync(tenant.Id, junior.Id);

        var audId = Guid.NewGuid();
        var fechaAud = DateTime.UtcNow.AddHours(20); // 24 Horas

        using (var context = CreateRealDbContext(tenant.Id))
        {
            context.Audiencias.Add(new Audiencia
            {
                Id = audId,
                TenantId = tenant.Id,
                ExpedienteId = exp.Id,
                FechaHora = fechaAud,
                SalaOVirtual = "Sala Penal",
                TipoAudiencia = TipoAudiencia.Juicio,
                Estado = EstadoAudiencia.Programada,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Ejecutar worker
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
            var currentUser = new MockCurrentUserService { UserId = junior.Id, TenantId = tenant.Id };
            var auditService = new MockAuditService();

            var alertasService = new AlertasService(
                context,
                currentUser,
                currentTenant,
                auditService,
                NullLogger<AlertasService>.Instance);

            await alertasService.ProcesarReglasAlertasTenantAsync(tenant.Id);

            var alertas = await context.AlertasProcesales
                .Where(a => a.OrigenId == audId && a.ReglaAlerta == ReglaAlertaCodigo.Audiencia24Horas)
                .ToListAsync();

            // Debe existir una alerta asignada al Junior y una alerta institucional (UsuarioId == null) para supervisor
            Assert.Contains(alertas, a => a.UsuarioId == junior.Id);
            Assert.Contains(alertas, a => a.UsuarioId == null);
            Assert.Equal(2, alertas.Count);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 6: Segunda Reprogramación
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test06_SegundaReprogramacion_MantieneHistoricoYGeneraTerceraAlerta()
    {
        var tenant = await SeedTenantAsync();
        var user = await SeedUsuarioAsync(tenant.Id, "senior2@estudio.com", Roles.AbogadoSenior);
        var exp = await SeedExpedienteAsync(tenant.Id, user.Id);

        var audId = Guid.NewGuid();
        var fecha1 = DateTime.UtcNow.AddDays(2);
        var fecha2 = DateTime.UtcNow.AddDays(7);
        var fecha3 = DateTime.UtcNow.AddHours(40);

        using (var context = CreateRealDbContext(tenant.Id))
        {
            context.Audiencias.Add(new Audiencia
            {
                Id = audId,
                TenantId = tenant.Id,
                ExpedienteId = exp.Id,
                FechaHora = fecha1,
                SalaOVirtual = "Virtual",
                TipoAudiencia = TipoAudiencia.Conciliacion,
                Estado = EstadoAudiencia.Programada,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
        var currentUser = new MockCurrentUserService { UserId = user.Id, TenantId = tenant.Id, Role = Roles.AbogadoSenior };
        var auditService = new MockAuditService();

        // Primera reprogramación
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var accessService = new ExpedienteAccessService(context, currentUser, currentTenant);
            var audienciaService = new AudienciaService(
                context, currentTenant, currentUser, accessService, auditService,
                new InlineValidator<CreateAudienciaDto>(), new InlineValidator<UpdateAudienciaDto>(), new InlineValidator<CambiarEstadoAudienciaDto>());

            var aud = await context.Audiencias.FirstAsync(a => a.Id == audId);
            await audienciaService.UpdateAudienciaAsync(audId, new UpdateAudienciaDto(null, fecha2, "Virtual", TipoAudiencia.Conciliacion, "Reprogramación 1", aud.Version));
        }

        // Segunda reprogramación
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var accessService = new ExpedienteAccessService(context, currentUser, currentTenant);
            var audienciaService = new AudienciaService(
                context, currentTenant, currentUser, accessService, auditService,
                new InlineValidator<CreateAudienciaDto>(), new InlineValidator<UpdateAudienciaDto>(), new InlineValidator<CambiarEstadoAudienciaDto>());

            var aud = await context.Audiencias.FirstAsync(a => a.Id == audId);
            await audienciaService.UpdateAudienciaAsync(audId, new UpdateAudienciaDto(null, fecha3, "Virtual", TipoAudiencia.Conciliacion, "Reprogramación 2", aud.Version));
        }

        // Validar historial completo
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var alertas = await context.AlertasProcesales
                .Where(a => a.OrigenId == audId)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync();

            var invalidas = alertas.Where(a => a.EstadoResolucion == EstadoAlertaResolucion.InvalidaPorReprogramacion).ToList();
            var activas = alertas.Where(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa).ToList();

            Assert.True(invalidas.Count >= 1, "Debe haber alertas invalidadas por reprogramaciones.");
            Assert.NotEmpty(activas);
            Assert.All(activas, a => Assert.True(Math.Abs((a.FechaObjetivoUtc - fecha3).TotalMilliseconds) < 1000));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 7: Tarea Reprogramada
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test07_TareaReprogramada_InvalidaAlertasPreviasYGeneraNueva()
    {
        var tenant = await SeedTenantAsync();
        var user = await SeedUsuarioAsync(tenant.Id, "asistente@estudio.com", Roles.AsistenteLegal);
        var exp = await SeedExpedienteAsync(tenant.Id, user.Id);

        var tareaId = Guid.NewGuid();
        var vencimientoOriginal = DateTime.UtcNow.AddHours(36); // 48 horas

        using (var context = CreateRealDbContext(tenant.Id))
        {
            context.Tareas.Add(new Tarea
            {
                Id = tareaId,
                TenantId = tenant.Id,
                ExpedienteId = exp.Id,
                AsignadoAUsuarioId = user.Id,
                Titulo = "Elaborar minuta",
                FechaVencimiento = vencimientoOriginal,
                Prioridad = Prioridad.Alta,
                Estado = EstadoTarea.Pendiente,
                CreatedAt = DateTime.UtcNow
            });

            context.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UsuarioId = user.Id,
                ExpedienteId = exp.Id,
                TipoOrigen = TipoOrigenAlerta.Tarea,
                OrigenId = tareaId,
                ReglaAlerta = ReglaAlertaCodigo.Tarea48Horas,
                Severidad = SeveridadAlerta.Media,
                Titulo = "Tarea por vencer en 48h",
                Mensaje = "Vence pronto",
                FechaObjetivoUtc = vencimientoOriginal,
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
            await context.SaveChangesAsync();
        }

        // Reprogramar fecha de vencimiento a 5 días
        var nuevoVencimiento = DateTime.UtcNow.AddDays(5);
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
            var currentUser = new MockCurrentUserService { UserId = user.Id, TenantId = tenant.Id, Role = Roles.AdminEstudio };
            var auditService = new MockAuditService();
            var accessService = new ExpedienteAccessService(context, currentUser, currentTenant);

            var tareaService = new TareaService(
                context, currentTenant, currentUser, accessService, auditService,
                new InlineValidator<CreateTareaDto>(), new InlineValidator<UpdateTareaDto>(), new InlineValidator<CambiarEstadoTareaDto>());

            var tareaActual = await context.Tareas.FirstAsync(t => t.Id == tareaId);
            await tareaService.UpdateTareaAsync(tareaId, new UpdateTareaDto(
                "Elaborar minuta reprogramada",
                "Nueva fecha",
                nuevoVencimiento,
                Prioridad.Media,
                user.Id,
                tareaActual.Version));
        }

        // Verificar que la alerta previa se invalidó
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var alertaPrevia = await context.AlertasProcesales
                .FirstAsync(a => a.OrigenId == tareaId && a.FechaObjetivoUtc == vencimientoOriginal);

            Assert.Equal(EstadoAlertaResolucion.InvalidaPorReprogramacion, alertaPrevia.EstadoResolucion);
            Assert.Contains("Reprogramación", alertaPrevia.MotivoResolucion);
            Assert.NotNull(alertaPrevia.ResueltaUtc);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 8: Resolución Automática al completar Tarea
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test08_ResolucionAutomatica_AlCompletarTareaOAudiencia_SeResuelvenAlertas()
    {
        var tenant = await SeedTenantAsync();
        var user = await SeedUsuarioAsync(tenant.Id, "abogado8@estudio.com", Roles.AbogadoSenior);
        var exp = await SeedExpedienteAsync(tenant.Id, user.Id);

        var tareaId = Guid.NewGuid();
        using (var context = CreateRealDbContext(tenant.Id))
        {
            context.Tareas.Add(new Tarea
            {
                Id = tareaId,
                TenantId = tenant.Id,
                ExpedienteId = exp.Id,
                AsignadoAUsuarioId = user.Id,
                Titulo = "Revisar casación",
                FechaVencimiento = DateTime.UtcNow.AddHours(20),
                Prioridad = Prioridad.Alta,
                Estado = EstadoTarea.EnProgreso,
                CreatedAt = DateTime.UtcNow
            });

            context.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UsuarioId = user.Id,
                ExpedienteId = exp.Id,
                TipoOrigen = TipoOrigenAlerta.Tarea,
                OrigenId = tareaId,
                ReglaAlerta = ReglaAlertaCodigo.Tarea48Horas,
                Severidad = SeveridadAlerta.Alta,
                Titulo = "Tarea próxima",
                Mensaje = "Urgente",
                FechaObjetivoUtc = DateTime.UtcNow.AddHours(20),
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
            await context.SaveChangesAsync();
        }

        // Completar la tarea
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
            var currentUser = new MockCurrentUserService { UserId = user.Id, TenantId = tenant.Id, Role = Roles.AbogadoSenior };
            var auditService = new MockAuditService();
            var accessService = new ExpedienteAccessService(context, currentUser, currentTenant);

            var tareaService = new TareaService(
                context, currentTenant, currentUser, accessService, auditService,
                new InlineValidator<CreateTareaDto>(), new InlineValidator<UpdateTareaDto>(), new InlineValidator<CambiarEstadoTareaDto>());

            var tarea = await context.Tareas.FirstAsync(t => t.Id == tareaId);
            await tareaService.CambiarEstadoAsync(tareaId, new CambiarEstadoTareaDto(EstadoTarea.Completada, tarea.Version));

            // Verificar auditoría
            Assert.Contains(auditService.LoggedEvents, e => e.Accion == "ALERTA_RESOLUCION_AUTOMATICA");
        }

        // Verificar estado de la alerta en la BD
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var alerta = await context.AlertasProcesales.FirstAsync(a => a.OrigenId == tareaId);
            Assert.Equal(EstadoAlertaResolucion.ResueltaAutomaticamente, alerta.EstadoResolucion);
            Assert.NotNull(alerta.ResueltaUtc);
            Assert.Contains("Completada", alerta.MotivoResolucion);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 9: Descarte Manual y Concurrencia Optimista (xmin)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test09_DescarteManual_Y_ConcurrenciaXmin()
    {
        var tenant = await SeedTenantAsync();
        var user = await SeedUsuarioAsync(tenant.Id, "senior9@estudio.com", Roles.AbogadoSenior);
        var exp = await SeedExpedienteAsync(tenant.Id, user.Id);

        var alertaId = Guid.NewGuid();
        using (var context = CreateRealDbContext(tenant.Id))
        {
            context.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = alertaId,
                TenantId = tenant.Id,
                UsuarioId = user.Id,
                ExpedienteId = exp.Id,
                TipoOrigen = TipoOrigenAlerta.Audiencia,
                OrigenId = Guid.NewGuid(),
                ReglaAlerta = ReglaAlertaCodigo.Audiencia7Dias,
                Severidad = SeveridadAlerta.Baja,
                Titulo = "Audiencia próxima",
                Mensaje = "Preparar testigos",
                FechaObjetivoUtc = DateTime.UtcNow.AddDays(7),
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
            await context.SaveChangesAsync();
        }

        // Simular conflicto de concurrencia optimista con versión errónea / desfasada
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
            var currentUser = new MockCurrentUserService { UserId = user.Id, TenantId = tenant.Id, Role = Roles.AbogadoSenior };
            var auditService = new MockAuditService();

            var alertasService = new AlertasService(context, currentUser, currentTenant, auditService, NullLogger<AlertasService>.Instance);

            // Pasamos version = 99999 (antigua / inválida)
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                alertasService.DescartarAlertaAsync(alertaId, "Descarte con token desactualizado", version: 99999u));
        }

        // Descarte exitoso sin conflicto
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
            var currentUser = new MockCurrentUserService { UserId = user.Id, TenantId = tenant.Id, Role = Roles.AbogadoSenior };
            var auditService = new MockAuditService();

            var alertasService = new AlertasService(context, currentUser, currentTenant, auditService, NullLogger<AlertasService>.Instance);

            var alertaActual = await context.AlertasProcesales.FirstAsync(a => a.Id == alertaId);
            var resultado = await alertasService.DescartarAlertaAsync(alertaId, "Ya se gestionó", version: alertaActual.Version);

            Assert.Equal(EstadoAlertaResolucion.DescartadaManualmente, resultado.EstadoResolucion);
            Assert.Contains(auditService.LoggedEvents, e => e.Accion == "DESCARTAR_ALERTA");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 14: Timezone — Validación Estricta (422 sin fallback)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test14_Timezone_ValidacionEstricta_422SiZonaInvalida_SinFallbackSilencioso()
    {
        var tenant = await SeedTenantAsync(zonaHoraria: "Zona/Invalida_Que_No_Existe");

        using var context = CreateRealDbContext(tenant.Id);
        var currentTenant = new MockCurrentTenantService { TenantId = tenant.Id };
        var currentUser = new MockCurrentUserService { TenantId = tenant.Id, Role = Roles.AdminEstudio };

        var agendaService = new AgendaService(
            context,
            currentUser,
            currentTenant,
            NullLogger<AgendaService>.Instance);

        var ex = await Assert.ThrowsAsync<TenantTimeZoneInvalidException>(() =>
            agendaService.GetEventosHoyAsync());

        Assert.Contains("Zona/Invalida_Que_No_Existe", ex.Message);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 15: Worker sin HttpContext
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test15_Worker_AlertasBackgroundService_EjecutaSinHttpContext()
    {
        var tenant = await SeedTenantAsync();
        var user = await SeedUsuarioAsync(tenant.Id, "worker_test@estudio.com", Roles.AdminEstudio);
        var exp = await SeedExpedienteAsync(tenant.Id, user.Id);

        using (var context = CreateRealDbContext(tenant.Id))
        {
            context.Audiencias.Add(new Audiencia
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ExpedienteId = exp.Id,
                FechaHora = DateTime.UtcNow.AddDays(1), // 24 Horas
                SalaOVirtual = "Sala A",
                TipoAudiencia = TipoAudiencia.EvaluacionYPreparatoria,
                Estado = EstadoAudiencia.Programada,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Ejecutar procesamiento con CurrentTenantContext manual y SIN HttpContext
        using (var context = CreateRealDbContext(tenant.Id))
        {
            var tenantService = new MockCurrentTenantService { TenantId = tenant.Id };
            var userService = new MockCurrentUserService { UserId = null, Role = null, Email = null }; // Sin usuario autenticado
            var auditService = new MockAuditService();

            var alertasService = new AlertasService(
                context,
                userService,
                tenantService,
                auditService,
                NullLogger<AlertasService>.Instance);

            // Debe completarse exitosamente
            await alertasService.ProcesarReglasAlertasTenantAsync(tenant.Id);

            var alertas = await context.AlertasProcesales.Where(a => a.TenantId == tenant.Id).ToListAsync();
            Assert.NotEmpty(alertas);
        }
    }
}
