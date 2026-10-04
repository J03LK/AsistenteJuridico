using System.Security.Cryptography;
using System.Text;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Domain.Tests.Fase6X;
using AsistenteJuridico.Infrastructure.Services.AI;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.2 — Regresión carácter a carácter de ExtractAsync (T38, A2), huellas golden de la segmentación y la
/// fragmentación por formato (T25) y límites de arquitectura (T40).
/// </summary>
public class Fase82RegresionYArquitecturaTests : IDisposable
{
    private readonly AlmacenamientoTemporal _almacenamiento = new();
    private readonly DocumentTextExtractor _extractor;

    public Fase82RegresionYArquitecturaTests() => _extractor = new DocumentTextExtractor(_almacenamiento.Servicio);

    public void Dispose() => _almacenamiento.Dispose();

    /// <summary>
    /// Valores de referencia capturados con ExtractAsync en el commit 518e2132 (antes de la 8.2), sobre los archivos
    /// deterministas de <see cref="Fase82Archivos.Referencia"/>: estado, longitud y SHA-256 (UTF-8) del texto.
    /// </summary>
    private static readonly Dictionary<string, (ExtractionStatus Estado, int Longitud, string Sha256)> ReferenciaExtractAsync = new()
    {
        ["txt-simple"] = (ExtractionStatus.Success, 43, "3dab41671e1e94fb79af0bb2338f8672e9738d3a958d4b7748042567c636ff0a"),
        ["txt-bom-crlf"] = (ExtractionStatus.Success, 66, "e8b8cdae53a07179240491b4b4ae925e673a45d65041540585567254779564c5"),
        ["txt-unicode"] = (ExtractionStatus.Success, 36, "2d401ee1a34e2a6ea94d2b6b875c0baedc58d128f30a20c060b9e23e33a6351a"),
        ["txt-solo-espacios"] = (ExtractionStatus.Empty, -1, "null"),
        ["pdf-paginas"] = (ExtractionStatus.Success, 60, "94f5d25aad3cb930375396c24fcfea3cc843237c147dae56d51594ee2f72b658"),
        ["pdf-sin-texto"] = (ExtractionStatus.Empty, -1, "null"),
        ["docx-estructura"] = (ExtractionStatus.Success, 215, "7c1139d309bf3f0c2fb3919bb9bfbc20beaa60a67d2acfb3673f50bd42f65ef0"),
        ["docx-vacio"] = (ExtractionStatus.Empty, -1, "null"),
        ["xlsx-dos-hojas"] = (ExtractionStatus.Success, 122, "aae5a328ac6d8c83d432c0d66e6ef5e9adf300f2ae2cb0af1bf0278a15f43f7c"),
        ["xlsx-sin-valores"] = (ExtractionStatus.Empty, -1, "null"),
    };

    /// <summary>Huellas golden de ExtractSegmentsAsync (Indexacion) + Fragmentar(2.000) por archivo de referencia. Sin el hash del archivo: los generadores de PDF y OOXML incluyen fechas, así que los bytes cambian entre ejecuciones (el hash se prueba en T36).</summary>
    private static readonly Dictionary<string, string> HuellasGolden = new()
    {
        ["docx-estructura"] = "a9fce5d52e35485408c47b2b5aed0c91",
        ["docx-vacio"] = "a5587188c9b35e782e73e371bdecdffa",
        ["pdf-paginas"] = "098d1fa4755e743db953d67534c8171e",
        ["pdf-sin-texto"] = "a5587188c9b35e782e73e371bdecdffa",
        ["txt-bom-crlf"] = "e0d0b750a76669acc90d90bbd61334a4",
        ["txt-simple"] = "7fe370d4c00413ebabbdc9524f27a6c0",
        ["txt-solo-espacios"] = "a5587188c9b35e782e73e371bdecdffa",
        ["txt-unicode"] = "63631e2d8f02b3213190d73f55993032",
        ["xlsx-dos-hojas"] = "8f6ce66e4b028e3de73b56d0651e8f7e",
        ["xlsx-sin-valores"] = "a5587188c9b35e782e73e371bdecdffa",
    };

    // T38
    [Fact]
    public async Task ExtractAsync_IdenticoAlCommitBase_CaracterACaracter()
    {
        Assert.Equal(ReferenciaExtractAsync.Keys.Order(), Fase82Archivos.Referencia().Select(d => d.Nombre).Order());
        foreach (var (nombre, contenido, mime, extension) in Fase82Archivos.Referencia())
        {
            var ruta = _almacenamiento.EscribirCrudo(Guid.NewGuid(), Guid.NewGuid(), contenido, extension);
            var r = await _extractor.ExtractAsync(Guid.NewGuid(), ruta, mime);
            var hash = r.Text == null ? "null" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.Text)));

            Assert.Equal(ReferenciaExtractAsync[nombre], (r.Status, r.Text?.Length ?? -1, hash));
        }
    }

    [Fact]
    public async Task ExtractAsync_TextoExactoDeLaTablaDocxYDelLibroXlsx()
    {
        var docx = await ExtraerAsync(Fase82Archivos.DocxEstructura(), Fase82Archivos.MimeDocx, ".docx");
        Assert.Contains("Arrendador\nJuan Pérez\ncédula 0102030405\n\n\nCanon\nUSD 500\nanidada A\nanidada B\n", docx);   // un párrafo por línea, como en la 6.X

        var xlsx = await ExtraerAsync(Fase82Archivos.XlsxDosHojas(), Fase82Archivos.MimeXlsx, ".xlsx");
        Assert.StartsWith("[Hoja: Honorarios]\nCliente\tMonto\tEstado\n", xlsx);
        Assert.Contains("\n \t\t\n[Hoja: Gastos]\n", xlsx);
    }

    private async Task<string> ExtraerAsync(byte[] contenido, string mime, string extension)
    {
        var ruta = _almacenamiento.EscribirCrudo(Guid.NewGuid(), Guid.NewGuid(), contenido, extension);
        return (await _extractor.ExtractAsync(Guid.NewGuid(), ruta, mime)).Text!;
    }

    // T25 (golden)
    [Fact]
    public async Task Segmentacion_HuellasGoldenPorFormato()
    {
        var obtenidas = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (nombre, contenido, mime, extension) in Fase82Archivos.Referencia())
        {
            var ruta = _almacenamiento.EscribirCrudo(Guid.NewGuid(), Guid.NewGuid(), contenido, extension);
            var primera = await HuellaAsync(ruta, mime);
            Assert.Equal(primera, await HuellaAsync(ruta, mime));   // dos ejecuciones idénticas
            obtenidas[nombre] = primera;
        }

        var texto = string.Join("\n", obtenidas.Select(kv => $"[\"{kv.Key}\"] = \"{kv.Value}\","));
        Assert.True(HuellasGolden.Count == obtenidas.Count && obtenidas.All(kv => HuellasGolden[kv.Key] == kv.Value), "Huellas obtenidas:\n" + texto);
    }

    private async Task<string> HuellaAsync(string ruta, string mime)
    {
        var extraccion = await _extractor.ExtractSegmentsAsync(Guid.NewGuid(), ruta, mime, ExtractionProfile.Indexacion);
        var segmentos = string.Concat(extraccion.Segmentos.Select(s =>
            $"{s.Texto}\u0000{s.Ubicacion}\u0000{s.RutaSeccion}\u0001"));
        var fragmentos = extraccion.IsSuccess ? Fase82Fragmentos.Huella(Fragmentador.Fragmentar(extraccion.Segmentos, 2000)) : "-";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{extraccion.Status}|{segmentos}|{fragmentos}")))[..32];
    }

    // T40
    [Fact]
    public void Arquitectura_NingunaViaNuevaDeAcceso_YSinLlamadoresEnProduccion()
    {
        var src = Fase82ExtraccionTests.UbicarDirectorio("backend", "src");
        var archivos = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText);
        Assert.Contains("AsistenteJuridico.Infrastructure/Services/AI/DocumentTextExtractor.cs", archivos.Keys);

        // La API no referencia los componentes de la 8.2.
        foreach (var (ruta, contenido) in archivos.Where(a => a.Key.StartsWith("AsistenteJuridico.API/")))
        {
            foreach (var simbolo in new[] { "ExtractSegmentsAsync", "Fragmentador", "TextoNormalizador", "ExtractionProfile" })
            {
                Assert.False(contenido.Contains(simbolo, StringComparison.Ordinal), $"{ruta} referencia {simbolo}");
            }
        }

        // ExtractSegmentsAsync solo aparece en su contrato, en su implementación y, desde la 8.4, en su único llamador
        // de producción: el servicio de indexación semántica.
        var conSegmentos = archivos.Where(a => a.Value.Contains("ExtractSegmentsAsync", StringComparison.Ordinal)).Select(a => a.Key).Order().ToArray();
        Assert.Equal(new[]
        {
            "AsistenteJuridico.Application/Common/Interfaces/AI/IDocumentTextExtractor.cs",
            "AsistenteJuridico.Infrastructure/Services/AI/DocumentTextExtractor.cs",
            "AsistenteJuridico.Infrastructure/Services/IndexacionSemanticaService.cs"
        }, conSegmentos);

        // Los componentes puros no tocan base de datos, archivos ni red.
        foreach (var componente in new[] { "AsistenteJuridico.Application/Common/Indexacion/Fragmentador.cs", "AsistenteJuridico.Application/Common/Indexacion/TextoNormalizador.cs" })
        {
            foreach (var prohibido in new[] { "DbContext", "SaveChanges", "System.IO", "File.", "HttpClient", "ILogger" })
            {
                Assert.DoesNotContain(prohibido, archivos[componente], StringComparison.Ordinal);
            }
        }

        // El extractor no escribe en base de datos.
        Assert.DoesNotContain("SaveChanges", archivos["AsistenteJuridico.Infrastructure/Services/AI/DocumentTextExtractor.cs"], StringComparison.Ordinal);
    }
}
