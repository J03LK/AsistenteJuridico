namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Contrato para almacenamiento físico seguro de documentos.
/// Aplica validación estricta de extensión, MIME y contenido (Magic Bytes, OOXML, TXT UTF-8), nombres físicos
/// GUID, cálculo de hash SHA-256 por streaming y protección contra Path Traversal y symlinks/puntos de reanálisis.
/// NOTA IMPORTANTE: La verificación de contenido valida la firma y la estructura del formato del archivo,
/// pero NO equivale a un antivirus ni analiza contenido activo o macros maliciosas.
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// Fase 7.2: valida y guarda el archivo de un documento. Copia el contenido a un temporal
    /// (<c>{base}/.tmp/{guid:N}.upload</c>) calculando el SHA-256, lo valida y lo mueve, sin sobrescribir, a
    /// <c>{base}/{tenant:N}/{expediente:N}/{guid:N}{ext}</c>. El temporal se elimina siempre.
    /// El nombre original saneado se devuelve para guardarlo como metadato; nunca se usa como nombre físico.
    /// </summary>
    Task<StoredDocumentoFile> SaveDocumentoAsync(
        Guid tenantId,
        Guid expedienteId,
        Stream fileStream,
        string originalFileName,
        string? declaredContentType,
        long? declaredLength,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default);

    Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resultado de guardar un documento. <see cref="RelativePath"/> es interno: nunca se expone en DTOs ni respuestas.
/// </summary>
public sealed record StoredDocumentoFile(
    string RelativePath,
    string ContentType,
    long SizeBytes,
    string Sha256Hash,
    string NombreArchivoOriginal);
