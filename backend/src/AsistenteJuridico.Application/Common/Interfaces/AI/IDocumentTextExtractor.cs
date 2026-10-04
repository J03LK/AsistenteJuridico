namespace AsistenteJuridico.Application.Common.Interfaces.AI;

/// <summary>
/// Fase 6.X (H10) — Resultado tipado de la extracción de texto de un documento. La cancelación solicitada por el
/// llamador NO es un estado: la <see cref="OperationCanceledException"/> se propaga sin alterar.
/// </summary>
public enum ExtractionStatus
{
    Success,
    Empty,
    FileNotFound,
    Forbidden,
    UnsupportedFormat,
    InvalidContent,
    ExtractionFailed,
    ContextExceeded
}

/// <summary>
/// Resultado de la extracción. <see cref="Text"/> solo tiene valor con <see cref="ExtractionStatus.Success"/>:
/// nunca hay texto de respaldo ni contenido inventado.
/// </summary>
public sealed record ExtractionResult(Guid DocumentoId, ExtractionStatus Status, string? Text = null)
{
    public bool IsSuccess => Status == ExtractionStatus.Success;
}

/// <summary>
/// Fase 8.2 — Límites de una extracción (contrato 8.2 §5). <c>Chat</c> reproduce los valores de la 6.X;
/// <c>Indexacion</c> usa las constantes de <c>ext-v1</c> fijadas en FASE_8_CONTRATO.md §15, sin configuración.
/// </summary>
public sealed record ExtractionProfile
{
    public const string VersionIndexacion = "ext-v1";
    public const int MaxCaracteresIndexacion = 3_000_000;
    public const int TimeoutSecondsIndexacion = 120;

    public string Nombre { get; }
    public int MaxCaracteres { get; }
    public int TimeoutSeconds { get; }

    public ExtractionProfile(string nombre, int maxCaracteres, int timeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombre);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCaracteres, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);
        Nombre = nombre;
        MaxCaracteres = maxCaracteres;
        TimeoutSeconds = timeoutSeconds;
    }

    public static ExtractionProfile Indexacion { get; } = new("Indexacion", MaxCaracteresIndexacion, TimeoutSecondsIndexacion);

    /// <summary>Perfil Chat con el timeout ya resuelto de AI:Extraction:TimeoutSeconds (≤ 0 → 30, como en la 6.X).</summary>
    public static ExtractionProfile Chat(int timeoutSeconds) =>
        new("Chat", Helpers.ContextWindowValidator.MaxDocumentCharacters, timeoutSeconds > 0 ? timeoutSeconds : 30);
}

/// <summary>Fase 8.2 — Ubicación de un segmento o fragmento. Tipo ∈ { paginas, parrafos, hoja, lineas }.</summary>
public sealed record UbicacionSegmento(string Tipo, int Desde, int Hasta, string Etiqueta)
{
    public const string Paginas = "paginas";
    public const string Parrafos = "parrafos";
    public const string Hoja = "hoja";
    public const string Lineas = "lineas";
}

/// <summary>Fase 8.2 — Segmento crudo (sin normalizar) en orden de documento. RutaSeccion solo en DOCX.</summary>
public sealed record TextSegment(string Texto, UbicacionSegmento Ubicacion, string? RutaSeccion);

/// <summary>
/// Fase 8.2 — Resultado de la extracción segmentada. <see cref="Segmentos"/> solo tiene elementos con
/// <see cref="ExtractionStatus.Success"/>; <see cref="HashSha256Archivo"/> (SHA-256 hexadecimal en minúsculas de los
/// mismos bytes analizados) solo con Success.
/// </summary>
public sealed record SegmentedExtractionResult(
    Guid DocumentoId, ExtractionStatus Status, IReadOnlyList<TextSegment> Segmentos, string? HashSha256Archivo)
{
    public bool IsSuccess => Status == ExtractionStatus.Success;
}

/// <summary>
/// Extrae el texto real de un documento almacenado (TXT, PDF, DOCX y XLSX). El formato se decide por el
/// ContentType canónico guardado en la Fase 7, nunca por lo que declare el cliente.
/// </summary>
public interface IDocumentTextExtractor
{
    /// <summary>Indica si el ContentType canónico tiene extracción de texto.</summary>
    bool IsSupported(string? contentType);

    Task<ExtractionResult> ExtractAsync(
        Guid documentoId,
        string? rutaAlmacenamiento,
        string? contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fase 8.2 — Extracción segmentada con ubicación y el límite y el timeout del perfil. Mismas defensas y estados
    /// que <see cref="ExtractAsync"/>; la cancelación del llamador se propaga como OperationCanceledException.
    /// No persiste nada.
    /// </summary>
    Task<SegmentedExtractionResult> ExtractSegmentsAsync(
        Guid documentoId,
        string? rutaAlmacenamiento,
        string? contentType,
        ExtractionProfile perfil,
        CancellationToken cancellationToken = default);
}
