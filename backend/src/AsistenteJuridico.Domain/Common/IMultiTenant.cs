namespace AsistenteJuridico.Domain.Common;

/// <summary>
/// Interfaz para entidades que pertenecen a una organización/tenant específico.
/// </summary>
public interface IMultiTenant
{
    public Guid TenantId { get; set; }
}
