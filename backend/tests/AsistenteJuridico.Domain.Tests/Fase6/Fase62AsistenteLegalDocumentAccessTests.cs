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

namespace AsistenteJuridico.Domain.Tests.Fase6;

/// <summary>
/// Fase 6.2 — Regla de acceso documental del AsistenteLegal con estados de tarea explícitos:
/// solo una tarea Pendiente o EnProgreso, asignada al usuario, en el mismo expediente y tenant, concede acceso.
/// </summary>
public class Fase62AsistenteLegalDocumentAccessTests
{
    private readonly string _dbName = "Fase62AsistenteLegal_" + Guid.NewGuid().ToString("N");
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otroTenantId = Guid.NewGuid();
    private readonly Guid _asistenteId = Guid.NewGuid();
    private readonly Guid _abogadoId = Guid.NewGuid();

    private ApplicationDbContext CreateContext(Guid tenantId) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_dbName).Options,
        new TestTenantService { TenantId = tenantId });

    private async Task<(Guid ExpedienteId, Guid DocumentoId, Guid OtroExpedienteId)> SeedAsync()
    {
        await using var context = CreateContext(_tenantId);

        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Identificacion = "1790000000001",
            NombreRazonSocial = "Cliente Asistente Legal",
            TipoIdentificacion = TipoIdentificacion.Ruc
        };

        Expediente NuevoExpediente(string numero) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroExpediente = numero,
            Titulo = "Caso " + numero,
            Materia = "Civil",
            Estado = EstadoExpediente.Abierto,
            ClienteId = cliente.Id,
            AbogadoResponsableId = _abogadoId
        };

        var expediente = NuevoExpediente("EXP-AL-001");
        var otroExpediente = NuevoExpediente("EXP-AL-002");

        var documento = new Documento
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Escrito de prueba",
            TipoDocumento = "Escrito",
            RutaAlmacenamiento = "tenant/escrito.pdf",
            ContentType = "text/plain; charset=utf-8", // Fase 6.X (DA-11): el doble de almacenamiento devuelve texto
            EstadoIa = EstadoProcesamientoIa.Pendiente
        };

        context.AddRange(cliente, expediente, otroExpediente, documento);
        await context.SaveChangesAsync();
        return (expediente.Id, documento.Id, otroExpediente.Id);
    }

    private async Task AgregarTareaAsync(Guid tenantId, Guid expedienteId, Guid asignadoA, EstadoTarea estado)
    {
        await using var context = CreateContext(tenantId);
        context.Tareas.Add(new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = expedienteId,
            AsignadoAUsuarioId = asignadoA,
            Titulo = $"Tarea {estado}",
            Estado = estado,
            Prioridad = Prioridad.Media,
            FechaVencimiento = DateTime.UtcNow.AddDays(3)
        });
        await context.SaveChangesAsync();
    }

    private ExpedienteAccessService CreateAccessService(ApplicationDbContext context)
    {
        var userService = new TestUserService { UserId = _asistenteId, TenantId = _tenantId, Role = Roles.AsistenteLegal };
        return new ExpedienteAccessService(context, userService, new TestTenantService { TenantId = _tenantId });
    }

    [Fact]
    public void EstadosPermitidos_SonListaExplicitaPendienteYEnProgreso()
    {
        Assert.Equal(
            [EstadoTarea.Pendiente, EstadoTarea.EnProgreso],
            ExpedienteAccessService.EstadosTareaQueHabilitanAccesoDocumental);
    }

    [Theory]
    [InlineData(EstadoTarea.Pendiente, true)]
    [InlineData(EstadoTarea.EnProgreso, true)]
    [InlineData(EstadoTarea.Completada, false)]
    [InlineData(EstadoTarea.Cancelada, false)]
    public async Task TareaAsignadaEnElExpediente_SegunEstado(EstadoTarea estado, bool accesoPermitido)
    {
        var (expedienteId, documentoId, _) = await SeedAsync();
        await AgregarTareaAsync(_tenantId, expedienteId, _asistenteId, estado);

        await using var context = CreateContext(_tenantId);
        var accessService = CreateAccessService(context);

        if (accesoPermitido)
        {
            var documento = await accessService.EnsureCanAccessDocumentoAsync(documentoId);
            Assert.Equal(documentoId, documento.Id);
        }
        else
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => accessService.EnsureCanAccessDocumentoAsync(documentoId));
        }
    }

    [Fact]
    public async Task SinTarea_Rechazado()
    {
        var (_, documentoId, _) = await SeedAsync();

        await using var context = CreateContext(_tenantId);
        await Assert.ThrowsAsync<ForbiddenException>(() => CreateAccessService(context).EnsureCanAccessDocumentoAsync(documentoId));
    }

    [Fact]
    public async Task TareaEnOtroExpediente_Rechazado()
    {
        var (_, documentoId, otroExpedienteId) = await SeedAsync();
        await AgregarTareaAsync(_tenantId, otroExpedienteId, _asistenteId, EstadoTarea.Pendiente);

        await using var context = CreateContext(_tenantId);
        await Assert.ThrowsAsync<ForbiddenException>(() => CreateAccessService(context).EnsureCanAccessDocumentoAsync(documentoId));
    }

    [Fact]
    public async Task TareaDeOtroTenant_Rechazado()
    {
        var (expedienteId, documentoId, _) = await SeedAsync();

        // Misma combinación expediente/usuario, pero la fila pertenece a otro tenant
        await AgregarTareaAsync(_otroTenantId, expedienteId, _asistenteId, EstadoTarea.Pendiente);

        await using var context = CreateContext(_tenantId);
        await Assert.ThrowsAsync<ForbiddenException>(() => CreateAccessService(context).EnsureCanAccessDocumentoAsync(documentoId));
    }

    [Fact]
    public async Task TareaAsignadaAOtroUsuario_Rechazado()
    {
        var (expedienteId, documentoId, _) = await SeedAsync();
        await AgregarTareaAsync(_tenantId, expedienteId, _abogadoId, EstadoTarea.Pendiente);

        await using var context = CreateContext(_tenantId);
        await Assert.ThrowsAsync<ForbiddenException>(() => CreateAccessService(context).EnsureCanAccessDocumentoAsync(documentoId));
    }

    [Fact]
    public async Task OperacionDeIA_AplicaLaMismaRegla()
    {
        var (expedienteId, documentoId, _) = await SeedAsync();
        await AgregarTareaAsync(_tenantId, expedienteId, _asistenteId, EstadoTarea.Completada);

        await using var context = CreateContext(_tenantId);
        var userService = new TestUserService { UserId = _asistenteId, TenantId = _tenantId, Role = Roles.AsistenteLegal };
        var tenantService = new TestTenantService { TenantId = _tenantId };
        var recorder = new RecordingAIProvider();
        var aiService = new AIService(
            context, recorder, tenantService, userService, new ExpedienteAccessService(context, userService, tenantService),
            new TestFileStorageService(), NullLogger<AIService>.Instance);

        // Solo una tarea completada: rechazado y el proveedor nunca recibe el documento
        await Assert.ThrowsAsync<ForbiddenException>(() => aiService.SummarizeDocumentoAsync(new AISummarizeDocumentoDto(documentoId)));
        Assert.Empty(recorder.Requests);

        // Con una tarea en progreso: permitido
        await AgregarTareaAsync(_tenantId, expedienteId, _asistenteId, EstadoTarea.EnProgreso);
        var resumen = await aiService.SummarizeDocumentoAsync(new AISummarizeDocumentoDto(documentoId));
        Assert.False(string.IsNullOrWhiteSpace(resumen.Contenido));
        Assert.Single(recorder.Requests);
    }
}
