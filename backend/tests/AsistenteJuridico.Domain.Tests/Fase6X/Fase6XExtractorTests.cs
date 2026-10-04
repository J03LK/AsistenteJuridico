using AsistenteJuridico.Application.Common.Helpers;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Domain.Tests.Fase6X;

/// <summary>
/// Fase 6.X (H10) — Extractor tipado contra el FileStorageService REAL (directorio temporal): texto real por formato,
/// errores distinguidos, seguridad y cancelación. Nunca hay texto de respaldo.
/// </summary>
public class Fase6XExtractorTests : IDisposable
{
    private const string MimeDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string MimeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly AlmacenamientoTemporal _almacenamiento = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _expediente = Guid.NewGuid();
    private readonly DocumentTextExtractor _extractor;

    public Fase6XExtractorTests() => _extractor = new DocumentTextExtractor(_almacenamiento.Servicio);

    public void Dispose() => _almacenamiento.Dispose();

    private async Task<ExtractionResult> SubirYExtraerAsync(byte[] contenido, string nombre, string mime)
    {
        var archivo = await _almacenamiento.SubirAsync(_tenant, _expediente, contenido, nombre, mime);
        return await _extractor.ExtractAsync(Guid.NewGuid(), archivo.RelativePath, archivo.ContentType);
    }

    private Task<ExtractionResult> CrudoYExtraerAsync(byte[] contenido, string extension, string contentType) =>
        _extractor.ExtractAsync(Guid.NewGuid(), _almacenamiento.EscribirCrudo(_tenant, _expediente, contenido, extension), contentType);

    // ── Formatos con extracción real ─────────────────────────────────────

    [Fact]
    public async Task Txt_Valido_TextoReal()
    {
        var resultado = await SubirYExtraerAsync(Archivos.Txt("Demanda de alimentos: señor juez, año 2026."), "demanda.txt", "text/plain");

        Assert.Equal(ExtractionStatus.Success, resultado.Status);
        Assert.Equal("Demanda de alimentos: señor juez, año 2026.", resultado.Text);
    }

    [Fact]
    public async Task Pdf_Valido_TextoReal()
    {
        var resultado = await SubirYExtraerAsync(Archivos.Pdf("Contrato de arrendamiento clausula primera", "Segunda pagina del escrito"), "contrato.pdf", "application/pdf");

        Assert.Equal(ExtractionStatus.Success, resultado.Status);
        Assert.Contains("Contrato de arrendamiento clausula primera", resultado.Text);
        Assert.Contains("Segunda pagina del escrito", resultado.Text);
        Assert.DoesNotContain("%PDF", resultado.Text);   // texto real, no bytes crudos
    }

    [Fact]
    public async Task Docx_Valido_TextoReal()
    {
        var resultado = await SubirYExtraerAsync(Archivos.Docx("Primer parrafo del escrito.", "Segundo parrafo con la pretension."), "escrito.docx", MimeDocx);

        Assert.Equal(ExtractionStatus.Success, resultado.Status);
        Assert.Contains("Primer parrafo del escrito.", resultado.Text);
        Assert.Contains("Segundo parrafo con la pretension.", resultado.Text);
    }

    [Fact]
    public async Task Xlsx_Valido_CeldasPorFilaYTabulador()
    {
        var resultado = await SubirYExtraerAsync(Archivos.Xlsx("Honorarios", "Pagado", "1500"), "cuentas.xlsx", MimeXlsx);

        Assert.Equal(ExtractionStatus.Success, resultado.Status);
        Assert.Contains("[Hoja: Datos]", resultado.Text);
        Assert.Contains("Honorarios\tPagado\t1500", resultado.Text);
    }

    // ── Contenido inválido, vacío y límites ──────────────────────────────

    [Fact]
    public async Task Pdf_Danado_InvalidContent()
    {
        var resultado = await SubirYExtraerAsync(Archivos.PdfDanado(), "danado.pdf", "application/pdf");
        Assert.Equal(ExtractionStatus.InvalidContent, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Fact]
    public async Task Pdf_Cifrado_InvalidContent()
    {
        var resultado = await SubirYExtraerAsync(Archivos.PdfCifrado(), "cifrado.pdf", "application/pdf");
        Assert.Equal(ExtractionStatus.InvalidContent, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Fact]
    public async Task Docx_Danado_InvalidContent()
    {
        var resultado = await SubirYExtraerAsync(Archivos.DocxDanado(), "danado.docx", MimeDocx);
        Assert.Equal(ExtractionStatus.InvalidContent, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Fact]
    public async Task Txt_Utf8Invalido_InvalidContent()
    {
        var resultado = await CrudoYExtraerAsync([0x61, 0x63, 0x63, 0xF3, 0x6E], ".txt", "text/plain; charset=utf-8");
        Assert.Equal(ExtractionStatus.InvalidContent, resultado.Status);
    }

    [Fact]
    public async Task Pdf_SinTexto_Empty()
    {
        var resultado = await SubirYExtraerAsync(Archivos.PdfSinTexto(), "escaneado.pdf", "application/pdf");
        Assert.Equal(ExtractionStatus.Empty, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x20, 0x0A, 0x09, 0x20 })]
    public async Task Txt_VacioOSoloEspacios_Empty(byte[] contenido)
    {
        var resultado = await CrudoYExtraerAsync(contenido, ".txt", "text/plain; charset=utf-8");
        Assert.Equal(ExtractionStatus.Empty, resultado.Status);
    }

    [Fact]
    public async Task Txt_MasDe30000Caracteres_ContextExceeded()
    {
        var texto = new string('a', ContextWindowValidator.MaxDocumentCharacters + 1);
        var resultado = await SubirYExtraerAsync(Archivos.Txt(texto), "largo.txt", "text/plain");
        Assert.Equal(ExtractionStatus.ContextExceeded, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Fact]
    public async Task Txt_Exactamente30000Caracteres_Success()
    {
        var texto = new string('b', ContextWindowValidator.MaxDocumentCharacters);
        var resultado = await SubirYExtraerAsync(Archivos.Txt(texto), "limite.txt", "text/plain");
        Assert.Equal(ExtractionStatus.Success, resultado.Status);
        Assert.Equal(ContextWindowValidator.MaxDocumentCharacters, resultado.Text!.Length);
    }

    [Fact]
    public async Task Txt_LargoConMultibyte_ContextExceeded_NoInvalidContent()
    {
        // Corte del decodificador en mitad de un carácter multibyte: no debe confundirse con UTF-8 inválido.
        var texto = string.Concat(Enumerable.Repeat("ñandú€", 20_000));
        var resultado = await SubirYExtraerAsync(Archivos.Txt(texto), "multibyte.txt", "text/plain");
        Assert.Equal(ExtractionStatus.ContextExceeded, resultado.Status);
    }

    [Fact]
    public async Task Pdf_MuchasPaginas_ContextExceeded()
    {
        var paginas = Enumerable.Range(0, 400).Select(i => $"Pagina {i} " + new string('x', 90)).ToArray();
        var resultado = await SubirYExtraerAsync(Archivos.Pdf(paginas), "extenso.pdf", "application/pdf");
        Assert.Equal(ExtractionStatus.ContextExceeded, resultado.Status);
    }

    // ── Formatos sin extracción (sin OCR) ────────────────────────────────

    [Theory]
    [InlineData("application/msword")]
    [InlineData("application/vnd.ms-excel")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public async Task FormatoNoSoportado_UnsupportedFormat_SinAbrirElArchivo(string contentType)
    {
        Assert.False(_extractor.IsSupported(contentType));

        // Ruta inexistente: si el extractor intentara abrirla, el resultado sería FileNotFound.
        var resultado = await _extractor.ExtractAsync(Guid.NewGuid(), $"{_tenant:N}/{_expediente:N}/no-existe.bin", contentType);
        Assert.Equal(ExtractionStatus.UnsupportedFormat, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Fact]
    public async Task Png_Subido_UnsupportedFormat()
    {
        var resultado = await SubirYExtraerAsync(Archivos.Png(), "foto.png", "image/png");
        Assert.Equal(ExtractionStatus.UnsupportedFormat, resultado.Status);
    }

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("APPLICATION/PDF", true)]
    [InlineData("text/plain; charset=utf-8", true)]
    [InlineData(MimeDocx, true)]
    [InlineData(MimeXlsx, true)]
    [InlineData("application/octet-stream", false)]
    [InlineData(null, false)]
    public void IsSupported_PorContentTypeCanonico(string? contentType, bool esperado) =>
        Assert.Equal(esperado, _extractor.IsSupported(contentType));

    // ── Almacenamiento y seguridad ───────────────────────────────────────

    [Fact]
    public async Task ArchivoInexistente_FileNotFound()
    {
        var resultado = await _extractor.ExtractAsync(Guid.NewGuid(), $"{_tenant:N}/{_expediente:N}/{Guid.NewGuid():N}.pdf", "application/pdf");
        Assert.Equal(ExtractionStatus.FileNotFound, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SinRuta_FileNotFound(string? ruta)
    {
        var resultado = await _extractor.ExtractAsync(Guid.NewGuid(), ruta, "application/pdf");
        Assert.Equal(ExtractionStatus.FileNotFound, resultado.Status);
    }

    [Theory]
    [InlineData("../fuera/secreto.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("tenant/../../secreto.txt")]
    public async Task RutaInsegura_Forbidden(string ruta)
    {
        var resultado = await _extractor.ExtractAsync(Guid.NewGuid(), ruta, "text/plain; charset=utf-8");
        Assert.Equal(ExtractionStatus.Forbidden, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Fact]
    public async Task Symlink_Forbidden_SinLeerElDestino()
    {
        var exterior = Path.Combine(Path.GetTempPath(), "fase6x-exterior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exterior);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(exterior, "secreto.txt"), "contenido fuera de la base");
            AlmacenamientoTemporal.CrearEnlaceDeDirectorio(Path.Combine(_almacenamiento.Directorio, "enlace"), exterior);

            var resultado = await _extractor.ExtractAsync(Guid.NewGuid(), "enlace/secreto.txt", "text/plain; charset=utf-8");

            Assert.Equal(ExtractionStatus.Forbidden, resultado.Status);
            Assert.Null(resultado.Text);
        }
        finally
        {
            try { Directory.Delete(Path.Combine(_almacenamiento.Directorio, "enlace")); } catch { }
            Directory.Delete(exterior, recursive: true);
        }
    }

    // ── Cancelación y timeout ────────────────────────────────────────────

    [Fact]
    public async Task CancelacionDelLlamador_SePropaga()
    {
        var archivo = await _almacenamiento.SubirAsync(_tenant, _expediente, Archivos.Pdf("Texto"), "c.pdf", "application/pdf");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _extractor.ExtractAsync(Guid.NewGuid(), archivo.RelativePath, archivo.ContentType, cts.Token));
    }

    [Fact]
    public async Task TimeoutDeExtraccion_ExtractionFailed()
    {
        var lento = new DocumentTextExtractor(new AlmacenamientoBloqueado(),
            Options.Create(new DocumentTextExtractionOptions { TimeoutSeconds = 0.2 }));

        var resultado = await lento.ExtractAsync(Guid.NewGuid(), "t/e/a.pdf", "application/pdf");

        Assert.Equal(ExtractionStatus.ExtractionFailed, resultado.Status);
        Assert.Null(resultado.Text);
    }

    [Fact]
    public async Task TimeoutDuranteElAnalisis_SeInterrumpeDentroDeLaBiblioteca_YNoSigueProcesandoDespues()
    {
        // PDF real y válido: sin timeout se extraería por completo (unos segundos con el stream lento).
        var paginas = Enumerable.Range(0, 60).Select(i => $"Pagina {i} del expediente").ToArray();
        var lento = new StreamLento(Archivos.Pdf(paginas));
        var extractor = new DocumentTextExtractor(new AlmacenamientoLento(lento),
            Options.Create(new DocumentTextExtractionOptions { TimeoutSeconds = 0.3 }));

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var resultado = await extractor.ExtractAsync(Guid.NewGuid(), "t/e/lento.pdf", "application/pdf");
        cronometro.Stop();

        Assert.Equal(ExtractionStatus.ExtractionFailed, resultado.Status);
        Assert.Null(resultado.Text);
        Assert.True(lento.Lecturas > 0);
        // Devuelve poco después del timeout, no al terminar el análisis completo.
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(2), $"Tardó {cronometro.Elapsed.TotalSeconds:F2} s");

        // Ninguna lectura después de devolver: el análisis no siguió en segundo plano.
        var lecturasAlDevolver = lento.Lecturas;
        await Task.Delay(800);
        Assert.Equal(lecturasAlDevolver, lento.Lecturas);
    }

    [Fact]
    public async Task SinTimeout_ElMismoPdfLentoSeExtraeCompleto()
    {
        // Control de la prueba anterior: el PDF es válido y el stream lento funciona; solo el timeout lo interrumpe.
        var lento = new StreamLento(Archivos.Pdf("Pagina unica del expediente"));
        var extractor = new DocumentTextExtractor(new AlmacenamientoLento(lento));

        var resultado = await extractor.ExtractAsync(Guid.NewGuid(), "t/e/lento.pdf", "application/pdf");

        Assert.Equal(ExtractionStatus.Success, resultado.Status);
        Assert.Contains("Pagina unica del expediente", resultado.Text);
    }

    [Fact]
    public async Task ErrorDeEntradaSalida_ExtractionFailed()
    {
        var extractor = new DocumentTextExtractor(new AlmacenamientoConErrorDeES());
        var resultado = await extractor.ExtractAsync(Guid.NewGuid(), "t/e/a.pdf", "application/pdf");
        Assert.Equal(ExtractionStatus.ExtractionFailed, resultado.Status);
    }

    // ── Prohibición del texto de respaldo ────────────────────────────────

    [Fact]
    public void CodigoFuente_NoContieneElTextoDeRespaldo()
    {
        var src = UbicarDirectorio("backend", "src");
        var coincidencias = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("Archivo procesado.", StringComparison.Ordinal))
            .ToList();

        // La búsqueda recorre el código real (no un directorio vacío): AIService.cs está entre los archivos analizados.
        Assert.NotEmpty(Directory.EnumerateFiles(src, "AIService.cs", SearchOption.AllDirectories));
        Assert.Empty(coincidencias);
    }

    private static string UbicarDirectorio(params string[] partes)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidato = Path.Combine([dir.FullName, .. partes]);
            if (Directory.Exists(candidato))
            {
                return candidato;
            }
        }

        throw new DirectoryNotFoundException(string.Join('/', partes));
    }

    private sealed class AlmacenamientoBloqueado : IFileStorageService
    {
        public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }

        public Task<StoredDocumentoFile> SaveDocumentoAsync(Guid tenantId, Guid expedienteId, Stream fileStream, string originalFileName,
            string? declaredContentType, long? declaredLength, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AlmacenamientoConErrorDeES : IFileStorageService
    {
        public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default) =>
            throw new IOException("Disco no disponible");

        public Task<StoredDocumentoFile> SaveDocumentoAsync(Guid tenantId, Guid expedienteId, Stream fileStream, string originalFileName,
            string? declaredContentType, long? declaredLength, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
