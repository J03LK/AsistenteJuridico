namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Contrato para almacenamiento físico seguro de documentos.
/// Aplica validación estricta de extensiones, Magic Bytes, nombres físicos UUID,
/// cálculo de hash SHA-256 por streaming y protección contra Path Traversal.
/// NOTA IMPORTANTE: La verificación de Magic Bytes valida la firma de formato del archivo,
/// pero NO equivale a un antivirus ni analiza contenido activo o macros maliciosas.
/// </summary>
public interface IFileStorageService
{
    Task<(string PhysicalFileName, string RelativeFilePath, string ContentType, long FileSizeBytes, string Sha256Hash)> SaveFileAsync(
        Guid tenantId,
        Stream fileStream,
        string originalFileName,
        string declaredContentType,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default);

    Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default);
}
