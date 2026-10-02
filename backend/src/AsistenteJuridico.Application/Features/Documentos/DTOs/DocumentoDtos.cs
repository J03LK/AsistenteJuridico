using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Documentos.DTOs;

public record DocumentoDto(
    Guid Id,
    Guid TenantId,
    Guid? ExpedienteId,
    string? ExpedienteNumero,
    string Titulo,
    string TipoDocumento,
    string RutaAlmacenamiento,
    string ContentType,
    long TamanioBytes,
    string? HashSha256,
    EstadoProcesamientoIa EstadoIa,
    string EstadoIaDescripcion,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version);

public record UploadDocumentoDto(
    Guid ExpedienteId,
    string Titulo,
    string TipoDocumento);

public record DocumentoDownloadResult(
    Stream FileStream,
    string ContentType,
    string DownloadFileName);
