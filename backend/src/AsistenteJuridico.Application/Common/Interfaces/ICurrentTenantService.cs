namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Servicio para resolver el identificador de la organización/tenant en el contexto de la petición actual.
/// </summary>
public interface ICurrentTenantService
{
    Guid? TenantId { get; }
    void SetTenantId(Guid tenantId);
}
