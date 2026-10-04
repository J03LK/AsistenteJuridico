namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Fase 6.X (DA-3) — Configuración de la extracción de texto (sección <c>AI:Extraction</c>).
/// </summary>
public class DocumentTextExtractionOptions
{
    public const string SectionName = "AI:Extraction";

    /// <summary>Tiempo máximo de la extracción de un documento (lectura más análisis). Timeout explícito: 30 s.</summary>
    public double TimeoutSeconds { get; set; } = 30;
}
