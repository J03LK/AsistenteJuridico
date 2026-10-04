using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Domain.Entities;

/// <summary>
/// Fase 8 — Estado de indexación semántica de un documento en un perfil de indexación (contrato §6.1).
/// Como mucho un índice vigente (Indexado) y una construcción en curso (Pendiente o Procesando) por documento y
/// perfil: la reindexación construye una fila nueva mientras la vigente sigue sirviendo.
/// </summary>
public class DocumentoIndice : BaseEntity, IMultiTenant
{
    /// <summary>Dimensión del único perfil admitido hoy (columna vector(1536), §3.2.1).</summary>
    public const int DimensionesPerfilInicial = 1536;

    public Guid TenantId { get; set; }

    public Guid DocumentoId { get; set; }
    public Documento Documento { get; set; } = null!;

    // Desnormalizado para filtrar por expediente (FK compuesta tenant-aware).
    public Guid ExpedienteId { get; set; }
    public Expediente Expediente { get; set; } = null!;

    /// <summary>Perfil determinista: "{proveedor}:{modelo}@{dimensiones}|chunk-vN|ext-vN|norm-vN" (§3.4).</summary>
    public string Perfil { get; set; } = string.Empty;

    public int Dimensiones { get; set; } = DimensionesPerfilInicial;

    public EstadoIndexacion Estado { get; set; } = EstadoIndexacion.Pendiente;

    /// <summary>SHA-256 del archivo; NULL hasta conocerlo en los documentos históricos sin hash.</summary>
    public string? HashContenido { get; set; }

    // Lease con latido (§7, §8).
    public DateTime? ProcesandoDesde { get; set; }
    public string? ProcesadoPor { get; set; }

    public int Intentos { get; set; }
    public DateTime? ProximoIntentoEn { get; set; }

    /// <summary>Solo el código (p. ej. DOCUMENT_TEXT_INVALID, DOCUMENT_INDEX_TOO_LARGE), nunca el mensaje.</summary>
    public string? CodigoError { get; set; }

    public int Fragmentos { get; set; }
    public int TokensTotales { get; set; }

    // Límite de fragmentos (§7.1).
    public int? FragmentosCalculados { get; set; }
    public int? LimiteAplicado { get; set; }

    public DateTime? IndexadoEn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>xmin de PostgreSQL: concurrencia optimista del lease y de la confirmación.</summary>
    public uint Version { get; set; }
}
