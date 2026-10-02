using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa el expediente interno de un caso legal gestionado por el estudio jurídico.
/// Desacoplado del proceso judicial externo para soportar casos sin juicio, acuerdos extrajudiciales
/// o expedientes con múltiples causas judiciales asociadas.
/// </summary>
public class Expediente : AuditableEntity, IMultiTenant, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public string NumeroExpediente { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public string Materia { get; set; } = "Civil";
    public EstadoExpediente Estado { get; set; } = EstadoExpediente.Abierto;
    public Prioridad Prioridad { get; set; } = Prioridad.Media;

    // Relación con Cliente (obligatorio)
    public Guid ClienteId { get; set; }
    public Cliente Cliente { get; set; } = null!;

    // Abogado responsable interno (opcional)
    public Guid? AbogadoResponsableId { get; set; }
    public Usuario? AbogadoResponsable { get; set; }

    public DateTime FechaApertura { get; set; } = DateTime.UtcNow;
    public DateTime? FechaCierreEstimada { get; set; }
    public DateTime? FechaCierreReal { get; set; }
    public uint Version { get; set; }

    // Soft delete
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    // Colecciones de navegación
    public ICollection<ExpedienteProcesoJudicial> ProcesosVinculados { get; set; } = new List<ExpedienteProcesoJudicial>();
    public ICollection<Documento> Documentos { get; set; } = new List<Documento>();
    public ICollection<Tarea> Tareas { get; set; } = new List<Tarea>();
    public ICollection<Audiencia> Audiencias { get; set; } = new List<Audiencia>();

    /// <summary>
    /// Valida si una transición de estado es válida según el flujo de vida del expediente.
    /// </summary>
    public bool PuedeTransicionarA(EstadoExpediente nuevoEstado)
    {
        if (Estado == nuevoEstado) return true;

        return (Estado, nuevoEstado) switch
        {
            // Desde Abierto
            (EstadoExpediente.Abierto, EstadoExpediente.EnTramite) => true,
            (EstadoExpediente.Abierto, EstadoExpediente.Suspendido) => true,
            (EstadoExpediente.Abierto, EstadoExpediente.Archivado) => true,

            // Desde EnTramite
            (EstadoExpediente.EnTramite, EstadoExpediente.Suspendido) => true,
            (EstadoExpediente.EnTramite, EstadoExpediente.Cerrado) => true,
            (EstadoExpediente.EnTramite, EstadoExpediente.Archivado) => true,

            // Desde Suspendido
            (EstadoExpediente.Suspendido, EstadoExpediente.EnTramite) => true,
            (EstadoExpediente.Suspendido, EstadoExpediente.Archivado) => true,

            // Desde Cerrado
            (EstadoExpediente.Cerrado, EstadoExpediente.EnTramite) => true, // Reapertura
            (EstadoExpediente.Cerrado, EstadoExpediente.Archivado) => true,

            // Desde Archivado
            (EstadoExpediente.Archivado, EstadoExpediente.EnTramite) => true, // Desarchivo

            _ => false
        };
    }
}
