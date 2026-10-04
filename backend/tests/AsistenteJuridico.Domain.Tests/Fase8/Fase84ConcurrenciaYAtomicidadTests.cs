using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Features.Indexacion;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.4 — Lease, latido, recuperación, doble worker, activación atómica, versionado, purga y aislamiento
/// multi-tenant (FASE_8_4_CONTRATO.md §10, §11, §19–§21, §24, §25).
/// </summary>
public class Fase84ConcurrenciaYAtomicidadTests(Entorno84 entorno) : Pruebas84(entorno)
{
    private Infrastructure.Services.IndexacionSemanticaService SinLease() => E.Servicio(o => o.LeaseSeconds = 0);

    private async Task<int> IndexadosAsync(Guid documentoId) =>
        (await new Escenario84(E).IndicesAsync(documentoId)).Count(i => i.Estado == EstadoIndexacion.Indexado);

    // ── Lease, latido y recuperación ─────────────────────────────────────

    [Fact]
    public async Task Caida_ElLeaseVence_YLaRecuperacionSumaUnIntento()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento cuyo worker cae sin avisar.");
        var caido = E.Servicio();
        await s.AdquirirUnoAsync(caido);   // el worker "cae": nunca procesa ni libera

        // Con el lease vigente nadie lo toca.
        await E.Servicio().RecuperarLeasesAsync();
        var indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Procesando, indice.Estado);
        Assert.Equal(0, indice.Intentos);
        Assert.Equal(caido.Instancia, indice.ProcesadoPor);

        // Lease vencido: dos instancias recuperan a la vez y el intento se cuenta una sola vez.
        var recuperados = await Task.WhenAll(SinLease().RecuperarLeasesAsync(), SinLease().RecuperarLeasesAsync(), SinLease().RecuperarLeasesAsync());
        indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Equal(1, indice.Intentos);
        Assert.Equal(CodigosIndexacion.LeaseVencido, indice.CodigoError);
        Assert.Null(indice.ProcesandoDesde);
        Assert.Null(indice.ProcesadoPor);
        Assert.NotNull(indice.ProximoIntentoEn);   // con backoff
        Assert.True(recuperados.Sum() >= 1);

        var auditoria = Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_RECOVERED"));
        var json = Json(auditoria);
        Assert.Null(auditoria.UsuarioId);
        Assert.Equal("worker:indexacion-semantica", Texto(json, "actor"));
        Assert.Equal(MotivosIndexacion.LeaseVencido, Texto(json, "motivo"));
        Assert.Equal("1", Texto(json, "intentos"));
        Assert.Equal(indice.Id.ToString(), Texto(json, "indiceId"));
        Assert.Contains(E.Logs.Mensajes, m => m.Contains("[INDEX_LEASE_EXPIRED]"));

        // Tras el backoff vuelve a ser adquirible y se indexa.
        await s.HacerElegibleAsync(indice.Id);
        var nuevo = E.Servicio();
        var trabajo = Assert.Single(await nuevo.AdquirirAsync(s.Tenants, 1));
        Assert.Equal(1, trabajo.Intentos);
        Assert.Equal(ResultadoIndexacion.Indexado, await nuevo.ProcesarAsync(trabajo));
    }

    [Fact]
    public async Task LeaseVencido_ConLosIntentosAgotados_QuedaFallido()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento que agota sus intentos por leases vencidos.");
        await s.AdquirirUnoAsync(E.Servicio());
        await s.SqlAsync("UPDATE documento_indices SET \"Intentos\" = 4 WHERE \"DocumentoId\" = {0}", documento);

        await SinLease().RecuperarLeasesAsync();

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Fallido, indice.Estado);
        Assert.Equal(5, indice.Intentos);
        Assert.Equal(CodigosIndexacion.LeaseVencido, indice.CodigoError);
        Assert.Null(indice.ProximoIntentoEn);
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED")));
        Assert.Equal(CodigosIndexacion.LeaseVencido, Texto(json, "codigoError"));
        Assert.Equal(MotivosIndexacion.LeaseVencido, Texto(json, "motivo"));
        Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_RECOVERED"));
    }

    [Fact]
    public async Task Latido_RenuevaElLeaseTrasLaExtraccionYTrasCadaLote()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(70));
        var servicio = E.Servicio();
        var trabajo = await s.AdquirirUnoAsync(servicio);
        var adquirido = (await s.IndiceAsync(documento)).ProcesandoDesde!.Value;
        var latidos = new List<DateTime>();
        E.Proveedor.AlLlamar = async (_, _, _) =>
        {
            await Task.Delay(30);
            latidos.Add((await s.IndiceAsync(documento)).ProcesandoDesde!.Value);
            return null;
        };

        Assert.Equal(ResultadoIndexacion.Indexado, await servicio.ProcesarAsync(trabajo));

        Assert.Equal(2, latidos.Count);
        Assert.True(latidos[0] > adquirido);    // latido tras la extracción
        Assert.True(latidos[1] > latidos[0]);   // latido tras el primer lote
    }

    [Fact]
    public async Task WorkerZombi_QueVuelveTrasPerderElLease_AbandonaSinEscribir()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(3));
        var zombi = E.Servicio();
        var trabajoZombi = await s.AdquirirUnoAsync(zombi);

        await SinLease().RecuperarLeasesAsync();
        var indice = await s.IndiceAsync(documento);
        await s.HacerElegibleAsync(indice.Id);
        var nuevo = E.Servicio();
        var trabajoNuevo = Assert.Single(await nuevo.AdquirirAsync(s.Tenants, 1));
        Assert.NotEqual(trabajoZombi.Version, trabajoNuevo.Version);

        // El zombi despierta con su xmin antiguo: su primera escritura no encuentra la fila.
        Assert.Equal(ResultadoIndexacion.Abandonado, await zombi.ProcesarAsync(trabajoZombi));
        indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Procesando, indice.Estado);
        Assert.Equal(nuevo.Instancia, indice.ProcesadoPor);
        Assert.Equal(1, indice.Intentos);
        Assert.Equal(0, E.Proveedor.Llamadas);
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        Assert.Contains(E.Logs.Mensajes, m => m.Contains("[INDEX_ACTIVATION_DISCARDED]"));

        Assert.Equal(ResultadoIndexacion.Indexado, await nuevo.ProcesarAsync(trabajoNuevo));
        Assert.Equal(3, await s.ContarFragmentosAsync(indice.Id));
        Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"));
    }

    [Fact]
    public async Task WorkerZombi_QuePierdeElLeaseAMitadDeLosEmbeddings_NoPisaElIndiceYaActivado()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(3));
        E.Proveedor.Tokens = _ => 40;
        var zombi = E.Servicio();
        var trabajoZombi = await s.AdquirirUnoAsync(zombi);
        var nuevo = E.Servicio();

        E.Proveedor.AlLlamar = async (n, _, _) =>
        {
            if (n == 1)
            {
                // Mientras el zombi espera al proveedor, su lease vence y otra instancia indexa el documento entero.
                await SinLease().RecuperarLeasesAsync();
                await s.HacerElegibleAsync((await s.IndiceAsync(documento)).Id);
                var trabajo = Assert.Single(await nuevo.AdquirirAsync(s.Tenants, 1));
                Assert.Equal(ResultadoIndexacion.Indexado, await nuevo.ProcesarAsync(trabajo));
            }

            return null;
        };

        Assert.Equal(ResultadoIndexacion.Abandonado, await zombi.ProcesarAsync(trabajoZombi));

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Indexado, indice.Estado);
        Assert.Equal(1, indice.Intentos);
        Assert.Equal(3, await s.ContarFragmentosAsync(indice.Id));   // sin duplicados ni fragmentos del zombi
        Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"));
        Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"));

        // Los dos intentos consumieron de verdad: una fila exitosa y otra del intento abandonado.
        var usos = await s.UsosAsync();
        Assert.Equal(2, usos.Count);
        Assert.Single(usos, u => u.Exitoso);
        Assert.Single(usos, u => !u.Exitoso && u.CodigoError == null && u.TokensEntrada == 40);
    }

    [Fact]
    public async Task DobleWorker_ConElMismoTrabajo_SoloUnoConfirma()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(4));
        var a = E.Servicio();
        var b = E.Servicio();
        var trabajo = await s.AdquirirUnoAsync(a);
        E.Proveedor.AlLlamar = async (_, _, _) =>
        {
            await Task.Delay(40);
            return null;
        };

        // Dos ejecuciones con la misma prueba de propiedad (misma idempotencia que un mensaje duplicado).
        var resultados = await Task.WhenAll(a.ProcesarAsync(trabajo), b.ProcesarAsync(trabajo));

        Assert.Single(resultados, r => r == ResultadoIndexacion.Indexado);
        Assert.Single(resultados, r => r == ResultadoIndexacion.Abandonado);
        var indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Indexado, indice.Estado);
        Assert.Equal(4, await s.ContarFragmentosAsync(indice.Id));
        Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"));

        // Reprocesar un trabajo ya confirmado tampoco escribe nada.
        Assert.Equal(ResultadoIndexacion.Abandonado, await a.ProcesarAsync(trabajo));
        Assert.Equal(4, await s.ContarFragmentosAsync(indice.Id));
    }

    [Fact]
    public async Task DosWorkers_EnParalelo_IndexanCadaDocumentoExactamenteUnaVez()
    {
        var s = await EscenarioAsync();
        var documentos = new List<Guid>();
        for (var i = 0; i < 16; i++)
        {
            documentos.Add(await s.DocumentoTxtAsync($"Documento {i} " + Escenario84.TextoDeFragmentos(2, "W" + i)));
        }

        var workers = new[] { E.Servicio(), E.Servicio() };
        await workers[0].SembrarAsync(s.Tenants);

        async Task<int> TrabajarAsync(IIndexacionSemanticaService w)
        {
            var hechos = 0;
            for (var vacios = 0; vacios < 3;)
            {
                var trabajos = await w.AdquirirAsync(s.Tenants, 2);
                if (trabajos.Count == 0)
                {
                    vacios++;
                    await Task.Delay(20);
                    continue;
                }

                foreach (var resultado in await Task.WhenAll(trabajos.Select(t => w.ProcesarAsync(t))))
                {
                    Assert.Equal(ResultadoIndexacion.Indexado, resultado);
                    hechos++;
                }
            }

            return hechos;
        }

        var totales = await Task.WhenAll(workers.Select(TrabajarAsync));

        Assert.Equal(16, totales.Sum());
        foreach (var documento in documentos)
        {
            var indice = await s.IndiceAsync(documento);
            Assert.Equal(EstadoIndexacion.Indexado, indice.Estado);
            Assert.Equal(indice.Fragmentos, await s.ContarFragmentosAsync(indice.Id));
            Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"));
        }

        // El proveedor nunca recibe en una misma llamada textos de dos documentos.
        Assert.All(E.Proveedor.Lotes, lote => Assert.Single(lote.SelectMany(t => t.Split(' ').Where(p => p.Length > 1 && p[0] == 'W' && char.IsDigit(p[1]))).Distinct()));
    }

    // ── Activación atómica y versionado (§19, §20) ───────────────────────

    /// <summary>Indexa el documento con el perfil actual y devuelve su índice vigente (N).</summary>
    private async Task<DocumentoIndice> VersionVigenteAsync(Escenario84 s, Guid documento)
    {
        Assert.Equal(ResultadoIndexacion.Indexado, (await IndexarAsync(s, E.Servicio())).Resultado);
        return await s.IndiceAsync(documento);
    }

    [Fact]
    public async Task CambioDePerfil_LaVersionAnteriorSirveHastaQueLaNuevaSeActiva()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(3));
        var anterior = await VersionVigenteAsync(s, documento);

        E.Proveedor.ModelId = "mock-modelo-v2";
        var servicio = E.Servicio();
        Assert.NotEqual(anterior.Perfil, servicio.PerfilActivo);
        Assert.Equal(1, await servicio.SembrarAsync(s.Tenants));
        await servicio.MarcarAsync(s.Tenants);   // el Indexado de otro perfil NO se retira al marcar
        Assert.Equal(EstadoIndexacion.Indexado, (await s.IndicesAsync(documento)).Single(i => i.Id == anterior.Id).Estado);

        var durante = new List<(EstadoIndexacion Estado, int Fragmentos)>();
        E.Proveedor.AlLlamar = async (_, _, _) =>
        {
            var fila = (await s.IndicesAsync(documento)).Single(i => i.Id == anterior.Id);
            durante.Add((fila.Estado, await s.ContarFragmentosAsync(anterior.Id)));
            return null;
        };
        var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
        Assert.NotEqual(anterior.Id, trabajo.IndiceId);
        Assert.Equal(ResultadoIndexacion.Indexado, await servicio.ProcesarAsync(trabajo));

        Assert.Equal((EstadoIndexacion.Indexado, 3), Assert.Single(durante));   // N sirve mientras N+1 se construye

        var filas = await s.IndicesAsync(documento);
        Assert.Equal(EstadoIndexacion.Obsoleto, filas.Single(i => i.Id == anterior.Id).Estado);
        var nueva = filas.Single(i => i.Id == trabajo.IndiceId);
        Assert.Equal(EstadoIndexacion.Indexado, nueva.Estado);
        Assert.Equal(servicio.PerfilActivo, nueva.Perfil);
        Assert.Equal(1, await IndexadosAsync(documento));
        var json = Json((await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED")).Last());
        Assert.Equal(anterior.Id.ToString(), Texto(json, "indiceReemplazadoId"));
        Assert.Contains(E.Logs.Mensajes, m => m.Contains("[INDEX_SUPERSEDED]"));

        // La versión retirada se purga físicamente, con sus fragmentos.
        await servicio.PurgarAsync(s.Tenants);
        Assert.Equal(nueva.Id, (await s.IndiceAsync(documento)).Id);
        Assert.Equal(0, await s.ContarFragmentosAsync(anterior.Id));
        Assert.Equal(3, await s.ContarFragmentosAsync(nueva.Id));
        var purga = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_PURGED")));
        Assert.Equal(MotivosIndexacion.PerfilObsoleto, Texto(purga, "motivo"));
        Assert.Equal(anterior.Id.ToString(), Texto(purga, "indiceId"));
    }

    [Fact]
    public async Task ReindexacionDelMismoPerfil_ReemplazaLaVersionVigenteEnUnaSolaTransaccion()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        var anterior = await VersionVigenteAsync(s, documento);
        var servicio = E.Servicio();
        var nuevaId = await s.IndicePendienteAsync(documento, servicio.PerfilActivo);   // lo que encolará la 8.5

        var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
        Assert.Equal(nuevaId, trabajo.IndiceId);
        Assert.Equal(ResultadoIndexacion.Indexado, await servicio.ProcesarAsync(trabajo));

        var filas = await s.IndicesAsync(documento);
        Assert.Equal(EstadoIndexacion.Obsoleto, filas.Single(i => i.Id == anterior.Id).Estado);
        Assert.Equal(EstadoIndexacion.Indexado, filas.Single(i => i.Id == nuevaId).Estado);
    }

    public static TheoryData<string> FallosDeLaNueva => ["proveedor-permanente", "proveedor-transitorio", "interno", "persistencia-en-confirmacion", "hash-en-confirmacion", "parada"];

    [Theory]
    [MemberData(nameof(FallosDeLaNueva))]
    public async Task SiLaVersionNuevaFalla_LaAnteriorSigueIndexada(string caso)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(3));
        var anterior = await VersionVigenteAsync(s, documento);
        var servicio = E.Servicio();
        var nuevaId = await s.IndicePendienteAsync(documento, servicio.PerfilActivo);
        using var parada = new CancellationTokenSource();

        switch (caso)
        {
            case "proveedor-permanente":
                E.Proveedor.AlLlamar = (_, _, _) => throw new EmbeddingProviderException(MotivoFalloEmbedding.Autenticacion, 401);
                break;
            case "proveedor-transitorio":
                E.Proveedor.AlLlamar = (_, _, _) => throw new EmbeddingProviderTimeoutException(3);
                break;
            case "interno":
                E.Proveedor.AlLlamar = (_, _, _) => throw new InvalidOperationException();
                break;
            case "persistencia-en-confirmacion":
                // Falla el guardado final, después de que la transacción ya haya retirado N: todo se revierte.
                E.Persistencia.AlGuardar = db => db.ChangeTracker.Entries<DocumentoFragmento>().Any()
                    ? new DbUpdateException("fallo simulado", new InvalidOperationException())
                    : null;
                break;
            case "hash-en-confirmacion":
                E.Proveedor.AlLlamar = async (_, _, _) =>
                {
                    await s.SqlAsync("UPDATE documentos SET \"HashSha256\" = {0} WHERE \"Id\" = {1}", new string('c', 64), documento);
                    return null;
                };
                break;
            default:
                E.Proveedor.AlLlamar = (_, _, ct) =>
                {
                    parada.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<EmbeddingBatchResult?>(null);
                };
                break;
        }

        var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
        var resultado = await servicio.ProcesarAsync(trabajo, parada.Token);
        Assert.NotEqual(ResultadoIndexacion.Indexado, resultado);

        var filas = await s.IndicesAsync(documento);
        var n = filas.Single(i => i.Id == anterior.Id);
        var n1 = filas.Single(i => i.Id == nuevaId);
        Assert.Equal(EstadoIndexacion.Indexado, n.Estado);            // N nunca queda Obsoleto si N+1 no se activó
        Assert.Equal(3, await s.ContarFragmentosAsync(anterior.Id));
        Assert.NotEqual(EstadoIndexacion.Indexado, n1.Estado);
        Assert.Equal(0, await s.ContarFragmentosAsync(nuevaId));
        Assert.Equal(anterior.Version, n.Version);                    // la fila vigente ni siquiera se tocó
        Assert.Equal(caso switch
        {
            "proveedor-permanente" or "interno" or "hash-en-confirmacion" => EstadoIndexacion.Fallido,
            "persistencia-en-confirmacion" => EstadoIndexacion.Procesando,
            _ => EstadoIndexacion.Pendiente
        }, n1.Estado);

        // El marcado y la purga tampoco retiran la versión vigente.
        E.Persistencia.AlGuardar = null;
        await servicio.MarcarAsync(s.Tenants);
        await servicio.PurgarAsync(s.Tenants);
        Assert.Equal(EstadoIndexacion.Indexado, (await s.IndicesAsync(documento)).Single(i => i.Id == anterior.Id).Estado);
    }

    [Fact]
    public async Task LectorConcurrente_SiempreVeExactamenteUnaVersionCompleta()
    {
        var s = await EscenarioAsync();
        var documentos = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(3, "L" + i));
            await VersionVigenteAsync(s, documento);
            documentos.Add(documento);
        }

        var servicio = E.Servicio();
        foreach (var documento in documentos)
        {
            await s.IndicePendienteAsync(documento, servicio.PerfilActivo);
        }

        // Lector: en cada instantánea, cada documento tiene exactamente un Indexado y ese índice tiene todos sus fragmentos.
        using var fin = new CancellationTokenSource();
        var lecturas = 0;
        var incoherencias = new List<string>();
        var lector = Task.Run(async () =>
        {
            await using var db = E.Contexto();
            while (!fin.IsCancellationRequested)
            {
                var filas = await db.Database.SqlQueryRaw<string>(
                    """
                    SELECT d."Id"::text || ':' || count(i."Id") || ':' || COALESCE(bool_and(i."Fragmentos" = (
                        SELECT count(*) FROM documento_fragmentos f WHERE f."IndiceId" = i."Id")), false) AS "Value"
                    FROM documentos d
                    LEFT JOIN documento_indices i ON i."DocumentoId" = d."Id" AND i."Estado" = 2
                    WHERE d."TenantId" = {0}
                    GROUP BY d."Id"
                    """, s.TenantId).ToListAsync();
                lecturas++;
                lock (incoherencias)
                {
                    incoherencias.AddRange(filas.Where(f => !f.EndsWith(":1:true", StringComparison.Ordinal)));
                }
            }
        });

        var trabajos = await servicio.AdquirirAsync(s.Tenants, 10);
        Assert.Equal(6, trabajos.Count);
        var resultados = await Task.WhenAll(trabajos.Select(t => Task.Run(() => servicio.ProcesarAsync(t))));
        await Task.Delay(50);
        fin.Cancel();
        await lector;

        Assert.All(resultados, r => Assert.Equal(ResultadoIndexacion.Indexado, r));
        Assert.True(lecturas > 0);
        Assert.Empty(incoherencias);
        foreach (var documento in documentos)
        {
            Assert.Equal(1, await IndexadosAsync(documento));
        }
    }

    // ── Borrados, desactivación y purga (§21, §24) ───────────────────────

    [Theory]
    [InlineData("pendiente")]
    [InlineData("procesando")]
    [InlineData("durante-embeddings")]
    [InlineData("indexado")]
    [InlineData("expediente")]
    public async Task DocumentoEliminado_PasaAPurgaPendiente_YSePurgaFisicamente(string momento)
    {
        var s = await EscenarioAsync();
        var conservado = await s.DocumentoTxtAsync("Documento que se conserva.");
        await VersionVigenteAsync(s, conservado);
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        var servicio = E.Servicio();
        await servicio.SembrarAsync(s.Tenants);

        Task BorrarAsync() => momento == "expediente"
            ? s.SqlAsync("UPDATE documentos SET \"IsDeleted\" = true WHERE \"Id\" = {0}", documento)
            : s.BorrarDocumentoAsync(documento);

        switch (momento)
        {
            case "pendiente" or "expediente":
                await BorrarAsync();
                await servicio.MarcarAsync(s.Tenants);
                break;
            case "procesando":
            {
                var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
                await BorrarAsync();
                Assert.Equal(ResultadoIndexacion.Purga, await servicio.ProcesarAsync(trabajo));
                Assert.Equal(0, E.Proveedor.Llamadas - 1);   // solo la llamada del documento conservado
                break;
            }
            case "durante-embeddings":
            {
                var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
                E.Proveedor.AlLlamar = async (_, _, _) =>
                {
                    await BorrarAsync();
                    return null;
                };
                Assert.Equal(ResultadoIndexacion.Purga, await servicio.ProcesarAsync(trabajo));   // lo detecta el latido
                break;
            }
            default:
            {
                var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
                Assert.Equal(ResultadoIndexacion.Indexado, await servicio.ProcesarAsync(trabajo));
                await BorrarAsync();
                await servicio.MarcarAsync(s.Tenants);
                break;
            }
        }

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.PurgaPendiente, indice.Estado);
        Assert.Null(indice.ProcesandoDesde);
        Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 5));
        Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"));

        await servicio.PurgarAsync(s.Tenants);

        Assert.Empty(await s.IndicesAsync(documento));
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_PURGED")));
        Assert.Equal(MotivosIndexacion.DocumentoEliminado, Texto(json, "motivo"));
        Assert.Equal(indice.Id.ToString(), Texto(json, "indiceId"));
        Assert.Equal("worker:indexacion-semantica", Texto(json, "actor"));

        // El documento conservado sigue indexado, y el eliminado no se vuelve a sembrar.
        Assert.Equal(EstadoIndexacion.Indexado, (await s.IndiceAsync(conservado)).Estado);
        Assert.Equal(0, await servicio.SembrarAsync(s.Tenants));
    }

    [Fact]
    public async Task ExpedienteEliminado_PurgaLosIndicesDeSusDocumentos()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento de un expediente que se elimina.");
        await VersionVigenteAsync(s, documento);
        var servicio = E.Servicio();

        await s.SqlAsync("UPDATE expedientes SET \"IsDeleted\" = true WHERE \"Id\" = {0}", s.ExpedienteId);
        await servicio.MarcarAsync(s.Tenants);
        Assert.Equal(EstadoIndexacion.PurgaPendiente, (await s.IndiceAsync(documento)).Estado);
        await servicio.PurgarAsync(s.Tenants);
        Assert.Empty(await s.IndicesAsync(documento));
    }

    [Fact]
    public async Task TenantDesactivado_SusIndicesSePurgan_YUnTrabajoEnCursoSeDescarta()
    {
        var s = await EscenarioAsync();
        var otro = await EscenarioAsync();
        var indexado = await s.DocumentoTxtAsync("Documento ya indexado del tenant que se desactiva.");
        await VersionVigenteAsync(s, indexado);
        var enCurso = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        var ajeno = await otro.DocumentoTxtAsync("Documento del tenant que sigue habilitado.");
        await VersionVigenteAsync(otro, ajeno);
        var servicio = E.Servicio();

        // Se desactiva mientras un trabajo está esperando al proveedor: el latido lo descarta.
        var trabajo = await s.AdquirirUnoAsync(servicio);
        E.Proveedor.AlLlamar = async (_, _, _) =>
        {
            await s.DesactivarTenantAsync();
            return null;
        };
        Assert.Equal(ResultadoIndexacion.Purga, await servicio.ProcesarAsync(trabajo));
        Assert.Equal(0, await s.ContarFragmentosAsync(trabajo.IndiceId));

        var habilitados = await servicio.TenantsHabilitadosAsync();
        Assert.DoesNotContain(s.TenantId, habilitados);
        await servicio.MarcarAsync(otro.Tenants);
        Assert.Equal(EstadoIndexacion.PurgaPendiente, (await s.IndiceAsync(indexado)).Estado);
        Assert.Equal(EstadoIndexacion.PurgaPendiente, (await s.IndiceAsync(enCurso)).Estado);

        await servicio.PurgarAsync(otro.Tenants);
        Assert.Empty(await s.IndicesAsync(indexado));
        Assert.Empty(await s.IndicesAsync(enCurso));
        Assert.Equal(MotivosIndexacion.TenantDesactivado, Texto(Json(Assert.Single(await s.AuditoriasAsync(indexado, "DOCUMENT_INDEX_PURGED"))), "motivo"));
        Assert.Equal(EstadoIndexacion.Indexado, (await otro.IndiceAsync(ajeno)).Estado);   // el otro tenant, intacto
        Assert.Equal(0, await servicio.SembrarAsync(otro.Tenants));
    }

    [Fact]
    public async Task Marcado_PendientesYFallidosDePerfilInactivo_PasanAObsoleto_YSeSiembraElPerfilActivo()
    {
        var s = await EscenarioAsync();
        var pendiente = await s.DocumentoTxtAsync("Documento pendiente con el perfil anterior.");
        var fallido = await s.DocumentoTxtAsync("Documento fallido con el perfil anterior.");
        var anterior = E.Servicio();
        await anterior.SembrarAsync(s.Tenants);
        await s.SqlAsync("UPDATE documento_indices SET \"Estado\" = 3, \"CodigoError\" = 'DOCUMENT_TEXT_EMPTY' WHERE \"DocumentoId\" = {0}", fallido);

        E.Proveedor.ModelId = "mock-modelo-v3";
        var actual = E.Servicio();
        await actual.MarcarAsync(s.Tenants);
        Assert.Equal(EstadoIndexacion.Obsoleto, (await s.IndiceAsync(pendiente)).Estado);
        Assert.Equal(EstadoIndexacion.Obsoleto, (await s.IndiceAsync(fallido)).Estado);

        Assert.Equal(2, await actual.SembrarAsync(s.Tenants));
        await actual.PurgarAsync(s.Tenants);
        Assert.Equal(actual.PerfilActivo, (await s.IndiceAsync(pendiente)).Perfil);
        Assert.Equal(EstadoIndexacion.Pendiente, (await s.IndiceAsync(fallido)).Estado);

        // Un worker con el perfil antiguo no adquiere trabajos del nuevo.
        Assert.Empty(await anterior.AdquirirAsync(s.Tenants, 5));
    }

    [Fact]
    public async Task ProveedorCambiadoBajoElWorker_NoGeneraVectoresDeOtroPerfil()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento adquirido con un perfil y procesado con otro proveedor.");
        var servicio = E.Servicio();
        var trabajo = await s.AdquirirUnoAsync(servicio);
        E.Proveedor.ModelId = "mock-modelo-inesperado";

        Assert.Equal(ResultadoIndexacion.Fallido, await servicio.ProcesarAsync(trabajo));
        Assert.Equal(CodigosIndexacion.ErrorInterno, (await s.IndiceAsync(documento)).CodigoError);
        Assert.Equal(0, E.Proveedor.Llamadas);
    }

    // ── Aislamiento multi-tenant (§25) ───────────────────────────────────

    [Fact]
    public async Task MultiTenant_ContenidoIdentico_IndicesYFragmentosSeparados()
    {
        var a = await EscenarioAsync();
        var b = await EscenarioAsync();
        var texto = Escenario84.TextoDeFragmentos(3, "Identico");
        var docA = await a.DocumentoTxtAsync(texto);
        var docB = await b.DocumentoTxtAsync(texto);
        E.Proveedor.Tokens = _ => 11;
        var servicio = E.Servicio();
        await servicio.SembrarAsync([a.TenantId, b.TenantId]);

        var trabajos = await servicio.AdquirirAsync([a.TenantId, b.TenantId], 10);
        Assert.Equal(2, trabajos.Count);
        await Task.WhenAll(trabajos.Select(t => servicio.ProcesarAsync(t)));

        var indiceA = await a.IndiceAsync(docA);
        var indiceB = await b.IndiceAsync(docB);
        Assert.NotEqual(indiceA.Id, indiceB.Id);
        var fragmentosA = await a.FragmentosAsync(indiceA.Id);
        var fragmentosB = await b.FragmentosAsync(indiceB.Id);
        Assert.Equal(3, fragmentosA.Count);
        Assert.Equal(3, fragmentosB.Count);
        Assert.All(fragmentosA, f => Assert.Equal((a.TenantId, docA, a.ExpedienteId), (f.TenantId, f.DocumentoId, f.ExpedienteId)));
        Assert.All(fragmentosB, f => Assert.Equal((b.TenantId, docB, b.ExpedienteId), (f.TenantId, f.DocumentoId, f.ExpedienteId)));
        Assert.Empty(fragmentosA.Select(f => f.Id).Intersect(fragmentosB.Select(f => f.Id)));

        // Consumo y auditoría, cada uno en su tenant.
        Assert.Equal(a.TenantId, Assert.Single(await a.UsosAsync()).TenantId);
        Assert.Equal(b.TenantId, Assert.Single(await b.UsosAsync()).TenantId);
        Assert.Single(await a.AuditoriasAsync(docA, "DOCUMENT_INDEXED"));
        Assert.Empty(await a.AuditoriasAsync(docB, "DOCUMENT_INDEXED"));

        // Con el filtro global de un tenant no se ve nada del otro.
        using var scope = E.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.ICurrentTenantService>().SetTenantId(a.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ApplicationDbContext>();
        Assert.Equal(new[] { indiceA.Id }, await db.DocumentoIndices.Where(i => i.Id == indiceA.Id || i.Id == indiceB.Id).Select(i => i.Id).ToListAsync());
        Assert.Equal(0, await db.DocumentoFragmentos.CountAsync(f => f.IndiceId == indiceB.Id));
        Assert.Equal(3, await db.DocumentoFragmentos.CountAsync(f => f.IndiceId == indiceA.Id));
    }

    [Fact]
    public async Task MultiTenant_LaBaseRechazaUnFragmentoQueCruceTenants()
    {
        var a = await EscenarioAsync();
        var b = await EscenarioAsync();
        var docA = await a.DocumentoTxtAsync("Documento del tenant A.");
        var docB = await b.DocumentoTxtAsync("Documento del tenant B.");
        var indiceA = await VersionVigenteAsync(a, docA);
        var plantilla = (await a.FragmentosAsync(indiceA.Id)).Single();

        DocumentoFragmento Cruzado(Guid tenant, Guid documento, Guid expediente) => new()
        {
            Id = Guid.NewGuid(), TenantId = tenant, DocumentoId = documento, ExpedienteId = expediente, IndiceId = indiceA.Id, Orden = 99,
            Texto = "cruce", Ubicacion = plantilla.Ubicacion, HashFragmento = plantilla.HashFragmento, Embedding = plantilla.Embedding,
            CaracterInicio = 0, CaracterFin = 5, TokensEstimados = 2, CreatedAt = DateTime.UtcNow
        };

        foreach (var cruzado in new[]
                 {
                     Cruzado(b.TenantId, docA, a.ExpedienteId),   // índice de A con tenant B
                     Cruzado(a.TenantId, docB, a.ExpedienteId),   // documento de B en un índice de A
                     Cruzado(a.TenantId, docA, b.ExpedienteId)    // expediente de B
                 })
        {
            await using var db = E.Contexto();
            db.DocumentoFragmentos.Add(cruzado);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        Assert.Equal(1, await a.ContarFragmentosAsync(indiceA.Id));
    }
}
