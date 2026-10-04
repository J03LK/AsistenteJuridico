using System.Text;
using System.Xml;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Helpers;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WordParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Fase 6.X (H10) — Extracción de texto real: TXT (UTF-8 estricto), PDF (PdfPig), DOCX y XLSX (Open XML SDK).
/// DOC, XLS, JPG y PNG no tienen extracción (sin OCR). Nunca devuelve texto de respaldo: cualquier problema es un
/// <see cref="ExtractionStatus"/> distinto de Success. La cancelación del llamador se propaga sin alterar.
/// Los logs incluyen el id del documento y el tipo de error, nunca la ruta ni el contenido.
///
/// TIMEOUT (30 s): ni PdfPig ni el Open XML SDK aceptan un CancellationToken. Por eso las bibliotecas leen el archivo a
/// través de <see cref="StreamCancelable"/>, que comprueba el token en CADA lectura o reposicionamiento: al vencer el
/// timeout, la siguiente lectura de la biblioteca lanza y el análisis se interrumpe desde dentro. Además se comprueba
/// el token entre páginas, párrafos y filas. La llamada espera a que el trabajo termine de verdad (no hay WaitAsync que
/// abandone una tarea en segundo plano): cuando ExtractAsync devuelve, ya no queda análisis en ejecución.
///
/// LIMITACIÓN EXPLÍCITA: el procesamiento en memoria entre dos puntos de comprobación (p. ej., descomprimir y
/// analizar UN flujo de contenido ya leído) no es interrumpible; el exceso sobre los 30 s queda acotado a ese tramo,
/// y el archivo ya está limitado a 25 MiB en la subida.
/// </summary>
public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    private const string MimePdf = "application/pdf";
    private const string MimeDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string MimeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string MimeTxt = "text/plain";

    /// <summary>Corte en MaxDocumentCharacters + 1: basta para detectar el exceso sin leer el documento entero.</summary>
    private static readonly int LimiteCaracteres = ContextWindowValidator.MaxDocumentCharacters;

    /// <summary>Bytes de TXT necesarios para detectar el exceso: (límite + 1) caracteres de hasta 4 bytes, más el BOM.</summary>
    private static readonly int MaxBytesTxt = (LimiteCaracteres + 1) * 4 + 3;

    /// <summary>Límite de caracteres por parte OOXML: frena las bombas de descompresión (25 MiB comprimidos).</summary>
    private const long MaxCaracteresPorParteOoxml = 64L * 1024 * 1024;

    private enum Formato { NoSoportado, Txt, Pdf, Docx, Xlsx }

    private readonly IFileStorageService _fileStorageService;
    private readonly TimeSpan _timeout;
    private readonly ILogger<DocumentTextExtractor> _logger;

    public DocumentTextExtractor(
        IFileStorageService fileStorageService,
        IOptions<DocumentTextExtractionOptions>? options = null,
        ILogger<DocumentTextExtractor>? logger = null)
    {
        _fileStorageService = fileStorageService;
        var segundos = options?.Value.TimeoutSeconds ?? 30;
        _timeout = TimeSpan.FromSeconds(segundos > 0 ? segundos : 30);
        _logger = logger ?? NullLogger<DocumentTextExtractor>.Instance;
    }

    public bool IsSupported(string? contentType) => Clasificar(contentType) != Formato.NoSoportado;

    public async Task<ExtractionResult> ExtractAsync(
        Guid documentoId,
        string? rutaAlmacenamiento,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        var formato = Clasificar(contentType);
        if (formato == Formato.NoSoportado)
        {
            return Resultado(documentoId, ExtractionStatus.UnsupportedFormat);
        }

        if (string.IsNullOrWhiteSpace(rutaAlmacenamiento))
        {
            _logger.LogError("[DOCUMENT_FILE_NOT_FOUND] El documento {DocumentoId} no tiene archivo físico asociado.", documentoId);
            return Resultado(documentoId, ExtractionStatus.FileNotFound);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        var token = timeoutCts.Token;

        Stream? archivo = null;
        try
        {
            try
            {
                archivo = await _fileStorageService.OpenReadFileAsync(rutaAlmacenamiento, token);
            }
            catch (NotFoundException)
            {
                _logger.LogError("[DOCUMENT_FILE_NOT_FOUND] El archivo físico del documento {DocumentoId} no existe en el almacenamiento.", documentoId);
                return Resultado(documentoId, ExtractionStatus.FileNotFound);
            }
            catch (ValidationException)
            {
                // Ruta vacía o inválida guardada en la base de datos: no hay archivo utilizable.
                _logger.LogError("[DOCUMENT_FILE_NOT_FOUND] El documento {DocumentoId} tiene una ruta de almacenamiento inválida.", documentoId);
                return Resultado(documentoId, ExtractionStatus.FileNotFound);
            }
            catch (ForbiddenException)
            {
                // Ruta insegura, symlink o punto de reanálisis: la operación se aborta (nunca se continúa).
                _logger.LogError("[AI_DOCUMENT_STORAGE_FORBIDDEN] El almacenamiento rechazó la ruta del documento {DocumentoId}.", documentoId);
                return Resultado(documentoId, ExtractionStatus.Forbidden);
            }

            string texto;
            if (formato == Formato.Txt)
            {
                texto = await LeerTxtAsync(archivo, token);
            }
            else
            {
                var fuente = archivo.CanSeek ? archivo : await CopiarAMemoriaAsync(archivo, token);
                var lectura = new StreamCancelable(fuente, token);
                // Se espera al trabajo real (sin token en Task.Run ni WaitAsync): nunca queda una tarea abandonada.
                texto = await Task.Run(() => Extraer(formato, lectura, token), CancellationToken.None);
            }

            // Una biblioteca podría tragarse la excepción de una lectura cancelada y devolver un resultado parcial.
            token.ThrowIfCancellationRequested();
            return Clasificar(documentoId, texto);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new OperationCanceledException("Extracción cancelada por el llamador.", ex, cancellationToken);
        }
        catch (Exception) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("La extracción del documento {DocumentoId} superó el tiempo máximo de {TimeoutSeconds} s y se interrumpió.",
                documentoId, _timeout.TotalSeconds);
            return Resultado(documentoId, ExtractionStatus.ExtractionFailed);
        }
        catch (Exception ex) when (EsContenidoInvalido(ex))
        {
            _logger.LogWarning("El documento {DocumentoId} está dañado, cifrado o no es legible. Error: {TipoError}.", documentoId, ex.GetType().Name);
            return Resultado(documentoId, ExtractionStatus.InvalidContent);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al extraer el texto del documento {DocumentoId}. Error: {TipoError}.", documentoId, ex.GetType().Name);
            return Resultado(documentoId, ExtractionStatus.ExtractionFailed);
        }
        finally
        {
            if (archivo != null)
            {
                await archivo.DisposeAsync();
            }
        }
    }

    private static async Task<Stream> CopiarAMemoriaAsync(Stream origen, CancellationToken token)
    {
        var memoria = new MemoryStream();
        await origen.CopyToAsync(memoria, token);
        memoria.Position = 0;
        return memoria;
    }

    private static ExtractionResult Clasificar(Guid documentoId, string texto)
    {
        if (texto.Length > LimiteCaracteres)
        {
            return Resultado(documentoId, ExtractionStatus.ContextExceeded);
        }

        return string.IsNullOrWhiteSpace(texto)
            ? Resultado(documentoId, ExtractionStatus.Empty)
            : new ExtractionResult(documentoId, ExtractionStatus.Success, texto);
    }

    private static ExtractionResult Resultado(Guid documentoId, ExtractionStatus estado) => new(documentoId, estado);

    private static Formato Clasificar(string? contentType)
    {
        var tipo = (contentType ?? string.Empty).Split(';')[0].Trim();
        return tipo.ToLowerInvariant() switch
        {
            MimePdf => Formato.Pdf,
            MimeDocx => Formato.Docx,
            MimeXlsx => Formato.Xlsx,
            MimeTxt => Formato.Txt,
            _ => Formato.NoSoportado
        };
    }

    private static string Extraer(Formato formato, Stream fuente, CancellationToken token) => formato switch
    {
        Formato.Pdf => ExtraerPdf(fuente, token),
        Formato.Docx => ExtraerDocx(fuente, token),
        Formato.Xlsx => ExtraerXlsx(fuente, token),
        _ => throw new InvalidOperationException("Formato no soportado.")
    };

    /// <summary>
    /// UTF-8 estricto (con o sin BOM) y sin bytes NUL, como en la validación de subida de la Fase 7.2. Solo se leen los
    /// bytes necesarios para detectar el exceso de caracteres, nunca el archivo completo.
    /// </summary>
    private static async Task<string> LeerTxtAsync(Stream archivo, CancellationToken token)
    {
        var buffer = new byte[MaxBytesTxt];
        var total = 0;
        int leidos;
        while (total < buffer.Length && (leidos = await archivo.ReadAsync(buffer.AsMemory(total), token)) > 0)
        {
            total += leidos;
        }

        var truncado = total == buffer.Length && await archivo.ReadAsync(new byte[1], token) > 0;
        if (buffer.AsSpan(0, total).Contains((byte)0))
        {
            throw new ContenidoIlegibleException();
        }

        var inicio = buffer.AsSpan(0, total).StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        var estricto = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            // Sin flush cuando se corta: un carácter multibyte partido en el corte no es UTF-8 inválido.
            var decoder = estricto.GetDecoder();
            var caracteres = new char[estricto.GetMaxCharCount(total - inicio)];
            var n = decoder.GetChars(buffer, inicio, total - inicio, caracteres, 0, flush: !truncado);
            return new string(caracteres, 0, n);
        }
        catch (DecoderFallbackException)
        {
            throw new ContenidoIlegibleException();
        }
    }

    private static string ExtraerPdf(Stream fuente, CancellationToken token)
    {
        using var documento = PdfDocument.Open(fuente);
        if (documento.NumberOfPages == 0)
        {
            throw new ContenidoIlegibleException();
        }

        var texto = new StringBuilder();
        for (var numero = 1; numero <= documento.NumberOfPages; numero++)
        {
            token.ThrowIfCancellationRequested();
            if (texto.Length > 0)
            {
                texto.Append("\n\n");
            }

            texto.Append(ContentOrderTextExtractor.GetText(documento.GetPage(numero)));
            if (texto.Length > LimiteCaracteres)
            {
                break;
            }
        }

        return texto.ToString();
    }

    private static OpenSettings ConfiguracionOoxml() => new() { MaxCharactersInPart = MaxCaracteresPorParteOoxml };

    private static string ExtraerDocx(Stream fuente, CancellationToken token)
    {
        using var documento = WordprocessingDocument.Open(fuente, false, ConfiguracionOoxml());
        var cuerpo = documento.MainDocumentPart?.Document?.Body ?? throw new ContenidoIlegibleException();

        var texto = new StringBuilder();
        // Párrafos del cuerpo, incluidos los de las tablas.
        foreach (var parrafo in cuerpo.Descendants<WordParagraph>())
        {
            token.ThrowIfCancellationRequested();
            texto.Append(parrafo.InnerText).Append('\n');
            if (texto.Length > LimiteCaracteres)
            {
                break;
            }
        }

        return texto.ToString();
    }

    /// <summary>Por hoja y fila, celdas separadas por tabulador; fórmulas: valor guardado en caché.</summary>
    private static string ExtraerXlsx(Stream fuente, CancellationToken token)
    {
        using var documento = SpreadsheetDocument.Open(fuente, false, ConfiguracionOoxml());
        var libro = documento.WorkbookPart ?? throw new ContenidoIlegibleException();
        var cadenas = libro.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList()
            ?? [];

        var texto = new StringBuilder();
        var hayValores = false;
        foreach (var hoja in libro.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (hoja.Id?.Value is not { } relacion || libro.GetPartById(relacion) is not WorksheetPart parte)
            {
                continue;
            }

            texto.Append("[Hoja: ").Append(hoja.Name?.Value).Append("]\n");
            foreach (var fila in parte.Worksheet?.Descendants<Row>() ?? [])
            {
                token.ThrowIfCancellationRequested();
                var valores = fila.Elements<Cell>().Select(c => ValorCelda(c, cadenas)).ToList();
                if (valores.Any(v => v.Length > 0))
                {
                    hayValores = true;
                    texto.AppendJoin('\t', valores).Append('\n');
                }

                if (texto.Length > LimiteCaracteres)
                {
                    return texto.ToString();
                }
            }
        }

        // Un libro sin ninguna celda con valor no tiene texto (solo cabeceras de hoja).
        return hayValores ? texto.ToString() : string.Empty;
    }

    private static string ValorCelda(Cell celda, IReadOnlyList<string> cadenas)
    {
        var valor = celda.CellValue?.Text ?? string.Empty;
        var tipo = celda.DataType?.Value;
        if (tipo == CellValues.SharedString)
        {
            return int.TryParse(valor, out var indice) && indice >= 0 && indice < cadenas.Count ? cadenas[indice] : string.Empty;
        }

        if (tipo == CellValues.InlineString)
        {
            return celda.InlineString?.InnerText ?? string.Empty;
        }

        if (tipo == CellValues.Boolean)
        {
            return valor == "1" ? "TRUE" : "FALSE";
        }

        return valor;
    }

    /// <summary>
    /// Archivo dañado, cifrado o que no corresponde a su formato. Las excepciones de PdfPig y del Open XML SDK por
    /// formato inválido; cualquier otra cosa es ExtractionFailed.
    /// </summary>
    private static bool EsContenidoInvalido(Exception ex) => ex is ContenidoIlegibleException
        or InvalidDataException
        or XmlException
        or OpenXmlPackageException
        or FileFormatException
        or UglyToad.PdfPig.Core.PdfDocumentFormatException
        or UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException;

    private sealed class ContenidoIlegibleException : Exception;

    /// <summary>
    /// Vista de solo lectura sobre el archivo que comprueba el token en cada lectura y reposicionamiento. Es el punto por
    /// el que la cancelación (timeout o llamador) llega dentro de PdfPig y del Open XML SDK. No cierra el stream interno.
    /// </summary>
    internal sealed class StreamCancelable(Stream interno, CancellationToken token) : Stream
    {
        public override bool CanRead => interno.CanRead;
        public override bool CanSeek => interno.CanSeek;
        public override bool CanWrite => false;
        public override long Length => interno.Length;

        public override long Position
        {
            get => interno.Position;
            set
            {
                token.ThrowIfCancellationRequested();
                interno.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        /// <summary>
        /// Completa la lectura hasta lo pedido o el fin del archivo (PdfPig no tolera lecturas parciales, que un
        /// almacenamiento distinto del disco local podría devolver), comprobando el token en cada vuelta.
        /// </summary>
        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                token.ThrowIfCancellationRequested();
                var leidos = interno.Read(buffer[total..]);
                if (leidos == 0)
                {
                    break;
                }

                total += leidos;
            }

            return total;
        }

        public override int ReadByte()
        {
            token.ThrowIfCancellationRequested();
            return interno.ReadByte();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            token.ThrowIfCancellationRequested();
            return interno.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            token.ThrowIfCancellationRequested();
            return interno.Seek(offset, origin);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
