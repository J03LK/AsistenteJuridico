using System.Security.Cryptography;
using System.Text;
using AsistenteJuridico.Application.Common.Helpers;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Domain.Tests.Fase6X;
using AsistenteJuridico.Infrastructure.Services.AI;
using static AsistenteJuridico.Domain.Tests.Fase8.Fase82Archivos;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.2 — ExtractSegmentsAsync contra el FileStorageService REAL en un directorio temporal (contrato 8.2 §6;
/// pruebas T28–T37, T41 y T43). No usa base de datos: la 8.2 no persiste nada.
/// </summary>
public class Fase82ExtraccionTests : IDisposable
{
    private static readonly string Raya = C(0x2013);

    private readonly AlmacenamientoTemporal _almacenamiento = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _expediente = Guid.NewGuid();
    private readonly DocumentTextExtractor _extractor;

    public Fase82ExtraccionTests() => _extractor = new DocumentTextExtractor(_almacenamiento.Servicio);

    public void Dispose() => _almacenamiento.Dispose();

    private string Escribir(byte[] contenido, string extension) => _almacenamiento.EscribirCrudo(_tenant, _expediente, contenido, extension);

    private Task<SegmentedExtractionResult> SegmentarAsync(byte[] contenido, string mime, string extension, ExtractionProfile? perfil = null) =>
        _extractor.ExtractSegmentsAsync(Guid.NewGuid(), Escribir(contenido, extension), mime, perfil ?? ExtractionProfile.Indexacion);

    private static TextSegment Seg(string texto, string tipo, int desde, int hasta, string etiqueta, string? ruta = null) =>
        new(texto, new UbicacionSegmento(tipo, desde, hasta, etiqueta), ruta);

    // ── T28: segmentos y ubicaciones por formato ─────────────────────────

    [Fact]
    public async Task Txt_UnSegmentoConSusLineas()
    {
        var r = await SegmentarAsync(Encoding.UTF8.GetBytes("Primera línea\nSegunda línea\nTercera"), MimeTxt, ".txt");

        Assert.Equal(ExtractionStatus.Success, r.Status);
        Assert.Equal(Seg("Primera línea\nSegunda línea\nTercera", UbicacionSegmento.Lineas, 1, 3, $"líneas 1{Raya}3"), Assert.Single(r.Segmentos));
    }

    [Fact]
    public async Task Pdf_UnSegmentoPorPagina_LaPaginaVaciaNoGeneraSegmentoPeroConservaLaNumeracion()
    {
        var r = await SegmentarAsync(Archivos.Pdf("Pagina uno del contrato", "", "Pagina tres con la clausula final"), MimePdf, ".pdf");

        Assert.Equal(ExtractionStatus.Success, r.Status);
        Assert.Equal(2, r.Segmentos.Count);
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Paginas, 1, 1, "página 1"), r.Segmentos[0].Ubicacion);
        Assert.Equal(new UbicacionSegmento(UbicacionSegmento.Paginas, 3, 3, "página 3"), r.Segmentos[1].Ubicacion);
        Assert.Contains("Pagina uno del contrato", r.Segmentos[0].Texto);
        Assert.Contains("Pagina tres con la clausula final", r.Segmentos[1].Texto);
        Assert.All(r.Segmentos, s => Assert.Null(s.RutaSeccion));
    }

    [Fact]
    public async Task Docx_UnidadesDeBloque_FilasDeTabla_TablasAnidadas_YRutaDeSeccion()
    {
        var r = await SegmentarAsync(DocxEstructura(), MimeDocx, ".docx");

        const string contrato = "CONTRATO DE ARRENDAMIENTO";
        const string clausulas = contrato + " > Cláusulas";
        Assert.Equal(ExtractionStatus.Success, r.Status);
        Assert.Equal(new[]
        {
            Seg(contrato, UbicacionSegmento.Parrafos, 1, 1, "párrafo 1", contrato),
            Seg("Comparecen las partes Juan Pérez y María López.", UbicacionSegmento.Parrafos, 2, 2, "párrafo 2", contrato),
            Seg("Cláusulas", UbicacionSegmento.Parrafos, 3, 3, "párrafo 3", clausulas),
            // párrafo 4 vacío y fila 6 sin texto: no generan segmento pero consumen numeración.
            Seg("Arrendador | Juan Pérez cédula 0102030405", UbicacionSegmento.Parrafos, 5, 5, "párrafo 5", clausulas),
            Seg("Canon | USD 500 anidada A anidada B", UbicacionSegmento.Parrafos, 7, 7, "párrafo 7", clausulas),
            Seg("Detalle", UbicacionSegmento.Parrafos, 8, 8, "párrafo 8", clausulas + " > Detalle"),
            Seg("Texto final del contrato.", UbicacionSegmento.Parrafos, 9, 9, "párrafo 9", clausulas + " > Detalle"),
            Seg("ANEXOS", UbicacionSegmento.Parrafos, 10, 10, "párrafo 10", "ANEXOS"),
            Seg("Anexo único.", UbicacionSegmento.Parrafos, 11, 11, "párrafo 11", "ANEXOS"),
        }, r.Segmentos);
    }

    [Fact]
    public async Task Docx_RutaDeMasDe500_SeRecortaPorLosNivelesAltos()
    {
        var r = await SegmentarAsync(
            Docx(P(new string('A', 300), "Ttulo1"), P(new string('B', 300), "Heading2"), P("Contenido.")), MimeDocx, ".docx");

        var ruta = r.Segmentos[^1].RutaSeccion;
        Assert.Equal(C(0x2026) + " > " + new string('B', 300), ruta);
        Assert.True(ruta!.Length <= 500);
    }

    [Fact]
    public async Task Xlsx_UnaFilaPorSegmento_NumeroRealDeFila_YLaHojaEnLaEtiqueta()
    {
        var r = await SegmentarAsync(XlsxDosHojas(), MimeXlsx, ".xlsx");

        Assert.Equal(ExtractionStatus.Success, r.Status);
        Assert.Equal(new[]
        {
            Seg("Cliente\tMonto\tEstado", UbicacionSegmento.Hoja, 1, 1, "hoja Honorarios"),
            Seg("Pérez\t1500\tPagado", UbicacionSegmento.Hoja, 2, 2, "hoja Honorarios"),
            // fila 3 sin valores y fila 6 solo con un espacio: sin segmento.
            Seg("López\t\tPendiente", UbicacionSegmento.Hoja, 5, 5, "hoja Honorarios"),
            Seg("Concepto\tValor", UbicacionSegmento.Hoja, 1, 1, "hoja Gastos"),   // sin RowIndex: ordinal
            Seg("Peritaje\t300", UbicacionSegmento.Hoja, 2, 2, "hoja Gastos"),
        }, r.Segmentos);
    }

    [Fact]
    public async Task Pipeline_ExtraccionMasFragmentacion_RespetaLasPropiedadesEnTodosLosFormatos()
    {
        var documentos = new (byte[] Contenido, string Mime, string Extension)[]
        {
            (Encoding.UTF8.GetBytes(Fase82Fragmentos.Prosa(200)), MimeTxt, ".txt"),
            (Archivos.Pdf(Enumerable.Range(1, 15).Select(n => string.Concat(Enumerable.Repeat($"Clausula {n} del expediente. ", 8))).ToArray()), MimePdf, ".pdf"),
            (Docx([P("TÍTULO", "Ttulo1"), .. Enumerable.Range(1, 60).Select(n => P(Fase82Fragmentos.Prosa(2)))]), MimeDocx, ".docx"),
            (Xlsx(("Datos", Enumerable.Range(1, 120).Select(n => new FilaXlsx((uint)n, $"Fila {n}", "dato de prueba", $"{n * 10}")).ToArray())), MimeXlsx, ".xlsx"),
        };

        foreach (var (contenido, mime, extension) in documentos)
        {
            var extraccion = await SegmentarAsync(contenido, mime, extension);
            Assert.Equal(ExtractionStatus.Success, extraccion.Status);
            var resultado = Fragmentador.Fragmentar(extraccion.Segmentos, 2000);
            Assert.True(resultado.Fragmentos.Count >= 2, mime);
            Fase82Fragmentos.VerificarPropiedades(extraccion.Segmentos, resultado);
        }
    }

    // ── T29–T35: estados ─────────────────────────────────────────────────

    [Theory]
    [InlineData("application/msword")]
    [InlineData("application/vnd.ms-excel")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public async Task FormatoNoSoportado_SinAbrirElArchivo(string contentType)
    {
        var r = await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), $"{_tenant:N}/{_expediente:N}/no-existe.bin", contentType, ExtractionProfile.Indexacion);
        AssertFallo(r, ExtractionStatus.UnsupportedFormat);
    }

    public static TheoryData<string> Vacios => new() { "txt-solo-espacios", "pdf-sin-texto", "docx-vacio", "xlsx-sin-valores" };

    [Theory]
    [MemberData(nameof(Vacios))]
    public async Task SinTexto_Empty(string nombre)
    {
        var (_, contenido, mime, extension) = Referencia().Single(d => d.Nombre == nombre);
        AssertFallo(await SegmentarAsync(contenido, mime, extension), ExtractionStatus.Empty);
    }

    [Fact]
    public async Task ArchivoVacio_Empty() => AssertFallo(await SegmentarAsync([], MimeTxt, ".txt"), ExtractionStatus.Empty);

    [Fact]
    public async Task ContenidoInvalido_InvalidContent()
    {
        AssertFallo(await SegmentarAsync(Archivos.PdfDanado(), MimePdf, ".pdf"), ExtractionStatus.InvalidContent);
        AssertFallo(await SegmentarAsync(Archivos.PdfCifrado(), MimePdf, ".pdf"), ExtractionStatus.InvalidContent);
        AssertFallo(await SegmentarAsync(Archivos.DocxDanado(), MimeDocx, ".docx"), ExtractionStatus.InvalidContent);
        AssertFallo(await SegmentarAsync([0x61, 0x63, 0xF3, 0x6E], MimeTxt, ".txt"), ExtractionStatus.InvalidContent);
        AssertFallo(await SegmentarAsync([0x61, 0x00, 0x62], MimeTxt, ".txt"), ExtractionStatus.InvalidContent);
    }

    // ── Corrección de la desviación #1: UTF-16 mal formado = InvalidContent ──

    /// <summary>
    /// Un sustituto aislado en un TXT solo puede llegar codificado como secuencia UTF-8 de un sustituto (CESU-8,
    /// ED A0 80 = U+D800; ED B0 80 = U+DC00). El decodificador estricto lo rechaza: InvalidContent, sin texto ni U+FFFD.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]                     // sustituto alto aislado
    [InlineData(new byte[] { 0xED, 0xB0, 0x80 })]                     // sustituto bajo aislado
    [InlineData(new byte[] { 0xED, 0xA0, 0xBD, 0xED, 0xB9, 0x82 })]   // par codificado como dos sustitutos (CESU-8)
    public async Task Txt_ConSustitutoAislado_InvalidContent(byte[] sustituto)
    {
        byte[] contenido = [.. Encoding.UTF8.GetBytes("Texto del escrito "), .. sustituto, .. Encoding.UTF8.GetBytes(" final.")];
        var ruta = Escribir(contenido, ".txt");

        AssertFallo(await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, MimeTxt, ExtractionProfile.Indexacion), ExtractionStatus.InvalidContent);
        Assert.Equal(ExtractionStatus.InvalidContent, (await _extractor.ExtractAsync(Guid.NewGuid(), ruta, MimeTxt)).Status);   // 6.X sin cambios
    }

    [Fact]
    public async Task ParValidoYEmojis_ContinuanNormalmente_SinU_FFFD()
    {
        var texto = "Acuerdo " + C(0x1F91D) + " firmado por " + C(0x1F468) + C(0x200D) + C(0x2696) + C(0xFE0F) + " y " + C(0x1F642) + ". "
            + Fase82Fragmentos.Prosa(30);
        var r = await SegmentarAsync(Encoding.UTF8.GetBytes(texto), MimeTxt, ".txt");

        Assert.Equal(ExtractionStatus.Success, r.Status);
        Assert.Equal(texto, Assert.Single(r.Segmentos).Texto);
        var fragmentacion = Fragmentador.Fragmentar(r.Segmentos, 2000);
        Fase82Fragmentos.VerificarPropiedades(r.Segmentos, fragmentacion);
        Assert.StartsWith("Acuerdo " + C(0x1F91D), fragmentacion.Fragmentos[0].Texto);
        Assert.All(fragmentacion.Fragmentos, f =>
        {
            Assert.DoesNotContain(C(0xFFFD), f.Texto);
            Assert.True(TextoNormalizador.EsUtf16BienFormado(f.Texto));
        });
    }

    /// <summary>
    /// PDF con una CMap /ToUnicode que apunta a un sustituto aislado: es PdfPig quien lo decodifica como U+FFFD (igual
    /// que en la 6.X), así que el texto que recibe la 8.2 ya es UTF-16 bien formado. norm-v1 no añade ningún U+FFFD:
    /// el fragmento contiene exactamente los mismos que la extracción. Con un par válido no aparece ninguno.
    /// </summary>
    [Fact]
    public async Task Pdf_ToUnicodeHaciaSustitutoAislado_ElNormalizadorNoIntroduceU_FFFD()
    {
        var ruta = Escribir(PdfType0ConToUnicode("D800"), ".pdf");
        var chat = await _extractor.ExtractAsync(Guid.NewGuid(), ruta, MimePdf);
        var r = await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, MimePdf, ExtractionProfile.Indexacion);

        Assert.Equal(ExtractionStatus.Success, r.Status);
        var segmento = Assert.Single(r.Segmentos).Texto;
        Assert.Equal(chat.Text, segmento);                                         // mismo texto que la 6.X
        var enExtraccion = segmento.Count(c => c == (char)0xFFFD);
        var fragmento = Assert.Single(Fragmentador.Fragmentar(r.Segmentos, 2000).Fragmentos).Texto;
        Assert.Equal(enExtraccion, fragmento.Count(c => c == (char)0xFFFD));

        var valido = await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), Escribir(PdfType0ConToUnicode("D83DDE42"), ".pdf"), MimePdf, ExtractionProfile.Indexacion);
        Assert.Equal(C(0x1F642) + "B", Assert.Single(valido.Segmentos).Texto);
        Assert.DoesNotContain(C(0xFFFD), Assert.Single(Fragmentador.Fragmentar(valido.Segmentos, 2000).Fragmentos).Texto);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SinRuta_FileNotFound(string? ruta) =>
        AssertFallo(await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, MimePdf, ExtractionProfile.Indexacion), ExtractionStatus.FileNotFound);

    [Fact]
    public async Task ArchivoInexistente_FileNotFound() => AssertFallo(
        await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), $"{_tenant:N}/{_expediente:N}/{Guid.NewGuid():N}.pdf", MimePdf, ExtractionProfile.Indexacion),
        ExtractionStatus.FileNotFound);

    [Theory]
    [InlineData("../fuera/secreto.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("tenant/../../secreto.txt")]
    public async Task RutaInsegura_Forbidden(string ruta) =>
        AssertFallo(await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, MimeTxt, ExtractionProfile.Indexacion), ExtractionStatus.Forbidden);

    [Fact]
    public async Task Symlink_Forbidden_SinLeerElDestino()
    {
        var exterior = Path.Combine(Path.GetTempPath(), "fase82-exterior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exterior);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(exterior, "secreto.txt"), "contenido fuera de la base");
            AlmacenamientoTemporal.CrearEnlaceDeDirectorio(Path.Combine(_almacenamiento.Directorio, "enlace"), exterior);

            AssertFallo(await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), "enlace/secreto.txt", MimeTxt, ExtractionProfile.Indexacion),
                ExtractionStatus.Forbidden);
        }
        finally
        {
            try { Directory.Delete(Path.Combine(_almacenamiento.Directorio, "enlace")); } catch { }
            Directory.Delete(exterior, recursive: true);
        }
    }

    // T33
    [Fact]
    public async Task TimeoutDelPerfil_ExtractionFailed_SinTareaAbandonada()
    {
        var bloqueado = new DocumentTextExtractor(new AlmacenamientoBloqueado());
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var r = await bloqueado.ExtractSegmentsAsync(Guid.NewGuid(), "t/e/a.pdf", MimePdf, new ExtractionProfile("Prueba", 1000, 1));

        AssertFallo(r, ExtractionStatus.ExtractionFailed);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TimeoutDuranteLaLectura_SeInterrumpe_YNoSigueLeyendo()
    {
        var goteo = new StreamGoteo(Archivos.Pdf(Enumerable.Range(0, 400).Select(i => $"Pagina {i} del expediente").ToArray()));
        var extractor = new DocumentTextExtractor(new AlmacenamientoFijo(goteo));
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var r = await extractor.ExtractSegmentsAsync(Guid.NewGuid(), "t/e/lento.pdf", MimePdf, new ExtractionProfile("Prueba", 1_000_000, 1));

        AssertFallo(r, ExtractionStatus.ExtractionFailed);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(4), $"Tardó {cronometro.Elapsed.TotalSeconds:F2} s");
        Assert.True(goteo.Lecturas > 0);
        var lecturas = goteo.Lecturas;
        await Task.Delay(500);
        Assert.Equal(lecturas, goteo.Lecturas);
    }

    // T34
    [Fact]
    public async Task CancelacionDelLlamador_SePropaga()
    {
        var ruta = Escribir(Archivos.Pdf("Texto"), ".pdf");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, MimePdf, ExtractionProfile.Indexacion, cts.Token));
    }

    [Fact]
    public async Task CancelacionDurantElAnalisis_SePropaga_NoEsUnEstado()
    {
        var goteo = new StreamGoteo(Archivos.Pdf(Enumerable.Range(0, 200).Select(i => $"Pagina {i}").ToArray()));
        var extractor = new DocumentTextExtractor(new AlmacenamientoFijo(goteo));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractSegmentsAsync(Guid.NewGuid(), "t/e/lento.pdf", MimePdf, ExtractionProfile.Indexacion, cts.Token));
    }

    [Fact]
    public async Task PerfilNulo_ArgumentNullException() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => _extractor.ExtractSegmentsAsync(Guid.NewGuid(), "x", MimeTxt, null!));

    // T35
    [Fact]
    public async Task PerfilIndexacion_LimiteDeTresMillones()
    {
        var justo = await SegmentarAsync(Encoding.UTF8.GetBytes(new string('a', ExtractionProfile.MaxCaracteresIndexacion)), MimeTxt, ".txt");
        Assert.Equal(ExtractionStatus.Success, justo.Status);
        Assert.Equal(ExtractionProfile.MaxCaracteresIndexacion, Assert.Single(justo.Segmentos).Texto.Length);

        // Con el límite de fragmentos del contrato (2.000), ese documento lo supera: todo o nada (§8.9).
        var fragmentacion = Fragmentador.Fragmentar(justo.Segmentos, 2000);
        Assert.Equal(EstadoFragmentacion.LimiteSuperado, fragmentacion.Estado);
        Assert.Empty(fragmentacion.Fragmentos);
        Assert.True(fragmentacion.FragmentosCalculados > 2000);
        Assert.Equal(2000, fragmentacion.LimiteAplicado);

        var excedido = await SegmentarAsync(Encoding.UTF8.GetBytes(new string('a', ExtractionProfile.MaxCaracteresIndexacion + 1)), MimeTxt, ".txt");
        AssertFallo(excedido, ExtractionStatus.ContextExceeded);
    }

    [Fact]
    public async Task PerfilChat_MismoContextExceededQueExtractAsync()
    {
        var chat = ExtractionProfile.Chat(30);
        var txt = Encoding.UTF8.GetBytes(new string('b', ContextWindowValidator.MaxDocumentCharacters + 1));
        AssertFallo(await SegmentarAsync(txt, MimeTxt, ".txt", chat), ExtractionStatus.ContextExceeded);

        // PDF de 400 páginas: ambos métodos con el perfil Chat dan ContextExceeded (mismo cómputo, §6.3).
        var pdf = Archivos.Pdf(Enumerable.Range(0, 400).Select(i => $"Pagina {i} " + new string('x', 90)).ToArray());
        var ruta = Escribir(pdf, ".pdf");
        Assert.Equal(ExtractionStatus.ContextExceeded, (await _extractor.ExtractAsync(Guid.NewGuid(), ruta, MimePdf)).Status);
        AssertFallo(await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, MimePdf, chat), ExtractionStatus.ContextExceeded);

        // Y el mismo PDF cabe en el perfil Indexacion.
        Assert.Equal(ExtractionStatus.Success, (await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, MimePdf, ExtractionProfile.Indexacion)).Status);
    }

    // ── T36–T37: hash y contenido intacto ────────────────────────────────

    [Fact]
    public async Task HashDelArchivo_SoloConSuccess_SobreLosMismosBytes()
    {
        foreach (var (nombre, contenido, mime, extension) in Referencia())
        {
            var r = await SegmentarAsync(contenido, mime, extension);
            if (r.Status == ExtractionStatus.Success)
            {
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(contenido)), r.HashSha256Archivo);
            }
            else
            {
                Assert.Null(r.HashSha256Archivo);
            }
        }

        Assert.Null((await SegmentarAsync(Archivos.PdfDanado(), MimePdf, ".pdf")).HashSha256Archivo);
    }

    [Fact]
    public async Task LaExtraccionNoModificaElArchivo()
    {
        foreach (var (_, contenido, mime, extension) in Referencia())
        {
            var ruta = Escribir(contenido, extension);
            var completa = Path.Combine(_almacenamiento.Directorio, ruta);
            var antes = (SHA256.HashData(await File.ReadAllBytesAsync(completa)), File.GetLastWriteTimeUtc(completa));

            await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, mime, ExtractionProfile.Indexacion);
            await _extractor.ExtractAsync(Guid.NewGuid(), ruta, mime);

            Assert.Equal(antes.Item1, SHA256.HashData(await File.ReadAllBytesAsync(completa)));
            Assert.Equal(antes.Item2, File.GetLastWriteTimeUtc(completa));
        }
    }

    // ── T41: logs sin contenido ──────────────────────────────────────────

    [Fact]
    public async Task Logs_SinTextoRutasNombresDeHojaNiRutasDeSeccion()
    {
        var logger = new LoggerEnLista<DocumentTextExtractor>();
        var extractor = new DocumentTextExtractor(_almacenamiento.Servicio, logger: logger);
        var rutas = new List<string>();

        async Task ExtraerAsync(byte[] contenido, string mime, string extension)
        {
            var ruta = Escribir(contenido, extension);
            rutas.Add(ruta);
            var r = await extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, mime, ExtractionProfile.Indexacion);
            Fragmentador.Fragmentar(r.Segmentos, 2000);
        }

        await ExtraerAsync(Encoding.UTF8.GetBytes("SECRETO-TXT del cliente Juan Pérez"), MimeTxt, ".txt");
        await ExtraerAsync(Docx(P("SECCION-SECRETA", "Ttulo1"), P("SECRETO-DOCX")), MimeDocx, ".docx");
        await ExtraerAsync(Xlsx(("HOJA-SECRETA", new[] { new FilaXlsx(1, "SECRETO-XLSX") })), MimeXlsx, ".xlsx");
        await ExtraerAsync(Archivos.PdfDanado(), MimePdf, ".pdf");                    // log de contenido inválido
        await extractor.ExtractSegmentsAsync(Guid.NewGuid(), $"{_tenant:N}/RUTA-SECRETA.pdf", MimePdf, ExtractionProfile.Indexacion);   // FileNotFound
        await extractor.ExtractSegmentsAsync(Guid.NewGuid(), "../RUTA-SECRETA.txt", MimeTxt, ExtractionProfile.Indexacion);              // Forbidden

        Assert.NotEmpty(logger.Mensajes);
        foreach (var mensaje in logger.Mensajes)
        {
            foreach (var prohibido in new[] { "SECRETO", "Juan Pérez", "SECCION-SECRETA", "HOJA-SECRETA", "RUTA-SECRETA", _tenant.ToString("N") }.Concat(rutas))
            {
                Assert.DoesNotContain(prohibido, mensaje, StringComparison.Ordinal);
            }
        }
    }

    // ── T43: configuración ───────────────────────────────────────────────

    [Fact]
    public void Perfiles_IndexacionConstanteYChatConElTratamientoDeLa6X()
    {
        Assert.Equal(("Indexacion", 3_000_000, 120),
            (ExtractionProfile.Indexacion.Nombre, ExtractionProfile.Indexacion.MaxCaracteres, ExtractionProfile.Indexacion.TimeoutSeconds));
        Assert.Equal(30, ExtractionProfile.Chat(0).TimeoutSeconds);
        Assert.Equal(30, ExtractionProfile.Chat(-5).TimeoutSeconds);
        Assert.Equal(45, ExtractionProfile.Chat(45).TimeoutSeconds);
        Assert.Equal(ContextWindowValidator.MaxDocumentCharacters, ExtractionProfile.Chat(30).MaxCaracteres);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractionProfile("X", 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractionProfile("X", 10, 0));
    }

    [Fact]
    public void SinClavesDeConfiguracionNuevas()
    {
        Assert.Equal(new[] { nameof(DocumentTextExtractionOptions.TimeoutSeconds) },
            typeof(DocumentTextExtractionOptions).GetProperties().Select(p => p.Name).ToArray());

        var api = UbicarDirectorio("backend", "src", "AsistenteJuridico.API");
        var configuraciones = Directory.EnumerateFiles(api, "appsettings*.json").ToList();
        Assert.NotEmpty(configuraciones);
        foreach (var archivo in configuraciones)
        {
            var contenido = File.ReadAllText(archivo);
            Assert.DoesNotContain("IndexacionTimeoutSeconds", contenido);
            Assert.DoesNotContain("MaxFragmentosPorDocumento", contenido);
            Assert.DoesNotContain("\"Indexing\"", contenido);
        }
    }

    internal static string UbicarDirectorio(params string[] partes)
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

    private static void AssertFallo(SegmentedExtractionResult r, ExtractionStatus esperado)
    {
        Assert.Equal(esperado, r.Status);
        Assert.Empty(r.Segmentos);
        Assert.Null(r.HashSha256Archivo);
    }

    /// <summary>Stream que entrega como mucho 64 bytes por lectura y espera 1 ms en cada una (lectura lenta y larga).</summary>
    private sealed class StreamGoteo(byte[] contenido) : MemoryStream(contenido, writable: false)
    {
        private int _lecturas;

        public int Lecturas => Volatile.Read(ref _lecturas);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Interlocked.Increment(ref _lecturas);
            Thread.Sleep(1);
            var temporal = new byte[Math.Min(64, buffer.Length)];
            var leidos = base.Read(temporal, 0, temporal.Length);
            temporal.AsSpan(0, leidos).CopyTo(buffer);
            return leidos;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));
    }

    private sealed class AlmacenamientoFijo(Stream stream) : IFileStorageService
    {
        public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default) => Task.FromResult(stream);

        public Task<StoredDocumentoFile> SaveDocumentoAsync(Guid tenantId, Guid expedienteId, Stream fileStream, string originalFileName,
            string? declaredContentType, long? declaredLength, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
}
