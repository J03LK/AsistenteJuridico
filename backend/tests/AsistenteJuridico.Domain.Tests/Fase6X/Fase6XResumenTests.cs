using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.AI.DTOs;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Services.AI;

namespace AsistenteJuridico.Domain.Tests.Fase6X;

/// <summary>
/// Fase 6.X — D-2 (autorización documental de resumir-expediente), D2 (DocumentoIds), resumen con varios documentos
/// (S-M1..S-M3, DA-13 = A) y D1 (el chat nunca lee documentos). PostgreSQL y FileStorageService reales.
/// </summary>
public class Fase6XResumenTests : IAsyncLifetime
{
    private readonly Escenario6X _e = new();
    private Guid _documentoA;
    private Guid _documentoB;

    public async Task InitializeAsync()
    {
        await _e.SembrarAsync();
        _documentoA = await _e.DocumentoAsync(Archivos.Txt("Texto del documento A: hechos del caso."), "a.txt", "text/plain", creado: DateTime.UtcNow.AddMinutes(-3));
        _documentoB = await _e.DocumentoAsync(Archivos.Pdf("Texto del documento B en PDF"), "b.pdf", "application/pdf", creado: DateTime.UtcNow.AddMinutes(-2));
    }

    public Task DisposeAsync()
    {
        _e.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(Exception? Error, ProveedorFijo Proveedor)> ResumirComoAsync(
        Guid usuarioId, string rol, Guid? expedienteId = null, IReadOnlyList<Guid>? documentoIds = null, Guid? tenantId = null)
    {
        var proveedor = new ProveedorFijo();
        await using var context = _e.Contexto(tenantId);
        var servicio = _e.Servicio(context, usuarioId, rol, proveedor, tenantId: tenantId);
        var error = await Record.ExceptionAsync(() =>
            servicio.SummarizeExpedienteAsync(new AISummarizeRequestDto(expedienteId ?? _e.ExpedienteId, documentoIds)));
        return (error, proveedor);
    }

    // ── D-2: regla del AsistenteLegal ────────────────────────────────────

    [Fact]
    public async Task AsistenteLegal_SinTarea_403_SinProveedorNiConversacionNiUso()
    {
        var (error, proveedor) = await ResumirComoAsync(_e.AsistenteId, Roles.AsistenteLegal);

        var prohibido = Assert.IsType<ForbiddenException>(error);
        Assert.Equal(DocumentoErrorCodes.AccessDenied, prohibido.ErrorCode);
        Assert.Empty(proveedor.Peticiones);
        Assert.Equal(0, await _e.ConversacionesAsync(_e.AsistenteId));
        Assert.DoesNotContain(await _e.UsosAsync(_e.AsistenteId), u => u.Exitoso);
    }

    [Theory]
    [InlineData(EstadoTarea.Pendiente, true)]
    [InlineData(EstadoTarea.EnProgreso, true)]
    [InlineData(EstadoTarea.Completada, false)]
    [InlineData(EstadoTarea.Cancelada, false)]
    public async Task AsistenteLegal_SegunEstadoDeSuTarea(EstadoTarea estado, bool permitido)
    {
        await _e.TareaAsync(_e.AsistenteId, estado);

        var (error, proveedor) = await ResumirComoAsync(_e.AsistenteId, Roles.AsistenteLegal);

        if (permitido)
        {
            Assert.Null(error);
            Assert.Single(proveedor.Peticiones);
            Assert.Equal(1, await _e.ConversacionesAsync(_e.AsistenteId));
        }
        else
        {
            Assert.Equal(DocumentoErrorCodes.AccessDenied, Assert.IsType<ForbiddenException>(error).ErrorCode);
            Assert.Empty(proveedor.Peticiones);
            Assert.Equal(0, await _e.ConversacionesAsync(_e.AsistenteId));
        }
    }

    [Fact]
    public async Task AsistenteLegal_TareaDeOtroUsuario_403()
    {
        await _e.TareaAsync(_e.OtroAsistenteId, EstadoTarea.Pendiente);
        var (error, proveedor) = await ResumirComoAsync(_e.AsistenteId, Roles.AsistenteLegal);
        Assert.Equal(DocumentoErrorCodes.AccessDenied, Assert.IsType<ForbiddenException>(error).ErrorCode);
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task AsistenteLegal_TareaEnOtroExpediente_403()
    {
        await _e.TareaAsync(_e.AsistenteId, EstadoTarea.Pendiente, _e.OtroExpedienteId);
        var (error, proveedor) = await ResumirComoAsync(_e.AsistenteId, Roles.AsistenteLegal);
        Assert.Equal(DocumentoErrorCodes.AccessDenied, Assert.IsType<ForbiddenException>(error).ErrorCode);
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task OtroTenant_403()
    {
        var (error, proveedor) = await ResumirComoAsync(_e.SeniorOtroTenantId, Roles.AbogadoSenior, tenantId: _e.OtroTenantId);
        Assert.Equal(DocumentoErrorCodes.AccessDenied, Assert.IsType<ForbiddenException>(error).ErrorCode);
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task SuperAdmin_403()
    {
        var (error, proveedor) = await ResumirComoAsync(Guid.NewGuid(), Roles.SuperAdmin);
        Assert.IsType<ForbiddenException>(error);
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task ExpedienteEliminado_404SinCodigo()
    {
        await _e.EliminarExpedienteAsync(_e.ExpedienteId);
        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior);
        Assert.Null(Assert.IsType<NotFoundException>(error).ErrorCode);
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task JuniorNoResponsable_403_Regresion()
    {
        var (error, proveedor) = await ResumirComoAsync(_e.JuniorId, Roles.AbogadoJunior);
        var prohibido = Assert.IsType<ForbiddenException>(error);
        Assert.Contains("responsable", prohibido.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task Senior_ResumeConElTextoRealDeTodosLosDocumentos()
    {
        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior);

        Assert.Null(error);
        var enviado = string.Join("\n", proveedor.Peticiones.Single().Messages.Select(m => m.Content));
        Assert.Contains("Texto del documento A: hechos del caso.", enviado);
        Assert.Contains("Texto del documento B en PDF", enviado);
        Assert.DoesNotContain("Archivo procesado.", enviado);
    }

    [Fact]
    public async Task Draft_DocumentoSinTareaVigente_403ConCodigo()
    {
        var proveedor = new ProveedorFijo();
        await using var context = _e.Contexto();
        var servicio = _e.Servicio(context, _e.AsistenteId, Roles.AsistenteLegal, proveedor);

        var error = await Assert.ThrowsAsync<ForbiddenException>(() => servicio.DraftEscritoAsync(
            new AIDraftRequestDto(null, "Demanda", "Redacta la demanda", [_documentoA])));

        Assert.Equal(DocumentoErrorCodes.AccessDenied, error.ErrorCode);
        Assert.Empty(proveedor.Peticiones);
    }

    // ── D2: DocumentoIds ─────────────────────────────────────────────────

    [Fact]
    public async Task DocumentoIds_Validos_SoloEsosDocumentosEnElPrompt()
    {
        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior, documentoIds: [_documentoB]);

        Assert.Null(error);
        var enviado = string.Join("\n", proveedor.Peticiones.Single().Messages.Select(m => m.Content));
        Assert.Contains("Texto del documento B en PDF", enviado);
        Assert.DoesNotContain("Texto del documento A", enviado);
    }

    [Fact]
    public async Task DocumentoIds_Duplicados_SeEliminanSinError()
    {
        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior, documentoIds: [_documentoA, _documentoA]);
        Assert.Null(error);
        var enviado = string.Join("\n", proveedor.Peticiones.Single().Messages.Select(m => m.Content));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(enviado, "Texto del documento A"));
    }

    public static TheoryData<string> CasosInvalidos => new() { "inexistente", "eliminado", "otro-expediente", "otro-tenant", "vacio" };

    [Theory]
    [MemberData(nameof(CasosInvalidos))]
    public async Task DocumentoIds_Invalido_400ConMensajeIdentico(string caso)
    {
        var invalido = caso switch
        {
            "inexistente" => Guid.NewGuid(),
            "eliminado" => await DocumentoEliminadoAsync(),
            "otro-expediente" => await _e.DocumentoAsync(Archivos.Txt("otro expediente"), "o.txt", "text/plain", _e.OtroExpedienteId),
            "otro-tenant" => await _e.DocumentoAsync(Archivos.Txt("otro tenant"), "t.txt", "text/plain", _e.ExpedienteOtroTenantId, _e.OtroTenantId),
            _ => Guid.Empty
        };

        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior, documentoIds: [invalido]);

        var validacion = Assert.IsType<ValidationException>(error);
        Assert.Equal(AIDocumentErrorCodes.DocumentsInvalid, validacion.ErrorCode);
        Assert.Equal([$"El documento {invalido} no es válido para este expediente."], validacion.ValidationErrors);
        Assert.Empty(proveedor.Peticiones);
        Assert.Equal(0, await _e.ConversacionesAsync(_e.SeniorId));
    }

    [Fact]
    public async Task DocumentoIds_ValidoMasInvalido_400SoloConElInvalido_SinProveedor()
    {
        var invalido = Guid.NewGuid();
        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior, documentoIds: [_documentoA, invalido]);

        var validacion = Assert.IsType<ValidationException>(error);
        Assert.Equal([$"El documento {invalido} no es válido para este expediente."], validacion.ValidationErrors);
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task DocumentoIds_SinAutorizacionEIdsInvalidos_PrevaleceEl403()
    {
        var (error, _) = await ResumirComoAsync(_e.AsistenteId, Roles.AsistenteLegal, documentoIds: [Guid.NewGuid()]);
        Assert.Equal(DocumentoErrorCodes.AccessDenied, Assert.IsType<ForbiddenException>(error).ErrorCode);
    }

    private async Task<Guid> DocumentoEliminadoAsync()
    {
        var id = await _e.DocumentoAsync(Archivos.Txt("eliminado"), "e.txt", "text/plain");
        await using var context = _e.Contexto();
        var documento = context.Documentos.Single(d => d.Id == id);
        documento.IsDeleted = true;
        documento.DeletedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return id;
    }

    // ── Resumen con varios documentos (DA-9, DA-13 = A) ──────────────────

    [Fact]
    public async Task SM1_TercerDocumentoDanado_FallaSinProveedor_YNingunEstadoCambia()
    {
        var danado = await _e.DocumentoCrudoAsync(
            _e.Almacenamiento.EscribirCrudo(_e.TenantId, _e.ExpedienteId, Archivos.PdfDanado(), ".pdf"), "application/pdf",
            EstadoProcesamientoIa.Procesado, "{\"previo\":true}", DateTime.UtcNow.AddMinutes(-1));
        var antes = await EstadosAsync(_documentoA, _documentoB, danado);

        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior);

        Assert.Equal(AIDocumentErrorCodes.TextInvalid, Assert.IsType<BusinessRuleException>(error).ErrorCode);
        Assert.Empty(proveedor.Peticiones);
        Assert.Equal(0, await _e.ConversacionesAsync(_e.SeniorId));
        Assert.DoesNotContain(await _e.UsosAsync(_e.SeniorId), u => u.Exitoso);
        Assert.Equal(antes, await EstadosAsync(_documentoA, _documentoB, danado));
    }

    [Fact]
    public async Task SM2_LaExtraccionOcurreSinTransaccionAbierta()
    {
        var proveedor = new ProveedorFijo();
        await using var context = _e.Contexto();
        var observado = new ExtractorObservado(new DocumentTextExtractor(_e.Almacenamiento.Servicio), context);
        var servicio = _e.Servicio(context, _e.SeniorId, Roles.AbogadoSenior, proveedor, observado);

        await servicio.SummarizeExpedienteAsync(new AISummarizeRequestDto(_e.ExpedienteId));

        Assert.Equal(2, observado.Llamadas);
        Assert.All(observado.TransaccionAbierta, abierta => Assert.False(abierta));
    }

    [Fact]
    public async Task SM3_ResumenCorrecto_NoModificaEstadoIaNiMetadatos()
    {
        var procesado = await _e.DocumentoAsync(Archivos.Txt("ya procesado"), "p.txt", "text/plain",
            estado: EstadoProcesamientoIa.Procesado, metadatos: "{\"hechos\":1}");
        var antes = await EstadosAsync(_documentoA, _documentoB, procesado);

        var (error, proveedor) = await ResumirComoAsync(_e.SeniorId, Roles.AbogadoSenior);

        Assert.Null(error);
        Assert.Single(proveedor.Peticiones);
        Assert.Equal(antes, await EstadosAsync(_documentoA, _documentoB, procesado));
    }

    private async Task<List<(EstadoProcesamientoIa, string?, uint)>> EstadosAsync(params Guid[] ids)
    {
        var lista = new List<(EstadoProcesamientoIa, string?, uint)>();
        foreach (var id in ids)
        {
            var d = await _e.LeerDocumentoAsync(id);
            lista.Add((d.EstadoIa, d.MetadatosJson, d.Version));
        }

        return lista;
    }

    // ── D1: el chat nunca lee documentos ─────────────────────────────────

    private (Infrastructure.Services.AIService Servicio, ProveedorFijo Proveedor) ServicioSinDocumentos(
        Infrastructure.Persistence.ApplicationDbContext context, Guid usuarioId, string rol)
    {
        var proveedor = new ProveedorFijo();
        var prohibido = new AlmacenamientoProhibido();
        return (_e.Servicio(context, usuarioId, rol, proveedor, new DocumentTextExtractor(prohibido), prohibido), proveedor);
    }

    [Fact]
    public async Task D1_ChatMensajeYStreaming_NoAbrenNingunArchivo()
    {
        await using var context = _e.Contexto();
        var (servicio, proveedor) = ServicioSinDocumentos(context, _e.SeniorId, Roles.AbogadoSenior);

        var conversacion = await servicio.CreateConversationAsync(new CreateAIConversationDto(_e.ExpedienteId, "Caso", AICasoUso.ChatLibre));
        var primera = await servicio.SendMessageAsync(new AIChatRequestDto(ConversationId: conversacion.Id, ExpedienteId: null, Mensaje: "Resume el caso"));
        await servicio.SendMessageAsync(new AIChatRequestDto(ConversationId: primera.ConversationId, ExpedienteId: null, Mensaje: "¿Y los plazos?"));
        await foreach (var _ in servicio.StreamMessageAsync(new AIChatRequestDto(ConversationId: null, ExpedienteId: _e.ExpedienteId, Mensaje: "Continúa")))
        {
        }

        // Ninguna operación abrió archivos (AlmacenamientoProhibido lanzaría) y el proveedor sí se usó.
        Assert.Equal(3, proveedor.Peticiones.Count);
    }

    [Fact]
    public async Task D1_ReanudarConElExpedienteEliminado_404()
    {
        await using var context = _e.Contexto();
        var (servicio, _) = ServicioSinDocumentos(context, _e.SeniorId, Roles.AbogadoSenior);
        var primera = await servicio.SendMessageAsync(new AIChatRequestDto(ConversationId: null, ExpedienteId: _e.ExpedienteId, Mensaje: "Hola"));

        await _e.EliminarExpedienteAsync(_e.ExpedienteId);

        await using var otroContexto = _e.Contexto();
        var (otroServicio, proveedor) = ServicioSinDocumentos(otroContexto, _e.SeniorId, Roles.AbogadoSenior);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            otroServicio.SendMessageAsync(new AIChatRequestDto(ConversationId: primera.ConversationId, ExpedienteId: null, Mensaje: "Sigo")));
        Assert.Empty(proveedor.Peticiones);
    }

    [Fact]
    public async Task D1_GetConversacionPropia_TrasPerderLaTarea_200ConHistorial()
    {
        await _e.TareaAsync(_e.AsistenteId, EstadoTarea.Pendiente);
        await using var context = _e.Contexto();
        var (servicio, _) = ServicioSinDocumentos(context, _e.AsistenteId, Roles.AsistenteLegal);
        var primera = await servicio.SendMessageAsync(new AIChatRequestDto(ConversationId: null, ExpedienteId: _e.ExpedienteId, Mensaje: "Consulta del caso"));

        await using (var tareas = _e.Contexto())
        {
            foreach (var tarea in tareas.Tareas.Where(t => t.AsignadoAUsuarioId == _e.AsistenteId))
            {
                tarea.Estado = EstadoTarea.Completada;
            }

            await tareas.SaveChangesAsync();
        }

        await using var otroContexto = _e.Contexto();
        var (otroServicio, _) = ServicioSinDocumentos(otroContexto, _e.AsistenteId, Roles.AsistenteLegal);
        var detalle = await otroServicio.GetConversationAsync(primera.ConversationId);

        Assert.Equal(2, detalle.Mensajes.Count);
    }
}
