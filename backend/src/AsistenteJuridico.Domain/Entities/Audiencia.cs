using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa una audiencia judicial (preliminar, de juicio, conciliación, etc.).
/// Puede vincularse al expediente interno y directamente a la causa judicial externa.
/// </summary>
public class Audiencia : AuditableEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid ExpedienteId { get; set; }
    public Expediente Expediente { get; set; } = null!;

    public Guid? ProcesoJudicialId { get; set; }
    public ProcesoJudicial? ProcesoJudicial { get; set; }

    public DateTime FechaHora { get; set; }
    public string SalaOVirtual { get; set; } = "Sala de Audiencias";
    public TipoAudiencia TipoAudiencia { get; set; } = TipoAudiencia.Preliminar;
    public EstadoAudiencia Estado { get; set; } = EstadoAudiencia.Programada;
    public string? Notas { get; set; }
    public uint Version { get; set; }
}
