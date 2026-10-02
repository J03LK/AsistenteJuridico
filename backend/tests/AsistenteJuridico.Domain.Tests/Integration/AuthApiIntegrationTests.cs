using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AsistenteJuridico.Domain.Tests.Integration;

public class AuthApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AuthApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private string GenerateValidJwt(Guid userId, Guid tenantId, string tenantSlug, string role)
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var tokenService = new TokenService(config);

        var tenant = new Tenant
        {
            Id = tenantId,
            IdentificadorUrl = tenantSlug,
            Nombre = "Estudio Demo"
        };

        var user = new Usuario
        {
            Id = userId,
            Email = "abogado@test.ec",
            NombreCompleto = "Abogado Test",
            Rol = role,
            TenantId = tenantId
        };

        var permissions = Permissions.GetPermissionsForRole(role);
        var (token, _) = tokenService.GenerateAccessToken(user, tenant, role, permissions);
        return token;
    }

    [Fact]
    public async Task EndpointProtegido_SinToken_Retorna401Unauthorized()
    {
        // Act: Llamar a /api/v1/auth/me sin header Authorization
        var response = await _client.GetAsync("/api/v1/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EndpointProtegido_ConTokenInvalido_Retorna401Unauthorized()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token.invalido.malformado123");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PeticionAutenticada_HeaderTenantDiferenteAlJwt_Retorna403Forbidden()
    {
        // Arrange: Token válido para Tenant A
        var userId = Guid.NewGuid();
        var tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var token = GenerateValidJwt(userId, tenantA, "demo-estudio", Roles.AbogadoSenior);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Intento malicioso de inyectar cabecera de Tenant B
        var tenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        request.Headers.Add("X-Tenant-ID", tenantB.ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert: El middleware debe interceptar y rechazar con 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var message = doc.RootElement.GetProperty("message").GetString();
        Assert.Contains("seguridad", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PeticionAutenticada_HeaderTenantSlugDiferenteAlJwt_Retorna403Forbidden()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var token = GenerateValidJwt(userId, tenantA, "demo-estudio", Roles.AbogadoSenior);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Slug", "otro-estudio-ajeno");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var message = doc.RootElement.GetProperty("message").GetString();
        Assert.Contains("seguridad", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForgotPassword_SiempreRetorna200_AntiEnumeracion()
    {
        // Arrange
        var payload = JsonSerializer.Serialize(new
        {
            email = "usuario.inexistente999@noexiste.ec",
            tenantSlug = "demo-estudio"
        });

        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/auth/forgot-password", content);

        // Assert: Siempre responde 200 OK con mensaje neutro
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CsrfTokenEndpoint_RetornaCookieXSRF()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/auth/csrf-token");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Set-Cookie"));

        var setCookieHeader = string.Join(";", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("XSRF-TOKEN", setCookieHeader);
    }
}
