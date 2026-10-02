using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace AsistenteJuridico.Infrastructure.Services;

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public (string Token, int ExpiresIn) GenerateAccessToken(Usuario user, Tenant tenant, string role, IReadOnlyList<string> permissions)
    {
        var secretKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("La clave secreta 'Jwt:Key' no está configurada.");

        var issuer = _configuration["Jwt:Issuer"] ?? "AsistenteJuridicoIA";
        var audience = _configuration["Jwt:Audience"] ?? "AsistenteJuridicoClients";
        var expiresInMinutes = int.TryParse(_configuration["Jwt:ExpiresInMinutes"], out var mins) ? mins : 15;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, user.NombreCompleto),
            new(ClaimTypes.Role, role),
            new("tenant_id", tenant.Id.ToString()),
            new("tenant_slug", tenant.IdentificadorUrl)
        };

        // Agregar claims granulares de permisos
        foreach (var permission in permissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(expiresInMinutes);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);

        return (tokenString, expiresInMinutes * 60);
    }

    public (string RawToken, RefreshToken Entity) GenerateRefreshToken(Guid userId, Guid tenantId, string securityStamp, string? ipAddress, Guid? familyId = null)
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);

        var rawToken = Convert.ToBase64String(randomBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

        var tokenHash = HashToken(rawToken);
        var family = familyId ?? Guid.NewGuid();

        var refreshTokenEntity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = tokenHash,
            UserId = userId,
            TenantId = tenantId,
            FamilyId = family,
            SecurityStamp = securityStamp,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            CreatedByIp = ipAddress,
            IsRevoked = false
        };

        return (rawToken, refreshTokenEntity);
    }

    public string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
