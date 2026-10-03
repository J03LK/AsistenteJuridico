using System.IO.Compression;
using System.Text;
using AsistenteJuridico.Application.Common.Exceptions;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Fase 7.2 — Validación de documentos subidos: extensión permitida, MIME declarado compatible y contenido
/// (Magic Bytes, estructura OOXML, TXT UTF-8 estricto). El ContentType guardado es siempre el canónico del servidor.
///
/// LIMITACIÓN DE SEGURIDAD EXPLÍCITA:
/// Esta validación comprueba la firma y la estructura del formato. NO es un antivirus: no detecta macros en
/// DOC/XLS, JavaScript en PDF, un .docm renombrado sin vbaProject.bin ni otro contenido malicioso, y no distingue
/// un DOC de un XLS (comparten la firma OLE).
/// </summary>
public static class DocumentoContentValidator
{
    /// <summary>Tamaño máximo de un documento: 25 MiB.</summary>
    public const long MaxFileSizeBytes = 25L * 1024 * 1024;

    public enum TipoValidacion
    {
        Firma,
        Ooxml,
        TextoUtf8
    }

    public sealed record FormatoDocumento(
        string Extension,
        string ContentTypeCanonico,
        IReadOnlyList<string> MimesDeclaradosAceptados,
        TipoValidacion Validacion,
        byte[] Firma,
        string? CarpetaOoxml = null);

    private static readonly byte[] FirmaZip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] FirmaOle = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] FirmaJpeg = [0xFF, 0xD8, 0xFF];

    private const string MimeDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string MimeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly Dictionary<string, FormatoDocumento> Formatos = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new(".pdf", "application/pdf", ["application/pdf", "application/x-pdf"], TipoValidacion.Firma, [0x25, 0x50, 0x44, 0x46]),
        [".docx"] = new(".docx", MimeDocx, [MimeDocx], TipoValidacion.Ooxml, FirmaZip, "word/"),
        [".xlsx"] = new(".xlsx", MimeXlsx, [MimeXlsx], TipoValidacion.Ooxml, FirmaZip, "xl/"),
        [".doc"] = new(".doc", "application/msword", ["application/msword"], TipoValidacion.Firma, FirmaOle),
        [".xls"] = new(".xls", "application/vnd.ms-excel", ["application/vnd.ms-excel"], TipoValidacion.Firma, FirmaOle),
        [".txt"] = new(".txt", "text/plain; charset=utf-8", ["text/plain"], TipoValidacion.TextoUtf8, []),
        [".jpg"] = new(".jpg", "image/jpeg", ["image/jpeg", "image/pjpeg"], TipoValidacion.Firma, FirmaJpeg),
        [".jpeg"] = new(".jpeg", "image/jpeg", ["image/jpeg", "image/pjpeg"], TipoValidacion.Firma, FirmaJpeg),
        [".png"] = new(".png", "image/png", ["image/png"], TipoValidacion.Firma, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
    };

    public static IReadOnlyCollection<string> ExtensionesPermitidas => Formatos.Keys;

    /// <summary>Formato de una extensión permitida; si no está en la lista, 415 DOCUMENT_TYPE_NOT_ALLOWED.</summary>
    public static FormatoDocumento ObtenerFormato(string extension)
    {
        if (string.IsNullOrEmpty(extension) || !Formatos.TryGetValue(extension, out var formato))
        {
            var mostrada = string.IsNullOrEmpty(extension) ? "(sin extensión)" : extension;
            throw new UnsupportedMediaTypeException(
                $"La extensión '{mostrada}' no está permitida. Extensiones admitidas: {string.Join(", ", Formatos.Keys)}.")
            {
                ErrorCode = DocumentoErrorCodes.TypeNotAllowed
            };
        }

        return formato;
    }

    /// <summary>
    /// El MIME declarado por el cliente debe ser compatible con la extensión. Se compara solo el tipo (sin
    /// parámetros, sin distinguir mayúsculas). Vacío o application/octet-stream se aceptan siempre.
    /// Contradictorio -> 415 DOCUMENT_TYPE_NOT_ALLOWED.
    /// </summary>
    public static void ValidarMimeDeclarado(FormatoDocumento formato, string? mimeDeclarado)
    {
        var tipo = (mimeDeclarado ?? string.Empty).Split(';')[0].Trim();
        if (tipo.Length == 0 || tipo.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!formato.MimesDeclaradosAceptados.Contains(tipo, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnsupportedMediaTypeException(
                $"El tipo de contenido declarado '{tipo}' no corresponde a la extensión '{formato.Extension}'.")
            {
                ErrorCode = DocumentoErrorCodes.TypeNotAllowed
            };
        }
    }

    /// <summary>
    /// Valida el contenido ya copiado (stream con Seek). Archivo vacío o contenido que no corresponde al formato
    /// -> 400 DOCUMENT_MAGIC_BYTES_INVALID. Deja el stream en una posición indeterminada.
    /// </summary>
    public static async Task ValidarContenidoAsync(FormatoDocumento formato, Stream contenido, CancellationToken cancellationToken = default)
    {
        if (contenido.Length == 0)
        {
            throw ContenidoInvalido("El archivo está vacío.");
        }

        contenido.Position = 0;
        switch (formato.Validacion)
        {
            case TipoValidacion.Firma:
                await ValidarFirmaAsync(formato, contenido, cancellationToken);
                break;
            case TipoValidacion.Ooxml:
                await ValidarFirmaAsync(formato, contenido, cancellationToken);
                contenido.Position = 0;
                ValidarOoxml(formato, contenido);
                break;
            case TipoValidacion.TextoUtf8:
                await ValidarTextoUtf8Async(contenido, cancellationToken);
                break;
        }
    }

    private static async Task ValidarFirmaAsync(FormatoDocumento formato, Stream contenido, CancellationToken cancellationToken)
    {
        var cabecera = new byte[formato.Firma.Length];
        var leidos = await contenido.ReadAtLeastAsync(cabecera, cabecera.Length, throwOnEndOfStream: false, cancellationToken);
        if (leidos < cabecera.Length || !cabecera.AsSpan().SequenceEqual(formato.Firma))
        {
            throw ContenidoInvalido($"El contenido binario no coincide con la extensión declarada '{formato.Extension}' (Magic Bytes inválidos).");
        }
    }

    /// <summary>
    /// Solo lee el directorio central del ZIP (no descomprime ninguna entrada): exige [Content_Types].xml y la
    /// carpeta del formato (word/ o xl/), y rechaza vbaProject.bin y entradas cifradas.
    /// </summary>
    private static void ValidarOoxml(FormatoDocumento formato, Stream contenido)
    {
        try
        {
            using var zip = new ZipArchive(contenido, ZipArchiveMode.Read, leaveOpen: true);
            var tieneContentTypes = false;
            var tieneCarpeta = false;

            foreach (var entrada in zip.Entries)
            {
                var nombre = entrada.FullName.Replace('\\', '/');
                if (nombre.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                {
                    tieneContentTypes = true;
                }
                else if (nombre.StartsWith(formato.CarpetaOoxml!, StringComparison.OrdinalIgnoreCase))
                {
                    tieneCarpeta = true;
                }

                if (nombre.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                {
                    throw ContenidoInvalido($"El archivo '{formato.Extension}' contiene macros (vbaProject.bin) y no está permitido.");
                }

                if (entrada.IsEncrypted)
                {
                    throw ContenidoInvalido($"El archivo '{formato.Extension}' está cifrado y no se puede validar.");
                }
            }

            if (!tieneContentTypes || !tieneCarpeta)
            {
                throw ContenidoInvalido($"El contenido no tiene la estructura de un documento '{formato.Extension}' válido (Magic Bytes inválidos).");
            }
        }
        catch (InvalidDataException)
        {
            throw ContenidoInvalido($"El archivo '{formato.Extension}' está dañado: no es un ZIP válido (Magic Bytes inválidos).");
        }
    }

    /// <summary>UTF-8 estricto (con o sin BOM) y sin bytes NUL, leído por bloques.</summary>
    private static async Task ValidarTextoUtf8Async(Stream contenido, CancellationToken cancellationToken)
    {
        // GetChars (no GetCharCount) para que el decoder conserve entre bloques los bytes de un carácter multibyte
        // partido, y para que el flush final detecte una secuencia truncada al final del archivo.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var decoder = encoding.GetDecoder();
        var buffer = new byte[81920];
        var caracteres = new char[encoding.GetMaxCharCount(buffer.Length)];
        try
        {
            int leidos;
            while ((leidos = await contenido.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                if (buffer.AsSpan(0, leidos).Contains((byte)0))
                {
                    throw ContenidoInvalido("El archivo de texto contiene bytes nulos y no es un texto UTF-8 válido.");
                }

                decoder.GetChars(buffer, 0, leidos, caracteres, 0, flush: false);
            }

            decoder.GetChars([], 0, 0, caracteres, 0, flush: true);
        }
        catch (DecoderFallbackException)
        {
            throw ContenidoInvalido("El archivo de texto no está codificado en UTF-8 válido.");
        }
    }

    private static ValidationException ContenidoInvalido(string mensaje) =>
        new([mensaje]) { ErrorCode = DocumentoErrorCodes.MagicBytesInvalid };
}
