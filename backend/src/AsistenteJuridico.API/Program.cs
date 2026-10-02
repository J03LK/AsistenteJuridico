using System.Threading.RateLimiting;
using AsistenteJuridico.API.Configuration;
using AsistenteJuridico.API.Middleware;
using AsistenteJuridico.Application;
using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Infrastructure;
using Scalar.AspNetCore;
using Serilog;

// ──────────────────────────────────────────────────────────────
// CONFIGURACIÓN DE SERILOG (antes de construir el host)
// ──────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Iniciando Asistente Jurídico IA API...");

    var builder = WebApplication.CreateBuilder(args);

    // ──────────────────────────────────────────────────────────
    // SERILOG como proveedor de logging
    // ──────────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "AsistenteJuridicoIA")
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .WriteTo.File(
                path: "logs/api-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7);
    });

    // ──────────────────────────────────────────────────────────
    // HTTP CONTEXT ACCESSOR & ANTIFORGERY
    // ──────────────────────────────────────────────────────────
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddAntiforgery(options =>
    {
        options.HeaderName = "X-XSRF-TOKEN";
        options.Cookie.Name = "XSRF-TOKEN";
        options.Cookie.HttpOnly = false; // Angular debe leerla para el header
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

    // ──────────────────────────────────────────────────────────
    // RATE LIMITING (.NET 10)
    // ──────────────────────────────────────────────────────────
    builder.Services.Configure<AIRateLimitOptions>(
        builder.Configuration.GetSection(AIRateLimitOptions.SectionName));

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Respuesta 429 con el mismo envelope ApiResponse que el resto de errores de la API
        options.OnRejected = async (context, cancellationToken) =>
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            response.ContentType = "application/json";

            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var body = ApiResponse<object>.Fail(
                "Se ha superado el límite de solicitudes permitido. Intente nuevamente más tarde.",
                ["TOO_MANY_REQUESTS"]);

            await response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(body, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                }),
                cancellationToken);
        };

        options.AddPolicy("AuthRateLimit", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));

        // Contrato Fase 6 — AIRateLimit: 15/minuto por usuario Y 60/minuto por tenant (AIRateLimitOptions).
        // Ambas cuotas coexisten y se identifican solo por claims del JWT (sub, tenant_id), nunca por IP.
        // Requiere que UseRateLimiter se ejecute después de UseAuthentication para conocer al usuario.
        // Las peticiones sin identidad no consumen cuota: la autorización las rechaza con 401.

        // 1. Cuota por usuario: política de endpoint "AIRateLimit"
        options.AddPolicy("AIRateLimit", httpContext =>
        {
            var aiRateLimit = GetAIRateLimitOptions(httpContext);
            var userId = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var tenantId = httpContext.User.FindFirst("tenant_id")?.Value;

            if (userId == null || tenantId == null)
            {
                return RateLimitPartition.GetNoLimiter("ai:sin-identidad");
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"ai:tenant:{tenantId}:user:{userId}",
                factory: _ => CreateAIWindow(aiRateLimit.UserPermitLimit, aiRateLimit.WindowSeconds));
        });

        // 2. Cuota por tenant: limitador global que solo actúa en endpoints con la política "AIRateLimit".
        // Se evalúa antes que la política de endpoint; si cualquiera de las dos se agota, la respuesta es 429.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var politica = httpContext.GetEndpoint()?.Metadata
                .GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName;
            var tenantId = httpContext.User.FindFirst("tenant_id")?.Value;

            if (politica != "AIRateLimit" || tenantId == null)
            {
                return RateLimitPartition.GetNoLimiter("ai:no-aplica");
            }

            var aiRateLimit = GetAIRateLimitOptions(httpContext);
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"ai:tenant:{tenantId}",
                factory: _ => CreateAIWindow(aiRateLimit.TenantPermitLimit, aiRateLimit.WindowSeconds));
        });

        static AIRateLimitOptions GetAIRateLimitOptions(HttpContext httpContext) =>
            httpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AIRateLimitOptions>>().Value;

        static FixedWindowRateLimiterOptions CreateAIWindow(int permitLimit, int windowSeconds) => new()
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        };
    });

    // ──────────────────────────────────────────────────────────
    // CONTROLADORES
    // ──────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.CamelCase;
        });

    // ──────────────────────────────────────────────────────────
    // OPENAPI / SCALAR
    // ──────────────────────────────────────────────────────────
    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, context, ct) =>
        {
            document.Info.Title = "Asistente Jurídico IA - API";
            document.Info.Version = "v1";
            document.Info.Description =
                "API REST del sistema Asistente Jurídico IA para el mercado ecuatoriano. " +
                "Proporciona gestión de expedientes, documentos y asistencia con inteligencia artificial.";
            return Task.CompletedTask;
        });
    });

    // ──────────────────────────────────────────────────────────
    // CAPAS DE APLICACIÓN E INFRAESTRUCTURA
    // ──────────────────────────────────────────────────────────
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructureServices(builder.Configuration);

    // ──────────────────────────────────────────────────────────
    // HEALTH CHECKS
    // ──────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AsistenteJuridico.Infrastructure.Persistence.ApplicationDbContext>(
            name: "dbcontext",
            tags: ["db", "efcore"])
        .AddNpgSql(
            connectionString: builder.Configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("ConnectionString 'DefaultConnection' no configurada."),
            name: "postgresql",
            tags: ["db", "postgresql"]);

    // ──────────────────────────────────────────────────────────
    // CORS — Desarrollo
    // ──────────────────────────────────────────────────────────
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? ["http://localhost:4200"];

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("DevelopmentCors", policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials(); // Obligatorio para cookies HttpOnly y SameSite
        });
    });

    // ──────────────────────────────────────────────────────────
    // BUILD DE LA APLICACIÓN
    // ──────────────────────────────────────────────────────────
    var app = builder.Build();

    // ──────────────────────────────────────────────────────────
    // PIPELINE DE MIDDLEWARE (Orden estricto de seguridad)
    // ──────────────────────────────────────────────────────────

    // 1. Manejo global de excepciones
    app.UseMiddleware<GlobalExceptionMiddleware>();

    // 2. Logging de peticiones HTTP
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} respondió {StatusCode} en {Elapsed:0.0000} ms";
    });

    // 3. OpenAPI / Scalar en desarrollo
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.Title = "Asistente Jurídico IA";
            options.Theme = ScalarTheme.Purple;
            options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
        });
    }

    // 4. HTTPS Redirection
    app.UseHttpsRedirection();

    // 5. CORS
    app.UseCors("DevelopmentCors");

    // 6. Autenticación (JWT Bearer)
    app.UseAuthentication();

    // 7. Rate Limiting (después de la autenticación: la cuota de IA se particiona por usuario y por tenant)
    app.UseRateLimiter();

    // 8. Seguridad Multi-Tenant (después de Authentication para leer claims del JWT)
    app.UseMiddleware<TenantSecurityMiddleware>();

    // 9. Autorización (Roles y Políticas PBAC)
    app.UseAuthorization();

    // 10. Controladores de la API
    app.MapControllers();

    // 11. Health Checks
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/api/v1/health/detailed", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description
                })
            });
            await context.Response.WriteAsync(result);
        }
    });

    Log.Information("API configurada. Ambiente: {Environment}", app.Environment.EnvironmentName);

    if (app.Environment.IsDevelopment())
    {
        await AsistenteJuridico.Infrastructure.Persistence.DatabaseSeeder.SeedDevelopmentDataAsync(app.Services);
    }

    await app.RunAsync();
}
catch (HostAbortedException)
{
    // Ocurre normalmente durante comandos de diseño de EF Core (dotnet ef migrations / database)
}
catch (Exception ex)
{
    Log.Fatal(ex, "La aplicación terminó inesperadamente.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
