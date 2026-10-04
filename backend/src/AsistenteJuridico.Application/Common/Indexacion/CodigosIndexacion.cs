using AsistenteJuridico.Application.Common.Interfaces.AI;

namespace AsistenteJuridico.Application.Common.Indexacion;

/// <summary>
/// Fase 8.4 — Catálogo CERRADO de códigos de la indexación semántica (FASE_8_4_CONTRATO.md §13.2). Son los únicos
/// valores que la indexación puede escribir en <c>documento_indices.CodigoError</c> y en <c>AIUsageLog.CodigoError</c>.
/// Códigos internos del índice: la Fase 8.4 no los expone por HTTP.
/// </summary>
public static class CodigosIndexacion
{
    public const string ProveedorTimeout = "AI_PROVIDER_TIMEOUT";
    public const string ProveedorError = "AI_PROVIDER_ERROR";
    public const string ExtraccionFallida = "DOCUMENT_TEXT_EXTRACTION_FAILED";
    public const string TextoNoSoportado = "DOCUMENT_TEXT_UNSUPPORTED";
    public const string TextoVacio = "DOCUMENT_TEXT_EMPTY";
    public const string TextoInvalido = "DOCUMENT_TEXT_INVALID";
    public const string ArchivoNoEncontrado = "DOCUMENT_FILE_NOT_FOUND";
    public const string IndiceDemasiadoGrande = "DOCUMENT_INDEX_TOO_LARGE";
    public const string LeaseVencido = "LEASE_EXPIRED";
    public const string ErrorInterno = "INDEX_INTERNAL_ERROR";

    public static IReadOnlySet<string> Catalogo { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProveedorTimeout, ProveedorError, ExtraccionFallida, TextoNoSoportado, TextoVacio, TextoInvalido,
        ArchivoNoEncontrado, IndiceDemasiadoGrande, LeaseVencido, ErrorInterno
    };

    /// <summary>Código de un estado de extracción distinto de Success (§14); null para Success.</summary>
    public static string? DeExtraccion(ExtractionStatus estado) => estado switch
    {
        ExtractionStatus.Success => null,
        ExtractionStatus.UnsupportedFormat => TextoNoSoportado,
        ExtractionStatus.Empty => TextoVacio,
        ExtractionStatus.InvalidContent => TextoInvalido,
        ExtractionStatus.FileNotFound => ArchivoNoEncontrado,
        ExtractionStatus.Forbidden => ErrorInterno,
        ExtractionStatus.ContextExceeded => IndiceDemasiadoGrande,
        ExtractionStatus.ExtractionFailed => ExtraccionFallida,
        _ => ErrorInterno
    };
}

/// <summary>
/// Fase 8.4 — Lista cerrada del campo <c>motivo</c> de la auditoría DOCUMENT_INDEX_FAILED (§13.3), además de los
/// nombres de MotivoFalloEmbedding para los fallos del proveedor.
/// </summary>
public static class MotivosIndexacion
{
    public const string HashDistinto = "HASH_MISMATCH";
    public const string AlmacenamientoRechazado = "STORAGE_FORBIDDEN";
    public const string ContextoExcedido = "CONTEXT_EXCEEDED";
    public const string LimiteDeFragmentos = "FRAGMENT_LIMIT";
    public const string LeaseVencido = "LEASE_EXPIRED";
    public const string ReintentosAgotados = "RETRIES_EXHAUSTED";
    public const string Interno = "INTERNAL";

    // Motivos de la purga (DOCUMENT_INDEX_PURGED, §24).
    public const string DocumentoEliminado = "DOCUMENTO_ELIMINADO";
    public const string PerfilObsoleto = "PERFIL_OBSOLETO";
    public const string TenantDesactivado = "TENANT_DESACTIVADO";
}

/// <summary>
/// Fase 8.4 — Perfil de indexación (FASE_8_CONTRATO.md §3.4): la firma del embedding más las versiones de
/// fragmentación, extracción y normalización. Función pura.
/// </summary>
public static class PerfilIndexacion
{
    public const int LongitudMaxima = 200;

    public static string Componer(IEmbeddingProvider proveedor) =>
        $"{FirmaEmbedding.De(proveedor)}|{Fragmentador.Version}|{ExtractionProfile.VersionIndexacion}|{TextoNormalizador.Version}";
}
