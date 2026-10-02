using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class ExpedienteAccessServiceSecurityTests
{
    private readonly Guid _tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _tenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

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
        public bool HasPermission(string permission) => Permissions.Contains(permission);
    }

    private ApplicationDbContext CreateDbContext(ICurrentTenantService? tenantService = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options, tenantService);
    }

    [Fact]
    public async Task EnsureCanAccessExpedienteAsync_SuperAdmin_LanzaForbiddenException()
    {
        using var context = CreateDbContext();
        var tenantService = new MockCurrentTenantService { TenantId = _tenantA };
        var userService = new MockCurrentUserService
        {
            UserId = Guid.NewGuid(),
            Email = "superadmin@sistema.com",
            Role = Roles.SuperAdmin
        };

        var accessService = new ExpedienteAccessService(context, userService, tenantService);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            accessService.EnsureCanAccessExpedienteAsync(Guid.NewGuid(), false));

        Assert.Contains("SuperAdmin", ex.Message);
    }

    [Fact]
    public async Task EnsureCanAccessExpedienteAsync_AbogadoJuniorNoAsignado_LanzaForbiddenException()
    {
        using var context = CreateDbContext();
        var juniorUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantA,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Caso Laboral",
            Materia = "Laboral",
            Estado = EstadoExpediente.Abierto,
            ClienteId = Guid.NewGuid(),
            AbogadoResponsableId = otherUserId
        };
        context.Expedientes.Add(expediente);
        await context.SaveChangesAsync();

        var tenantService = new MockCurrentTenantService { TenantId = _tenantA };
        var userService = new MockCurrentUserService
        {
            UserId = juniorUserId,
            Email = "junior@estudio.com",
            Role = Roles.AbogadoJunior
        };

        var accessService = new ExpedienteAccessService(context, userService, tenantService);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            accessService.EnsureCanAccessExpedienteAsync(expediente.Id, false));

        Assert.Contains("asignado como responsable", ex.Message);
    }

    [Fact]
    public async Task EnsureCanAccessExpedienteAsync_AbogadoJuniorAsignado_PermiteAcceso()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantA };
        using var context = CreateDbContext(tenantService);
        var juniorUserId = Guid.NewGuid();

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantA,
            NumeroExpediente = "EXP-2026-0002",
            Titulo = "Caso Penal",
            Materia = "Penal",
            Estado = EstadoExpediente.Abierto,
            ClienteId = Guid.NewGuid(),
            AbogadoResponsableId = juniorUserId
        };
        context.Expedientes.Add(expediente);
        await context.SaveChangesAsync();

        var userService = new MockCurrentUserService
        {
            UserId = juniorUserId,
            Email = "junior@estudio.com",
            Role = Roles.AbogadoJunior
        };

        var accessService = new ExpedienteAccessService(context, userService, tenantService);

        var exception = await Record.ExceptionAsync(() => accessService.EnsureCanAccessExpedienteAsync(expediente.Id, false));
        Assert.Null(exception);
    }

    [Fact]
    public async Task EnsureCanAccessTareaAsync_HeredaRestriccionDelExpedientePadre()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantA };
        using var context = CreateDbContext(tenantService);
        var juniorUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantA,
            NumeroExpediente = "EXP-2026-0003",
            Titulo = "Caso Civil",
            Materia = "Civil",
            Estado = EstadoExpediente.Abierto,
            ClienteId = Guid.NewGuid(),
            AbogadoResponsableId = otherUserId
        };
        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantA,
            ExpedienteId = expediente.Id,
            Titulo = "Revisar contestación",
            Estado = EstadoTarea.Pendiente
        };
        context.Expedientes.Add(expediente);
        context.Tareas.Add(tarea);
        await context.SaveChangesAsync();

        var userService = new MockCurrentUserService
        {
            UserId = juniorUserId,
            Email = "junior@estudio.com",
            Role = Roles.AbogadoJunior
        };

        var accessService = new ExpedienteAccessService(context, userService, tenantService);

        // Junior intenta acceder a una tarea de un expediente no asignado
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            accessService.EnsureCanAccessTareaAsync(tarea.Id, false));

        Assert.Contains("asignado como responsable", ex.Message);
    }

    [Fact]
    public async Task EnsureCanAccessDocumentoAsync_DocumentoDeOtroTenant_LanzaNotFoundException()
    {
        using var context = CreateDbContext();

        var expedienteB = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantB,
            NumeroExpediente = "EXP-2026-0004",
            Titulo = "Caso Confidencial Tenant B",
            Materia = "Mercantil",
            Estado = EstadoExpediente.Abierto,
            ClienteId = Guid.NewGuid()
        };
        var documentoB = new Documento
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantB,
            ExpedienteId = expedienteB.Id,
            Titulo = "Secreto comercial.pdf",
            RutaAlmacenamiento = "tenant-b/docs/uuid.pdf",
            ContentType = "application/pdf"
        };
        context.Expedientes.Add(expedienteB);
        context.Documentos.Add(documentoB);
        await context.SaveChangesAsync();

        // Usuario autenticado en Tenant A
        var tenantService = new MockCurrentTenantService { TenantId = _tenantA };
        var userService = new MockCurrentUserService
        {
            UserId = Guid.NewGuid(),
            Email = "abogado@estudioA.com",
            Role = Roles.AbogadoSenior
        };

        var accessService = new ExpedienteAccessService(context, userService, tenantService);

        // Intento de acceso a recurso de Tenant B
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            accessService.EnsureCanAccessDocumentoAsync(documentoB.Id, false));
        Assert.Contains("otro estudio jurídico", ex.Message);
    }
}
