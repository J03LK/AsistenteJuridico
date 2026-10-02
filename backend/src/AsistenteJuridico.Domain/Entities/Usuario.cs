using AsistenteJuridico.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa un usuario dentro de un estudio jurídico (Abogado, Asistente, Administrador).
/// Integrado con ASP.NET Core Identity con soporte multi-tenant, soft-delete y auditoría.
/// </summary>
public class Usuario : IdentityUser<Guid>, IMultiTenant, ISoftDeletable, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public string NombreCompleto { get; set; } = string.Empty;
    public string Rol { get; set; } = "AbogadoJunior";
    public bool Activo { get; set; } = true;

    // Soft delete
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    // Auditoría
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    // Colecciones de navegación
    public ICollection<Expediente> ExpedientesAsignados { get; set; } = new List<Expediente>();
    public ICollection<Tarea> TareasAsignadas { get; set; } = new List<Tarea>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
