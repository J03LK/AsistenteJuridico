namespace AsistenteJuridico.Application.Common.Interfaces;

public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? Email { get; }
    Guid? TenantId { get; }
    string? Role { get; }
    bool IsAuthenticated { get; }
    bool HasPermission(string permission);
}
