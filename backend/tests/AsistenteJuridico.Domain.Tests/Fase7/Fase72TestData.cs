using System.IO.Compression;
using System.Text;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>Contenidos de prueba de la Fase 7.2 (archivos mínimos válidos e inválidos por formato).</summary>
internal static class Fase72TestData
{
    public const string MimeDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string MimeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Pdf(string texto = "contenido") => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {texto}\n%%EOF");

    public static byte[] Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    public static byte[] Jpeg => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    public static byte[] Ole => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00];

    public static byte[] Docx(bool conMacros = false) => Zip(
        ("[Content_Types].xml", "<Types/>"),
        ("word/document.xml", "<w:document/>"),
        conMacros ? ("word/vbaProject.bin", "macro") : default);

    public static byte[] Xlsx(bool conMacros = false) => Zip(
        ("[Content_Types].xml", "<Types/>"),
        ("xl/workbook.xml", "<workbook/>"),
        conMacros ? ("xl/vbaProject.bin", "macro") : default);

    /// <summary>ZIP válido que no es OOXML (sin [Content_Types].xml).</summary>
    public static byte[] ZipNoOoxml => Zip(("leeme.txt", "hola"));

    /// <summary>Cabecera ZIP correcta seguida de basura: no tiene directorio central.</summary>
    public static byte[] ZipCorrupto => [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00, 0xDE, 0xAD, 0xBE, 0xEF];

    public static byte[] Zip(params (string Nombre, string Contenido)[] entradas)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (nombre, contenido) in entradas)
            {
                if (nombre == null)
                {
                    continue;
                }

                using var escritor = new StreamWriter(zip.CreateEntry(nombre).Open());
                escritor.Write(contenido);
            }
        }

        return memoria.ToArray();
    }

    /// <summary>PDF de exactamente <paramref name="tamanio"/> bytes (cabecera %PDF y relleno).</summary>
    public static byte[] PdfDeTamanio(long tamanio)
    {
        var bytes = new byte[tamanio];
        "%PDF-1.4\n"u8.CopyTo(bytes);
        return bytes;
    }
}
