using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Representa un documento digital (escrito, demanda, providencia, contrato, prueba documental).
/// Diseñado con soporte nativo para la futura indexación de texto y generación con IA/RAG (Fase 7).
/// </summary>
public class Documento : AuditableEntity, IMultiTenant, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    // Fase 7: todo documento pertenece a un expediente (NOT NULL, FK tenant-aware con RESTRICT).
    public Guid ExpedienteId { get; set; }
    public Expediente Expediente { get; set; } = null!;

    public string Titulo { get; set; } = string.Empty;
    public string TipoDocumento { get; set; } = "Escrito"; // Demanda, Providencia, Sentencia, Contrato, etc.
    public string? Descripcion { get; set; }

    // Nombre original saneado, solo para mostrar y descargar; nunca se usa como nombre físico.
    // Nullable por las filas anteriores a la Fase 7.
    public string? NombreArchivoOriginal { get; set; }

    // Identificador físico interno: nunca se expone en DTOs ni en la auditoría.
    public string RutaAlmacenamiento { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public long TamanioBytes { get; set; }
    public string? HashSha256 { get; set; }

    // Soporte para IA / RAG
    public EstadoProcesamientoIa EstadoIa { get; set; } = EstadoProcesamientoIa.Pendiente;
    public string? MetadatosJson { get; set; }
    public uint Version { get; set; }

    // Soft delete
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
