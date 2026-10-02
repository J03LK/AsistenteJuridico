using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace AsistenteJuridico.Domain.Tests.Security;

public class TokenServiceTests
{
    private readonly TokenService _tokenService;
    private readonly IConfiguration _configuration;

    public TokenServiceTests()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "Jwt:Key", "ClaveSecretaDePruebasUnitariasMinimo256BitsParaHmacSha256!12345" },
            { "Jwt:Issuer", "AsistenteJuridicoTest" },
            { "Jwt:Audience", "AsistenteJuridicoClientsTest" },
            { "Jwt:ExpiresInMinutes", "15" }
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        _tokenService = new TokenService(_configuration);
    }

    [Fact]
    public void GenerateAccessToken_ShouldContainRequiredClaims_WhenValidParametersProvided()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var tenant = new Tenant
        {
            Id = tenantId,
            IdentificadorUrl = "estudio-test",
            Nombre = "Estudio Jurídico Test"
        };

        var user = new Usuario
        {
            Id = userId,
            Email = "abogado@estudio-test.ec",
            NombreCompleto = "Dr. Pedro Gómez",
            Rol = Roles.AbogadoSenior,
            TenantId = tenantId
        };

        var permissions = Permissions.GetPermissionsForRole(Roles.AbogadoSenior);

        // Act
        var (tokenString, expiresIn) = _tokenService.GenerateAccessToken(user, tenant, user.Rol, permissions);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(tokenString));
        Assert.Equal(15 * 60, expiresIn);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        Assert.Equal("AsistenteJuridicoTest", jwt.Issuer);
        Assert.Contains("AsistenteJuridicoClientsTest", jwt.Audiences);

        // Verificar claims obligatorios
        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("abogado@estudio-test.ec", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal(tenantId.ToString(), jwt.Claims.First(c => c.Type == "tenant_id").Value);
        Assert.Equal("estudio-test", jwt.Claims.First(c => c.Type == "tenant_slug").Value);
        Assert.Equal(Roles.AbogadoSenior, jwt.Claims.First(c => c.Type == ClaimTypes.Role || c.Type == "role").Value);
        Assert.Equal("Dr. Pedro Gómez", jwt.Claims.First(c => c.Type == ClaimTypes.Name || c.Type == "unique_name" || c.Type == "name").Value);

        // Verificar presencia de claims de permisos
        var permissionClaims = jwt.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList();
        Assert.Equal(permissions.Count, permissionClaims.Count);
        Assert.Contains(Permissions.ExpedientesCreate, permissionClaims);
        Assert.Contains(Permissions.ClientesRead, permissionClaims);
    }

    [Fact]
    public void GenerateAccessToken_ShouldHaveExact15MinutesLifetime()
    {
        // Arrange
        var tenant = new Tenant { Id = Guid.NewGuid(), IdentificadorUrl = "slug", Nombre = "Estudio" };
        var user = new Usuario { Id = Guid.NewGuid(), Email = "user@test.ec", NombreCompleto = "Test", Rol = Roles.AbogadoJunior };
        var permissions = Permissions.GetPermissionsForRole(Roles.AbogadoJunior);

        // Act
        var (tokenString, _) = _tokenService.GenerateAccessToken(user, tenant, user.Rol, permissions);
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        // Assert
        var duration = jwt.ValidTo - jwt.ValidFrom;
        Assert.Equal(TimeSpan.FromMinutes(15), duration);
    }

    [Fact]
    public void GenerateRefreshToken_ShouldProduceCryptographicallySecureTokenWithValidHash()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var securityStamp = Guid.NewGuid().ToString();
        var ipAddress = "192.168.1.100";

        // Act
        var (rawToken, entity) = _tokenService.GenerateRefreshToken(userId, tenantId, securityStamp, ipAddress);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(rawToken));
        Assert.NotNull(entity);
        Assert.Equal(userId, entity.UserId);
        Assert.Equal(tenantId, entity.TenantId);
        Assert.Equal(securityStamp, entity.SecurityStamp);
        Assert.Equal(ipAddress, entity.CreatedByIp);
        Assert.False(entity.IsRevoked);
        Assert.True(entity.ExpiresAt > DateTime.UtcNow.AddDays(6));

        // El hash debe coincidir con el hash del token en crudo
        var computedHash = _tokenService.HashToken(rawToken);
        Assert.Equal(computedHash, entity.TokenHash);
    }

    [Fact]
    public void HashToken_ShouldBeDeterministicAndConsistent()
    {
        // Arrange
        const string token = "SampleSecureRandomTokenString123456789";

        // Act
        var hash1 = _tokenService.HashToken(token);
        var hash2 = _tokenService.HashToken(token);

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 en hexadecimal es de 64 caracteres
    }
}
