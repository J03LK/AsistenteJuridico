using System.Security.Cryptography;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Servicio de almacenamiento físico seguro de documentos.
/// Aplica validación estricta de extensiones, comprobación de Magic Bytes, nombres físicos UUID,
/// cálculo de SHA-256 en streaming y validación canónica contra ataques de Path Traversal.
/// 
/// LIMITACIÓN DE SEGURIDAD EXPLÍCITA:
/// La verificación de Magic Bytes valida exclusivamente la firma estructural de cabecera del formato.
/// NO constituye un motor antivirus, NO analiza contenido activo, scripts incrustados ni macros maliciosas.
/// </summary>
public class FileStorageService : IFileStorageService
{
    private readonly string _baseStoragePath;
    private readonly ILogger<FileStorageService> _logger;

    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB

    private static readonly Dictionary<string, (string ContentType, byte[][] Signatures)> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ("application/pdf", [
            [0x25, 0x50, 0x44, 0x46] // %PDF
        ]),
        [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [
            [0x50, 0x4B, 0x03, 0x04] // PK.. (ZIP format)
        ]),
        [".doc"] = ("application/msword", [
            [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1] // OLE Compound File
        ]),
        [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [
            [0x50, 0x4B, 0x03, 0x04] // PK.. (ZIP format)
        ]),
        [".xls"] = ("application/vnd.ms-excel", [
            [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1] // OLE Compound File
        ]),
        [".png"] = ("image/png", [
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] // PNG signature
        ]),
        [".jpg"] = ("image/jpeg", [
            [0xFF, 0xD8, 0xFF] // JPEG SOI
        ]),
        [".jpeg"] = ("image/jpeg", [
            [0xFF, 0xD8, 0xFF] // JPEG SOI
        ])
    };

    public FileStorageService(IConfiguration configuration, ILogger<FileStorageService> logger)
    {
        _logger = logger;

        var configuredPath = configuration["FileStorage:BasePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            _baseStoragePath = Path.GetFullPath(configuredPath);
        }
        else
        {
            // Entorno Docker /app/storage o carpeta local de ejecución
            _baseStoragePath = Directory.Exists("/app/storage")
                ? "/app/storage"
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "storage"));
        }

        if (!Directory.Exists(_baseStoragePath))
        {
            Directory.CreateDirectory(_baseStoragePath);
        }
    }

    public async Task<(string PhysicalFileName, string RelativeFilePath, string ContentType, long FileSizeBytes, string Sha256Hash)> SaveFileAsync(
        Guid tenantId,
        Stream fileStream,
        string originalFileName,
        string declaredContentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new ValidationException(["El nombre original del archivo es obligatorio."]);
        }

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var extensionMetadata))
        {
            throw new ValidationException([$"La extensión '{extension}' no está permitida. Extensiones admitidas: .pdf, .docx, .doc, .xlsx, .xls, .png, .jpg, .jpeg"]);
        }

        // Inspeccionar Magic Bytes sin consumir el stream por completo
        byte[] headerBytes = new byte[8];
        int bytesRead = await fileStream.ReadAsync(headerBytes.AsMemory(0, 8), cancellationToken);
        if (bytesRead < 3)
        {
            throw new ValidationException(["El archivo está vacío o dañado (tamaño inferior al mínimo de cabecera)."]);
        }

        var matchesMagicBytes = extensionMetadata.Signatures.Any(sig =>
            headerBytes.Take(sig.Length).SequenceEqual(sig));

        if (!matchesMagicBytes)
        {
            throw new ValidationException([$"El contenido binario no coincide con la extensión declarada '{extension}' (Magic Bytes inválidos)."]);
        }

        // Reiniciar stream al principio para escritura
        if (fileStream.CanSeek)
        {
            fileStream.Seek(0, SeekOrigin.Begin);
        }
        else
        {
            throw new InvalidOperationException("El stream del archivo debe permitir Seek para almacenar con verificación de cabecera.");
        }

        var physicalFileName = $"{Guid.NewGuid():N}{extension}";
        var tenantFolder = Path.Combine(_baseStoragePath, tenantId.ToString("N"));
        if (!Directory.Exists(tenantFolder))
        {
            Directory.CreateDirectory(tenantFolder);
        }

        var fullPhysicalPath = Path.Combine(tenantFolder, physicalFileName);
        var relativePath = Path.Combine(tenantId.ToString("N"), physicalFileName).Replace('\\', '/');

        long totalBytesWritten = 0;
        string sha256Hex;

        try
        {
            using var sha256 = SHA256.Create();
            await using (var destinationFileStream = new FileStream(fullPhysicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    totalBytesWritten += read;
                    if (totalBytesWritten > MaxFileSizeBytes)
                    {
                        throw new ValidationException(["El archivo excede el tamaño máximo permitido de 25 MB."]);
                    }

                    await destinationFileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                }

                sha256.TransformFinalBlock(buffer, 0, 0);
                sha256Hex = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            }
        }
        catch
        {
            // Compensación inmediata si falla la escritura física o excede el tamaño
            if (File.Exists(fullPhysicalPath))
            {
                try
                {
                    File.Delete(fullPhysicalPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "No se pudo eliminar el archivo huérfano en {Path}", fullPhysicalPath);
                }
            }

            throw;
        }

        return (physicalFileName, relativePath, extensionMetadata.ContentType, totalBytesWritten, sha256Hex);
    }

    public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default)
    {
        var validatedFullPath = ResolveAndValidatePath(relativeFilePath);

        if (!File.Exists(validatedFullPath))
        {
            throw new NotFoundException("El archivo físico no fue encontrado en el almacenamiento.");
        }

        var stream = new FileStream(validatedFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream>(stream);
    }

    public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath))
            return Task.CompletedTask;

        try
        {
            var validatedFullPath = ResolveAndValidatePath(relativeFilePath);
            if (File.Exists(validatedFullPath))
            {
                File.Delete(validatedFullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al eliminar archivo físico de compensación: {Path}", relativeFilePath);
        }

        return Task.CompletedTask;
    }

    private string ResolveAndValidatePath(string relativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            throw new ValidationException(["La ruta relativa del archivo es inválida."]);
        }

        var normalizedRelative = relativeFilePath.Replace('\\', '/').TrimStart('/');
        var fullPath = Path.GetFullPath(Path.Combine(_baseStoragePath, normalizedRelative));
        var baseFullPath = Path.GetFullPath(_baseStoragePath);

        // Protección determinista contra Path Traversal
        if (!fullPath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenException("Violación de seguridad: Intento de Path Traversal detectado.");
        }

        var relativeCheck = Path.GetRelativePath(baseFullPath, fullPath);
        if (relativeCheck.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeCheck))
        {
            throw new ForbiddenException("Violación de seguridad: Intento de Path Traversal detectado.");
        }

        return fullPath;
    }
}
