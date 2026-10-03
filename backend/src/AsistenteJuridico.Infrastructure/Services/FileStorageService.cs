using System.Security.Cryptography;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Servicio de almacenamiento físico seguro de documentos (Fase 7.2).
/// - Validación de extensión, MIME y contenido con <see cref="DocumentoContentValidator"/>.
/// - Copia a un temporal en <c>{base}/.tmp</c> con SHA-256 incremental y corte a 25 MiB; movimiento final sin
///   sobrescribir a <c>{base}/{tenant:N}/{expediente:N}/{guid:N}{ext}</c>; el temporal se elimina siempre.
/// - Nombre físico siempre GUID + extensión; el nombre original saneado nunca se usa como nombre físico.
/// - Rutas resueltas solo desde la base de datos, validadas por componentes, contenidas en <c>base + separador</c>
///   y sin symlinks ni puntos de reanálisis bajo la base (escritura, lectura y eliminación).
///
/// LIMITACIÓN DE SEGURIDAD EXPLÍCITA:
/// La validación de contenido comprueba la firma y la estructura del formato.
/// NO constituye un motor antivirus, NO analiza contenido activo, scripts incrustados ni macros maliciosas.
/// </summary>
public class FileStorageService : IFileStorageService
{
    private const string CarpetaTemporal = ".tmp";
    private const string MensajeTraversal = "Violación de seguridad: Intento de Path Traversal detectado.";
    private const string MensajeEnlace = "Violación de seguridad: La ruta de almacenamiento contiene un enlace simbólico o punto de reanálisis.";

    private static readonly StringComparison ComparacionRutas =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly char[] CaracteresInvalidosEnComponente =
        Path.GetInvalidFileNameChars().Concat(['\\', '/', ':']).Distinct().ToArray();

    private readonly string _baseStoragePath;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(IConfiguration configuration, ILogger<FileStorageService> logger)
    {
        _logger = logger;

        var configuredPath = configuration["FileStorage:BasePath"];
        string basePath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            basePath = Path.GetFullPath(configuredPath);
        }
        else
        {
            // Entorno Docker /app/storage o carpeta local de ejecución
            basePath = Directory.Exists("/app/storage")
                ? "/app/storage"
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "storage"));
        }

        _baseStoragePath = Path.TrimEndingDirectorySeparator(basePath);

        if (!Directory.Exists(_baseStoragePath))
        {
            Directory.CreateDirectory(_baseStoragePath);
        }
    }

    public async Task<StoredDocumentoFile> SaveDocumentoAsync(
        Guid tenantId,
        Guid expedienteId,
        Stream fileStream,
        string originalFileName,
        string? declaredContentType,
        long? declaredLength,
        CancellationToken cancellationToken = default)
    {
        // 1. Comprobaciones previas a leer el contenido: nombre, extensión (415), MIME (415) y tamaño declarado (413)
        var nombreOriginal = NombreArchivoSanitizer.Sanitizar(originalFileName);
        var extension = Path.GetExtension(nombreOriginal).ToLowerInvariant();
        var formato = DocumentoContentValidator.ObtenerFormato(extension);
        DocumentoContentValidator.ValidarMimeDeclarado(formato, declaredContentType);

        if (declaredLength > DocumentoContentValidator.MaxFileSizeBytes)
        {
            throw TamanioExcedido();
        }

        var carpetaTemporal = Path.Combine(_baseStoragePath, CarpetaTemporal);
        CrearDirectorioSeguro(carpetaTemporal);
        var rutaTemporal = Path.Combine(carpetaTemporal, $"{Guid.NewGuid():N}.upload");

        try
        {
            // 2. Copia al temporal con SHA-256 incremental, corte al superar 25 MiB y validación de contenido
            long totalBytes = 0;
            string sha256Hex;
            await using (var temporal = new FileStream(rutaTemporal, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int leidos;
                while ((leidos = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    totalBytes += leidos;
                    if (totalBytes > DocumentoContentValidator.MaxFileSizeBytes)
                    {
                        throw TamanioExcedido();
                    }

                    await temporal.WriteAsync(buffer.AsMemory(0, leidos), cancellationToken);
                    sha256.AppendData(buffer, 0, leidos);
                }

                sha256Hex = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();

                await temporal.FlushAsync(cancellationToken);
                await DocumentoContentValidator.ValidarContenidoAsync(formato, temporal, cancellationToken);
            }

            // 3. Movimiento al destino final, sin sobrescribir. Nombre físico: GUID + extensión.
            var rutaRelativa = $"{tenantId:N}/{expedienteId:N}/{Guid.NewGuid():N}{extension}";
            var rutaFinal = ResolveAndValidatePath(rutaRelativa);
            CrearDirectorioSeguro(Path.GetDirectoryName(rutaFinal)!);
            File.Move(rutaTemporal, rutaFinal, overwrite: false);

            return new StoredDocumentoFile(rutaRelativa, formato.ContentTypeCanonico, totalBytes, sha256Hex, nombreOriginal);
        }
        finally
        {
            // 4. El temporal se elimina siempre (éxito ya movido, validación, cancelación, exceso o error de E/S)
            EliminarTemporal(rutaTemporal);
        }
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

    /// <summary>
    /// Compensación: borra el archivo si existe. Aplica las mismas defensas que la lectura; si la ruta no es
    /// segura no borra nada y deja constancia en el log (no lanza, para no ocultar el error original).
    /// </summary>
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

    /// <summary>
    /// Resuelve una ruta relativa guardada en la base de datos. Cada componente (separados por '/') no puede estar
    /// vacío, ser '.' o '..', empezar por '.', ni contener '\', ':' u otro carácter inválido; la ruta resultante debe
    /// quedar bajo <c>base + separador</c> y no atravesar symlinks ni puntos de reanálisis. Violación -> 403.
    /// Las rutas antiguas ({tenant}/{archivo}) siguen siendo válidas.
    /// </summary>
    private string ResolveAndValidatePath(string relativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            throw new ValidationException(["La ruta relativa del archivo es inválida."]);
        }

        if (Path.IsPathRooted(relativeFilePath) || relativeFilePath.StartsWith('/') || relativeFilePath.StartsWith('\\'))
        {
            throw Traversal(relativeFilePath);
        }

        var componentes = relativeFilePath.Split('/');
        foreach (var componente in componentes)
        {
            if (componente.Length == 0
                || componente.StartsWith('.')
                || componente.IndexOfAny(CaracteresInvalidosEnComponente) >= 0)
            {
                throw Traversal(relativeFilePath);
            }
        }

        var fullPath = Path.GetFullPath(Path.Combine([_baseStoragePath, .. componentes]));
        if (!RutaContenidaEnBase(_baseStoragePath, fullPath))
        {
            throw Traversal(relativeFilePath);
        }

        AsegurarSinEnlacesBajoLaBase(fullPath);
        return fullPath;
    }

    /// <summary>
    /// Defensa en profundidad: la ruta completa debe empezar por <c>base + separador</c> (no basta con el texto de
    /// la base, que también es prefijo de una carpeta vecina como <c>storage-evil</c>). Sin distinguir mayúsculas
    /// solo en Windows. La base misma no cuenta como contenida.
    /// </summary>
    public static bool RutaContenidaEnBase(string basePath, string fullPath)
    {
        var baseConSeparador = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(baseConSeparador, ComparacionRutas);
    }

    /// <summary>
    /// Rechaza cualquier symlink, junction o punto de reanálisis en los componentes existentes bajo la base
    /// (incluido el propio archivo). La base puede ser un montaje (por ejemplo, un volumen de Docker).
    /// </summary>
    private void AsegurarSinEnlacesBajoLaBase(string fullPath)
    {
        var relativa = Path.GetRelativePath(_baseStoragePath, fullPath);
        var actual = _baseStoragePath;
        foreach (var componente in relativa.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            actual = Path.Combine(actual, componente);

            FileAttributes atributos;
            try
            {
                atributos = File.GetAttributes(actual);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Un symlink roto no existe para GetAttributes; se detecta por su destino de enlace.
                if (new FileInfo(actual).LinkTarget != null)
                {
                    throw Enlace(actual);
                }

                return; // El resto de la ruta todavía no existe
            }

            if ((atributos & FileAttributes.ReparsePoint) != 0)
            {
                throw Enlace(actual);
            }
        }
    }

    /// <summary>Crea un directorio bajo la base comprobando que no haya enlaces antes y después de crearlo.</summary>
    private void CrearDirectorioSeguro(string directorio)
    {
        AsegurarSinEnlacesBajoLaBase(directorio);
        Directory.CreateDirectory(directorio);
        AsegurarSinEnlacesBajoLaBase(directorio);
    }

    private void EliminarTemporal(string rutaTemporal)
    {
        try
        {
            if (File.Exists(rutaTemporal))
            {
                File.Delete(rutaTemporal);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo eliminar el archivo temporal de subida {Path}", Path.GetFileName(rutaTemporal));
        }
    }

    private ForbiddenException Traversal(string rutaRelativa)
    {
        _logger.LogWarning("[STORAGE_PATH_TRAVERSAL] Ruta de almacenamiento rechazada: {Path}", rutaRelativa);
        return new ForbiddenException(MensajeTraversal);
    }

    private ForbiddenException Enlace(string rutaCompleta)
    {
        _logger.LogWarning(
            "[STORAGE_REPARSE_POINT] Enlace simbólico o punto de reanálisis bajo la base de almacenamiento: {Path}",
            Path.GetRelativePath(_baseStoragePath, rutaCompleta));
        return new ForbiddenException(MensajeEnlace);
    }

    private static PayloadTooLargeException TamanioExcedido() =>
        new($"El archivo excede el tamaño máximo permitido de {DocumentoContentValidator.MaxFileSizeBytes / (1024 * 1024)} MiB.")
        {
            ErrorCode = DocumentoErrorCodes.SizeExceeded
        };
}
