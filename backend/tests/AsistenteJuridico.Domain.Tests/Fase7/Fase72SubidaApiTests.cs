using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static AsistenteJuridico.Domain.Tests.Fase7.Fase72TestData;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.2 — Subida por la API real (JWT, políticas, middleware, PostgreSQL) con un almacenamiento en una carpeta
/// temporal propia: 201 + Location, metadatos guardados, errores 400/413/415 con errors[] y compensación si la base
/// de datos falla después de mover el archivo.
/// </summary>
public class Fase72SubidaApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "aj_fase72_api_" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly string _tenantSlug = $"fase72-{Guid.NewGuid():N}";
    private readonly Guid _seniorId = Guid.NewGuid();
    private readonly Guid _expedienteId = Guid.NewGuid();

    public Fase72SubidaApiTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_storageDir);
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("FileStorage:BasePath", _storageDir));
        _client = _factory.CreateClient();

        using var context = CrearContexto();
        var email = $"{_seniorId:N}@fase72.com";
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8],
            NombreRazonSocial = "Cliente Fase 7.2",
            Activo = true
        };
        context.Tenants.Add(new Tenant { Id = _tenantId, Nombre = "Estudio Fase 7.2", IdentificadorUrl = _tenantSlug, ZonaHorariaId = "America/Guayaquil", Activo = true });
        context.Usuarios.Add(new Usuario
        {
            Id = _seniorId, TenantId = _tenantId, UserName = email, Email = email,
            NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant(),
            NombreCompleto = "Abogado Senior", Rol = Roles.AbogadoSenior, Activo = true
        });
        context.Clientes.Add(cliente);
        context.Expedientes.Add(new Expediente
        {
            Id = _expedienteId, TenantId = _tenantId, ClienteId = cliente.Id,
            NumeroExpediente = "EXP-72-" + Guid.NewGuid().ToString("N")[..8], Titulo = "Caso Fase 7.2",
            Materia = "Civil", AbogadoResponsableId = _seniorId, Estado = EstadoExpediente.Abierto
        });
        context.SaveChanges();
    }

    public void Dispose()
    {
        try { Directory.Delete(_storageDir, recursive: true); } catch { }
    }

    private ApplicationDbContext CrearContexto() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(TestConfiguration.PostgresConnectionString).Options,
        new TestTenantService { TenantId = _tenantId });

    private string Token()
    {
        var tokenService = new TokenService(_factory.Services.GetRequiredService<IConfiguration>());
        var tenant = new Tenant { Id = _tenantId, IdentificadorUrl = _tenantSlug, Nombre = "Estudio Fase 7.2" };
        var user = new Usuario { Id = _seniorId, Email = $"{_seniorId:N}@fase72.com", NombreCompleto = "Abogado Senior", Rol = Roles.AbogadoSenior, TenantId = _tenantId };
        return tokenService.GenerateAccessToken(user, tenant, Roles.AbogadoSenior, Permissions.GetPermissionsForRole(Roles.AbogadoSenior).ToList()).Item1;
    }

    private async Task<(HttpStatusCode Status, string Body, Uri? Location)> SubirAsync(
        byte[] contenido, string nombre, string? mime, string titulo = "Demanda", string? descripcion = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(_expedienteId.ToString()), "expedienteId" },
            { new StringContent(titulo), "titulo" },
            { new StringContent("Demanda"), "tipoDocumento" }
        };
        if (descripcion != null)
        {
            form.Add(new StringContent(descripcion), "descripcion");
        }

        var archivo = new ByteArrayContent(contenido);
        if (mime != null)
        {
            archivo.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
        }
        form.Add(archivo, "file", nombre);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documentos/upload") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        request.Headers.Add("X-Tenant-Slug", _tenantSlug);
        request.Headers.Add("X-Tenant-ID", _tenantId.ToString());
        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.Location);
    }

    private static string[] Errores(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private int ArchivosEnStorage() =>
        Directory.EnumerateFiles(_storageDir, "*", SearchOption.AllDirectories).Count();

    private async Task<int> DocumentosDelExpedienteAsync()
    {
        await using var context = CrearContexto();
        return await context.Documentos.CountAsync(d => d.ExpedienteId == _expedienteId);
    }

    // ── Subida válida ────────────────────────────────────────────────────

    [Fact]
    public async Task SubidaValida_201ConLocation_MetadatosGuardados_SinRutaFisicaEnLaRespuesta()
    {
        var contenido = Docx();
        var (status, body, location) = await SubirAsync(contenido, "C:\\docs\\Contrato\u202E de arriendo.docx", MimeDocx,
            titulo: new string('T', 250), descripcion: "  Contrato firmado por ambas partes  ");

        Assert.Equal(HttpStatusCode.Created, status);
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        var documentoId = data.GetProperty("id").GetGuid();
        Assert.NotNull(location);
        Assert.EndsWith($"/api/v1/documentos/{documentoId}", location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rutaAlmacenamiento", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tenantId.ToString("N") + "/", body, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("Contrato de arriendo.docx", data.GetProperty("nombreArchivoOriginal").GetString());
        Assert.Equal("Contrato firmado por ambas partes", data.GetProperty("descripcion").GetString());
        Assert.Equal(MimeDocx, data.GetProperty("contentType").GetString());

        await using var context = CrearContexto();
        var documento = await context.Documentos.SingleAsync(d => d.Id == documentoId);
        Assert.Equal("Contrato de arriendo.docx", documento.NombreArchivoOriginal);
        Assert.Equal("Contrato firmado por ambas partes", documento.Descripcion);
        Assert.Equal(MimeDocx, documento.ContentType);
        Assert.Equal(contenido.Length, documento.TamanioBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(contenido)).ToLowerInvariant(), documento.HashSha256);
        Assert.Matches($"^{_tenantId:N}/{_expedienteId:N}/[0-9a-f]{{32}}\\.docx$", documento.RutaAlmacenamiento);
        Assert.Equal(contenido, await File.ReadAllBytesAsync(Path.Combine(_storageDir, documento.RutaAlmacenamiento)));
        Assert.False(Directory.Exists(Path.Combine(_storageDir, ".tmp")) && Directory.EnumerateFiles(Path.Combine(_storageDir, ".tmp")).Any());
    }

    [Fact]
    public async Task SubidaConMimeOctetStream_GuardaElCanonico()
    {
        var (status, body, _) = await SubirAsync(Pdf(), "escrito.pdf", "application/octet-stream");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("application/pdf", JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("contentType").GetString());
    }

    // ── Errores 415 / 400 / 413 con errors[] ─────────────────────────────

    [Fact]
    public async Task ExtensionNoPermitida_415ConCodigo()
    {
        var (status, body, _) = await SubirAsync(Pdf(), "script.exe", "application/octet-stream");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        Assert.Equal(["DOCUMENT_TYPE_NOT_ALLOWED"], Errores(body));
        Assert.Equal(0, ArchivosEnStorage());
        Assert.Equal(0, await DocumentosDelExpedienteAsync());
    }

    [Fact]
    public async Task MimeContradictorio_415ConCodigo()
    {
        var (status, body, _) = await SubirAsync(Pdf(), "escrito.pdf", "text/html");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        Assert.Equal(["DOCUMENT_TYPE_NOT_ALLOWED"], Errores(body));
    }

    [Fact]
    public async Task MagicBytesInvalidos_400ConCodigoYMensaje()
    {
        var (status, body, _) = await SubirAsync([0x4D, 0x5A, 0x90, 0x00, 0x03], "virus.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var errores = Errores(body);
        Assert.Equal("DOCUMENT_MAGIC_BYTES_INVALID", errores[0]);   // código primero (decisión D)
        Assert.Contains("Magic Bytes", errores[1]);
        Assert.Equal(0, ArchivosEnStorage());
        Assert.Equal(0, await DocumentosDelExpedienteAsync());
    }

    [Fact]
    public async Task DocxConMacros_400ConCodigo()
    {
        var (status, body, _) = await SubirAsync(Docx(conMacros: true), "macro.docx", MimeDocx);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("DOCUMENT_MAGIC_BYTES_INVALID", Errores(body)[0]);
    }

    [Fact]
    public async Task ArchivoVacio_400ConCodigo()
    {
        var (status, body, _) = await SubirAsync([], "vacio.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("DOCUMENT_MAGIC_BYTES_INVALID", Errores(body)[0]);
    }

    [Fact]
    public async Task Exactamente25MiB_201()
    {
        var (status, _, _) = await SubirAsync(PdfDeTamanio(25 * MiB), "grande.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.Created, status);
    }

    [Fact]
    public async Task VeinticincoMiBMasUno_413ConCodigo()
    {
        var (status, body, _) = await SubirAsync(PdfDeTamanio(25 * MiB + 1), "grande.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal(["DOCUMENT_SIZE_EXCEEDED"], Errores(body));
        Assert.Equal(0, ArchivosEnStorage());
        Assert.Equal(0, await DocumentosDelExpedienteAsync());
    }

    [Fact]
    public async Task PeticionMayorA26MiB_413ConEnvelope()
    {
        // Supera el límite de la petición/formulario (26 MiB), no solo el del archivo
        var (status, body, _) = await SubirAsync(PdfDeTamanio(27 * MiB), "enorme.pdf", "application/pdf");

        Assert.True(status == HttpStatusCode.RequestEntityTooLarge, $"Estado {(int)status}: {body}");
        var raiz = JsonDocument.Parse(body).RootElement;
        Assert.False(raiz.GetProperty("success").GetBoolean());
        Assert.Equal(["DOCUMENT_SIZE_EXCEEDED"], Errores(body));
        Assert.Equal(0, ArchivosEnStorage());
    }

    [Fact]
    public async Task PeticionMayorA26MiB_SobreKestrelReal_413ConEnvelope()
    {
        // TestServer no aplica MaxRequestBodySize; sobre Kestrel real el límite de [RequestSizeLimit] lanza
        // BadHttpRequestException (413) al leer el cuerpo, que el filtro traduce igualmente.
        // Expect: 100-continue hace que el servidor responda antes de recibir el cuerpo; sin él, Kestrel responde y
        // cierra la conexión mientras el cliente aún envía, y HttpClient falla al escribir antes de leer la respuesta.
        using var kestrel = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("FileStorage:BasePath", _storageDir));
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        using var client = kestrel.CreateClient();

        using var form = new MultipartFormDataContent
        {
            { new StringContent(_expedienteId.ToString()), "expedienteId" },
            { new StringContent("Demanda"), "titulo" },
            { new StringContent("Demanda"), "tipoDocumento" },
            { new ByteArrayContent(PdfDeTamanio(27 * MiB)), "file", "enorme.pdf" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documentos/upload") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        request.Headers.Add("X-Tenant-Slug", _tenantSlug);
        request.Headers.Add("X-Tenant-ID", _tenantId.ToString());
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"Estado {(int)response.StatusCode}: {body}");
        Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(["DOCUMENT_SIZE_EXCEEDED"], Errores(body));
        Assert.Equal(0, ArchivosEnStorage());
    }

    [Fact]
    public async Task ValidacionDeCampos_TituloDe251YDescripcionDe1001_400SinCodigo()
    {
        var titulo = await SubirAsync(Pdf(), "a.pdf", "application/pdf", titulo: new string('T', 251));
        Assert.Equal(HttpStatusCode.BadRequest, titulo.Status);
        Assert.Equal(["El título no puede exceder los 250 caracteres."], Errores(titulo.Body));

        var descripcion = await SubirAsync(Pdf(), "a.pdf", "application/pdf", descripcion: new string('d', 1001));
        Assert.Equal(HttpStatusCode.BadRequest, descripcion.Status);
        Assert.Equal(["La descripción no puede exceder los 1000 caracteres."], Errores(descripcion.Body));
        Assert.Equal(0, ArchivosEnStorage());
    }

    // ── Compensación si falla la base de datos ───────────────────────────

    private sealed class FalloAlGuardarDocumentos : SaveChangesInterceptor
    {
        public bool Activo { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Activo && eventData.Context!.ChangeTracker.Entries<Documento>().Any(e => e.State == EntityState.Added))
            {
                throw new DbUpdateException("Fallo simulado de la base de datos al insertar el documento.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class AuditoriaNula : IAuditService
    {
        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task FalloDeLaBaseTrasMoverElArchivo_NoQuedaNiArchivoFinalNiTemporal()
    {
        var interceptor = new FalloAlGuardarDocumentos();
        var tenantService = new TestTenantService { TenantId = _tenantId };
        var userService = new TestUserService { UserId = _seniorId, TenantId = _tenantId, Role = Roles.AbogadoSenior };
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(TestConfiguration.PostgresConnectionString)
                .AddInterceptors(interceptor)
                .Options,
            tenantService);

        var storage = new FileStorageService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:BasePath"] = _storageDir }).Build(),
            NullLogger<FileStorageService>.Instance);
        var service = new DocumentoService(context, storage, new ExpedienteAccessService(context, userService, tenantService),
            tenantService, userService, new AuditoriaNula(), new UploadDocumentoValidator(),
            new UpdateDocumentoValidator(), new DocumentoFilterValidator());

        interceptor.Activo = true;
        var contenido = Pdf("compensacion");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.UploadDocumentoAsync(
            new UploadDocumentoDto(_expedienteId, "Escrito", "Escrito"), new MemoryStream(contenido), "escrito.pdf", "application/pdf", contenido.Length));

        Assert.Equal(0, ArchivosEnStorage());                       // ni el final ni el temporal
        Assert.True(Directory.Exists(Path.Combine(_storageDir, _tenantId.ToString("N"), _expedienteId.ToString("N"))),
            "El archivo llegó a moverse al destino final antes de la compensación");
        Assert.Equal(0, await DocumentosDelExpedienteAsync());

        // Sin el fallo, el mismo servicio guarda archivo y fila
        interceptor.Activo = false;
        context.ChangeTracker.Clear();
        var creado = await service.UploadDocumentoAsync(
            new UploadDocumentoDto(_expedienteId, "Escrito", "Escrito"), new MemoryStream(contenido), "escrito.pdf", "application/pdf", contenido.Length);
        Assert.Equal(1, ArchivosEnStorage());
        Assert.Equal("escrito.pdf", creado.NombreArchivoOriginal);
    }

    [Fact]
    public async Task ExcepcionDeValidacion_NoEsDomainExceptionSinCodigo()
    {
        // Comprobación de contrato: las tres excepciones de la subida llevan su código
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("x"));
        var storage = new FileStorageService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:BasePath"] = _storageDir }).Build(),
            NullLogger<FileStorageService>.Instance);

        var tipo = await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() => storage.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "a.bat", null, 1));
        var tamanio = await Assert.ThrowsAsync<PayloadTooLargeException>(() => storage.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "a.pdf", null, 26 * MiB));
        var contenido = await Assert.ThrowsAsync<ValidationException>(() => storage.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "a.png", null, 1));

        Assert.Equal(DocumentoErrorCodes.TypeNotAllowed, tipo.ErrorCode);
        Assert.Equal(DocumentoErrorCodes.SizeExceeded, tamanio.ErrorCode);
        Assert.Equal(DocumentoErrorCodes.MagicBytesInvalid, contenido.ErrorCode);
    }
}
