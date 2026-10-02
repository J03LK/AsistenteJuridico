namespace AsistenteJuridico.API.Configuration;

/// <summary>
/// Cuotas de la política "AIRateLimit" (contrato Fase 6). Ambas coexisten: una solicitud de IA debe
/// respetar a la vez la cuota del usuario autenticado y la cuota global de su tenant.
/// La identidad se toma exclusivamente del JWT (sub y tenant_id); la IP nunca se usa como identidad.
/// </summary>
public class AIRateLimitOptions
{
    public const string SectionName = "RateLimiting:AI";

    /// <summary>Solicitudes de IA permitidas por usuario en cada ventana (contrato: 15/minuto).</summary>
    public int UserPermitLimit { get; set; } = 15;

    /// <summary>Solicitudes de IA permitidas por tenant en cada ventana, sumando todos sus usuarios (contrato: 60/minuto).</summary>
    public int TenantPermitLimit { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;
}
