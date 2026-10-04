using System.Diagnostics;
using System.Globalization;
using System.Text;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Features.Indexacion;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.BackgroundServices;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.4 — Worker alojado, configuración, catálogo cerrado, límites de fase y mediciones
/// (FASE_8_4_CONTRATO.md §9, §12, §13, §23, §26, §28–§30).
/// </summary>
public class Fase84WorkerYArquitecturaTests(Entorno84 entorno) : Pruebas84(entorno)
{
    /// <summary>Deja la base temporal con un único tenant habilitado y sin índices: el ciclo del worker es global.</summary>
    private async Task<Escenario84> EscenarioAisladoAsync()
    {
        var s = await EscenarioAsync();
        await s.SqlAsync("UPDATE tenants SET \"ConfiguracionJson\" = NULL WHERE \"Id\" <> {0}", s.TenantId);
        await s.SqlAsync("DELETE FROM documento_indices");
        return s;
    }

    private IndexacionSemanticaBackgroundService Worker(IIndexacionSemanticaService servicio, Action<IndexacionOptions>? configurar = null)
    {
        var opciones = new IndexacionOptions { IntervalSeconds = 1 };
        configurar?.Invoke(opciones);
        return new IndexacionSemanticaBackgroundService(servicio, Options.Create(opciones), E.Registro.CreateLogger<IndexacionSemanticaBackgroundService>());
    }

    // ── Worker ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_Ciclos_IndexanRespetandoElParalelismo_YPurgan()
    {
        var s = await EscenarioAisladoAsync();
        var documentos = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            documentos.Add(await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2, "C" + i)));
        }

        E.Proveedor.AlLlamar = async (_, _, ct) =>
        {
            await Task.Delay(60, ct);
            return null;
        };
        var servicio = E.Servicio(o => { o.MaxParalelismo = 2; o.MaxDocumentosPorTenant = 2; });
        var worker = Worker(servicio, o => { o.MaxParalelismo = 2; o.MaxDocumentosPorTenant = 2; });

        // Primer ciclo: siembra los cinco y adquiere solo dos (MaxParalelismo); sin huecos libres no adquiere más.
        Assert.Equal(2, await worker.EjecutarCicloAsync(CancellationToken.None));
        Assert.Equal(2, worker.TrabajosEnCurso);
        Assert.Equal(0, await worker.EjecutarCicloAsync(CancellationToken.None));
        await worker.EsperarTrabajosAsync();

        for (var ciclo = 0; ciclo < 10; ciclo++)
        {
            if (await worker.EjecutarCicloAsync(CancellationToken.None) == 0)
            {
                break;
            }

            await worker.EsperarTrabajosAsync();
        }

        foreach (var documento in documentos)
        {
            Assert.Equal(EstadoIndexacion.Indexado, (await s.IndiceAsync(documento)).Estado);
        }

        Assert.InRange(E.Proveedor.MaximoEnVuelo, 1, 2);   // nunca más peticiones simultáneas que MaxParalelismo
        Assert.Equal(0, worker.TrabajosEnCurso);

        // Un documento eliminado se marca y se purga en el mismo ciclo.
        await s.BorrarDocumentoAsync(documentos[0]);
        await worker.EjecutarCicloAsync(CancellationToken.None);
        Assert.Empty(await s.IndicesAsync(documentos[0]));
        Assert.Single(await s.AuditoriasAsync(documentos[0], "DOCUMENT_INDEX_PURGED"));
        Assert.Equal(EstadoIndexacion.Indexado, (await s.IndiceAsync(documentos[1])).Estado);
    }

    [Fact]
    public async Task Worker_ParadaLimpiaDelHost_LiberaLosTrabajosEnCursoSinSumarIntento()
    {
        var s = await EscenarioAisladoAsync();
        var documentos = new[]
        {
            await s.DocumentoTxtAsync("Primer documento en curso durante la parada."),
            await s.DocumentoTxtAsync("Segundo documento en curso durante la parada.")
        };
        E.Proveedor.Tokens = _ => 9;
        E.Proveedor.AlLlamar = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);   // el proveedor no responde hasta la parada
            return null;
        };
        var worker = Worker(E.Servicio());

        await worker.StartAsync(CancellationToken.None);
        var limite = Stopwatch.StartNew();
        while (E.Proveedor.Llamadas < 2 && limite.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(25);
        }

        Assert.Equal(2, E.Proveedor.Llamadas);
        var parada = Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);
        Assert.True(parada.Elapsed < TimeSpan.FromSeconds(10));

        Assert.Equal(0, worker.TrabajosEnCurso);
        foreach (var documento in documentos)
        {
            var indice = await s.IndiceAsync(documento);
            Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
            Assert.Equal(0, indice.Intentos);
            Assert.Null(indice.ProximoIntentoEn);
            Assert.Null(indice.ProcesandoDesde);
            Assert.Null(indice.CodigoError);
            Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        }

        Assert.Empty(await s.UsosAsync());   // ningún lote completado
        Assert.Equal(2, E.Logs.Mensajes.Count(m => m.Contains("[INDEX_RELEASED]")));

        // Otra instancia los toma de inmediato.
        E.Proveedor.AlLlamar = null;
        var otro = E.Servicio();
        var trabajos = await otro.AdquirirAsync(s.Tenants, 5);
        Assert.Equal(2, trabajos.Count);
        Assert.All(trabajos, t => Assert.Equal(0, t.Intentos));
    }

    [Fact]
    public async Task Worker_Deshabilitado_NoHaceNada_YElHostDePruebasLoRegistraDeshabilitado()
    {
        var s = await EscenarioAisladoAsync();
        var documento = await s.DocumentoTxtAsync("Documento que nadie debe sembrar.");
        var worker = Worker(E.Servicio(), o => o.Enabled = false);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);
        Assert.Empty(await s.IndicesAsync(documento));

        // Registro en DI: el worker alojado existe, el servicio es único y los hosts de prueba lo traen deshabilitado.
        var servicios = E.Factory.Services;
        Assert.Single(servicios.GetServices<IHostedService>().OfType<IndexacionSemanticaBackgroundService>());
        Assert.Same(servicios.GetRequiredService<IIndexacionSemanticaService>(), servicios.GetRequiredService<IIndexacionSemanticaService>());
        var opciones = servicios.GetRequiredService<IOptions<IndexacionOptions>>().Value;
        Assert.False(opciones.Enabled);
        Assert.Equal((15, 2, 2, 300, 5, 2000, 5_000_000L, 500, 200),
            (opciones.IntervalSeconds, opciones.MaxParalelismo, opciones.MaxDocumentosPorTenant, opciones.LeaseSeconds, opciones.MaxIntentos,
                opciones.MaxFragmentosPorDocumento, opciones.MaxTokensDiariosPorTenant, opciones.LoteSembrado, opciones.LotePurga));
    }

    // ── Saneamiento de la auditoría (§22.3, §27) ─────────────────────────

    [Fact]
    public async Task Auditoria_SoloLosDosContadoresExactosSeConservan_ElRestoDeClavesConTokenSeRedacta()
    {
        var s = await EscenarioAsync();
        var entidadId = Guid.NewGuid().ToString();
        using (var scope = E.Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.ICurrentTenantService>().SetTenantId(s.TenantId);
            await scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IAuditService>().LogAsync("Documento", entidadId, "PRUEBA_SANEADO_84", null, new
            {
                tokensTotales = 698,
                tokensInformados = (long?)null,
                anidado = new { tokensInformados = 0, tokensTotales = 12L, accessToken = "valor-secreto-anidado" },
                comoCadena = new { tokensTotales = "valor-secreto-en-cadena", tokensInformados = "eyJhbGciOi.jwt.secreto" },
                otraGrafia = new Dictionary<string, object?> { ["TokensTotales"] = 1, ["tokenstotales"] = 2, ["TOKENSINFORMADOS"] = null },
                sinValor = (string?)null,
                tokensTotalesExtra = 3,
                misTokensInformados = 4,
                tokensEntrada = 5,
                token = "valor-secreto-token",
                accessToken = "valor-secreto-access",
                refreshToken = "valor-secreto-refresh",
                jwt = "valor-secreto-jwt",
                apiKey = "valor-secreto-apikey",
                password = "valor-secreto-password",
                clientSecret = "valor-secreto-client"
            });
        }

        await using var db = E.Contexto();
        var fila = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.HistorialAuditorias),
            a => a.EntidadId == entidadId && a.Accion == "PRUEBA_SANEADO_84");
        var json = Json(fila);

        // Las dos claves exactas, con valor numérico o null, se conservan (también 0 y anidadas).
        Assert.Equal(698, json.GetProperty("tokensTotales").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("tokensInformados").ValueKind);
        Assert.Equal(0, json.GetProperty("anidado").GetProperty("tokensInformados").GetInt64());
        Assert.Equal(12, json.GetProperty("anidado").GetProperty("tokensTotales").GetInt64());

        // Todo lo demás sigue redactado: otras claves con "token", otra grafía, y esas mismas claves con una cadena.
        foreach (var clave in new[] { "TokensTotales", "tokenstotales", "TOKENSINFORMADOS" })
        {
            Assert.Equal("[REDACTED]", json.GetProperty("otraGrafia").GetProperty(clave).GetString());
        }

        Assert.False(json.TryGetProperty("sinValor", out _));   // el resto de null se sigue omitiendo, como antes

        foreach (var clave in new[] { "tokensTotalesExtra", "misTokensInformados", "tokensEntrada", "token",
                     "accessToken", "refreshToken", "jwt", "apiKey", "password", "clientSecret" })
        {
            Assert.Equal("[REDACTED]", json.GetProperty(clave).GetString());
        }

        Assert.Equal("[REDACTED]", json.GetProperty("anidado").GetProperty("accessToken").GetString());
        Assert.Equal("[REDACTED]", json.GetProperty("comoCadena").GetProperty("tokensTotales").GetString());
        Assert.Equal("[REDACTED]", json.GetProperty("comoCadena").GetProperty("tokensInformados").GetString());
        Assert.DoesNotContain("valor-secreto", fila.ValoresNuevosJson);
        Assert.DoesNotContain("eyJ", fila.ValoresNuevosJson);
    }

    // ── Configuración ────────────────────────────────────────────────────

    private static ValidateOptionsResult Validar(Action<IndexacionOptions> configurar, double timeoutEmbeddings = 60)
    {
        var opciones = new IndexacionOptions();
        configurar(opciones);
        return new IndexacionOptionsValidator(Options.Create(new EmbeddingOptions { TimeoutSeconds = timeoutEmbeddings })).Validate(null, opciones);
    }

    [Fact]
    public void Opciones_PorDefecto_SonValidas_YLasInvalidasSeRechazan()
    {
        Assert.True(Validar(_ => { }).Succeeded);
        Assert.True(Validar(o => o.LeaseSeconds = 151).Succeeded);

        Assert.True(Validar(o => o.IntervalSeconds = 0).Failed);
        Assert.True(Validar(o => o.MaxParalelismo = 0).Failed);
        Assert.True(Validar(o => o.MaxParalelismo = 17).Failed);
        Assert.True(Validar(o => o.MaxDocumentosPorTenant = 0).Failed);
        Assert.True(Validar(o => o.MaxIntentos = 0).Failed);
        Assert.True(Validar(o => o.MaxFragmentosPorDocumento = 0).Failed);
        Assert.True(Validar(o => o.LoteSembrado = 0).Failed);
        Assert.True(Validar(o => o.LotePurga = 0).Failed);

        // El lease debe superar con margen la extracción (120 s) y el timeout de un lote de embeddings.
        Assert.True(Validar(o => o.LeaseSeconds = 150).Failed);
        Assert.True(Validar(o => o.LeaseSeconds = 300, timeoutEmbeddings: 280).Failed);
        Assert.True(Validar(o => { o.LeaseSeconds = 300; o.Enabled = false; }, timeoutEmbeddings: 280).Succeeded);

        // Un documento del tamaño máximo debe caber en el presupuesto de un día.
        Assert.True(Validar(o => o.MaxTokensDiariosPorTenant = 999_999).Failed);
        Assert.True(Validar(o => o.MaxTokensDiariosPorTenant = 1_000_000).Succeeded);
    }

    [Fact]
    public void Backoff_ExponencialConTopeDeSeisHoras_YJitterDeVeintePorCiento()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), IndexacionSemanticaService.Backoff(0, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(2), IndexacionSemanticaService.Backoff(1, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(4), IndexacionSemanticaService.Backoff(2, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(16), IndexacionSemanticaService.Backoff(4, 0.5));
        Assert.Equal(TimeSpan.FromHours(6), IndexacionSemanticaService.Backoff(9, 0.5));
        Assert.Equal(TimeSpan.FromHours(6), IndexacionSemanticaService.Backoff(60, 1));     // el tope incluye el jitter
        Assert.Equal(TimeSpan.FromSeconds(96), IndexacionSemanticaService.Backoff(1, 0));   // −20 %
        Assert.Equal(144, IndexacionSemanticaService.Backoff(1, 1).TotalSeconds, 3);        // +20 %

        var muestras = Enumerable.Range(0, 500).Select(_ => IndexacionSemanticaService.Backoff(3).TotalSeconds).ToList();
        Assert.All(muestras, m => Assert.InRange(m, 480 * 0.8, 480 * 1.2));
        Assert.True(muestras.Distinct().Count() > 100);
    }

    [Fact]
    public void CatalogoCerrado_DiezCodigos_YPerfilConLasVersionesDeLasFasesAnteriores()
    {
        Assert.Equal(new[]
        {
            "AI_PROVIDER_ERROR", "AI_PROVIDER_TIMEOUT", "DOCUMENT_FILE_NOT_FOUND", "DOCUMENT_INDEX_TOO_LARGE", "DOCUMENT_TEXT_EMPTY",
            "DOCUMENT_TEXT_EXTRACTION_FAILED", "DOCUMENT_TEXT_INVALID", "DOCUMENT_TEXT_UNSUPPORTED", "INDEX_INTERNAL_ERROR", "LEASE_EXPIRED"
        }, CodigosIndexacion.Catalogo.Order(StringComparer.Ordinal));

        // Todo estado de extracción distinto de Success cae en el catálogo.
        Assert.Null(CodigosIndexacion.DeExtraccion(ExtractionStatus.Success));
        Assert.All(Enum.GetValues<ExtractionStatus>().Where(e => e != ExtractionStatus.Success),
            e => Assert.Contains(CodigosIndexacion.DeExtraccion(e)!, CodigosIndexacion.Catalogo));

        var perfil = PerfilIndexacion.Componer(new MockEmbeddingProvider());
        Assert.Equal("mock:mock-bow-sha256-v1@1536|chunk-v1|ext-v1|norm-v1", perfil);
        Assert.True(perfil.Length <= PerfilIndexacion.LongitudMaxima);
    }

    // ── Límites de fase ──────────────────────────────────────────────────

    [Fact]
    public void Arquitectura_SinMigracionesNiEndpointsNiBusqueda_YSinContenidoEnLosLogs()
    {
        var src = Fase82ExtraccionTests.UbicarDirectorio("backend", "src");
        var archivos = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText);

        string[] Con(string simbolo) => archivos.Where(a => a.Value.Contains(simbolo, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Key).Order(StringComparer.Ordinal).ToArray();

        // 0 migraciones: la última sigue siendo la de la 8.1.
        var migraciones = archivos.Keys.Where(k => k.Contains("/Migrations/") && !k.EndsWith(".Designer.cs") && !k.EndsWith("Snapshot.cs"))
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();
        Assert.Equal("20261004044624_Fase81IndiceSemantico", migraciones.Last());
        Assert.DoesNotContain(migraciones, m => m!.Contains("Fase84", StringComparison.OrdinalIgnoreCase));

        // El servicio solo se usa desde el worker y el registro en DI: ningún controlador ni endpoint.
        Assert.Equal(new[]
        {
            "AsistenteJuridico.Application/Features/Indexacion/IIndexacionSemanticaService.cs",
            "AsistenteJuridico.Infrastructure/BackgroundServices/IndexacionSemanticaBackgroundService.cs",
            "AsistenteJuridico.Infrastructure/DependencyInjection.cs",
            "AsistenteJuridico.Infrastructure/Services/IndexacionSemanticaService.cs"
        }, Con("IIndexacionSemanticaService"));
        Assert.DoesNotContain(archivos.Keys, k => k.StartsWith("AsistenteJuridico.API/") && archivos[k].Contains("Indexacion", StringComparison.Ordinal)
            && !archivos[k].Contains("IndexacionSemanticaHabilitada", StringComparison.Ordinal) && !k.EndsWith("Program.cs"));

        // Nada de fases posteriores: búsqueda, RRF, reindexación manual, cobertura, RAG, HNSW.
        foreach (var prohibido in new[] { "ai/buscar", "reindexar", "cobertura", "ReciprocalRank", "hnsw", "<=>" })
        {
            Assert.DoesNotContain(Con(prohibido), k => !k.Contains("/Migrations/"));
        }

        // Los logs y la auditoría del worker nunca llevan mensajes de excepción, texto, rutas ni vectores.
        foreach (var archivo in new[]
                 {
                     "AsistenteJuridico.Infrastructure/Services/IndexacionSemanticaService.cs",
                     "AsistenteJuridico.Infrastructure/BackgroundServices/IndexacionSemanticaBackgroundService.cs"
                 })
        {
            var codigo = archivos[archivo];
            Assert.DoesNotContain(".Message", codigo);
            Assert.DoesNotContain("LogError(ex", codigo);
            Assert.DoesNotContain("LogWarning(ex", codigo);
            Assert.DoesNotContain("ToString()}", codigo);
        }

        // AIUsageLog del worker: sin coste y sin estimaciones.
        var servicio = archivos["AsistenteJuridico.Infrastructure/Services/IndexacionSemanticaService.cs"];
        Assert.Contains("CostoEstimadoUsd = null", servicio);
        Assert.DoesNotContain("TokensEstimados(", servicio);
        Assert.Contains("FOR UPDATE SKIP LOCKED", servicio);
    }

    // ── Mediciones (§29) ─────────────────────────────────────────────────

    [Fact]
    public async Task Rendimiento_MedicionesReales()
    {
        var informe = new StringBuilder();
        void Anotar(string linea) => informe.AppendLine(linea);
        static string Ms(double ms) => ms.ToString("F1", CultureInfo.InvariantCulture) + " ms";

        var s = await EscenarioAisladoAsync();
        var servicio = E.Servicio();
        Anotar($"Fecha UTC: {DateTime.UtcNow:O}; proveedor: simulado en memoria (sin red); PostgreSQL local.");

        // 1 fragmento: mediana de 9 documentos.
        var tiempos = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            await s.DocumentoTxtAsync($"Documento pequeno numero {i}.");
            var trabajo = await s.AdquirirUnoAsync(servicio);
            var reloj = Stopwatch.StartNew();
            Assert.Equal(ResultadoIndexacion.Indexado, await servicio.ProcesarAsync(trabajo));
            tiempos.Add(reloj.Elapsed.TotalMilliseconds);
        }

        tiempos.Sort();
        Anotar($"Documento de 1 fragmento (9 muestras): mediana {Ms(tiempos[4])}; min {Ms(tiempos[0])}; max {Ms(tiempos[8])}.");

        // 2000 fragmentos (el máximo por documento): 32 lotes.
        var grande = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2000));
        var trabajoGrande = await s.AdquirirUnoAsync(servicio);
        GC.Collect();
        var memoriaAntes = GC.GetTotalMemory(true);
        var asignadoAntes = GC.GetTotalAllocatedBytes(true);
        var relojGrande = Stopwatch.StartNew();
        Assert.Equal(ResultadoIndexacion.Indexado, await servicio.ProcesarAsync(trabajoGrande));
        relojGrande.Stop();
        var asignado = GC.GetTotalAllocatedBytes(true) - asignadoAntes;
        var indiceGrande = await s.IndiceAsync(grande);
        Assert.Equal(2000, indiceGrande.Fragmentos);
        Assert.Equal(2000, await s.ContarFragmentosAsync(indiceGrande.Id));
        Assert.Equal(32, E.Proveedor.Lotes.Count(l => l.Count == 64 || l.Count == 16));
        Anotar($"Documento de 2000 fragmentos (32 lotes): total {Ms(relojGrande.Elapsed.TotalMilliseconds)}; "
            + $"{(2000 / relojGrande.Elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture)} fragmentos/s; "
            + $"bytes asignados {(asignado / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)} MiB; "
            + $"memoria gestionada retenida tras el proceso {((GC.GetTotalMemory(true) - memoriaAntes) / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)} MiB; "
            + $"pico de memoria del proceso de pruebas {(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0).ToString("F0", CultureInfo.InvariantCulture)} MiB.");

        // Sembrado y adquisición con 300 documentos pendientes.
        for (var i = 0; i < 300; i++)
        {
            await s.DocumentoCrudoAsync($"{s.TenantId}/{s.ExpedienteId}/{Guid.NewGuid():N}.txt", "text/plain");
        }

        var relojSembrado = Stopwatch.StartNew();
        Assert.Equal(300, await servicio.SembrarAsync(s.Tenants));
        Anotar($"Sembrado de 300 documentos: {Ms(relojSembrado.Elapsed.TotalMilliseconds)}.");
        var relojAdquisicion = Stopwatch.StartNew();
        Assert.Equal(2, (await servicio.AdquirirAsync(s.Tenants, 2)).Count);
        Anotar($"Adquisición de 2 trabajos con 300 pendientes: {Ms(relojAdquisicion.Elapsed.TotalMilliseconds)}.");
        var relojPurga = Stopwatch.StartNew();
        await s.SqlAsync("UPDATE documento_indices SET \"Estado\" = 5 WHERE \"TenantId\" = {0} AND \"Estado\" IN (0, 1)", s.TenantId);
        var purgados = await servicio.PurgarAsync(s.Tenants);
        Assert.Equal(200, purgados);   // LotePurga
        Anotar($"Purga de 200 índices (un ciclo): {Ms(relojPurga.Elapsed.TotalMilliseconds)}.");
        await s.SqlAsync("DELETE FROM documento_indices WHERE \"Estado\" = 5");
        await s.SqlAsync("UPDATE documentos SET \"IsDeleted\" = true WHERE \"TenantId\" = {0}", s.TenantId);

        // Concurrencia 1 frente a 2, con un proveedor que tarda 40 ms por lote (latencia simulada, declarada).
        E.Proveedor.Reiniciar();
        E.Proveedor.AlLlamar = async (_, _, ct) =>
        {
            await Task.Delay(40, ct);
            return null;
        };
        foreach (var paralelismo in new[] { 1, 2 })
        {
            var t = await EscenarioAsync();
            for (var i = 0; i < 12; i++)
            {
                await t.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(20, $"P{paralelismo}D{i}"));
            }

            await servicio.SembrarAsync(t.Tenants);
            var reloj = Stopwatch.StartNew();
            var hechos = 0;
            while (true)
            {
                var trabajos = await servicio.AdquirirAsync(t.Tenants, paralelismo);
                if (trabajos.Count == 0)
                {
                    break;
                }

                var resultados = await Task.WhenAll(trabajos.Select(x => Task.Run(() => servicio.ProcesarAsync(x))));
                Assert.All(resultados, r => Assert.Equal(ResultadoIndexacion.Indexado, r));
                hechos += resultados.Length;
            }

            Assert.Equal(12, hechos);
            Anotar($"Paralelismo {paralelismo}: 12 documentos × 20 fragmentos (proveedor con 40 ms simulados por lote) en {Ms(reloj.Elapsed.TotalMilliseconds)}; "
                + $"{(12 / reloj.Elapsed.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)} documentos/s.");
        }

        Assert.InRange(E.Proveedor.MaximoEnVuelo, 1, 2);
        var ruta = Path.Combine(Path.GetTempPath(), "fase84-rendimiento.txt");
        await File.WriteAllTextAsync(ruta, informe.ToString());
    }
}
