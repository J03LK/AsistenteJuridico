using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Indexacion;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Features.Indexacion;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.BackgroundServices;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.4 — Errores, reintentos, límites, hash, persistencia y parada del host
/// (FASE_8_4_CONTRATO.md §12–§14, §22, §23, §26).
/// </summary>
public class Fase84ErroresTests(Entorno84 entorno) : Pruebas84(entorno)
{
    /// <summary>Fallo tipado ajeno a las clases de la 8.3: lo único que cuenta es IFalloProveedorEmbeddings.EsTransitorio.</summary>
    private sealed class FalloPersonalizado(MotivoFalloEmbedding motivo, bool transitorio) : Exception("mensaje-secreto-del-fallo"), IFalloProveedorEmbeddings
    {
        public MotivoFalloEmbedding Motivo => motivo;
        public bool EsTransitorio => transitorio;
        public int? CodigoEstadoHttp => null;
        public int Intentos => 1;
        public TimeSpan? EsperaSugerida => null;
    }

    private static Exception Fallo(MotivoFalloEmbedding motivo, TimeSpan? espera = null) => motivo == MotivoFalloEmbedding.Timeout
        ? new EmbeddingProviderTimeoutException(3, null, espera)
        : new EmbeddingProviderException(motivo, 500, 3, espera);

    private void ProveedorFalla(Func<int, Exception?> fallo) =>
        E.Proveedor.AlLlamar = (n, _, _) => fallo(n) is { } ex ? throw ex : Task.FromResult<EmbeddingBatchResult?>(null);

    private async Task<DateTime> AhoraAsync()
    {
        await using var db = E.Contexto();
        return DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT now() AS \"Value\"").SingleAsync(), DateTimeKind.Utc);
    }

    // ── Errores del proveedor ────────────────────────────────────────────

    public static TheoryData<MotivoFalloEmbedding> Motivos => new(Enum.GetValues<MotivoFalloEmbedding>());

    [Theory]
    [MemberData(nameof(Motivos))]
    public async Task FalloTipadoDelProveedor_SeClasificaPorEsTransitorio(MotivoFalloEmbedding motivo)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento cuyo lote de embeddings falla.");
        ProveedorFalla(_ => Fallo(motivo));
        var servicio = E.Servicio();
        var antes = await AhoraAsync();

        var (_, resultado) = await IndexarAsync(s, servicio);

        var indice = await s.IndiceAsync(documento);
        var codigo = motivo == MotivoFalloEmbedding.Timeout ? CodigosIndexacion.ProveedorTimeout : CodigosIndexacion.ProveedorError;
        Assert.Equal(codigo, indice.CodigoError);
        Assert.Equal(1, E.Proveedor.Llamadas);            // el worker no reintenta la llamada: eso ya lo hizo el proveedor
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        Assert.Null(indice.ProcesandoDesde);
        Assert.Empty(await s.UsosAsync());                // ningún lote completado: no hay consumo que registrar

        if (ClasificacionFalloEmbedding.EsTransitorio(motivo))
        {
            Assert.Equal(ResultadoIndexacion.Reintento, resultado);
            Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
            Assert.Equal(1, indice.Intentos);
            // Backoff 2^1 × 1 min ± 20 % con el reloj de la base.
            Assert.InRange(indice.ProximoIntentoEn!.Value, antes.AddSeconds(95), (await AhoraAsync()).AddSeconds(145));
            Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"));

            // Enfriamiento de la instancia: 60 s sin adquirir; otra instancia no se ve afectada.
            Assert.True(servicio.EnEnfriamiento);
            await s.HacerElegibleAsync(indice.Id);
            Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 5));
            E.Reloj.Desfase = TimeSpan.FromSeconds(59);
            Assert.True(servicio.EnEnfriamiento);
            E.Reloj.Desfase = TimeSpan.FromSeconds(61);
            Assert.False(servicio.EnEnfriamiento);
            Assert.Single(await servicio.AdquirirAsync(s.Tenants, 5));
        }
        else
        {
            Assert.Equal(ResultadoIndexacion.Fallido, resultado);
            Assert.Equal(EstadoIndexacion.Fallido, indice.Estado);
            Assert.Equal(0, indice.Intentos);
            Assert.Null(indice.ProximoIntentoEn);
            Assert.False(servicio.EnEnfriamiento);
            var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED")));
            Assert.Equal(codigo, Texto(json, "codigoError"));
            Assert.Equal(motivo.ToString(), Texto(json, "motivo"));
            Assert.Equal("worker:indexacion-semantica", Texto(json, "actor"));
            Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("tokensInformados").ValueKind);

            // Sin reintento: ni se vuelve a sembrar ni es adquirible.
            Assert.Equal(0, await servicio.SembrarAsync(s.Tenants));
            await servicio.MarcarAsync(s.Tenants);
            Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 5));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FalloTipadoPersonalizado_MandaEsTransitorio_NoElMotivo(bool transitorio)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento con un fallo tipado propio.");
        // Motivo "permanente" declarado transitorio, y motivo "transitorio" declarado permanente.
        ProveedorFalla(_ => new FalloPersonalizado(transitorio ? MotivoFalloEmbedding.Autenticacion : MotivoFalloEmbedding.NoDisponible, transitorio));

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        Assert.Equal(transitorio ? ResultadoIndexacion.Reintento : ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(transitorio ? EstadoIndexacion.Pendiente : EstadoIndexacion.Fallido, (await s.IndiceAsync(documento)).Estado);
        Assert.DoesNotContain("mensaje-secreto-del-fallo", string.Join("\n", E.Logs.Mensajes));
    }

    [Fact]
    public async Task FalloTransitorio_AlQuintoIntento_QuedaFallidoPorReintentosAgotados()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento cuyo proveedor nunca responde.");
        ProveedorFalla(_ => Fallo(MotivoFalloEmbedding.NoDisponible));

        await E.Servicio().SembrarAsync(s.Tenants);
        var resultados = new List<ResultadoIndexacion>();
        for (var intento = 1; intento <= 5; intento++)
        {
            var servicio = E.Servicio();   // instancia nueva: sin enfriamiento
            var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
            Assert.Equal(intento - 1, trabajo.Intentos);
            resultados.Add(await servicio.ProcesarAsync(trabajo));
            var fila = await s.IndiceAsync(documento);
            Assert.Equal(intento, fila.Intentos);
            if (fila.Estado == EstadoIndexacion.Pendiente)
            {
                await s.HacerElegibleAsync(fila.Id);
            }
        }

        Assert.Equal(Enumerable.Repeat(ResultadoIndexacion.Reintento, 4).Append(ResultadoIndexacion.Fallido), resultados);
        var indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Fallido, indice.Estado);
        Assert.Equal(5, indice.Intentos);
        Assert.Equal(CodigosIndexacion.ProveedorError, indice.CodigoError);
        Assert.Null(indice.ProximoIntentoEn);
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED")));
        Assert.Equal(MotivosIndexacion.ReintentosAgotados, Texto(json, "motivo"));
        Assert.Equal("5", Texto(json, "intentos"));
        Assert.Empty(await E.Servicio().AdquirirAsync(s.Tenants, 5));
        Assert.Equal(5, E.Proveedor.Llamadas);
    }

    [Theory]
    [InlineData(30, 30)]       // la espera sugerida por el proveedor (429) supera al backoff: se respeta
    [InlineData(600, 360)]     // … con el tope de 6 h
    [InlineData(0, 2)]         // inferior al backoff: manda el backoff (≈ 2 min)
    public async Task LimiteDeTasa_RespetaLaEsperaSugerida(int minutosSugeridos, int minutosEsperados)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento con limite de tasa.");
        ProveedorFalla(_ => new EmbeddingProviderException(MotivoFalloEmbedding.LimiteDeTasa, 429, 3, TimeSpan.FromMinutes(minutosSugeridos)));
        var antes = await AhoraAsync();

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        Assert.Equal(ResultadoIndexacion.Reintento, resultado);
        var proximo = (await s.IndiceAsync(documento)).ProximoIntentoEn!.Value;
        var holgura = minutosSugeridos == 0 ? TimeSpan.FromSeconds(25) : TimeSpan.Zero;   // ±20 % del backoff
        Assert.InRange(proximo, antes.AddMinutes(minutosEsperados) - holgura, (await AhoraAsync()).AddMinutes(minutosEsperados) + holgura);
    }

    [Theory]
    [InlineData(false)]   // fallo permanente en el segundo lote
    [InlineData(true)]    // fallo transitorio en el segundo lote
    public async Task FalloEnElSegundoLote_NoDejaFragmentos_YRegistraElConsumoRealDelIntento(bool transitorio)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(70));
        E.Proveedor.Tokens = _ => 640;
        ProveedorFalla(n => n == 2 ? Fallo(transitorio ? MotivoFalloEmbedding.ErrorDelServidor : MotivoFalloEmbedding.SolicitudRechazada) : null);

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(transitorio ? ResultadoIndexacion.Reintento : ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));   // los vectores del primer lote no se persisten
        Assert.Equal(0, indice.Fragmentos);

        // Consumo real del intento fallido, en un contexto independiente: solo lo informado por el lote completado.
        var uso = Assert.Single(await s.UsosAsync());
        Assert.False(uso.Exitoso);
        Assert.Equal(CodigosIndexacion.ProveedorError, uso.CodigoError);
        Assert.Equal(640, uso.TokensEntrada);
        Assert.Equal(640, uso.TotalTokens);
        Assert.Equal(0, uso.TokensSalida);
        Assert.Null(uso.CostoEstimadoUsd);
        Assert.Equal(OrigenUsoIA.Worker, uso.Origen);

        if (!transitorio)
        {
            var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED")));
            Assert.Equal(uso.Id.ToString(), Texto(json, "aiUsageLogId"));
            Assert.Equal(640, json.GetProperty("tokensInformados").GetInt64());
            Assert.Equal("1", Texto(json, "lotesCompletados"));
            Assert.Equal("2", Texto(json, "lotesTotales"));
        }
        else
        {
            Assert.Contains(E.Logs.Mensajes, m => m.Contains("[INDEX_RETRY]") && m.Contains(uso.Id.ToString()));
        }
    }

    public static TheoryData<string> NoTipadas => ["InvalidOperation", "HttpRequest", "Timeout", "OperationCanceled", "NullReference"];

    [Theory]
    [MemberData(nameof(NoTipadas))]
    public async Task ExcepcionNoTipadaDeEmbedAsync_EsErrorInternoNoReintentable(string tipo)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento cuyo proveedor lanza una excepcion no tipada.");
        ProveedorFalla(_ => tipo switch
        {
            "HttpRequest" => new HttpRequestException("mensaje-secreto-interno"),
            "Timeout" => new TimeoutException("mensaje-secreto-interno"),
            "OperationCanceled" => new OperationCanceledException("mensaje-secreto-interno"),   // sin parada del host
            "NullReference" => new NullReferenceException("mensaje-secreto-interno"),
            _ => new InvalidOperationException("mensaje-secreto-interno")
        });
        var servicio = E.Servicio();

        var (_, resultado) = await IndexarAsync(s, servicio);

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(EstadoIndexacion.Fallido, indice.Estado);
        Assert.Equal(CodigosIndexacion.ErrorInterno, indice.CodigoError);
        Assert.Equal(0, indice.Intentos);                 // no cuenta como intento reintentable
        Assert.Null(indice.ProximoIntentoEn);
        Assert.False(servicio.EnEnfriamiento);            // no se trata como fallo transitorio del proveedor
        Assert.Equal(1, E.Proveedor.Llamadas);
        Assert.Equal(MotivosIndexacion.Interno, Texto(Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"))), "motivo"));
        Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 5));

        var logs = string.Join("\n", E.Logs.Mensajes);
        Assert.Contains("[INDEX_INTERNAL_ERROR]", logs);
        Assert.DoesNotContain("mensaje-secreto-interno", logs);
    }

    [Theory]
    [InlineData("menos-vectores")]
    [InlineData("dimension-1535")]
    [InlineData("dimension-declarada")]
    [InlineData("otro-modelo")]
    [InlineData("otro-proveedor")]
    public async Task LoteIncoherente_NoSeAcepta_NiSeRellenaNiSeTrunca(string caso)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(3));
        E.Proveedor.AlLlamar = async (_, textos, ct) =>
        {
            var r = await new MockEmbeddingProvider().EmbedAsync(textos, EmbeddingPurpose.Documento, ct);
            return caso switch
            {
                "menos-vectores" => r with { Vectores = r.Vectores.Take(2).ToList() },
                "dimension-1535" => r with { Vectores = r.Vectores.Select((v, i) => i == 1 ? v[..1535] : v).ToList() },
                "dimension-declarada" => r with { Dimensiones = 3072 },
                "otro-modelo" => r with { ModelId = "otro-modelo" },
                _ => r with { ProviderId = "otro" }
            };
        };

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(CodigosIndexacion.ErrorInterno, indice.CodigoError);
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
    }

    // ── Extracción y límites ─────────────────────────────────────────────

    [Theory]
    [InlineData(ExtractionStatus.UnsupportedFormat, CodigosIndexacion.TextoNoSoportado, null)]
    [InlineData(ExtractionStatus.Empty, CodigosIndexacion.TextoVacio, null)]
    [InlineData(ExtractionStatus.InvalidContent, CodigosIndexacion.TextoInvalido, null)]
    [InlineData(ExtractionStatus.FileNotFound, CodigosIndexacion.ArchivoNoEncontrado, null)]
    [InlineData(ExtractionStatus.Forbidden, CodigosIndexacion.ErrorInterno, MotivosIndexacion.AlmacenamientoRechazado)]
    [InlineData(ExtractionStatus.ContextExceeded, CodigosIndexacion.IndiceDemasiadoGrande, MotivosIndexacion.ContextoExcedido)]
    public async Task ExtraccionPermanente_FallaSinReintento_YSinLlamarAlProveedor(ExtractionStatus estado, string codigo, string? motivo)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento cuya extraccion no termina bien.");
        E.Extraccion.Forzar = estado;

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(EstadoIndexacion.Fallido, indice.Estado);
        Assert.Equal(codigo, indice.CodigoError);
        Assert.Equal(0, indice.Intentos);
        Assert.Null(indice.FragmentosCalculados);   // también en ContextExceeded: no hay conteo que guardar
        Assert.Null(indice.LimiteAplicado);
        Assert.Equal(0, E.Proveedor.Llamadas);
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED")));
        Assert.Equal(codigo, Texto(json, "codigoError"));
        Assert.Equal(motivo, Texto(json, "motivo"));

        // ContextExceeded no se reactiva: no tiene FragmentosCalculados.
        await E.Servicio().MarcarAsync(s.Tenants);
        Assert.Equal(EstadoIndexacion.Fallido, (await s.IndiceAsync(documento)).Estado);
    }

    [Fact]
    public async Task ExtraccionFallida_EsTransitoria_ReintentaSinEnfriarElProveedor()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento cuya extraccion falla una vez.");
        E.Extraccion.Forzar = ExtractionStatus.ExtractionFailed;
        var servicio = E.Servicio();

        var (_, resultado) = await IndexarAsync(s, servicio);

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Reintento, resultado);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Equal(1, indice.Intentos);
        Assert.Equal(CodigosIndexacion.ExtraccionFallida, indice.CodigoError);
        Assert.NotNull(indice.ProximoIntentoEn);
        Assert.False(servicio.EnEnfriamiento);
        Assert.Equal(0, E.Proveedor.Llamadas);

        // Al reintentar con la extracción recuperada se indexa y el código del último fallo se limpia.
        E.Extraccion.Forzar = null;
        await s.HacerElegibleAsync(indice.Id);
        var trabajo = Assert.Single(await servicio.AdquirirAsync(s.Tenants, 1));
        Assert.Equal(ResultadoIndexacion.Indexado, await servicio.ProcesarAsync(trabajo));
        indice = await s.IndiceAsync(documento);
        Assert.Null(indice.CodigoError);
        Assert.Equal(1, indice.Intentos);
    }

    [Fact]
    public async Task ArchivosReales_Inexistente_YRutaInsegura_Fallan()
    {
        var s = await EscenarioAsync();
        var inexistente = await s.DocumentoCrudoAsync($"{s.TenantId}/{s.ExpedienteId}/{Guid.NewGuid():N}.txt", "text/plain");
        var servicio = E.Servicio();
        var (_, resultado) = await IndexarAsync(s, servicio);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(CodigosIndexacion.ArchivoNoEncontrado, (await s.IndiceAsync(inexistente)).CodigoError);

        var insegura = await s.DocumentoCrudoAsync("../../fuera-del-almacenamiento.txt", "text/plain");
        (_, resultado) = await IndexarAsync(s, servicio);
        var indice = await s.IndiceAsync(insegura);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Contains(indice.CodigoError, new[] { CodigosIndexacion.ErrorInterno, CodigosIndexacion.ArchivoNoEncontrado });
        Assert.DoesNotContain("fuera-del-almacenamiento", string.Join("\n", E.Logs.Mensajes));
    }

    [Fact]
    public async Task LimiteDeFragmentos_FallaAntesDelProveedor_YSeReactivaCuandoCabe()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(5));
        var estricto = E.Servicio(o => o.MaxFragmentosPorDocumento = 3);

        var (_, resultado) = await IndexarAsync(s, estricto);

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(CodigosIndexacion.IndiceDemasiadoGrande, indice.CodigoError);
        Assert.Equal(5, indice.FragmentosCalculados);
        Assert.Equal(3, indice.LimiteAplicado);
        Assert.Equal(0, E.Proveedor.Llamadas);
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        var json = Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED")));
        Assert.Equal(MotivosIndexacion.LimiteDeFragmentos, Texto(json, "motivo"));
        Assert.Equal("5", Texto(json, "fragmentosCalculados"));
        Assert.Equal("3", Texto(json, "limiteAplicado"));

        // Con el mismo límite no se reactiva; con un límite suficiente vuelve a Pendiente con los intentos a cero.
        await estricto.MarcarAsync(s.Tenants);
        Assert.Equal(EstadoIndexacion.Fallido, (await s.IndiceAsync(documento)).Estado);
        await E.Servicio(o => o.MaxFragmentosPorDocumento = 4).MarcarAsync(s.Tenants);
        Assert.Equal(EstadoIndexacion.Fallido, (await s.IndiceAsync(documento)).Estado);

        var amplio = E.Servicio(o => o.MaxFragmentosPorDocumento = 5);
        await amplio.MarcarAsync(s.Tenants);
        indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Null(indice.CodigoError);

        var trabajo = Assert.Single(await amplio.AdquirirAsync(s.Tenants, 1));
        Assert.Equal(ResultadoIndexacion.Indexado, await amplio.ProcesarAsync(trabajo));
        indice = await s.IndiceAsync(documento);
        Assert.Equal(5, indice.Fragmentos);
        Assert.Null(indice.FragmentosCalculados);
        Assert.Null(indice.LimiteAplicado);
    }

    [Fact]
    public async Task PresupuestoPreventivo_AplazaAlDiaUtcSiguiente_SinSumarIntentoNiLlamarAlProveedor()
    {
        var s = await EscenarioAsync();
        var primero = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        var holgado = E.Servicio();
        await IndexarAsync(s, holgado);
        var usados = (await s.IndiceAsync(primero)).TokensTotales;
        E.Proveedor.Reiniciar();

        // El presupuesto del día cubre lo ya indexado pero no un segundo documento igual.
        var segundo = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2, "Segundo"));
        var ajustado = E.Servicio(o => o.MaxTokensDiariosPorTenant = usados + 10);
        var (_, resultado) = await IndexarAsync(s, ajustado);

        var indice = await s.IndiceAsync(segundo);
        var ahora = await AhoraAsync();
        Assert.Equal(ResultadoIndexacion.Aplazado, resultado);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Equal(0, indice.Intentos);
        Assert.Null(indice.CodigoError);
        Assert.Null(indice.ProcesandoDesde);
        Assert.InRange(indice.ProximoIntentoEn!.Value, ahora.Date.AddDays(1), ahora.Date.AddDays(1).AddMinutes(10));
        Assert.Equal(0, E.Proveedor.Llamadas);
        Assert.False(ajustado.EnEnfriamiento);
        Assert.Empty(await s.AuditoriasAsync(segundo, "DOCUMENT_INDEX_FAILED"));
        Assert.Contains(E.Logs.Mensajes, m => m.Contains("[INDEX_BUDGET_DEFERRED]"));
        Assert.Empty(await ajustado.AdquirirAsync(s.Tenants, 5));   // no es elegible hasta el día siguiente

        // El presupuesto es por tenant: otro tenant no se ve afectado. Con presupuesto suficiente, el documento entra.
        var otro = await EscenarioAsync();
        await otro.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        Assert.Equal(ResultadoIndexacion.Indexado, (await IndexarAsync(otro, ajustado)).Resultado);

        await s.HacerElegibleAsync(indice.Id);
        var trabajo = Assert.Single(await holgado.AdquirirAsync(s.Tenants, 1));
        Assert.Equal(ResultadoIndexacion.Indexado, await holgado.ProcesarAsync(trabajo));
    }

    // ── Hash (§14) ───────────────────────────────────────────────────────

    [Fact]
    public async Task HashDistintoTrasLaExtraccion_FallidoSinReencolar()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento cuyo archivo no coincide con su hash.");
        await s.SqlAsync("UPDATE documentos SET \"HashSha256\" = {0} WHERE \"Id\" = {1}", new string('a', 64), documento);
        var servicio = E.Servicio();

        var (_, resultado) = await IndexarAsync(s, servicio);

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(EstadoIndexacion.Fallido, indice.Estado);   // ni Obsoleto ni Pendiente
        Assert.Equal(CodigosIndexacion.TextoInvalido, indice.CodigoError);
        Assert.Equal(0, indice.Intentos);
        Assert.Null(indice.ProximoIntentoEn);
        Assert.Equal(0, E.Proveedor.Llamadas);
        Assert.Equal(MotivosIndexacion.HashDistinto, Texto(Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"))), "motivo"));
        Assert.Contains(E.Logs.Mensajes, m => m.Contains("[Error]") && m.Contains("[INDEX_HASH_MISMATCH]"));

        // Sin reencolado automático: ni sembrado, ni marcado, ni adquisición lo recuperan.
        Assert.Equal(0, await servicio.SembrarAsync(s.Tenants));
        await servicio.MarcarAsync(s.Tenants);
        Assert.Empty(await servicio.AdquirirAsync(s.Tenants, 5));
        Assert.Single(await s.IndicesAsync(documento));
    }

    [Fact]
    public async Task HashCambiadoDuranteLosEmbeddings_SeDetectaEnLaConfirmacion()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        E.Proveedor.AlLlamar = async (_, _, _) =>
        {
            await s.SqlAsync("UPDATE documentos SET \"HashSha256\" = {0} WHERE \"Id\" = {1}", new string('b', 64), documento);
            return null;
        };

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Fallido, resultado);
        Assert.Equal(CodigosIndexacion.TextoInvalido, indice.CodigoError);
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        Assert.Equal(MotivosIndexacion.HashDistinto, Texto(Json(Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"))), "motivo"));
        Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"));
    }

    // ── Persistencia (§13.1) ─────────────────────────────────────────────

    [Fact]
    public async Task ErrorTransitorioDeLaBase_EnLaConfirmacion_SeReintentaSinDuplicar()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(4));
        E.Proveedor.Tokens = _ => 80;
        var fallos = 0;
        E.Persistencia.AlGuardar = db =>
            db.ChangeTracker.Entries<DocumentoFragmento>().Any() && Interlocked.Increment(ref fallos) <= 2
                ? new NpgsqlException("fallo transitorio simulado", new IOException())
                : null;

        var (_, resultado) = await IndexarAsync(s, E.Servicio());

        Assert.Equal(ResultadoIndexacion.Indexado, resultado);
        Assert.True(fallos >= 3);
        var indice = await s.IndiceAsync(documento);
        Assert.Equal(4, await s.ContarFragmentosAsync(indice.Id));
        Assert.Single(await s.UsosAsync());
        Assert.Single(await s.AuditoriasAsync(documento, "DOCUMENT_INDEXED"));
        Assert.Equal(1, E.Proveedor.Llamadas);   // la política de la base es propia: no se repiten los embeddings
    }

    [Fact]
    public async Task ErrorPersistenteDeLaBase_NoEscribeEstado_YLoResuelveElLease()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(2));
        E.Persistencia.AlGuardar = db => db.ChangeTracker.Entries<DocumentoFragmento>().Any()
            ? new DbUpdateException("fallo persistente simulado", new InvalidOperationException())
            : null;
        var servicio = E.Servicio();

        var (_, resultado) = await IndexarAsync(s, servicio);

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.SinEstado, resultado);
        Assert.Equal(EstadoIndexacion.Procesando, indice.Estado);   // ningún estado escrito
        Assert.Equal(0, indice.Intentos);
        Assert.Null(indice.CodigoError);
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"));
        Assert.False(servicio.EnEnfriamiento);
        Assert.Contains(E.Logs.Mensajes, m => m.Contains("[INDEX_PERSISTENCE_ERROR]"));

        // El lease vence y la recuperación cuenta el intento.
        E.Persistencia.AlGuardar = null;
        await E.Servicio(o => o.LeaseSeconds = 0).RecuperarLeasesAsync();
        indice = await s.IndiceAsync(documento);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Equal(1, indice.Intentos);
        Assert.Equal(CodigosIndexacion.LeaseVencido, indice.CodigoError);
    }

    // ── Parada del host (§23) ────────────────────────────────────────────

    [Theory]
    [InlineData(1)]   // parada durante el primer lote: sin consumo que registrar
    [InlineData(2)]   // parada durante el segundo lote: el primero ya consumió
    public async Task ParadaLimpia_LiberaElTrabajoSinSumarIntento(int loteDeLaParada)
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync(Escenario84.TextoDeFragmentos(70));
        E.Proveedor.Tokens = _ => 50;
        using var parada = new CancellationTokenSource();
        E.Proveedor.AlLlamar = (n, _, ct) =>
        {
            if (n == loteDeLaParada)
            {
                parada.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult<EmbeddingBatchResult?>(null);
        };
        var servicio = E.Servicio();

        var (_, resultado) = await IndexarAsync(s, servicio, parada.Token);

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Liberado, resultado);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Equal(0, indice.Intentos);                 // no es un error ni un reintento
        Assert.Null(indice.ProximoIntentoEn);             // sin backoff
        Assert.Null(indice.CodigoError);
        Assert.Null(indice.ProcesandoDesde);
        Assert.Null(indice.ProcesadoPor);
        Assert.Equal(0, await s.ContarFragmentosAsync(indice.Id));
        Assert.False(servicio.EnEnfriamiento);
        Assert.Empty(await s.AuditoriasAsync(documento, "DOCUMENT_INDEX_FAILED"));
        Assert.Contains(E.Logs.Mensajes, m => m.Contains("[INDEX_RELEASED]"));

        var usos = await s.UsosAsync();
        if (loteDeLaParada == 1)
        {
            Assert.Empty(usos);
        }
        else
        {
            var uso = Assert.Single(usos);
            Assert.False(uso.Exitoso);
            Assert.Null(uso.CodigoError);                 // intento interrumpido, no un error
            Assert.Equal(50, uso.TokensEntrada);
        }

        // Disponible de inmediato para cualquier instancia.
        E.Proveedor.AlLlamar = null;
        var otro = E.Servicio();
        var trabajo = Assert.Single(await otro.AdquirirAsync(s.Tenants, 1));
        Assert.Equal(0, trabajo.Intentos);
        Assert.Equal(ResultadoIndexacion.Indexado, await otro.ProcesarAsync(trabajo));
    }

    [Fact]
    public async Task ParadaYaSolicitada_AntesDeEmpezar_LiberaSinLlamarAlProveedor()
    {
        var s = await EscenarioAsync();
        var documento = await s.DocumentoTxtAsync("Documento adquirido justo antes de la parada.");
        var servicio = E.Servicio();
        var trabajo = await s.AdquirirUnoAsync(servicio);
        using var parada = new CancellationTokenSource();
        parada.Cancel();

        var resultado = await servicio.ProcesarAsync(trabajo, parada.Token);

        var indice = await s.IndiceAsync(documento);
        Assert.Equal(ResultadoIndexacion.Liberado, resultado);
        Assert.Equal(EstadoIndexacion.Pendiente, indice.Estado);
        Assert.Equal(0, indice.Intentos);
        Assert.Equal(0, E.Proveedor.Llamadas);
        Assert.Empty(await s.UsosAsync());
    }
}
