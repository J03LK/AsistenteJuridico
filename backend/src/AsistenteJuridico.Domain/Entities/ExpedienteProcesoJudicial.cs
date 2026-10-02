using AsistenteJuridico.Domain.Common;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Entidad de unión para la relación muchos a muchos entre Expediente interno y ProcesoJudicial externo.
/// Permite que un expediente interno vincule múltiples causas judiciales (e.g. juicio principal + incidentes + apelación),
/// o que una causa judicial se relacione con expedientes corporativos asociados.
/// </summary>
public class ExpedienteProcesoJudicial : BaseEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid ExpedienteId { get; set; }
    public Expediente Expediente { get; set; } = null!;

    public Guid ProcesoJudicialId { get; set; }
    public ProcesoJudicial ProcesoJudicial { get; set; } = null!;

    /// <summary>
    /// Indica si esta causa es la principal del expediente interno.
    /// </summary>
    public bool EsPrincipal { get; set; } = true;

    public DateTime FechaVinculacion { get; set; } = DateTime.UtcNow;
    public string? Observaciones { get; set; }
}
