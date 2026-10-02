using System.Collections.Concurrent;
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

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class ProcesoJudicialPostgreSqlIntegrationTests
{
    private static string PostgresConnectionString => TestConfiguration.PostgresConnectionString;

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
        public string? Role { get; set; } = Roles.AbogadoSenior;
        public bool IsAuthenticated => true;
        public IEnumerable<string> Permissions => [Application.Common.Security.Permissions.ExpedientesUpdate, Application.Common.Security.Permissions.ProcesosLink];
        public bool HasPermission(string permission) => true;
    }

    private class MockAuditService : IAuditService
    {
        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private class MockCodeGenerator : IExpedienteCodeGenerator
    {
        public Task<string> GenerateNextCodeAsync(Guid tenantId, int? anio = null, CancellationToken cancellationToken = default)
            => Task.FromResult("EXP-2026-9999");
    }

    private ApplicationDbContext CreateRealDbContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        return new ApplicationDbContext(options, tenantService);
    }

    private ExpedienteService CreateExpedienteService(ApplicationDbContext context, Guid tenantId)
    {
        var tenantService = new MockCurrentTenantService { TenantId = tenantId };
        var userService = new MockCurrentUserService { TenantId = tenantId };
        var accessService = new ExpedienteAccessService(context, userService, tenantService);
        var auditService = new MockAuditService();
        var codeGen = new MockCodeGenerator();

        return new ExpedienteService(
            context,
            tenantService,
            userService,
            accessService,
            codeGen,
            auditService,
            new InlineValidator<CreateExpedienteDto>(),
            new InlineValidator<UpdateExpedienteDto>(),
            new CambiarEstadoExpedienteValidator(),
            new VincularProcesoValidator());
    }

    [Fact]
    public async Task ReemplazoConcurrenteProcesoPrincipal_DosPeticionesSimultaneas_Una200Otra409Conflict()
    {
        var tenantId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();
        var expedienteId = Guid.NewGuid();
        var proceso0Id = Guid.NewGuid();
        var proceso1Id = Guid.NewGuid();
        var proceso2Id = Guid.NewGuid();

        // 1. Setup en PostgreSQL real
        using (var setupContext = CreateRealDbContext(tenantId))
        {
            setupContext.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Estudio Concurrencia " + Guid.NewGuid().ToString("N")[..8],
                IdentificadorUrl = "estudio-" + Guid.NewGuid().ToString("N")[..8],
                Activo = true,
                CreatedAt = DateTime.UtcNow
            });

            setupContext.Clientes.Add(new Cliente
            {
                Id = clienteId,
                TenantId = tenantId,
                TipoIdentificacion = TipoIdentificacion.Cedula,
                Identificacion = "17" + Random.Shared.Next(10000000, 99999999),
                NombreRazonSocial = "Cliente Concurrencia S.A.",
                Activo = true,
                CreatedAt = DateTime.UtcNow
            });

            setupContext.Expedientes.Add(new Expediente
            {
                Id = expedienteId,
                TenantId = tenantId,
                NumeroExpediente = "EXP-2026-" + Random.Shared.Next(1000, 9999),
                Titulo = "Expediente Concurrencia Proceso Principal",
                Materia = "Civil",
                Estado = EstadoExpediente.Abierto,
                Prioridad = Prioridad.Media,
                ClienteId = clienteId,
                CreatedAt = DateTime.UtcNow
            });

            setupContext.ProcesosJudiciales.AddRange(
                new ProcesoJudicial
                {
                    Id = proceso0Id,
                    TenantId = tenantId,
                    NumeroProceso = "17230" + Random.Shared.Next(100000000, 999999999),
                    Judicatura = "Unidad Judicial Inicial",
                    EstadoJudicial = "En trámite",
                    CreatedAt = DateTime.UtcNow
                },
                new ProcesoJudicial
                {
                    Id = proceso1Id,
                    TenantId = tenantId,
                    NumeroProceso = "17230" + Random.Shared.Next(100000000, 999999999),
                    Judicatura = "Unidad Judicial Concurrente 1",
                    EstadoJudicial = "En trámite",
                    CreatedAt = DateTime.UtcNow
                },
                new ProcesoJudicial
                {
                    Id = proceso2Id,
                    TenantId = tenantId,
                    NumeroProceso = "17230" + Random.Shared.Next(100000000, 999999999),
                    Judicatura = "Unidad Judicial Concurrente 2",
                    EstadoJudicial = "En trámite",
                    CreatedAt = DateTime.UtcNow
                }
            );

            await setupContext.SaveChangesAsync();

            // Vincular Proceso 0 como proceso principal inicial
            var initialService = CreateExpedienteService(setupContext, tenantId);
            await initialService.VincularProcesoAsync(expedienteId, new VincularProcesoDto(proceso0Id, EsPrincipal: true, Observaciones: "Principal Inicial"));
        }

        try
        {
            // 2. Disparar dos reemplazos simultáneos del proceso principal
            var task1Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task2Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var results = new ConcurrentBag<(bool Success, Exception? Error)>();

            var task1 = Task.Run(async () =>
            {
                try
                {
                    using var context1 = CreateRealDbContext(tenantId);
                    var service1 = CreateExpedienteService(context1, tenantId);

                    var res = await service1.VincularProcesoAsync(
                        expedienteId,
                        new VincularProcesoDto(proceso1Id, EsPrincipal: true, Observaciones: "Reemplazo A"),
                        CancellationToken.None,
                        onBeforeCommit: async () =>
                        {
                            task1Started.TrySetResult(true);
                            // Esperar que la tarea 2 entre a ejecutar concurrentemente y bloquee en el lock
                            await task2Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            await Task.Delay(100); // margen para que la consulta UPDATE de task 2 bloquee en PostgreSQL
                        });
                    results.Add((true, null));
                }
                catch (Exception ex)
                {
                    results.Add((false, ex));
                }
            });

            var task2 = Task.Run(async () =>
            {
                try
                {
                    using var context2 = CreateRealDbContext(tenantId);
                    var service2 = CreateExpedienteService(context2, tenantId);

                    // Esperar que la tarea 1 haya iniciado su transacción y actualizado el expediente
                    await task1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    task2Entered.TrySetResult(true);

                    var res = await service2.VincularProcesoAsync(
                        expedienteId,
                        new VincularProcesoDto(proceso2Id, EsPrincipal: true, Observaciones: "Reemplazo B"),
                        CancellationToken.None);
                    results.Add((true, null));
                }
                catch (Exception ex)
                {
                    results.Add((false, ex));
                }
            });

            await Task.WhenAll(task1, task2);

            // 3. Verificación de resultados de la carrera:
            // Una petición debe finalizar 200 (Success == true)
            // La otra petición debe finalizar 409 (ConflictException)
            var successCount = results.Count(r => r.Success);
            var conflictCount = results.Count(r => !r.Success && (r.Error is ConflictException || (r.Error is DbUpdateException dbEx && dbEx.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")));

            Assert.Equal(2, results.Count);
            Assert.Equal(1, successCount);
            Assert.Equal(1, conflictCount);

            // 4. Verificación en PostgreSQL: Debe existir EXACTAMENTE UN proceso con EsPrincipal = true
            using (var verifyContext = CreateRealDbContext(tenantId))
            {
                var principales = await verifyContext.ExpedienteProcesosJudiciales
                    .Where(ep => ep.ExpedienteId == expedienteId && ep.EsPrincipal)
                    .ToListAsync();

                Assert.Single(principales);
                Assert.True(principales[0].EsPrincipal);
                Assert.True(principales[0].ProcesoJudicialId == proceso1Id || principales[0].ProcesoJudicialId == proceso2Id);
            }
        }
        finally
        {
            // Limpieza
            try
            {
                using var cleanupContext = CreateRealDbContext(tenantId);
                var vinculos = await cleanupContext.ExpedienteProcesosJudiciales.Where(x => x.TenantId == tenantId).ToListAsync();
                cleanupContext.ExpedienteProcesosJudiciales.RemoveRange(vinculos);
                var procesos = await cleanupContext.ProcesosJudiciales.Where(x => x.TenantId == tenantId).ToListAsync();
                cleanupContext.ProcesosJudiciales.RemoveRange(procesos);
                var expedientes = await cleanupContext.Expedientes.Where(x => x.TenantId == tenantId).ToListAsync();
                cleanupContext.Expedientes.RemoveRange(expedientes);
                var clientes = await cleanupContext.Clientes.Where(x => x.TenantId == tenantId).ToListAsync();
                cleanupContext.Clientes.RemoveRange(clientes);
                var tenant = await cleanupContext.Tenants.FindAsync(tenantId);
                if (tenant != null) cleanupContext.Tenants.Remove(tenant);
                await cleanupContext.SaveChangesAsync();
            }
            catch { }
        }
    }
}
