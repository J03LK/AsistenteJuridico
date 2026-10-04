using AsistenteJuridico.Domain.Common;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Fase 8 — Fragmento de un documento con su embedding (contrato §6.2). Hereda el perfil de su índice; se borra
/// en cascada con él. El dominio no conoce pgvector: el embedding es un float[] que Infrastructure guarda como
/// vector(1536), y PostgreSQL rechaza cualquier otra dimensión.
/// </summary>
public class DocumentoFragmento : BaseEntity, IMultiTenant
{
    public Guid TenantId { get; set; }
    public Guid DocumentoId { get; set; }
    public Guid ExpedienteId { get; set; }

    public Guid IndiceId { get; set; }
    public DocumentoIndice Indice { get; set; } = null!;

    public int Orden { get; set; }

    /// <summary>Texto normalizado del fragmento.</summary>
    public string Texto { get; set; } = string.Empty;

    /// <summary>jsonb: { "tipo": "paginas|parrafos|hoja|lineas", "desde": n, "hasta": m, "etiqueta": "…" }.</summary>
    public string Ubicacion { get; set; } = "{}";

    public string? RutaSeccion { get; set; }

    public int CaracterInicio { get; set; }
    public int CaracterFin { get; set; }
    public int TokensEstimados { get; set; }

    /// <summary>SHA-256 del texto normalizado (deduplicación y verificación de citas).</summary>
    public string HashFragmento { get; set; } = string.Empty;

    public float[] Embedding { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
