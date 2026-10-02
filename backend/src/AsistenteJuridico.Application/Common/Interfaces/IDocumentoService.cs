using AsistenteJuridico.Application.Features.Documentos.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IDocumentoService
{
    Task<DocumentoDto> UploadDocumentoAsync(
        UploadDocumentoDto dto,
        Stream fileStream,
        string originalFileName,
        string declaredContentType,
        CancellationToken cancellationToken = default);

    Task<DocumentoDownloadResult> DownloadDocumentoAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentoDto>> GetDocumentosByExpedienteAsync(Guid expedienteId, CancellationToken cancellationToken = default);
    Task<DocumentoDto> GetDocumentoByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteDocumentoAsync(Guid id, CancellationToken cancellationToken = default);
}
