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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.1 — API real (pipeline completo, JWT, políticas, middleware) contra PostgreSQL: reglas de acceso
/// documental, DTO sin ruta física, códigos DOCUMENT_* y respuestas de las Fases 0–6 sin cambios.
/// </summary>
public class Fase71DocumentosApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string RutaFisicaSecreta = "tenant-fase71/ruta-interna-no-expuesta.pdf";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly string _tenantSlug = $"fase71-{Guid.NewGuid():N}";
    private readonly Guid _responsableJuniorId = Guid.NewGuid();
    private readonly Guid _otroJuniorId = Guid.NewGuid();
    private readonly Guid _seniorId = Guid.NewGuid();
    private readonly Guid _asistenteId = Guid.NewGuid();

    public Fase71DocumentosApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        using var context = CrearContexto();
        context.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Nombre = "Estudio Fase 7.1",
            IdentificadorUrl = _tenantSlug,
            ZonaHorariaId = "America/Guayaquil",
            Activo = true
        });
        context.SaveChanges();

        foreach (var (id, rol) in new[]
        {
            (_responsableJuniorId, Roles.AbogadoJunior),
            (_otroJuniorId, Roles.AbogadoJunior),
            (_seniorId, Roles.AbogadoSenior),
            (_asistenteId, Roles.AsistenteLegal)
        })
        {
            var email = $"{id:N}@fase71.com";
            context.Usuarios.Add(new Usuario
            {
                Id = id,
                TenantId = _tenantId,
                UserName = email,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(),
                NombreCompleto = "Usuario " + rol,
                Rol = rol,
                Activo = true
            });
        }
        context.SaveChanges();
    }

    private ApplicationDbContext CrearContexto() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(TestConfiguration.PostgresConnectionString).Options,
        new TestTenantService { TenantId = _tenantId });

    private async Task<(Guid ExpedienteId, Guid DocumentoId)> SeedExpedienteConDocumentoAsync(bool expedienteEliminado = false)
    {
        await using var context = CrearContexto();
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8],
            NombreRazonSocial = "Cliente Fase 7.1",
            Activo = true
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ClienteId = cliente.Id,
            NumeroExpediente = "EXP-71-" + Guid.NewGuid().ToString("N")[..8],
            Titulo = "Caso Fase 7.1",
            Materia = "Civil",
            AbogadoResponsableId = _responsableJuniorId,
            Estado = EstadoExpediente.Abierto,
            IsDeleted = expedienteEliminado,
            DeletedAt = expedienteEliminado ? DateTime.UtcNow : null
        };
        var documento = new Documento
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Demanda inicial",
            TipoDocumento = "Demanda",
            Descripcion = "Descripción de prueba",
            NombreArchivoOriginal = "demanda.pdf",
            RutaAlmacenamiento = RutaFisicaSecreta,
            ContentType = "application/pdf",
            TamanioBytes = 1234,
            HashSha256 = new string('a', 64),
            EstadoIa = EstadoProcesamientoIa.Pendiente
        };
        context.AddRange(cliente, expediente, documento);
        await context.SaveChangesAsync();
        return (expediente.Id, documento.Id);
    }

    private async Task AgregarTareaAsistenteAsync(Guid expedienteId, EstadoTarea estado)
    {
        await using var context = CrearContexto();
        context.Tareas.Add(new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ExpedienteId = expedienteId,
            AsignadoAUsuarioId = _asistenteId,
            Titulo = "Tarea " + estado,
            Estado = estado,
            Prioridad = Prioridad.Media,
            FechaVencimiento = DateTime.UtcNow.AddDays(3)
        });
        await context.SaveChangesAsync();
    }

    private string Token(Guid userId, string rol)
    {
        var tokenService = new TokenService(_factory.Services.GetRequiredService<IConfiguration>());
        var tenant = new Tenant { Id = _tenantId, IdentificadorUrl = _tenantSlug, Nombre = "Estudio Fase 7.1" };
        var user = new Usuario { Id = userId, Email = $"{userId:N}@fase71.com", NombreCompleto = "Usuario " + rol, Rol = rol, TenantId = _tenantId };
        var (token, _) = tokenService.GenerateAccessToken(user, tenant, rol, Permissions.GetPermissionsForRole(rol).ToList());
        return token;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Slug", _tenantSlug);
        request.Headers.Add("X-Tenant-ID", _tenantId.ToString());
        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string[] Errores(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static void AssertSinRutaFisica(string body)
    {
        Assert.DoesNotContain("rutaAlmacenamiento", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RutaFisicaSecreta, body, StringComparison.OrdinalIgnoreCase);
    }

    // ── DTO sin ruta física ──────────────────────────────────────────────

    [Fact]
    public async Task DetalleYAlias_NuncaExponenRutaAlmacenamiento()
    {
        var (expedienteId, documentoId) = await SeedExpedienteConDocumentoAsync();
        var token = Token(_seniorId, Roles.AbogadoSenior);

        var detalle = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{documentoId}", token);
        Assert.Equal(HttpStatusCode.OK, detalle.Status);
        AssertSinRutaFisica(detalle.Body);
        var data = JsonDocument.Parse(detalle.Body).RootElement.GetProperty("data");
        Assert.Equal(expedienteId, data.GetProperty("expedienteId").GetGuid());
        Assert.Equal("demanda.pdf", data.GetProperty("nombreArchivoOriginal").GetString());
        Assert.Equal("Descripción de prueba", data.GetProperty("descripcion").GetString());

        var alias = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{expedienteId}", token);
        Assert.Equal(HttpStatusCode.OK, alias.Status);
        AssertSinRutaFisica(alias.Body);
        Assert.Single(JsonDocument.Parse(alias.Body).RootElement.GetProperty("data").EnumerateArray());
    }

    // ── Expediente eliminado e inexistente ───────────────────────────────

    [Fact]
    public async Task ExpedienteEliminado_Documento404ConCodigo()
    {
        var (expedienteId, documentoId) = await SeedExpedienteConDocumentoAsync(expedienteEliminado: true);
        var token = Token(_seniorId, Roles.AbogadoSenior);

        foreach (var url in new[] { $"/api/v1/documentos/{documentoId}", $"/api/v1/documentos/{documentoId}/download" })
        {
            var (status, body) = await SendAsync(HttpMethod.Get, url, token);
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal(["DOCUMENT_NOT_FOUND"], Errores(body));
        }

        var borrado = await SendAsync(HttpMethod.Delete, $"/api/v1/documentos/{documentoId}", token);
        Assert.Equal(HttpStatusCode.NotFound, borrado.Status);
        Assert.Equal(["DOCUMENT_NOT_FOUND"], Errores(borrado.Body));

        // El listado del expediente eliminado es un 404 de expediente: sin código documental
        var alias = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{expedienteId}", token);
        Assert.Equal(HttpStatusCode.NotFound, alias.Status);
        Assert.Empty(Errores(alias.Body));

        // El documento sigue sin borrar en la base: el 404 no lo modificó
        await using var context = CrearContexto();
        Assert.False((await context.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == documentoId)).IsDeleted);
    }

    [Fact]
    public async Task DocumentoInexistente_404ConCodigo()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{Guid.NewGuid()}", Token(_seniorId, Roles.AbogadoSenior));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(["DOCUMENT_NOT_FOUND"], Errores(body));
    }

    // ── Reglas por rol ───────────────────────────────────────────────────

    [Fact]
    public async Task SuperAdmin_SinAccesoDocumental()
    {
        var (expedienteId, documentoId) = await SeedExpedienteConDocumentoAsync();
        var token = Token(Guid.NewGuid(), Roles.SuperAdmin);

        foreach (var url in new[]
        {
            $"/api/v1/documentos/{documentoId}",
            $"/api/v1/documentos/{documentoId}/download",
            $"/api/v1/documentos/expediente/{expedienteId}"
        })
        {
            var (status, _) = await SendAsync(HttpMethod.Get, url, token);
            Assert.Equal(HttpStatusCode.Forbidden, status);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/api/v1/documentos/{documentoId}", token)).Status);
    }

    [Fact]
    public async Task Junior_SoloEnExpedienteDelQueEsResponsable()
    {
        var (expedienteId, documentoId) = await SeedExpedienteConDocumentoAsync();

        var ajeno = Token(_otroJuniorId, Roles.AbogadoJunior);
        var detalleAjeno = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{documentoId}", ajeno);
        Assert.Equal(HttpStatusCode.Forbidden, detalleAjeno.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], Errores(detalleAjeno.Body));
        var listadoAjeno = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{expedienteId}", ajeno);
        Assert.Equal(HttpStatusCode.Forbidden, listadoAjeno.Status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], Errores(listadoAjeno.Body));

        var responsable = Token(_responsableJuniorId, Roles.AbogadoJunior);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{documentoId}", responsable)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{expedienteId}", responsable)).Status);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(EstadoTarea.Pendiente, true)]
    [InlineData(EstadoTarea.EnProgreso, true)]
    [InlineData(EstadoTarea.Completada, false)]
    [InlineData(EstadoTarea.Cancelada, false)]
    public async Task AsistenteLegal_DetalleListadoYDescarga_SoloConTareaVigente(EstadoTarea? estado, bool permitido)
    {
        var (expedienteId, documentoId) = await SeedExpedienteConDocumentoAsync();
        if (estado.HasValue)
        {
            await AgregarTareaAsistenteAsync(expedienteId, estado.Value);
        }

        var token = Token(_asistenteId, Roles.AsistenteLegal);
        var detalle = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{documentoId}", token);
        var listado = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/expediente/{expedienteId}", token);

        if (permitido)
        {
            Assert.Equal(HttpStatusCode.OK, detalle.Status);
            Assert.Equal(HttpStatusCode.OK, listado.Status);
            AssertSinRutaFisica(detalle.Body);
            AssertSinRutaFisica(listado.Body);
        }
        else
        {
            // Antes de la Fase 7 el listado no aplicaba esta regla (D-1)
            Assert.Equal(HttpStatusCode.Forbidden, detalle.Status);
            Assert.Equal(["DOCUMENT_ACCESS_DENIED"], Errores(detalle.Body));
            Assert.Equal(HttpStatusCode.Forbidden, listado.Status);
            Assert.Equal(["DOCUMENT_ACCESS_DENIED"], Errores(listado.Body));

            // La autorización se resuelve antes de abrir el archivo
            var descarga = await SendAsync(HttpMethod.Get, $"/api/v1/documentos/{documentoId}/download", token);
            Assert.Equal(HttpStatusCode.Forbidden, descarga.Status);
            Assert.Equal(["DOCUMENT_ACCESS_DENIED"], Errores(descarga.Body));
        }
    }

    [Fact]
    public async Task AsistenteLegal_NoPuedeSubirNiConTareaVigente()
    {
        var (expedienteId, _) = await SeedExpedienteConDocumentoAsync();
        await AgregarTareaAsistenteAsync(expedienteId, EstadoTarea.EnProgreso);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(expedienteId.ToString()), "expedienteId" },
            { new StringContent("Escrito"), "titulo" },
            { new StringContent("Escrito"), "tipoDocumento" }
        };
        var archivo = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n%prueba\n"));
        archivo.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(archivo, "file", "escrito.pdf");

        var (status, _) = await SendAsync(HttpMethod.Post, "/api/v1/documentos/upload", Token(_asistenteId, Roles.AsistenteLegal), form);
        Assert.Equal(HttpStatusCode.Forbidden, status);

        await using var context = CrearContexto();
        Assert.Equal(1, await context.Documentos.CountAsync(d => d.ExpedienteId == expedienteId));
    }

    [Fact]
    public async Task PoliticaDocumentosUpdate_RegistradaPorReflexion()
    {
        var provider = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var politica = await provider.GetPolicyAsync(Permissions.DocumentosUpdate);

        Assert.NotNull(politica);
        var requisito = Assert.Single(politica!.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement>());
        Assert.Equal("permission", requisito.ClaimType);
        Assert.Equal([Permissions.DocumentosUpdate], requisito.AllowedValues);
    }

    // ── ErrorCode aditivo: las respuestas de las Fases 0–6 no cambian ────

    [Fact]
    public async Task RespuestasExistentes_SinCodigo_MantienenErrorsVacio()
    {
        var senior = Token(_seniorId, Roles.AbogadoSenior);

        foreach (var url in new[]
        {
            $"/api/v1/expedientes/{Guid.NewGuid()}",
            $"/api/v1/tareas/{Guid.NewGuid()}",
            $"/api/v1/clientes/{Guid.NewGuid()}"
        })
        {
            var (status, body) = await SendAsync(HttpMethod.Get, url, senior);
            Assert.Equal(HttpStatusCode.NotFound, status);
            var raiz = JsonDocument.Parse(body).RootElement;
            Assert.False(raiz.GetProperty("success").GetBoolean());
            Assert.Empty(Errores(body));
            Assert.Equal(
                ["data", "errors", "message", "success", "timestamp"],
                raiz.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        }

        // 403 de dominio (ForbiddenException por el middleware): Junior en un expediente del que no es responsable.
        // (El 403 de una política PBAC no pasa por el middleware y nunca tuvo envelope; no cambia.)
        var (expedienteId, _) = await SeedExpedienteConDocumentoAsync();
        var junior = await SendAsync(HttpMethod.Get, $"/api/v1/expedientes/{expedienteId}", Token(_otroJuniorId, Roles.AbogadoJunior));
        Assert.Equal(HttpStatusCode.Forbidden, junior.Status);
        Assert.Empty(Errores(junior.Body));
    }

    [Fact]
    public async Task EndpointDeIA_NoEmiteCodigosDocumentales_YHeredaElEndurecimiento()
    {
        var senior = Token(_seniorId, Roles.AbogadoSenior);

        // Documento inexistente: 404 de la IA sin código, igual que en la Fase 6
        var inexistente = await SendAsync(HttpMethod.Post, "/api/v1/ai/resumir-documento", senior,
            new StringContent(JsonSerializer.Serialize(new { documentoId = Guid.NewGuid() }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.Status);
        Assert.Empty(Errores(inexistente.Body));

        // X3: documento de un expediente eliminado ahora es 404 también para la IA, sin cambiar su contrato
        var (_, documentoId) = await SeedExpedienteConDocumentoAsync(expedienteEliminado: true);
        var eliminado = await SendAsync(HttpMethod.Post, "/api/v1/ai/resumir-documento", senior,
            new StringContent(JsonSerializer.Serialize(new { documentoId }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, eliminado.Status);
        Assert.Empty(Errores(eliminado.Body));
    }
}
