namespace AsistenteJuridico.Domain.Common;

/// <summary>
/// Contrato para entidades que registran metadatos de auditoría temporal y autoría.
/// </summary>
public interface IAuditableEntity
{
    DateTime CreatedAt { get; set; }
    string? CreatedBy { get; set; }
    DateTime? UpdatedAt { get; set; }
    string? UpdatedBy { get; set; }
}
