using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Helpers;
using AsistenteJuridico.Application.Common.Indexacion;
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
using WordStyle = DocumentFormat.OpenXml.Wordprocessing.Style;
using WordTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using WordTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Fase 6.X (H10) — Extracción de texto real: TXT (UTF-8 estricto), PDF (PdfPig), DOCX y XLSX (Open XML SDK).
/// DOC, XLS, JPG y PNG no tienen extracción (sin OCR). Nunca devuelve texto de respaldo: cualquier problema es un
/// <see cref="ExtractionStatus"/> distinto de Success. La cancelación del llamador se propaga sin alterar.
/// Los logs incluyen el id del documento y el tipo de error, nunca la ruta ni el contenido.
///
/// FASE 8.2 (contrato 8.2 §6): un ÚNICO recorrido por formato (PdfPig / Open XML) alimenta dos composiciones:
/// <see cref="ComposicionChat"/>, que reproduce carácter a carácter el texto de la 6.X para <see cref="ExtractAsync"/>,
/// y <see cref="ComposicionSegmentos"/>, que produce los segmentos con ubicación de <see cref="ExtractSegmentsAsync"/>.
/// Las defensas (almacenamiento seguro, StreamCancelable, timeout, límites, clasificación de errores) son las mismas.
///
/// TIMEOUT: ni PdfPig ni el Open XML SDK aceptan un CancellationToken. Por eso las bibliotecas leen el archivo a
/// través de <see cref="StreamCancelable"/>, que comprueba el token en CADA lectura o reposicionamiento: al vencer el
/// timeout, la siguiente lectura de la biblioteca lanza y el análisis se interrumpe desde dentro. Además se comprueba
/// el token entre páginas, párrafos y filas. La llamada espera a que el trabajo termine de verdad (no hay WaitAsync que
/// abandone una tarea en segundo plano): cuando la extracción devuelve, ya no queda análisis en ejecución.
///
/// LIMITACIÓN EXPLÍCITA: el procesamiento en memoria entre dos puntos de comprobación (p. ej., descomprimir y
/// analizar UN flujo de contenido ya leído) no es interrumpible; el exceso sobre el timeout queda acotado a ese tramo,
/// y el archivo ya está limitado a 25 MiB en la subida.
/// </summary>
public sealed partial class DocumentTextExtractor : IDocumentTextExtractor
{
    private const string MimePdf = "application/pdf";
    private const string MimeDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string MimeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string MimeTxt = "text/plain";

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
        var composicion = new ComposicionChat(ContextWindowValidator.MaxDocumentCharacters);
        var (estado, _) = await ExtraerAsync(
            documentoId, rutaAlmacenamiento, contentType, composicion, _timeout, calcularHash: false, cancellationToken);

        return estado == ExtractionStatus.Success
            ? new ExtractionResult(documentoId, ExtractionStatus.Success, composicion.TextoFinal)
            : Resultado(documentoId, estado);
    }

    public async Task<SegmentedExtractionResult> ExtractSegmentsAsync(
        Guid documentoId,
        string? rutaAlmacenamiento,
        string? contentType,
        ExtractionProfile perfil,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(perfil);

        var composicion = new ComposicionSegmentos(perfil.MaxCaracteres);
        var (estado, hash) = await ExtraerAsync(
            documentoId, rutaAlmacenamiento, contentType, composicion, TimeSpan.FromSeconds(perfil.TimeoutSeconds),
            calcularHash: true, cancellationToken);

        return estado == ExtractionStatus.Success
            ? new SegmentedExtractionResult(documentoId, ExtractionStatus.Success, composicion.Segmentos, hash)
            : new SegmentedExtractionResult(documentoId, estado, [], null);
    }

    /// <summary>
    /// Flujo común: formato, apertura segura del archivo, timeout, recorrido y clasificación de errores. Con
    /// <paramref name="calcularHash"/>, el archivo se lee entero en memoria y se analiza esa misma copia, así el
    /// SHA-256 corresponde exactamente a los bytes analizados.
    /// </summary>
    private async Task<(ExtractionStatus Estado, string? Hash)> ExtraerAsync(
        Guid documentoId,
        string? rutaAlmacenamiento,
        string? contentType,
        Composicion composicion,
        TimeSpan timeout,
        bool calcularHash,
        CancellationToken cancellationToken)
    {
        var formato = Clasificar(contentType);
        if (formato == Formato.NoSoportado)
        {
            return (ExtractionStatus.UnsupportedFormat, null);
        }

        if (string.IsNullOrWhiteSpace(rutaAlmacenamiento))
        {
            _logger.LogError("[DOCUMENT_FILE_NOT_FOUND] El documento {DocumentoId} no tiene archivo físico asociado.", documentoId);
            return (ExtractionStatus.FileNotFound, null);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
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
                return (ExtractionStatus.FileNotFound, null);
            }
            catch (ValidationException)
            {
                // Ruta vacía o inválida guardada en la base de datos: no hay archivo utilizable.
                _logger.LogError("[DOCUMENT_FILE_NOT_FOUND] El documento {DocumentoId} tiene una ruta de almacenamiento inválida.", documentoId);
                return (ExtractionStatus.FileNotFound, null);
            }
            catch (ForbiddenException)
            {
                // Ruta insegura, symlink o punto de reanálisis: la operación se aborta (nunca se continúa).
                _logger.LogError("[AI_DOCUMENT_STORAGE_FORBIDDEN] El almacenamiento rechazó la ruta del documento {DocumentoId}.", documentoId);
                return (ExtractionStatus.Forbidden, null);
            }

            string? hash = null;
            if (formato == Formato.Txt)
            {
                var (texto, bytes, total) = await LeerTxtAsync(archivo, composicion.Limite, token);
                composicion.Texto(texto);
                if (calcularHash)
                {
                    hash = Sha256(bytes, total);
                }
            }
            else
            {
                Stream fuente;
                if (calcularHash)
                {
                    var copia = await CopiarAMemoriaAsync(archivo, token);
                    hash = Sha256(copia.GetBuffer(), (int)copia.Length);
                    fuente = copia;
                }
                else
                {
                    fuente = archivo.CanSeek ? archivo : await CopiarAMemoriaAsync(archivo, token);
                }

                var lectura = new StreamCancelable(fuente, token);
                // Se espera al trabajo real (sin token en Task.Run ni WaitAsync): nunca queda una tarea abandonada.
                await Task.Run(() => Recorrer(formato, lectura, composicion, token), CancellationToken.None);
            }

            // Una biblioteca podría tragarse la excepción de una lectura cancelada y devolver un resultado parcial.
            token.ThrowIfCancellationRequested();
            var estado = composicion.Clasificar();
            return (estado, estado == ExtractionStatus.Success ? hash : null);
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
                documentoId, timeout.TotalSeconds);
            return (ExtractionStatus.ExtractionFailed, null);
        }
        catch (Exception ex) when (EsContenidoInvalido(ex))
        {
            _logger.LogWarning("El documento {DocumentoId} está dañado, cifrado o no es legible. Error: {TipoError}.", documentoId, ex.GetType().Name);
            return (ExtractionStatus.InvalidContent, null);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error al extraer el texto del documento {DocumentoId}. Error: {TipoError}.", documentoId, ex.GetType().Name);
            return (ExtractionStatus.ExtractionFailed, null);
        }
        finally
        {
            if (archivo != null)
            {
                await archivo.DisposeAsync();
            }
        }
    }

    private static string Sha256(byte[] bytes, int longitud) => Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, longitud)));

    private static async Task<MemoryStream> CopiarAMemoriaAsync(Stream origen, CancellationToken token)
    {
        var memoria = new MemoryStream();
        await origen.CopyToAsync(memoria, token);
        memoria.Position = 0;
        return memoria;
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

    private static void Recorrer(Formato formato, Stream fuente, Composicion composicion, CancellationToken token)
    {
        switch (formato)
        {
            case Formato.Pdf:
                RecorrerPdf(fuente, composicion, token);
                break;
            case Formato.Docx:
                RecorrerDocx(fuente, composicion, token);
                break;
            case Formato.Xlsx:
                RecorrerXlsx(fuente, composicion, token);
                break;
            default:
                throw new InvalidOperationException("Formato no soportado.");
        }
    }

    /// <summary>
    /// UTF-8 estricto (con o sin BOM) y sin bytes NUL, como en la validación de subida de la Fase 7.2. Solo se leen los
    /// bytes necesarios para detectar el exceso de caracteres ((límite + 1) caracteres de hasta 4 bytes, más el BOM),
    /// nunca el archivo completo. Devuelve también los bytes leídos (el archivo entero cuando no se superó el límite).
    /// </summary>
    private static async Task<(string Texto, byte[] Bytes, int Total)> LeerTxtAsync(Stream archivo, int limiteCaracteres, CancellationToken token)
    {
        var maxBytes = (int)Math.Min(((long)limiteCaracteres + 1) * 4 + 3, int.MaxValue);
        if (archivo.CanSeek)
        {
            // Mismo comportamiento con un búfer menor: un archivo que cabe se lee entero y nunca se considera truncado.
            maxBytes = (int)Math.Min(maxBytes, archivo.Length - archivo.Position + 1);
        }

        var buffer = new byte[maxBytes];
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
            return (new string(caracteres, 0, n), buffer, total);
        }
        catch (DecoderFallbackException)
        {
            throw new ContenidoIlegibleException();
        }
    }

    private static void RecorrerPdf(Stream fuente, Composicion composicion, CancellationToken token)
    {
        using var documento = PdfDocument.Open(fuente);
        if (documento.NumberOfPages == 0)
        {
            throw new ContenidoIlegibleException();
        }

        for (var numero = 1; numero <= documento.NumberOfPages; numero++)
        {
            token.ThrowIfCancellationRequested();
            composicion.Pagina(numero, ContentOrderTextExtractor.GetText(documento.GetPage(numero)));
            if (composicion.Excedido)
            {
                break;
            }
        }
    }

    private static OpenSettings ConfiguracionOoxml() => new() { MaxCharactersInPart = MaxCaracteresPorParteOoxml };

    private static void RecorrerDocx(Stream fuente, Composicion composicion, CancellationToken token)
    {
        using var documento = WordprocessingDocument.Open(fuente, false, ConfiguracionOoxml());
        var cuerpo = documento.MainDocumentPart?.Document?.Body ?? throw new ContenidoIlegibleException();

        composicion.IniciarDocx(documento.MainDocumentPart);
        // Párrafos del cuerpo, incluidos los de las tablas.
        foreach (var parrafo in cuerpo.Descendants<WordParagraph>())
        {
            token.ThrowIfCancellationRequested();
            composicion.Parrafo(parrafo);
            if (composicion.Excedido)
            {
                break;
            }
        }

        composicion.FinDocx();
    }

    /// <summary>Por hoja y fila; fórmulas: valor guardado en caché.</summary>
    private static void RecorrerXlsx(Stream fuente, Composicion composicion, CancellationToken token)
    {
        using var documento = SpreadsheetDocument.Open(fuente, false, ConfiguracionOoxml());
        var libro = documento.WorkbookPart ?? throw new ContenidoIlegibleException();
        var cadenas = libro.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList()
            ?? [];

        foreach (var hoja in libro.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (hoja.Id?.Value is not { } relacion || libro.GetPartById(relacion) is not WorksheetPart parte)
            {
                continue;
            }

            composicion.Hoja(hoja.Name?.Value);
            var ordinal = 0;
            foreach (var fila in parte.Worksheet?.Descendants<Row>() ?? [])
            {
                token.ThrowIfCancellationRequested();
                ordinal++;
                var valores = fila.Elements<Cell>().Select(c => ValorCelda(c, cadenas)).ToList();
                var numero = fila.RowIndex?.Value is { } indice ? (int)Math.Min(indice, int.MaxValue) : ordinal;
                composicion.Fila(numero, valores);
                if (composicion.Excedido)
                {
                    composicion.FinXlsx(retornoTemprano: true);
                    return;
                }
            }
        }

        composicion.FinXlsx(retornoTemprano: false);
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

    // ── Composiciones del recorrido ──────────────────────────────────────

    /// <summary>Recibe las unidades del recorrido en orden de documento. <see cref="Excedido"/> detiene el recorrido.</summary>
    private abstract class Composicion(int limite)
    {
        public int Limite { get; } = limite;
        public abstract bool Excedido { get; }
        public abstract void Texto(string texto);
        public abstract void Pagina(int numero, string texto);
        public virtual void IniciarDocx(MainDocumentPart parte) { }
        public abstract void Parrafo(WordParagraph parrafo);
        public virtual void FinDocx() { }
        public abstract void Hoja(string? nombre);
        public abstract void Fila(int numero, IReadOnlyList<string> valores);
        public virtual void FinXlsx(bool retornoTemprano) { }
        public abstract ExtractionStatus Clasificar();
    }

    /// <summary>Texto de la 6.X, carácter a carácter (contrato 8.2 §6.6, A2).</summary>
    private sealed class ComposicionChat(int limite) : Composicion(limite)
    {
        private readonly StringBuilder _texto = new();
        private bool _hayValores;

        public string? TextoFinal { get; private set; }

        public override bool Excedido => _texto.Length > Limite;

        public override void Texto(string texto) => _texto.Append(texto);

        public override void Pagina(int numero, string texto)
        {
            if (_texto.Length > 0)
            {
                _texto.Append("\n\n");
            }

            _texto.Append(texto);
        }

        public override void Parrafo(WordParagraph parrafo) => _texto.Append(parrafo.InnerText).Append('\n');

        public override void Hoja(string? nombre) => _texto.Append("[Hoja: ").Append(nombre).Append("]\n");

        /// <summary>Celdas separadas por tabulador; solo las filas con algún valor.</summary>
        public override void Fila(int numero, IReadOnlyList<string> valores)
        {
            if (valores.Any(v => v.Length > 0))
            {
                _hayValores = true;
                _texto.AppendJoin('\t', valores).Append('\n');
            }
        }

        /// <summary>Un libro sin ninguna celda con valor no tiene texto (solo cabeceras de hoja).</summary>
        public override void FinXlsx(bool retornoTemprano)
        {
            if (!retornoTemprano && !_hayValores)
            {
                _texto.Clear();
            }
        }

        public override ExtractionStatus Clasificar()
        {
            var texto = _texto.ToString();
            if (texto.Length > Limite)
            {
                return ExtractionStatus.ContextExceeded;
            }

            if (string.IsNullOrWhiteSpace(texto))
            {
                return ExtractionStatus.Empty;
            }

            TextoFinal = texto;
            return ExtractionStatus.Success;
        }
    }

    /// <summary>
    /// Segmentos con ubicación (contrato 8.2 §6.2). El límite del perfil se aplica sobre la longitud que tendría el
    /// texto de la composición Chat (§6.3), así el perfil Chat da el mismo ContextExceeded por ambos métodos.
    /// </summary>
    private sealed partial class ComposicionSegmentos(int limite) : Composicion(limite)
    {
        private readonly List<TextSegment> _segmentos = [];
        private long _longitudChat;

        // DOCX
        private readonly Dictionary<string, string> _nombresEstilo = new(StringComparer.Ordinal);
        private readonly SortedDictionary<int, string> _titulos = [];
        private int _unidad;
        private WordTableRow? _filaActual;
        private WordTableCell? _celdaActual;
        private int _unidadFila;
        private string? _rutaFila;
        private List<List<string>> _celdas = [];

        // XLSX
        private string _hoja = string.Empty;

        public IReadOnlyList<TextSegment> Segmentos => _segmentos;

        public override bool Excedido => _longitudChat > Limite;

        public override void Texto(string texto)
        {
            ExigirUtf16BienFormado(texto);
            _longitudChat += texto.Length;
            if (!string.IsNullOrWhiteSpace(texto))
            {
                var lineas = texto.Count(c => c == '\n') + 1;
                Agregar(texto, UbicacionSegmento.Lineas, 1, lineas, lineas == 1 ? "línea 1" : $"líneas 1{Raya}{lineas}", null);
            }
        }

        public override void Pagina(int numero, string texto)
        {
            ExigirUtf16BienFormado(texto);
            _longitudChat += (_longitudChat > 0 ? 2 : 0) + texto.Length;
            if (!string.IsNullOrWhiteSpace(texto))
            {
                Agregar(texto, UbicacionSegmento.Paginas, numero, numero, $"página {numero}", null);
            }
        }

        public override void IniciarDocx(MainDocumentPart parte)
        {
            foreach (var estilo in parte.StyleDefinitionsPart?.Styles?.Elements<WordStyle>() ?? [])
            {
                if (estilo.StyleId?.Value is { } id && estilo.StyleName?.Val?.Value is { } nombre)
                {
                    _nombresEstilo.TryAdd(id, nombre);
                }
            }
        }

        /// <summary>
        /// Unidad de bloque: cada párrafo fuera de tablas y cada fila de tabla (la más externa), con las celdas unidas
        /// con " | " y los párrafos de una celda (incluidas las tablas anidadas) unidos con un espacio.
        /// </summary>
        public override void Parrafo(WordParagraph parrafo)
        {
            var texto = parrafo.InnerText;
            ExigirUtf16BienFormado(texto);
            _longitudChat += texto.Length + 1;

            var fila = parrafo.Ancestors<WordTableRow>().LastOrDefault();
            if (fila is null)
            {
                CerrarFila();
                _unidad++;
                if (NivelTitulo(parrafo) is { } nivel)
                {
                    var titulo = TextoNormalizador.Normalizar(texto);
                    if (!string.IsNullOrWhiteSpace(titulo))
                    {
                        foreach (var mayor in _titulos.Keys.Where(k => k >= nivel).ToList())
                        {
                            _titulos.Remove(mayor);
                        }

                        _titulos[nivel] = titulo;
                    }
                }

                if (!string.IsNullOrWhiteSpace(texto))
                {
                    Agregar(texto, UbicacionSegmento.Parrafos, _unidad, _unidad, $"párrafo {_unidad}", Ruta());
                }

                return;
            }

            if (!ReferenceEquals(fila, _filaActual))
            {
                CerrarFila();
                _filaActual = fila;
                _celdaActual = null;
                _celdas = [];
                _unidad++;
                _unidadFila = _unidad;
                _rutaFila = Ruta();
            }

            var celda = parrafo.Ancestors<WordTableCell>().Last();
            if (!ReferenceEquals(celda, _celdaActual))
            {
                _celdaActual = celda;
                _celdas.Add([]);
            }

            _celdas[^1].Add(texto);
        }

        public override void FinDocx() => CerrarFila();

        private void CerrarFila()
        {
            if (_filaActual is null)
            {
                return;
            }

            var celdas = _celdas.Select(c => string.Join(' ', c)).ToList();
            if (celdas.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                Agregar(string.Join(" | ", celdas), UbicacionSegmento.Parrafos, _unidadFila, _unidadFila, $"párrafo {_unidadFila}", _rutaFila);
            }

            _filaActual = null;
            _celdaActual = null;
        }

        private string? Ruta() => _titulos.Count == 0 ? null : Fragmentador.ComponerRutaSeccion([.. _titulos.Values]);

        /// <summary>Título de nivel 1–9 por StyleId o nombre de estilo: "Heading n", "Título n", "Titulo n" o "Ttulo n" (con o sin espacio).</summary>
        private int? NivelTitulo(WordParagraph parrafo)
        {
            var id = parrafo.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (id is null)
            {
                return null;
            }

            var coincidencia = PatronTitulo().Match(id);
            if (!coincidencia.Success && _nombresEstilo.TryGetValue(id, out var nombre))
            {
                coincidencia = PatronTitulo().Match(nombre);
            }

            return coincidencia.Success ? coincidencia.Groups[1].Value[0] - '0' : null;
        }

        [GeneratedRegex(@"^(?:heading|t[ií]tulo|ttulo)\s*([1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex PatronTitulo();

        public override void Hoja(string? nombre)
        {
            ExigirUtf16BienFormado(nombre ?? string.Empty);
            _longitudChat += "[Hoja: ".Length + (nombre?.Length ?? 0) + "]\n".Length;
            _hoja = nombre ?? string.Empty;
        }

        public override void Fila(int numero, IReadOnlyList<string> valores)
        {
            if (!valores.Any(v => v.Length > 0))
            {
                return;
            }

            foreach (var valor in valores)
            {
                ExigirUtf16BienFormado(valor);
            }

            _longitudChat += valores.Sum(v => (long)v.Length) + Math.Max(0, valores.Count - 1) + 1;
            var texto = string.Join('\t', valores);
            if (!string.IsNullOrWhiteSpace(texto))
            {
                Agregar(texto, UbicacionSegmento.Hoja, numero, numero, "hoja " + _hoja, null);
            }
        }

        public override ExtractionStatus Clasificar() =>
            Excedido ? ExtractionStatus.ContextExceeded
            : _segmentos.Count == 0 ? ExtractionStatus.Empty
            : ExtractionStatus.Success;

        /// <summary>
        /// UTF-16 mal formado (un sustituto aislado, posible en el texto de un PDF): contenido inválido. Nunca se repara
        /// ni se sustituye; la extracción termina en InvalidContent por la vía de los archivos ilegibles.
        /// </summary>
        private static void ExigirUtf16BienFormado(string texto)
        {
            if (!TextoNormalizador.EsUtf16BienFormado(texto))
            {
                throw new ContenidoIlegibleException();
            }
        }

        private void Agregar(string texto, string tipo, int desde, int hasta, string etiqueta, string? ruta) =>
            _segmentos.Add(new TextSegment(texto, new UbicacionSegmento(tipo, desde, hasta, etiqueta), ruta));

        private static readonly string Raya = char.ConvertFromUtf32(0x2013);
    }

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
