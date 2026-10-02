using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AsistenteJuridico.API.Configuration;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Domain.Tests.Fase6;

/// <summary>
/// Host compartido cuyo proveedor de IA falla siempre (con secretos en el mensaje de la excepción).
/// </summary>
public sealed class FailingProviderApiFixture : IDisposable
{
    internal AiApiFactory Factory { get; } = new(AiApiFactory.WithProvider(new ThrowingAIProvider()));

    public void Dispose() => Factory.Dispose();
}

/// <summary>
/// Fase 6.2 — Comportamiento HTTP real del pipeline completo de la API (autenticación, rate limiting,
/// middleware global de excepciones) contra PostgreSQL 16, sin proveedores comerciales.
/// </summary>
public class Fase62ApiBehaviorTests : IClassFixture<FailingProviderApiFixture>
{
    private readonly AiApiFactory _failingFactory;

    public Fase62ApiBehaviorTests(FailingProviderApiFixture fixture)
    {
        _failingFactory = fixture.Factory;
    }

    public static TheoryData<string> OperacionesDeIA => new()
    {
        "chat",
        "resumir-expediente",
        "resumir-documento",
        "extraer-datos-documento",
        "generar-borrador"
    };

    private static object CuerpoPara(string operacion, Guid expedienteId, Guid documentoId) => operacion switch
    {
        "chat" => new { mensaje = "Consulta jurídica", streaming = false },
        "resumir-expediente" => new { expedienteId },
        "resumir-documento" => new { documentoId },
        "extraer-datos-documento" => new { documentoId },
        "generar-borrador" => new { expedienteId, tipoEscrito = "Demanda", instrucciones = "Redactar demanda" },
        _ => throw new ArgumentOutOfRangeException(nameof(operacion))
    };

    private static async Task<JsonElement> LeerJsonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    // ─────────────────────────────────────────────────────────────
    // Proveedor falla -> Servicio -> AIProviderException -> Middleware -> HTTP 502
    // ─────────────────────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(OperacionesDeIA))]
    public async Task FallaProveedor_EnCadaOperacion_DevuelveHttp502SinSecretos(string operacion)
    {
        using var client = _failingFactory.CreateClient();
        var userId = Guid.NewGuid();
        var token = _failingFactory.CreateUserToken(userId, Roles.AbogadoSenior);
        var (expedienteId, documentoId) = _failingFactory.SeedExpedienteConDocumento(userId);

        var response = await client.SendAsync(_failingFactory.Request(
            HttpMethod.Post, $"/api/v1/ai/{operacion}", token, CuerpoPara(operacion, expedienteId, documentoId)));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ThrowingAIProvider.SecretInMessage, raw);
        Assert.DoesNotContain("api.proveedor.test", raw);

        var body = await LeerJsonAsync(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString() == "AI_PROVIDER_ERROR");

        // El intento fallido quedó registrado en AIUsageLog para el usuario y tenant correctos
        var fallidos = await _failingFactory.QueryDbAsync(db => db.AIUsageLogs.IgnoreQueryFilters()
            .Where(l => l.TenantId == _failingFactory.TenantId && l.UsuarioId == userId)
            .ToListAsync());
        var log = Assert.Single(fallidos);
        Assert.False(log.Exitoso);
        Assert.Equal(nameof(HttpRequestException), log.CodigoError);
    }

    // ─────────────────────────────────────────────────────────────
    // Streaming: fallo ANTES del primer fragmento -> HTTP 502 con envelope JSON
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Streaming_FallaAntesDelPrimerFragmento_DevuelveHttp502Json()
    {
        using var client = _failingFactory.CreateClient();
        var token = _failingFactory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);

        var response = await client.SendAsync(_failingFactory.Request(
            HttpMethod.Post, "/api/v1/ai/chat", token, new { mensaje = "Consulta", streaming = true }));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ThrowingAIProvider.SecretInMessage, raw);
        var body = await LeerJsonAsync(response);
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString() == "AI_PROVIDER_ERROR");
    }

    // ─────────────────────────────────────────────────────────────
    // Streaming: fallo A MITAD de la transmisión -> event: error y sin [DONE]
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Streaming_FallaAMitadDeTransmision_EmiteEventoErrorSinDone()
    {
        await using var factory = new AiApiFactory(AiApiFactory.WithProvider(new MidStreamFailingAIProvider()));
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var token = factory.CreateUserToken(userId, Roles.AbogadoSenior);

        var response = await client.SendAsync(factory.Request(
            HttpMethod.Post, "/api/v1/ai/chat", token, new { mensaje = "Consulta", streaming = true }));

        // El 200 ya fue enviado con el primer fragmento: el error viaja dentro del protocolo SSE
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("Primer fragmento", raw);
        Assert.Contains("event: error\ndata: ", raw);
        Assert.Contains("\"code\":\"AI_PROVIDER_ERROR\"", raw);
        Assert.DoesNotContain("[DONE]", raw);
        Assert.DoesNotContain(ThrowingAIProvider.SecretInMessage, raw);

        // El evento de error es el último bloque del stream
        var bloques = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("event: error", bloques[^1]);

        // Se registra el intento fallido con los tokens parciales recibidos hasta el corte
        var log = await factory.QueryDbAsync(db => db.AIUsageLogs.IgnoreQueryFilters()
            .SingleAsync(l => l.TenantId == factory.TenantId && l.UsuarioId == userId));
        Assert.False(log.Exitoso);
        Assert.True(log.TokensSalida > 0);
    }

    // ─────────────────────────────────────────────────────────────
    // Streaming exitoso: POST + SSE, fragmentos data: y cierre data: [DONE]
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Streaming_Exitoso_UsaPostSseYFinalizaConDone()
    {
        await using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);

        var response = await client.SendAsync(factory.Request(
            HttpMethod.Post, "/api/v1/ai/chat", token, new { mensaje = "¿Qué es la caducidad?", streaming = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        var bloques = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(bloques.Length > 2);
        Assert.All(bloques, b => Assert.StartsWith("data: ", b));
        Assert.Equal("data: [DONE]", bloques[^1]);
        Assert.DoesNotContain("event: error", raw);

        using var primero = JsonDocument.Parse(bloques[0]["data: ".Length..]);
        Assert.True(primero.RootElement.TryGetProperty("deltaContent", out _));

        // El streaming solo existe vía POST
        var get = await client.SendAsync(factory.Request(HttpMethod.Get, "/api/v1/ai/chat", token));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────
    // Timeout real: OpenAICompatibleProvider + transporte lento -> HTTP 502 AI_PROVIDER_TIMEOUT
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Timeout_ProveedorLento_SeCancelaYDevuelveHttp502Timeout()
    {
        var transporte = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return StubHttpMessageHandler.OpenAiCompletion("respuesta tardía");
        });

        await using var factory = new AiApiFactory(AiApiFactory.WithOpenAICompatibleProvider(transporte, o =>
        {
            o.TimeoutSeconds = 0.05;
            o.MaxRetries = 0;
        }));
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var token = factory.CreateUserToken(userId, Roles.AbogadoSenior);

        var sw = Stopwatch.StartNew();
        var response = await client.SendAsync(factory.Request(
            HttpMethod.Post, "/api/v1/ai/chat", token, new { mensaje = "Consulta que excede el tiempo", streaming = false }));
        sw.Stop();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"La petición no se canceló a tiempo: {sw.Elapsed}");
        Assert.True(transporte.CancellationObserved);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ThrowingAIProvider.SecretInMessage, raw);
        var body = await LeerJsonAsync(response);
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString() == "AI_PROVIDER_TIMEOUT");

        var log = await factory.QueryDbAsync(db => db.AIUsageLogs.IgnoreQueryFilters()
            .SingleAsync(l => l.TenantId == factory.TenantId && l.UsuarioId == userId));
        Assert.False(log.Exitoso);
        Assert.Equal("AI_PROVIDER_TIMEOUT", log.CodigoError);
    }

    // ─────────────────────────────────────────────────────────────
    // Reintentos (v1.1.1 §18): solo ante 429/503 o errores de red; nunca ante 4xx de negocio
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Reintentos_SoloAnteErroresTransitorios()
    {
        var respuestas = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests]);
        var transitorio = new StubHttpMessageHandler((_, _) => Task.FromResult(
            respuestas.Count > 0
                ? new HttpResponseMessage(respuestas.Dequeue())
                : StubHttpMessageHandler.OpenAiCompletion("Respuesta tras reintentos")));

        await using (var factory = new AiApiFactory(AiApiFactory.WithOpenAICompatibleProvider(transitorio, o =>
        {
            o.MaxRetries = 2;
            o.RetryBaseDelaySeconds = 0.01;
        })))
        {
            using var client = factory.CreateClient();
            var token = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);
            var ok = await client.SendAsync(factory.Request(HttpMethod.Post, "/api/v1/ai/chat", token, new { mensaje = "Consulta" }));

            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(3, transitorio.Calls);
        }

        var noTransitorio = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        await using (var factory = new AiApiFactory(AiApiFactory.WithOpenAICompatibleProvider(noTransitorio, o =>
        {
            o.MaxRetries = 2;
            o.RetryBaseDelaySeconds = 0.01;
        })))
        {
            using var client = factory.CreateClient();
            var token = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);
            var error = await client.SendAsync(factory.Request(HttpMethod.Post, "/api/v1/ai/chat", token, new { mensaje = "Consulta" }));

            Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
            Assert.Equal(1, noTransitorio.Calls);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Configuración efectiva del host real (sin sobrescrituras de prueba)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public void ConfiguracionDeProduccion_Timeout60s_RateLimit15PorUsuario60PorTenant()
    {
        using var factory = new AiApiFactory();

        var aiOptions = factory.Services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        Assert.Equal(60, aiOptions.TimeoutSeconds);
        Assert.Equal(2, aiOptions.MaxRetries);

        var rateLimit = factory.Services.GetRequiredService<IOptions<AIRateLimitOptions>>().Value;
        Assert.Equal(15, rateLimit.UserPermitLimit);
        Assert.Equal(60, rateLimit.TenantPermitLimit);
        Assert.Equal(60, rateLimit.WindowSeconds);
    }

    private static async Task<HttpStatusCode> CapacidadesAsync(HttpClient client, AiApiFactory factory, string token) =>
        (await client.SendAsync(factory.Request(HttpMethod.Get, "/api/v1/ai/capacidades", token))).StatusCode;

    // ─────────────────────────────────────────────────────────────
    // Rate limit Caso A: el usuario supera su cuota individual con contadores reales de producción
    // (15/minuto/usuario) -> la solicitud 16 recibe 429
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task RateLimit_CasoA_UsuarioSuperaCuotaIndividualDe15()
    {
        await using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);

        for (var i = 1; i <= 15; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await CapacidadesAsync(client, factory, token));
        }

        var rechazada = await client.SendAsync(factory.Request(HttpMethod.Get, "/api/v1/ai/capacidades", token));
        Assert.Equal(HttpStatusCode.TooManyRequests, rechazada.StatusCode);
        Assert.True(rechazada.Headers.Contains("Retry-After"));
        var body = await LeerJsonAsync(rechazada);
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString() == "TOO_MANY_REQUESTS");
    }

    // ─────────────────────────────────────────────────────────────
    // Rate limit Caso B: ningún usuario supera su cuota, pero el tenant supera la suya -> 429
    // Caso C: un usuario de otro tenant no se ve afectado por la cuota agotada del tenant A
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task RateLimit_CasosByC_CuotaDeTenantCompartidaEntreUsuariosEIsladaEntreTenants()
    {
        // Cuotas reducidas solo en este host: 3 por usuario, 5 por tenant
        await using var factory = new AiApiFactory(services =>
            services.Configure<AIRateLimitOptions>(o =>
            {
                o.UserPermitLimit = 3;
                o.TenantPermitLimit = 5;
                o.WindowSeconds = 60;
            }));
        using var client = factory.CreateClient();

        var usuario1 = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);
        var usuario2 = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);
        var usuario3 = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoJunior);

        // Tenant A: usuario1 usa 3 (su máximo) y usuario2 usa 2 -> 5 solicitudes del tenant, todas permitidas
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await CapacidadesAsync(client, factory, usuario1));
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, await CapacidadesAsync(client, factory, usuario2));

        // Caso B: usuario2 (2 de 3) y usuario3 (0 de 3) están bajo su cuota individual, pero el tenant agotó 5 -> 429
        var rechazoUsuario2 = await client.SendAsync(factory.Request(HttpMethod.Get, "/api/v1/ai/capacidades", usuario2));
        Assert.Equal(HttpStatusCode.TooManyRequests, rechazoUsuario2.StatusCode);
        Assert.True(rechazoUsuario2.Headers.Contains("Retry-After"));
        var body = await LeerJsonAsync(rechazoUsuario2);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString() == "TOO_MANY_REQUESTS");

        Assert.Equal(HttpStatusCode.TooManyRequests, await CapacidadesAsync(client, factory, usuario3));

        // Caso C: un usuario del tenant B, desde el mismo origen, no se ve afectado por la cuota del tenant A
        var (tenantB, slugB) = factory.CreateAdditionalTenant();
        var usuarioTenantB = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior, tenantB, slugB);
        Assert.Equal(HttpStatusCode.OK, await CapacidadesAsync(client, factory, usuarioTenantB));

        // ...y su propia cuota individual sigue aplicándose
        Assert.Equal(HttpStatusCode.OK, await CapacidadesAsync(client, factory, usuarioTenantB));
        Assert.Equal(HttpStatusCode.OK, await CapacidadesAsync(client, factory, usuarioTenantB));
        Assert.Equal(HttpStatusCode.TooManyRequests, await CapacidadesAsync(client, factory, usuarioTenantB));
    }

    // ─────────────────────────────────────────────────────────────
    // La cuota de IA no usa la IP como identidad: una petición sin JWT no consume cuota (401)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task RateLimit_SinIdentidad_NoConsumeCuotaYDevuelve401()
    {
        await using var factory = new AiApiFactory(services =>
            services.Configure<AIRateLimitOptions>(o =>
            {
                o.UserPermitLimit = 1;
                o.TenantPermitLimit = 1;
            }));
        using var client = factory.CreateClient();

        // Varias peticiones anónimas desde el mismo origen: todas 401, ninguna 429
        for (var i = 0; i < 3; i++)
        {
            var anonima = await client.GetAsync("/api/v1/ai/capacidades");
            Assert.Equal(HttpStatusCode.Unauthorized, anonima.StatusCode);
        }

        // Un usuario autenticado del mismo origen conserva su cuota completa
        var token = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);
        Assert.Equal(HttpStatusCode.OK, await CapacidadesAsync(client, factory, token));
    }

    // ─────────────────────────────────────────────────────────────
    // Contrato de timeout: 502 + AI_PROVIDER_TIMEOUT (nunca 504)
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Timeout_ContratoEs502ConAIProviderTimeout_No504()
    {
        var transporte = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return StubHttpMessageHandler.OpenAiCompletion("tarde");
        });

        await using var factory = new AiApiFactory(AiApiFactory.WithOpenAICompatibleProvider(transporte, o =>
        {
            o.TimeoutSeconds = 0.05;
            o.MaxRetries = 0;
        }));
        using var client = factory.CreateClient();
        var token = factory.CreateUserToken(Guid.NewGuid(), Roles.AbogadoSenior);

        // También en streaming: el fallo ocurre antes del primer fragmento -> 502 JSON
        foreach (var streaming in new[] { false, true })
        {
            var response = await client.SendAsync(factory.Request(
                HttpMethod.Post, "/api/v1/ai/chat", token, new { mensaje = "Consulta", streaming }));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
            var body = await LeerJsonAsync(response);
            var errores = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            Assert.Equal(["AI_PROVIDER_TIMEOUT"], errores);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TEST 29 (HTTP): AIUsageLog no admite modificación ni borrado vía API
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Consumo_PutPatchDelete_NoExistenEnLaApi()
    {
        using var client = _failingFactory.CreateClient();
        var token = _failingFactory.CreateUserToken(Guid.NewGuid(), Roles.AdminEstudio);

        foreach (var metodo in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var response = await client.SendAsync(_failingFactory.Request(metodo, "/api/v1/ai/consumo", token, new { }));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Endpoint administrativo de purga: solo AdminEstudio
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Purga_SoloAdminEstudio()
    {
        using var client = _failingFactory.CreateClient();

        foreach (var rol in new[] { Roles.AbogadoSenior, Roles.AbogadoJunior, Roles.AsistenteLegal })
        {
            var token = _failingFactory.CreateUserToken(Guid.NewGuid(), rol);
            var response = await client.SendAsync(_failingFactory.Request(HttpMethod.Post, "/api/v1/ai/mantenimiento/purgar", token));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var adminToken = _failingFactory.CreateUserToken(Guid.NewGuid(), Roles.AdminEstudio);
        var ok = await client.SendAsync(_failingFactory.Request(HttpMethod.Post, "/api/v1/ai/mantenimiento/purgar", adminToken));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
