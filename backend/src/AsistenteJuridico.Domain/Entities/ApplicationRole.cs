using Microsoft.AspNetCore.Identity;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa un rol dentro del sistema de seguridad de Asistente Jurídico IA.
/// </summary>
public class ApplicationRole : IdentityRole<Guid>
{
    public string? Descripcion { get; set; }
    public bool IsSystemRole { get; set; } = true;

    public ApplicationRole() : base() { }

    public ApplicationRole(string roleName, string? descripcion = null) : base(roleName)
    {
        Descripcion = descripcion;
    }
}
