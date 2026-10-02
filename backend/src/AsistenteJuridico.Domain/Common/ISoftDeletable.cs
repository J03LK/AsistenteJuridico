namespace AsistenteJuridico.Domain.Common;

/// <summary>
/// Interfaz para entidades con borrado lógico (soft delete).
/// </summary>
public interface ISoftDeletable
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
