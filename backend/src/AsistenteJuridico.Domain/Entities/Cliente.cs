using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa un cliente del estudio jurídico (persona natural o jurídica patrocinada).
/// </summary>
public class Cliente : AuditableEntity, IMultiTenant, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public TipoIdentificacion TipoIdentificacion { get; set; } = TipoIdentificacion.Cedula;
    public string Identificacion { get; set; } = string.Empty;
    public string NombreRazonSocial { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Telefono { get; set; }
    public string? Direccion { get; set; }
    public string? Notas { get; set; }
    public bool Activo { get; set; } = true;
    public uint Version { get; set; }

    // Soft delete
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    // Colección de expedientes
    public ICollection<Expediente> Expedientes { get; set; } = new List<Expediente>();
}
