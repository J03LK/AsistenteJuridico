using System.Text.Json;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using AsistenteJuridico.Application.Features.Audiencias.Validators;
using AsistenteJuridico.Application.Features.Clientes.DTOs;
using AsistenteJuridico.Application.Features.Clientes.Validators;
using AsistenteJuridico.Application.Features.Documentos.DTOs;
using AsistenteJuridico.Application.Features.Documentos.Validators;
using AsistenteJuridico.Application.Features.Expedientes.DTOs;
using AsistenteJuridico.Application.Features.Expedientes.Validators;
using AsistenteJuridico.Application.Features.Tareas.DTOs;
using AsistenteJuridico.Application.Features.Tareas.Validators;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class AuditEventsTests
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
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public Guid? TenantId { get; set; }
        public string? Email { get; set; } = "auditor@estudio.com";
        public string? Role { get; set; } = Roles.AbogadoSenior;
        public bool IsAuthenticated => true;
        public IEnumerable<string> Permissions =>
        [
            Application.Common.Security.Permissions.ClientesCreate,
            Application.Common.Security.Permissions.ClientesUpdate,
            Application.Common.Security.Permissions.ClientesDelete,
            Application.Common.Security.Permissions.ExpedientesCreate,
            Application.Common.Security.Permissions.ExpedientesUpdate,
            Application.Common.Security.Permissions.ExpedientesCloseForce,
            Application.Common.Security.Permissions.ExpedientesDelete,
            Application.Common.Security.Permissions.ExpedientesAssign,
            Application.Common.Security.Permissions.ProcesosLink,
            Application.Common.Security.Permissions.TareasManage,
            Application.Common.Security.Permissions.AudienciasManage,
            Application.Common.Security.Permissions.DocumentosUpload,
            Application.Common.Security.Permissions.DocumentosDelete
        ];
        public bool HasPermission(string permission) => true;
    }

    private class TrackingAuditService : IAuditService
    {
        public List<(string Entidad, string EntidadId, string Accion, string? ValoresAnteriores, string? ValoresNuevos)> Records { get; } = [];

        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
        {
            var antJson = valoresAnteriores != null ? JsonSerializer.Serialize(valoresAnteriores) : null;
            var nueJson = valoresNuevos != null ? JsonSerializer.Serialize(valoresNuevos) : null;
            Records.Add((entidad, entidadId, accion, antJson, nueJson));
            return Task.CompletedTask;
        }
    }

    private class MockCodeGenerator : IExpedienteCodeGenerator
    {
        public Task<string> GenerateNextCodeAsync(Guid tenantId, int? anio = null, CancellationToken cancellationToken = default)
            => Task.FromResult("EXP-2026-AUDIT");
    }

    private ApplicationDbContext CreateDbContext(ICurrentTenantService tenantService)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ApplicationDbContext(options, tenantService);
    }

    [Fact]
    public async Task AuditService_SanitizesSensitiveData_NeverStoresPasswordsOrTokens()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var userService = new MockCurrentUserService { TenantId = _tenantId };
        var httpAccessor = new HttpContextAccessor();
        var context = CreateDbContext(tenantService);

        var auditService = new AuditService(context, userService, tenantService, httpAccessor);

        var sensitivePayload = new
        {
            password = "SecretPassword123!",
            passwordHash = "AQAAAAIAAYagAAAAEGmK...",
            jwtToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
            refreshToken = "dGhpcy1pcy1hLXJlZnJlc2gtdG9rZW4=",
            securityStamp = "SEC-STAMP-999",
            secret = "SuperSecretVal",
            normalField = "Normal Legal Value"
        };

        await auditService.LogAsync("Usuario", Guid.NewGuid().ToString(), "AUTH_TEST", null, sensitivePayload);

        var savedEntry = await context.HistorialAuditorias.FirstOrDefaultAsync();
        Assert.NotNull(savedEntry);
        Assert.NotNull(savedEntry.ValoresNuevosJson);

        var json = savedEntry.ValoresNuevosJson;

        // Verificar que ningún secreto ni token quedó en texto plano
        Assert.DoesNotContain("SecretPassword123!", json);
        Assert.DoesNotContain("AQAAAAIAAYagAAAAEGmK...", json);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...", json);
        Assert.DoesNotContain("SEC-STAMP-999", json);
        Assert.DoesNotContain("SuperSecretVal", json);

        // Verificar redacción
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("Normal Legal Value", json);
    }

    [Fact]
    public async Task Clientes_AuditoriaCompleta_CreateUpdateDelete()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var userService = new MockCurrentUserService { TenantId = _tenantId };
        var trackingAudit = new TrackingAuditService();
        var context = CreateDbContext(tenantService);

        var service = new ClienteService(
            context,
            tenantService,
            userService,
            trackingAudit,
            new CreateClienteValidator(),
            new UpdateClienteValidator());

        // 1. Create
        var createDto = new CreateClienteDto(
            TipoIdentificacion.Cedula,
            "1710034065",
            "Empresa Demo S.A.",
            "demo@empresa.com",
            "0991234567",
            "Av. Amazonas",
            "Cliente corporativo");
        var created = await service.CreateClienteAsync(createDto);

        // 2. Update
        var updateDto = new UpdateClienteDto(
            TipoIdentificacion.Cedula,
            "1710034065",
            "Empresa Demo S.A. Modificada",
            "demo2@empresa.com",
            "0997654321",
            "Av. 10 de Agosto",
            "Notas actualizadas",
            true,
            created.Version);
        var updated = await service.UpdateClienteAsync(created.Id, updateDto);

        // 3. Delete
        await service.DeleteClienteAsync(created.Id);

        // Verificaciones
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Cliente" && r.Accion == "CREATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Cliente" && r.Accion == "UPDATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Cliente" && r.Accion == "DELETE");
    }

    [Fact]
    public async Task Expedientes_AuditoriaCompleta_CreateUpdateAssignLawyerStateChangeForceClose()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var userService = new MockCurrentUserService { TenantId = _tenantId };
        var trackingAudit = new TrackingAuditService();
        var context = CreateDbContext(tenantService);

        var clienteId = Guid.NewGuid();
        var abogado1Id = Guid.NewGuid();
        var abogado2Id = Guid.NewGuid();

        context.Clientes.Add(new Cliente
        {
            Id = clienteId,
            TenantId = _tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "1710034065",
            NombreRazonSocial = "Cliente Test",
            Activo = true
        });
        context.Usuarios.AddRange(
            new Usuario { Id = abogado1Id, TenantId = _tenantId, Email = "abogado1@estudio.com", Activo = true, NombreCompleto = "Abogado 1", UserName = "abogado1@estudio.com" },
            new Usuario { Id = abogado2Id, TenantId = _tenantId, Email = "abogado2@estudio.com", Activo = true, NombreCompleto = "Abogado 2", UserName = "abogado2@estudio.com" }
        );
        await context.SaveChangesAsync();

        var accessService = new ExpedienteAccessService(context, userService, tenantService);
        var service = new ExpedienteService(
            context,
            tenantService,
            userService,
            accessService,
            new MockCodeGenerator(),
            trackingAudit,
            new InlineValidator<CreateExpedienteDto>(),
            new InlineValidator<UpdateExpedienteDto>(),
            new CambiarEstadoExpedienteValidator(),
            new VincularProcesoValidator());

        // 1. Create with Lawyer
        var createDto = new CreateExpedienteDto(
            clienteId,
            "Expediente Auditoria",
            "Descripcion",
            "Civil",
            Prioridad.Alta,
            abogado1Id,
            DateTime.UtcNow.AddMonths(3));
        var created = await service.CreateExpedienteAsync(createDto);

        // 2. Update with Reassigned Lawyer
        var updateDto = new UpdateExpedienteDto(
            "Expediente Auditoria Mod",
            "Desc Mod",
            "Civil",
            Prioridad.Urgente,
            abogado2Id,
            DateTime.UtcNow.AddMonths(4),
            created.Version);
        var updated = await service.UpdateExpedienteAsync(created.Id, updateDto);

        // 3. State change: Abierto -> EnTramite
        var stateDto = new CambiarEstadoExpedienteDto(
            EstadoExpediente.EnTramite,
            ConfirmarCierreConTareasPendientes: false,
            MotivoCierreForzado: null,
            updated.Version);
        var stateChanged = await service.CambiarEstadoAsync(created.Id, stateDto);

        // 4. Add task to test Force Close
        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = created.Id,
            Titulo = "Tarea Bloqueante",
            Estado = EstadoTarea.Pendiente
        };
        context.Tareas.Add(tarea);
        await context.SaveChangesAsync();

        // 5. Force Close: EnTramite -> Cerrado con confirmación y motivo
        var forceCloseDto = new CambiarEstadoExpedienteDto(
            EstadoExpediente.Cerrado,
            ConfirmarCierreConTareasPendientes: true,
            MotivoCierreForzado: "Acuerdo extrajudicial transaccional",
            stateChanged.Version);
        await service.CambiarEstadoAsync(created.Id, forceCloseDto);

        // Verificaciones
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Expediente" && r.Accion == "CREATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Expediente" && r.Accion == "ASSIGN_LAWYER");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Expediente" && r.Accion == "UPDATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Expediente" && r.Accion == "STATE_CHANGE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Expediente" && r.Accion == "FORCE_CLOSE");
    }

    [Fact]
    public async Task Tareas_AuditoriaCompleta_CreateUpdateCompleteDelete()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var userService = new MockCurrentUserService { TenantId = _tenantId };
        var trackingAudit = new TrackingAuditService();
        var context = CreateDbContext(tenantService);

        var expedienteId = Guid.NewGuid();
        context.Expedientes.Add(new Expediente
        {
            Id = expedienteId,
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Expediente Test",
            Materia = "Civil",
            Estado = EstadoExpediente.EnTramite,
            ClienteId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        var accessService = new ExpedienteAccessService(context, userService, tenantService);
        var service = new TareaService(
            context,
            tenantService,
            userService,
            accessService,
            trackingAudit,
            new CreateTareaValidator(),
            new UpdateTareaValidator(),
            new CambiarEstadoTareaValidator());

        // 1. Create
        var createDto = new CreateTareaDto(
            expedienteId,
            "Redactar demanda",
            "Demanda de cobro de pagaré",
            DateTime.UtcNow.AddDays(5),
            Prioridad.Alta,
            null);
        var created = await service.CreateTareaAsync(createDto);

        // 2. Update
        var updateDto = new UpdateTareaDto(
            "Redactar demanda ejecutiva",
            "Demanda con medida cautelar",
            DateTime.UtcNow.AddDays(7),
            Prioridad.Urgente,
            null,
            created.Version);
        var updated = await service.UpdateTareaAsync(created.Id, updateDto);

        // 3. Complete (Cambiar estado a Completada)
        var completeDto = new CambiarEstadoTareaDto(EstadoTarea.Completada, updated.Version);
        await service.CambiarEstadoAsync(created.Id, completeDto);

        // 4. Delete
        await service.DeleteTareaAsync(created.Id);

        // Verificaciones
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Tarea" && r.Accion == "CREATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Tarea" && r.Accion == "UPDATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Tarea" && r.Accion == "STATE_CHANGE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Tarea" && r.Accion == "COMPLETE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Tarea" && r.Accion == "DELETE");
    }

    [Fact]
    public async Task Audiencias_AuditoriaCompleta_CreateUpdateCompleteDelete()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var userService = new MockCurrentUserService { TenantId = _tenantId };
        var trackingAudit = new TrackingAuditService();
        var context = CreateDbContext(tenantService);

        var expedienteId = Guid.NewGuid();
        context.Expedientes.Add(new Expediente
        {
            Id = expedienteId,
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Expediente Audiencia",
            Materia = "Civil",
            Estado = EstadoExpediente.EnTramite,
            ClienteId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        var accessService = new ExpedienteAccessService(context, userService, tenantService);
        var service = new AudienciaService(
            context,
            tenantService,
            userService,
            accessService,
            trackingAudit,
            new CreateAudienciaValidator(),
            new UpdateAudienciaValidator(),
            new CambiarEstadoAudienciaValidator());

        // 1. Create
        var createDto = new CreateAudienciaDto(
            expedienteId,
            null,
            DateTime.UtcNow.AddDays(10),
            "Sala 204",
            TipoAudiencia.Preliminar,
            "Audiencia preliminar");
        var created = await service.CreateAudienciaAsync(createDto);

        // 2. Update
        var updateDto = new UpdateAudienciaDto(
            null,
            DateTime.UtcNow.AddDays(12),
            "Sala 205",
            TipoAudiencia.Juicio,
            "Audiencia de juicio diferida",
            created.Version);
        var updated = await service.UpdateAudienciaAsync(created.Id, updateDto);

        // 3. Complete (Cambiar estado a Realizada)
        var completeDto = new CambiarEstadoAudienciaDto(EstadoAudiencia.Realizada, updated.Version);
        await service.CambiarEstadoAsync(created.Id, completeDto);

        // 4. Delete
        await service.DeleteAudienciaAsync(created.Id);

        // Verificaciones
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Audiencia" && r.Accion == "CREATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Audiencia" && r.Accion == "UPDATE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Audiencia" && r.Accion == "STATE_CHANGE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Audiencia" && r.Accion == "COMPLETE");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Audiencia" && r.Accion == "DELETE");
    }

    [Fact]
    public async Task Documentos_AuditoriaCompleta_UploadDelete()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var userService = new MockCurrentUserService { TenantId = _tenantId };
        var trackingAudit = new TrackingAuditService();
        var context = CreateDbContext(tenantService);

        var expedienteId = Guid.NewGuid();
        context.Expedientes.Add(new Expediente
        {
            Id = expedienteId,
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Expediente Documentos",
            Materia = "Civil",
            Estado = EstadoExpediente.EnTramite,
            ClienteId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        var accessService = new ExpedienteAccessService(context, userService, tenantService);
        var storageMock = new MockFileStorageService();

        var service = new DocumentoService(
            context,
            storageMock,
            accessService,
            tenantService,
            userService,
            trackingAudit,
            new UploadDocumentoValidator());

        // 1. Upload
        var pdfBytes = "%PDF-1.4 header and content for test"u8.ToArray();
        using var stream = new MemoryStream(pdfBytes);
        var uploadDto = new UploadDocumentoDto(
            expedienteId,
            "Demanda Firmada",
            "Escrito");

        var doc = await service.UploadDocumentoAsync(uploadDto, stream, "Demanda.pdf", "application/pdf");

        // 2. Delete
        await service.DeleteDocumentoAsync(doc.Id);

        // Verificaciones
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Documento" && r.Accion == "UPLOAD");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "Documento" && r.Accion == "DELETE");
    }

    [Fact]
    public async Task ProcesosJudiciales_AuditoriaCompleta_LinkUnlink()
    {
        var tenantService = new MockCurrentTenantService { TenantId = _tenantId };
        var userService = new MockCurrentUserService { TenantId = _tenantId };
        var trackingAudit = new TrackingAuditService();
        var context = CreateDbContext(tenantService);

        var clienteId = Guid.NewGuid();
        var expedienteId = Guid.NewGuid();
        var procesoId = Guid.NewGuid();

        context.Clientes.Add(new Cliente
        {
            Id = clienteId,
            TenantId = _tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "1710034065",
            NombreRazonSocial = "Cliente Test Proceso",
            Activo = true
        });

        context.Expedientes.Add(new Expediente
        {
            Id = expedienteId,
            TenantId = _tenantId,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Expediente Vinculacion",
            Materia = "Civil",
            Estado = EstadoExpediente.EnTramite,
            ClienteId = clienteId
        });

        context.ProcesosJudiciales.Add(new ProcesoJudicial
        {
            Id = procesoId,
            TenantId = _tenantId,
            NumeroProceso = "17230202300001",
            Judicatura = "Unidad Judicial Civil",
            EstadoJudicial = "En trámite"
        });

        await context.SaveChangesAsync();

        var accessService = new ExpedienteAccessService(context, userService, tenantService);
        var service = new ExpedienteService(
            context,
            tenantService,
            userService,
            accessService,
            new MockCodeGenerator(),
            trackingAudit,
            new InlineValidator<CreateExpedienteDto>(),
            new InlineValidator<UpdateExpedienteDto>(),
            new CambiarEstadoExpedienteValidator(),
            new VincularProcesoValidator());

        // 1. Link (Vincular proceso)
        var vincularDto = new VincularProcesoDto(procesoId, EsPrincipal: true, Observaciones: "Causa Principal");
        var vinculo = await service.VincularProcesoAsync(expedienteId, vincularDto);

        // 2. Unlink (Desvincular proceso)
        await service.DesvincularProcesoAsync(expedienteId, procesoId);

        // Verificaciones
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "ExpedienteProcesoJudicial" && r.Accion == "LINK");
        Assert.Contains(trackingAudit.Records, r => r.Entidad == "ExpedienteProcesoJudicial" && r.Accion == "UNLINK");
    }

    private class MockFileStorageService : IFileStorageService
    {
        public Task<(string PhysicalFileName, string RelativeFilePath, string ContentType, long FileSizeBytes, string Sha256Hash)> SaveFileAsync(
            Guid tenantId,
            Stream fileStream,
            string originalFileName,
            string declaredContentType,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult((
                "doc-uuid.pdf",
                $"storage/{tenantId}/doc-uuid.pdf",
                "application/pdf",
                1024L,
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            ));
        }

        public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream("%PDF-1.4 dummy"u8.ToArray()));

        public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
