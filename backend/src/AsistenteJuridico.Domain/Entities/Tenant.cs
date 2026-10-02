using AsistenteJuridico.Domain.Common;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa una firma jurídica, estudio de abogados o profesional independiente (inquilino multi-tenant).
/// </summary>
public class Tenant : BaseEntity
{
    public string Nombre { get; set; } = string.Empty;
    public string? Ruc { get; set; }
    public string IdentificadorUrl { get; set; } = string.Empty;
    public string Plan { get; set; } = "Starter";
    public bool Activo { get; set; } = true;
    public string ZonaHorariaId { get; set; } = "America/Guayaquil";
    public string? ConfiguracionJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Colecciones de navegación
    public ICollection<Usuario> Usuarios { get; set; } = new List<Usuario>();
    public ICollection<Cliente> Clientes { get; set; } = new List<Cliente>();
    public ICollection<Expediente> Expedientes { get; set; } = new List<Expediente>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
