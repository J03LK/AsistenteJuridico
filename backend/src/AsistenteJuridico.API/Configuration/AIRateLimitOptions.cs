namespace AsistenteJuridico.API.Configuration;

/// <summary>
/// Cuota de la política "AIRateLimit" aplicada a los endpoints de IA, por usuario autenticado.
/// </summary>
public class AIRateLimitOptions
{
    public const string SectionName = "RateLimiting:AI";

    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
}
