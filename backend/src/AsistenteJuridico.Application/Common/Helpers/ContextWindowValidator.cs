using AsistenteJuridico.Application.Common.Exceptions;

namespace AsistenteJuridico.Application.Common.Helpers;

/// <summary>
/// Validador estricto de ventanas de contexto y longitudes documentales.
/// Prohíbe terminantemente el truncamiento silencioso y la eliminación no auditada de contexto.
/// </summary>
public static class ContextWindowValidator
{
    public const int MaxDocumentCharacters = 30000;
    public const int MaxUserInputCharacters = 4000;

    /// <summary>
    /// Valida que la entrada del usuario no sobrepase el límite máximo de 4.000 caracteres.
    /// Lanza UserInputLimitExceededException (HTTP 422) si se supera el umbral sin truncamiento silencioso.
    /// </summary>
    public static void ValidateUserInputLength(string? content, int maxChars = MaxUserInputCharacters)
    {
        if (content != null && content.Length > maxChars)
        {
            throw new UserInputLimitExceededException(content.Length, maxChars);
        }
    }

    /// <summary>
    /// Valida que el texto del documento no sobrepase el límite máximo de 30.000 caracteres.
    /// Lanza DocumentContextExceededException (HTTP 422) si se supera el umbral.
    /// </summary>
    public static void ValidateDocumentLength(string? content, int maxChars = MaxDocumentCharacters)
    {
        if (content != null && content.Length > maxChars)
        {
            throw new DocumentContextExceededException(content.Length, maxChars);
        }
    }

    /// <summary>
    /// Valida que la estimación previa de tokens requeridos no exceda la capacidad máxima del modelo/proveedor.
    /// Lanza AIContextWindowExceededException (HTTP 422) si la ventana es insuficiente.
    /// </summary>
    public static void ValidateContextTokens(int totalEstimatedTokens, int maxContextTokens)
    {
        if (totalEstimatedTokens > maxContextTokens)
        {
            throw new AIContextWindowExceededException(totalEstimatedTokens, maxContextTokens);
        }
    }
}
