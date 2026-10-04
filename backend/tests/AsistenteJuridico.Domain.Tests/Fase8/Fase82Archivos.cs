using System.Text;
using AsistenteJuridico.Domain.Tests.Fase6X;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.2 — Generadores deterministas de archivos reales para la extracción segmentada (sin binarios en el
/// repositorio). Los mismos archivos sirven de referencia para la regresión carácter a carácter de ExtractAsync (T38).
/// </summary>
internal static class Fase82Archivos
{
    public const string MimePdf = "application/pdf";
    public const string MimeDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string MimeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string MimeTxt = "text/plain";

    // ── DOCX ─────────────────────────────────────────────────────────────

    public static W.Paragraph P(string texto, string? estilo = null)
    {
        var parrafo = new W.Paragraph(new W.Run(new W.Text(texto) { Space = SpaceProcessingModeValues.Preserve }));
        if (estilo != null)
        {
            parrafo.PrependChild(new W.ParagraphProperties(new W.ParagraphStyleId { Val = estilo }));
        }

        return parrafo;
    }

    /// <summary>Tabla: filas → celdas → contenido de la celda (párrafos o tablas anidadas).</summary>
    public static W.Table Tabla(params OpenXmlElement[][][] filas) =>
        new(filas.Select(celdas => new W.TableRow(celdas.Select(contenido => new W.TableCell(contenido.Select(e => e.CloneNode(true)))))));

    public static OpenXmlElement[] Celda(params string[] parrafos) => parrafos.Select(p => (OpenXmlElement)P(p)).ToArray();

    /// <summary>DOCX con estilos: "Ttulo1" (nombre "heading 1", como Word en español), "Heading2", "MiTitulo" ("Título 3").</summary>
    public static byte[] Docx(params OpenXmlElement[] bloques)
    {
        using var memoria = new MemoryStream();
        using (var documento = WordprocessingDocument.Create(memoria, WordprocessingDocumentType.Document))
        {
            var principal = documento.AddMainDocumentPart();
            var estilos = principal.AddNewPart<StyleDefinitionsPart>();
            estilos.Styles = new W.Styles(
                Estilo("Ttulo1", "heading 1"),
                Estilo("Heading2", "heading 2"),
                Estilo("MiTitulo", "Título 3"),
                Estilo("Normal", "Normal"));
            principal.Document = new W.Document(new W.Body(bloques.Select(b => b.CloneNode(true))));
        }

        return memoria.ToArray();
    }

    private static W.Style Estilo(string id, string nombre) =>
        new(new W.StyleName { Val = nombre }) { Type = W.StyleValues.Paragraph, StyleId = id };

    // ── XLSX ─────────────────────────────────────────────────────────────

    public sealed record FilaXlsx(uint? Numero, params string[] Celdas);

    /// <summary>Libro con varias hojas; celdas como cadenas en línea (una celda vacía no lleva valor).</summary>
    public static byte[] Xlsx(params (string Nombre, FilaXlsx[] Filas)[] hojas)
    {
        using var memoria = new MemoryStream();
        using (var documento = SpreadsheetDocument.Create(memoria, SpreadsheetDocumentType.Workbook))
        {
            var libro = documento.AddWorkbookPart();
            libro.Workbook = new S.Workbook();
            var lista = new S.Sheets();
            uint id = 1;
            foreach (var (nombre, filas) in hojas)
            {
                var parte = libro.AddNewPart<WorksheetPart>();
                parte.Worksheet = new S.Worksheet(new S.SheetData(filas.Select(f =>
                {
                    var fila = new S.Row(f.Celdas.Select(c => c.Length == 0
                        ? new S.Cell()
                        : new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(c) { Space = SpaceProcessingModeValues.Preserve }) }));
                    if (f.Numero is { } n)
                    {
                        fila.RowIndex = n;
                    }

                    return fila;
                })));
                lista.AppendChild(new S.Sheet { Id = libro.GetIdOfPart(parte), SheetId = id++, Name = nombre });
            }

            libro.Workbook.AppendChild(lista);
        }

        return memoria.ToArray();
    }

    // ── PDF con ToUnicode hacia un sustituto aislado ─────────────────────

    /// <summary>
    /// PDF real con una fuente Type0 (Identity-H, códigos de 2 bytes) cuya CMap /ToUnicode asigna al código 0x0041 el
    /// destino UTF-16BE indicado y al 0x0042 la letra "B". El contenido muestra los códigos 0041 y 0042.
    /// </summary>
    public static byte[] PdfType0ConToUnicode(string destinoHex)
    {
        var cmap = string.Join((char)10,
            "/CIDInit /ProcSet findresource begin", "12 dict begin", "begincmap",
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def", "/CMapName /Adobe-Identity-UCS def", "/CMapType 2 def",
            "1 begincodespacerange", "<0000> <FFFF>", "endcodespacerange",
            "2 beginbfchar", $"<0041> <{destinoHex}>", "<0042> <0042>", "endbfchar",
            "endcmap", "CMapName currentdict /CMap defineresource pop", "end", "end");
        var contenido = "BT /F1 12 Tf 72 700 Td <00410042> Tj ET";
        return PdfConObjetos(contenido,
            "<< /Type /Font /Subtype /Type0 /BaseFont /MiFuente /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode 6 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(cmap)} >>\nstream\n{cmap}\nendstream",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /MiFuente /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /DW 500 >>",
            "<< /Type /FontDescriptor /FontName /MiFuente /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>");
    }

    /// <summary>PDF mínimo: catálogo, páginas, página, contenido (obj. 4), fuente /F1 (obj. 5) y objetos adicionales (6…).</summary>
    private static byte[] PdfConObjetos(string contenido, string fuente, params string[] adicionales)
    {
        var objetos = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contenido)} >>\nstream\n{contenido}\nendstream",
            fuente
        };
        objetos.AddRange(adicionales);

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objetos.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objetos.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objetos.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── Conjunto de referencia para la regresión de ExtractAsync (T38) ───

    /// <summary>Caracteres invisibles por código (no literales) para que ningún editor los altere.</summary>
    public static string C(int codigo) => char.ConvertFromUtf32(codigo);

    public static readonly string TextoBomCrlf =
        "Línea uno\r\nLínea dos\rTres" + C(0x85) + "cuatro" + C(0x2028) + "cinco\ttab  doble" + C(0xA0) + C(0xA0) + "nbsp"
        + C(0x01) + "ctl\n\n\nFIN ";

    public static readonly string TextoUnicode =
        "Cafe" + C(0x301) + " Ñandú " + C(0x1F642) + " ﬁn ½ Ⅻ " + C(0x200B) + " cero " + C(0xAD) + " guion";

    public static IReadOnlyList<(string Nombre, byte[] Contenido, string Mime, string Extension)> Referencia() =>
    [
        ("txt-simple", Encoding.UTF8.GetBytes("Demanda de alimentos: señor juez, año 2026."), MimeTxt, ".txt"),
        ("txt-bom-crlf", [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(TextoBomCrlf)], MimeTxt, ".txt"),
        ("txt-unicode", Encoding.UTF8.GetBytes(TextoUnicode), MimeTxt, ".txt"),
        ("txt-solo-espacios", Encoding.UTF8.GetBytes(" \n\t "), MimeTxt, ".txt"),
        ("pdf-paginas", Archivos.Pdf("Pagina uno del contrato", "", "Pagina tres con la clausula final"), MimePdf, ".pdf"),
        ("pdf-sin-texto", Archivos.Pdf(""), MimePdf, ".pdf"),
        ("docx-estructura", DocxEstructura(), MimeDocx, ".docx"),
        ("docx-vacio", Docx(P(""), P("   ")), MimeDocx, ".docx"),
        ("xlsx-dos-hojas", XlsxDosHojas(), MimeXlsx, ".xlsx"),
        ("xlsx-sin-valores", Xlsx(("Vacia", new[] { new FilaXlsx(1, "", "") })), MimeXlsx, ".xlsx"),
    ];

    public static byte[] DocxEstructura() => Docx(
        P("CONTRATO DE ARRENDAMIENTO", "Ttulo1"),
        P("Comparecen las partes Juan Pérez y María López."),
        P("Cláusulas", "Heading2"),
        P(""),
        Tabla(
            [Celda("Arrendador"), Celda("Juan Pérez", "cédula 0102030405")],
            [Celda(""), Celda("")],
            [Celda("Canon"), [P("USD 500"), Tabla([Celda("anidada A"), Celda("anidada B")])]]),
        P("Detalle", "MiTitulo"),
        P("Texto final del contrato."),
        P("ANEXOS", "Ttulo1"),
        P("Anexo único."));

    public static byte[] XlsxDosHojas() => Xlsx(
        ("Honorarios", new[]
        {
            new FilaXlsx(1, "Cliente", "Monto", "Estado"),
            new FilaXlsx(2, "Pérez", "1500", "Pagado"),
            new FilaXlsx(3, "", "", ""),
            new FilaXlsx(5, "López", "", "Pendiente"),
            new FilaXlsx(6, " ", "", "")
        }),
        ("Gastos", new[]
        {
            new FilaXlsx(null, "Concepto", "Valor"),
            new FilaXlsx(null, "Peritaje", "300")
        }));
}
