namespace AsistenteJuridico.Domain.Common;

/// <summary>
/// Clase base para entidades auditables con registro de creación y modificación.
/// </summary>
public abstract class AuditableEntity : BaseEntity, IAuditableEntity
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
