using System.Net;
using AsistenteJuridico.Domain.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Fase 8.3 — Configuración del proveedor de embeddings (sección <c>AI:Embeddings</c>, FASE_8_3_CONTRATO.md §12).
/// Es independiente de <c>AI:OpenAICompatible</c> (chat). No hay opciones de reintentos, de retardo, de coste ni de
/// estimación de tokens: son constantes contractuales o no pertenecen a esta fase.
/// </summary>
public class EmbeddingOptions
{
    public const string SectionName = "AI:Embeddings";

    public const string ProveedorMock = "Mock";
    public const string ProveedorOpenAICompatible = "OpenAICompatible";

    /// <summary>MaxRetries = 2 del contrato rector, fijo: como máximo 3 intentos por llamada. No es configurable.</summary>
    public const int MaxReintentos = 2;

    /// <summary>Retardo base del backoff exponencial con jitter: 1,5 s, contractual. No es configurable.</summary>
    public static readonly TimeSpan RetardoBaseReintento = TimeSpan.FromSeconds(1.5);

    /// <summary>Tamaño máximo de la respuesta del proveedor: 16 MiB.</summary>
    public const int MaxBytesRespuesta = 16 * 1024 * 1024;

    /// <summary>Máximo contractual absoluto de textos por lote (el de la validación común de lotes).</summary>
    public const int MaxEntradasPorLoteContractual = AsistenteJuridico.Application.Common.Interfaces.AI.LoteEmbeddings.MaxEntradasPorLoteContractual;

    /// <summary>Entrada máxima del modelo inicial (text-embedding-3-small): 8.191 tokens por texto.</summary>
    public const int MaxTokensPorEntrada = 8191;

    /// <summary>
    /// "Mock" u "OpenAICompatible". Sin valor: Mock fuera de Production; en Production la aplicación no arranca
    /// (el simulado está prohibido en producción, explícito o por defecto).
    /// </summary>
    public string? Provider { get; set; }

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Secreta: solo desde user-secrets, variables de entorno o el almacén de secretos del despliegue.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string ModelId { get; set; } = "text-embedding-3-small";

    public int Dimensiones { get; set; } = DocumentoIndice.DimensionesPerfilInicial;

    /// <summary>Límite duro por lote, que incluye los reintentos y sus esperas. Contrato: 60 s.</summary>
    public double TimeoutSeconds { get; set; } = 60;

    public int MaxEntradasPorLote { get; set; } = MaxEntradasPorLoteContractual;

    /// <summary>True si el proveedor seleccionado es el compatible con OpenAI; false si es el simulado (explícito o por defecto).</summary>
    public bool UsaOpenAICompatible => string.Equals(Provider?.Trim(), ProveedorOpenAICompatible, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Fase 8.3 — Validación al arrancar (ValidateOnStart), el mismo mecanismo que la validación del lease de la 6.X.
/// Los mensajes nunca contienen la clave ni la URL configurada.
/// </summary>
public sealed class EmbeddingOptionsValidator(IHostEnvironment entorno) : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        var errores = new List<string>();
        var proveedor = options.Provider?.Trim();
        var esMock = string.IsNullOrEmpty(proveedor) || string.Equals(proveedor, EmbeddingOptions.ProveedorMock, StringComparison.OrdinalIgnoreCase);

        if (!esMock && !options.UsaOpenAICompatible)
        {
            errores.Add("AI:Embeddings:Provider debe ser 'Mock' u 'OpenAICompatible'.");
        }

        if (esMock && entorno.IsProduction())
        {
            errores.Add("AI:Embeddings:Provider debe ser 'OpenAICompatible' en Production: el proveedor simulado (explícito o por defecto) no está permitido.");
        }

        if (options.Dimensiones != DocumentoIndice.DimensionesPerfilInicial)
        {
            errores.Add("AI:Embeddings:Dimensiones debe ser exactamente 1536.");
        }

        if (options.MaxEntradasPorLote is < 1 or > EmbeddingOptions.MaxEntradasPorLoteContractual)
        {
            errores.Add("AI:Embeddings:MaxEntradasPorLote debe estar entre 1 y 64.");
        }

        if (!(options.TimeoutSeconds > 0) || double.IsInfinity(options.TimeoutSeconds))
        {
            errores.Add("AI:Embeddings:TimeoutSeconds debe ser mayor que 0.");
        }

        if (options.UsaOpenAICompatible)
        {
            ValidarOpenAICompatible(options, errores);
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }

    private static void ValidarOpenAICompatible(EmbeddingOptions options, List<string> errores)
    {
        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            errores.Add("AI:Embeddings:ModelId es obligatorio.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            errores.Add("AI:Embeddings:BaseUrl debe ser una URL absoluta http(s).");
            return;
        }

        var esLoopback = EsLoopback(url);
        if (url.Scheme != Uri.UriSchemeHttps && !esLoopback)
        {
            errores.Add("AI:Embeddings:BaseUrl debe usar HTTPS (http solo se admite con un host de loopback).");
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            errores.Add("AI:Embeddings:BaseUrl no puede contener credenciales.");
        }

        if (!string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            errores.Add("AI:Embeddings:BaseUrl no puede contener query ni fragmento.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey) && !esLoopback)
        {
            errores.Add("AI:Embeddings:ApiKey es obligatoria para el proveedor 'OpenAICompatible' (salvo con un host de loopback).");
        }
    }

    private static bool EsLoopback(Uri url) =>
        url.IsLoopback || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
