using AsistenteJuridico.Domain.Common;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa la causa o proceso judicial externo radicado en el sistema de la Función Judicial del Ecuador (SATJE / e-SATJE).
/// Esta entidad modela los datos oficiales del Consejo de la Judicatura.
/// </summary>
public class ProcesoJudicial : AuditableEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Código judicial oficial (formato SATJE, e.g. 17230-2023-00456 o 21 dígitos numéricos).
    /// </summary>
    public string NumeroProceso { get; set; } = string.Empty;

    /// <summary>
    /// Unidad Judicial o Judicatura donde se tramita la causa (e.g. Unidad Judicial Civil de Quito).
    /// </summary>
    public string Judicatura { get; set; } = string.Empty;

    public string? JuezPonente { get; set; }
    public string? AccionInfraccion { get; set; }
    public string Materia { get; set; } = "Civil";
    public string EstadoJudicial { get; set; } = "En Trámite";

    public DateTime? FechaInicio { get; set; }
    public DateTime? UltimaSincronizacion { get; set; }

    /// <summary>
    /// Contenido JSON estructurado con actuaciones, partes procesales y providencias extraídas del SATJE.
    /// </summary>
    public string? DetallesJson { get; set; }
    public uint Version { get; set; }

    // Colecciones de navegación
    public ICollection<ExpedienteProcesoJudicial> ExpedientesVinculados { get; set; } = new List<ExpedienteProcesoJudicial>();
    public ICollection<Audiencia> Audiencias { get; set; } = new List<Audiencia>();
}
