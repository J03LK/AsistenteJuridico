using System.Collections.Concurrent;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class TenantSecuenciaPostgreSqlIntegrationTests
{
    private const string PostgresConnectionString =
        "Host=localhost;Port=5433;Database=asistente_juridico;Username=aj_user;Password=REMOVED_SECRET";

    private class MockCurrentTenantService : AsistenteJuridico.Application.Common.Interfaces.ICurrentTenantService
    {
        public Guid? TenantId { get; set; }
        public string? TenantSlug => "test";
        public bool IsMultiTenantContext => TenantId.HasValue;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
    }

    private ApplicationDbContext CreateRealDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var tenantService = tenantId.HasValue ? new MockCurrentTenantService { TenantId = tenantId } : null;
        return new ApplicationDbContext(options, tenantService);
    }

    [Fact]
    public async Task GenerateNextCodeAsync_10PeticionesConcurrentes_GeneraCodigosUnicosSinDuplicados()
    {
        // 1. Crear un tenant nuevo para aislar la prueba
        var tenantId = Guid.NewGuid();
        using (var setupContext = CreateRealDbContext())
        {
            setupContext.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Estudio Juridico Concurrencia " + Guid.NewGuid().ToString("N")[..8],
                IdentificadorUrl = "tenant-" + Guid.NewGuid().ToString("N")[..8],
                Activo = true,
                CreatedAt = DateTime.UtcNow
            });
            await setupContext.SaveChangesAsync();
        }

        try
        {
            var generatedCodes = new ConcurrentBag<string>();
            var tasks = new List<Task>();

            // 2. Disparar 10 solicitudes concurrentes en hilos separados
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var context = CreateRealDbContext();
                    var generator = new ExpedienteCodeGenerator(context);
                    var code = await generator.GenerateNextCodeAsync(tenantId);
                    generatedCodes.Add(code);
                }));
            }

            await Task.WhenAll(tasks);

            // 3. Verificaciones de invariantes requeridas por diseño v4.0:
            // a) Exactamente 10 códigos generados
            Assert.Equal(10, generatedCodes.Count);

            // b) Cero duplicados (todos únicos)
            var uniqueCodes = generatedCodes.Distinct().ToList();
            Assert.Equal(10, uniqueCodes.Count);

            // c) Formato estricto EXP-{YYYY}-{NNNN}
            var currentYear = DateTime.UtcNow.Year;
            foreach (var code in generatedCodes)
            {
                Assert.StartsWith($"EXP-{currentYear}-", code);
                var numPart = code.Substring(9);
                Assert.True(int.TryParse(numPart, out var num));
                Assert.InRange(num, 1, 10);
            }
        }
        finally
        {
            // Limpieza
            try
            {
                using var cleanupContext = CreateRealDbContext();
                var secuencias = await cleanupContext.TenantSecuencias.Where(s => s.TenantId == tenantId).ToListAsync();
                cleanupContext.TenantSecuencias.RemoveRange(secuencias);
                var tenant = await cleanupContext.Tenants.FindAsync(tenantId);
                if (tenant != null) cleanupContext.Tenants.Remove(tenant);
                await cleanupContext.SaveChangesAsync();
            }
            catch { }
        }
    }

    [Fact]
    public async Task XminConcurrency_ActualizacionSimultanea_LanzaDbUpdateConcurrencyException()
    {
        var tenantId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();

        using (var setupContext = CreateRealDbContext(tenantId))
        {
            setupContext.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Estudio Concurrencia xmin " + Guid.NewGuid().ToString("N")[..8],
                IdentificadorUrl = "xmin-" + Guid.NewGuid().ToString("N")[..8],
                Activo = true,
                CreatedAt = DateTime.UtcNow
            });

            var cliente = new Cliente
            {
                Id = clienteId,
                TenantId = tenantId,
                TipoIdentificacion = AsistenteJuridico.Domain.Enums.TipoIdentificacion.Cedula,
                Identificacion = "1710034065",
                NombreRazonSocial = "Cliente Concurrencia xmin",
                Email = "xmin@test.com",
                Activo = true,
                CreatedAt = DateTime.UtcNow
            };
            setupContext.Clientes.Add(cliente);
            await setupContext.SaveChangesAsync();
        }

        try
        {
            // Usuario A lee cliente
            using var contextA = CreateRealDbContext(tenantId);
            var clienteA = await contextA.Clientes.FindAsync(clienteId);
            Assert.NotNull(clienteA);
            var originalVersion = clienteA.Version;

            // Usuario B lee cliente simultáneamente
            using var contextB = CreateRealDbContext(tenantId);
            var clienteB = await contextB.Clientes.FindAsync(clienteId);
            Assert.NotNull(clienteB);

            // Usuario A modifica y guarda
            clienteA.NombreRazonSocial = "Modificado por Usuario A";
            await contextA.SaveChangesAsync();

            // Usuario B intenta modificar con la versión original obsoleta
            clienteB.NombreRazonSocial = "Modificado por Usuario B";

            // Debe lanzar DbUpdateConcurrencyException
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                contextB.SaveChangesAsync());
        }
        finally
        {
            // Limpieza
            try
            {
                using var cleanupContext = CreateRealDbContext(tenantId);
                var cliente = await cleanupContext.Clientes.FindAsync(clienteId);
                if (cliente != null) cleanupContext.Clientes.Remove(cliente);
                var tenant = await cleanupContext.Tenants.FindAsync(tenantId);
                if (tenant != null) cleanupContext.Tenants.Remove(tenant);
                await cleanupContext.SaveChangesAsync();
            }
            catch { }
        }
    }
}
