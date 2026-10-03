using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.4 — Auditoría documental definitiva por la API real (AuditService real, PostgreSQL): UPLOAD, UPDATE,
/// DELETE y DOWNLOAD con su JSON exacto, prueba contractual de sha256Contenido, datos prohibidos, tenant, roles,
/// concurrencia e invariantes de los documentos nuevos.
/// </summary>
public class Fase74AuditoriaApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private static readonly Regex Sha256Valido = new("^[0-9a-f]{64}\\z");

    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "aj_fase74_" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly string _slugA = $"fase74a-{Guid.NewGuid():N}";
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly string _slugB = $"fase74b-{Guid.NewGuid():N}";

    private readonly Guid _seniorId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _juniorId = Guid.NewGuid();
    private readonly Guid _asistenteId = Guid.NewGuid();
    private readonly Guid _seniorBId = Guid.NewGuid();
    private readonly Guid _expediente;

    public Fase74AuditoriaApiTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_storageDir);
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("FileStorage:BasePath", _storageDir));
        _client = _factory.CreateClient();

        SeedTenant(_tenantA, _slugA, (_seniorId, Roles.AbogadoSenior), (_adminId, Roles.AdminEstudio),
            (_juniorId, Roles.AbogadoJunior), (_asistenteId, Roles.AsistenteLegal));
        SeedTenant(_tenantB, _slugB, (_seniorBId, Roles.AbogadoSenior));
        _expediente = SeedExpediente(_tenantA, _juniorId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_storageDir, recursive: true); } catch { }
    }

    // ══ Datos ═════════════════════════════════════════════════════════════

    private ApplicationDbContext Contexto(Guid tenantId) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(TestConfiguration.PostgresConnectionString).Options,
        new TestTenantService { TenantId = tenantId });

    private void SeedTenant(Guid tenantId, string slug, params (Guid Id, string Rol)[] usuarios)
    {
        using var context = Contexto(tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Estudio " + slug, IdentificadorUrl = slug, ZonaHorariaId = "America/Guayaquil", Activo = true });
        foreach (var (id, rol) in usuarios)
        {
            var email = $"{id:N}@fase74.com";
            context.Usuarios.Add(new Usuario
            {
                Id = id, TenantId = tenantId, UserName = email, Email = email, NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(), NombreCompleto = "Usuario " + rol, Rol = rol, Activo = true
            });
        }
        context.SaveChanges();
    }

    private Guid SeedExpediente(Guid tenantId, Guid responsableId)
    {
        using var context = Contexto(tenantId);
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(), TenantId = tenantId, TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8], NombreRazonSocial = "Cliente Fase 7.4", Activo = true
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClienteId = cliente.Id, NumeroExpediente = "EXP-74-" + Guid.NewGuid().ToString("N")[..8],
            Titulo = "Caso Fase 7.4", Materia = "Civil", AbogadoResponsableId = responsableId, Estado = EstadoExpediente.Abierto
        };
        context.AddRange(cliente, expediente);
        context.SaveChanges();
        return expediente.Id;
    }

    /// <summary>Documento antiguo (anterior a la Fase 7): sin hash, sin nombre original y con tamaño 0.</summary>
    private async Task<Guid> SeedDocumentoHistoricoAsync()
    {
        await using var context = Contexto(_tenantA);
        var documento = new Documento
        {
            Id = Guid.NewGuid(), TenantId = _tenantA, ExpedienteId = _expediente, Titulo = "Escrito histórico", TipoDocumento = "Escrito",
            RutaAlmacenamiento = $"{_tenantA:N}/{Guid.NewGuid():N}.pdf", ContentType = "application/pdf", TamanioBytes = 0,
            HashSha256 = null, NombreArchivoOriginal = null, EstadoIa = EstadoProcesamientoIa.Pendiente
        };
        context.Documentos.Add(documento);
        await context.SaveChangesAsync();
        return documento.Id;
    }

    private async Task<Documento> LeerAsync(Guid id)
    {
        await using var context = Contexto(_tenantA);
        return await context.Documentos.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private async Task<List<HistorialAuditoria>> EventosAsync(Guid documentoId, Guid? tenantId = null)
    {
        await using var context = Contexto(tenantId ?? _tenantA);
        return await context.HistorialAuditorias.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.EntidadId == documentoId.ToString() && (tenantId == null || a.TenantId == tenantId))
            .OrderBy(a => a.Fecha).ToListAsync();
    }

    private static string[] Claves(string? json) => json == null
        ? []
        : JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ══ HTTP ══════════════════════════════════════════════════════════════

    private string Token(Guid userId, string rol, Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenantA;
        var tokenService = new TokenService(_factory.Services.GetRequiredService<IConfiguration>());
        return tokenService.GenerateAccessToken(
            new Usuario { Id = userId, Email = $"{userId:N}@fase74.com", NombreCompleto = "Usuario", Rol = rol, TenantId = tenant },
            new Tenant { Id = tenant, IdentificadorUrl = tenant == _tenantA ? _slugA : _slugB, Nombre = "Estudio" },
            rol, Permissions.GetPermissionsForRole(rol).ToList()).Item1;
    }

    private string Senior => Token(_seniorId, Roles.AbogadoSenior);

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string url, string token, HttpContent? content = null, Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenantA;
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Slug", tenant == _tenantA ? _slugA : _slugB);
        request.Headers.Add("X-Tenant-ID", tenant.ToString());
        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, Guid Id)> SubirAsync(byte[] contenido, string nombre, string mime,
        string? descripcion = "Descripción de la subida", string? token = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(_expediente.ToString()), "expedienteId" },
            { new StringContent("Demanda de alimentos"), "titulo" },
            { new StringContent("Demanda"), "tipoDocumento" }
        };
        if (descripcion != null)
        {
            form.Add(new StringContent(descripcion), "descripcion");
        }
        var archivo = new ByteArrayContent(contenido);
        archivo.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
        form.Add(archivo, "file", nombre);

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/v1/documentos/upload", token ?? Senior, form);
        return (status, status == HttpStatusCode.Created ? Json(body).GetProperty("data").GetProperty("id").GetGuid() : Guid.Empty);
    }

    private static StringContent Cuerpo(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private async Task<HttpStatusCode> PutAsync(Guid id, string titulo, string tipo, string? descripcion, string? token = null, Guid? tenantId = null) =>
        (await SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", token ?? Senior,
            Cuerpo(new { titulo, tipoDocumento = tipo, descripcion, version = (await LeerAsync(id)).Version }), tenantId)).Status;

    private async Task<HttpStatusCode> DeleteAsync(Guid id, string? token = null, Guid? tenantId = null) =>
        (await SendAsync(HttpMethod.Delete, $"/api/v1/documentos/{id}?version={(await LeerAsync(id)).Version}", token ?? Senior, tenantId: tenantId)).Status;

    private async Task<HttpStatusCode> DescargarAsync(Guid id, string? token = null, Guid? tenantId = null) =>
        (await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{id}/download", token ?? Senior, tenantId: tenantId)).Status;

    // ══ UPLOAD ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Upload_EventoExacto_EnLaMismaTransaccion()
    {
        var contenido = Fase72TestData.Pdf("upload-auditado");
        var (status, id) = await SubirAsync(contenido, "Demanda Alimentos.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.Created, status);

        var documento = await LeerAsync(id);
        var evento = Assert.Single(await EventosAsync(id));
        Assert.Equal("Documento", evento.Entidad);
        Assert.Equal("UPLOAD", evento.Accion);
        Assert.Equal(_tenantA, evento.TenantId);
        Assert.Equal(_seniorId, evento.UsuarioId);
        Assert.Equal($"{_seniorId:N}@fase74.com", evento.UsuarioEmail);
        Assert.Null(evento.ValoresAnterioresJson);

        Assert.Equal(
            ["contentType", "descripcion", "expedienteId", "nombreArchivoOriginal", "sha256Contenido", "tamanioBytes", "tipoDocumento", "titulo"],
            Claves(evento.ValoresNuevosJson));
        var nuevos = Json(evento.ValoresNuevosJson!);
        Assert.Equal(_expediente, nuevos.GetProperty("expedienteId").GetGuid());
        Assert.Equal("Demanda de alimentos", nuevos.GetProperty("titulo").GetString());
        Assert.Equal("Demanda", nuevos.GetProperty("tipoDocumento").GetString());
        Assert.Equal("Descripción de la subida", nuevos.GetProperty("descripcion").GetString());
        Assert.Equal("Demanda Alimentos.pdf", nuevos.GetProperty("nombreArchivoOriginal").GetString());
        Assert.Equal("application/pdf", nuevos.GetProperty("contentType").GetString());
        Assert.Equal(contenido.Length, nuevos.GetProperty("tamanioBytes").GetInt64());
        Assert.Equal(documento.HashSha256, nuevos.GetProperty("sha256Contenido").GetString());
    }

    [Fact]
    public async Task Upload_SinDescripcion_OmiteLaClave()
    {
        var (status, id) = await SubirAsync(Fase72TestData.Pdf("sin-desc"), "a.pdf", "application/pdf", descripcion: null);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.DoesNotContain("descripcion", Claves(Assert.Single(await EventosAsync(id)).ValoresNuevosJson));
    }

    public static TheoryData<string, string, byte[]> FormatosDeSubida => new()
    {
        { "escrito.pdf", "application/pdf", Fase72TestData.Pdf("invariantes") },
        { "contrato.docx", Fase72TestData.MimeDocx, Fase72TestData.Docx() },
        { "nota.txt", "text/plain", Encoding.UTF8.GetBytes("Nota en UTF-8: acción y señoría") },
        { "prueba.png", "image/png", Fase72TestData.Png }
    };

    [Theory]
    [MemberData(nameof(FormatosDeSubida))]
    public async Task Upload_InvariantesDelDocumentoNuevo(string nombre, string mime, byte[] contenido)
    {
        var (status, id) = await SubirAsync(contenido, nombre, mime);
        Assert.Equal(HttpStatusCode.Created, status);

        var documento = await LeerAsync(id);
        var enDisco = await File.ReadAllBytesAsync(Path.Combine(_storageDir, documento.RutaAlmacenamiento));
        var esperado = Convert.ToHexString(SHA256.HashData(contenido)).ToLowerInvariant();

        Assert.NotNull(documento.HashSha256);
        Assert.Equal(64, documento.HashSha256!.Length);
        Assert.Matches(Sha256Valido, documento.HashSha256);
        Assert.Equal(esperado, documento.HashSha256);                                          // bytes enviados
        Assert.Equal(Convert.ToHexString(SHA256.HashData(enDisco)).ToLowerInvariant(), documento.HashSha256);   // bytes guardados
        Assert.True(documento.TamanioBytes > 0);
        Assert.Equal(enDisco.Length, documento.TamanioBytes);
        Assert.Equal(contenido.Length, documento.TamanioBytes);
    }

    [Fact]
    public async Task Upload_ArchivoVacio_SinFilaArchivoNiAuditoria()
    {
        var antes = await ContarDocumentosDelExpedienteAsync();
        var (status, _) = await SubirAsync([], "vacio.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(antes, await ContarDocumentosDelExpedienteAsync());
        Assert.Empty(Directory.EnumerateFiles(_storageDir, "*", SearchOption.AllDirectories));
        await using var context = Contexto(_tenantA);
        Assert.Equal(0, await context.HistorialAuditorias.IgnoreQueryFilters().CountAsync(a => a.TenantId == _tenantA && a.Entidad == "Documento"));
    }

    private async Task<int> ContarDocumentosDelExpedienteAsync()
    {
        await using var context = Contexto(_tenantA);
        return await context.Documentos.IgnoreQueryFilters().CountAsync(d => d.ExpedienteId == _expediente);
    }

    // ══ UPDATE ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_SoloCamposModificados()
    {
        var (_, id) = await SubirAsync(Fase72TestData.Pdf("update"), "u.pdf", "application/pdf", descripcion: "Original");

        // 1) Solo el título
        Assert.Equal(HttpStatusCode.OK, await PutAsync(id, "Título nuevo", "Demanda", "Original"));
        // 2) Solo la descripción, a null
        Assert.Equal(HttpStatusCode.OK, await PutAsync(id, "Título nuevo", "Demanda", null));
        // 3) Los tres
        Assert.Equal(HttpStatusCode.OK, await PutAsync(id, "Final", "Escrito", "Nueva"));

        var updates = (await EventosAsync(id)).Where(e => e.Accion == "UPDATE").ToList();
        Assert.Equal(3, updates.Count);

        Assert.Equal(["titulo"], Claves(updates[0].ValoresAnterioresJson));
        Assert.Equal(["titulo"], Claves(updates[0].ValoresNuevosJson));
        Assert.Equal("Demanda de alimentos", Json(updates[0].ValoresAnterioresJson!).GetProperty("titulo").GetString());
        Assert.Equal("Título nuevo", Json(updates[0].ValoresNuevosJson!).GetProperty("titulo").GetString());

        Assert.Equal(["descripcion"], Claves(updates[1].ValoresAnterioresJson));
        Assert.Equal("Original", Json(updates[1].ValoresAnterioresJson!).GetProperty("descripcion").GetString());
        Assert.Equal(JsonValueKind.Null, Json(updates[1].ValoresNuevosJson!).GetProperty("descripcion").ValueKind);

        Assert.Equal(["descripcion", "tipoDocumento", "titulo"], Claves(updates[2].ValoresAnterioresJson));
        Assert.Equal(["descripcion", "tipoDocumento", "titulo"], Claves(updates[2].ValoresNuevosJson));
        Assert.Equal(JsonValueKind.Null, Json(updates[2].ValoresAnterioresJson!).GetProperty("descripcion").ValueKind);
        Assert.Equal("Escrito", Json(updates[2].ValoresNuevosJson!).GetProperty("tipoDocumento").GetString());

        Assert.All(updates, e => Assert.Equal(_tenantA, e.TenantId));
    }

    [Fact]
    public async Task Update_SinCambios_NoAudita()
    {
        var (_, id) = await SubirAsync(Fase72TestData.Pdf("noop"), "n.pdf", "application/pdf", descripcion: "Igual");
        Assert.Equal(HttpStatusCode.OK, await PutAsync(id, "  Demanda de alimentos  ", "Demanda", "Igual"));
        Assert.DoesNotContain(await EventosAsync(id), e => e.Accion == "UPDATE");
    }

    // ══ DELETE ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_EventoExacto_SinRutaFisica()
    {
        var (_, id) = await SubirAsync(Fase72TestData.Pdf("delete"), "Para borrar.pdf", "application/pdf");
        var documento = await LeerAsync(id);

        Assert.Equal(HttpStatusCode.OK, await DeleteAsync(id));

        var evento = Assert.Single(await EventosAsync(id), e => e.Accion == "DELETE");
        Assert.Null(evento.ValoresNuevosJson);
        Assert.Equal(["expedienteId", "nombreArchivoOriginal", "sha256Contenido", "titulo"], Claves(evento.ValoresAnterioresJson));
        var anteriores = Json(evento.ValoresAnterioresJson!);
        Assert.Equal(_expediente, anteriores.GetProperty("expedienteId").GetGuid());
        Assert.Equal("Demanda de alimentos", anteriores.GetProperty("titulo").GetString());
        Assert.Equal("Para borrar.pdf", anteriores.GetProperty("nombreArchivoOriginal").GetString());
        Assert.Equal(documento.HashSha256, anteriores.GetProperty("sha256Contenido").GetString());
        Assert.DoesNotContain(documento.RutaAlmacenamiento, evento.ValoresAnterioresJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_DocumentoHistoricoSinHash_OmiteSha256Contenido()
    {
        var id = await SeedDocumentoHistoricoAsync();

        Assert.Equal(HttpStatusCode.OK, await DeleteAsync(id));

        var evento = Assert.Single(await EventosAsync(id));
        Assert.Equal("DELETE", evento.Accion);
        Assert.Equal(["expedienteId", "titulo"], Claves(evento.ValoresAnterioresJson));   // sin sha256Contenido, ni siquiera null
        Assert.DoesNotContain("sha256Contenido", evento.ValoresAnterioresJson!);
    }

    [Fact]
    public async Task DocumentoHistorico_HashNuloYTamanio0_SeEditaYDescargaSinRomperLasCheck()
    {
        var id = await SeedDocumentoHistoricoAsync();
        await File.WriteAllBytesAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(_storageDir, _tenantA.ToString("N"))).FullName,
            Path.GetFileName((await LeerAsync(id)).RutaAlmacenamiento)), Fase72TestData.Pdf("historico"));

        Assert.Equal(HttpStatusCode.OK, await PutAsync(id, "Histórico editado", "Escrito", null));
        Assert.Equal(HttpStatusCode.OK, await DescargarAsync(id));
        var documento = await LeerAsync(id);
        Assert.Null(documento.HashSha256);
        Assert.Equal(0, documento.TamanioBytes);
    }

    // ══ DOWNLOAD ══════════════════════════════════════════════════════════

    [Fact]
    public async Task Download_EventoExacto_SinRutaNiHash()
    {
        var contenido = Fase72TestData.Pdf("download");
        var (_, id) = await SubirAsync(contenido, "d.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.OK, await DescargarAsync(id));

        var evento = Assert.Single(await EventosAsync(id), e => e.Accion == "DOWNLOAD");
        Assert.Null(evento.ValoresAnterioresJson);
        Assert.Equal(["expedienteId", "tamanioBytes"], Claves(evento.ValoresNuevosJson));
        Assert.Equal(_expediente, Json(evento.ValoresNuevosJson!).GetProperty("expedienteId").GetGuid());
        Assert.Equal(contenido.Length, Json(evento.ValoresNuevosJson!).GetProperty("tamanioBytes").GetInt64());
    }

    [Fact]
    public async Task Download_DenegadaOArchivoInexistente_NoAudita()
    {
        var (_, id) = await SubirAsync(Fase72TestData.Pdf("denegada"), "x.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.Forbidden, await DescargarAsync(id, Token(_asistenteId, Roles.AsistenteLegal)));

        var sinArchivo = await SeedDocumentoHistoricoAsync();     // su archivo no existe en disco
        Assert.Equal(HttpStatusCode.NotFound, await DescargarAsync(sinArchivo));

        Assert.DoesNotContain(await EventosAsync(id), e => e.Accion == "DOWNLOAD");
        Assert.Empty(await EventosAsync(sinArchivo));
    }

    // ══ Prueba contractual de sha256Contenido ═════════════════════════════

    [Fact]
    public async Task Contrato_Sha256Contenido_SoloEsElSha256DocumentalValido()
    {
        var contenidos = new[] { Fase72TestData.Pdf("contrato-1"), Fase72TestData.Pdf("contrato-2") };
        var ids = new List<Guid>();
        foreach (var contenido in contenidos)
        {
            var (_, id) = await SubirAsync(contenido, "c.pdf", "application/pdf");
            ids.Add(id);
        }
        await PutAsync(ids[0], "Editado", "Demanda", null);
        await DescargarAsync(ids[0]);
        await DeleteAsync(ids[1]);

        for (var i = 0; i < ids.Count; i++)
        {
            var documento = await LeerAsync(ids[i]);
            var esperado = Convert.ToHexString(SHA256.HashData(contenidos[i])).ToLowerInvariant();
            foreach (var evento in await EventosAsync(ids[i]))
            {
                var json = evento.ValoresAnterioresJson ?? evento.ValoresNuevosJson ?? "{}";
                var raiz = Json(json);

                if (evento.Accion is "UPLOAD" or "DELETE")
                {
                    var sha = raiz.GetProperty("sha256Contenido").GetString()!;
                    Assert.Equal(64, sha.Length);                     // 1. exactamente 64 caracteres
                    Assert.Matches(Sha256Valido, sha);                // 2. solo [0-9a-f]
                    Assert.Equal(documento.HashSha256, sha);          // 3. coincide con Documento.HashSha256
                    Assert.Equal(esperado, sha);                      //    y con el SHA-256 de los bytes
                }
                else
                {
                    Assert.False(raiz.TryGetProperty("sha256Contenido", out _), $"{evento.Accion} no debe llevar sha256Contenido");
                }

                foreach (var texto in new[] { evento.ValoresAnterioresJson, evento.ValoresNuevosJson }.Where(t => t != null))
                {
                    Assert.DoesNotContain("hashSha256", texto!, StringComparison.OrdinalIgnoreCase);   // 4. sin la clave sin sanear
                    Assert.DoesNotContain("[REDACTED]", texto!);
                    Assert.DoesNotContain("%PDF", texto!);                                              // 5. sin contenido
                    Assert.DoesNotContain(Convert.ToBase64String(contenidos[i]), texto!);
                    Assert.DoesNotContain("contrato-", texto!);
                }
            }
        }
    }

    // ══ Datos prohibidos en todos los eventos nuevos ══════════════════════

    [Fact]
    public async Task RecorridoCompleto_NingunEventoContieneDatosProhibidos()
    {
        var contenido = Fase72TestData.Pdf("MARCADOR-CONTENIDO-SECRETO");
        var (_, id) = await SubirAsync(contenido, "secreto.pdf", "application/pdf");
        var documento = await LeerAsync(id);
        await PutAsync(id, "Editado", "Escrito", "Nueva");
        await DescargarAsync(id);
        await DeleteAsync(id);

        var eventos = await EventosAsync(id);
        Assert.Equal(["UPLOAD", "UPDATE", "DOWNLOAD", "DELETE"], eventos.Select(e => e.Accion));

        var prohibidos = new[]
        {
            "rutaAlmacenamiento", documento.RutaAlmacenamiento, _tenantA.ToString("N") + "/", "metadatosJson", "estadoIa",
            "\"version\"", "xmin", "tenantId", "MARCADOR-CONTENIDO-SECRETO", Convert.ToBase64String(contenido), "%PDF", "[REDACTED]"
        };
        foreach (var evento in eventos)
        {
            var texto = (evento.ValoresAnterioresJson ?? "") + (evento.ValoresNuevosJson ?? "");
            foreach (var prohibido in prohibidos)
            {
                Assert.DoesNotContain(prohibido, texto, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ══ Tenant y roles ════════════════════════════════════════════════════

    [Fact]
    public async Task OtroTenant_NoProvocaEventosEnNingunTenant()
    {
        var (_, id) = await SubirAsync(Fase72TestData.Pdf("tenant"), "t.pdf", "application/pdf");
        var tokenB = Token(_seniorBId, Roles.AbogadoSenior, _tenantB);

        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync(id, "B", "B", null, tokenB, _tenantB));
        Assert.Equal(HttpStatusCode.Forbidden, await DeleteAsync(id, tokenB, _tenantB));
        Assert.Equal(HttpStatusCode.Forbidden, await DescargarAsync(id, tokenB, _tenantB));

        var eventos = await EventosAsync(id);
        Assert.Equal(["UPLOAD"], eventos.Select(e => e.Accion));
        Assert.All(eventos, e => Assert.Equal(_tenantA, e.TenantId));
        Assert.Empty(await EventosAsync(id, _tenantB));
    }

    [Fact]
    public async Task Roles_QuienProvocaCadaEvento()
    {
        // Junior responsable: UPLOAD, UPDATE y DOWNLOAD con su usuario; DELETE prohibido
        var junior = Token(_juniorId, Roles.AbogadoJunior);
        var (status, id) = await SubirAsync(Fase72TestData.Pdf("roles"), "r.pdf", "application/pdf", token: junior);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(HttpStatusCode.OK, await PutAsync(id, "Junior", "Demanda", null, junior));
        Assert.Equal(HttpStatusCode.OK, await DescargarAsync(id, junior));
        Assert.Equal(HttpStatusCode.Forbidden, await DeleteAsync(id, junior));

        // AsistenteLegal con tarea vigente: solo DOWNLOAD; PUT/DELETE/UPLOAD prohibidos por política
        await using (var context = Contexto(_tenantA))
        {
            context.Tareas.Add(new Tarea
            {
                Id = Guid.NewGuid(), TenantId = _tenantA, ExpedienteId = _expediente, AsignadoAUsuarioId = _asistenteId,
                Titulo = "Tarea", Estado = EstadoTarea.EnProgreso, Prioridad = Prioridad.Media, FechaVencimiento = DateTime.UtcNow.AddDays(2)
            });
            await context.SaveChangesAsync();
        }
        var asistente = Token(_asistenteId, Roles.AsistenteLegal);
        Assert.Equal(HttpStatusCode.OK, await DescargarAsync(id, asistente));
        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync(id, "A", "A", null, asistente));
        Assert.Equal(HttpStatusCode.Forbidden, await DeleteAsync(id, asistente));
        Assert.Equal(HttpStatusCode.Forbidden, (await SubirAsync(Fase72TestData.Pdf(), "a.pdf", "application/pdf", token: asistente)).Status);

        // SuperAdmin: nada
        var superAdmin = Token(Guid.NewGuid(), Roles.SuperAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, await DescargarAsync(id, superAdmin));
        Assert.Equal(HttpStatusCode.Forbidden, await DeleteAsync(id, superAdmin));

        // Admin: DELETE
        Assert.Equal(HttpStatusCode.OK, await DeleteAsync(id, Token(_adminId, Roles.AdminEstudio)));

        var eventos = await EventosAsync(id);
        Assert.Equal(
            [("UPLOAD", _juniorId), ("UPDATE", _juniorId), ("DOWNLOAD", _juniorId), ("DOWNLOAD", _asistenteId), ("DELETE", _adminId)],
            eventos.Select(e => (e.Accion, e.UsuarioId!.Value)));
    }

    // ══ Concurrencia: exactamente un ganador y una auditoría ══════════════

    [Fact]
    public async Task PutConcurrentes_UnSoloEventoUpdate()
    {
        var (_, id) = await SubirAsync(Fase72TestData.Pdf("put-concurrente"), "p.pdf", "application/pdf");
        var version = (await LeerAsync(id)).Version;

        var resultados = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", Senior,
            Cuerpo(new { titulo = $"Paralelo {i}", tipoDocumento = "D", version }))));

        Assert.Equal(1, resultados.Count(r => r.Status == HttpStatusCode.OK));
        var update = Assert.Single(await EventosAsync(id), e => e.Accion == "UPDATE");
        Assert.Equal((await LeerAsync(id)).Titulo, Json(update.ValoresNuevosJson!).GetProperty("titulo").GetString());
    }

    [Fact]
    public async Task DeleteConcurrentes_UnSoloEventoDelete()
    {
        var (_, id) = await SubirAsync(Fase72TestData.Pdf("delete-concurrente"), "dc.pdf", "application/pdf");
        var version = (await LeerAsync(id)).Version;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            SendAsync(HttpMethod.Delete, $"/api/v1/documentos/{id}?version={version}", Senior)));

        Assert.Equal(1, resultados.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Single(await EventosAsync(id), e => e.Accion == "DELETE");
    }

    [Fact]
    public async Task PutYDeleteConcurrentes_SoloElEventoDelGanador()
    {
        for (var intento = 0; intento < 4; intento++)
        {
            var (_, id) = await SubirAsync(Fase72TestData.Pdf("carrera-" + intento), "cr.pdf", "application/pdf");
            var version = (await LeerAsync(id)).Version;

            var put = SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", Senior, Cuerpo(new { titulo = "Editado", tipoDocumento = "D", version }));
            var delete = SendAsync(HttpMethod.Delete, $"/api/v1/documentos/{id}?version={version}", Senior);
            var (rPut, rDelete) = (await put, await delete);

            var acciones = (await EventosAsync(id)).Select(e => e.Accion).Where(a => a != "UPLOAD").ToList();
            if (rDelete.Status == HttpStatusCode.OK)
            {
                Assert.NotEqual(HttpStatusCode.OK, rPut.Status);
                Assert.Equal(["DELETE"], acciones);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, rPut.Status);
                Assert.Equal(["UPDATE"], acciones);
            }
        }
    }
}
