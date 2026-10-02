namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Registra y controla los contadores secuenciales independientes para cada estudio jurídico y año calendario.
/// Utilizado para generar identificadores con numeración monotónicamente creciente sin duplicados (ej. EXP-2026-0001).
/// </summary>
public class TenantSecuencia
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public string TipoSecuencia { get; set; } = "EXPEDIENTE";
    public int Anio { get; set; }
    public int UltimoValor { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
