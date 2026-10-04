using System.Net;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Fase 8.3 — Registro del proveedor de embeddings (FASE_8_3_CONTRATO.md §5, §8.3 y §12). Solo registra la
/// infraestructura: ningún servicio de producción consume todavía <see cref="IEmbeddingProvider"/>.
/// </summary>
public static class EmbeddingServiceCollectionExtensions
{
    public const string NombreManejadorResiliencia = "ai-embeddings-retry";

    public static IServiceCollection AddEmbeddingProvider(this IServiceCollection services, IConfiguration configuration)
    {
        // Validación al arrancar: Mock prohibido en Production, dimensión 1536, HTTPS salvo loopback, clave obligatoria…
        services.AddSingleton<IValidateOptions<EmbeddingOptions>, EmbeddingOptionsValidator>();
        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateOnStart();

        services.AddTransient<OpenAICompatibleEmbeddingProvider.ContadorDeIntentosHandler>();

        // El límite por lote (que incluye los reintentos) lo gobierna el proveedor con un CTS enlazado, igual que el
        // chat: por eso se desactiva el timeout propio de HttpClient.
        // Sin los logs automáticos de HttpClientFactory: registran el mensaje de las excepciones de transporte, y el
        // contrato solo permite el tipo. Los únicos logs de este cliente son los del propio proveedor.
        var cliente = services.AddHttpClient<OpenAICompatibleEmbeddingProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CrearManejadorPrimario)
            .RemoveAllLoggers();

        // Política contractual FIJA (no configurable): MaxRetries = 2 (como máximo 3 intentos), backoff exponencial con
        // jitter y base de 1,5 s; solo ante errores de red, 429 y 503. El mismo patrón que el proveedor de chat.
        cliente.AddResilienceHandler(NombreManejadorResiliencia, (builder, context) =>
        {
            builder.TimeProvider = context.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            // Sin telemetría de Polly en logs: incluiría el mensaje de las excepciones de transporte de cada reintento.
            builder.ConfigureTelemetry(NullLoggerFactory.Instance);
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = EmbeddingOptions.MaxReintentos,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = EmbeddingOptions.RetardoBaseReintento,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException
                    || args.Outcome.Result?.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            });
        });

        // Interno al manejador de resiliencia: cuenta cada intento real.
        cliente.AddHttpMessageHandler<OpenAICompatibleEmbeddingProvider.ContadorDeIntentosHandler>();

        services.AddSingleton<MockEmbeddingProvider>();
        services.AddScoped<IEmbeddingProvider>(sp =>
            sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value.UsaOpenAICompatible
                ? sp.GetRequiredService<OpenAICompatibleEmbeddingProvider>()
                : sp.GetRequiredService<MockEmbeddingProvider>());

        return services;
    }

    /// <summary>
    /// Manejador HTTP primario del proveedor externo: sin redirecciones (la clave nunca viaja a otro host) y sin
    /// contenedor de cookies.
    /// </summary>
    public static HttpMessageHandler CrearManejadorPrimario() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };
}
