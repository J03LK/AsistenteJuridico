using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

using Microsoft.Extensions.Logging.Abstractions;

namespace AsistenteJuridico.Domain.Tests.Fase4;

public class FileStorageSecurityTests : IDisposable
{
    private readonly string _tempStorageDir;
    private readonly FileStorageService _storageService;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _expedienteId = Guid.NewGuid();

    public FileStorageSecurityTests()
    {
        _tempStorageDir = Path.Combine(Path.GetTempPath(), "aj_test_storage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempStorageDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "FileStorage:BasePath", _tempStorageDir }
            })
            .Build();

        _storageService = new FileStorageService(config, NullLogger<FileStorageService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempStorageDir))
            {
                Directory.Delete(_tempStorageDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task SaveFileAsync_ArchivoPdfValido_GuardaConExitoYCalculaSha256()
    {
        // %PDF-1.4 header
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");
        using var stream = new MemoryStream(pdfBytes);

        var resultado = await _storageService.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "demanda_inicial.pdf", "application/pdf", pdfBytes.Length);

        Assert.Equal("application/pdf", resultado.ContentType);
        Assert.Equal(pdfBytes.Length, resultado.SizeBytes);
        Assert.EndsWith(".pdf", resultado.RelativePath);

        // Verificar SHA-256 esperado
        using var sha256 = SHA256.Create();
        var expectedHash = Convert.ToHexString(sha256.ComputeHash(pdfBytes)).ToLowerInvariant();
        Assert.Equal(expectedHash, resultado.Sha256Hash);

        // Verificar que existe físicamente en el storage
        var fullPath = Path.Combine(_tempStorageDir, resultado.RelativePath);
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task SaveFileAsync_ArchivoDocxValido_GuardaConExito()
    {
        // Fase 7.2: un DOCX debe ser un ZIP OOXML real ([Content_Types].xml + word/), no solo la cabecera PK..
        var docxBytes = CrearDocxMinimo();
        using var stream = new MemoryStream(docxBytes);

        var resultado = await _storageService.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "contrato.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", docxBytes.Length);

        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", resultado.ContentType);
        Assert.EndsWith(".docx", resultado.RelativePath);
    }

    [Fact]
    public async Task SaveFileAsync_EjecutableDisfrazadoDePdf_LanzaValidationException()
    {
        // MZ header (Windows PE executable) pero con extensión .pdf
        var exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 };
        using var stream = new MemoryStream(exeBytes);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _storageService.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "virus_oculto.pdf", "application/pdf", exeBytes.Length));

        Assert.Contains("Magic Bytes", ex.ValidationErrors.First());
    }

    [Fact]
    public async Task SaveFileAsync_ExtensionNoPermitida_LanzaUnsupportedMediaType415()
    {
        // Fase 7.2: extensión fuera de la lista -> 415 DOCUMENT_TYPE_NOT_ALLOWED (antes ValidationException 400)
        var bytes = Encoding.UTF8.GetBytes("echo 'malicious script'");
        using var stream = new MemoryStream(bytes);

        var ex = await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() =>
            _storageService.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "script.exe", "application/x-msdownload", bytes.Length));

        Assert.Contains("La extensión '.exe' no está permitida", ex.Message);
        Assert.Equal("DOCUMENT_TYPE_NOT_ALLOWED", ex.ErrorCode);
    }

    [Fact]
    public async Task OpenReadFileAsync_IntentoDePathTraversal_LanzaForbiddenException()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _storageService.OpenReadFileAsync("../../Windows/System32/drivers/etc/hosts"));

        Assert.Contains("Path Traversal", ex.Message);
    }

    [Fact]
    public async Task DeleteFileIfExistsAsync_ArchivoExistente_LoEliminaFisicamente()
    {
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 test");
        using var stream = new MemoryStream(pdfBytes);

        var resultado = await _storageService.SaveDocumentoAsync(_tenantId, _expedienteId, stream, "test.pdf", "application/pdf", pdfBytes.Length);

        var fullPath = Path.Combine(_tempStorageDir, resultado.RelativePath);
        Assert.True(File.Exists(fullPath));

        // Compensación / borrado
        await _storageService.DeleteFileIfExistsAsync(resultado.RelativePath);

        Assert.False(File.Exists(fullPath));
    }

    private static byte[] CrearDocxMinimo()
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (nombre, contenido) in new[]
            {
                ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
                ("word/document.xml", "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>")
            })
            {
                using var escritor = new StreamWriter(zip.CreateEntry(nombre).Open());
                escritor.Write(contenido);
            }
        }

        return memoria.ToArray();
    }
}
