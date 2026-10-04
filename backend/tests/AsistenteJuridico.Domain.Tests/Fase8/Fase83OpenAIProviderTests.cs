using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using static AsistenteJuridico.Domain.Tests.Fase8.Respuestas83;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.3 — OpenAICompatibleEmbeddingProvider con HttpClient real, AddHttpClient y el manejador de resiliencia
/// sobre un transporte simulado, sin red (FASE_8_3_CONTRATO.md §8, §9, §10 y §11; pruebas E2 y E11–E28).
/// </summary>
public class Fase83OpenAIProviderTests
{
    private static Task<EmbeddingBatchResult> Embed(Entorno83 e, int n = 2, CancellationToken ct = default) =>
        e.Proveedor.EmbedAsync(Textos(n), EmbeddingPurpose.Documento, ct);

    private static async Task<EmbeddingProviderException> FalloAsync(Entorno83 e, int n = 2) =>
        await Assert.ThrowsAsync<EmbeddingProviderException>(() => Embed(e, n));

    private static Entorno83 Con(TransporteSimulado transporte, TimeProvider? tiempo = null, Dictionary<string, string?>? config = null) =>
        new(transporte, config, tiempo: tiempo ?? new TiempoFalso(disparar: true));

    // ── E11, E2, E1: éxito ───────────────────────────────────────────────

    [Fact]
    public async Task Exito_PeticionExacta_YResultadoCompleto()
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(2, tokens: 17)));
        using var e = Con(transporte);

        var r = await Embed(e);

        Assert.Equal(2, r.Vectores.Count);
        Assert.All(r.Vectores, v => Assert.Equal(1536, v.Length));
        Assert.Equal((1f, 0f), (r.Vectores[0][0], r.Vectores[0][1]));
        Assert.Equal((0f, 1f), (r.Vectores[1][0], r.Vectores[1][1]));
        Assert.Equal((17, Entorno83.Modelo, Entorno83.Modelo, "openai-compatible", 1536),
            (r.TokensEntrada, r.ModelId, r.ModeloDeclarado, r.ProviderId, r.Dimensiones));

        var peticion = Assert.Single(transporte.Peticiones);
        Assert.Equal(("POST", "https://embeddings.prueba/v1/embeddings"), (peticion.Metodo, peticion.Url.ToString()));
        Assert.Equal("Bearer " + Entorno83.Clave, peticion.Cabeceras["Authorization"]);
    }

    [Fact]
    public async Task Lote_De64_YOrdenDesordenado_SeReordenaPorIndice()
    {
        var indices = Enumerable.Range(0, 64).Reverse().ToArray();
        using var e = Con(TransporteSimulado.Fijo(() => Ok(Json(64, indices: indices))));

        var r = await Embed(e, 64);

        Assert.Equal(64, r.Vectores.Count);
        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(1f, r.Vectores[i][i]);   // Vectores[i] corresponde a entradas[i]
        }
    }

    // ── E4: validación de entrada sin red ───────────────────────────────

    [Fact]
    public async Task LoteInvalido_ArgumentException_SinLlamadasDeRed()
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(1)));
        using var e = Con(transporte);
        var p = e.Proveedor;

        await Assert.ThrowsAsync<ArgumentNullException>(() => p.EmbedAsync(null!, EmbeddingPurpose.Documento, default));
        await Assert.ThrowsAsync<ArgumentException>(() => p.EmbedAsync([], EmbeddingPurpose.Documento, default));
        await Assert.ThrowsAsync<ArgumentException>(() => p.EmbedAsync(Textos(65), EmbeddingPurpose.Documento, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => p.EmbedAsync(["a", null!], EmbeddingPurpose.Documento, default));
        await Assert.ThrowsAsync<ArgumentException>(() => p.EmbedAsync(["a", "  "], EmbeddingPurpose.Documento, default));
        await Assert.ThrowsAsync<ArgumentException>(() => p.EmbedAsync(["x" + (char)0xDC00], EmbeddingPurpose.Documento, default));
        await Assert.ThrowsAsync<ArgumentException>(() => p.EmbedAsync([new string('a', 8191 * 4 + 1)], EmbeddingPurpose.Documento, default));

        Assert.Equal(0, transporte.Llamadas);
    }

    // ── E12: tokens informados o no informados (nunca estimados) ────────

    [Theory]
    [InlineData(null)]                                   // sin usage
    [InlineData("{}")]                                   // usage sin prompt_tokens
    [InlineData("{\"prompt_tokens\":-3}")]
    [InlineData("{\"prompt_tokens\":2.5}")]
    [InlineData("{\"prompt_tokens\":\"12\"}")]
    [InlineData("null")]
    public async Task SinConsumoInformado_TokensEntradaEsNull_NuncaUnaEstimacion(string? usage)
    {
        using var e = Con(TransporteSimulado.Fijo(() => Ok(Json(2, tokens: null, usageCrudo: usage))));

        var r = await Embed(e);

        Assert.Null(r.TokensEntrada);
        Assert.Equal(2, r.Vectores.Count);
        Assert.Contains(e.Logs.Mensajes, m => m.Contains("[Warning]") && m.Contains("no informó el consumo"));
    }

    [Fact]
    public async Task ConsumoInformadoComoCero_EsCero_NoNull()
    {
        using var e = Con(TransporteSimulado.Fijo(() => Ok(Json(2, tokens: 0))));
        Assert.Equal(0, (await Embed(e)).TokensEntrada);
    }

    // ── E12b: modelo solicitado frente a declarado ──────────────────────

    [Fact]
    public async Task ModeloNoDeclarado_SeAcepta_ConModeloDeclaradoNull()
    {
        foreach (var json in new[]
        {
            Json(2, modelo: null),
            Json(2, modelo: ""),
            Json(2).Replace("\"model\":\"" + Entorno83.Modelo + "\"", "\"model\":null")
        })
        {
            using var e = Con(TransporteSimulado.Fijo(() => Ok(json)));
            var r = await Embed(e);
            Assert.Null(r.ModeloDeclarado);
            Assert.Equal(Entorno83.Modelo, r.ModelId);
        }
    }

    [Theory]
    [InlineData("text-embedding-3-large")]
    [InlineData("otro-modelo")]
    [InlineData("text-embedding-3-small-2024-01-25")]   // sufijo de versión: sin equivalencias
    [InlineData("Text-Embedding-3-Small")]              // otras mayúsculas: sin normalización
    [InlineData(" text-embedding-3-small")]
    public async Task ModeloDeclaradoDistinto_SeRechaza_ModeloInesperado(string declarado)
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(2, modelo: declarado)));
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.Equal((MotivoFalloEmbedding.ModeloInesperado, false, "AI_PROVIDER_ERROR"), (fallo.Motivo, fallo.EsTransitorio, fallo.ErrorCode));
        Assert.Equal(1, transporte.Llamadas);   // no se reintenta
        Assert.DoesNotContain(e.Logs.Mensajes, m => m.Contains(declarado));
    }

    [Fact]
    public async Task ModeloDeclarado_QueNoEsCadena_RespuestaInvalida()
    {
        using var e = Con(TransporteSimulado.Fijo(() => Ok(Json(2).Replace("\"model\":\"" + Entorno83.Modelo + "\"", "\"model\":42"))));
        Assert.Equal(MotivoFalloEmbedding.RespuestaInvalida, (await FalloAsync(e)).Motivo);
    }

    // ── E13–E18: reintentos y clasificación por código de estado ────────

    [Fact]
    public async Task Un429YDespues200_ExitoConUnReintento()
    {
        var transporte = TransporteSimulado.Secuencia(() => Estado(HttpStatusCode.TooManyRequests), () => Ok(Json(2)));
        using var e = Con(transporte);

        Assert.Equal(2, (await Embed(e)).Vectores.Count);
        Assert.Equal(2, transporte.Llamadas);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task Siempre429_ExactamenteTresIntentos_LimiteDeTasa(int fallosDisponibles)
    {
        var respuestas = Enumerable.Repeat<Func<HttpResponseMessage>>(() => Estado(HttpStatusCode.TooManyRequests, retryAfter: 7), fallosDisponibles).ToArray();
        var transporte = TransporteSimulado.Secuencia(respuestas);
        var tiempo = new TiempoFalso(disparar: true);
        using var e = Con(transporte, tiempo);

        var fallo = await FalloAsync(e);

        Assert.Equal(3, transporte.Llamadas);
        Assert.Equal((MotivoFalloEmbedding.LimiteDeTasa, true, (int?)429, 3, (TimeSpan?)TimeSpan.FromSeconds(7)),
            (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos, fallo.EsperaSugerida));
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, tiempo.Esperas);   // respeta Retry-After
    }

    [Fact]
    public async Task Siempre503_TresIntentos_NoDisponible()
    {
        var transporte = TransporteSimulado.Fijo(() => Estado(HttpStatusCode.ServiceUnavailable));
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.Equal(3, transporte.Llamadas);
        Assert.Equal((MotivoFalloEmbedding.NoDisponible, true, (int?)503, 3, (TimeSpan?)null),
            (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos, fallo.EsperaSugerida));
    }

    [Fact]
    public async Task ErrorDeRedPersistente_TresIntentos_NoDisponible_YRecuperacionEnElSegundo()
    {
        var caido = new TransporteSimulado((_, _) => throw new HttpRequestException("fallo de red hacia https://usuario:SECRETO-URL@host"));
        using (var e = Con(caido))
        {
            var fallo = await FalloAsync(e);
            Assert.Equal(3, caido.Llamadas);
            Assert.Equal((MotivoFalloEmbedding.NoDisponible, true, (int?)null, 3), (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos));
            Assert.Null(fallo.InnerException);
            Assert.DoesNotContain("SECRETO-URL", fallo.ToString());
            Assert.DoesNotContain(e.Logs.Mensajes, m => m.Contains("SECRETO-URL"));
        }

        var intermitente = new TransporteSimulado((n, _) => n == 1 ? throw new HttpRequestException("red") : Task.FromResult(Ok(Json(2))));
        using var e2 = Con(intermitente);
        Assert.Equal(2, (await Embed(e2)).Vectores.Count);
        Assert.Equal(2, intermitente.Llamadas);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Errores5xxNoReintentables_UnSoloIntento_TransitorioParaElConsumidor(HttpStatusCode estado)
    {
        var transporte = TransporteSimulado.Fijo(() => Estado(estado));
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.Equal(1, transporte.Llamadas);
        Assert.Equal((MotivoFalloEmbedding.ErrorDelServidor, true, (int?)(int)estado, 1), (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, MotivoFalloEmbedding.SolicitudRechazada)]
    [InlineData(HttpStatusCode.NotFound, MotivoFalloEmbedding.SolicitudRechazada)]
    [InlineData(HttpStatusCode.Conflict, MotivoFalloEmbedding.SolicitudRechazada)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, MotivoFalloEmbedding.SolicitudRechazada)]
    [InlineData(HttpStatusCode.UnprocessableEntity, MotivoFalloEmbedding.SolicitudRechazada)]
    [InlineData(HttpStatusCode.Unauthorized, MotivoFalloEmbedding.Autenticacion)]
    [InlineData(HttpStatusCode.Forbidden, MotivoFalloEmbedding.Autenticacion)]
    public async Task Errores4xx_UnSoloIntento_Permanentes(HttpStatusCode estado, MotivoFalloEmbedding motivo)
    {
        var transporte = TransporteSimulado.Fijo(() => Estado(estado));
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.Equal(1, transporte.Llamadas);
        Assert.Equal((motivo, false, (int?)(int)estado, 1), (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos));
    }

    [Fact]
    public async Task Redireccion_NoSeSigue_SolicitudRechazada()
    {
        var transporte = TransporteSimulado.Fijo(() =>
        {
            var r = Estado(HttpStatusCode.TemporaryRedirect);
            r.Headers.Location = new Uri("https://otro-host.prueba/v1/embeddings");
            return r;
        });
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.Equal((MotivoFalloEmbedding.SolicitudRechazada, false, (int?)307), (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp));
        Assert.Equal(1, transporte.Llamadas);
        Assert.All(transporte.Peticiones, p => Assert.Equal("embeddings.prueba", p.Url.Host));
    }

    // ── E19–E21: respuesta inválida (determinista, todo o nada) ─────────

    public static TheoryData<string, string> RespuestasInvalidas => new()
    {
        { "json-invalido", "{ esto no es json" },
        { "vacio", "" },
        { "sin-data", "{\"object\":\"list\"}" },
        { "data-no-array", "{\"data\":{}}" },
        { "raiz-array", "[]" },
        { "faltan-vectores", Json(1) },
        { "sobran-vectores", Json(3) },
        { "indice-repetido", Json(2, indices: [0, 0]) },
        { "indice-fuera-de-rango", Json(2, indices: [0, 2]) },
        { "indice-negativo", Json(2, indices: [-1, 0]) },
        { "indice-ausente", Json(2).Replace("\"index\":1,", "") },
        { "indice-no-entero", Json(2).Replace("\"index\":1", "\"index\":1.5") },
        { "embedding-ausente", Json(2).Replace("\"embedding\":", "\"otro\":") },
        { "nan", Json(2, vector: i => VectorJson(i, sustituto: "NaN")) },
        { "infinito", Json(2, vector: i => VectorJson(i, sustituto: "1e999")) },
        { "desborda-float", Json(2, vector: i => VectorJson(i, sustituto: "1e39")) },
        { "valor-no-numerico", Json(2, vector: i => VectorJson(i, sustituto: "\"1\"")) },
        { "valor-null", Json(2, vector: i => VectorJson(i, sustituto: "null")) },
        { "norma-cero", Json(2, vector: _ => VectorJson(-1)) },
    };

    [Theory]
    [MemberData(nameof(RespuestasInvalidas))]
    public async Task RespuestaInvalida_SeRechaza_SinVectores_SinReintentos(string caso, string cuerpo)
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(cuerpo));
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.True(fallo.Motivo == MotivoFalloEmbedding.RespuestaInvalida, caso);
        Assert.False(fallo.EsTransitorio);
        Assert.Equal(1, transporte.Llamadas);
    }

    [Theory]
    [InlineData(1535)]
    [InlineData(1537)]
    [InlineData(1024)]
    [InlineData(0)]
    public async Task DimensionDistinta_SeRechaza_SinPaddingNiTruncamiento(int dimension)
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(2, vector: i => i == 1 ? VectorJson(0, dimension) : VectorJson(0))));
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.Equal((MotivoFalloEmbedding.DimensionInvalida, false), (fallo.Motivo, fallo.EsTransitorio));
        Assert.Equal(1, transporte.Llamadas);
        Assert.Contains(e.Logs.Mensajes, m => m.Contains($"dimensión {dimension}") && m.Contains("1536"));
    }

    [Fact]
    public async Task RespuestaDeMasDe16MiB_SeRechaza_ConOSinContentLength()
    {
        const int limite = 16 * 1024 * 1024;

        using (var e = Con(TransporteSimulado.Fijo(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[limite + 1]) })))
        {
            Assert.Equal(MotivoFalloEmbedding.RespuestaInvalida, (await FalloAsync(e)).Motivo);
        }

        // Sin Content-Length (flujo): se corta al superar el límite, sin procesar nada.
        using (var e = Con(TransporteSimulado.Fijo(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlujoSinLongitud(limite + 1)) })))
        {
            Assert.Equal(MotivoFalloEmbedding.RespuestaInvalida, (await FalloAsync(e)).Motivo);
        }
    }

    // ── E22–E24: timeout y cancelación ──────────────────────────────────

    [Fact]
    public async Task ProveedorQueNoResponde_TimeoutDelLote()
    {
        var transporte = TransporteSimulado.QueNuncaResponde();
        using var e = Con(transporte, config: new() { ["AI:Embeddings:TimeoutSeconds"] = "0.3" });
        var cronometro = Stopwatch.StartNew();

        var fallo = await Assert.ThrowsAsync<EmbeddingProviderTimeoutException>(() => Embed(e));

        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(2), $"Tardó {cronometro.Elapsed.TotalSeconds:F2} s");
        Assert.Equal((MotivoFalloEmbedding.Timeout, true, "AI_PROVIDER_TIMEOUT", (int?)null, 1),
            (fallo.Motivo, fallo.EsTransitorio, fallo.ErrorCode, fallo.CodigoEstadoHttp, fallo.Intentos));
        Assert.IsAssignableFrom<AIProviderTimeoutException>(fallo);

        await Task.Delay(300);
        Assert.Equal(1, transporte.Llamadas);   // sin peticiones posteriores
    }

    // Corrección final 1: Intentos está siempre entre 1 y 3, también si el fallo llega antes del primer envío HTTP.
    [Fact]
    public async Task TimeoutAntesDelPrimerEnvio_IntentosEsUno_YNingunaPeticionSale()
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(2)));
        var bloqueo = new BloqueoAntesDelPrimerEnvio();
        using var e = new Entorno83(transporte, new() { ["AI:Embeddings:TimeoutSeconds"] = "0.3" },
            tiempo: new TiempoFalso(disparar: true), adicionales: s => s.AddSingleton<IHttpMessageHandlerBuilderFilter>(bloqueo));

        var fallo = await Assert.ThrowsAsync<EmbeddingProviderTimeoutException>(() => Embed(e));

        Assert.Equal(1, bloqueo.Entradas);       // la llamada entró en la cadena HTTP…
        Assert.Equal(0, transporte.Llamadas);    // …pero ninguna petición llegó a emitirse
        Assert.Equal(1, fallo.Intentos);         // la llamada es en sí el primer intento
        Assert.Equal((MotivoFalloEmbedding.Timeout, true, (int?)null), (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp));
        Assert.Contains(e.Logs.Mensajes, m => m.Contains("tras 1 intento(s)"));
    }

    [Fact]
    public async Task CancelacionAntesDelPrimerEnvio_SePropagaSinExcepcionDelProveedor_YNingunaPeticionSale()
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(2)));
        var bloqueo = new BloqueoAntesDelPrimerEnvio();
        using var e = new Entorno83(transporte, tiempo: new TiempoFalso(disparar: true),
            adicionales: s => s.AddSingleton<IHttpMessageHandlerBuilderFilter>(bloqueo));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Embed(e, ct: cts.Token));

        Assert.IsNotAssignableFrom<AIProviderException>(error);
        Assert.Equal(0, transporte.Llamadas);
    }

    [Fact]
    public void Intentos_SoloAdmiteElRangoContractualDe1A3()
    {
        Assert.Equal((1, 3), (ClasificacionFalloEmbedding.IntentosMinimos, ClasificacionFalloEmbedding.IntentosMaximos));
        Assert.Equal(1, new EmbeddingProviderException(MotivoFalloEmbedding.NoDisponible).Intentos);   // valor por defecto
        Assert.Equal(1, new EmbeddingProviderTimeoutException().Intentos);

        foreach (var fuera in new[] { 0, -1, 4, 6 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EmbeddingProviderException(MotivoFalloEmbedding.NoDisponible, null, fuera));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EmbeddingProviderTimeoutException(fuera));
        }

        foreach (var dentro in new[] { 1, 2, 3 })
        {
            Assert.Equal(dentro, new EmbeddingProviderException(MotivoFalloEmbedding.LimiteDeTasa, 429, dentro).Intentos);
            Assert.Equal(dentro, new EmbeddingProviderTimeoutException(dentro).Intentos);
        }
    }

    [Fact]
    public async Task TimeoutDuranteLaEsperaEntreReintentos_ElLimiteIncluyeElBackoff()
    {
        var transporte = TransporteSimulado.Fijo(() => Estado(HttpStatusCode.ServiceUnavailable));
        using var e = Con(transporte, new TiempoFalso(disparar: false), new() { ["AI:Embeddings:TimeoutSeconds"] = "0.3" });

        var fallo = await Assert.ThrowsAsync<EmbeddingProviderTimeoutException>(() => Embed(e));

        Assert.Equal(1, fallo.Intentos);
        Assert.Equal(1, transporte.Llamadas);
    }

    [Fact]
    public async Task CancelacionDelLlamador_AntesDuranteLaPeticionYDuranteElBackoff_SePropagaSinTraducir()
    {
        using (var e = Con(TransporteSimulado.Fijo(() => Ok(Json(2)))))
        {
            using var antes = new CancellationTokenSource();
            await antes.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Embed(e, ct: antes.Token));
        }

        var colgado = TransporteSimulado.QueNuncaResponde();
        using (var e = Con(colgado))
        {
            using var durante = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Embed(e, ct: durante.Token));
            Assert.IsNotAssignableFrom<AIProviderException>(error);
        }

        var caido = TransporteSimulado.Fijo(() => Estado(HttpStatusCode.ServiceUnavailable));
        using (var e = Con(caido, new TiempoFalso(disparar: false)))
        {
            using var enBackoff = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Embed(e, ct: enBackoff.Token));
            await Task.Delay(200);
            Assert.Equal(1, caido.Llamadas);   // sin reintentos posteriores a la cancelación
        }
    }

    // ── Revisión final: solo el transporte es NoDisponible; lo inesperado no se oculta ni se reintenta ──

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(FormatException))]
    public async Task ErrorInternoInesperado_SePropagaSinClasificar_SinReintentos_YSinRegistrarSuMensaje(Type tipo)
    {
        var transporte = new TransporteSimulado((_, _) => throw (Exception)Activator.CreateInstance(tipo, "DEFECTO-INTERNO con SECRETO-FRAGMENTO")!);
        using var e = Con(transporte);

        var error = await Assert.ThrowsAsync(tipo, () => Embed(e));

        Assert.IsNotAssignableFrom<IFalloProveedorEmbeddings>(error);   // no es un fallo transitorio del proveedor
        Assert.IsNotAssignableFrom<AIProviderException>(error);
        Assert.Equal(1, transporte.Llamadas);                            // sin reintentos
        Assert.Contains(e.Logs.Mensajes, m => m.Contains("Error interno inesperado") && m.Contains(tipo.Name));
        Assert.DoesNotContain(e.Logs.Mensajes, m => m.Contains("DEFECTO-INTERNO") || m.Contains("SECRETO-FRAGMENTO"));
    }

    [Fact]
    public async Task ErrorInternoInesperado_ConElLimiteYaVencido_NoSeDisfrazaDeTimeout()
    {
        var transporte = new TransporteSimulado(async (_, _) =>
        {
            await Task.Delay(500);   // ignora el token: el límite de 0,2 s vence antes de que lance
            throw new InvalidOperationException("DEFECTO-INTERNO");
        });
        using var e = Con(transporte, config: new() { ["AI:Embeddings:TimeoutSeconds"] = "0.2" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Embed(e));

        Assert.IsNotAssignableFrom<IFalloProveedorEmbeddings>(error);
        Assert.Equal(1, transporte.Llamadas);
    }

    [Fact]
    public async Task ErrorDeEntradaSalidaDelTransporte_NoDisponible_SinReintentosInternos()
    {
        var transporte = new TransporteSimulado((_, _) => throw new IOException("conexión restablecida hacia SECRETO-URL"));
        using var e = Con(transporte);

        var fallo = await FalloAsync(e);

        Assert.Equal((MotivoFalloEmbedding.NoDisponible, true, (int?)null, 1), (fallo.Motivo, fallo.EsTransitorio, fallo.CodigoEstadoHttp, fallo.Intentos));
        Assert.Equal(1, transporte.Llamadas);   // el manejador de resiliencia solo reintenta HttpRequestException, 429 y 503
        Assert.Null(fallo.InnerException);
        Assert.DoesNotContain(e.Logs.Mensajes, m => m.Contains("SECRETO-URL"));
    }

    [Fact]
    public async Task FalloDeTransporteProvocadoPorLaCancelacionDelLlamador_SiguiendoSiendoCancelacion()
    {
        var transporte = new TransporteSimulado(async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Algunos transportes convierten la cancelación en un error de red.
            }

            throw new HttpRequestException("conexión abortada");
        });
        using var e = Con(transporte);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Embed(e, ct: cts.Token));

        Assert.IsNotAssignableFrom<AIProviderException>(error);
        Assert.Equal(1, transporte.Llamadas);
    }

    // ── E25, E25b: backoff contractual y sin configuración ──────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backoff_Base1_5sExponencialConJitter_YTresIntentos_AunqueLaConfiguracionPidaOtraCosa(bool conConfiguracionInvalida)
    {
        var config = conConfiguracionInvalida
            ? new Dictionary<string, string?>
            {
                ["AI:Embeddings:MaxRetries"] = "5",
                ["AI:Embeddings:MaxReintentos"] = "5",
                ["AI:Embeddings:RetryBaseDelaySeconds"] = "30",
                ["AI:Embeddings:RetryDelay"] = "30",
                ["AI:Embeddings:RetardoBaseReintento"] = "00:00:30",
            }
            : null;

        var primeras = new List<double>();
        var segundas = new List<double>();
        for (var i = 0; i < 400; i++)
        {
            var transporte = TransporteSimulado.Fijo(() => Estado(HttpStatusCode.ServiceUnavailable));
            var tiempo = new TiempoFalso(disparar: true);
            using var e = Con(transporte, tiempo, config);

            var fallo = await FalloAsync(e);

            Assert.Equal(3, transporte.Llamadas);   // nunca 6
            Assert.Equal(3, fallo.Intentos);
            var esperas = tiempo.Esperas.ToArray();
            Assert.Equal(2, esperas.Length);
            Assert.All(esperas, espera => Assert.InRange(espera.TotalSeconds, 0, 5));
            primeras.Add(esperas[0].TotalSeconds);
            segundas.Add(esperas[1].TotalSeconds);
        }

        // Exponencial con jitter sobre una base de 1,5 s: la primera espera ronda 1,5 s y la segunda es mayor.
        // Medido con 400 muestras: primera espera con media de ~1,31 s y máximo de ~2,06 s (1,5 s por el factor de
        // jitter de Polly); segunda con media de ~1,74 s y máximo de ~4,1 s. Con una base de 30 s serían ~20 veces más.
        Assert.InRange(primeras.Average(), 1.0, 1.6);
        Assert.InRange(primeras.Max(), 1.7, 2.3);
        Assert.InRange(segundas.Average(), 1.4, 2.2);
        Assert.InRange(segundas.Max(), 2.5, 5.0);
        Assert.True(segundas.Average() > primeras.Average());
        Assert.True(primeras.Distinct().Count() > 10, "Sin jitter");
    }

    // ── E26, E27: ni la clave, ni los textos, ni el cuerpo del proveedor ─

    [Fact]
    public async Task LogsYExcepciones_SinClaveNiTextosNiCuerpoDelProveedorNiVectores()
    {
        string[] textos = ["SECRETO-FRAGMENTO del cliente Juan Pérez", "otro SECRETO-FRAGMENTO"];
        var escenarios = new Func<TransporteSimulado>[]
        {
            () => TransporteSimulado.Fijo(() => Ok(Json(2))),
            () => TransporteSimulado.Fijo(() => Ok(Json(2, tokens: null))),
            () => TransporteSimulado.Fijo(() => Estado(HttpStatusCode.TooManyRequests)),
            () => TransporteSimulado.Fijo(() => Estado(HttpStatusCode.Unauthorized)),
            () => TransporteSimulado.Fijo(() => Estado(HttpStatusCode.BadRequest, "{\"error\":\"eco: SECRETO-FRAGMENTO CUERPO-DEL-PROVEEDOR\"}")),
            () => TransporteSimulado.Fijo(() => Estado(HttpStatusCode.InternalServerError)),
            () => TransporteSimulado.Fijo(() => Ok("{ CUERPO-DEL-PROVEEDOR no es json")),
            () => TransporteSimulado.Fijo(() => Ok(Json(2, modelo: "otro"))),
            () => TransporteSimulado.Fijo(() => Ok(Json(2, vector: i => VectorJson(0, 7)))),
            () => new TransporteSimulado((_, _) => throw new HttpRequestException("https://x:" + Entorno83.Clave + "@host SECRETO-FRAGMENTO")),
            TransporteSimulado.QueNuncaResponde,
        };

        var evidencias = new List<string>();
        foreach (var crear in escenarios)
        {
            using var e = Con(crear(), config: new() { ["AI:Embeddings:TimeoutSeconds"] = "0.3" });
            try
            {
                await e.Proveedor.EmbedAsync(textos, EmbeddingPurpose.Documento, default);
            }
            catch (Exception ex)
            {
                Assert.InRange(Assert.IsAssignableFrom<IFalloProveedorEmbeddings>(ex).Intentos, 1, 3);
                Assert.Null(ex.InnerException);
                evidencias.Add(ex.Message);
                evidencias.Add(ex.ToString());
            }

            evidencias.AddRange(e.Logs.Mensajes);
        }

        Assert.True(evidencias.Count > 20);
        foreach (var texto in evidencias)
        {
            foreach (var prohibido in new[] { Entorno83.Clave, "Bearer", "SECRETO-FRAGMENTO", "Juan Pérez", "CUERPO-DEL-PROVEEDOR", "0,0,0", "\"embedding\"" })
            {
                Assert.DoesNotContain(prohibido, texto, StringComparison.Ordinal);
            }
        }
    }

    // ── E28: privacidad del payload ─────────────────────────────────────

    [Fact]
    public async Task Payload_SoloModeloTextosYFormato_SinIdentificadoresNiCabecerasDeUsuario()
    {
        string[] textos = ["Primer fragmento con ñ y \"comillas\"", "Segundo fragmento\ncon salto", "Primer fragmento con ñ y \"comillas\""];
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(3)));
        using var e = Con(transporte);

        await e.Proveedor.EmbedAsync(textos, EmbeddingPurpose.Consulta, default);

        var peticion = Assert.Single(transporte.Peticiones);
        using var json = JsonDocument.Parse(peticion.Cuerpo);
        Assert.Equal(new[] { "model", "input", "encoding_format" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Entorno83.Modelo, json.RootElement.GetProperty("model").GetString());
        Assert.Equal("float", json.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal(textos, json.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()));   // exactamente las entradas, en orden

        foreach (var prohibido in new[] { "user", "dimensions", "tenant", "documento", "expediente", "usuario", "titulo", "ruta", "hash", "metadata" })
        {
            Assert.False(json.RootElement.TryGetProperty(prohibido, out _), prohibido);
        }

        Assert.Equal(new[] { "Authorization", "Content-Type" },
            peticion.Cabeceras.Keys.Where(k => !k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Bearer " + Entorno83.Clave, peticion.Cabeceras["Authorization"]);
        Assert.DoesNotContain("Cookie", peticion.Cabeceras.Keys);
    }

    [Fact]
    public async Task SinClave_EnLoopback_NoEnviaCabeceraDeAutenticacion()
    {
        var transporte = TransporteSimulado.Fijo(() => Ok(Json(1)));
        using var e = new Entorno83(transporte, new() { ["AI:Embeddings:BaseUrl"] = "http://localhost:8080/v1", ["AI:Embeddings:ApiKey"] = "" });

        await Embed(e, 1);

        var peticion = Assert.Single(transporte.Peticiones);
        Assert.DoesNotContain("Authorization", peticion.Cabeceras.Keys);
        Assert.Equal("http://localhost:8080/v1/embeddings", peticion.Url.ToString());
    }

    // ── HTTP real en loopback: redirección no seguida y petición correcta ─

    [Fact]
    public async Task HttpReal_EnLoopback_ExitoYRedireccionNoSeguida()
    {
        var destino = 0;
        string? autorizacionEnDestino = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/ok/embeddings", () => Results.Content(Json(2), "application/json"));
        app.MapPost("/redirige/embeddings", () => Results.Redirect("/destino/embeddings", permanent: false, preserveMethod: true));
        app.MapPost("/destino/embeddings", (HttpRequest peticion) =>
        {
            Interlocked.Increment(ref destino);
            autorizacionEnDestino = peticion.Headers.Authorization;
            return Results.Content(Json(2), "application/json");
        });
        await app.StartAsync();
        var raiz = app.Urls.First();

        // Manejador primario real (sin transporte simulado): SocketsHttpHandler sin redirecciones.
        using (var e = new Entorno83(null, new() { ["AI:Embeddings:BaseUrl"] = raiz + "/ok" }))
        {
            Assert.Equal(2, (await Embed(e)).Vectores.Count);
        }

        using (var e = new Entorno83(null, new() { ["AI:Embeddings:BaseUrl"] = raiz + "/redirige" }))
        {
            var fallo = await FalloAsync(e);
            Assert.Equal((MotivoFalloEmbedding.SolicitudRechazada, (int?)307, 1), (fallo.Motivo, fallo.CodigoEstadoHttp, fallo.Intentos));
        }

        Assert.Equal(0, destino);                 // la redirección nunca se siguió
        Assert.Null(autorizacionEnDestino);       // la clave no viajó a otro destino
        await app.StopAsync();
    }

    /// <summary>Flujo no posicionable de N bytes de espacios (respuesta sin Content-Length).</summary>
    private sealed class FlujoSinLongitud(long total) : Stream
    {
        private long _restante = total;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _restante);
            buffer.AsSpan(offset, n).Fill((byte)' ');
            _restante -= n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
