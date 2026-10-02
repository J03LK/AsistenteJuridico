namespace AsistenteJuridico.Infrastructure.Services.AI;

public class OpenAIOptions
{
    public const string SectionName = "AI:OpenAICompatible";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gpt-4o-mini";
    public int MaxContextTokens { get; set; } = 128000;
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Tiempo máximo por petición al proveedor (incluye reintentos). Contrato v1.1.1: 60 segundos.
    /// En streaming se aplica a la obtención de la respuesta y como tiempo máximo de inactividad entre fragmentos.
    /// </summary>
    public double TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Reintentos ante errores transitorios de red o códigos 429/503 del proveedor (v1.1.1 §18).
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Retardo base del backoff exponencial con jitter entre reintentos.
    /// </summary>
    public double RetryBaseDelaySeconds { get; set; } = 1.5;
}
