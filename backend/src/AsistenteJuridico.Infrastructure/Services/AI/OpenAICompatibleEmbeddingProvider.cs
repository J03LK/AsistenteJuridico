using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Fase 8.3 — Proveedor de embeddings compatible con la API de OpenAI (<c>POST {BaseUrl}/embeddings</c>),
/// FASE_8_3_CONTRATO.md §8.
///
/// - Envía únicamente <c>model</c>, <c>input</c> y <c>encoding_format</c>: nunca identificadores, títulos, rutas,
///   hashes, el campo <c>user</c> ni <c>dimensions</c>. La única cabecera de autenticación es su propia clave.
/// - Límite duro por lote (60 s) que incluye los reintentos y sus esperas. Los reintentos (como máximo 2, base 1,5 s,
///   exponencial con jitter; solo ante 429, 503 y errores de red) los aplica el manejador de resiliencia
///   <c>ai-embeddings-retry</c> registrado en DI: aquí no hay ninguna opción que los cambie.
/// - Valida la respuesta de forma estricta (cardinalidad, índices, dimensión, valores finitos, norma, modelo
///   declarado y tamaño): todo o nada, sin padding, truncamiento ni vectores inventados.
/// - No estima tokens, no calcula costes, no registra consumo y no persiste nada.
/// - Logs y excepciones: solo códigos de estado, tipos de excepción y conteos; nunca la clave, los textos, el cuerpo
///   de la respuesta ni los vectores.
/// </summary>
public sealed class OpenAICompatibleEmbeddingProvider : IEmbeddingProvider
{
    public const string Proveedor = "openai-compatible";

    /// <summary>Contador de intentos HTTP de una llamada; lo incrementa <see cref="ContadorDeIntentosHandler"/> en cada envío.</summary>
    internal static readonly HttpRequestOptionsKey<ContadorDeIntentos> ClaveIntentos = new("AsistenteJuridico.Embeddings.Intentos");

    private readonly HttpClient _httpClient;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OpenAICompatibleEmbeddingProvider> _logger;

    public OpenAICompatibleEmbeddingProvider(
        HttpClient httpClient,
        IOptions<EmbeddingOptions> options,
        ILogger<OpenAICompatibleEmbeddingProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderId => Proveedor;
    public string ModelId => _options.ModelId;
    public int Dimensiones => _options.Dimensiones;
    public int MaxTokensPorEntrada => EmbeddingOptions.MaxTokensPorEntrada;
    public int MaxEntradasPorLote => LoteEmbeddings.LimiteEfectivo(_options.MaxEntradasPorLote);

    public async Task<EmbeddingBatchResult> EmbedAsync(IReadOnlyList<string> entradas, EmbeddingPurpose proposito, CancellationToken ct)
    {
        LoteEmbeddings.Validar(entradas, MaxEntradasPorLote, MaxTokensPorEntrada);
        ct.ThrowIfCancellationRequested();

        var intentos = new ContadorDeIntentos();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            return await EmbedCoreAsync(entradas, intentos, timeoutCts.Token);
        }
        catch (EmbeddingProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación del llamador: se propaga sin traducir.
            throw;
        }
        catch (Exception ex) when (EsFalloDeTransporte(ex) && ct.IsCancellationRequested)
        {
            // El transporte falló porque el llamador canceló: sigue siendo una cancelación, no un fallo del proveedor.
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when ((ex is OperationCanceledException || EsFalloDeTransporte(ex)) && timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "El proveedor de embeddings superó el tiempo máximo de {TimeoutSeconds} s por lote tras {Intentos} intento(s); operación cancelada.",
                _options.TimeoutSeconds, intentos.Intentos);
            throw new EmbeddingProviderTimeoutException(intentos.Intentos);
        }
        catch (Exception ex) when (EsFalloDeTransporte(ex))
        {
            // Error de red o de transporte (tras los reintentos del manejador de resiliencia) o fallo de E/S al leer la
            // respuesta. Solo el tipo: el mensaje original puede contener la URL o datos de la conexión. Sin excepción
            // interna, para que nada del transporte viaje en la excepción tipada.
            _logger.LogError(
                "Fallo de comunicación con el proveedor de embeddings tras {Intentos} intento(s): {ExceptionType}.",
                intentos.Intentos, ex.GetType().Name);
            throw new EmbeddingProviderException(MotivoFalloEmbedding.NoDisponible, null, intentos.Intentos);
        }
        catch (Exception ex)
        {
            // Cualquier otra excepción es un defecto interno (error de programación, estado inválido…), no un fallo
            // del proveedor: NO se clasifica como transitoria ni se envuelve, para no ocultarla ni provocar
            // reintentos injustificados. Se registra solo el tipo y se propaga sin cambios.
            _logger.LogError(
                "Error interno inesperado en el proveedor de embeddings: {ExceptionType}. No se clasifica como fallo del proveedor.",
                ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Errores de red o de E/S del transporte HTTP. Son los únicos que se traducen a <see cref="MotivoFalloEmbedding.NoDisponible"/>;
    /// el manejador de resiliencia solo reintenta <see cref="HttpRequestException"/> (además de 429 y 503).
    /// </summary>
    private static bool EsFalloDeTransporte(Exception ex) =>
        ex is HttpRequestException or IOException or System.Net.Sockets.SocketException;

    private async Task<EmbeddingBatchResult> EmbedCoreAsync(IReadOnlyList<string> entradas, ContadorDeIntentos intentos, CancellationToken token)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + "/embeddings")
        {
            // Cuerpo exacto: model, input y encoding_format. Nada más.
            Content = new StringContent(
                JsonSerializer.Serialize(new { model = _options.ModelId, input = entradas, encoding_format = "float" }),
                Encoding.UTF8, "application/json")
        };
        peticion.Options.Set(ClaveIntentos, intentos);
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var respuesta = await _httpClient.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, token);
        var estado = (int)respuesta.StatusCode;
        if (!respuesta.IsSuccessStatusCode)
        {
            var motivo = MotivoPorEstado(respuesta.StatusCode);
            // Solo el código de estado: el cuerpo del proveedor puede reflejar los textos enviados.
            _logger.LogError(
                "El proveedor de embeddings respondió con estado {StatusCode} tras {Intentos} intento(s). Motivo: {Motivo}.",
                estado, intentos.Intentos, motivo);
            throw new EmbeddingProviderException(motivo, estado, intentos.Intentos, EsperaSugerida(respuesta));
        }

        var cuerpo = await LeerCuerpoAsync(respuesta, intentos, token);
        return Interpretar(cuerpo, entradas.Count, intentos);
    }

    private static MotivoFalloEmbedding MotivoPorEstado(HttpStatusCode estado) => (int)estado switch
    {
        429 => MotivoFalloEmbedding.LimiteDeTasa,
        503 => MotivoFalloEmbedding.NoDisponible,
        >= 500 => MotivoFalloEmbedding.ErrorDelServidor,
        401 or 403 => MotivoFalloEmbedding.Autenticacion,
        // Resto de 4xx y cualquier 1xx/3xx inesperado (las redirecciones no se siguen).
        _ => MotivoFalloEmbedding.SolicitudRechazada
    };

    private static TimeSpan? EsperaSugerida(HttpResponseMessage respuesta)
    {
        if (respuesta.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            || respuesta.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        var espera = retryAfter.Delta ?? (retryAfter.Date is { } fecha ? fecha - DateTimeOffset.UtcNow : null);
        return espera is { } valor && valor > TimeSpan.Zero ? valor : null;
    }

    /// <summary>Lee el cuerpo con un tope de 16 MiB; por encima, la respuesta es inválida (nunca se procesa a medias).</summary>
    private async Task<byte[]> LeerCuerpoAsync(HttpResponseMessage respuesta, ContadorDeIntentos intentos, CancellationToken token)
    {
        if (respuesta.Content.Headers.ContentLength > EmbeddingOptions.MaxBytesRespuesta)
        {
            throw RespuestaInvalida("tamaño", intentos, (int)respuesta.StatusCode);
        }

        await using var flujo = await respuesta.Content.ReadAsStreamAsync(token);
        using var memoria = new MemoryStream();
        var buffer = new byte[81920];
        int leidos;
        while ((leidos = await flujo.ReadAsync(buffer, token)) > 0)
        {
            if (memoria.Length + leidos > EmbeddingOptions.MaxBytesRespuesta)
            {
                throw RespuestaInvalida("tamaño", intentos, (int)respuesta.StatusCode);
            }

            memoria.Write(buffer, 0, leidos);
        }

        return memoria.ToArray();
    }

    private EmbeddingBatchResult Interpretar(byte[] cuerpo, int esperados, ContadorDeIntentos intentos)
    {
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(cuerpo);
        }
        catch (JsonException)
        {
            throw RespuestaInvalida("json", intentos);
        }

        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object
                || !raiz.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw RespuestaInvalida("estructura", intentos);
            }

            var modeloDeclarado = ModeloDeclarado(raiz, intentos);

            if (data.GetArrayLength() != esperados)
            {
                throw RespuestaInvalida("cardinalidad", intentos);
            }

            var vectores = new float[esperados][];
            foreach (var elemento in data.EnumerateArray())
            {
                if (elemento.ValueKind != JsonValueKind.Object
                    || !elemento.TryGetProperty("index", out var indiceJson)
                    || indiceJson.ValueKind != JsonValueKind.Number || !indiceJson.TryGetInt32(out var indice)
                    || indice < 0 || indice >= esperados || vectores[indice] != null)
                {
                    // Índice ausente, no entero, fuera de rango o repetido: no se reordena una respuesta inconsistente.
                    throw RespuestaInvalida("indices", intentos);
                }

                if (!elemento.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
                {
                    throw RespuestaInvalida("estructura", intentos);
                }

                vectores[indice] = Vector(embedding, intentos);
            }

            return new EmbeddingBatchResult(vectores, TokensInformados(raiz), _options.ModelId, modeloDeclarado, Proveedor, _options.Dimensiones);
        }
    }

    /// <summary>
    /// Modelo declarado por el proveedor: null si no viene (sin campo, null o vacío). Si viene y no es EXACTAMENTE el
    /// solicitado (comparación ordinal, sin alias ni equivalencias), la respuesta se rechaza.
    /// </summary>
    private string? ModeloDeclarado(JsonElement raiz, ContadorDeIntentos intentos)
    {
        if (!raiz.TryGetProperty("model", out var modelo) || modelo.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (modelo.ValueKind != JsonValueKind.String)
        {
            throw RespuestaInvalida("estructura", intentos);
        }

        var declarado = modelo.GetString();
        if (string.IsNullOrEmpty(declarado))
        {
            return null;
        }

        if (!string.Equals(declarado, _options.ModelId, StringComparison.Ordinal))
        {
            _logger.LogError(
                "El proveedor de embeddings declaró un modelo distinto del solicitado ({ModeloSolicitado}); respuesta rechazada.",
                _options.ModelId);
            throw new EmbeddingProviderException(MotivoFalloEmbedding.ModeloInesperado, null, intentos.Intentos);
        }

        return declarado;
    }

    private float[] Vector(JsonElement embedding, ContadorDeIntentos intentos)
    {
        var dimension = embedding.GetArrayLength();
        if (dimension != _options.Dimensiones)
        {
            _logger.LogError(
                "El proveedor de embeddings devolvió un vector de dimensión {DimensionRecibida}; se esperaba {DimensionEsperada}. Respuesta rechazada.",
                dimension, _options.Dimensiones);
            throw new EmbeddingProviderException(MotivoFalloEmbedding.DimensionInvalida, null, intentos.Intentos);
        }

        var vector = new float[dimension];
        double sumaCuadrados = 0;
        var i = 0;
        foreach (var valorJson in embedding.EnumerateArray())
        {
            if (valorJson.ValueKind != JsonValueKind.Number || !valorJson.TryGetDouble(out var valor))
            {
                throw RespuestaInvalida("valores", intentos);
            }

            var comoFloat = (float)valor;
            if (!float.IsFinite(comoFloat))
            {
                throw RespuestaInvalida("valores", intentos);
            }

            vector[i++] = comoFloat;
            sumaCuadrados += (double)comoFloat * comoFloat;
        }

        if (!(sumaCuadrados > 0))
        {
            throw RespuestaInvalida("norma", intentos);
        }

        return vector;
    }

    /// <summary>Solo el consumo que informa el proveedor; null si no lo informa. Nunca una estimación.</summary>
    private int? TokensInformados(JsonElement raiz)
    {
        if (raiz.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("prompt_tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number
            && tokens.TryGetInt32(out var valor) && valor >= 0)
        {
            return valor;
        }

        _logger.LogWarning("El proveedor de embeddings no informó el consumo de tokens del lote.");
        return null;
    }

    private EmbeddingProviderException RespuestaInvalida(string aspecto, ContadorDeIntentos intentos, int? estado = null)
    {
        _logger.LogError("Respuesta inválida del proveedor de embeddings ({Aspecto}); rechazada sin devolver vectores.", aspecto);
        return new EmbeddingProviderException(MotivoFalloEmbedding.RespuestaInvalida, estado, intentos.Intentos);
    }

    /// <summary>
    /// Contador compartido por todos los intentos de una misma llamada. Distingue los envíos HTTP reales del intento
    /// lógico que expone <see cref="IFalloProveedorEmbeddings.Intentos"/>.
    /// </summary>
    internal sealed class ContadorDeIntentos
    {
        private int _envios;

        /// <summary>Peticiones HTTP efectivamente enviadas (0 si el fallo llegó antes del primer envío).</summary>
        public int Envios => Volatile.Read(ref _envios);

        /// <summary>
        /// Intento lógico, siempre entre 1 y 3: la llamada es en sí el primer intento, aunque el timeout venza antes de
        /// que salga la petición; cada reintento suma uno, hasta el máximo contractual.
        /// </summary>
        public int Intentos => Math.Clamp(Envios, ClasificacionFalloEmbedding.IntentosMinimos, ClasificacionFalloEmbedding.IntentosMaximos);

        public void Incrementar() => Interlocked.Increment(ref _envios);
    }

    /// <summary>
    /// Manejador interno al de resiliencia: cuenta cada envío real (intento inicial y reintentos) para informar
    /// <see cref="IFalloProveedorEmbeddings.Intentos"/>. No altera la petición ni la respuesta.
    /// </summary>
    internal sealed class ContadorDeIntentosHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Options.TryGetValue(ClaveIntentos, out var contador))
            {
                contador.Incrementar();
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
