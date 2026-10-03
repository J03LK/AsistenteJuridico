using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Documentos.DTOs;
using AsistenteJuridico.Application.Features.Documentos.Validators;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.3 — Demuestra que el conflicto lo detecta PostgreSQL (UPDATE ... WHERE xmin = @version) y no solo la
/// comparación en memoria: un interceptor modifica la fila desde OTRA conexión justo después de que el servicio la
/// cargó y comprobó la versión, y justo antes de su UPDATE.
/// </summary>
public class Fase73ConcurrenciaXminTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _seniorId = Guid.NewGuid();

    /// <summary>Ejecuta un SQL en otra conexión (confirmado al instante) en el momento del SaveChanges del servicio.</summary>
    private sealed class CambioExternoAntesDeGuardar(Guid documentoId, string sql) : SaveChangesInterceptor
    {
        public bool Armado { get; set; }
        public int Ejecuciones { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armado && eventData.Context!.ChangeTracker.Entries<Documento>().Any(e => e.State == EntityState.Modified))
            {
                Armado = false;
                Ejecuciones++;
                await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
                await conexion.OpenAsync(cancellationToken);
                await using var cmd = new NpgsqlCommand(sql, conexion);
                cmd.Parameters.AddWithValue("id", documentoId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Fase 7.4: AuditService real. Desde que UPDATE/DELETE auditan con LogInTransactionAsync, un doble que solo
    /// implementa LogAsync contaría el evento aunque la transacción se revierta; por eso se cuenta en PostgreSQL.
    /// </summary>
    private AuditService AuditoriaReal(ApplicationDbContext context) => new(
        context,
        new TestUserService { UserId = _seniorId, TenantId = _tenantId, Role = Roles.AbogadoSenior, Email = "senior@fase73x.com" },
        new TestTenantService { TenantId = _tenantId },
        new Microsoft.AspNetCore.Http.HttpContextAccessor());

    private async Task<int> EventosEnBaseAsync(Guid documentoId)
    {
        await using var context = Contexto();
        return await context.HistorialAuditorias.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == _tenantId && a.EntidadId == documentoId.ToString());
    }

    private ApplicationDbContext Contexto(params IInterceptor[] interceptores) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(TestConfiguration.PostgresConnectionString)
            .AddInterceptors(interceptores)
            .Options,
        new TestTenantService { TenantId = _tenantId });

    private async Task<Guid> SeedAsync()
    {
        await using var context = Contexto();
        var email = $"{_seniorId:N}@fase73x.com";
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8], NombreRazonSocial = "Cliente xmin", Activo = true
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ClienteId = cliente.Id, NumeroExpediente = "EXP-X-" + Guid.NewGuid().ToString("N")[..8],
            Titulo = "Caso xmin", Materia = "Civil", AbogadoResponsableId = _seniorId
        };
        var documento = new Documento
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ExpedienteId = expediente.Id, Titulo = "Original", TipoDocumento = "Demanda",
            RutaAlmacenamiento = "t/x.pdf", ContentType = "application/pdf", TamanioBytes = 1, EstadoIa = EstadoProcesamientoIa.Pendiente
        };
        context.Tenants.Add(new Tenant { Id = _tenantId, Nombre = "Estudio xmin", IdentificadorUrl = "fase73x-" + Guid.NewGuid().ToString("N"), ZonaHorariaId = "America/Guayaquil", Activo = true });
        context.Usuarios.Add(new Usuario
        {
            Id = _seniorId, TenantId = _tenantId, UserName = email, Email = email, NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(), NombreCompleto = "Senior", Rol = Roles.AbogadoSenior, Activo = true
        });
        context.AddRange(cliente, expediente, documento);
        await context.SaveChangesAsync();
        return documento.Id;
    }

    private DocumentoService Servicio(ApplicationDbContext context, IAuditService auditoria)
    {
        var tenantService = new TestTenantService { TenantId = _tenantId };
        var userService = new TestUserService { UserId = _seniorId, TenantId = _tenantId, Role = Roles.AbogadoSenior, Email = "senior@fase73x.com" };
        return new DocumentoService(context, new TestFileStorageService(), new ExpedienteAccessService(context, userService, tenantService),
            tenantService, userService, auditoria, new UploadDocumentoValidator(), new UpdateDocumentoValidator(), new DocumentoFilterValidator());
    }

    private async Task<Documento> LeerAsync(Guid id)
    {
        await using var context = Contexto();
        return await context.Documentos.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == id);
    }

    [Fact]
    public async Task Put_CambioExternoEntreCargaYGuardado_409PorPostgreSql_SinSobrescribir()
    {
        var id = await SeedAsync();
        var version = (await LeerAsync(id)).Version;
        var interceptor = new CambioExternoAntesDeGuardar(id, "UPDATE documentos SET \"Titulo\" = 'Externo' WHERE \"Id\" = @id") { Armado = true };
        await using var context = Contexto(interceptor);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Servicio(context, AuditoriaReal(context)).UpdateDocumentoAsync(id, new UpdateDocumentoDto("Mio", "Demanda", null, version)));

        Assert.Equal(1, interceptor.Ejecuciones);                      // la versión en memoria coincidía al comprobarla
        Assert.Equal(DocumentoErrorCodes.ConcurrencyConflict, ex.ErrorCode);
        Assert.Equal("Externo", (await LeerAsync(id)).Titulo);           // el cambio externo no se pisó
        Assert.Equal(0, await EventosEnBaseAsync(id));                   // la auditoría UPDATE se revirtió con el UPDATE
    }

    [Fact]
    public async Task Put_LaIaMarcaProcesandoEntreCargaYGuardado_409_YEstadoIaSigueProcesando()
    {
        var id = await SeedAsync();
        var version = (await LeerAsync(id)).Version;
        var interceptor = new CambioExternoAntesDeGuardar(id, "UPDATE documentos SET \"EstadoIa\" = 1 WHERE \"Id\" = @id") { Armado = true };
        await using var context = Contexto(interceptor);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Servicio(context, AuditoriaReal(context)).UpdateDocumentoAsync(id, new UpdateDocumentoDto("Mio", "Demanda", null, version)));

        Assert.Equal(DocumentoErrorCodes.ConcurrencyConflict, ex.ErrorCode);
        Assert.Equal(0, await EventosEnBaseAsync(id));
        var final = await LeerAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Procesando, final.EstadoIa);
        Assert.Equal("Original", final.Titulo);
    }

    [Fact]
    public async Task Delete_CambioExternoEntreCargaYGuardado_409_SinBorrarNiAuditar()
    {
        var id = await SeedAsync();
        var version = (await LeerAsync(id)).Version;
        var interceptor = new CambioExternoAntesDeGuardar(id, "UPDATE documentos SET \"Titulo\" = 'Externo' WHERE \"Id\" = @id") { Armado = true };
        await using var context = Contexto(interceptor);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Servicio(context, AuditoriaReal(context)).DeleteDocumentoAsync(id, version));

        Assert.Equal(1, interceptor.Ejecuciones);
        Assert.Equal(DocumentoErrorCodes.ConcurrencyConflict, ex.ErrorCode);
        Assert.False((await LeerAsync(id)).IsDeleted);
        Assert.Equal(0, await EventosEnBaseAsync(id));   // la auditoría DELETE se revirtió con el borrado
    }

    [Fact]
    public async Task Version0_EsErrorDeValidacion_EnPutYDelete_SinEscribir()
    {
        var id = await SeedAsync();
        var antes = await LeerAsync(id);
        await using var context = Contexto();
        var servicio = Servicio(context, AuditoriaReal(context));

        var put = await Assert.ThrowsAsync<ValidationException>(() => servicio.UpdateDocumentoAsync(id, new UpdateDocumentoDto("Mio", "Demanda", null, 0)));
        Assert.Null(put.ErrorCode);
        Assert.Equal(["La versión del documento no es válida."], put.ValidationErrors);

        var delete = await Assert.ThrowsAsync<ValidationException>(() => servicio.DeleteDocumentoAsync(id, 0));
        Assert.Null(delete.ErrorCode);
        Assert.Equal(["La versión del documento no es válida."], delete.ValidationErrors);

        var despues = await LeerAsync(id);
        Assert.Equal(antes.Version, despues.Version);
        Assert.False(despues.IsDeleted);
        Assert.Equal(0, await EventosEnBaseAsync(id));
    }

    [Fact]
    public async Task Put_SinCambioExterno_GuardaConLaVersionDelCliente()
    {
        var id = await SeedAsync();
        var version = (await LeerAsync(id)).Version;
        await using var context = Contexto();

        var resultado = await Servicio(context, AuditoriaReal(context)).UpdateDocumentoAsync(id, new UpdateDocumentoDto("Mio", "Demanda", null, version));

        var final = await LeerAsync(id);
        Assert.Equal("Mio", final.Titulo);
        Assert.NotEqual(version, final.Version);
        Assert.Equal(final.Version, resultado.Version);   // el DTO devuelve el xmin nuevo (RETURNING)
    }
}
