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
}
