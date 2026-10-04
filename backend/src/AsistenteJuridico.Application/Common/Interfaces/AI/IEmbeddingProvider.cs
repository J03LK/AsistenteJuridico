using System.Globalization;
using AsistenteJuridico.Application.Common.Indexacion;

namespace AsistenteJuridico.Application.Common.Interfaces.AI;

/// <summary>
/// Fase 8.3 — Finalidad del texto. Existe para los modelos que exigen prefijos distintos para consulta y pasaje
/// (E5, BGE). El proveedor compatible con OpenAI y el simulado lo ignoran.
/// </summary>
public enum EmbeddingPurpose
{
    Documento,
    Consulta
}

/// <summary>
/// Fase 8.3 — Resultado de un lote (FASE_8_CONTRATO.md §9 con la adenda A-8.3-1, FASE_8_3_CONTRATO.md §6.3).
/// </summary>
/// <param name="Vectores">Un vector por entrada, en el mismo orden; cada uno con exactamente <paramref name="Dimensiones"/> valores finitos y norma mayor que 0.</param>
/// <param name="TokensEntrada">Consumo INFORMADO por el proveedor. <c>null</c> = no informado (nunca una estimación); 0 conserva su significado numérico.</param>
/// <param name="ModelId">Modelo efectivo: el solicitado, que coincide con el declarado por el proveedor o no fue declarado.</param>
/// <param name="ModeloDeclarado">El modelo que declaró el proveedor en la respuesta, tal cual; <c>null</c> si no lo declaró.</param>
/// <param name="ProviderId">Identificador del proveedor.</param>
/// <param name="Dimensiones">Dimensión de todos los vectores.</param>
public sealed record EmbeddingBatchResult(
    IReadOnlyList<float[]> Vectores,
    int? TokensEntrada,
    string ModelId,
    string? ModeloDeclarado,
    string ProviderId,
    int Dimensiones);

/// <summary>
/// Fase 8.3 — Contrato agnóstico del proveedor de embeddings (FASE_8_CONTRATO.md §9). No conoce HTTP, OpenAI ni
/// pgvector. Las implementaciones no persisten nada ni registran consumo: solo convierten textos en vectores.
/// </summary>
public interface IEmbeddingProvider
{
    string ProviderId { get; }
    string ModelId { get; }
    int Dimensiones { get; }
    int MaxTokensPorEntrada { get; }
    int MaxEntradasPorLote { get; }

    /// <summary>
    /// Convierte de 1 a <see cref="MaxEntradasPorLote"/> textos en vectores, todo o nada.
    /// Lote inválido: ArgumentException / ArgumentNullException, sin llamada de red.
    /// Fallo del proveedor: EmbeddingProviderException (AI_PROVIDER_ERROR) o EmbeddingProviderTimeoutException
    /// (AI_PROVIDER_TIMEOUT), ambas con la clasificación de IFalloProveedorEmbeddings.
    /// La cancelación del llamador se propaga como OperationCanceledException. Nunca devuelve vectores parciales,
    /// rellenados ni inventados.
    /// </summary>
    Task<EmbeddingBatchResult> EmbedAsync(IReadOnlyList<string> entradas, EmbeddingPurpose proposito, CancellationToken ct);
}

/// <summary>
/// Fase 8.3 — Parte de embedding del perfil de indexación (FASE_8_CONTRATO.md §3.4): "{proveedor}:{modelo}@{dimensiones}".
/// Helper sin estado: la interfaz del contrato rector no cambia.
/// </summary>
public static class FirmaEmbedding
{
    public static string De(IEmbeddingProvider proveedor)
    {
        ArgumentNullException.ThrowIfNull(proveedor);
        return string.Create(CultureInfo.InvariantCulture, $"{proveedor.ProviderId}:{proveedor.ModelId}@{proveedor.Dimensiones}");
    }
}

/// <summary>
/// Fase 8.3 — Validación de un lote de entrada, común a todos los proveedores y previa a cualquier llamada de red
/// (FASE_8_3_CONTRATO.md §6.1). Un lote inválido es un error de programación del llamador, no un fallo del proveedor.
/// </summary>
public static class LoteEmbeddings
{
    /// <summary>
    /// Máximo contractual ABSOLUTO de textos por lote (FASE_8_CONTRATO.md §3.2.2). Ninguna configuración ni proveedor
    /// puede elevarlo: <see cref="Validar"/> lo aplica siempre, además del límite propio del proveedor.
    /// </summary>
    public const int MaxEntradasPorLoteContractual = 64;

    /// <summary>Límite efectivo de un proveedor: el configurado, acotado al rango contractual de 1 a 64.</summary>
    public static int LimiteEfectivo(int maxEntradasPorLote) => Math.Clamp(maxEntradasPorLote, 1, MaxEntradasPorLoteContractual);

    /// <summary>Tokens estimados de un texto con la convención del proyecto: max(1, ceil(longitud / 4)).</summary>
    public static int TokensEstimados(string texto) => Math.Max(1, (texto.Length + 3) / 4);

    public static void Validar(IReadOnlyList<string> entradas, int maxEntradasPorLote, int maxTokensPorEntrada)
    {
        ArgumentNullException.ThrowIfNull(entradas);
        if (entradas.Count == 0)
        {
            throw new ArgumentException("El lote de embeddings no puede estar vacío.", nameof(entradas));
        }

        if (entradas.Count > LimiteEfectivo(maxEntradasPorLote))
        {
            throw new ArgumentException("El lote de embeddings supera el número máximo de entradas por lote.", nameof(entradas));
        }

        for (var i = 0; i < entradas.Count; i++)
        {
            var texto = entradas[i];
            if (texto is null)
            {
                throw new ArgumentNullException(nameof(entradas), "El lote de embeddings contiene una entrada nula.");
            }

            if (string.IsNullOrWhiteSpace(texto))
            {
                throw new ArgumentException("El lote de embeddings contiene una entrada vacía.", nameof(entradas));
            }

            if (!TextoNormalizador.EsUtf16BienFormado(texto))
            {
                throw new ArgumentException("El lote de embeddings contiene texto UTF-16 mal formado.", nameof(entradas));
            }

            if (TokensEstimados(texto) > maxTokensPorEntrada)
            {
                throw new ArgumentException("Una entrada del lote de embeddings supera el máximo de tokens por entrada.", nameof(entradas));
            }
        }
    }
}
