using System.Text.Json;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.AI.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase6X;

/// <summary>
/// Fase 6.X — Máquina de estados D8 de ExtractFromDocumentAsync (nunca Procesado sin texto real), lease
/// (IaProcesandoDesde), conflicto de concurrencia con AIUsageLog independiente (X1-C1/C2) y cancelación (D9).
/// </summary>
public class Fase6XExtraccionTests : IAsyncLifetime
{
    private const string MetadatosPrevios = "{\"previo\":true}";
    private readonly Escenario6X _e = new();

    public Task InitializeAsync() => _e.SembrarAsync();

    public Task DisposeAsync()
    {
        _e.Dispose();
        return Task.CompletedTask;
    }

    private Task<Guid> PdfValidoAsync(EstadoProcesamientoIa estado = EstadoProcesamientoIa.Pendiente, string? metadatos = null) =>
        _e.DocumentoAsync(Archivos.Pdf("Hechos: el demandado incumplio el contrato"), "hechos.pdf", "application/pdf", estado: estado, metadatos: metadatos);

    private async Task<Exception?> ExtraerAsync(Guid documentoId, IAIProvider proveedor, CancellationToken token = default,
        params IInterceptor[] interceptores)
    {
        await using var context = _e.Contexto(null, interceptores);
        var servicio = _e.Servicio(context, _e.SeniorId, Roles.AbogadoSenior, proveedor);
        return await Record.ExceptionAsync(() => servicio.ExtractFromDocumentAsync(new AIExtractRequestDto(documentoId), token));
    }

    /// <summary>MetadatosJson es jsonb: PostgreSQL normaliza el formato, así que se compara el contenido JSON.</summary>
    private static void AssertMismoJson(string esperado, string? real) =>
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(esperado), System.Text.Json.Nodes.JsonNode.Parse(real!)),
            $"JSON distinto. Esperado: {esperado}. Real: {real}");

    private async Task<string?> CausaAuditadaAsync(Guid documentoId)
    {
        var evento = Assert.Single(await _e.AuditoriasAsync(documentoId, "AI_EXTRACTION_FAILED"));
        return JsonDocument.Parse(evento.ValoresNuevosJson!).RootElement.GetProperty("causa").GetString();
    }

    // ── Éxito ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exito_Procesado_ConTextoReal_LeaseLimpio_YAuditado()
    {
        var id = await PdfValidoAsync();
        var proveedor = new ProveedorFijo();

        var error = await ExtraerAsync(id, proveedor);

        Assert.Null(error);
        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Procesado, documento.EstadoIa);
        Assert.Null(documento.IaProcesandoDesde);                                     // X1-N2
        AssertMismoJson(proveedor.Respuesta, documento.MetadatosJson);
        Assert.Contains("el demandado incumplio el contrato", proveedor.Peticiones.Single().Messages.Single().Content);

        var evento = Assert.Single(await _e.AuditoriasAsync(id, "AI_EXTRACTION_COMPLETED"));
        Assert.Contains("modelo-6x", evento.ValoresNuevosJson);
        Assert.DoesNotContain("demandado", evento.ValoresNuevosJson);                 // sin contenido
        Assert.Contains(await _e.UsosAsync(_e.SeniorId), u => u.Exitoso);
    }

    [Fact]
    public async Task X1N1_ProcesoNuevo_SiempreFijaIaProcesandoDesde_EnLaTransicion()
    {
        var id = await PdfValidoAsync();
        var antes = DateTime.UtcNow.AddSeconds(-2);
        (int Estado, DateTime? Desde) leido = default;
        var proveedor = new ProveedorFijo
        {
            // Lectura desde OTRA conexión, tras la transición a Procesando y antes de que responda el proveedor.
            AlLlamar = async ct =>
            {
                await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
                await conexion.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT \"EstadoIa\", \"IaProcesandoDesde\" FROM documentos WHERE \"Id\" = @id", conexion);
                cmd.Parameters.AddWithValue("id", id);
                await using var lector = await cmd.ExecuteReaderAsync(ct);
                await lector.ReadAsync(ct);
                leido = (lector.GetInt32(0), lector.IsDBNull(1) ? null : lector.GetDateTime(1));
            }
        };

        Assert.Null(await ExtraerAsync(id, proveedor));

        Assert.Equal((int)EstadoProcesamientoIa.Procesando, leido.Estado);
        Assert.NotNull(leido.Desde);
        Assert.InRange(leido.Desde!.Value.ToUniversalTime(), antes, DateTime.UtcNow.AddSeconds(2));
        Assert.Null((await _e.LeerDocumentoAsync(id)).IaProcesandoDesde);           // X1-N2
    }

    [Fact]
    public async Task ReintentoTrasFallido_Procesado()
    {
        var id = await PdfValidoAsync(EstadoProcesamientoIa.Fallido);
        Assert.Null(await ExtraerAsync(id, new ProveedorFijo()));
        Assert.Equal(EstadoProcesamientoIa.Procesado, (await _e.LeerDocumentoAsync(id)).EstadoIa);
    }

    // ── Fallos de extracción: Fallido, nunca Procesado; MetadatosJson se conserva (DA-8) ──

    public static TheoryData<string> FallosDeExtraccion => new() { "inexistente", "vacio", "pdf-danado", "docx-danado", "ruta-insegura", "excede-contexto" };

    [Theory]
    [MemberData(nameof(FallosDeExtraccion))]
    public async Task FalloDeExtraccion_Fallido_SinProveedor_ConservaMetadatos(string caso)
    {
        var (ruta, contentType) = caso switch
        {
            "inexistente" => ($"{_e.TenantId:N}/{_e.ExpedienteId:N}/{Guid.NewGuid():N}.pdf", "application/pdf"),
            "vacio" => (_e.Almacenamiento.EscribirCrudo(_e.TenantId, _e.ExpedienteId, Archivos.PdfSinTexto(), ".pdf"), "application/pdf"),
            "pdf-danado" => (_e.Almacenamiento.EscribirCrudo(_e.TenantId, _e.ExpedienteId, Archivos.PdfDanado(), ".pdf"), "application/pdf"),
            "docx-danado" => (_e.Almacenamiento.EscribirCrudo(_e.TenantId, _e.ExpedienteId, Archivos.DocxDanado(), ".docx"),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            "ruta-insegura" => ("../fuera/secreto.pdf", "application/pdf"),
            _ => (_e.Almacenamiento.EscribirCrudo(_e.TenantId, _e.ExpedienteId, Archivos.Txt(new string('x', 30_001)), ".txt"), "text/plain; charset=utf-8")
        };
        var id = await _e.DocumentoCrudoAsync(ruta, contentType, EstadoProcesamientoIa.Procesado, MetadatosPrevios);
        var proveedor = new ProveedorFijo();

        var error = await ExtraerAsync(id, proveedor);

        var (tipo, codigo, causa) = caso switch
        {
            "inexistente" => (typeof(NotFoundException), DocumentoErrorCodes.FileNotFound, "FileNotFound"),
            "vacio" => (typeof(BusinessRuleException), AIDocumentErrorCodes.TextEmpty, "Empty"),
            "pdf-danado" or "docx-danado" => (typeof(BusinessRuleException), AIDocumentErrorCodes.TextInvalid, "InvalidContent"),
            "ruta-insegura" => (typeof(ForbiddenException), (string?)null, "Forbidden"),
            _ => (typeof(DocumentContextExceededException), "DOCUMENT_EXCEEDS_CONTEXT_LIMIT", "ContextExceeded")
        };
        Assert.IsType(tipo, error);
        Assert.Equal(codigo, ((DomainException)error!).ErrorCode);
        Assert.Empty(proveedor.Peticiones);

        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Fallido, documento.EstadoIa);
        Assert.Null(documento.IaProcesandoDesde);
        AssertMismoJson(MetadatosPrevios, documento.MetadatosJson);
        Assert.Equal(causa, await CausaAuditadaAsync(id));
    }

    [Fact]
    public async Task TimeoutDeExtraccion_Fallido_ConservaMetadatos_Auditado_SinProveedor()
    {
        var paginas = Enumerable.Range(0, 60).Select(i => $"Pagina {i} del expediente").ToArray();
        var lento = new StreamLento(Archivos.Pdf(paginas));
        var extractor = new DocumentTextExtractor(new AlmacenamientoLento(lento),
            Microsoft.Extensions.Options.Options.Create(new DocumentTextExtractionOptions { TimeoutSeconds = 0.3 }));
        var id = await _e.DocumentoCrudoAsync("t/e/lento.pdf", "application/pdf", EstadoProcesamientoIa.Procesado, MetadatosPrevios);
        var proveedor = new ProveedorFijo();

        await using var context = _e.Contexto();
        var servicio = _e.Servicio(context, _e.SeniorId, Roles.AbogadoSenior, proveedor, extractor);
        var error = await Record.ExceptionAsync(() => servicio.ExtractFromDocumentAsync(new AIExtractRequestDto(id)));

        Assert.Equal(AIDocumentErrorCodes.TextExtractionFailed, Assert.IsType<BusinessRuleException>(error).ErrorCode);
        Assert.Empty(proveedor.Peticiones);
        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Fallido, documento.EstadoIa);
        Assert.Null(documento.IaProcesandoDesde);
        AssertMismoJson(MetadatosPrevios, documento.MetadatosJson);
        Assert.Equal("ExtractionFailed", await CausaAuditadaAsync(id));

        // El análisis no sigue leyendo el archivo después de que la operación terminó.
        var lecturas = lento.Lecturas;
        await Task.Delay(800);
        Assert.Equal(lecturas, lento.Lecturas);
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("application/msword", ".doc")]
    public async Task FormatoNoSoportado_422_SinCambioDeEstado(string contentType, string extension)
    {
        var ruta = _e.Almacenamiento.EscribirCrudo(_e.TenantId, _e.ExpedienteId, Archivos.Png(), extension);
        var id = await _e.DocumentoCrudoAsync(ruta, contentType, EstadoProcesamientoIa.Procesado, MetadatosPrevios);
        var antes = await _e.LeerDocumentoAsync(id);
        var proveedor = new ProveedorFijo();

        var error = await ExtraerAsync(id, proveedor);

        Assert.Equal(AIDocumentErrorCodes.TextUnsupported, Assert.IsType<BusinessRuleException>(error).ErrorCode);
        Assert.Empty(proveedor.Peticiones);
        var despues = await _e.LeerDocumentoAsync(id);
        Assert.Equal((antes.EstadoIa, antes.MetadatosJson, antes.Version), (despues.EstadoIa, despues.MetadatosJson, despues.Version));
        Assert.Empty(await _e.AuditoriasAsync(id, "AI_EXTRACTION_FAILED"));
    }

    [Fact]
    public async Task DocumentoEnProcesando_409DocumentProcessing_SinCambios()
    {
        var id = await PdfValidoAsync(EstadoProcesamientoIa.Procesando);
        var antes = await _e.LeerDocumentoAsync(id);

        var error = await ExtraerAsync(id, new ProveedorFijo());

        Assert.Equal(DocumentoErrorCodes.Processing, Assert.IsType<ConflictException>(error).ErrorCode);
        Assert.Equal(antes.Version, (await _e.LeerDocumentoAsync(id)).Version);
    }

    // ── Proveedor ────────────────────────────────────────────────────────

    [Fact]
    public async Task FalloDelProveedor_Fallido_502_UsoFallidoRegistrado()
    {
        var id = await PdfValidoAsync(metadatos: MetadatosPrevios);

        var error = await ExtraerAsync(id, new ThrowingAIProvider());

        Assert.IsType<AIProviderException>(error);
        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Fallido, documento.EstadoIa);
        Assert.Null(documento.IaProcesandoDesde);
        AssertMismoJson(MetadatosPrevios, documento.MetadatosJson);
        Assert.Equal("PROVIDER_ERROR", await CausaAuditadaAsync(id));
        Assert.Contains(await _e.UsosAsync(_e.SeniorId), u => !u.Exitoso);
    }

    [Fact]
    public async Task TimeoutDelProveedor_Fallido_502Timeout()
    {
        var id = await PdfValidoAsync();

        var error = await ExtraerAsync(id, new ProveedorFijo { Lanzar = new AIProviderTimeoutException() });

        Assert.Equal("AI_PROVIDER_TIMEOUT", Assert.IsType<AIProviderTimeoutException>(error).ErrorCode);
        Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(id)).EstadoIa);
        Assert.Equal("PROVIDER_TIMEOUT", await CausaAuditadaAsync(id));
    }

    // ── Cancelación (D9) ─────────────────────────────────────────────────

    [Fact]
    public async Task CancelacionDuranteElProveedor_SePropaga_Fallido_AuditoriaCancelled()
    {
        var id = await PdfValidoAsync();
        using var cts = new CancellationTokenSource();
        var proveedor = new ProveedorFijo
        {
            AlLlamar = async ct =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
        };

        var error = await ExtraerAsync(id, proveedor, cts.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Fallido, documento.EstadoIa);
        Assert.Null(documento.IaProcesandoDesde);
        Assert.Equal("CANCELLED", await CausaAuditadaAsync(id));
        Assert.Contains(await _e.UsosAsync(_e.SeniorId), u => !u.Exitoso);
        Assert.DoesNotContain(await _e.UsosAsync(_e.SeniorId), u => u.Exitoso);
    }

    // ── X1-C1 / X1-C2: conflicto de concurrencia tras la respuesta del proveedor ──

    private static Func<CancellationToken, Task> RecuperacionExterna(Guid documentoId) => async ct =>
    {
        // Simula a la recuperación por lease (u otra operación) cambiando la fila desde otra conexión.
        await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
        await conexion.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE documentos SET \"EstadoIa\" = 3, \"IaProcesandoDesde\" = NULL, \"MetadatosJson\" = '{\"otra\":1}' WHERE \"Id\" = @id", conexion);
        cmd.Parameters.AddWithValue("id", documentoId);
        await cmd.ExecuteNonQueryAsync(ct);
    };

    [Fact]
    public async Task X1C1_ConflictoTrasElProveedor_409_NoSobrescribe_YUsoFallidoIndependiente()
    {
        var id = await PdfValidoAsync();
        var proveedor = new ProveedorFijo { AlLlamar = RecuperacionExterna(id) };

        var error = await ExtraerAsync(id, proveedor);

        Assert.Equal(DocumentoErrorCodes.ConcurrencyConflict, Assert.IsType<ConflictException>(error).ErrorCode);

        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Fallido, documento.EstadoIa);          // el de la otra operación
        AssertMismoJson("{\"otra\":1}", documento.MetadatosJson);
        Assert.Empty(await _e.AuditoriasAsync(id, "AI_EXTRACTION_COMPLETED"));

        var usos = await _e.UsosAsync(_e.SeniorId);
        Assert.DoesNotContain(usos, u => u.Exitoso);
        var uso = Assert.Single(usos);
        Assert.False(uso.Exitoso);
        Assert.Equal(DocumentoErrorCodes.ConcurrencyConflict, uso.CodigoError);
        Assert.Equal(ProveedorFijo.TokensEntrada, uso.TokensEntrada);
        Assert.Equal(ProveedorFijo.TokensSalida, uso.TokensSalida);
        Assert.Equal(ProveedorFijo.TokensEntrada + ProveedorFijo.TokensSalida, uso.TotalTokens);
        Assert.True(uso.CostoEstimadoUsd > 0);                                     // consumo real, no 0
        Assert.Equal("modelo-6x", uso.ModelId);
        Assert.Equal("proveedor-6x", uso.ProviderId);
    }

    [Fact]
    public async Task X1C2_ConflictoYFallaElRegistroIndependiente_409_YLogAiUsageNoRegistrado()
    {
        var id = await PdfValidoAsync();
        var interceptor = new FallarRegistroDeUsoIndependiente { Armado = true };
        var logger = new LoggerEnLista<AIService>();
        var proveedor = new ProveedorFijo { AlLlamar = RecuperacionExterna(id) };

        await using var context = _e.Contexto(null, interceptor);
        var servicio = _e.Servicio(context, _e.SeniorId, Roles.AbogadoSenior, proveedor, logger: logger);
        var error = await Record.ExceptionAsync(() => servicio.ExtractFromDocumentAsync(new AIExtractRequestDto(id)));

        Assert.Equal(DocumentoErrorCodes.ConcurrencyConflict, Assert.IsType<ConflictException>(error).ErrorCode);
        Assert.Empty(await _e.UsosAsync(_e.SeniorId));
        Assert.Contains(logger.Mensajes, m => m.Contains("[AI_USAGE_NO_REGISTRADO]") && m.Contains(id.ToString()));
        Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(id)).EstadoIa);
    }
}
