using AsistenteJuridico.Domain.Common;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa un token de refresco con rotación estricta, rastreo de familia y detección de replay.
/// </summary>
public class RefreshToken : IMultiTenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Hash criptográfico SHA-256 del token para evitar persistir secretos en texto plano.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public Usuario User { get; set; } = null!;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Identificador de la familia o linaje del token para detección de reutilización maliciosa.
    /// </summary>
    public Guid FamilyId { get; set; }

    /// <summary>
    /// Stamp de seguridad del usuario en el momento de emisión.
    /// </summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByIp { get; set; }

    public bool IsRevoked { get; set; } = false;
    public DateTime? RevokedAt { get; set; }
    public string? RevokedByIp { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? ReasonRevoked { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsActive => !IsRevoked && !IsExpired;
}
