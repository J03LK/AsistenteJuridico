using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Expedientes.DTOs;
using AsistenteJuridico.Application.Features.Expedientes.Validators;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Xunit;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class ProcesoJudicialConcurrencyTests
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
        public IEnumerable<string> Permissions { get; set; } = [];
        public bool HasPermission(string permission) => false;
    }

    private class MockAuditService : IAuditService
    {
        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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

    private (ExpedienteService Service, ApplicationDbContext Context, Guid ClienteId) SetupService()
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
            NombreRazonSocial = "Cliente Test Proceso",
            Email = "proceso@cliente.com",
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

        return (service, context, clienteId);
    }

    [Fact]
    public async Task VincularProceso_ReemplazaPrincipalAnterior_Atomicamente()
    {
        var (service, context, clienteId) = SetupService();

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Caso Principal",
            Materia = "Civil",
            Estado = EstadoExpediente.Abierto,
            ClienteId = clienteId
        };
        var proceso1 = new ProcesoJudicial
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroProceso = "17230202300001",
            Judicatura = "Unidad Judicial Civil",
            EstadoJudicial = "En trámite"
        };
        var proceso2 = new ProcesoJudicial
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroProceso = "17230202300002",
            Judicatura = "Sala Civil Corte Provincial",
            EstadoJudicial = "En trámite"
        };

        context.Expedientes.Add(expediente);
        context.ProcesosJudiciales.AddRange(proceso1, proceso2);
        await context.SaveChangesAsync();

        // 1. Vincular proceso 1 como principal
        var vincularDto1 = new VincularProcesoDto(proceso1.Id, EsPrincipal: true, Observaciones: "Primera instancia");
        await service.VincularProcesoAsync(expediente.Id, vincularDto1);

        var epj1 = await context.ExpedienteProcesosJudiciales
            .FirstOrDefaultAsync(x => x.ExpedienteId == expediente.Id && x.ProcesoJudicialId == proceso1.Id);
        Assert.NotNull(epj1);
        Assert.True(epj1.EsPrincipal);

        // 2. Vincular proceso 2 como nuevo principal -> Debe desmarcar a proceso 1
        var vincularDto2 = new VincularProcesoDto(proceso2.Id, EsPrincipal: true, Observaciones: "Segunda instancia / apelación");
        await service.VincularProcesoAsync(expediente.Id, vincularDto2);

        var epj1After = await context.ExpedienteProcesosJudiciales
            .FirstOrDefaultAsync(x => x.ExpedienteId == expediente.Id && x.ProcesoJudicialId == proceso1.Id);
        var epj2After = await context.ExpedienteProcesosJudiciales
            .FirstOrDefaultAsync(x => x.ExpedienteId == expediente.Id && x.ProcesoJudicialId == proceso2.Id);

        Assert.NotNull(epj1After);
        Assert.NotNull(epj2After);
        Assert.False(epj1After.EsPrincipal); // Desmarcado
        Assert.True(epj2After.EsPrincipal);  // Nuevo principal
    }

    [Fact]
    public async Task VincularProceso_ProcesoYaVinculado_LanzaConflictException()
    {
        var (service, context, clienteId) = SetupService();

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0002",
            Titulo = "Caso Duplicado",
            Materia = "Penal",
            Estado = EstadoExpediente.Abierto,
            ClienteId = clienteId
        };
        var proceso = new ProcesoJudicial
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroProceso = "09281202300001",
            Judicatura = "Unidad Penal Guayaquil",
            EstadoJudicial = "Instrucción Fiscal"
        };

        context.Expedientes.Add(expediente);
        context.ProcesosJudiciales.Add(proceso);
        await context.SaveChangesAsync();

        var dto = new VincularProcesoDto(proceso.Id, EsPrincipal: false, Observaciones: "Causa Penal");
        await service.VincularProcesoAsync(expediente.Id, dto);

        // Intentar vincular de nuevo
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.VincularProcesoAsync(expediente.Id, dto));
    }
}
