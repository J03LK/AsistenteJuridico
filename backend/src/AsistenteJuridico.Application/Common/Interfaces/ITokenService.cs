using AsistenteJuridico.Domain.Entities;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface ITokenService
{
    (string Token, int ExpiresIn) GenerateAccessToken(Usuario user, Tenant tenant, string role, IReadOnlyList<string> permissions);
    (string RawToken, RefreshToken Entity) GenerateRefreshToken(Guid userId, Guid tenantId, string securityStamp, string? ipAddress, Guid? familyId = null);
    string HashToken(string token);
}
