using AsistenteJuridico.Domain.Common;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Bitácora inmutable de auditoría para registro de cambios, trazabilidad y seguridad legal.
/// </summary>
public class HistorialAuditoria : BaseEntity, IMultiTenant
{
    public Guid TenantId { get; set; }

    public string Entidad { get; set; } = string.Empty;
    public string EntidadId { get; set; } = string.Empty;
    public string Accion { get; set; } = string.Empty; // Create, Update, Delete

    public string? ValoresAnterioresJson { get; set; }
    public string? ValoresNuevosJson { get; set; }

    public Guid? UsuarioId { get; set; }
    public string? UsuarioEmail { get; set; }
    public string? IpAddress { get; set; }
    public DateTime Fecha { get; set; } = DateTime.UtcNow;
}
