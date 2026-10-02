using System;
using System.Collections.Generic;
using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Sesión de conversación o interacción jurídica asistida por IA.
/// Implementa multi-tenancy estricto, concurrencia xmin y borrado lógico.
/// </summary>
public class AIConversation : BaseEntity, IMultiTenant, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = null!;

    public Guid? ExpedienteId { get; set; }
    public Expediente? Expediente { get; set; }

    public string Titulo { get; set; } = "Nueva Conversación";
    public AICasoUso CasoUso { get; set; } = AICasoUso.ChatLibre;

    public bool IsArchived { get; set; } = false;

    // ISoftDeletable (período de gracia de 30 días antes de purga física)
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public uint Version { get; set; }

    public ICollection<AIMessage> Mensajes { get; set; } = new List<AIMessage>();
}
