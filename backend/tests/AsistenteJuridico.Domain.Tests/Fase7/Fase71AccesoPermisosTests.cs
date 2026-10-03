using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Documentos.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.1 — Permisos (D2, D3), endurecimiento de EnsureCanAccessDocumentoAsync (D-1, D-3, D-4), DocumentoDto sin
/// ruta física (D-6) y ErrorCode aditivo (D8). Pruebas sin base de datos real; el esquema y la API se prueban contra
/// PostgreSQL en Fase71EsquemaPostgreSqlTests y Fase71DocumentosApiTests.
/// </summary>
public class Fase71AccesoPermisosTests
{
    private readonly string _dbName = "Fase71Acceso_" + Guid.NewGuid().ToString("N");
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _responsableId = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();

    // ── PBAC ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.AdminEstudio, true)]
    [InlineData(Roles.AbogadoSenior, true)]
    [InlineData(Roles.AbogadoJunior, true)]
    [InlineData(Roles.AsistenteLegal, false)]
    [InlineData(Roles.SuperAdmin, false)]
    public void DocumentosUpdate_SoloAdminSeniorYJunior(string rol, bool tienePermiso)
    {
        Assert.Equal("Documentos.Update", Permissions.DocumentosUpdate);
        Assert.Equal(tienePermiso, Permissions.GetPermissionsForRole(rol).Contains(Permissions.DocumentosUpdate));
    }

    [Theory]
    [InlineData(Roles.AdminEstudio, true)]
    [InlineData(Roles.AbogadoSenior, true)]
    [InlineData(Roles.AbogadoJunior, true)]
    [InlineData(Roles.AsistenteLegal, false)]
    [InlineData(Roles.SuperAdmin, false)]
    public void DocumentosUpload_RetiradoDeAsistenteLegal(string rol, bool tienePermiso)
    {
        Assert.Equal(tienePermiso, Permissions.GetPermissionsForRole(rol).Contains(Permissions.DocumentosUpload));
    }

    [Fact]
    public void MatrizDocumental_ExactamenteLaAprobada()
    {
        string[] Documentales(string rol) => Permissions.GetPermissionsForRole(rol)
            .Where(p => p.StartsWith("Documentos.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(Documentales(Roles.SuperAdmin));
        Assert.Equal(["Documentos.Delete", "Documentos.Read", "Documentos.Update", "Documentos.Upload"], Documentales(Roles.AdminEstudio));
        Assert.Equal(["Documentos.Delete", "Documentos.Read", "Documentos.Update", "Documentos.Upload"], Documentales(Roles.AbogadoSenior));
        Assert.Equal(["Documentos.Read", "Documentos.Update", "Documentos.Upload"], Documentales(Roles.AbogadoJunior));
        Assert.Equal(["Documentos.Read"], Documentales(Roles.AsistenteLegal));
    }

    // ── DTO sin ruta física ──────────────────────────────────────────────

    [Fact]
    public void DocumentoDto_NoTienePropiedadDeRutaFisica()
    {
        var propiedades = typeof(DocumentoDto).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("RutaAlmacenamiento", propiedades);
        Assert.DoesNotContain(propiedades, p => p.Contains("Ruta", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("NombreArchivoOriginal", propiedades);
        Assert.Contains("Descripcion", propiedades);
        Assert.Equal(typeof(Guid), typeof(DocumentoDto).GetProperty(nameof(DocumentoDto.ExpedienteId))!.PropertyType);
    }

    // ── ErrorCode aditivo ────────────────────────────────────────────────

    [Fact]
    public void ErrorCode_PorDefectoNulo_EnLasExcepcionesExistentes()
    {
        DomainException[] existentes =
        [
            new NotFoundException("Expediente", Guid.NewGuid()),
            new ForbiddenException(),
            new ConflictException("conflicto"),
            new BusinessRuleException("regla"),
            new ValidationException(["error"]),
            new UnauthorizedException(),
            new UserLockedException(),
            new TenantMismatchException(),
            new TenantTimeZoneInvalidException()
        ];

        Assert.All(existentes, e => Assert.Null(e.ErrorCode));
    }

    [Fact]
    public void ErrorCode_DeLaFase6_SeMantieneAunqueSeLeaComoDomainException()
    {
        Assert.Equal("AI_PROVIDER_ERROR", ((DomainException)new AIProviderException()).ErrorCode);
        Assert.Equal("AI_PROVIDER_TIMEOUT", ((DomainException)new AIProviderTimeoutException()).ErrorCode);
        Assert.Equal("TOO_MANY_REQUESTS", ((DomainException)new TooManyRequestsException()).ErrorCode);
        Assert.Equal("DOCUMENT_EXCEEDS_CONTEXT_LIMIT", ((DomainException)new DocumentContextExceededException(40000)).ErrorCode);
        Assert.Equal("AI_CONTEXT_WINDOW_EXCEEDED", ((DomainException)new AIContextWindowExceededException(10, 5)).ErrorCode);
        Assert.Equal("USER_INPUT_LIMIT_EXCEEDED", ((DomainException)new UserInputLimitExceededException(5000)).ErrorCode);
    }

    [Fact]
    public void CodigosDocumentales_SonLosDelContrato()
    {
        Assert.Equal("DOCUMENT_NOT_FOUND", DocumentoErrorCodes.NotFound);
        Assert.Equal("DOCUMENT_ACCESS_DENIED", DocumentoErrorCodes.AccessDenied);
        Assert.Equal("DOCUMENT_FILE_NOT_FOUND", DocumentoErrorCodes.FileNotFound);
        Assert.Equal("DOCUMENT_TYPE_NOT_ALLOWED", DocumentoErrorCodes.TypeNotAllowed);
        Assert.Equal("DOCUMENT_MAGIC_BYTES_INVALID", DocumentoErrorCodes.MagicBytesInvalid);
        Assert.Equal("DOCUMENT_SIZE_EXCEEDED", DocumentoErrorCodes.SizeExceeded);
        Assert.Equal("DOCUMENT_CONCURRENCY_CONFLICT", DocumentoErrorCodes.ConcurrencyConflict);
        Assert.Equal("DOCUMENT_PROCESSING", DocumentoErrorCodes.Processing);
        Assert.Equal("DOCUMENT_HASH_MISMATCH", DocumentoErrorCodes.HashMismatch);
    }

    // ── EnsureCanAccessDocumentoAsync y listado ──────────────────────────

    private ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_dbName).Options,
        new TestTenantService { TenantId = _tenantId });

    private ExpedienteAccessService CreateAccessService(ApplicationDbContext context, string rol, Guid? usuarioId = null) => new(
        context,
        new TestUserService { UserId = usuarioId ?? _usuarioId, TenantId = _tenantId, Role = rol },
        new TestTenantService { TenantId = _tenantId });

    private async Task<(Guid ExpedienteId, Guid DocumentoId)> SeedAsync(bool expedienteEliminado = false)
    {
        await using var context = CreateContext();
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Identificacion = "1790000000001",
            NombreRazonSocial = "Cliente Fase 7.1",
            TipoIdentificacion = TipoIdentificacion.Ruc
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            NumeroExpediente = "EXP-71-" + Guid.NewGuid().ToString("N")[..6],
            Titulo = "Caso Fase 7.1",
            Materia = "Civil",
            ClienteId = cliente.Id,
            AbogadoResponsableId = _responsableId,
            IsDeleted = expedienteEliminado,
            DeletedAt = expedienteEliminado ? DateTime.UtcNow : null
        };
        var documento = new Documento
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Escrito",
            TipoDocumento = "Escrito",
            RutaAlmacenamiento = "tenant/escrito.pdf",
            ContentType = "application/pdf"
        };
        context.AddRange(cliente, expediente, documento);
        await context.SaveChangesAsync();
        return (expediente.Id, documento.Id);
    }

    private async Task AgregarTareaAsync(Guid expedienteId, EstadoTarea estado)
    {
        await using var context = CreateContext();
        context.Tareas.Add(new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expedienteId,
            AsignadoAUsuarioId = _usuarioId,
            Titulo = "Tarea " + estado,
            Estado = estado,
            Prioridad = Prioridad.Media,
            FechaVencimiento = DateTime.UtcNow.AddDays(3)
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SuperAdmin_SinAccesoDocumental_NiDetalleNiListado()
    {
        var (expedienteId, documentoId) = await SeedAsync();
        await using var context = CreateContext();
        var access = CreateAccessService(context, Roles.SuperAdmin);

        await Assert.ThrowsAsync<ForbiddenException>(() => access.EnsureCanAccessDocumentoAsync(documentoId));
        await Assert.ThrowsAsync<ForbiddenException>(() => access.EnsureCanAccessDocumentoAsync(documentoId, requireWriteAccess: true));
        await Assert.ThrowsAsync<ForbiddenException>(() => access.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId));
    }

    [Theory]
    [InlineData(Roles.AdminEstudio)]
    [InlineData(Roles.AbogadoSenior)]
    [InlineData(Roles.AbogadoJunior)]
    [InlineData(Roles.AsistenteLegal)]
    public async Task ExpedienteEliminado_Documento404_ParaCualquierRol(string rol)
    {
        var (expedienteId, documentoId) = await SeedAsync(expedienteEliminado: true);
        await AgregarTareaAsync(expedienteId, EstadoTarea.Pendiente);
        await using var context = CreateContext();
        var access = CreateAccessService(context, rol, rol == Roles.AbogadoJunior ? _responsableId : null);

        await Assert.ThrowsAsync<NotFoundException>(() => access.EnsureCanAccessDocumentoAsync(documentoId));
        await Assert.ThrowsAsync<NotFoundException>(() => access.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId));
    }

    [Fact]
    public async Task Junior_SoloEnExpedienteDelQueEsResponsable()
    {
        var (expedienteId, documentoId) = await SeedAsync();
        await using var context = CreateContext();

        var ajeno = CreateAccessService(context, Roles.AbogadoJunior);
        await Assert.ThrowsAsync<ForbiddenException>(() => ajeno.EnsureCanAccessDocumentoAsync(documentoId));
        await Assert.ThrowsAsync<ForbiddenException>(() => ajeno.EnsureCanAccessDocumentoAsync(documentoId, requireWriteAccess: true));
        await Assert.ThrowsAsync<ForbiddenException>(() => ajeno.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId));

        var responsable = CreateAccessService(context, Roles.AbogadoJunior, _responsableId);
        Assert.Equal(documentoId, (await responsable.EnsureCanAccessDocumentoAsync(documentoId)).Id);
        Assert.Equal(documentoId, (await responsable.EnsureCanAccessDocumentoAsync(documentoId, requireWriteAccess: true)).Id);
        await responsable.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(EstadoTarea.Pendiente, true)]
    [InlineData(EstadoTarea.EnProgreso, true)]
    [InlineData(EstadoTarea.Completada, false)]
    [InlineData(EstadoTarea.Cancelada, false)]
    public async Task AsistenteLegal_DetalleYListado_SoloConTareaVigente(EstadoTarea? estado, bool permitido)
    {
        var (expedienteId, documentoId) = await SeedAsync();
        if (estado.HasValue)
        {
            await AgregarTareaAsync(expedienteId, estado.Value);
        }

        await using var context = CreateContext();
        var access = CreateAccessService(context, Roles.AsistenteLegal);

        if (permitido)
        {
            Assert.Equal(documentoId, (await access.EnsureCanAccessDocumentoAsync(documentoId)).Id);
            await access.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId);
        }
        else
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => access.EnsureCanAccessDocumentoAsync(documentoId));
            await Assert.ThrowsAsync<ForbiddenException>(() => access.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId));
        }

        // Nunca escritura, ni siquiera con tarea vigente
        await Assert.ThrowsAsync<ForbiddenException>(() => access.EnsureCanAccessDocumentoAsync(documentoId, requireWriteAccess: true));
    }

    [Theory]
    [InlineData(Roles.AdminEstudio)]
    [InlineData(Roles.AbogadoSenior)]
    public async Task AdminYSenior_AccesoCompleto(string rol)
    {
        var (expedienteId, documentoId) = await SeedAsync();
        await using var context = CreateContext();
        var access = CreateAccessService(context, rol);

        Assert.Equal(documentoId, (await access.EnsureCanAccessDocumentoAsync(documentoId)).Id);
        Assert.Equal(documentoId, (await access.EnsureCanAccessDocumentoAsync(documentoId, requireWriteAccess: true)).Id);
        await access.EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId);
    }
}
