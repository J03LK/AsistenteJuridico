using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Features.Documentos.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IDocumentoService
{
    Task<DocumentoDto> UploadDocumentoAsync(
        UploadDocumentoDto dto,
        Stream fileStream,
        string originalFileName,
        string declaredContentType,
        long? declaredLength = null,
        CancellationToken cancellationToken = default);

    Task<DocumentoDownloadResult> DownloadDocumentoAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Fase 7.3 — Listado oficial paginado (GET /api/v1/documentos).</summary>
    Task<PagedResult<DocumentoDto>> GetDocumentosPagedAsync(DocumentoFilterRequest filtro, CancellationToken cancellationToken = default);

    /// <summary>Ruta obsoleta: misma consulta que el listado oficial, sin paginar.</summary>
    Task<IReadOnlyList<DocumentoDto>> GetDocumentosByExpedienteAsync(Guid expedienteId, CancellationToken cancellationToken = default);

    Task<DocumentoDto> GetDocumentoByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Fase 7.3 — Edita solo Titulo, TipoDocumento y Descripcion, con control de versión xmin.</summary>
    Task<DocumentoDto> UpdateDocumentoAsync(Guid id, UpdateDocumentoDto dto, CancellationToken cancellationToken = default);

    /// <summary>Fase 7.3 — Borrado lógico con control de versión xmin (obligatoria).</summary>
    Task DeleteDocumentoAsync(Guid id, uint? version, CancellationToken cancellationToken = default);
}
