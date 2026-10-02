using AsistenteJuridico.Application.Common.Interfaces;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Implementación de ICurrentTenantService con resolución estricta por petición HTTP.
/// Si no se ha establecido un TenantId válido, retorna null para activar el aislamiento estricto.
/// </summary>
public class CurrentTenantService : ICurrentTenantService
{
    public static readonly Guid DefaultDevTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private Guid? _tenantId;

    public Guid? TenantId => _tenantId;

    public void SetTenantId(Guid tenantId)
    {
        _tenantId = tenantId;
    }
}
