namespace AsistenteJuridico.Application.Common.Exceptions;

/// <summary>
/// Fase 6.X (DA-12) — Códigos de error de los flujos de IA sobre documentos. Se declaran aparte de
/// <see cref="DocumentoErrorCodes"/> (contrato cerrado de la Fase 7).
/// </summary>
public static class AIDocumentErrorCodes
{
    /// <summary>400: algún DocumentoIds de resumir-expediente no es válido para el expediente.</summary>
    public const string DocumentsInvalid = "DOCUMENT_DOCUMENTS_INVALID";

    /// <summary>422: formato soportado sin texto (PDF escaneado, archivo en blanco).</summary>
    public const string TextEmpty = "DOCUMENT_TEXT_EMPTY";

    /// <summary>422: formato sin extracción de texto (DOC, XLS, JPG, PNG).</summary>
    public const string TextUnsupported = "DOCUMENT_TEXT_UNSUPPORTED";

    /// <summary>422: documento dañado, cifrado o con codificación inválida.</summary>
    public const string TextInvalid = "DOCUMENT_TEXT_INVALID";

    /// <summary>422: timeout de extracción o error inesperado del parser o de E/S.</summary>
    public const string TextExtractionFailed = "DOCUMENT_TEXT_EXTRACTION_FAILED";
}
