namespace AsistenteJuridico.Infrastructure.BackgroundServices;

/// <summary>
/// Fase 6.X (X1, DA-3) — Recuperación de documentos atascados en Procesando (sección <c>AI:ProcessingRecovery</c>).
/// </summary>
public class ProcesamientoIaRecoveryOptions
{
    public const string SectionName = "AI:ProcessingRecovery";

    /// <summary>
    /// Lease: 300 s. Margen operacional deliberadamente superior a los timeouts explícitos de extracción (30 s) y
    /// proveedor (60 s); NO es un máximo garantizado de SaveChanges. Si una operación legítima lo superara, la protege
    /// xmin (409 y resultado descartado).
    /// </summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Intervalo entre ciclos del worker.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Documentos recuperados como máximo por tenant y ciclo.</summary>
    public int BatchSize { get; set; } = 100;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Único mínimo demostrable del contrato: el lease debe superar la suma de los timeouts explícitos.
    /// </summary>
    public static bool LeaseEsValido(int leaseSeconds, double providerTimeoutSeconds, double extractionTimeoutSeconds) =>
        leaseSeconds > providerTimeoutSeconds + extractionTimeoutSeconds;
}
