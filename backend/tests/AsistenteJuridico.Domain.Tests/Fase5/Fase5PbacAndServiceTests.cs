using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Agenda.DTOs;
using AsistenteJuridico.Application.Features.Alertas.DTOs;
using AsistenteJuridico.Application.Features.Dashboard.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase5;

public class Fase5PbacAndServiceTests
{
    private class MockCurrentTenantService : ICurrentTenantService
    {
        public Guid? TenantId { get; set; }
        public string? TenantSlug => "test-tenant";
        public bool IsMultiTenantContext => TenantId.HasValue;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
    }

    private class MockCurrentUserService : ICurrentUserService
    {
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public Guid? TenantId { get; set; }
        public string? Email { get; set; } = "user@estudio.com";
        public string? Role { get; set; } = Roles.AbogadoJunior;
        public bool IsAuthenticated => true;
        public IEnumerable<string> Permissions => [Application.Common.Security.Permissions.AlertasRead, Application.Common.Security.Permissions.AgendaRead, Application.Common.Security.Permissions.DashboardRead];
        public bool HasPermission(string permission) => true;
    }

    private class MockAuditService : IAuditService
    {
        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private ApplicationDbContext CreateInMemoryDbContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("Fase5PbacDb_" + Guid.NewGuid().ToString("N"))
            .Options;
        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        return new ApplicationDbContext(options, tenantService);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 10: PBAC Abogado Junior
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test10_PBAC_AbogadoJunior_SoloVeSusAlertasYExpedientesAsignados()
    {
        var tenantId = Guid.NewGuid();
        var juniorId = Guid.NewGuid();
        var seniorId = Guid.NewGuid();

        using var context = CreateInMemoryDbContext(tenantId);

        var expJunior = new Expediente { Id = Guid.NewGuid(), TenantId = tenantId, NumeroExpediente = "EXP-JUN", Titulo = "Caso Junior", AbogadoResponsableId = juniorId, Estado = EstadoExpediente.Abierto };
        var expSenior = new Expediente { Id = Guid.NewGuid(), TenantId = tenantId, NumeroExpediente = "EXP-SEN", Titulo = "Caso Senior", AbogadoResponsableId = seniorId, Estado = EstadoExpediente.Abierto };
        context.Expedientes.AddRange(expJunior, expSenior);

        // 1. Alerta personal del Junior
        context.AlertasProcesales.Add(new AlertaProcesal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = juniorId,
            ExpedienteId = expJunior.Id,
            TipoOrigen = TipoOrigenAlerta.Audiencia,
            OrigenId = Guid.NewGuid(),
            ReglaAlerta = ReglaAlertaCodigo.Audiencia24Horas,
            Severidad = SeveridadAlerta.Alta,
            Titulo = "Alerta del Junior",
            Mensaje = "Msg",
            FechaObjetivoUtc = DateTime.UtcNow.AddDays(1),
            EstadoResolucion = EstadoAlertaResolucion.Activa
        });

        // 2. Alerta personal del Senior
        context.AlertasProcesales.Add(new AlertaProcesal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = seniorId,
            ExpedienteId = expSenior.Id,
            TipoOrigen = TipoOrigenAlerta.Audiencia,
            OrigenId = Guid.NewGuid(),
            ReglaAlerta = ReglaAlertaCodigo.Audiencia24Horas,
            Severidad = SeveridadAlerta.Alta,
            Titulo = "Alerta del Senior",
            Mensaje = "Msg",
            FechaObjetivoUtc = DateTime.UtcNow.AddDays(1),
            EstadoResolucion = EstadoAlertaResolucion.Activa
        });

        // 3. Alerta Institucional (UsuarioId == null)
        context.AlertasProcesales.Add(new AlertaProcesal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = null,
            ExpedienteId = expSenior.Id,
            TipoOrigen = TipoOrigenAlerta.Expediente,
            OrigenId = expSenior.Id,
            ReglaAlerta = ReglaAlertaCodigo.InactividadOperativa60Dias,
            Severidad = SeveridadAlerta.Critica,
            Titulo = "Alerta Institucional Estudio",
            Mensaje = "Msg",
            FechaObjetivoUtc = DateTime.UtcNow,
            EstadoResolucion = EstadoAlertaResolucion.Activa
        });

        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { UserId = juniorId, TenantId = tenantId, Role = Roles.AbogadoJunior };
        var audit = new MockAuditService();

        var service = new AlertasService(context, currentUser, currentTenant, audit, NullLogger<AlertasService>.Instance);
        var alertas = await service.GetAlertasAsync(new AlertasFilterRequest());

        // Junior solo debe ver su única alerta asignada
        Assert.Single(alertas);
        Assert.Equal(juniorId, alertas[0].UsuarioId);
        Assert.Equal("Alerta del Junior", alertas[0].Titulo);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 11: PBAC Asistente Legal
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test11_PBAC_AsistenteLegal_SoloVeAlertasDeSusTareasAsignadas()
    {
        var tenantId = Guid.NewGuid();
        var asistenteId = Guid.NewGuid();
        var otroUsuarioId = Guid.NewGuid();

        using var context = CreateInMemoryDbContext(tenantId);

        // Alerta propia del asistente
        context.AlertasProcesales.Add(new AlertaProcesal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = asistenteId,
            TipoOrigen = TipoOrigenAlerta.Tarea,
            OrigenId = Guid.NewGuid(),
            ReglaAlerta = ReglaAlertaCodigo.Tarea48Horas,
            Severidad = SeveridadAlerta.Media,
            Titulo = "Tarea Asistente",
            Mensaje = "Msg",
            FechaObjetivoUtc = DateTime.UtcNow.AddHours(40),
            EstadoResolucion = EstadoAlertaResolucion.Activa
        });

        // Alerta de otro usuario
        context.AlertasProcesales.Add(new AlertaProcesal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UsuarioId = otroUsuarioId,
            TipoOrigen = TipoOrigenAlerta.Tarea,
            OrigenId = Guid.NewGuid(),
            ReglaAlerta = ReglaAlertaCodigo.Tarea48Horas,
            Severidad = SeveridadAlerta.Media,
            Titulo = "Tarea de Otro",
            Mensaje = "Msg",
            FechaObjetivoUtc = DateTime.UtcNow.AddHours(40),
            EstadoResolucion = EstadoAlertaResolucion.Activa
        });

        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { UserId = asistenteId, TenantId = tenantId, Role = Roles.AsistenteLegal };
        var audit = new MockAuditService();

        var service = new AlertasService(context, currentUser, currentTenant, audit, NullLogger<AlertasService>.Instance);
        var alertas = await service.GetAlertasAsync(new AlertasFilterRequest());

        Assert.Single(alertas);
        Assert.Equal(asistenteId, alertas[0].UsuarioId);
        Assert.Equal("Tarea Asistente", alertas[0].Titulo);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 12: Agenda — Límite Máximo 500 y Default 200
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test12_Agenda_LimiteMaximo500YDefault200()
    {
        var tenantId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        context.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Nombre = "Tenant Agenda",
            IdentificadorUrl = "tenant-agenda",
            ZonaHorariaId = "America/Guayaquil",
            Activo = true
        });

        var desde = DateTime.UtcNow.Date;
        var hasta = desde.AddDays(7);

        // Insertar 600 tareas en el rango
        for (int i = 0; i < 600; i++)
        {
            context.Tareas.Add(new Tarea
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Titulo = $"Tarea {i}",
                FechaVencimiento = desde.AddHours(i % 100),
                Prioridad = Prioridad.Media,
                Estado = EstadoTarea.Pendiente
            });
        }
        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };

        var agendaService = new AgendaService(context, currentUser, currentTenant, NullLogger<AgendaService>.Instance);

        // Caso A: PageSize = 0 o negativo -> Toma Default 200
        var resDefault = await agendaService.GetEventosAsync(new AgendaFilterRequest
        {
            FechaDesdeUtc = desde,
            FechaHastaUtc = hasta,
            PageSize = 0
        });
        Assert.Equal(200, resDefault.Items.Count);
        Assert.Equal(200, resDefault.PageSize);

        // Caso B: PageSize = 1000 -> Clamped a Máximo 500
        var resMax = await agendaService.GetEventosAsync(new AgendaFilterRequest
        {
            FechaDesdeUtc = desde,
            FechaHastaUtc = hasta,
            PageSize = 1000
        });
        Assert.Equal(500, resMax.Items.Count);
        Assert.Equal(500, resMax.PageSize);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 13: Dashboard — KPIs Generales e Inactividad Operativa
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test13_Dashboard_KpisGeneralesEInactividadOperativa()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        // Expedientes activos
        var expActivo1 = new Expediente { Id = Guid.NewGuid(), TenantId = tenantId, NumeroExpediente = "EXP-1", Titulo = "Exp 1", Estado = EstadoExpediente.Abierto, CreatedAt = DateTime.UtcNow.AddDays(-40) };
        var expActivo2 = new Expediente { Id = Guid.NewGuid(), TenantId = tenantId, NumeroExpediente = "EXP-2", Titulo = "Exp 2", Estado = EstadoExpediente.EnTramite, CreatedAt = DateTime.UtcNow.AddDays(-70) };
        var expActivo3 = new Expediente { Id = Guid.NewGuid(), TenantId = tenantId, NumeroExpediente = "EXP-3", Titulo = "Exp 3", Estado = EstadoExpediente.Abierto, CreatedAt = DateTime.UtcNow };
        var expCerrado = new Expediente { Id = Guid.NewGuid(), TenantId = tenantId, NumeroExpediente = "EXP-4", Titulo = "Exp 4", Estado = EstadoExpediente.Cerrado, CreatedAt = DateTime.UtcNow.AddDays(-10) };
        context.Expedientes.AddRange(expActivo1, expActivo2, expActivo3, expCerrado);

        // Tareas pendientes
        context.Tareas.Add(new Tarea { Id = Guid.NewGuid(), TenantId = tenantId, Titulo = "T1", Estado = EstadoTarea.Pendiente, FechaVencimiento = DateTime.UtcNow.AddDays(2), Prioridad = Prioridad.Alta });
        context.Tareas.Add(new Tarea { Id = Guid.NewGuid(), TenantId = tenantId, Titulo = "T2", Estado = EstadoTarea.EnProgreso, FechaVencimiento = DateTime.UtcNow.AddDays(3), Prioridad = Prioridad.Urgente });
        context.Tareas.Add(new Tarea { Id = Guid.NewGuid(), TenantId = tenantId, Titulo = "T3", Estado = EstadoTarea.Completada, FechaVencimiento = DateTime.UtcNow.AddDays(-1), Prioridad = Prioridad.Baja });

        // Audiencia próximas 7 días (en expActivo3 recién creado)
        context.Audiencias.Add(new Audiencia { Id = Guid.NewGuid(), TenantId = tenantId, ExpedienteId = expActivo3.Id, FechaHora = DateTime.UtcNow.AddDays(4), Estado = EstadoAudiencia.Programada, SalaOVirtual = "Sala 1", TipoAudiencia = TipoAudiencia.Juicio });
        context.Audiencias.Add(new Audiencia { Id = Guid.NewGuid(), TenantId = tenantId, ExpedienteId = expActivo3.Id, FechaHora = DateTime.UtcNow.AddDays(15), Estado = EstadoAudiencia.Programada, SalaOVirtual = "Sala 2", TipoAudiencia = TipoAudiencia.Juicio }); // Fuera de 7 días

        // Alertas Alta / Crítica sin resolver
        context.AlertasProcesales.Add(new AlertaProcesal { Id = Guid.NewGuid(), TenantId = tenantId, Titulo = "A1", Severidad = SeveridadAlerta.Alta, EstadoResolucion = EstadoAlertaResolucion.Activa, FechaObjetivoUtc = DateTime.UtcNow, TipoOrigen = TipoOrigenAlerta.Audiencia, OrigenId = Guid.NewGuid(), ReglaAlerta = ReglaAlertaCodigo.Audiencia48Horas });
        context.AlertasProcesales.Add(new AlertaProcesal { Id = Guid.NewGuid(), TenantId = tenantId, Titulo = "A2", Severidad = SeveridadAlerta.Critica, EstadoResolucion = EstadoAlertaResolucion.Activa, FechaObjetivoUtc = DateTime.UtcNow, TipoOrigen = TipoOrigenAlerta.Tarea, OrigenId = Guid.NewGuid(), ReglaAlerta = ReglaAlertaCodigo.TareaVencida });
        context.AlertasProcesales.Add(new AlertaProcesal { Id = Guid.NewGuid(), TenantId = tenantId, Titulo = "A3", Severidad = SeveridadAlerta.Media, EstadoResolucion = EstadoAlertaResolucion.Activa, FechaObjetivoUtc = DateTime.UtcNow, TipoOrigen = TipoOrigenAlerta.Tarea, OrigenId = Guid.NewGuid(), ReglaAlerta = ReglaAlertaCodigo.Tarea48Horas }); // Media

        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { UserId = adminId, TenantId = tenantId, Role = Roles.AdminEstudio };

        var dashboardService = new DashboardService(context, currentUser, currentTenant, NullLogger<DashboardService>.Instance);
        var resumen = await dashboardService.GetResumenAsync();

        Assert.Equal(3, resumen.Kpis.TotalExpedientesActivos);
        Assert.Equal(2, resumen.Kpis.TotalTareasPendientes);
        Assert.Equal(1, resumen.Kpis.TotalAudienciasProximas7Dias);
        Assert.Equal(2, resumen.Kpis.TotalAlertasAltaCriticasSinResolver);

        // Inactividad: expActivo1 tiene 40 días inactivo (>30), expActivo2 tiene 70 días (>30 y >60)
        Assert.Equal(2, resumen.Inactividad.ExpedientesInactivos30Dias);
        Assert.Equal(1, resumen.Inactividad.ExpedientesInactivos60Dias);
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 16: Eficiencia — División por Cero retorna null
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Test16_Dashboard_Eficiencia_DivisionPorCero_RetornaNull()
    {
        var tenantId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        // 0 expedientes cerrados
        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };

        var dashboardService = new DashboardService(context, currentUser, currentTenant, NullLogger<DashboardService>.Instance);
        var eficiencia = await dashboardService.GetEficienciaAsync(new EficienciaRequest());

        Assert.Equal(0, eficiencia.TotalExpedientesCerradosEvaluados);
        Assert.Equal(0, eficiencia.ExpedientesConPlazoEstimado);
        Assert.Null(eficiencia.PorcentajeCumplimiento); // Exactamente null, nunca 0.0 ni excepción!
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 1. TipoOrigenInactividadEsExpediente
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task TipoOrigenInactividadEsExpediente()
    {
        Assert.Equal(3, (int)TipoOrigenAlerta.Expediente);

        var tenantId = Guid.NewGuid();
        var abogadoId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        var exp = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = "EXP-INACT",
            Titulo = "Expediente Inactivo",
            AbogadoResponsableId = abogadoId,
            Estado = EstadoExpediente.Abierto,
            CreatedAt = DateTime.UtcNow.AddDays(-45)
        };
        context.Expedientes.Add(exp);
        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };
        var audit = new MockAuditService();

        var service = new AlertasService(context, currentUser, currentTenant, audit, NullLogger<AlertasService>.Instance);
        await service.ProcesarReglasAlertasTenantAsync(tenantId, CancellationToken.None);

        var alertas = await context.AlertasProcesales.Where(a => a.TenantId == tenantId).ToListAsync();
        Assert.NotEmpty(alertas);
        foreach (var alerta in alertas)
        {
            Assert.Equal(TipoOrigenAlerta.Expediente, alerta.TipoOrigen);
            Assert.Equal(exp.Id, alerta.OrigenId);
            Assert.Equal(exp.Id, alerta.ExpedienteId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 2. ValoresEstadoAlertaCoincidenConContrato
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void ValoresEstadoAlertaCoincidenConContrato()
    {
        Assert.Equal(1, (int)EstadoAlertaResolucion.Activa);
        Assert.Equal(2, (int)EstadoAlertaResolucion.ResueltaAutomaticamente);
        Assert.Equal(3, (int)EstadoAlertaResolucion.InvalidaPorReprogramacion);
        Assert.Equal(4, (int)EstadoAlertaResolucion.DescartadaManualmente);
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 3. ValoresEstadoAgendaCoincidenConContrato
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void ValoresEstadoAgendaCoincidenConContrato()
    {
        Assert.Equal(1, (int)EstadoEventoAgenda.Programada);
        Assert.Equal(2, (int)EstadoEventoAgenda.EnProgreso);
        Assert.Equal(3, (int)EstadoEventoAgenda.RealizadaOCompletada);
        Assert.Equal(4, (int)EstadoEventoAgenda.Cancelada);
        Assert.Equal(5, (int)EstadoEventoAgenda.Suspendida);

        // Mapeo exhaustivo debe lanzar ArgumentOutOfRangeException para valores no soportados
        Assert.Throws<ArgumentOutOfRangeException>(() => AgendaService.MapearEstadoAudiencia((EstadoAudiencia)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgendaService.MapearEstadoTarea((EstadoTarea)999));
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 4. AdvisoryLockUsaXxHash64
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void AdvisoryLockUsaXxHash64()
    {
        var tenantId = Guid.NewGuid();
        var key = AsistenteJuridico.Infrastructure.Common.AdvisoryLockHelper.ObtenerTenantLockKey(tenantId);

        var bytes = tenantId.ToByteArray();
        ulong expectedHash = System.IO.Hashing.XxHash64.HashToUInt64(bytes, seed: 0);
        long expectedKey = unchecked((long)expectedHash);

        Assert.Equal(expectedKey, key);
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 5. EndpointMarcarLeidaUsaRutaAprobada
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void EndpointMarcarLeidaUsaRutaAprobada()
    {
        var controllerType = typeof(AsistenteJuridico.API.Controllers.v1.AlertasController);
        var method = controllerType.GetMethod(nameof(AsistenteJuridico.API.Controllers.v1.AlertasController.MarcarLeida));
        Assert.NotNull(method);

        var putAttr = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPutAttribute), false)
                            .Cast<Microsoft.AspNetCore.Mvc.HttpPutAttribute>()
                            .FirstOrDefault();

        Assert.NotNull(putAttr);
        Assert.Equal("{id:guid}/marcar-leida", putAttr.Template);
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 6. DestinatariosAudiencia
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task DestinatariosAudiencia()
    {
        var tenantId = Guid.NewGuid();
        var abogadoId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        var exp = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = "EXP-AUD",
            Titulo = "Caso Audiencia",
            AbogadoResponsableId = abogadoId,
            Estado = EstadoExpediente.Abierto
        };
        context.Expedientes.Add(exp);

        var aud = new Audiencia
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = exp.Id,
            TipoAudiencia = TipoAudiencia.Juicio,
            FechaHora = DateTime.UtcNow.AddHours(20),
            Estado = EstadoAudiencia.Programada,
            SalaOVirtual = "Sala 1"
        };
        context.Audiencias.Add(aud);
        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };
        var audit = new MockAuditService();

        var service = new AlertasService(context, currentUser, currentTenant, audit, NullLogger<AlertasService>.Instance);
        await service.ProcesarReglasAlertasTenantAsync(tenantId, CancellationToken.None);

        var alertas = await context.AlertasProcesales.Where(a => a.TenantId == tenantId && a.OrigenId == aud.Id).ToListAsync();
        Assert.NotEmpty(alertas);

        var alertasPorRegla = alertas.GroupBy(a => a.ReglaAlerta);
        foreach (var grupo in alertasPorRegla)
        {
            Assert.Contains(grupo, a => a.UsuarioId == abogadoId);
            Assert.Contains(grupo, a => a.UsuarioId == null);
            Assert.Equal(2, grupo.Count());
        }
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 7. DestinatariosTarea48h
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task DestinatariosTarea48h()
    {
        var tenantId = Guid.NewGuid();
        var asignadoId = Guid.NewGuid();
        var abogadoId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        var exp = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = "EXP-T48",
            Titulo = "Caso T48",
            AbogadoResponsableId = abogadoId,
            Estado = EstadoExpediente.Abierto
        };
        context.Expedientes.Add(exp);

        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = exp.Id,
            Titulo = "Tarea 48 Horas",
            AsignadoAUsuarioId = asignadoId,
            FechaVencimiento = DateTime.UtcNow.AddHours(24),
            Prioridad = Prioridad.Alta,
            Estado = EstadoTarea.Pendiente
        };
        context.Tareas.Add(tarea);
        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };
        var audit = new MockAuditService();

        var service = new AlertasService(context, currentUser, currentTenant, audit, NullLogger<AlertasService>.Instance);
        await service.ProcesarReglasAlertasTenantAsync(tenantId, CancellationToken.None);

        var alertas = await context.AlertasProcesales.Where(a => a.TenantId == tenantId && a.OrigenId == tarea.Id).ToListAsync();
        Assert.Single(alertas);
        Assert.Equal(asignadoId, alertas[0].UsuarioId);
        Assert.Equal(ReglaAlertaCodigo.Tarea48Horas, alertas[0].ReglaAlerta);
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 8. DestinatariosTareaVencida
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task DestinatariosTareaVencida()
    {
        var tenantId = Guid.NewGuid();
        var asignadoId = Guid.NewGuid();
        var abogadoId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        var exp = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = "EXP-TVEN",
            Titulo = "Caso TVEN",
            AbogadoResponsableId = abogadoId,
            Estado = EstadoExpediente.Abierto
        };
        context.Expedientes.Add(exp);

        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = exp.Id,
            Titulo = "Tarea Vencida",
            AsignadoAUsuarioId = asignadoId,
            FechaVencimiento = DateTime.UtcNow.AddHours(-10),
            Prioridad = Prioridad.Alta,
            Estado = EstadoTarea.Pendiente
        };
        context.Tareas.Add(tarea);
        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };
        var audit = new MockAuditService();

        var service = new AlertasService(context, currentUser, currentTenant, audit, NullLogger<AlertasService>.Instance);
        await service.ProcesarReglasAlertasTenantAsync(tenantId, CancellationToken.None);

        var alertas = await context.AlertasProcesales.Where(a => a.TenantId == tenantId && a.OrigenId == tarea.Id).ToListAsync();
        Assert.Equal(2, alertas.Count);
        Assert.Contains(alertas, a => a.UsuarioId == asignadoId);
        Assert.Contains(alertas, a => a.UsuarioId == abogadoId);
        Assert.DoesNotContain(alertas, a => a.UsuarioId == null);
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 9. DestinatariosInactividad
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task DestinatariosInactividad()
    {
        var tenantId = Guid.NewGuid();
        var abogadoId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        var exp = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = "EXP-INACT-35",
            Titulo = "Caso Inactivo",
            AbogadoResponsableId = abogadoId,
            Estado = EstadoExpediente.Abierto,
            CreatedAt = DateTime.UtcNow.AddDays(-35)
        };
        context.Expedientes.Add(exp);
        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };
        var audit = new MockAuditService();

        var service = new AlertasService(context, currentUser, currentTenant, audit, NullLogger<AlertasService>.Instance);
        await service.ProcesarReglasAlertasTenantAsync(tenantId, CancellationToken.None);

        var alertas = await context.AlertasProcesales.Where(a => a.TenantId == tenantId && a.OrigenId == exp.Id).ToListAsync();
        Assert.Equal(2, alertas.Count);
        Assert.Contains(alertas, a => a.UsuarioId == abogadoId);
        Assert.Contains(alertas, a => a.UsuarioId == null);
        Assert.All(alertas, a => Assert.Equal(TipoOrigenAlerta.Expediente, a.TipoOrigen));
        Assert.All(alertas, a => Assert.Equal(exp.Id, a.OrigenId));
    }

    // ─────────────────────────────────────────────────────────────
    // FASE 5.1 — 10. AgendaUsaIntervaloSemiabierto
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task AgendaUsaIntervaloSemiabierto()
    {
        var tenantId = Guid.NewGuid();
        using var context = CreateInMemoryDbContext(tenantId);

        var tenant = new Tenant
        {
            Id = tenantId,
            Nombre = "Tenant Agenda Semiabierta",
            IdentificadorUrl = "tenant-agenda-semiabierta",
            ZonaHorariaId = "America/Guayaquil",
            Activo = true
        };
        context.Tenants.Add(tenant);

        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil");
        var ahoraLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var inicioDiaLocal = ahoraLocal.Date;
        var siguienteDiaLocal = inicioDiaLocal.AddDays(1);

        var desdeUtc = TimeZoneInfo.ConvertTimeToUtc(inicioDiaLocal, tz);
        var siguienteDiaUtc = TimeZoneInfo.ConvertTimeToUtc(siguienteDiaLocal, tz);

        var exp = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = "EXP-AGENDA",
            Titulo = "Caso Agenda",
            Estado = EstadoExpediente.Abierto
        };
        context.Expedientes.Add(exp);

        // Evento 1: Exactamente al inicio (desdeUtc) -> DEBE INCLUIRSE (>= desdeUtc)
        context.Audiencias.Add(new Audiencia
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = exp.Id,
            TipoAudiencia = TipoAudiencia.Conciliacion,
            FechaHora = desdeUtc,
            Estado = EstadoAudiencia.Programada,
            SalaOVirtual = "Virtual"
        });

        // Evento 2: Durante el día -> DEBE INCLUIRSE
        context.Audiencias.Add(new Audiencia
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = exp.Id,
            TipoAudiencia = TipoAudiencia.Juicio,
            FechaHora = desdeUtc.AddHours(5),
            Estado = EstadoAudiencia.Programada,
            SalaOVirtual = "Sala 1"
        });

        // Evento 3: Exactamente en el límite siguienteDiaUtc (medianoche del día siguiente) -> DEBE EXCLUIRSE (< siguienteDiaUtc)
        context.Audiencias.Add(new Audiencia
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = exp.Id,
            TipoAudiencia = TipoAudiencia.Preliminar,
            FechaHora = siguienteDiaUtc,
            Estado = EstadoAudiencia.Programada,
            SalaOVirtual = "Sala 2"
        });

        await context.SaveChangesAsync();

        var currentTenant = new MockCurrentTenantService { TenantId = tenantId };
        var currentUser = new MockCurrentUserService { TenantId = tenantId, Role = Roles.AdminEstudio };

        var agendaService = new AgendaService(context, currentUser, currentTenant, NullLogger<AgendaService>.Instance);
        var eventos = await agendaService.GetEventosHoyAsync();

        Assert.Equal(2, eventos.Count);
        Assert.Contains(eventos, e => e.FechaHoraInicioUtc == desdeUtc);
        Assert.Contains(eventos, e => e.FechaHoraInicioUtc == desdeUtc.AddHours(5));
        Assert.DoesNotContain(eventos, e => e.FechaHoraInicioUtc == siguienteDiaUtc);
    }
}

