using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Infrastructure.BackgroundServices;

/// <summary>
/// Fase 8.4 — Opciones del worker de indexación semántica (sección <c>AI:Indexing</c>, FASE_8_4_CONTRATO.md §29.4).
/// Ninguna es secreta y ninguna configura el tamaño del lote de embeddings: ese es el del proveedor, con el tope
/// contractual de 64.
/// </summary>
public class IndexacionOptions
{
    public const string SectionName = "AI:Indexing";

    /// <summary>Tokens estimados máximos de un fragmento: ceil(2.000 caracteres / 4).</summary>
    public const int TokensEstimadosMaximosPorFragmento = 500;

    /// <summary>Enfriamiento de la instancia tras un fallo transitorio del proveedor (§12). Contractual: 60 s.</summary>
    public static readonly TimeSpan Enfriamiento = TimeSpan.FromSeconds(60);

    /// <summary>Backoff del trabajo: min(2^Intentos × 1 min, 6 h) con jitter de ±20 % (§12).</summary>
    public static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan BackoffMaximo = TimeSpan.FromHours(6);
    public const double BackoffJitter = 0.20;

    /// <summary>Plazo de la liberación de un trabajo en una parada limpia (§23).</summary>
    public static readonly TimeSpan PlazoLiberacion = TimeSpan.FromSeconds(5);

    public bool Enabled { get; set; } = true;

    /// <summary>Intervalo entre ciclos del worker.</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Documentos en proceso a la vez por instancia; también el máximo de peticiones simultáneas al proveedor.</summary>
    public int MaxParalelismo { get; set; } = 2;

    /// <summary>Documentos en proceso a la vez por tenant (límite aproximado, para la equidad).</summary>
    public int MaxDocumentosPorTenant { get; set; } = 2;

    /// <summary>Lease: segundos desde el último latido.</summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Intentos máximos del trabajo antes de quedar Fallido.</summary>
    public int MaxIntentos { get; set; } = 5;

    public int MaxFragmentosPorDocumento { get; set; } = 2000;

    /// <summary>Presupuesto PREVENTIVO ESTIMADO por tenant y día UTC; no es consumo real del proveedor.</summary>
    public long MaxTokensDiariosPorTenant { get; set; } = 5_000_000;

    public int LoteSembrado { get; set; } = 500;

    public int LotePurga { get; set; } = 200;
}

/// <summary>Validación al arrancar (ValidateOnStart), como el resto de opciones de IA.</summary>
public sealed class IndexacionOptionsValidator(IOptions<EmbeddingOptions> embeddings) : IValidateOptions<IndexacionOptions>
{
    /// <summary>Margen del lease sobre el paso individual más largo.</summary>
    public const int MargenLeaseSegundos = 30;

    public ValidateOptionsResult Validate(string? name, IndexacionOptions options)
    {
        var errores = new List<string>();

        void Exigir(bool condicion, string mensaje)
        {
            if (!condicion)
            {
                errores.Add(mensaje);
            }
        }

        Exigir(options.IntervalSeconds >= 1, "AI:Indexing:IntervalSeconds debe ser al menos 1.");
        Exigir(options.MaxParalelismo is >= 1 and <= 16, "AI:Indexing:MaxParalelismo debe estar entre 1 y 16.");
        Exigir(options.MaxDocumentosPorTenant >= 1, "AI:Indexing:MaxDocumentosPorTenant debe ser al menos 1.");
        Exigir(options.MaxIntentos is >= 1 and <= 20, "AI:Indexing:MaxIntentos debe estar entre 1 y 20.");
        Exigir(options.MaxFragmentosPorDocumento >= 1, "AI:Indexing:MaxFragmentosPorDocumento debe ser al menos 1.");
        Exigir(options.LoteSembrado >= 1, "AI:Indexing:LoteSembrado debe ser al menos 1.");
        Exigir(options.LotePurga >= 1, "AI:Indexing:LotePurga debe ser al menos 1.");

        // El lease debe superar cada paso individual con margen: la extracción (120 s) y un lote de embeddings.
        double pasoMasLargo = ExtractionProfile.TimeoutSecondsIndexacion;
        if (options.Enabled)
        {
            // Solo se leen las opciones de embeddings si el worker va a ejecutarse (su propia validación ya las cubre).
            pasoMasLargo = Math.Max(pasoMasLargo, embeddings.Value.TimeoutSeconds);
        }

        Exigir(options.LeaseSeconds > pasoMasLargo + MargenLeaseSegundos,
            "AI:Indexing:LeaseSeconds debe superar con margen la extracción (120 s) y el timeout de un lote de embeddings.");

        // Todo documento admitido debe caber en el presupuesto de un día vacío: si no, quedaría aplazado para siempre.
        Exigir(options.MaxFragmentosPorDocumento < 1
               || options.MaxTokensDiariosPorTenant >= (long)options.MaxFragmentosPorDocumento * IndexacionOptions.TokensEstimadosMaximosPorFragmento,
            "AI:Indexing:MaxTokensDiariosPorTenant debe admitir al menos un documento del tamaño máximo (MaxFragmentosPorDocumento × 500).");

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}
