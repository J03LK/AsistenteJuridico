using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Domain.Tests.Fase8;

internal sealed class EntornoFalso(string nombre) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = nombre;
    public string ApplicationName { get; set; } = "Pruebas";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Captura todos los logs (mensaje formateado y excepción) de cualquier categoría.</summary>
internal sealed class CapturaDeLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Mensajes { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Registrador(this);

    public void Dispose() { }

    private sealed class Registrador(CapturaDeLogs captura) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            captura.Mensajes.Enqueue($"[{logLevel}] {formatter(state, exception)} {exception}");
    }
}

internal sealed record PeticionCapturada(Uri Url, string Metodo, Dictionary<string, string> Cabeceras, string Cuerpo);

/// <summary>Transporte HTTP simulado (sin red): registra cada petición y responde con la función indicada.</summary>
internal sealed class TransporteSimulado(Func<int, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _llamadas;

    public ConcurrentQueue<PeticionCapturada> Peticiones { get; } = new();
    public int Llamadas => Volatile.Read(ref _llamadas);

    public static TransporteSimulado Fijo(Func<HttpResponseMessage> respuesta) => new((_, _) => Task.FromResult(respuesta()));

    /// <summary>Respuestas por número de intento (base 1); la última se repite.</summary>
    public static TransporteSimulado Secuencia(params Func<HttpResponseMessage>[] respuestas) =>
        new((n, _) => Task.FromResult(respuestas[Math.Min(n, respuestas.Length) - 1]()));

    public static TransporteSimulado QueNuncaResponde() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException();
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _llamadas);
        var cabeceras = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var cuerpo = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Peticiones.Enqueue(new PeticionCapturada(request.RequestUri!, request.Method.Method, cabeceras, cuerpo));
        return await responder(n, cancellationToken);
    }
}

/// <summary>
/// Reloj falso para el manejador de resiliencia: registra cada espera pedida y, según <paramref name="disparar"/>,
/// la completa de inmediato o no la completa nunca (para probar cancelación y timeout durante el backoff).
/// </summary>
internal sealed class TiempoFalso(bool disparar) : TimeProvider
{
    public ConcurrentQueue<TimeSpan> Esperas { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Esperas.Enqueue(dueTime);
        if (disparar)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new TemporizadorInerte();
    }

    private sealed class TemporizadorInerte : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class Entorno83 : IDisposable
{
    public const string Clave = "clave-secreta-de-prueba";
    public const string Modelo = "text-embedding-3-small";
    public const string BaseUrl = "https://embeddings.prueba/v1";

    public ServiceProvider Servicios { get; }
    public CapturaDeLogs Logs { get; } = new();
    public IEmbeddingProvider Proveedor => _scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();

    private readonly IServiceScope _scope;

    public Entorno83(HttpMessageHandler? transporte = null, Dictionary<string, string?>? configuracion = null,
        string entorno = "Development", TimeProvider? tiempo = null, bool openAI = true, Action<IServiceCollection>? adicionales = null)
    {
        var valores = new Dictionary<string, string?>();
        if (openAI)
        {
            valores["AI:Embeddings:Provider"] = "OpenAICompatible";
            valores["AI:Embeddings:BaseUrl"] = BaseUrl;
            valores["AI:Embeddings:ApiKey"] = Clave;
        }

        foreach (var (clave, valor) in configuracion ?? [])
        {
            valores[clave] = valor;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddSingleton<IHostEnvironment>(new EntornoFalso(entorno));
        if (tiempo != null)
        {
            services.AddSingleton(tiempo);
        }

        services.AddEmbeddingProvider(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        if (transporte != null)
        {
            services.AddHttpClient<OpenAICompatibleEmbeddingProvider>().ConfigurePrimaryHttpMessageHandler(() => transporte);
        }

        adicionales?.Invoke(services);
        Servicios = services.BuildServiceProvider();
        _scope = Servicios.CreateScope();
    }

    public void Dispose()
    {
        _scope.Dispose();
        Servicios.Dispose();
    }
}

/// <summary>
/// Coloca un manejador que nunca avanza como el MÁS EXTERNO de la cadena (por delante del manejador de resiliencia y
/// del contador de intentos): la petición HTTP no llega a emitirse. Sirve para probar el timeout antes del primer envío.
/// </summary>
internal sealed class BloqueoAntesDelPrimerEnvio : IHttpMessageHandlerBuilderFilter
{
    public int Entradas;

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.AdditionalHandlers.Insert(0, new Manejador(this));
    };

    private sealed class Manejador(BloqueoAntesDelPrimerEnvio filtro) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref filtro.Entradas);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}

internal static class Respuestas83
{
    /// <summary>Vector de 1536 (o la dimensión indicada) con un único 1 en la posición <paramref name="marca"/>.</summary>
    public static string VectorJson(int marca, int dimension = 1536, string? sustituto = null)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < dimension; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(i == marca ? sustituto ?? "1" : "0");
        }

        return sb.Append(']').ToString();
    }

    /// <summary>Respuesta 200 con el formato de la API de OpenAI. <paramref name="indices"/> permite desordenar o corromper.</summary>
    public static string Json(int n, string? modelo = Entorno83.Modelo, int? tokens = 10, int[]? indices = null,
        Func<int, string>? vector = null, string? usageCrudo = null)
    {
        indices ??= Enumerable.Range(0, n).ToArray();
        var data = string.Join(",", indices.Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"{{\"object\":\"embedding\",\"index\":{i},\"embedding\":{(vector ?? (x => VectorJson(x)))(i)}}}")));
        var partes = new List<string> { "\"object\":\"list\"", $"\"data\":[{data}]" };
        if (modelo != null)
        {
            partes.Add($"\"model\":\"{modelo}\"");
        }

        if (usageCrudo != null)
        {
            partes.Add($"\"usage\":{usageCrudo}");
        }
        else if (tokens != null)
        {
            partes.Add(string.Create(CultureInfo.InvariantCulture, $"\"usage\":{{\"prompt_tokens\":{tokens},\"total_tokens\":{tokens}}}"));
        }

        return "{" + string.Join(",", partes) + "}";
    }

    public static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Estado(HttpStatusCode estado, string cuerpo = "{\"error\":{\"message\":\"CUERPO-DEL-PROVEEDOR\"}}", int? retryAfter = null)
    {
        var respuesta = new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        if (retryAfter is { } segundos)
        {
            respuesta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(segundos));
        }

        return respuesta;
    }

    public static string[] Textos(int n) => Enumerable.Range(1, n).Select(i => $"Fragmento número {i} del contrato").ToArray();
}
