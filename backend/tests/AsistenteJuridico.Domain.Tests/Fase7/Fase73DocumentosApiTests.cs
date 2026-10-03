using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.3 — API real (JWT, políticas, middleware) contra PostgreSQL: listado oficial paginado, alias obsoleto con
/// Deprecation/Link, PUT restringido, DELETE con xmin, bloqueo por Procesando, descarga endurecida y auditoría.
/// </summary>
public class Fase73DocumentosApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string DeprecationEsperada = "@1790985600";

    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "aj_fase73_" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly string _slugA = $"fase73a-{Guid.NewGuid():N}";
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly string _slugB = $"fase73b-{Guid.NewGuid():N}";

    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _seniorId = Guid.NewGuid();
    private readonly Guid _juniorResponsableId = Guid.NewGuid();
    private readonly Guid _juniorOtroId = Guid.NewGuid();
    private readonly Guid _asistenteId = Guid.NewGuid();
    private readonly Guid _seniorBId = Guid.NewGuid();

    private readonly Guid _expediente;
    private readonly Guid _expedienteBorrado;
    private readonly Guid _expedienteB;

    public Fase73DocumentosApiTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_storageDir);
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("FileStorage:BasePath", _storageDir));
        _client = _factory.CreateClient();

        SeedTenant(_tenantA, _slugA,
            (_adminId, Roles.AdminEstudio), (_seniorId, Roles.AbogadoSenior), (_juniorResponsableId, Roles.AbogadoJunior),
            (_juniorOtroId, Roles.AbogadoJunior), (_asistenteId, Roles.AsistenteLegal));
        SeedTenant(_tenantB, _slugB, (_seniorBId, Roles.AbogadoSenior));

        _expediente = SeedExpediente(_tenantA, _juniorResponsableId, eliminado: false);
        _expedienteBorrado = SeedExpediente(_tenantA, _juniorResponsableId, eliminado: true);
        _expedienteB = SeedExpediente(_tenantB, _seniorBId, eliminado: false);
    }

    public void Dispose()
    {
        try
        {
            foreach (var enlace in Directory.EnumerateDirectories(_storageDir, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).LinkTarget != null).ToList())
            {
                Directory.Delete(enlace);
            }

            Directory.Delete(_storageDir, recursive: true);
        }
        catch { }
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
            var email = $"{id:N}@fase73.com";
            context.Usuarios.Add(new Usuario
            {
                Id = id, TenantId = tenantId, UserName = email, Email = email,
                NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant(),
                NombreCompleto = "Usuario " + rol, Rol = rol, Activo = true
            });
        }
        context.SaveChanges();
    }

    private Guid SeedExpediente(Guid tenantId, Guid responsableId, bool eliminado)
    {
        using var context = Contexto(tenantId);
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(), TenantId = tenantId, TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8], NombreRazonSocial = "Cliente Fase 7.3", Activo = true
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClienteId = cliente.Id,
            NumeroExpediente = "EXP-73-" + Guid.NewGuid().ToString("N")[..8], Titulo = "Caso Fase 7.3", Materia = "Civil",
            AbogadoResponsableId = responsableId, Estado = EstadoExpediente.Abierto,
            IsDeleted = eliminado, DeletedAt = eliminado ? DateTime.UtcNow : null
        };
        context.AddRange(cliente, expediente);
        context.SaveChanges();
        return expediente.Id;
    }

    private async Task<Guid> SeedDocumentoAsync(
        Guid? expedienteId = null,
        string titulo = "Demanda",
        string tipo = "Demanda",
        DateTime? creado = null,
        string? nombreOriginal = "demanda.pdf",
        string? descripcion = "Descripción inicial",
        EstadoProcesamientoIa estado = EstadoProcesamientoIa.Pendiente,
        byte[]? contenido = null,
        string? ruta = null,
        bool eliminado = false,
        Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenantA;
        var expediente = expedienteId ?? _expediente;
        var id = Guid.NewGuid();
        var rutaRelativa = ruta ?? $"{tenant:N}/{expediente:N}/{Guid.NewGuid():N}.pdf";
        if (contenido != null)
        {
            var completa = Path.Combine(_storageDir, rutaRelativa);
            Directory.CreateDirectory(Path.GetDirectoryName(completa)!);
            await File.WriteAllBytesAsync(completa, contenido);
        }

        await using var context = Contexto(tenant);
        context.Documentos.Add(new Documento
        {
            Id = id, TenantId = tenant, ExpedienteId = expediente, Titulo = titulo, TipoDocumento = tipo,
            Descripcion = descripcion, NombreArchivoOriginal = nombreOriginal, RutaAlmacenamiento = rutaRelativa,
            ContentType = "application/pdf", TamanioBytes = contenido?.Length ?? 10, HashSha256 = new string('b', 64),
            EstadoIa = estado, MetadatosJson = "{\"fase6\":true}", CreatedAt = creado ?? DateTime.UtcNow,
            CreatedBy = "creador@fase73.com", IsDeleted = eliminado, DeletedAt = eliminado ? DateTime.UtcNow : null
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task AgregarTareaAsistenteAsync(EstadoTarea estado)
    {
        await using var context = Contexto(_tenantA);
        context.Tareas.Add(new Tarea
        {
            Id = Guid.NewGuid(), TenantId = _tenantA, ExpedienteId = _expediente, AsignadoAUsuarioId = _asistenteId,
            Titulo = "Tarea " + estado, Estado = estado, Prioridad = Prioridad.Media, FechaVencimiento = DateTime.UtcNow.AddDays(3)
        });
        await context.SaveChangesAsync();
    }

    private async Task<Documento> LeerAsync(Guid id)
    {
        await using var context = Contexto(_tenantA);
        return await context.Documentos.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private async Task<int> AuditoriasAsync(Guid documentoId)
    {
        await using var context = Contexto(_tenantA);
        return await context.HistorialAuditorias.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == _tenantA && a.EntidadId == documentoId.ToString());
    }

    // ══ HTTP ══════════════════════════════════════════════════════════════

    private string Token(Guid userId, string rol, Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenantA;
        var slug = tenant == _tenantA ? _slugA : _slugB;
        var tokenService = new TokenService(_factory.Services.GetRequiredService<IConfiguration>());
        return tokenService.GenerateAccessToken(
            new Usuario { Id = userId, Email = $"{userId:N}@fase73.com", NombreCompleto = "Usuario", Rol = rol, TenantId = tenant },
            new Tenant { Id = tenant, IdentificadorUrl = slug, Nombre = "Estudio" },
            rol,
            Permissions.GetPermissionsForRole(rol).ToList()).Item1;
    }

    private string Senior => Token(_seniorId, Roles.AbogadoSenior);

    private sealed record Respuesta(HttpStatusCode Status, string Body, HttpResponseHeaders Headers, HttpContentHeaders? ContentHeaders, byte[] Bytes)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public string[] Errores => Json.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();
        public JsonElement Data => Json.GetProperty("data");
    }

    private async Task<Respuesta> SendAsync(HttpMethod method, string url, string token, object? cuerpo = null, Guid? tenantId = null, string? jsonCrudo = null)
    {
        var tenant = tenantId ?? _tenantA;
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Slug", tenant == _tenantA ? _slugA : _slugB);
        request.Headers.Add("X-Tenant-ID", tenant.ToString());
        if (jsonCrudo != null || cuerpo != null)
        {
            request.Content = new StringContent(jsonCrudo ?? JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new Respuesta(response.StatusCode, Encoding.UTF8.GetString(bytes), response.Headers, response.Content.Headers, bytes);
    }

    private Task<Respuesta> PutAsync(Guid id, object cuerpo, string? token = null) =>
        SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", token ?? Senior, cuerpo);

    private Task<Respuesta> DeleteAsync(Guid id, uint? version, string? token = null) =>
        SendAsync(HttpMethod.Delete, $"/api/v1/documentos/{id}" + (version.HasValue ? $"?version={version}" : ""), token ?? Senior);

    private Task<Respuesta> ListarAsync(string query, string? token = null) =>
        SendAsync(HttpMethod.Get, "/api/v1/documentos?" + query, token ?? Senior);

    // ══ LISTADO ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Listado_PaginacionYOrdenDeterminista_ConEmpatesDeCreatedAt()
    {
        var baseFecha = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var ids = new List<(Guid Id, DateTime Creado)>();
        foreach (var minutos in new[] { 0, 10, 10, 10, 20, 30, 30 })   // empates en 10 y 30
        {
            var creado = baseFecha.AddMinutes(minutos);
            ids.Add((await SeedDocumentoAsync(creado: creado), creado));
        }

        var esperado = ids.OrderByDescending(x => x.Creado).ThenByDescending(x => x.Id).Select(x => x.Id).ToList();

        var recorrido = new List<Guid>();
        for (var pagina = 1; pagina <= 3; pagina++)
        {
            var r = await ListarAsync($"expedienteId={_expediente}&pageNumber={pagina}&pageSize=3");
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.Equal(7, r.Data.GetProperty("totalCount").GetInt32());
            Assert.Equal(3, r.Data.GetProperty("totalPages").GetInt32());
            Assert.Equal(pagina, r.Data.GetProperty("pageNumber").GetInt32());
            Assert.Equal(3, r.Data.GetProperty("pageSize").GetInt32());
            Assert.Equal(pagina > 1, r.Data.GetProperty("hasPreviousPage").GetBoolean());
            Assert.Equal(pagina < 3, r.Data.GetProperty("hasNextPage").GetBoolean());
            recorrido.AddRange(r.Data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        }

        Assert.Equal(esperado, recorrido);               // orden CreatedAt DESC, Id DESC, sin solapes ni huecos

        // Repetir la misma página devuelve exactamente lo mismo
        var otraVez = await ListarAsync($"expedienteId={_expediente}&pageNumber=2&pageSize=3");
        Assert.Equal(esperado.Skip(3).Take(3), otraVez.Data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData("pageNumber=0&pageSize=5", 1, 5)]
    [InlineData("pageNumber=-3&pageSize=5", 1, 5)]
    [InlineData("pageNumber=1&pageSize=500", 1, 100)]
    [InlineData("pageNumber=1&pageSize=101", 1, 100)]
    [InlineData("pageNumber=1&pageSize=0", 1, 10)]
    [InlineData("", 1, 10)]
    public async Task Listado_PageNumberYPageSizeFueraDeRango_SeCorrigen(string paginacion, int paginaEsperada, int tamanioEsperado)
    {
        await SeedDocumentoAsync();
        var r = await ListarAsync($"expedienteId={_expediente}&{paginacion}");

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(paginaEsperada, r.Data.GetProperty("pageNumber").GetInt32());
        Assert.Equal(tamanioEsperado, r.Data.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Listado_Filtros_TipoFechasYBusqueda()
    {
        var enero = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var marzo = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var demandaEnero = await SeedDocumentoAsync(titulo: "Demanda de alimentos", tipo: "Demanda", creado: enero, nombreOriginal: "alimentos.pdf");
        var contratoMarzo = await SeedDocumentoAsync(titulo: "Contrato", tipo: "Contrato", creado: marzo, nombreOriginal: "ARRIENDO-Firmado.pdf");
        var demandaMarzo = await SeedDocumentoAsync(titulo: "Otra demanda", tipo: "Demanda", creado: marzo.AddDays(1), nombreOriginal: null);

        async Task<Guid[]> Ids(string filtros) => (await ListarAsync($"expedienteId={_expediente}&{filtros}"))
            .Data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

        Assert.Equal([demandaMarzo, demandaEnero], await Ids("tipoDocumento=Demanda"));
        Assert.Equal([demandaMarzo, contratoMarzo], await Ids("fechaDesde=2026-03-01T00:00:00Z"));
        Assert.Equal([demandaEnero], await Ids("fechaHasta=2026-02-01T00:00:00Z"));
        Assert.Equal([contratoMarzo], await Ids("fechaDesde=2026-03-01&fechaHasta=2026-03-15T23:59:59"));
        Assert.Equal([demandaMarzo, demandaEnero], await Ids("searchTerm=DEMANDA"));          // título, sin mayúsculas
        Assert.Equal([contratoMarzo], await Ids("searchTerm=arriendo"));                      // nombre original
        Assert.Empty(await Ids("searchTerm=%25"));                                             // '%' literal, no comodín
    }

    [Theory]
    [InlineData("")]
    [InlineData("expedienteId=00000000-0000-0000-0000-000000000000")]
    public async Task Listado_SinExpedienteId_400(string query)
    {
        var r = await ListarAsync(query);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal(["El expediente es obligatorio."], r.Errores);
    }

    [Fact]
    public async Task Listado_ValidacionesDeFiltros_400()
    {
        var fechas = await ListarAsync($"expedienteId={_expediente}&fechaDesde=2026-05-01&fechaHasta=2026-04-01");
        Assert.Equal(HttpStatusCode.BadRequest, fechas.Status);
        Assert.Equal(["La fecha desde no puede ser posterior a la fecha hasta."], fechas.Errores);

        var busqueda = await ListarAsync($"expedienteId={_expediente}&searchTerm={new string('x', 201)}");
        Assert.Equal(HttpStatusCode.BadRequest, busqueda.Status);
    }

    [Fact]
    public async Task Listado_ExcluyeEliminadosYOtrosExpedientes_SinRutaFisica()
    {
        var visible = await SeedDocumentoAsync();
        await SeedDocumentoAsync(eliminado: true);
        var otroExpediente = SeedExpediente(_tenantA, _juniorResponsableId, eliminado: false);
        await SeedDocumentoAsync(expedienteId: otroExpediente);

        var r = await ListarAsync($"expedienteId={_expediente}");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal([visible], r.Data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.DoesNotContain("rutaAlmacenamiento", r.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tenantA.ToString("N") + "/", r.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Listado_TenantIsolation()
    {
        var docA = await SeedDocumentoAsync();
        var docB = await SeedDocumentoAsync(expedienteId: _expedienteB, tenantId: _tenantB);
        var tokenB = Token(_seniorBId, Roles.AbogadoSenior, _tenantB);

        // Usuario de B pidiendo el expediente de A: 403, sin datos
        var cruzado = await SendAsync(HttpMethod.Get, $"/api/v1/documentos?expedienteId={_expediente}", tokenB, tenantId: _tenantB);
        Assert.Equal(HttpStatusCode.Forbidden, cruzado.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], cruzado.Errores);
        Assert.DoesNotContain(docA.ToString(), cruzado.Body);

        // B en su expediente solo ve lo suyo
        var propio = await SendAsync(HttpMethod.Get, $"/api/v1/documentos?expedienteId={_expedienteB}", tokenB, tenantId: _tenantB);
        Assert.Equal([docB], propio.Data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Listado_PbacPorRol()
    {
        await SeedDocumentoAsync();
        var url = $"/api/v1/documentos?expedienteId={_expediente}";

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, Token(_adminId, Roles.AdminEstudio))).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, Senior)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, Token(_juniorResponsableId, Roles.AbogadoJunior))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, url, Token(_juniorOtroId, Roles.AbogadoJunior))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, url, Token(Guid.NewGuid(), Roles.SuperAdmin))).Status);

        var asistente = Token(_asistenteId, Roles.AsistenteLegal);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, url, asistente)).Status);
        await AgregarTareaAsistenteAsync(EstadoTarea.Completada);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, url, asistente)).Status);
        await AgregarTareaAsistenteAsync(EstadoTarea.EnProgreso);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, asistente)).Status);
    }

    [Fact]
    public async Task Listado_ExpedienteEliminado_404()
    {
        await SeedDocumentoAsync(expedienteId: _expedienteBorrado);
        var r = await ListarAsync($"expedienteId={_expedienteBorrado}");
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.Empty(r.Errores);   // 404 de expediente, sin código documental (como en la 7.1)
    }

    // ══ ALIAS OBSOLETO ════════════════════════════════════════════════════

    private void AssertCabecerasDeObsolescencia(Respuesta r, Guid expedienteId)
    {
        Assert.Equal([DeprecationEsperada], r.Headers.GetValues("Deprecation"));
        Assert.Equal([$"</api/v1/documentos?expedienteId={expedienteId}>; rel=\"successor-version\""], r.Headers.GetValues("Link"));
        Assert.False(r.Headers.Contains("Sunset"));
    }

    [Fact]
    public async Task Alias_MismaConsultaQueLaOficial_ConDeprecationYLink()
    {
        var baseFecha = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 4; i++)
        {
            await SeedDocumentoAsync(creado: baseFecha.AddMinutes(i % 2));   // con empates
        }
        await SeedDocumentoAsync(eliminado: true);

        var alias = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{_expediente}", Senior);
        var oficial = await ListarAsync($"expedienteId={_expediente}&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, alias.Status);
        Assert.Equal(JsonValueKind.Array, alias.Data.ValueKind);   // forma de siempre: lista, no PagedResult
        Assert.Equal(
            oficial.Data.GetProperty("items").EnumerateArray().Select(i => i.GetRawText()),
            alias.Data.EnumerateArray().Select(i => i.GetRawText()));
        AssertCabecerasDeObsolescencia(alias, _expediente);

        // Ruta oficial: sin cabeceras de obsolescencia
        Assert.False(oficial.Headers.Contains("Deprecation"));
        Assert.False(oficial.Headers.Contains("Link"));
        Assert.False(oficial.Headers.Contains("Sunset"));
    }

    [Fact]
    public void Deprecation_EsUnaFechaEstructuradaDe20261003()
    {
        // RFC 9745: Structured Field Date = '@' + segundos Unix enteros
        Assert.Matches("^@[0-9]+$", DeprecationEsperada);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(DeprecationEsperada[1..])));
    }

    [Fact]
    public async Task Alias_CabecerasTambienEn403Y404()
    {
        await SeedDocumentoAsync();
        var prohibido = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{_expediente}", Token(_juniorOtroId, Roles.AbogadoJunior));
        Assert.Equal(HttpStatusCode.Forbidden, prohibido.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], prohibido.Errores);
        AssertCabecerasDeObsolescencia(prohibido, _expediente);

        var noEncontrado = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{_expedienteBorrado}", Senior);
        Assert.Equal(HttpStatusCode.NotFound, noEncontrado.Status);
        AssertCabecerasDeObsolescencia(noEncontrado, _expedienteBorrado);
    }

    // ══ PUT ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Put_Valido_ActualizaSoloLosTresCampos_YDevuelveNuevaVersion()
    {
        var id = await SeedDocumentoAsync();
        var antes = await LeerAsync(id);

        var r = await PutAsync(id, new { titulo = "  Demanda reformada  ", tipoDocumento = " Escrito ", descripcion = " Nueva ", version = antes.Version });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("Demanda reformada", r.Data.GetProperty("titulo").GetString());
        Assert.DoesNotContain("rutaAlmacenamiento", r.Body, StringComparison.OrdinalIgnoreCase);
        var despues = await LeerAsync(id);
        Assert.Equal("Demanda reformada", despues.Titulo);
        Assert.Equal("Escrito", despues.TipoDocumento);
        Assert.Equal("Nueva", despues.Descripcion);
        Assert.NotEqual(antes.Version, despues.Version);
        Assert.Equal(despues.Version, r.Data.GetProperty("version").GetUInt32());
        Assert.Equal($"{_seniorId:N}@fase73.com", despues.UpdatedBy);
        Assert.NotNull(despues.UpdatedAt);
    }

    [Fact]
    public async Task Put_PayloadMalicioso_NoModificaCamposProtegidos()
    {
        var id = await SeedDocumentoAsync(estado: EstadoProcesamientoIa.Procesado);
        var antes = await LeerAsync(id);
        var json = $$"""
            {
              "titulo": "Titulo nuevo", "tipoDocumento": "Escrito", "descripcion": "Desc", "version": {{antes.Version}},
              "id": "{{Guid.NewGuid()}}", "tenantId": "{{_tenantB}}", "expedienteId": "{{_expedienteB}}",
              "rutaAlmacenamiento": "../../etc/passwd", "hashSha256": "{{new string('f', 64)}}",
              "contentType": "text/html", "tamanioBytes": 999999, "nombreArchivoOriginal": "malo.exe",
              "estadoIa": 1, "metadatosJson": "{\"inyectado\":true}", "createdAt": "2000-01-01T00:00:00Z",
              "createdBy": "atacante", "isDeleted": true, "deletedBy": "atacante"
            }
            """;

        var r = await SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", Senior, jsonCrudo: json);
        Assert.Equal(HttpStatusCode.OK, r.Status);

        var despues = await LeerAsync(id);
        Assert.Equal("Titulo nuevo", despues.Titulo);
        Assert.Equal(antes.Id, despues.Id);
        Assert.Equal(antes.TenantId, despues.TenantId);
        Assert.Equal(antes.ExpedienteId, despues.ExpedienteId);
        Assert.Equal(antes.RutaAlmacenamiento, despues.RutaAlmacenamiento);
        Assert.Equal(antes.HashSha256, despues.HashSha256);
        Assert.Equal(antes.ContentType, despues.ContentType);
        Assert.Equal(antes.TamanioBytes, despues.TamanioBytes);
        Assert.Equal(antes.NombreArchivoOriginal, despues.NombreArchivoOriginal);
        Assert.Equal(antes.EstadoIa, despues.EstadoIa);
        Assert.Equal(antes.MetadatosJson, despues.MetadatosJson);
        Assert.Equal(antes.CreatedAt, despues.CreatedAt);
        Assert.Equal(antes.CreatedBy, despues.CreatedBy);
        Assert.False(despues.IsDeleted);
        Assert.Null(despues.DeletedBy);
    }

    [Fact]
    public async Task Put_SinCambios_200SinEscribir()
    {
        var id = await SeedDocumentoAsync(titulo: "Igual", tipo: "Demanda", descripcion: null);
        var antes = await LeerAsync(id);

        var r = await PutAsync(id, new { titulo = "Igual", tipoDocumento = "Demanda", descripcion = "   ", version = antes.Version });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        var despues = await LeerAsync(id);
        Assert.Equal(antes.Version, despues.Version);
        Assert.Null(despues.UpdatedAt);
    }

    [Fact]
    public async Task Put_DescripcionNulaOAusente_LaBorra()
    {
        var id = await SeedDocumentoAsync(descripcion: "Existente");
        var version = (await LeerAsync(id)).Version;

        var r = await PutAsync(id, new { titulo = "Demanda", tipoDocumento = "Demanda", version });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Null((await LeerAsync(id)).Descripcion);
    }

    public static TheoryData<string, string> CuerposInvalidos => new()
    {
        { "{\"titulo\":\"" + new string('T', 251) + "\",\"tipoDocumento\":\"D\",\"version\":1}", "El título no puede exceder los 250 caracteres." },
        { "{\"titulo\":\"   \",\"tipoDocumento\":\"D\",\"version\":1}", "El título del documento es obligatorio." },
        { "{\"tipoDocumento\":\"D\",\"version\":1}", "El título del documento es obligatorio." },
        { "{\"titulo\":\"T\",\"tipoDocumento\":\"\",\"version\":1}", "El tipo de documento es obligatorio." },
        { "{\"titulo\":\"T\",\"tipoDocumento\":\"" + new string('D', 101) + "\",\"version\":1}", "El tipo de documento no puede exceder los 100 caracteres." },
        { "{\"titulo\":\"T\",\"tipoDocumento\":\"D\",\"descripcion\":\"" + new string('d', 1001) + "\",\"version\":1}", "La descripción no puede exceder los 1000 caracteres." },
        { "{\"titulo\":\"T\",\"tipoDocumento\":\"D\"}", "La versión del documento es obligatoria." }
    };

    [Theory]
    [MemberData(nameof(CuerposInvalidos))]
    public async Task Put_Validaciones_400SinModificar(string json, string mensaje)
    {
        var id = await SeedDocumentoAsync();
        var antes = await LeerAsync(id);

        var r = await SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", Senior, jsonCrudo: json);

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Contains(mensaje, r.Errores);
        Assert.Equal(antes.Version, (await LeerAsync(id)).Version);
    }

    [Fact]
    public async Task Put_TituloDe250_Aceptado()
    {
        var id = await SeedDocumentoAsync();
        var r = await PutAsync(id, new { titulo = new string('T', 250), tipoDocumento = "D", version = (await LeerAsync(id)).Version });
        Assert.Equal(HttpStatusCode.OK, r.Status);
    }

    [Fact]
    public async Task Put_InexistenteEliminadoOExpedienteEliminado_404()
    {
        var eliminado = await SeedDocumentoAsync(eliminado: true);
        var enBorrado = await SeedDocumentoAsync(expedienteId: _expedienteBorrado);
        var cuerpo = new { titulo = "T", tipoDocumento = "D", version = 1u };

        foreach (var id in new[] { Guid.NewGuid(), eliminado, enBorrado })
        {
            var r = await PutAsync(id, cuerpo);
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.Equal(["DOCUMENT_NOT_FOUND"], r.Errores);
        }
    }

    [Fact]
    public async Task Put_PbacYTenant()
    {
        var id = await SeedDocumentoAsync();
        uint Version() => LeerAsync(id).GetAwaiter().GetResult().Version;
        object Cuerpo(string t) => new { titulo = t, tipoDocumento = "D", version = Version() };

        var otroTenant = await SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", Token(_seniorBId, Roles.AbogadoSenior, _tenantB), Cuerpo("B"), tenantId: _tenantB);
        Assert.Equal(HttpStatusCode.Forbidden, otroTenant.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], otroTenant.Errores);

        var juniorAjeno = await PutAsync(id, Cuerpo("J"), Token(_juniorOtroId, Roles.AbogadoJunior));
        Assert.Equal(HttpStatusCode.Forbidden, juniorAjeno.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], juniorAjeno.Errores);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(id, Cuerpo("A"), Token(_asistenteId, Roles.AsistenteLegal))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(id, Cuerpo("S"), Token(Guid.NewGuid(), Roles.SuperAdmin))).Status);
        Assert.Equal("Demanda", (await LeerAsync(id)).Titulo);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(id, Cuerpo("Junior"), Token(_juniorResponsableId, Roles.AbogadoJunior))).Status);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(id, Cuerpo("Admin"), Token(_adminId, Roles.AdminEstudio))).Status);
        Assert.Equal("Admin", (await LeerAsync(id)).Titulo);
    }

    [Fact]
    public async Task Put_Procesando_409SinTocarEstadoIa()
    {
        var id = await SeedDocumentoAsync(estado: EstadoProcesamientoIa.Procesando);
        var antes = await LeerAsync(id);

        var r = await PutAsync(id, new { titulo = "Cambio", tipoDocumento = "D", version = antes.Version });

        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal(["DOCUMENT_PROCESSING"], r.Errores);
        var despues = await LeerAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Procesando, despues.EstadoIa);
        Assert.Equal("Demanda", despues.Titulo);
        Assert.Equal(antes.Version, despues.Version);
    }

    [Fact]
    public async Task Put_VersionObsoleta_409SinSobrescribir()
    {
        var id = await SeedDocumentoAsync();
        var versionLeida = (await LeerAsync(id)).Version;

        // Otro proceso modifica la fila directamente en PostgreSQL (cambia xmin)
        await using (var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString))
        {
            await conexion.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE documentos SET \"Titulo\" = 'Externo' WHERE \"Id\" = @id", conexion);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        var r = await PutAsync(id, new { titulo = "Mio", tipoDocumento = "D", version = versionLeida });

        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal(["DOCUMENT_CONCURRENCY_CONFLICT"], r.Errores);
        Assert.Equal("Externo", (await LeerAsync(id)).Titulo);
    }

    [Fact]
    public async Task Put_MatrizDeVersion_Ausente400_Cero400_Obsoleta409_Vigente200()
    {
        var id = await SeedDocumentoAsync();
        var vigente = (await LeerAsync(id)).Version;

        var ausente = await SendAsync(HttpMethod.Put, $"/api/v1/documentos/{id}", Senior, jsonCrudo: "{\"titulo\":\"T\",\"tipoDocumento\":\"D\"}");
        Assert.Equal(HttpStatusCode.BadRequest, ausente.Status);
        Assert.Equal(["La versión del documento es obligatoria."], ausente.Errores);

        var cero = await PutAsync(id, new { titulo = "T", tipoDocumento = "D", version = 0u });
        Assert.Equal(HttpStatusCode.BadRequest, cero.Status);
        Assert.False(cero.Json.GetProperty("success").GetBoolean());
        Assert.Equal(["La versión del documento no es válida."], cero.Errores);   // validación, sin código documental

        var obsoleta = await PutAsync(id, new { titulo = "T", tipoDocumento = "D", version = vigente + 1 });
        Assert.Equal(HttpStatusCode.Conflict, obsoleta.Status);
        Assert.Equal(["DOCUMENT_CONCURRENCY_CONFLICT"], obsoleta.Errores);

        Assert.Equal(vigente, (await LeerAsync(id)).Version);   // ninguno de los rechazos escribió

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(id, new { titulo = "T", tipoDocumento = "D", version = vigente })).Status);
    }

    [Fact]
    public async Task Delete_MatrizDeVersion_Ausente400_Cero400_Obsoleta409_Vigente200()
    {
        var id = await SeedDocumentoAsync();
        var vigente = (await LeerAsync(id)).Version;

        var ausente = await DeleteAsync(id, null);
        Assert.Equal(HttpStatusCode.BadRequest, ausente.Status);
        Assert.Equal(["La versión del documento es obligatoria."], ausente.Errores);

        var cero = await DeleteAsync(id, 0);
        Assert.Equal(HttpStatusCode.BadRequest, cero.Status);
        Assert.False(cero.Json.GetProperty("success").GetBoolean());
        Assert.Equal(["La versión del documento no es válida."], cero.Errores);

        var obsoleta = await DeleteAsync(id, vigente + 1);
        Assert.Equal(HttpStatusCode.Conflict, obsoleta.Status);
        Assert.Equal(["DOCUMENT_CONCURRENCY_CONFLICT"], obsoleta.Errores);

        Assert.False((await LeerAsync(id)).IsDeleted);
        Assert.Equal(0, await AuditoriasAsync(id));

        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(id, vigente)).Status);
        Assert.True((await LeerAsync(id)).IsDeleted);
    }

    [Fact]
    public async Task Version0_ElExpedienteEliminadoTienePrioridad_404()
    {
        // D73-6: el 404/403 de la autorización va antes que el 400 de versión, también con version=0
        var enBorrado = await SeedDocumentoAsync(expedienteId: _expedienteBorrado);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(enBorrado, 0)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(enBorrado, new { titulo = "T", tipoDocumento = "D", version = 0u })).Status);
    }

    [Fact]
    public async Task Put_Concurrentes_UnoGanaYElOtro409()
    {
        var id = await SeedDocumentoAsync();
        var version = (await LeerAsync(id)).Version;

        var resultados = await Task.WhenAll(Enumerable.Range(1, 6).Select(i =>
            PutAsync(id, new { titulo = $"Paralelo {i}", tipoDocumento = "D", version })));

        Assert.Equal(1, resultados.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(resultados.Where(r => r.Status != HttpStatusCode.OK), r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal(["DOCUMENT_CONCURRENCY_CONFLICT"], r.Errores);
        });
        var ganador = resultados.Single(r => r.Status == HttpStatusCode.OK).Data.GetProperty("titulo").GetString();
        Assert.Equal(ganador, (await LeerAsync(id)).Titulo);
    }

    // ══ DELETE ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_Valido_BorradoLogico_ArchivoYRutaIntactos()
    {
        var contenido = Fase72TestData.Pdf("borrar");
        var id = await SeedDocumentoAsync(contenido: contenido);
        var antes = await LeerAsync(id);

        var r = await DeleteAsync(id, antes.Version);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        var despues = await LeerAsync(id);
        Assert.True(despues.IsDeleted);
        Assert.NotNull(despues.DeletedAt);
        Assert.Equal($"{_seniorId:N}@fase73.com", despues.DeletedBy);
        Assert.Equal(antes.RutaAlmacenamiento, despues.RutaAlmacenamiento);
        Assert.Equal(contenido, await File.ReadAllBytesAsync(Path.Combine(_storageDir, antes.RutaAlmacenamiento)));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{id}", Senior)).Status);
        var segundo = await DeleteAsync(id, despues.Version);
        Assert.Equal(HttpStatusCode.NotFound, segundo.Status);
        Assert.Equal(["DOCUMENT_NOT_FOUND"], segundo.Errores);
    }

    [Fact]
    public async Task Delete_ArchivoFisicoInexistente_200()
    {
        var id = await SeedDocumentoAsync(contenido: null);
        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(id, (await LeerAsync(id)).Version)).Status);
    }

    [Fact]
    public async Task Delete_SinVersion400_Cero400_Obsoleta409_Procesando409()
    {
        var id = await SeedDocumentoAsync();
        var version = (await LeerAsync(id)).Version;

        var sinVersion = await DeleteAsync(id, null);
        Assert.Equal(HttpStatusCode.BadRequest, sinVersion.Status);
        Assert.Equal(["La versión del documento es obligatoria."], sinVersion.Errores);

        var obsoleta = await DeleteAsync(id, version + 1);
        Assert.Equal(HttpStatusCode.Conflict, obsoleta.Status);
        Assert.Equal(["DOCUMENT_CONCURRENCY_CONFLICT"], obsoleta.Errores);

        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteAsync(id, 0)).Status);
        Assert.False((await LeerAsync(id)).IsDeleted);

        var procesando = await SeedDocumentoAsync(estado: EstadoProcesamientoIa.Procesando);
        var r = await DeleteAsync(procesando, (await LeerAsync(procesando)).Version);
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal(["DOCUMENT_PROCESSING"], r.Errores);
        var tras = await LeerAsync(procesando);
        Assert.False(tras.IsDeleted);
        Assert.Equal(EstadoProcesamientoIa.Procesando, tras.EstadoIa);
    }

    [Fact]
    public async Task Delete_InexistenteOExpedienteEliminado_404_AntesQueLaValidacionDeVersion()
    {
        var enBorrado = await SeedDocumentoAsync(expedienteId: _expedienteBorrado);
        foreach (var id in new[] { Guid.NewGuid(), enBorrado })
        {
            var r = await DeleteAsync(id, null);   // sin versión: el 404 tiene prioridad (D73-6)
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.Equal(["DOCUMENT_NOT_FOUND"], r.Errores);
        }
    }

    [Fact]
    public async Task Delete_PbacYTenant()
    {
        var id = await SeedDocumentoAsync();
        var version = (await LeerAsync(id)).Version;

        var otroTenant = await SendAsync(HttpMethod.Delete, $"/api/v1/documentos/{id}?version={version}", Token(_seniorBId, Roles.AbogadoSenior, _tenantB), tenantId: _tenantB);
        Assert.Equal(HttpStatusCode.Forbidden, otroTenant.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], otroTenant.Errores);

        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(id, version, Token(_juniorResponsableId, Roles.AbogadoJunior))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(id, version, Token(_asistenteId, Roles.AsistenteLegal))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(id, version, Token(Guid.NewGuid(), Roles.SuperAdmin))).Status);
        Assert.False((await LeerAsync(id)).IsDeleted);

        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(id, version, Token(_adminId, Roles.AdminEstudio))).Status);
    }

    [Fact]
    public async Task Delete_Concurrentes_NuncaDosExitos_YUnSoloEventoDeAuditoria()
    {
        var id = await SeedDocumentoAsync();
        var version = (await LeerAsync(id)).Version;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => DeleteAsync(id, version)));

        Assert.Equal(1, resultados.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(resultados.Where(r => r.Status != HttpStatusCode.OK),
            r => Assert.Contains(r.Status, new[] { HttpStatusCode.Conflict, HttpStatusCode.NotFound }));
        Assert.True((await LeerAsync(id)).IsDeleted);
        Assert.Equal(1, await AuditoriasAsync(id));
    }

    [Fact]
    public async Task PutYDelete_Concurrentes_UnoGanaSinSobrescritura()
    {
        for (var intento = 0; intento < 5; intento++)
        {
            var id = await SeedDocumentoAsync();
            var version = (await LeerAsync(id)).Version;

            var put = PutAsync(id, new { titulo = "Editado", tipoDocumento = "D", version });
            var delete = DeleteAsync(id, version);
            var (rPut, rDelete) = (await put, await delete);
            var final = await LeerAsync(id);

            Assert.False(rPut.Status == HttpStatusCode.OK && rDelete.Status == HttpStatusCode.OK, "PUT y DELETE no pueden ganar ambos");
            if (rDelete.Status == HttpStatusCode.OK)
            {
                Assert.True(final.IsDeleted);
                Assert.Equal("Demanda", final.Titulo);
                Assert.Contains(rPut.Status, new[] { HttpStatusCode.Conflict, HttpStatusCode.NotFound });
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, rPut.Status);
                Assert.Equal(HttpStatusCode.Conflict, rDelete.Status);
                Assert.False(final.IsDeleted);
                Assert.Equal("Editado", final.Titulo);
            }
        }
    }

    // ══ DESCARGA ══════════════════════════════════════════════════════════

    private Task<Respuesta> DescargarAsync(Guid id, string? token = null) =>
        SendAsync(HttpMethod.Get, $"/api/v1/documentos/{id}/download", token ?? Senior);

    [Fact]
    public async Task Descarga_Valida_BytesYCabecerasDeSeguridad()
    {
        var contenido = Fase72TestData.Pdf("descarga");
        var id = await SeedDocumentoAsync(contenido: contenido, nombreOriginal: "Acción de protección.pdf");

        var r = await DescargarAsync(id);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(contenido, r.Bytes);
        Assert.Equal("application/pdf", r.ContentHeaders!.ContentType!.MediaType);
        Assert.Equal("attachment", r.ContentHeaders.ContentDisposition!.DispositionType);
        Assert.Equal("Acción de protección.pdf", r.ContentHeaders.ContentDisposition.FileNameStar);
        Assert.Equal(["nosniff"], r.Headers.GetValues("X-Content-Type-Options"));
        Assert.True(r.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain(_tenantA.ToString("N"), r.ContentHeaders.ContentDisposition.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Descarga_FilaAntiguaSinNombreOriginal_UsaTituloSaneado()
    {
        var id = await SeedDocumentoAsync(contenido: Fase72TestData.Pdf(), nombreOriginal: null, titulo: "Escrito/final\\v2");
        var r = await DescargarAsync(id);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("Escrito_final_v2.pdf", r.ContentHeaders!.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task Descarga_ArchivoFisicoInexistente_404DocumentFileNotFound()
    {
        var id = await SeedDocumentoAsync(contenido: null);
        var ruta = (await LeerAsync(id)).RutaAlmacenamiento;

        var r = await DescargarAsync(id);

        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.Equal(["DOCUMENT_FILE_NOT_FOUND"], r.Errores);
        Assert.DoesNotContain(ruta, r.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tenantA.ToString("N"), r.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../fuera.pdf")]
    [InlineData(".tmp/robado.upload")]
    [InlineData("tenant/../../fuera.pdf")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    public async Task Descarga_RutaInseguraEnLaBase_403SinCodigoDocumental(string rutaManipulada)
    {
        var id = await SeedDocumentoAsync(ruta: rutaManipulada);
        var r = await DescargarAsync(id);

        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Empty(r.Errores);    // D73-8: problema de integridad del servidor, no denegación al usuario
        Assert.Contains("Path Traversal", r.Json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Descarga_JunctionEnLaCarpetaDelTenant_403()
    {
        var externo = Path.Combine(Path.GetTempPath(), "aj_fase73_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(externo, _expediente.ToString("N")));
        var nombre = Guid.NewGuid().ToString("N") + ".pdf";
        await File.WriteAllBytesAsync(Path.Combine(externo, _expediente.ToString("N"), nombre), Fase72TestData.Pdf("externo"));
        try
        {
            CrearEnlaceDeDirectorio(Path.Combine(_storageDir, _tenantA.ToString("N")), externo);
            var id = await SeedDocumentoAsync(ruta: $"{_tenantA:N}/{_expediente:N}/{nombre}");

            var r = await DescargarAsync(id);

            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.Contains("enlace simbólico o punto de reanálisis", r.Json.GetProperty("message").GetString());
        }
        finally
        {
            try { Directory.Delete(Path.Combine(_storageDir, _tenantA.ToString("N"))); } catch { }
            try { Directory.Delete(externo, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Descarga_DocumentoInexistenteEliminadoOExpedienteEliminado_404()
    {
        var eliminado = await SeedDocumentoAsync(contenido: Fase72TestData.Pdf(), eliminado: true);
        var enBorrado = await SeedDocumentoAsync(contenido: Fase72TestData.Pdf(), expedienteId: _expedienteBorrado);

        foreach (var id in new[] { Guid.NewGuid(), eliminado, enBorrado })
        {
            var r = await DescargarAsync(id);
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.Equal(["DOCUMENT_NOT_FOUND"], r.Errores);
        }
    }

    [Fact]
    public async Task Descarga_PbacYTenant()
    {
        var id = await SeedDocumentoAsync(contenido: Fase72TestData.Pdf());

        var otroTenant = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{id}/download", Token(_seniorBId, Roles.AbogadoSenior, _tenantB), tenantId: _tenantB);
        Assert.Equal(HttpStatusCode.Forbidden, otroTenant.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], otroTenant.Errores);

        Assert.Equal(HttpStatusCode.Forbidden, (await DescargarAsync(id, Token(Guid.NewGuid(), Roles.SuperAdmin))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await DescargarAsync(id, Token(_juniorOtroId, Roles.AbogadoJunior))).Status);

        var asistente = Token(_asistenteId, Roles.AsistenteLegal);
        Assert.Equal(HttpStatusCode.Forbidden, (await DescargarAsync(id, asistente)).Status);
        await AgregarTareaAsistenteAsync(EstadoTarea.Pendiente);
        Assert.Equal(HttpStatusCode.OK, (await DescargarAsync(id, asistente)).Status);
        Assert.Equal(HttpStatusCode.OK, (await DescargarAsync(id, Token(_juniorResponsableId, Roles.AbogadoJunior))).Status);
    }

    private static void CrearEnlaceDeDirectorio(string enlace, string destino)
    {
        try
        {
            Directory.CreateSymbolicLink(enlace, destino);
            return;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
        }

        var proceso = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{enlace}\" \"{destino}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        })!;
        proceso.WaitForExit();
        Assert.True(proceso.ExitCode == 0 && Directory.Exists(enlace), "No se pudo crear el enlace: " + proceso.StandardError.ReadToEnd());
    }

    // ══ AUDITORÍA (ajustada en la 7.4: PUT y descarga auditan; DELETE sin ruta) ══

    [Fact]
    public async Task Auditoria_LecturasNoAuditan_PutYDescargaSi()
    {
        var id = await SeedDocumentoAsync(contenido: Fase72TestData.Pdf());

        Assert.Equal(HttpStatusCode.OK, (await ListarAsync($"expedienteId={_expediente}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{_expediente}", Senior)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{id}", Senior)).Status);
        Assert.Equal(0, await AuditoriasAsync(id));   // listado, alias y detalle no auditan

        Assert.Equal(HttpStatusCode.OK, (await DescargarAsync(id)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(id, new { titulo = "Nuevo", tipoDocumento = "D", version = (await LeerAsync(id)).Version })).Status);

        await using var context = Contexto(_tenantA);
        var acciones = await context.HistorialAuditorias.IgnoreQueryFilters()
            .Where(a => a.TenantId == _tenantA && a.EntidadId == id.ToString())
            .Select(a => a.Accion).OrderBy(a => a).ToListAsync();
        Assert.Equal(["DOWNLOAD", "UPDATE"], acciones);
    }

    [Fact]
    public async Task Auditoria_DeleteRechazado_NoCreaEvento_DeleteConfirmado_SinRutaFisica()
    {
        var id = await SeedDocumentoAsync(titulo: "Escrito auditado");
        var doc = await LeerAsync(id);

        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteAsync(id, null)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(id, doc.Version + 1)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(id, doc.Version, Token(_seniorBId, Roles.AbogadoSenior, _tenantB))).Status);
        var procesando = await SeedDocumentoAsync(estado: EstadoProcesamientoIa.Procesando);
        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(procesando, (await LeerAsync(procesando)).Version)).Status);
        Assert.Equal(0, await AuditoriasAsync(id));
        Assert.Equal(0, await AuditoriasAsync(procesando));

        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(id, doc.Version)).Status);

        await using var context = Contexto(_tenantA);
        var evento = await context.HistorialAuditorias.IgnoreQueryFilters()
            .SingleAsync(a => a.TenantId == _tenantA && a.EntidadId == id.ToString());
        Assert.Equal("Documento", evento.Entidad);
        Assert.Equal("DELETE", evento.Accion);
        Assert.Null(evento.ValoresNuevosJson);

        // Fase 7.4: evento DELETE definitivo, sin rutaAlmacenamiento
        using var anteriores = JsonDocument.Parse(evento.ValoresAnterioresJson!);
        Assert.Equal(["expedienteId", "nombreArchivoOriginal", "sha256Contenido", "titulo"],
            anteriores.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Escrito auditado", anteriores.RootElement.GetProperty("titulo").GetString());
        Assert.Equal(doc.HashSha256, anteriores.RootElement.GetProperty("sha256Contenido").GetString());
        Assert.DoesNotContain(doc.RutaAlmacenamiento, evento.ValoresAnterioresJson!, StringComparison.OrdinalIgnoreCase);
    }
}
