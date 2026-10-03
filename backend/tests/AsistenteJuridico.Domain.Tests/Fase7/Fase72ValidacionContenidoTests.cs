using System.Text;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Infrastructure.Services;
using static AsistenteJuridico.Domain.Tests.Fase7.Fase72TestData;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.2 — Extensión, MIME, Magic Bytes, estructura OOXML, TXT UTF-8 estricto y saneado del nombre original.
/// </summary>
public class Fase72ValidacionContenidoTests
{
    private static async Task ValidarAsync(string extension, byte[] contenido)
    {
        var formato = DocumentoContentValidator.ObtenerFormato(extension);
        await using var stream = new MemoryStream(contenido);
        await DocumentoContentValidator.ValidarContenidoAsync(formato, stream);
    }

    private static async Task AssertContenidoInvalidoAsync(string extension, byte[] contenido)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => ValidarAsync(extension, contenido));
        Assert.Equal(DocumentoErrorCodes.MagicBytesInvalid, ex.ErrorCode);
        Assert.NotEmpty(ex.ValidationErrors);
    }

    // ── Extensiones ──────────────────────────────────────────────────────

    [Fact]
    public void ExtensionesPermitidas_SonExactamenteLasDelContrato()
    {
        Assert.Equal(
            [".doc", ".docx", ".jpeg", ".jpg", ".pdf", ".png", ".txt", ".xls", ".xlsx"],
            DocumentoContentValidator.ExtensionesPermitidas.Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".js")]
    [InlineData(".html")]
    [InlineData(".svg")]
    [InlineData(".docm")]
    [InlineData(".zip")]
    [InlineData("")]
    public void ExtensionNoPermitida_415(string extension)
    {
        var ex = Assert.Throws<UnsupportedMediaTypeException>(() => DocumentoContentValidator.ObtenerFormato(extension));
        Assert.Equal(DocumentoErrorCodes.TypeNotAllowed, ex.ErrorCode);
    }

    [Theory]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".PDF", "application/pdf")]
    [InlineData(".docx", MimeDocx)]
    [InlineData(".xlsx", MimeXlsx)]
    [InlineData(".doc", "application/msword")]
    [InlineData(".xls", "application/vnd.ms-excel")]
    [InlineData(".txt", "text/plain; charset=utf-8")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    public void ContentTypeCanonico_PorExtension(string extension, string esperado)
    {
        Assert.Equal(esperado, DocumentoContentValidator.ObtenerFormato(extension).ContentTypeCanonico);
    }

    // ── MIME declarado ───────────────────────────────────────────────────

    [Theory]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".pdf", "application/x-pdf")]
    [InlineData(".pdf", "APPLICATION/PDF")]
    [InlineData(".pdf", "")]
    [InlineData(".pdf", null)]
    [InlineData(".pdf", "application/octet-stream")]
    [InlineData(".docx", MimeDocx)]
    [InlineData(".xlsx", MimeXlsx)]
    [InlineData(".txt", "text/plain")]
    [InlineData(".txt", "text/plain; charset=iso-8859-1")]
    [InlineData(".jpg", "image/pjpeg")]
    [InlineData(".png", "image/png")]
    public void MimeCompatible_Aceptado(string extension, string? mime)
    {
        DocumentoContentValidator.ValidarMimeDeclarado(DocumentoContentValidator.ObtenerFormato(extension), mime);
    }

    [Theory]
    [InlineData(".pdf", "text/html")]
    [InlineData(".pdf", "image/png")]
    [InlineData(".docx", "application/zip")]
    [InlineData(".docx", "application/msword")]
    [InlineData(".png", "image/jpeg")]
    [InlineData(".txt", "application/x-msdownload")]
    public void MimeContradictorio_415(string extension, string mime)
    {
        var ex = Assert.Throws<UnsupportedMediaTypeException>(() =>
            DocumentoContentValidator.ValidarMimeDeclarado(DocumentoContentValidator.ObtenerFormato(extension), mime));
        Assert.Equal(DocumentoErrorCodes.TypeNotAllowed, ex.ErrorCode);
    }

    // ── Magic Bytes ──────────────────────────────────────────────────────

    public static TheoryData<string, byte[]> ContenidosValidos => new()
    {
        { ".pdf", Pdf() },
        { ".png", Png },
        { ".jpg", Jpeg },
        { ".jpeg", Jpeg },
        { ".doc", Ole },
        { ".xls", Ole },
        { ".docx", Docx() },
        { ".xlsx", Xlsx() },
        { ".txt", Encoding.UTF8.GetBytes("Demanda de alimentos — señor juez, año 2026") }
    };

    [Theory]
    [MemberData(nameof(ContenidosValidos))]
    public async Task ContenidoValido_Aceptado(string extension, byte[] contenido)
    {
        await ValidarAsync(extension, contenido);
    }

    public static TheoryData<string, byte[]> MagicBytesCruzados => new()
    {
        { ".pdf", [0x4D, 0x5A, 0x90, 0x00] },   // ejecutable PE con extensión .pdf
        { ".pdf", Png },
        { ".png", Jpeg },
        { ".jpg", Pdf() },
        { ".doc", Pdf() },
        { ".xls", Docx() },
        { ".docx", Ole },
        { ".xlsx", Pdf() },
        { ".pdf", [0x25, 0x50] }               // más corto que la firma
    };

    [Theory]
    [MemberData(nameof(MagicBytesCruzados))]
    public async Task MagicBytesNoCorresponden_400(string extension, byte[] contenido)
    {
        await AssertContenidoInvalidoAsync(extension, contenido);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".txt")]
    [InlineData(".png")]
    public async Task ArchivoVacio_400(string extension)
    {
        await AssertContenidoInvalidoAsync(extension, []);
    }

    // ── OOXML ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ZipQueNoEsOoxml_400()
    {
        await AssertContenidoInvalidoAsync(".docx", ZipNoOoxml);
        await AssertContenidoInvalidoAsync(".xlsx", ZipNoOoxml);
    }

    [Fact]
    public async Task ZipCorrupto_400()
    {
        await AssertContenidoInvalidoAsync(".docx", ZipCorrupto);
        await AssertContenidoInvalidoAsync(".xlsx", ZipCorrupto);
    }

    [Fact]
    public async Task OoxmlDeOtroTipo_400()
    {
        // Un XLSX renombrado a .docx (y al revés) no tiene la carpeta del formato
        await AssertContenidoInvalidoAsync(".docx", Xlsx());
        await AssertContenidoInvalidoAsync(".xlsx", Docx());
    }

    [Fact]
    public async Task OoxmlConVbaProject_400()
    {
        await AssertContenidoInvalidoAsync(".docx", Docx(conMacros: true));
        await AssertContenidoInvalidoAsync(".xlsx", Xlsx(conMacros: true));
        await AssertContenidoInvalidoAsync(".docx", Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w/>"), ("customXml/VBAPROJECT.BIN", "x")));
    }

    // ── TXT UTF-8 estricto ───────────────────────────────────────────────

    [Fact]
    public async Task TxtUtf8_ConYSinBom_Aceptado()
    {
        await ValidarAsync(".txt", Encoding.UTF8.GetBytes("Escrito: ñandú, acción, €"));
        await ValidarAsync(".txt", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Con BOM: acción")]);
    }

    [Fact]
    public async Task TxtConNul_400()
    {
        await AssertContenidoInvalidoAsync(".txt", [.. Encoding.UTF8.GetBytes("texto"), 0x00, .. Encoding.UTF8.GetBytes("oculto")]);
    }

    [Fact]
    public async Task TxtLatin1_NoEsUtf8Valido_400()
    {
        // "acción" en ISO-8859-1: 0xF3 aislado no es una secuencia UTF-8 válida
        await AssertContenidoInvalidoAsync(".txt", Encoding.Latin1.GetBytes("acción"));
    }

    [Fact]
    public async Task TxtConSecuenciaUtf8Truncada_400()
    {
        // Primer byte de "€" (E2 82 AC) sin sus continuaciones, al final del archivo
        await AssertContenidoInvalidoAsync(".txt", [.. Encoding.UTF8.GetBytes("precio "), 0xE2, 0x82]);
    }

    [Fact]
    public async Task TxtUtf8Grande_ValidadoPorBloques()
    {
        // Más de un bloque de lectura, con caracteres multibyte cruzando los límites de bloque
        var texto = string.Concat(Enumerable.Repeat("señoría ñ € ", 20000));
        await ValidarAsync(".txt", Encoding.UTF8.GetBytes(texto));
    }

    // ── Saneado del nombre original ──────────────────────────────────────

    [Theory]
    [InlineData("demanda.pdf", "demanda.pdf")]
    [InlineData("C:\\Users\\abogado\\Escritorio\\demanda.pdf", "demanda.pdf")]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("/var/www/../demanda.pdf", "demanda.pdf")]
    [InlineData("  escrito final.pdf  ", "escrito final.pdf")]
    [InlineData("escrito.pdf...", "escrito.pdf")]
    [InlineData("con\ttab\u0000nul\u001Fy\u007Fdel.pdf", "contabnulydel.pdf")]
    [InlineData("factura\u202Efdp.exe", "facturafdp.exe")]          // control de dirección (RTLO)
    [InlineData("cero\u200Bancho\uFEFF.pdf", "ceroancho.pdf")]       // formato invisible
    public void Sanitizar_EliminaRutaYCaracteresPeligrosos(string original, string esperado)
    {
        Assert.Equal(esperado, NombreArchivoSanitizer.Sanitizar(original));
    }

    [Fact]
    public void Sanitizar_NormalizaANfc()
    {
        var descompuesto = "acci" + "o\u0301" + "n.pdf"; // ó como o + acento combinante (NFD)
        var resultado = NombreArchivoSanitizer.Sanitizar(descompuesto);

        Assert.Equal("acción.pdf", resultado);
        Assert.True(resultado.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void Sanitizar_TruncaA255ConservandoExtension()
    {
        var resultado = NombreArchivoSanitizer.Sanitizar(new string('a', 400) + ".docx");

        Assert.Equal(NombreArchivoSanitizer.MaxLength, resultado.Length);
        Assert.EndsWith(".docx", resultado);
    }

    [Fact]
    public void Sanitizar_NoPartePares_Surrogados()
    {
        // El corte en 251 caracteres de base cae justo en medio del primer emoji (par surrogado)
        var resultado = NombreArchivoSanitizer.Sanitizar(new string('a', 250) + "😀😀😀😀" + ".pdf");

        Assert.True(resultado.Length <= NombreArchivoSanitizer.MaxLength);
        Assert.EndsWith(".pdf", resultado);
        Assert.Equal(new string('a', 250) + ".pdf", resultado);
        Assert.Equal(resultado, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(resultado))); // sin surrogados sueltos
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("carpeta/")]
    [InlineData("..")]
    [InlineData("\u202E\u0000")]
    public void Sanitizar_NombreVacioOInvalido_400(string? original)
    {
        Assert.Throws<ValidationException>(() => NombreArchivoSanitizer.Sanitizar(original));
    }
}
