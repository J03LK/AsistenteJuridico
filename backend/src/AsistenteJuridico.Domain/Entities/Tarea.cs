using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa una tarea procesal, vencimiento de término o actividad interna del estudio jurídico.
/// </summary>
public class Tarea : AuditableEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid? ExpedienteId { get; set; }
    public Expediente? Expediente { get; set; }

    public Guid? AsignadoAUsuarioId { get; set; }
    public Usuario? AsignadoA { get; set; }

    public string Titulo { get; set; } = string.Empty;
    public string? Descripcion { get; set; }

    /// <summary>
    /// Fecha y hora límite o vencimiento de término judicial.
    /// </summary>
    public DateTime FechaVencimiento { get; set; }

    public Prioridad Prioridad { get; set; } = Prioridad.Media;
    public EstadoTarea Estado { get; set; } = EstadoTarea.Pendiente;
    public DateTime? FechaCompletada { get; set; }
    public uint Version { get; set; }
}
