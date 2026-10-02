using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Integration;

public class Fase4ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly string _tenantSlug = $"fase4-{Guid.NewGuid():N}";

    public Fase4ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Asegurar que el Tenant existe en la base de datos
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Nombre = "Estudio Jurídico Fase 4 Test",
            IdentificadorUrl = _tenantSlug,
            Ruc = "1790016919001",
            Activo = true
        });
        context.SaveChanges();
    }

    private string GenerateJwt(Guid userId, string role, IEnumerable<string>? permissions = null)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!db.Usuarios.Any(u => u.Id == userId))
            {
                db.Usuarios.Add(new Usuario
                {
                    Id = userId,
                    TenantId = _tenantId,
                    UserName = $"usuario_{role.ToLower()}_{userId:N}@fase4.com",
                    Email = $"usuario_{role.ToLower()}_{userId:N}@fase4.com",
                    NombreCompleto = $"Usuario {role}",
                    Rol = role,
                    Activo = true
                });
                db.SaveChanges();
            }
        }

        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var tokenService = new TokenService(config);

        var tenant = new Tenant
        {
            Id = _tenantId,
            IdentificadorUrl = _tenantSlug,
            Nombre = "Estudio Jurídico Fase 4"
        };

        var user = new Usuario
        {
            Id = userId,
            Email = $"usuario_{role.ToLower()}@fase4.com",
            NombreCompleto = $"Usuario {role}",
            Rol = role,
            TenantId = _tenantId
        };

        var userPerms = (permissions ?? Permissions.GetPermissionsForRole(role)).ToList();
        var (token, _) = tokenService.GenerateAccessToken(user, tenant, role, userPerms);
        return token;
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Tenant-Slug", _tenantSlug);
        req.Headers.Add("X-Tenant-ID", _tenantId.ToString());
        return req;
    }

    [Fact]
    public async Task SuperAdmin_AccesoADatosJuridicos_Retorna403Forbidden()
    {
        // Arrange: Token para SuperAdmin
        var superAdminToken = GenerateJwt(Guid.NewGuid(), Roles.SuperAdmin);

        // Act: Intentar consultar expedientes
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/v1/expedientes", superAdminToken);
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FlujoCompletoFase4_ClienteExpedienteTareaAudienciaDocumento_FuncionaCorrectamente()
    {
        // 1. Token Abogado Senior
        var seniorUserId = Guid.NewGuid();
        var seniorToken = GenerateJwt(seniorUserId, Roles.AbogadoSenior);

        // 2. Crear Cliente con Cédula válida ecuatoriana
        var clientePayload = new
        {
            tipoIdentificacion = (int)TipoIdentificacion.Cedula,
            identificacion = "1710034065",
            nombreRazonSocial = "Juan Carlos Pérez Echeverría",
            email = "juan.perez@empresa.com",
            telefono = "0991234567",
            direccion = "Av. Amazonas y Colón",
            notas = "Cliente persona natural"
        };

        var clienteReq = CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/clientes", seniorToken);
        clienteReq.Content = new StringContent(JsonSerializer.Serialize(clientePayload), Encoding.UTF8, "application/json");
        var clienteResp = await _client.SendAsync(clienteReq);

        Assert.Equal(HttpStatusCode.Created, clienteResp.StatusCode);
        var clienteDoc = JsonDocument.Parse(await clienteResp.Content.ReadAsStringAsync());
        var clienteId = clienteDoc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        Assert.NotNull(clienteId);

        // 3. Crear Expediente
        var expedientePayload = new
        {
            titulo = "Demanda Ordinaria Laboral por Despido Intempestivo",
            descripcion = "Caso laboral contra empresa demandada",
            materia = "Laboral",
            prioridad = (int)Prioridad.Alta,
            clienteId = Guid.Parse(clienteId),
            abogadoResponsableId = seniorUserId,
            observaciones = "Revisar liquidación actuarial"
        };

        var expReq = CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/expedientes", seniorToken);
        expReq.Content = new StringContent(JsonSerializer.Serialize(expedientePayload), Encoding.UTF8, "application/json");
        var expResp = await _client.SendAsync(expReq);

        Assert.Equal(HttpStatusCode.Created, expResp.StatusCode);
        var expDoc = JsonDocument.Parse(await expResp.Content.ReadAsStringAsync());
        var expData = expDoc.RootElement.GetProperty("data");
        var expedienteId = expData.GetProperty("id").GetString()!;
        var numeroExpediente = expData.GetProperty("numeroExpediente").GetString()!;
        var expedienteVersion = expData.GetProperty("version").GetInt64();

        // Validar formato correlativo EXP-{YYYY}-{NNNN}
        Assert.Matches(@"^EXP-\d{4}-\d{4}$", numeroExpediente);

        // 4. Crear Tarea asociada al expediente
        var tareaPayload = new
        {
            expedienteId = Guid.Parse(expedienteId),
            titulo = "Elaborar contestación a la demanda",
            descripcion = "Redactar fundamentos de hecho y derecho",
            fechaVencimiento = DateTime.UtcNow.AddDays(5),
            prioridad = (int)Prioridad.Alta,
            asignadoAUsuarioId = seniorUserId
        };

        var tareaReq = CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/tareas", seniorToken);
        tareaReq.Content = new StringContent(JsonSerializer.Serialize(tareaPayload), Encoding.UTF8, "application/json");
        var tareaResp = await _client.SendAsync(tareaReq);

        Assert.Equal(HttpStatusCode.Created, tareaResp.StatusCode);
        var tareaDoc = JsonDocument.Parse(await tareaResp.Content.ReadAsStringAsync());
        var tareaId = tareaDoc.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        // 5. Crear Audiencia
        var audienciaPayload = new
        {
            expedienteId = Guid.Parse(expedienteId),
            fechaHora = DateTime.UtcNow.AddDays(15),
            salaOVirtual = "Sala 204 - Complejo Judicial Norte",
            tipoAudiencia = (int)TipoAudiencia.Preliminar,
            notas = "Audiencia preliminar de prueba"
        };

        var audReq = CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/audiencias", seniorToken);
        audReq.Content = new StringContent(JsonSerializer.Serialize(audienciaPayload), Encoding.UTF8, "application/json");
        var audResp = await _client.SendAsync(audReq);

        Assert.Equal(HttpStatusCode.Created, audResp.StatusCode);

        // 6. Subir Documento PDF con Magic Bytes reales
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");
        using var multiContent = new MultipartFormDataContent();
        multiContent.Add(new StringContent(expedienteId), "expedienteId");
        multiContent.Add(new StringContent("Demanda Inicial Foliada"), "titulo");
        multiContent.Add(new StringContent("Demanda"), "tipoDocumento");
        multiContent.Add(new ByteArrayContent(pdfBytes)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") }
        }, "file", "demanda_foliada.pdf");

        var docReq = CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/documentos/upload", seniorToken);
        docReq.Content = multiContent;
        var docResp = await _client.SendAsync(docReq);

        Assert.Equal(HttpStatusCode.Created, docResp.StatusCode);
        var docResult = JsonDocument.Parse(await docResp.Content.ReadAsStringAsync());
        var docId = docResult.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        // 7. Descargar Documento y verificar headers de seguridad (nosniff, attachment)
        var downloadReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/documentos/{docId}/download", seniorToken);
        var downloadResp = await _client.SendAsync(downloadReq);

        Assert.Equal(HttpStatusCode.OK, downloadResp.StatusCode);
        Assert.Equal("application/pdf", downloadResp.Content.Headers.ContentType?.MediaType);
        Assert.True(downloadResp.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", downloadResp.Headers.GetValues("X-Content-Type-Options").First());
        Assert.Contains("attachment", downloadResp.Content.Headers.ContentDisposition?.DispositionType);

        // 8. Intentar cierre normal mientras la tarea está pendiente -> Debe retornar 422 BusinessRuleException
        // Primero avanzar a EnTramite
        var pasarTramitePayload = new
        {
            nuevoEstado = (int)EstadoExpediente.EnTramite,
            confirmarCierreConTareasPendientes = false,
            version = expedienteVersion
        };
        var tramiteReq = CreateAuthenticatedRequest(HttpMethod.Patch, $"/api/v1/expedientes/{expedienteId}/estado", seniorToken);
        tramiteReq.Content = new StringContent(JsonSerializer.Serialize(pasarTramitePayload), Encoding.UTF8, "application/json");
        var tramiteResp = await _client.SendAsync(tramiteReq);
        Assert.Equal(HttpStatusCode.OK, tramiteResp.StatusCode);
        var tramiteDoc = JsonDocument.Parse(await tramiteResp.Content.ReadAsStringAsync());
        expedienteVersion = tramiteDoc.RootElement.GetProperty("data").GetProperty("version").GetInt64();

        // Intento de cierre normal con tarea activa
        var cierreNormalPayload = new
        {
            nuevoEstado = (int)EstadoExpediente.Cerrado,
            confirmarCierreConTareasPendientes = false,
            version = expedienteVersion
        };
        var cierreReq = CreateAuthenticatedRequest(HttpMethod.Patch, $"/api/v1/expedientes/{expedienteId}/estado", seniorToken);
        cierreReq.Content = new StringContent(JsonSerializer.Serialize(cierreNormalPayload), Encoding.UTF8, "application/json");
        var cierreResp = await _client.SendAsync(cierreReq);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, cierreResp.StatusCode);

        // 9. Cierre forzado con permiso Expedientes.CloseForce y motivo justificado -> Exitoso
        var cierreForzadoPayload = new
        {
            nuevoEstado = (int)EstadoExpediente.Cerrado,
            confirmarCierreConTareasPendientes = true,
            motivoCierreForzado = "Transacción judicial integral acordada en audiencia",
            version = expedienteVersion
        };
        var forzadoReq = CreateAuthenticatedRequest(HttpMethod.Patch, $"/api/v1/expedientes/{expedienteId}/estado", seniorToken);
        forzadoReq.Content = new StringContent(JsonSerializer.Serialize(cierreForzadoPayload), Encoding.UTF8, "application/json");
        var forzadoResp = await _client.SendAsync(forzadoReq);

        Assert.Equal(HttpStatusCode.OK, forzadoResp.StatusCode);
        var forzadoDoc = JsonDocument.Parse(await forzadoResp.Content.ReadAsStringAsync());
        Assert.Equal((int)EstadoExpediente.Cerrado, forzadoDoc.RootElement.GetProperty("data").GetProperty("estado").GetInt32());
        Assert.Equal("Cerrado", forzadoDoc.RootElement.GetProperty("data").GetProperty("estadoDescripcion").GetString());

        // 10. Verificar que la tarea pendiente fue cancelada automáticamente por el cierre forzado
        var getTareaReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/tareas/{tareaId}", seniorToken);
        var getTareaResp = await _client.SendAsync(getTareaReq);
        Assert.Equal(HttpStatusCode.OK, getTareaResp.StatusCode);
        var getTareaDoc = JsonDocument.Parse(await getTareaResp.Content.ReadAsStringAsync());
        Assert.Equal((int)EstadoTarea.Cancelada, getTareaDoc.RootElement.GetProperty("data").GetProperty("estado").GetInt32());
        Assert.Equal("Cancelada", getTareaDoc.RootElement.GetProperty("data").GetProperty("estadoDescripcion").GetString());
    }
}
