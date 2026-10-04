using System.Text.Json;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Features.Indexacion;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>Base de las pruebas de la 8.4: entorno compartido reiniciado y utilidades comunes.</summary>
[Collection(Coleccion84.Nombre)]
public abstract class Pruebas84
{
    protected Entorno84 E { get; }

    protected Pruebas84(Entorno84 entorno)
    {
        E = entorno;
        E.Reiniciar();
    }

    protected async Task<Escenario84> EscenarioAsync(bool habilitado = true) => await new Escenario84(E).CrearAsync(habilitado);

    protected static JsonElement Json(HistorialAuditoria auditoria) => JsonDocument.Parse(auditoria.ValoresNuevosJson!).RootElement;

    protected static string? Texto(JsonElement json, string propiedad) =>
        json.TryGetProperty(propiedad, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    /// <summary>Vectores que el proveedor simulado determinista genera para esos textos.</summary>
    protected static async Task<List<float[]>> VectoresEsperadosAsync(IReadOnlyList<string> textos)
    {
        var mock = new MockEmbeddingProvider();
        var vectores = new List<float[]>();
        foreach (var lote in textos.Chunk(64))
        {
            vectores.AddRange((await mock.EmbedAsync(lote, EmbeddingPurpose.Documento, CancellationToken.None)).Vectores);
        }

        return vectores;
    }

    /// <summary>Siembra, adquiere y procesa el único trabajo pendiente del tenant.</summary>
    protected static async Task<(TrabajoIndexacion Trabajo, ResultadoIndexacion Resultado)> IndexarAsync(
        Escenario84 s, IIndexacionSemanticaService servicio, CancellationToken parada = default)
    {
        var trabajo = await s.AdquirirUnoAsync(servicio);
        return (trabajo, await servicio.ProcesarAsync(trabajo, parada));
    }

    protected async Task<long> XminDocumentoAsync(Guid documentoId)
    {
        await using var db = E.Contexto();
        return await db.Database.SqlQueryRaw<long>("SELECT xmin::text::bigint AS \"Value\" FROM documentos WHERE \"Id\" = {0}", documentoId).SingleAsync();
    }
}

/// <summary>
/// Fase 8.4 — Sembrado, adquisición, flujo completo, lotes y consumo (FASE_8_4_CONTRATO.md §9, §11, §15–§19, §22).
/// </summary>
public class Fase84FlujoTests(Entorno84 entorno) : Pruebas84(entorno)
{
    // ── Sembrado ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Sembrado_SoloDocumentosElegibles_YEsIdempotente()
    {
        var s = await EscenarioAsync();
        var elegible = await s.DocumentoTxtAsync("Texto elegible para el indice.");
        var noSoportado = await s.DocumentoCrudoAsync("x/archivo.zip", "application/zip");
        var borrado = await s.DocumentoTxtAsync("Documento que se borra.");
        await s.BorrarDocumentoAsync(borrado);
        var deExpedienteBorrado = await s.DocumentoTxtAsync("Documento de un expediente borrado.");
        await s.SqlAsync("UPDATE documentos SET \"ExpedienteId\" = {0} WHERE \"Id\" = {1}", s.OtroExpedienteId, deExpedienteBorrado);
        await s.SqlAsync("UPDATE expedientes SET \"IsDeleted\" = true WHERE \"Id\" = {0}", s.OtroExpedienteId);

        var servicio = E.Servicio();
        Assert.Equal(1, await servicio.SembrarAsync(s.Tenants));
        Assert.Equal(0, await servicio.SembrarAsync(s.Tenants));

        var indice = await s.IndiceAsync(elegible);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Equal(servicio.PerfilActivo, indice.Perfil);
        Assert.Equal("mock:mock-bow-sha256-v1@1536|chunk-v1|ext-v1|norm-v1", indice.Perfil);
        Assert.Equal(1536, indice.Dimensiones);
        Assert.Equal(s.TenantId, indice.TenantId);
        Assert.Equal(s.ExpedienteId, indice.ExpedienteId);
        Assert.Equal(0, indice.Intentos);
        Assert.NotNull(indice.HashContenido);
        Assert.Empty(await s.IndicesAsync(noSoportado));
        Assert.Empty(await s.IndicesAsync(borrado));
        Assert.Empty(await s.IndicesAsync(deExpedienteBorrado));
    }

    [Fact]
    public async Task Sembrado_TenantNoHabilitado_NoSeSiembra_YTenantsHabilitadosLoExcluye()
    {
        var habilitado = await EscenarioAsync();
        var deshabilitado = await EscenarioAsync(habilitado: false);
        var doc = await deshabilitado.DocumentoTxtAsync("Documento de un tenant sin la indexacion habilitada.");

        var servicio = E.Servicio();
        var tenants = await servicio.TenantsHabilitadosAsync();
        Assert.Contains(habilitado.TenantId, tenants);
        Assert.DoesNotContain(deshabilitado.TenantId, tenants);

        await servicio.SembrarAsync(tenants);
        Assert.Empty(await deshabilitado.IndicesAsync(doc));
        Assert.Equal(0, await servicio.SembrarAsync([]));
    }

    [Fact]
    public async Task Sembrado_Concurrente_NoDuplica_YRespetaElLote()
    {
        var s = await EscenarioAsync();
        var documentos = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            documentos.Add(await s.DocumentoTxtAsync($"Documento numero {i} para el sembrado concurrente."));
        }

        var a = E.Servicio(o => o.LoteSembrado = 5);
        Assert.Equal(5, await a.SembrarAsync(s.Tenants));   // el lote limita cada ciclo

        var b = E.Servicio();
        for (var ronda = 0; ronda < 5; ronda++)
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i => (i % 2 == 0 ? a : b).SembrarAsync(s.Tenants)));
        }

        foreach (var documento in documentos)
        {
            Assert.Single(await s.IndicesAsync(documento));
        }
    }

    // ── Adquisición ──────────────────────────────────────────────────────

    [Fact]
    public async Task Adquisicion_Concurrente_CadaTrabajoTieneUnSoloPropietario()
    {
        var s = await EscenarioAsync();
        for (var i = 0; i < 40; i++)
        {
            await s.DocumentoTxtAsync($"Documento {i} de la adquisicion concurrente.");
        }

        var workers = Enumerable.Range(0, 4).Select(_ => E.Servicio()).ToArray();
        await workers[0].SembrarAsync(s.Tenants);

        var adquiridos = new List<(string Worker, TrabajoIndexacion Trabajo)>();
        for (var ronda = 0; ronda < 30 && adquiridos.Count < 40; ronda++)
        {
            var lotes = await Task.WhenAll(workers.Select(async w => (w.Instancia, Trabajos: await w.AdquirirAsync(s.Tenants, 7))));
            adquiridos.AddRange(lotes.SelectMany(l => l.Trabajos.Select(t => (l.Instancia, t))));
        }

        Assert.Equal(40, adquiridos.Count);
        Assert.Equal(40, adquiridos.Select(a => a.Trabajo.IndiceId).Distinct().Count());   // ningún trabajo repetido

        await using var db = E.Contexto();
        var filas = await db.DocumentoIndices.IgnoreQueryFilters().AsNoTracking().Where(i => i.TenantId == s.TenantId).ToListAsync();
        Assert.All(filas, f =>
        {
            Assert.Equal(EstadoIndexacion.Procesando, f.Estado);
            Assert.NotNull(f.ProcesandoDesde);
            Assert.Equal(adquiridos.Single(a => a.Trabajo.IndiceId == f.Id).Worker, f.ProcesadoPor);
            Assert.Equal(f.Version, adquiridos.Single(a => a.Trabajo.IndiceId == f.Id).Trabajo.Version);   // xmin = prueba de propiedad
        });

        // Un trabajo Procesando no se puede volver a adquirir.
        Assert.Empty(await workers[1].AdquirirAsync(s.Tenants, 10));
    }

    [Fact]
    public async Task Adquisicion_RespetaTopePorTenant_Backoff_YOtrosTenants()
    {
        var s = await EscenarioAsync();
        var otro = await EscenarioAsync();
        for (var i = 0; i < 5; i++)
        {
            await s.DocumentoTxtAsync($"Documento {i} del tenant con tope.");
        }

        var ajeno = await otro.DocumentoTxtAsync("Documento de otro tenant.");
        var servicio = E.Servicio(o => o.MaxDocumentosPorTenant = 2);
        await servicio.SembrarAsync([s.TenantId, otro.TenantId]);

        Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 0));
        Assert.Empty(await servicio.AdquirirAsync([], 10));

        var primeros = await servicio.AdquirirAsync(s.Tenants, 10);
        Assert.Equal(2, primeros.Count);                               // tope por tenant en una sola sentencia
        Assert.All(primeros, t => Assert.Equal(s.TenantId, t.TenantId));
        Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 10));     // el tope cuenta los que ya procesa
        Assert.Equal(EstadoIndexacion.Pendiente, (await otro.IndiceAsync(ajeno)).Estado);   // nunca un tenant no pedido

        // Un índice con ProximoIntentoEn en el futuro no es elegible.
        await s.SqlAsync("UPDATE documento_indices SET \"ProximoIntentoEn\" = now() + interval '1 hour' WHERE \"TenantId\" = {0} AND \"Estado\" = 0", s.TenantId);
        Assert.Empty(await E.Servicio().AdquirirAsync(s.Tenants, 10));
        await s.SqlAsync("UPDATE documento_indices SET \"ProximoIntentoEn\" = now() - interval '1 second' WHERE \"TenantId\" = {0} AND \"Estado\" = 0", s.TenantId);
        Assert.Equal(3, (await E.Servicio().AdquirirAsync(s.Tenants, 10)).Count);
    }

    // ── Flujo completo ───────────────────────────────────────────────────

    [Fact]
    public async Task FlujoCompleto_IndexaFragmentosVectoresAuditoriaYConsumo_SinTocarElDocumento()
    {
        var s = await EscenarioAsync();
        var texto = Escenario84.TextoDeFragmentos(3, "CONTENIDO-CONFIDENCIAL-84");
        var documento = await s.DocumentoTxtAsync(texto);
        var xminAntes = await XminDocumentoAsync(documento);
        E.Proveedor.Tokens = _ => 123;

        var servicio = E.Servicio();
        var (trabajo, resultado) = await IndexarAsync(s, servicio);
        Assert.Equal(ResultadoIndexacion.Indexado, resultado);

        var indice = await s.IndiceAsync(documento);
        var fragmentos = await s.FragmentosAsync(indice.Id);
        Assert.Equal(EstadoIndexacion.Indexado, indice.Estado);
        Assert.Equal(3, indice.Fragmentos);
        Assert.Equal(3, fragmentos.Count);
        Assert.NotNull(indice.IndexadoEn);
        Assert.Null(indice.ProcesandoDesde);
        Assert.Null(indice.ProcesadoPor);
        Assert.Null(indice.ProximoIntentoEn);
        Assert.Null(indice.CodigoError);
        Assert.Null(indice.FragmentosCalculados);
        Assert.Equal(0, indice.Intentos);

        // TokensTotales es la estimación local (suma de TokensEstimados), no el consumo informado (123).
        Assert.Equal(fragmentos.Sum(f => f.TokensEstimados), indice.TokensTotales);
        Assert.NotEqual(123, indice.TokensTotales);

        // Fragmentos: los de la 8.2, en orden, con los identificadores del índice y el vector tal cual lo dio el proveedor.
        var esperados = Fragmentador.Fragmentar([Fase82Fragmentos.Txt(texto)], 2000).Fragmentos;
        var vectores = await VectoresEsperadosAsync(esperados.Select(f => f.Texto).ToList());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(esperados[i].Orden, fragmentos[i].Orden);
            Assert.Equal(esperados[i].Texto, fragmentos[i].Texto);
            Assert.Equal(esperados[i].HashFragmento, fragmentos[i].HashFragmento);
            Assert.Equal(esperados[i].CaracterInicio, fragmentos[i].CaracterInicio);
            Assert.Equal(esperados[i].CaracterFin, fragmentos[i].CaracterFin);
            Assert.Equal(s.TenantId, fragmentos[i].TenantId);
            Assert.Equal(documento, fragmentos[i].DocumentoId);
            Assert.Equal(s.ExpedienteId, fragmentos[i].ExpedienteId);
            Assert.Equal(1536, fragmentos[i].Embedding.Length);
            Assert.Equal(vectores[i], fragmentos[i].Embedding);
        }

        // Consumo real: una fila, del worker, con lo que informó el proveedor.
        var uso = Assert.Single(await s.UsosAsync());
        Assert.Equal(OrigenUsoIA.Worker, uso.Origen);
        Assert.Equal(IndexacionSemanticaService.ActorWorker, uso.ActorSistema);
        Assert.Equal("worker:indexacion-semantica", uso.ActorSistema);
        Assert.Null(uso.UsuarioId);
        Assert.Equal(AICasoUso.IndexacionSemantica, uso.CasoUso);
        Assert.Equal("mock", uso.ProviderId);
        Assert.Equal(MockEmbeddingProvider.Modelo, uso.ModelId);
        Assert.Equal(123, uso.TokensEntrada);
        Assert.Equal(0, uso.TokensSalida);
        Assert.Equal(123, uso.TotalTokens);
        Assert.Null(uso.CostoEstimadoUsd);
        Assert.True(uso.Exitoso);
        Assert.Null(uso.CodigoError);

        // Auditoría con trazabilidad: ejecución, consumo y fila de AIUsageLog.
        var auditoria = Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"));
        var json = Json(auditoria);
        Assert.Null(auditoria.UsuarioId);
        Assert.Equal("worker:indexacion-semantica", Texto(json, "actor"));
        Assert.Equal(indice.Id.ToString(), Texto(json, "indiceId"));
        Assert.True(Guid.TryParse(Texto(json, "ejecucionId"), out _));
        Assert.Equal(uso.Id.ToString(), Texto(json, "aiUsageLogId"));
        Assert.Equal("3", Texto(json, "fragmentos"));
        Assert.Equal(123, json.GetProperty("tokensInformados").GetInt64());                      // consumo real informado
        Assert.Equal(indice.TokensTotales, json.GetProperty("tokensTotales").GetInt64());        // estimación local
        Assert.Equal("1", Texto(json, "lotesCompletados"));
        Assert.Equal("1", Texto(json, "intento"));
        Assert.Null(Texto(json, "indiceReemplazadoId"));
        Assert.DoesNotContain("CONTENIDO-CONFIDENCIAL-84", auditoria.ValoresNuevosJson);
        Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"));

        // La indexación no escribe en documentos (EstadoIa, MetadatosJson, UpdatedAt…): la fila no cambia de versión.
        Assert.Equal(xminAntes, await XminDocumentoAsync(documento));

        // Ni el sembrado ni el marcado vuelven a tocarlo; editar metadatos tampoco reindexa.
        await s.SqlAsync("UPDATE documentos SET \"Titulo\" = 'Otro titulo', \"UpdatedAt\" = now() WHERE \"Id\" = {0}", documento);
        Assert.Equal(0, await servicio.SembrarAsync(s.Tenants));
        await servicio.MarcarAsync(s.Tenants);
        Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 10));
        Assert.Equal(EstadoIndexacion.Indexado, (await s.IndiceAsync(documento)).Estado);

        // Logs: identificadores y conteos, nunca contenido, título ni ruta.
        var logs = string.Join("\n", E.Logs.Mensajes);
        Assert.Contains("[INDEX_CLAIMED]", logs);
        Assert.Contains("[INDEX_ACTIVATED]", logs);
        Assert.Contains(trabajo.IndiceId.ToString(), logs);
        Assert.DoesNotContain("CONTENIDO-CONFIDENCIAL-84", logs);
        Assert.DoesNotContain("TITULO-SECRETO-84", logs);
    }

    [Fact]
    public async Task Formatos_TxtPdfDocxXlsx_SeIndexan_YLosVaciosFallanSinLlamarAlProveedor()
    {
        var s = await EscenarioAsync();
        var servicio = E.Servicio();
        foreach (var (nombre, contenido, mime, extension) in Fase82Archivos.Referencia())
        {
            E.Proveedor.Reiniciar();
            var documento = await s.DocumentoAsync(contenido, nombre + extension, mime);
            var (_, resultado) = await IndexarAsync(s, servicio);
            var indice = await s.IndiceAsync(documento);
            var vacio = nombre is "txt-solo-espacios" or "pdf-sin-texto" or "docx-vacio" or "xlsx-sin-valores";
            if (vacio)
            {
                Assert.Equal(ResultadoIndexacion.Fallido, resultado);
                Assert.Equal(EstadoIndexacion.Fallido, indice.Estado);
                Assert.Equal(CodigosIndexacion.TextoVacio, indice.CodigoError);
                Assert.Equal(0, indice.Intentos);
                Assert.Equal(0, E.Proveedor.Llamadas);
                Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
            }
            else
            {
                Assert.True(resultado == ResultadoIndexacion.Indexado, nombre);
                Assert.Equal(EstadoIndexacion.Indexado, indice.Estado);
                Assert.True(indice.Fragmentos > 0);
                Assert.Equal(indice.Fragmentos, await s.ContarFragmentosAsync(indice.Id));
                var fragmentos = await s.FragmentosAsync(indice.Id);
                Assert.All(fragmentos, f => Assert.Contains("\"tipo\"", f.Ubicacion));
            }
        }
    }

    [Fact]
    public async Task DocumentoSinHashRegistrado_SeIndexaConElHashDelArchivo()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento historico sin hash registrado.", conHash: false);
        var (_, resultado) = await IndexarAsync(s, E.Servicio());
        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Indexado, resultado);
        Assert.Matches("^[0-9a-f]{64}$", indice.HashContenido!);
    }

    // ── Lotes ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(64, 64, new[] { 64 })]
    [InlineData(65, 64, new[] { 64, 1 })]
    [InlineData(130, 64, new[] { 64, 64, 2 })]
    [InlineData(130, 200, new[] { 64, 64, 2 })]   // el máximo absoluto es 64 aunque el proveedor declare más
    [InlineData(25, 10, new[] { 10, 10, 5 })]
    public async Task Lotes_SecuencialesConMaximoAbsolutoDe64(int fragmentos, int maximoDelProveedor, int[] lotesEsperados)
    {
        var s = await EscenarioAsync();
        var texto = Escenario84.TextoDeFragmentos(fragmentos);
        var documento = await s.DocumentoTxtAsync(texto);
        E.Proveedor.MaxEntradasPorLote = maximoDelProveedor;
        E.Proveedor.Tokens = n => n * 10;

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        Assert.Equal(ResultadoIndexacion.Indexado, resultado);
        Assert.Equal(lotesEsperados, E.Proveedor.Lotes.Select(l => l.Count).ToArray());
        Assert.Equal(1, E.Proveedor.MaximoEnVuelo);   // lotes secuenciales dentro del documento

        var indice = await s.IndiceAsync(documento);
        var guardados = await s.FragmentosAsync(indice.Id);
        Assert.Equal(fragmentos, indice.Fragmentos);
        Assert.Equal(fragmentos, guardados.Count);
        Assert.Equal(Enumerable.Range(guardados[0].Orden, fragmentos), guardados.Select(f => f.Orden));

        // Cada vector corresponde a SU fragmento, también en las fronteras entre lotes.
        var vectores = await VectoresEsperadosAsync(guardados.Select(f => f.Texto).ToList());
        Assert.All(Enumerable.Range(0, fragmentos), i => Assert.Equal(vectores[i], guardados[i].Embedding));
        Assert.Equal(E.Proveedor.Lotes.SelectMany(l => l), guardados.Select(f => f.Texto));

        // Consumo: suma de lo informado por cada lote, en una sola fila.
        var uso = Assert.Single(await s.UsosAsync());
        Assert.Equal(Enumerable.Range(1, lotesEsperados.Length).Sum(n => n * 10), uso.TokensEntrada);
        Assert.Equal(lotesEsperados.Length.ToString(), Texto(Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"))), "lotesTotales"));
    }

    // ── Consumo (§22) ────────────────────────────────────────────────────

    [Fact]
    public async Task Tokens_NoInformados_NoSeCreaAIUsageLog_NiSeEstima()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        E.Proveedor.Tokens = _ => null;

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        Assert.Equal(ResultadoIndexacion.Indexado, resultado);
        Assert.Empty(await s.UsosAsync());                      // ni fila con 0 ni estimación
        var indice = await s.IndiceAsync(documento);
        Assert.True(indice.TokensTotales > 0);                  // la estimación local sí se guarda en el índice
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED")));
        Assert.Null(Texto(json, "aiUsageLogId"));
        Assert.Equal("1", Texto(json, "lotesCompletados"));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("tokensInformados").ValueKind);        // no informado: null, no 0
        Assert.Equal(indice.TokensTotales, json.GetProperty("tokensTotales").GetInt64());
    }

    [Fact]
    public async Task Tokens_ProveedorSimuladoPorDefecto_RegistraSuPropioConsumoInformado()
    {
        // El simulado es él mismo el proveedor (8.3 §16): su cuenta determinista es consumo informado, con firma mock.
        var s = await EscenarioAsync();
        const string texto = "Documento indexado con el proveedor simulado tal cual.";
        await s.DocumentoTxtAsync(texto);
        var (_, resultado) = await IndexarAsync(s, E.Servicio());
        Assert.Equal(ResultadoIndexacion.Indexado, resultado);
        var uso = Assert.Single(await s.UsosAsync());
        var informado = (await new MockEmbeddingProvider().EmbedAsync(E.Proveedor.Lotes.Single(), EmbeddingPurpose.Documento, CancellationToken.None)).TokensEntrada;
        Assert.Equal(informado, uso.TokensEntrada);
        Assert.Equal("mock", uso.ProviderId);
    }

    [Fact]
    public async Task Tokens_InformadosSoloEnAlgunosLotes_NoSeCreaAIUsageLog()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(70));
        E.Proveedor.Tokens = n => n == 1 ? 500 : null;

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        Assert.Equal(ResultadoIndexacion.Indexado, resultado);
        Assert.Equal(2, E.Proveedor.Llamadas);
        Assert.Empty(await s.UsosAsync());   // información incompleta: no se registra un consumo parcial como total
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED")));
        Assert.Null(Texto(json, "aiUsageLogId"));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("tokensInformados").ValueKind);
    }

    [Fact]
    public async Task Tokens_CeroInformadoPorElProveedor_EsUnDatoReal_YNoSeSustituyePorLaEstimacion()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        E.Proveedor.Tokens = _ => 0;

        await IndexarAsync(s, E.Servicio());

        var uso = Assert.Single(await s.UsosAsync());
        Assert.Equal(0, uso.TokensEntrada);
        Assert.Equal(0, uso.TotalTokens);
        Assert.True((await s.IndiceAsync(documento)).TokensTotales > 0);
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED")));
        Assert.Equal(uso.Id.ToString(), Texto(json, "aiUsageLogId"));
        Assert.Equal(JsonValueKind.Number, json.GetProperty("tokensInformados").ValueKind);      // 0 informado ≠ no informado
        Assert.Equal(0, json.GetProperty("tokensInformados").GetInt64());
    }

    [Fact]
    public async Task Tokens_LaEstimacionLocalNuncaLlegaAAIUsageLog()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(70));
        E.Proveedor.Tokens = _ => 7;

        await IndexarAsync(s, E.Servicio());

        var indice = await s.IndiceAsync(documento);
        var uso = Assert.Single(await s.UsosAsync());
        Assert.Equal(14, uso.TokensEntrada);              // 2 lotes × 7 informados
        Assert.Equal(14, uso.TotalTokens);
        Assert.True(indice.TokensTotales > 1000);         // la estimación es de otro orden de magnitud
        Assert.NotEqual(indice.TokensTotales, uso.TotalTokens);
    }
}
