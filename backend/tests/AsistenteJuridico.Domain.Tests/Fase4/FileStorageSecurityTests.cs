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

        var (physicalName, relativePath, verifiedContentType, size, hash) =
            await _storageService.SaveFileAsync(_tenantId, stream, "demanda_inicial.pdf", "application/pdf");

        Assert.Equal("application/pdf", verifiedContentType);
        Assert.Equal(pdfBytes.Length, size);
        Assert.EndsWith(".pdf", physicalName);

        // Verificar SHA-256 esperado
        using var sha256 = SHA256.Create();
        var expectedHash = Convert.ToHexString(sha256.ComputeHash(pdfBytes)).ToLowerInvariant();
        Assert.Equal(expectedHash, hash);

        // Verificar que existe físicamente en el storage
        var fullPath = Path.Combine(_tempStorageDir, relativePath);
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task SaveFileAsync_ArchivoDocxValido_GuardaConExito()
    {
        // PK\x03\x04 zip header for docx
        var docxBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00 };
        using var stream = new MemoryStream(docxBytes);

        var (physicalName, relativePath, verifiedContentType, size, hash) =
            await _storageService.SaveFileAsync(_tenantId, stream, "contrato.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", verifiedContentType);
        Assert.EndsWith(".docx", physicalName);
    }

    [Fact]
    public async Task SaveFileAsync_EjecutableDisfrazadoDePdf_LanzaValidationException()
    {
        // MZ header (Windows PE executable) pero con extensión .pdf
        var exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 };
        using var stream = new MemoryStream(exeBytes);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _storageService.SaveFileAsync(_tenantId, stream, "virus_oculto.pdf", "application/pdf"));

        Assert.Contains("Magic Bytes", ex.ValidationErrors.First());
    }

    [Fact]
    public async Task SaveFileAsync_ExtensionNoPermitida_LanzaValidationException()
    {
        var bytes = Encoding.UTF8.GetBytes("echo 'malicious script'");
        using var stream = new MemoryStream(bytes);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _storageService.SaveFileAsync(_tenantId, stream, "script.exe", "application/x-msdownload"));

        Assert.Contains("La extensión '.exe' no está permitida", ex.ValidationErrors.First());
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

        var (_, relativePath, _, _, _) =
            await _storageService.SaveFileAsync(_tenantId, stream, "test.pdf", "application/pdf");

        var fullPath = Path.Combine(_tempStorageDir, relativePath);
        Assert.True(File.Exists(fullPath));

        // Compensación / borrado
        await _storageService.DeleteFileIfExistsAsync(relativePath);

        Assert.False(File.Exists(fullPath));
    }
}
