namespace AsistenteJuridico.Application.Common.Exceptions;

/// <summary>
/// Fase 8.3 — Motivo interno de un fallo del proveedor de embeddings (FASE_8_3_CONTRATO.md §9). No es un código
/// público: los códigos siguen siendo AI_PROVIDER_ERROR y AI_PROVIDER_TIMEOUT.
/// </summary>
public enum MotivoFalloEmbedding
{
    // Transitorios
    Timeout,
    LimiteDeTasa,
    NoDisponible,
    ErrorDelServidor,

    // Permanentes
    Autenticacion,
    SolicitudRechazada,
    RespuestaInvalida,
    DimensionInvalida,
    ModeloInesperado
}

/// <summary>
/// Fase 8.3 — Clasificación común a todo fallo del proveedor de embeddings. El proveedor solo informa: qué hacer
/// con cada motivo (reintentar más tarde, marcar un índice…) lo decide el consumidor (Fase 8.4).
/// </summary>
public interface IFalloProveedorEmbeddings
{
    MotivoFalloEmbedding Motivo { get; }

    /// <summary>Derivado solo de <see cref="Motivo"/> (tabla fija); no se puede asignar.</summary>
    bool EsTransitorio { get; }

    /// <summary>Último código HTTP recibido; null si no hubo respuesta (red, timeout) o si el fallo es de validación de un 2xx.</summary>
    int? CodigoEstadoHttp { get; }

    /// <summary>
    /// Intentos de la llamada: siempre entre 1 y 3. La llamada es en sí el primer intento, aunque el fallo llegue antes
    /// de que la petición HTTP salga; cada reintento suma uno, hasta el máximo de 2 reintentos.
    /// </summary>
    int Intentos { get; }

    /// <summary>Retry-After del último 429 o 503, si vino y es válido.</summary>
    TimeSpan? EsperaSugerida { get; }
}

/// <summary>Tabla fija Motivo → EsTransitorio (FASE_8_3_CONTRATO.md §9).</summary>
public static class ClasificacionFalloEmbedding
{
    /// <summary>Rango contractual de <see cref="IFalloProveedorEmbeddings.Intentos"/>: el intento inicial y como máximo 2 reintentos.</summary>
    public const int IntentosMinimos = 1;
    public const int IntentosMaximos = 3;

    internal static int ValidarIntentos(int intentos)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intentos, IntentosMinimos);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(intentos, IntentosMaximos);
        return intentos;
    }

    public static bool EsTransitorio(MotivoFalloEmbedding motivo) => motivo
        is MotivoFalloEmbedding.Timeout
        or MotivoFalloEmbedding.LimiteDeTasa
        or MotivoFalloEmbedding.NoDisponible
        or MotivoFalloEmbedding.ErrorDelServidor;
}

/// <summary>
/// Fase 8.3 — Fallo del proveedor de embeddings. Conserva el código público AI_PROVIDER_ERROR (HTTP 502) y el mensaje
/// fijo de <see cref="AIProviderException"/>: nunca incluye la clave, los textos, el cuerpo de la respuesta, la URL
/// ni los vectores, y no lleva excepción interna.
/// </summary>
public sealed class EmbeddingProviderException : AIProviderException, IFalloProveedorEmbeddings
{
    public EmbeddingProviderException(MotivoFalloEmbedding motivo, int? codigoEstadoHttp = null, int intentos = ClasificacionFalloEmbedding.IntentosMinimos, TimeSpan? esperaSugerida = null)
    {
        if (motivo == MotivoFalloEmbedding.Timeout)
        {
            throw new ArgumentException("El timeout se representa con EmbeddingProviderTimeoutException.", nameof(motivo));
        }

        Motivo = motivo;
        CodigoEstadoHttp = codigoEstadoHttp;
        Intentos = ClasificacionFalloEmbedding.ValidarIntentos(intentos);
        EsperaSugerida = esperaSugerida;
    }

    public MotivoFalloEmbedding Motivo { get; }
    public bool EsTransitorio => ClasificacionFalloEmbedding.EsTransitorio(Motivo);
    public int? CodigoEstadoHttp { get; }
    public int Intentos { get; }
    public TimeSpan? EsperaSugerida { get; }
}

/// <summary>
/// Fase 8.3 — El proveedor de embeddings no respondió dentro del límite por lote (que incluye los reintentos).
/// Conserva el código público AI_PROVIDER_TIMEOUT. Siempre transitorio.
/// </summary>
public sealed class EmbeddingProviderTimeoutException : AIProviderTimeoutException, IFalloProveedorEmbeddings
{
    public EmbeddingProviderTimeoutException(int intentos = ClasificacionFalloEmbedding.IntentosMinimos, int? codigoEstadoHttp = null, TimeSpan? esperaSugerida = null)
    {
        Intentos = ClasificacionFalloEmbedding.ValidarIntentos(intentos);
        CodigoEstadoHttp = codigoEstadoHttp;
        EsperaSugerida = esperaSugerida;
    }

    public MotivoFalloEmbedding Motivo => MotivoFalloEmbedding.Timeout;
    public bool EsTransitorio => true;
    public int? CodigoEstadoHttp { get; }
    public int Intentos { get; }
    public TimeSpan? EsperaSugerida { get; }
}
