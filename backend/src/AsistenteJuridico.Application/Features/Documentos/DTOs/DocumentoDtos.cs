using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Documentos.DTOs;

/// <summary>
/// Metadatos públicos de un documento. Fase 7: nunca incluye RutaAlmacenamiento ni otra referencia física.
/// </summary>
public record DocumentoDto(
    Guid Id,
    Guid TenantId,
    Guid ExpedienteId,
    string? ExpedienteNumero,
    string Titulo,
    string TipoDocumento,
    string? Descripcion,
    string? NombreArchivoOriginal,
    string ContentType,
    long TamanioBytes,
    string? HashSha256,
    EstadoProcesamientoIa EstadoIa,
    string EstadoIaDescripcion,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime? UpdatedAt,
    uint Version);

public record UploadDocumentoDto(
    Guid ExpedienteId,
    string Titulo,
    string TipoDocumento,
    string? Descripcion = null);

/// <summary>
/// Fase 7.3 — Cuerpo del PUT /api/v1/documentos/{id}. Contiene EXCLUSIVAMENTE los campos editables y la versión
/// (xmin) que el cliente leyó. Cualquier otro campo del JSON se ignora. Las propiedades son nullable para que la
/// validación (400) ocurra en el servicio, después de la autorización.
/// </summary>
public record UpdateDocumentoDto(
    string? Titulo,
    string? TipoDocumento,
    string? Descripcion,
    uint? Version);

/// <summary>
/// Fase 7.3 — Filtros del listado oficial GET /api/v1/documentos. ExpedienteId es obligatorio. El tenant nunca
/// viene del cliente: se toma del contexto autenticado.
/// </summary>
public record DocumentoFilterRequest : PagedRequest
{
    public Guid? ExpedienteId { get; init; }
    public string? TipoDocumento { get; init; }
    public DateTime? FechaDesde { get; init; }
    public DateTime? FechaHasta { get; init; }
}

public record DocumentoDownloadResult(
    Stream FileStream,
    string ContentType,
    string DownloadFileName);
