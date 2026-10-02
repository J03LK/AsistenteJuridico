namespace AsistenteJuridico.Domain.Common;

/// <summary>
/// Clase base para todas las entidades del dominio con identificador único UUIDv7/v4.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
