using System.Diagnostics;
using System.Security.Cryptography;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static AsistenteJuridico.Domain.Tests.Fase7.Fase72TestData;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.2 — FileStorageService sobre un sistema de archivos real: temporal y movimiento final, estructura
/// {tenant}/{expediente}/{guid}{ext}, límite de 25 MiB, SHA-256, MIME canónico, limpieza del temporal y defensas de
/// ruta (traversal, base con nombre parecido, symlinks/junctions).
/// </summary>
public class Fase72AlmacenamientoTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly string _raiz;
    private readonly string _base;
    private readonly FileStorageService _storage;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _expedienteId = Guid.NewGuid();

    public Fase72AlmacenamientoTests()
    {
        _raiz = Path.Combine(Path.GetTempPath(), "aj_fase72_" + Guid.NewGuid().ToString("N"));
        _base = Path.Combine(_raiz, "storage");
        Directory.CreateDirectory(_base);
        _storage = CrearStorage(_base);
    }

    private static FileStorageService CrearStorage(string basePath) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:BasePath"] = basePath }).Build(),
        NullLogger<FileStorageService>.Instance);

    public void Dispose()
    {
        try
        {
            // Quitar primero los enlaces para no borrar nunca el destino a través de ellos
            foreach (var directorio in Directory.EnumerateDirectories(_raiz, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).LinkTarget != null).ToList())
            {
                Directory.Delete(directorio);
            }

            Directory.Delete(_raiz, recursive: true);
        }
        catch { }
    }

    private Task<Application.Common.Interfaces.StoredDocumentoFile> GuardarAsync(
        byte[] contenido, string nombre = "demanda.pdf", string? mime = "application/pdf", long? longitudDeclarada = null, CancellationToken ct = default) =>
        _storage.SaveDocumentoAsync(_tenantId, _expedienteId, new MemoryStream(contenido), nombre, mime, longitudDeclarada ?? contenido.Length, ct);

    private string CarpetaTemporal => Path.Combine(_base, ".tmp");

    private void AssertSinTemporales()
    {
        Assert.True(!Directory.Exists(CarpetaTemporal) || !Directory.EnumerateFileSystemEntries(CarpetaTemporal).Any(),
            "Quedaron archivos temporales en .tmp");
    }

    private void AssertSinArchivosDeDocumento()
    {
        var carpetaTenant = Path.Combine(_base, _tenantId.ToString("N"));
        Assert.True(!Directory.Exists(carpetaTenant) || !Directory.EnumerateFiles(carpetaTenant, "*", SearchOption.AllDirectories).Any(),
            "Quedó un archivo de documento en el destino final");
    }

    // ── Estructura, hash y MIME ──────────────────────────────────────────

    [Fact]
    public async Task Guardar_EstructuraTenantExpedienteGuid_HashYMimeCanonico()
    {
        var contenido = Pdf("estructura");
        var resultado = await GuardarAsync(contenido, "Demanda Inicial (firmada).PDF", "application/x-pdf");

        var partes = resultado.RelativePath.Split('/');
        Assert.Equal(3, partes.Length);
        Assert.Equal(_tenantId.ToString("N"), partes[0]);
        Assert.Equal(_expedienteId.ToString("N"), partes[1]);
        Assert.Matches("^[0-9a-f]{32}\\.pdf$", partes[2]);              // GUID + extensión, nunca el nombre original
        Assert.DoesNotContain("Demanda", resultado.RelativePath, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("Demanda Inicial (firmada).PDF", resultado.NombreArchivoOriginal);
        Assert.Equal("application/pdf", resultado.ContentType);         // canónico, no el declarado
        Assert.Equal(contenido.Length, resultado.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(contenido)).ToLowerInvariant(), resultado.Sha256Hash);

        var rutaFisica = Path.Combine(_base, partes[0], partes[1], partes[2]);
        Assert.Equal(contenido, await File.ReadAllBytesAsync(rutaFisica));
        AssertSinTemporales();
    }

    [Fact]
    public async Task Guardar_DosVecesElMismoContenido_DosArchivosDistintos()
    {
        var contenido = Pdf("duplicado");
        var primero = await GuardarAsync(contenido);
        var segundo = await GuardarAsync(contenido);

        Assert.NotEqual(primero.RelativePath, segundo.RelativePath);
        Assert.Equal(primero.Sha256Hash, segundo.Sha256Hash);
        Assert.True(File.Exists(Path.Combine(_base, primero.RelativePath)));
        Assert.True(File.Exists(Path.Combine(_base, segundo.RelativePath)));
    }

    [Fact]
    public async Task Guardar_Txt_GuardaContentTypeCanonicoConCharset()
    {
        var resultado = await GuardarAsync("Escrito en UTF-8: acción"u8.ToArray(), "nota.txt", "text/plain");
        Assert.Equal("text/plain; charset=utf-8", resultado.ContentType);
        Assert.EndsWith(".txt", resultado.RelativePath);
    }

    // ── Límite de 25 MiB ─────────────────────────────────────────────────

    [Fact]
    public async Task Exactamente25MiB_Aceptado()
    {
        var resultado = await GuardarAsync(PdfDeTamanio(25 * MiB));

        Assert.Equal(25 * MiB, resultado.SizeBytes);
        Assert.Equal(25 * MiB, new FileInfo(Path.Combine(_base, resultado.RelativePath)).Length);
        AssertSinTemporales();
    }

    [Fact]
    public async Task VeinticincoMiBMasUno_SinLongitudDeclarada_CortaEnStreaming_413()
    {
        // Sin longitud declarada: el exceso se detecta durante la copia
        var ex = await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            _storage.SaveDocumentoAsync(_tenantId, _expedienteId, new MemoryStream(PdfDeTamanio(25 * MiB + 1)), "grande.pdf", "application/pdf", declaredLength: null));

        Assert.Equal(DocumentoErrorCodes.SizeExceeded, ex.ErrorCode);
        AssertSinTemporales();
        AssertSinArchivosDeDocumento();
    }

    [Fact]
    public async Task LongitudDeclaradaMayorA25MiB_413_SinLeerElContenido()
    {
        var stream = new StreamQueFallaAlLeer();
        var ex = await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            _storage.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "grande.pdf", "application/pdf", 25 * MiB + 1));

        Assert.Equal(DocumentoErrorCodes.SizeExceeded, ex.ErrorCode);
        Assert.False(stream.SeLeyo);
        AssertSinTemporales();
    }

    // ── Limpieza del temporal ────────────────────────────────────────────

    [Fact]
    public async Task ErrorDeValidacionDeContenido_SinTemporalNiArchivoFinal()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => GuardarAsync(Docx(conMacros: true), "macro.docx", MimeDocx));

        Assert.Equal(DocumentoErrorCodes.MagicBytesInvalid, ex.ErrorCode);
        Assert.True(Directory.Exists(CarpetaTemporal)); // el temporal llegó a crearse y se eliminó
        AssertSinTemporales();
        AssertSinArchivosDeDocumento();
    }

    [Fact]
    public async Task ExtensionOMimeRechazados_NoCreanNingunArchivo()
    {
        await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() => GuardarAsync(Pdf(), "script.exe", "application/octet-stream"));
        await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() => GuardarAsync(Pdf(), "demanda.pdf", "text/html"));

        AssertSinTemporales();
        AssertSinArchivosDeDocumento();
    }

    [Fact]
    public async Task Cancelacion_DuranteLaCopia_SinTemporalNiArchivoFinal()
    {
        using var cts = new CancellationTokenSource();
        var stream = new StreamQueCancela(PdfDeTamanio(2 * MiB), cts, cancelarTrasBytes: 200_000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _storage.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "demanda.pdf", "application/pdf", null, cts.Token));

        Assert.True(stream.BytesLeidos > 0);
        AssertSinTemporales();
        AssertSinArchivosDeDocumento();
    }

    [Fact]
    public async Task ErrorDeLecturaDelCliente_SinTemporal()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            _storage.SaveDocumentoAsync(_tenantId, _expedienteId, new StreamQueFallaAlLeer(), "demanda.pdf", "application/pdf", null));

        AssertSinTemporales();
    }

    // ── Rutas antiguas y path traversal ──────────────────────────────────

    [Fact]
    public async Task RutaAntigua_TenantArchivo_SigueSiendoLegible()
    {
        var carpetaTenant = Path.Combine(_base, _tenantId.ToString("N"));
        Directory.CreateDirectory(carpetaTenant);
        var nombre = Guid.NewGuid().ToString("N") + ".pdf";
        await File.WriteAllBytesAsync(Path.Combine(carpetaTenant, nombre), Pdf("antiguo"));

        await using var stream = await _storage.OpenReadFileAsync($"{_tenantId:N}/{nombre}");
        using var memoria = new MemoryStream();
        await stream.CopyToAsync(memoria);
        Assert.Equal(Pdf("antiguo"), memoria.ToArray());
    }

    [Theory]
    [InlineData("../fuera.pdf")]
    [InlineData("../../Windows/System32/drivers/etc/hosts")]
    [InlineData("tenant/../../fuera.pdf")]
    [InlineData("tenant/./archivo.pdf")]
    [InlineData("tenant//archivo.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\servidor\\recurso\\archivo.pdf")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("tenant\\..\\..\\fuera.pdf")]
    [InlineData("tenant/archivo.pdf:flujo")]
    [InlineData(".tmp/abc.upload")]
    [InlineData("tenant/.oculto")]
    public async Task RutaInsegura_403_EnLecturaYSinBorrarNada(string ruta)
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _storage.OpenReadFileAsync(ruta));
        Assert.Contains("Path Traversal", ex.Message);

        // La eliminación aplica la misma defensa: no lanza (es compensación) y no borra nada
        var testigo = Path.Combine(_raiz, "fuera.pdf");
        await File.WriteAllBytesAsync(testigo, Pdf());
        await _storage.DeleteFileIfExistsAsync(ruta);
        await _storage.DeleteFileIfExistsAsync("../fuera.pdf");
        Assert.True(File.Exists(testigo));
    }

    [Fact]
    public void RutaContenidaEnBase_ExigeSeparadorTrasLaBase()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.True(FileStorageService.RutaContenidaEnBase(_base, Path.Combine(_base, "t", "a.pdf")));
        Assert.True(FileStorageService.RutaContenidaEnBase(_base + sep, Path.Combine(_base, "a.pdf")));

        // Carpeta vecina cuyo nombre empieza por el texto de la base: fuera (el StartsWith antiguo la aceptaba)
        Assert.False(FileStorageService.RutaContenidaEnBase(_base, _base + "-evil" + sep + "secreto.pdf"));
        Assert.False(FileStorageService.RutaContenidaEnBase(_base, _base + "2" + sep + "a.pdf"));
        Assert.False(FileStorageService.RutaContenidaEnBase(_base, _base));
        Assert.False(FileStorageService.RutaContenidaEnBase(_base, Path.Combine(_base, "..", "fuera.pdf")));

        // Mayúsculas: solo equivalentes en Windows
        Assert.Equal(OperatingSystem.IsWindows(), FileStorageService.RutaContenidaEnBase(_base, Path.Combine(_base.ToUpperInvariant(), "a.pdf")));
    }

    [Fact]
    public async Task BaseConNombreParecido_NoSeConsideraDentroDeLaBase()
    {
        // {raiz}/storage es la base; {raiz}/storage-evil empieza por el mismo texto pero está fuera
        var vecina = Path.Combine(_raiz, "storage-evil");
        Directory.CreateDirectory(vecina);
        await File.WriteAllBytesAsync(Path.Combine(vecina, "secreto.pdf"), Pdf("secreto"));

        await Assert.ThrowsAsync<ForbiddenException>(() => _storage.OpenReadFileAsync("../storage-evil/secreto.pdf"));

        // También con la base configurada con separador final
        var storageConSeparador = CrearStorage(_base + Path.DirectorySeparatorChar);
        await Assert.ThrowsAsync<ForbiddenException>(() => storageConSeparador.OpenReadFileAsync("../storage-evil/secreto.pdf"));
        Assert.True(File.Exists(Path.Combine(vecina, "secreto.pdf")));
    }

    // ── Symlinks / junctions / puntos de reanálisis ──────────────────────

    /// <summary>
    /// Crea un enlace de directorio: symlink si el sistema lo permite; en Windows sin privilegios, junction
    /// (mklink /J, no requiere administrador). Ambos son puntos de reanálisis.
    /// </summary>
    private static void CrearEnlaceDeDirectorio(string enlace, string destino)
    {
        try
        {
            Directory.CreateSymbolicLink(enlace, destino);
            return;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            // Sin privilegio para symlinks: se usa una junction
        }

        var proceso = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{enlace}\" \"{destino}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        proceso.WaitForExit();
        Assert.True(proceso.ExitCode == 0 && Directory.Exists(enlace),
            "No se pudo crear ni un symlink ni una junction para la prueba: " + proceso.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task EnlaceEnCarpetaDeTenant_403_EnEscrituraLecturaYEliminacion()
    {
        var destinoExterno = Path.Combine(_raiz, "externo");
        Directory.CreateDirectory(Path.Combine(destinoExterno, _expedienteId.ToString("N")));
        var archivoExterno = Path.Combine(destinoExterno, _expedienteId.ToString("N"), "victima.pdf");
        await File.WriteAllBytesAsync(archivoExterno, Pdf("victima"));

        CrearEnlaceDeDirectorio(Path.Combine(_base, _tenantId.ToString("N")), destinoExterno);
        Assert.True((File.GetAttributes(Path.Combine(_base, _tenantId.ToString("N"))) & FileAttributes.ReparsePoint) != 0);

        // Escritura: no escribe a través del enlace, y no deja temporal
        var escritura = await Assert.ThrowsAsync<ForbiddenException>(() => GuardarAsync(Pdf("nuevo")));
        Assert.Contains("enlace simbólico o punto de reanálisis", escritura.Message);
        Assert.Single(Directory.EnumerateFiles(destinoExterno, "*", SearchOption.AllDirectories));
        AssertSinTemporales();

        // Lectura a través del enlace: 403 (no 404)
        var rutaPorEnlace = $"{_tenantId:N}/{_expedienteId:N}/victima.pdf";
        var lectura = await Assert.ThrowsAsync<ForbiddenException>(() => _storage.OpenReadFileAsync(rutaPorEnlace));
        Assert.Contains("enlace simbólico o punto de reanálisis", lectura.Message);

        // Eliminación a través del enlace: no borra el archivo externo
        await _storage.DeleteFileIfExistsAsync(rutaPorEnlace);
        Assert.True(File.Exists(archivoExterno));
    }

    [Fact]
    public async Task EnlaceEnCarpetaTemporal_403_SinEscribirFuera()
    {
        var destinoExterno = Path.Combine(_raiz, "tmp-externo");
        Directory.CreateDirectory(destinoExterno);
        CrearEnlaceDeDirectorio(CarpetaTemporal, destinoExterno);

        await Assert.ThrowsAsync<ForbiddenException>(() => GuardarAsync(Pdf()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinoExterno));
    }

    [Fact]
    public async Task BaseQueEsUnEnlace_SePermite()
    {
        // La base misma puede ser un montaje o enlace (por ejemplo, un volumen de Docker)
        var baseReal = Path.Combine(_raiz, "base-real");
        Directory.CreateDirectory(baseReal);
        var baseEnlace = Path.Combine(_raiz, "base-enlace");
        CrearEnlaceDeDirectorio(baseEnlace, baseReal);

        var storage = CrearStorage(baseEnlace);
        var resultado = await storage.SaveDocumentoAsync(_tenantId, _expedienteId, new MemoryStream(Pdf()), "demanda.pdf", "application/pdf", null);

        await using var lectura = await storage.OpenReadFileAsync(resultado.RelativePath);
        Assert.True(File.Exists(Path.Combine(baseReal, resultado.RelativePath)));
    }

    // ── Streams de prueba ────────────────────────────────────────────────

    private sealed class StreamQueFallaAlLeer : Stream
    {
        public bool SeLeyo { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            SeLeyo = true;
            throw new IOException("Conexión del cliente interrumpida");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            SeLeyo = true;
            throw new IOException("Conexión del cliente interrumpida");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StreamQueCancela(byte[] contenido, CancellationTokenSource cts, int cancelarTrasBytes) : MemoryStream(contenido)
    {
        public long BytesLeidos { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leidos = await base.ReadAsync(buffer, cancellationToken);
            BytesLeidos += leidos;
            if (BytesLeidos >= cancelarTrasBytes)
            {
                await cts.CancelAsync();
            }

            return leidos;
        }
    }
}
