using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using AsistenteJuridico.Infrastructure.Services.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsistenteJuridico.Domain.Tests.Fase6;

internal sealed class TestTenantService : ICurrentTenantService
{
    public Guid? TenantId { get; set; }
    public string? TenantSlug => "tenant-test";
    public bool IsMultiTenantContext => TenantId.HasValue;
    public void SetTenantId(Guid tenantId) => TenantId = tenantId;
}

internal sealed class TestUserService : ICurrentUserService
{
    public Guid? UserId { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string? Email { get; set; } = "abogado@estudio.com";
    public string? Role { get; set; } = Roles.AbogadoSenior;
    public bool IsAuthenticated => true;
    public IEnumerable<string> Permissions => Application.Common.Security.Permissions.GetPermissionsForRole(Role ?? Roles.AbogadoSenior);
    public bool HasPermission(string permission) => true;
}

internal sealed class TestFileStorageService : IFileStorageService
{
    public string ContentToReturn { get; set; } = "Contenido procesal para análisis jurídico.";

    public Task DeleteFileIfExistsAsync(string? relativeFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Stream> OpenReadFileAsync(string relativeFilePath, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(ContentToReturn)));

    public Task<(string PhysicalFileName, string RelativeFilePath, string ContentType, long FileSizeBytes, string Sha256Hash)> SaveFileAsync(
        Guid tenantId, Stream fileStream, string originalFileName, string declaredContentType, CancellationToken cancellationToken = default) =>
        Task.FromResult(("f.pdf", "tenant/f.pdf", "application/pdf", 100L, "hash"));
}

/// <summary>
/// Proveedor que falla siempre con una excepción cuyo mensaje contiene datos sensibles,
/// para comprobar que nada de ese mensaje llega al cliente ni a la base de datos.
/// </summary>
internal sealed class ThrowingAIProvider : IAIProvider
{
    public const string SecretInMessage = "sk-SECRETKEY12345";

    public int Invocations { get; private set; }

    public string ProviderId => "failing-provider";

    public AIProviderCapabilities Capabilities => new(
        MaxContextTokens: 16384,
        MaxOutputTokens: 4096,
        SupportsStreaming: true,
        SupportsSystemPrompt: true,
        SupportedModels: ["mock-fail"],
        SupportsStructuredOutput: true);

    public int EstimateTokens(string text) => 10;

    public Task<AIChatCompletionResponse> CompleteChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Invocations++;
        throw new HttpRequestException($"Error de red conectando con https://api.proveedor.test/v1 - API Key {SecretInMessage}");
    }

    public async IAsyncEnumerable<AIChatCompletionChunk> StreamChatAsync(
        AIChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Invocations++;
        await Task.Yield();
        throw new HttpRequestException($"Error de streaming en upstream - API Key {SecretInMessage}");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>
/// Proveedor cuyo streaming emite algunos fragmentos y luego falla a mitad de la transmisión.
/// </summary>
internal sealed class MidStreamFailingAIProvider : IAIProvider
{
    public string ProviderId => "midstream-failing-provider";

    public AIProviderCapabilities Capabilities => new(
        MaxContextTokens: 16384,
        MaxOutputTokens: 4096,
        SupportsStreaming: true,
        SupportsSystemPrompt: true,
        SupportedModels: ["mock-midstream"],
        SupportsStructuredOutput: true);

    public int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    public Task<AIChatCompletionResponse> CompleteChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<AIChatCompletionChunk> StreamChatAsync(
        AIChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AIChatCompletionChunk("Primer fragmento");
        yield return new AIChatCompletionChunk(" segundo fragmento");
        await Task.Yield();
        throw new HttpRequestException($"Conexión reiniciada por el proveedor - API Key {ThrowingAIProvider.SecretInMessage}");
    }
}

/// <summary>
/// Envuelve al MockAIProvider y registra cada petición que realmente llega al proveedor.
/// </summary>
internal sealed class RecordingAIProvider : IAIProvider
{
    private readonly MockAIProvider _inner = new(NullLogger<MockAIProvider>.Instance, simulateLatencyMs: 0);

    public List<AIChatCompletionRequest> Requests { get; } = [];

    public string ProviderId => _inner.ProviderId;
    public AIProviderCapabilities Capabilities => _inner.Capabilities;
    public int EstimateTokens(string text) => _inner.EstimateTokens(text);

    public Task<AIChatCompletionResponse> CompleteChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return _inner.CompleteChatAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<AIChatCompletionChunk> StreamChatAsync(AIChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return _inner.StreamChatAsync(request, cancellationToken);
    }

    /// <summary>Todo el texto enviado al proveedor (system prompt + mensajes).</summary>
    public string AllSentText() =>
        string.Join("\n", Requests.SelectMany(r => new[] { r.SystemPrompt ?? string.Empty }.Concat(r.Messages.Select(m => m.Content))));
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;

    public int Calls { get; private set; }
    public bool CancellationObserved { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        try
        {
            return await _handler(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancellationObserved = true;
            throw;
        }
    }

    public static HttpResponseMessage OpenAiCompletion(string content) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 12, completion_tokens = 7, total_tokens = 19 }
        }), Encoding.UTF8, "application/json")
    };
}

/// <summary>
/// Stream que entrega un contenido inicial y luego queda en espera hasta que se cancele la lectura.
/// Simula un proveedor que deja de enviar fragmentos a mitad de un streaming.
/// </summary>
internal sealed class StallingStream : Stream
{
    private readonly byte[] _initial;
    private int _position;

    public StallingStream(string initialContent) => _initial = Encoding.UTF8.GetBytes(initialContent);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _initial.Length)
        {
            var count = Math.Min(buffer.Length, _initial.Length - _position);
            _initial.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Host real de la API (pipeline completo: autenticación, rate limiting, middleware de excepciones)
/// contra PostgreSQL 16, con la posibilidad de sustituir servicios para simular al proveedor de IA.
/// </summary>
internal sealed class AiApiFactory : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? _configureServices;

    public AiApiFactory(Action<IServiceCollection>? configureServices = null) => _configureServices = configureServices;

    public Guid TenantId { get; } = Guid.NewGuid();
    public string TenantSlug { get; } = $"fase62-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services => _configureServices?.Invoke(services));
    }

    public static Action<IServiceCollection> WithProvider(IAIProvider provider) => services =>
    {
        services.RemoveAll<IAIProvider>();
        services.AddScoped(_ => provider);
    };

    /// <summary>
    /// Usa el OpenAICompatibleProvider real con su pipeline de HttpClient, pero con un transporte simulado.
    /// </summary>
    public static Action<IServiceCollection> WithOpenAICompatibleProvider(HttpMessageHandler transport, Action<OpenAIOptions> configure) => services =>
    {
        services.Configure<OpenAIOptions>(options =>
        {
            options.BaseUrl = "http://ai-provider.test/v1";
            options.ApiKey = ThrowingAIProvider.SecretInMessage;
            options.ModelId = "modelo-de-prueba";
            configure(options);
        });
        services.AddHttpClient<OpenAICompatibleProvider>().ConfigurePrimaryHttpMessageHandler(() => transport);
        services.RemoveAll<IAIProvider>();
        services.AddScoped<IAIProvider>(sp => sp.GetRequiredService<OpenAICompatibleProvider>());
    };

    public void EnsureTenant() => EnsureTenant(TenantId, TenantSlug);

    private void EnsureTenant(Guid tenantId, string slug)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (db.Tenants.Any(t => t.Id == tenantId))
        {
            return;
        }

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Nombre = "Estudio Jurídico Fase 6.2 Test",
            IdentificadorUrl = slug,
            Ruc = "1790016919001",
            Activo = true
        });
        db.SaveChanges();
    }

    /// <summary>Crea un tenant adicional (distinto del tenant principal del host) y devuelve su identidad.</summary>
    public (Guid TenantId, string TenantSlug) CreateAdditionalTenant()
    {
        var tenant = (Guid.NewGuid(), $"fase62-{Guid.NewGuid():N}");
        EnsureTenant(tenant.Item1, tenant.Item2);
        return tenant;
    }

    /// <summary>Crea el usuario en el tenant de prueba y devuelve un JWT válido para él.</summary>
    public string CreateUserToken(Guid userId, string role) => CreateUserToken(userId, role, TenantId, TenantSlug);

    /// <summary>Crea el usuario en el tenant indicado y devuelve un JWT válido para él.</summary>
    public string CreateUserToken(Guid userId, string role, Guid tenantId, string tenantSlug)
    {
        EnsureTenant(tenantId, tenantSlug);

        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Usuarios.Add(new Usuario
            {
                Id = userId,
                TenantId = tenantId,
                UserName = $"usuario_{userId:N}@fase62.test",
                Email = $"usuario_{userId:N}@fase62.test",
                NombreCompleto = $"Usuario {role}",
                Rol = role,
                Activo = true
            });
            db.SaveChanges();
        }

        var tokenService = new TokenService(Services.GetRequiredService<IConfiguration>());
        var tenant = new Tenant { Id = tenantId, IdentificadorUrl = tenantSlug, Nombre = "Estudio Jurídico Fase 6.2 Test" };
        var user = new Usuario
        {
            Id = userId,
            Email = $"usuario_{userId:N}@fase62.test",
            NombreCompleto = $"Usuario {role}",
            Rol = role,
            TenantId = tenantId
        };

        var (token, _) = tokenService.GenerateAccessToken(user, tenant, role, Permissions.GetPermissionsForRole(role).ToList());
        return token;
    }

    public (Guid ExpedienteId, Guid DocumentoId) SeedExpedienteConDocumento(Guid abogadoResponsableId)
    {
        EnsureTenant();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            // Única por tenant (índice IX_clientes_TenantId_Identificacion)
            Identificacion = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString(),
            NombreRazonSocial = "Compañía de Pruebas F6.2 S.A.",
            TipoIdentificacion = TipoIdentificacion.Ruc,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Clientes.Add(cliente);

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            NumeroExpediente = "EXP-F62-" + Guid.NewGuid().ToString("N")[..6],
            Titulo = "Litigio de Prueba F6.2",
            Materia = "Civil",
            Estado = EstadoExpediente.Abierto,
            ClienteId = cliente.Id,
            AbogadoResponsableId = abogadoResponsableId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Expedientes.Add(expediente);

        var documento = new Documento
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            ExpedienteId = expediente.Id,
            Titulo = "Contrato de Prueba.pdf",
            TipoDocumento = "Contrato",
            RutaAlmacenamiento = "tenant/inexistente.pdf",
            ContentType = "application/pdf",
            EstadoIa = EstadoProcesamientoIa.Pendiente,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Documentos.Add(documento);

        db.SaveChanges();
        return (expediente.Id, documento.Id);
    }

    public HttpRequestMessage Request(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        // Sin cabecera X-Tenant-ID: el tenant se toma exclusivamente del JWT
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        return request;
    }

    public async Task<T> QueryDbAsync<T>(Func<ApplicationDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await query(db);
    }
}
