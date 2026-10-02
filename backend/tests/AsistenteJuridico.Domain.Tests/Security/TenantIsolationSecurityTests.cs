using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Domain.Tests.Security;

public class TenantIsolationSecurityTests
{
    private readonly Guid _tenantAId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly Guid _tenantBId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private ApplicationDbContext CreateContext(ICurrentTenantService tenantService, string dbName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new ApplicationDbContext(options, tenantService);
    }

    private async Task SeedDataAsync(string dbName)
    {
        // Usar un contexto sin filtro para sembrar datos directamente
        var emptyTenantService = new CurrentTenantService();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        using var seedContext = new ApplicationDbContext(options, emptyTenantService);

        var tenantA = new Tenant
        {
            Id = _tenantAId,
            IdentificadorUrl = "estudio-a",
            Nombre = "Estudio Jurídico A",
            Activo = true
        };

        var tenantB = new Tenant
        {
            Id = _tenantBId,
            IdentificadorUrl = "estudio-b",
            Nombre = "Estudio Jurídico B",
            Activo = true
        };

        seedContext.Tenants.AddRange(tenantA, tenantB);

        // Clientes Tenant A
        var clienteA1 = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantAId,
            NombreRazonSocial = "Juan Pérez (A)",
            Identificacion = "1710000001",
            Email = "juan.a@test.ec"
        };
        var clienteA2 = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantAId,
            NombreRazonSocial = "María López (A)",
            Identificacion = "1710000002",
            Email = "maria.a@test.ec"
        };

        // Clientes Tenant B
        var clienteB1 = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantBId,
            NombreRazonSocial = "Carlos Sánchez (B)",
            Identificacion = "1720000001",
            Email = "carlos.b@test.ec"
        };
        var clienteB2 = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantBId,
            NombreRazonSocial = "Ana García (B)",
            Identificacion = "1720000002",
            Email = "ana.b@test.ec"
        };

        seedContext.Clientes.AddRange(clienteA1, clienteA2, clienteB1, clienteB2);

        // Expedientes
        var expA = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantAId,
            NumeroExpediente = "EXP-A-001",
            Titulo = "Caso Confidencial A",
            ClienteId = clienteA1.Id,
            Materia = "Laboral"
        };

        var expB = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantBId,
            NumeroExpediente = "EXP-B-001",
            Titulo = "Caso Confidencial B",
            ClienteId = clienteB1.Id,
            Materia = "Penal"
        };

        seedContext.Expedientes.AddRange(expA, expB);
        await seedContext.SaveChangesAsync();
    }

    [Fact]
    public async Task StrictMultiTenantFilter_ShouldReturnZeroRows_WhenCurrentTenantIdIsNull()
    {
        // Arrange: DB con datos de Tenant A y Tenant B
        var dbName = Guid.NewGuid().ToString();
        await SeedDataAsync(dbName);

        // Petición sin tenant en contexto (CurrentTenantId == null)
        var tenantService = new CurrentTenantService();
        Assert.Null(tenantService.TenantId);

        using var context = CreateContext(tenantService, dbName);

        // Act
        var clientes = await context.Clientes.ToListAsync();
        var expedientes = await context.Expedientes.ToListAsync();

        // Assert: GARANTÍA CRÍTICA DE SEGURIDAD: CERO FILTRACIONES
        Assert.Empty(clientes);
        Assert.Empty(expedientes);
    }

    [Fact]
    public async Task StrictMultiTenantFilter_ShouldOnlyReturnTenantARecords_WhenCurrentTenantIsTenantA()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        await SeedDataAsync(dbName);

        var tenantService = new CurrentTenantService();
        tenantService.SetTenantId(_tenantAId);

        using var context = CreateContext(tenantService, dbName);

        // Act
        var clientes = await context.Clientes.ToListAsync();
        var expedientes = await context.Expedientes.ToListAsync();

        // Assert: Solo registros de Tenant A, jamás Tenant B
        Assert.Equal(2, clientes.Count);
        Assert.All(clientes, c => Assert.Equal(_tenantAId, c.TenantId));

        Assert.Single(expedientes);
        Assert.Equal(_tenantAId, expedientes[0].TenantId);
        Assert.Equal("EXP-A-001", expedientes[0].NumeroExpediente);
    }

    [Fact]
    public async Task StrictMultiTenantFilter_ShouldOnlyReturnTenantBRecords_WhenCurrentTenantIsTenantB()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        await SeedDataAsync(dbName);

        var tenantService = new CurrentTenantService();
        tenantService.SetTenantId(_tenantBId);

        using var context = CreateContext(tenantService, dbName);

        // Act
        var clientes = await context.Clientes.ToListAsync();
        var expedientes = await context.Expedientes.ToListAsync();

        // Assert: Solo registros de Tenant B, jamás Tenant A
        Assert.Equal(2, clientes.Count);
        Assert.All(clientes, c => Assert.Equal(_tenantBId, c.TenantId));

        Assert.Single(expedientes);
        Assert.Equal(_tenantBId, expedientes[0].TenantId);
        Assert.Equal("EXP-B-001", expedientes[0].NumeroExpediente);
    }

    [Fact]
    public async Task StrictMultiTenantFilter_ShouldPreventCrossTenantAccess_WhenDirectIdIsRequested()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        await SeedDataAsync(dbName);

        // Obtener ID del expediente confidencial de Tenant B
        var emptyTenantService = new CurrentTenantService();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        using var readContext = new ApplicationDbContext(options, emptyTenantService);
        var expB = await readContext.Expedientes.IgnoreQueryFilters().FirstAsync(e => e.TenantId == _tenantBId);

        // Act: Usuario de Tenant A intenta consultar por ID exacto el expediente de Tenant B
        var tenantAService = new CurrentTenantService();
        tenantAService.SetTenantId(_tenantAId);
        using var contextTenantA = CreateContext(tenantAService, dbName);

        var resultado = await contextTenantA.Expedientes.FirstOrDefaultAsync(e => e.Id == expB.Id);

        // Assert: El resultado DEBE ser null debido al filtro global estricto
        Assert.Null(resultado);
    }
}
