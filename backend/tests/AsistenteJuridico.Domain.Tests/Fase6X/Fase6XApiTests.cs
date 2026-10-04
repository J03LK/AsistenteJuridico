using System.Net;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AsistenteJuridico.Domain.Tests.Fase6X;

/// <summary>
/// Fase 6.X — Pipeline HTTP completo: D-2 (403 DOCUMENT_ACCESS_DENIED sin llamar al proveedor), DocumentoIds (400
/// con código y mensajes) y DA-7 (los 422 de extracción llevan su código en errors).
/// </summary>
public class Fase6XApiTests : IDisposable
{
    private readonly ProveedorFijo _proveedor = new();
    private readonly AiApiFactory _factory;
    private readonly HttpClient _client;
    private readonly Guid _seniorId = Guid.NewGuid();
    private readonly Guid _asistenteId = Guid.NewGuid();

    public Fase6XApiTests()
    {
        _factory = new AiApiFactory(AiApiFactory.WithProvider(_proveedor));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, string[] Errors)> PostAsync(string ruta, string token, object cuerpo)
    {
        using var respuesta = await _client.SendAsync(_factory.Request(HttpMethod.Post, $"/api/v1/ai/{ruta}", token, cuerpo));
        var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;
        var errores = json.TryGetProperty("errors", out var lista) && lista.ValueKind == JsonValueKind.Array
            ? lista.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];
        return (respuesta.StatusCode, errores);
    }

    private async Task<Guid> DocumentoAsync(Guid expedienteId, byte[] contenido, string nombre, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var archivo = await scope.ServiceProvider.GetRequiredService<IFileStorageService>()
            .SaveDocumentoAsync(_factory.TenantId, expedienteId, new MemoryStream(contenido), nombre, mime, contenido.Length);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var documento = new Documento
        {
            Id = Guid.NewGuid(), TenantId = _factory.TenantId, ExpedienteId = expedienteId, Titulo = nombre, TipoDocumento = "Escrito",
            NombreArchivoOriginal = archivo.NombreArchivoOriginal, RutaAlmacenamiento = archivo.RelativePath, ContentType = archivo.ContentType,
            TamanioBytes = archivo.SizeBytes, HashSha256 = archivo.Sha256Hash, EstadoIa = EstadoProcesamientoIa.Pendiente
        };
        db.Documentos.Add(documento);
        await db.SaveChangesAsync();
        return documento.Id;
    }

    [Fact]
    public async Task ResumirExpediente_AsistenteSinTarea_403Codigo_SinProveedorNiConversacionNiUso()
    {
        _factory.CreateUserToken(_seniorId, Roles.AbogadoSenior);   // responsable del expediente (FK)
        var token = _factory.CreateUserToken(_asistenteId, Roles.AsistenteLegal);
        var (expedienteId, _) = _factory.SeedExpedienteConDocumento(_seniorId);

        var (status, errores) = await PostAsync("resumir-expediente", token, new { expedienteId });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(["DOCUMENT_ACCESS_DENIED"], errores);
        Assert.Empty(_proveedor.Peticiones);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.AIConversations.IgnoreQueryFilters().CountAsync(c => c.UsuarioId == _asistenteId)));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.AIUsageLogs.IgnoreQueryFilters().CountAsync(u => u.UsuarioId == _asistenteId)));
    }

    [Fact]
    public async Task ResumirExpediente_AsistenteConTareaVigente_200ConElTextoReal()
    {
        _factory.CreateUserToken(_seniorId, Roles.AbogadoSenior);   // responsable del expediente (FK)
        var token = _factory.CreateUserToken(_asistenteId, Roles.AsistenteLegal);
        var (expedienteId, _) = _factory.SeedExpedienteConDocumento(_seniorId);
        await _factory.QueryDbAsync(async db =>
        {
            db.Tareas.Add(new Tarea
            {
                Id = Guid.NewGuid(), TenantId = _factory.TenantId, ExpedienteId = expedienteId, AsignadoAUsuarioId = _asistenteId,
                Titulo = "Revisar contrato", Estado = EstadoTarea.EnProgreso, Prioridad = Prioridad.Media, FechaVencimiento = DateTime.UtcNow.AddDays(2)
            });
            return await db.SaveChangesAsync();
        });

        var (status, _) = await PostAsync("resumir-expediente", token, new { expedienteId });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Cláusula primera: objeto.", string.Join("\n", _proveedor.Peticiones.Single().Messages.Select(m => m.Content)));
    }

    [Fact]
    public async Task ResumirExpediente_DocumentoIdsInvalidos_400ConCodigoYMensaje()
    {
        var token = _factory.CreateUserToken(_seniorId, Roles.AbogadoSenior);
        var (expedienteId, _) = _factory.SeedExpedienteConDocumento(_seniorId);
        var invalido = Guid.NewGuid();

        var (status, errores) = await PostAsync("resumir-expediente", token, new { expedienteId, documentoIds = new[] { invalido } });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(["DOCUMENT_DOCUMENTS_INVALID", $"El documento {invalido} no es válido para este expediente."], errores);
        Assert.Empty(_proveedor.Peticiones);
    }

    [Fact]
    public async Task ResumirDocumento_Png_422ConCodigoEnErrors()
    {
        var token = _factory.CreateUserToken(_seniorId, Roles.AbogadoSenior);
        var (expedienteId, _) = _factory.SeedExpedienteConDocumento(_seniorId);
        var png = await DocumentoAsync(expedienteId, Archivos.Png(), "foto.png", "image/png");

        var (status, errores) = await PostAsync("resumir-documento", token, new { documentoId = png });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(["DOCUMENT_TEXT_UNSUPPORTED"], errores);
        Assert.Empty(_proveedor.Peticiones);
    }

    [Fact]
    public async Task ResumirDocumento_ExcedeContexto_422ConCodigoEnErrors()
    {
        var token = _factory.CreateUserToken(_seniorId, Roles.AbogadoSenior);
        var (expedienteId, _) = _factory.SeedExpedienteConDocumento(_seniorId);
        var largo = await DocumentoAsync(expedienteId, Archivos.Txt(new string('x', 30_001)), "largo.txt", "text/plain");

        var (status, errores) = await PostAsync("resumir-documento", token, new { documentoId = largo });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(["DOCUMENT_EXCEEDS_CONTEXT_LIMIT"], errores);
        Assert.Empty(_proveedor.Peticiones);
    }

    [Fact]
    public async Task ExtraerDocumento_PdfDanado_422Invalid_YDocumentoFallido()
    {
        var token = _factory.CreateUserToken(_seniorId, Roles.AbogadoSenior);
        var (expedienteId, _) = _factory.SeedExpedienteConDocumento(_seniorId);
        var danado = await DocumentoAsync(expedienteId, Archivos.PdfDanado(), "danado.pdf", "application/pdf");

        var (status, errores) = await PostAsync("extraer-documento", token, new { documentoId = danado });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(["DOCUMENT_TEXT_INVALID"], errores);
        Assert.Empty(_proveedor.Peticiones);
        var estado = await _factory.QueryDbAsync(db => db.Documentos.IgnoreQueryFilters().Where(d => d.Id == danado).Select(d => d.EstadoIa).SingleAsync());
        Assert.Equal(EstadoProcesamientoIa.Fallido, estado);
    }
}
