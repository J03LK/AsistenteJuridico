using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Expedientes.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Application.Features.Expedientes.Validators;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Xunit;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class ExpedienteClosingAndTasksTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private class MockCurrentTenantService : ICurrentTenantService
    {
        public Guid? TenantId { get; set; }
        public string? TenantSlug => "test";
        public bool IsMultiTenantContext => TenantId.HasValue;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
    }

    private class MockCurrentUserService : ICurrentUserService
    {
        public Guid? UserId { get; set; }
        public Guid? TenantId { get; set; }
        public string? Email { get; set; }
        public string? Role { get; set; }
        public bool IsAuthenticated => UserId.HasValue;
        public List<string> Permissions { get; set; } = [];
        public bool HasPermission(string permission) => Permissions.Contains(permission);
    }

    private class MockAuditService : IAuditService
    {
        public List<(string Entity, string EntityId, string Action)> Logs { get; } = [];
        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
        {
            Logs.Add((entidad, entidadId, accion));
            return Task.CompletedTask;
        }
    }

    private class MockCodeGenerator : IExpedienteCodeGenerator
    {
        public Task<string> GenerateNextCodeAsync(Guid tenantId, int? anio = null, CancellationToken cancellationToken = default)
            => Task.FromResult("EXP-2026-0001");
    }

    private ApplicationDbContext CreateDbContext(ICurrentTenantService tenantService)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ApplicationDbContext(options, tenantService);
    }

    private (ExpedienteService Service, MockCurrentUserService UserService, MockAuditService AuditService, ApplicationDbContext Context, Guid ClienteId) SetupService()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var context = CreateDbContext(tenantService);

        var clienteId = Guid.NewGuid();
        context.Clientes.Add(new Cliente
        {
            Id = clienteId,
            TenantId = _tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "1710034065",
            NombreRazonSocial = "Cliente Test Cierre",
            Email = "cliente@test.com",
            Activo = true
        });
        context.SaveChanges();

        var userService = new MockCurrentUserService
        {
            UserId = Guid.NewGuid(),
            Email = "abogado@estudio.com",
            Role = Roles.AbogadoSenior
        };
        var accessService = new ExpedienteAccessService(context, userService, tenantService);
        var auditService = new MockAuditService();
        var codeGen = new MockCodeGenerator();

        var createValidator = new InlineValidator<CreateExpedienteDto>();
        var updateValidator = new InlineValidator<UpdateExpedienteDto>();
        var cambiarEstadoValidator = new CambiarEstadoExpedienteValidator();
        var vincularProcesoValidator = new VincularProcesoValidator();

        var service = new ExpedienteService(
            context,
            tenantService,
            userService,
            accessService,
            codeGen,
            auditService,
            createValidator,
            updateValidator,
            cambiarEstadoValidator,
            vincularProcesoValidator);

        return (service, userService, auditService, context, clienteId);
    }

    [Fact]
    public async Task CerrarExpediente_ConTareasPendientesYSinConfirmacion_LanzaValidationException()
    {
        var (service, userService, _, context, clienteId) = SetupService();

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Caso Activo",
            Materia = "Civil",
            Estado = EstadoExpediente.EnTramite,
            ClienteId = clienteId
        };
        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Tarea pendiente urgente",
            Estado = EstadoTarea.Pendiente
        };
        context.Expedientes.Add(expediente);
        context.Tareas.Add(tarea);
        await context.SaveChangesAsync();

        var dto = new CambiarEstadoExpedienteDto(EstadoExpediente.Cerrado, false, null, 0);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.CambiarEstadoAsync(expediente.Id, dto));

        Assert.Contains("No se puede cerrar el expediente: existen 1 tarea(s)", ex.Message);
    }

    [Fact]
    public async Task CierreForzado_SinPermisoCloseForce_LanzaForbiddenException()
    {
        var (service, userService, _, context, clienteId) = SetupService();
        // Usuario sin permiso ExpedientesCloseForce
        userService.Permissions.Clear();

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0002",
            Titulo = "Caso a Forzar Cierre",
            Materia = "Laboral",
            Estado = EstadoExpediente.EnTramite,
            ClienteId = clienteId
        };
        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Tarea en progreso",
            Estado = EstadoTarea.EnProgreso
        };
        context.Expedientes.Add(expediente);
        context.Tareas.Add(tarea);
        await context.SaveChangesAsync();

        var dto = new CambiarEstadoExpedienteDto(
            EstadoExpediente.Cerrado,
            true,
            "Cliente desistió formalmente del proceso.",
            0);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.CambiarEstadoAsync(expediente.Id, dto));

        Assert.Contains("Expedientes.CloseForce", ex.Message);
    }

    [Fact]
    public async Task CierreForzado_ConPermisoYMotivoValido_CancelaTareasYCompletaCierre()
    {
        var (service, userService, auditService, context, clienteId) = SetupService();
        // Asignar permiso de cierre forzado
        userService.Permissions.Add(Permissions.ExpedientesCloseForce);

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0003",
            Titulo = "Caso Acuerdo Extrajudicial",
            Materia = "Civil",
            Estado = EstadoExpediente.EnTramite,
            ClienteId = clienteId
        };
        var tarea1 = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Tarea 1 activa",
            Estado = EstadoTarea.Pendiente
        };
        var tarea2 = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Tarea 2 en progreso",
            Estado = EstadoTarea.EnProgreso
        };
        context.Expedientes.Add(expediente);
        context.Tareas.AddRange(tarea1, tarea2);
        await context.SaveChangesAsync();

        var dto = new CambiarEstadoExpedienteDto(
            EstadoExpediente.Cerrado,
            true,
            "Transacción extrajudicial aprobada por ambas partes.",
            0);

        var result = await service.CambiarEstadoAsync(expediente.Id, dto);

        Assert.Equal(EstadoExpediente.Cerrado, result.Estado);

        // Verificar que las tareas activas fueron canceladas
        var t1 = await context.Tareas.FindAsync(tarea1.Id);
        var t2 = await context.Tareas.FindAsync(tarea2.Id);
        Assert.Equal(EstadoTarea.Cancelada, t1!.Estado);
        Assert.Equal(EstadoTarea.Cancelada, t2!.Estado);

        // Verificar que la auditoría registró el cierre forzado
        Assert.Contains(auditService.Logs, l => l.Entity == "Expediente" && l.Action == "FORCE_CLOSE");
    }
}
