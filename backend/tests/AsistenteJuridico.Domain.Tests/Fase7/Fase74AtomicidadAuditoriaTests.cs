using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Documentos.DTOs;
using AsistenteJuridico.Application.Features.Documentos.Validators;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase5;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.4 — Atomicidad de la auditoría documental con AuditService real contra PostgreSQL.
/// El fallo del INSERT de auditoría es REAL: un interceptor de prueba alarga "Accion" por encima de su varchar(50)
/// y PostgreSQL rechaza esa fila (22001) dentro de la misma transacción. Nada de esto existe en producción.
/// </summary>
public class Fase74AtomicidadAuditoriaTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "aj_fase74_atom_" + Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _seniorId = Guid.NewGuid();
    private readonly Guid _expedienteId = Guid.NewGuid();

    public Fase74AtomicidadAuditoriaTests()
    {
        Directory.CreateDirectory(_storageDir);
        using var context = Contexto();
        var email = $"{_seniorId:N}@fase74atom.com";
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8], NombreRazonSocial = "Cliente atomicidad", Activo = true
        };
        context.Tenants.Add(new Tenant { Id = _tenantId, Nombre = "Estudio atomicidad", IdentificadorUrl = "fase74atom-" + Guid.NewGuid().ToString("N"), ZonaHorariaId = "America/Guayaquil", Activo = true });
        context.Usuarios.Add(new Usuario
        {
            Id = _seniorId, TenantId = _tenantId, UserName = email, Email = email, NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(), NombreCompleto = "Senior", Rol = Roles.AbogadoSenior, Activo = true
        });
        context.Clientes.Add(cliente);
        context.Expedientes.Add(new Expediente
        {
            Id = _expedienteId, TenantId = _tenantId, ClienteId = cliente.Id, NumeroExpediente = "EXP-AT-" + Guid.NewGuid().ToString("N")[..8],
            Titulo = "Caso atomicidad", Materia = "Civil", AbogadoResponsableId = _seniorId
        });
        context.SaveChanges();
    }

    public void Dispose()
    {
        try { Directory.Delete(_storageDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Provoca el rechazo real del INSERT de auditoría de una acción concreta: deja "Accion" con 70 caracteres
    /// (columna varchar(50)), y PostgreSQL responde 22001 al ejecutar esa fila del lote.
    /// </summary>
    private sealed class FalloInsertAuditoria(string accion) : SaveChangesInterceptor
    {
        public bool Armado { get; set; } = true;
        public int Provocados { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armado)
            {
                foreach (var entrada in eventData.Context!.ChangeTracker.Entries<HistorialAuditoria>()
                             .Where(e => e.State == EntityState.Added && e.Entity.Accion == accion))
                {
                    entrada.Entity.Accion = accion + new string('X', 70);
                    Provocados++;
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private ApplicationDbContext Contexto(params IInterceptor[] interceptores) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(TestConfiguration.PostgresConnectionString)
            .AddInterceptors(interceptores)
            .Options,
        new TestTenantService { TenantId = _tenantId });

    private FileStorageService Storage() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:BasePath"] = _storageDir }).Build(),
        NullLogger<FileStorageService>.Instance);

    private (DocumentoService Servicio, ExecutionStrategyTestSupport.CapturingLoggerProvider Logs) Servicio(
        ApplicationDbContext context, IFileStorageService? storage = null)
    {
        var logs = new ExecutionStrategyTestSupport.CapturingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var tenantService = new TestTenantService { TenantId = _tenantId };
        var userService = new TestUserService { UserId = _seniorId, TenantId = _tenantId, Role = Roles.AbogadoSenior, Email = "senior@fase74atom.com" };
        var auditoria = new AuditService(context, userService, tenantService, new HttpContextAccessor(), loggerFactory.CreateLogger<AuditService>());
        var servicio = new DocumentoService(context, storage ?? Storage(), new ExpedienteAccessService(context, userService, tenantService),
            tenantService, userService, auditoria, new UploadDocumentoValidator(), new UpdateDocumentoValidator(), new DocumentoFilterValidator(),
            loggerFactory.CreateLogger<DocumentoService>());
        return (servicio, logs);
    }

    private async Task<Guid> SubirSinFallosAsync(byte[] contenido)
    {
        await using var context = Contexto();
        var (servicio, _) = Servicio(context);
        var dto = await servicio.UploadDocumentoAsync(new UploadDocumentoDto(_expedienteId, "Escrito", "Escrito", "Desc"),
            new MemoryStream(contenido), "escrito.pdf", "application/pdf", contenido.Length);
        return dto.Id;
    }

    private async Task<Documento?> LeerAsync(Guid id)
    {
        await using var context = Contexto();
        return await context.Documentos.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
    }

    private async Task<List<string>> AccionesAsync(Guid? documentoId = null)
    {
        await using var context = Contexto();
        return await context.HistorialAuditorias.IgnoreQueryFilters()
            .Where(a => a.TenantId == _tenantId && a.Entidad == "Documento" && (documentoId == null || a.EntidadId == documentoId.ToString()))
            .Select(a => a.Accion).ToListAsync();
    }

    private static void AssertRechazoRealDelServidor(DbUpdateException ex)
    {
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, pg.SqlState);   // 22001
    }

    [Fact]
    public async Task Upload_FalloRealDelInsertDeAuditoria_RollbackCompleto()
    {
        var interceptor = new FalloInsertAuditoria("UPLOAD");
        await using var context = Contexto(interceptor);
        var (servicio, _) = Servicio(context);
        var contenido = Fase72TestData.Pdf("upload-rollback");

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => servicio.UploadDocumentoAsync(
            new UploadDocumentoDto(_expedienteId, "Escrito", "Escrito"), new MemoryStream(contenido), "e.pdf", "application/pdf", contenido.Length));

        Assert.Equal(1, interceptor.Provocados);
        AssertRechazoRealDelServidor(ex);
        await using var lectura = Contexto();
        Assert.Equal(0, await lectura.Documentos.IgnoreQueryFilters().CountAsync(d => d.ExpedienteId == _expedienteId));   // sin documento
        Assert.Empty(await AccionesAsync());                                                                                   // sin auditoría
        Assert.Empty(Directory.EnumerateFiles(_storageDir, "*", SearchOption.AllDirectories));                                 // ni archivo final ni temporal
    }

    [Fact]
    public async Task Update_FalloRealDelInsertDeAuditoria_RollbackCompleto()
    {
        var id = await SubirSinFallosAsync(Fase72TestData.Pdf("update-rollback"));
        var antes = (await LeerAsync(id))!;
        var interceptor = new FalloInsertAuditoria("UPDATE");
        await using var context = Contexto(interceptor);
        var (servicio, _) = Servicio(context);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => servicio.UpdateDocumentoAsync(id, new UpdateDocumentoDto("Cambiado", "Escrito", "Desc", antes.Version)));

        Assert.Equal(1, interceptor.Provocados);
        AssertRechazoRealDelServidor(ex);
        var despues = (await LeerAsync(id))!;
        Assert.Equal("Escrito", despues.Titulo);
        Assert.Equal(antes.Version, despues.Version);
        Assert.Equal(["UPLOAD"], await AccionesAsync(id));
    }

    [Fact]
    public async Task Delete_FalloRealDelInsertDeAuditoria_RollbackCompleto()
    {
        var id = await SubirSinFallosAsync(Fase72TestData.Pdf("delete-rollback"));
        var antes = (await LeerAsync(id))!;
        var interceptor = new FalloInsertAuditoria("DELETE");
        await using var context = Contexto(interceptor);
        var (servicio, _) = Servicio(context);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => servicio.DeleteDocumentoAsync(id, antes.Version));

        Assert.Equal(1, interceptor.Provocados);
        AssertRechazoRealDelServidor(ex);
        var despues = (await LeerAsync(id))!;
        Assert.False(despues.IsDeleted);
        Assert.Equal(antes.Version, despues.Version);
        Assert.Equal(["UPLOAD"], await AccionesAsync(id));
        Assert.True(File.Exists(Path.Combine(_storageDir, despues.RutaAlmacenamiento)));
    }

    [Fact]
    public async Task Download_FalloRealDeLaAuditoria_NoBloqueaLaDescarga_YQuedaElErrorSeguro()
    {
        var contenido = Fase72TestData.Pdf("download-auditoria-fallida");
        var id = await SubirSinFallosAsync(contenido);
        var interceptor = new FalloInsertAuditoria("DOWNLOAD");
        await using var context = Contexto(interceptor);   // LogAsync usa un contexto propio con las mismas opciones
        var (servicio, logs) = Servicio(context);

        var resultado = await servicio.DownloadDocumentoAsync(id);
        await using (resultado.FileStream)
        {
            using var memoria = new MemoryStream();
            await resultado.FileStream.CopyToAsync(memoria);
            Assert.Equal(contenido, memoria.ToArray());
        }

        Assert.Equal(1, interceptor.Provocados);
        Assert.Equal(["UPLOAD"], await AccionesAsync(id));
        var error = Assert.Single(logs.Entries, e => e.Message.Contains("[AUDITORIA_NO_REGISTRADA]"));
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains("DOWNLOAD", error.Message);
        Assert.Contains("22001", error.Message);
        var ruta = (await LeerAsync(id))!.RutaAlmacenamiento;
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(ruta, StringComparison.OrdinalIgnoreCase));
    }

    // ══ Invariantes: ningún valor arbitrario llega a HashSha256 / sha256Contenido ══

    private sealed class StorageConResultado(string hash, long tamanio) : IFileStorageService
    {
        public List<string> Borrados { get; } = [];

        public Task<StoredDocumentoFile> SaveDocumentoAsync(Guid tenantId, Guid expedienteId, Stream fileStream, string originalFileName,
            string? declaredContentType, long? declaredLength, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredDocumentoFile($"{tenantId:N}/{expedienteId:N}/{Guid.NewGuid():N}.pdf", "application/pdf", tamanio, hash, "x.pdf"));

        public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default)
        {
            Borrados.Add(relativeFilePath!);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("hash", 100)]                                                                  // valor arbitrario
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", 100)]      // mayúsculas
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678", 100)]       // 63
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567890", 100)]     // 65
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\n", 100)]    // salto de línea final
    [InlineData("", 100)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", 0)]        // tamaño 0
    public async Task Upload_InvarianteIncumplida_NoCreaDocumentoNiAuditoria_YCompensa(string hash, long tamanio)
    {
        var storage = new StorageConResultado(hash, tamanio);
        await using var context = Contexto();
        var (servicio, _) = Servicio(context, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.UploadDocumentoAsync(
            new UploadDocumentoDto(_expedienteId, "Escrito", "Escrito"), new MemoryStream([1]), "x.pdf", "application/pdf", 1));

        Assert.Single(storage.Borrados);   // compensación del archivo
        await using var lectura = Contexto();
        Assert.Equal(0, await lectura.Documentos.IgnoreQueryFilters().CountAsync(d => d.ExpedienteId == _expedienteId));
        Assert.Empty(await AccionesAsync());
    }

    // ══ El saneador de AuditService sigue intacto ═════════════════════════

    [Fact]
    public async Task AuditService_LaClaveHashSha256_SigueGuardandoseComoRedacted()
    {
        var entidadId = Guid.NewGuid().ToString();
        await using (var context = Contexto())
        {
            var auditoria = new AuditService(context,
                new TestUserService { UserId = _seniorId, TenantId = _tenantId, Role = Roles.AbogadoSenior },
                new TestTenantService { TenantId = _tenantId }, new HttpContextAccessor());

            var hash = new string('a', 64);
            await auditoria.LogAsync("PruebaSaneador", entidadId, "LOG_ASYNC", null, new { hashSha256 = hash, otro = "visible" });
            await auditoria.LogInTransactionAsync("PruebaSaneador", entidadId, "LOG_IN_TX", null, new { HashSha256 = hash });
            await context.SaveChangesAsync();
        }

        await using var lectura = Contexto();
        var eventos = await lectura.HistorialAuditorias.IgnoreQueryFilters()
            .Where(a => a.TenantId == _tenantId && a.EntidadId == entidadId).OrderBy(a => a.Accion).ToListAsync();
        Assert.Equal(2, eventos.Count);
        Assert.All(eventos, e =>
        {
            Assert.Contains("\"hashSha256\": \"[REDACTED]\"", e.ValoresNuevosJson!);
            Assert.DoesNotContain(new string('a', 64), e.ValoresNuevosJson!);
        });
        Assert.Contains("\"otro\": \"visible\"", eventos.Single(e => e.Accion == "LOG_ASYNC").ValoresNuevosJson!);
    }
}
