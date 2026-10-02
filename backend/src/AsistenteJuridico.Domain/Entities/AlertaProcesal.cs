using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa una notificación o alerta procesal u operativa generada por el sistema.
/// Implementa multi-tenancy estricto, concurrencia optimista vía xmin y ciclo de vida determinista.
/// </summary>
public class AlertaProcesal : BaseEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Usuario destinatario. Si es null, representa una alerta institucional de supervisión del estudio.
    /// </summary>
    public Guid? UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }

    /// <summary>
    /// Tipo de entidad origen de la alerta (Audiencia, Tarea, Expediente).
    /// </summary>
    public TipoOrigenAlerta TipoOrigen { get; set; }

    /// <summary>
    /// Identificador único de la entidad origen.
    /// </summary>
    public Guid OrigenId { get; set; }

    /// <summary>
    /// Código de la regla evaluada (ej. Audiencia7Dias, TareaVencida, etc.).
    /// </summary>
    public ReglaAlertaCodigo ReglaAlerta { get; set; }

    /// <summary>
    /// Hito temporal objetivo sobre el cual se calculó la alerta (ej. FechaHora de la audiencia o FechaVencimiento de la tarea).
    /// </summary>
    public DateTime FechaObjetivoUtc { get; set; }

    /// <summary>
    /// Instante cronológico exacto en que el worker insertó la alerta.
    /// </summary>
    public DateTime FechaDisparoUtc { get; set; } = DateTime.UtcNow;

    public string Titulo { get; set; } = string.Empty;
    public string Mensaje { get; set; } = string.Empty;
    public SeveridadAlerta Severidad { get; set; } = SeveridadAlerta.Media;

    /// <summary>
    /// Expediente asociado para contextualización y navegación directa.
    /// </summary>
    public Guid? ExpedienteId { get; set; }
    public Expediente? Expediente { get; set; }

    /// <summary>
    /// Única fuente de verdad para el estado de la alerta.
    /// </summary>
    public EstadoAlertaResolucion EstadoResolucion { get; set; } = EstadoAlertaResolucion.Activa;

    /// <summary>
    /// Fecha en que la alerta fue resuelta, invalidada o descartada.
    /// </summary>
    public DateTime? ResueltaUtc { get; set; }

    /// <summary>
    /// Motivo descriptivo de la resolución o descarte.
    /// </summary>
    public string? MotivoResolucion { get; set; }

    /// <summary>
    /// Indica si el usuario destinatario ha marcado la alerta como leída.
    /// </summary>
    public bool Leida { get; set; } = false;

    /// <summary>
    /// Fecha y hora en que se marcó como leída.
    /// </summary>
    public DateTime? FechaLeidaUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Token de concurrencia optimista mapeado al system column xmin de PostgreSQL.
    /// </summary>
    public uint Version { get; set; }
}
