using System.Net;
using System.Reflection;
using System.Text;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Persistence.Interceptors;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Polly;

namespace AsistenteJuridico.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionString 'DefaultConnection' no configurada.");

        services.AddScoped<AuditableEntityInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var interceptor = sp.GetRequiredService<AuditableEntityInterceptor>();
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorCodesToAdd: null);
            })
            .AddInterceptors(interceptor);
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<IProcesoJudicialProvider, MockProcesoJudicialProvider>();

        // ──────────────────────────────────────────────────────────
        // ASP.NET CORE IDENTITY
        // ──────────────────────────────────────────────────────────
        services.AddIdentity<Usuario, ApplicationRole>(options =>
        {
            // Políticas de contraseña robustas
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.Password.RequiredLength = 8;
            options.Password.RequiredUniqueChars = 1;

            // Bloqueo temporal automático (Lockout)
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            // En multi-tenant la unicidad se controla por el índice compuesto (TenantId, NormalizedEmail)
            options.User.RequireUniqueEmail = false;
            options.SignIn.RequireConfirmedEmail = false;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        // ──────────────────────────────────────────────────────────
        // AUTENTICACIÓN JWT BEARER
        // ──────────────────────────────────────────────────────────
        // La clave de firma se proporciona SIEMPRE fuera del repositorio (variable de entorno Jwt__Key o
        // dotnet user-secrets en desarrollo). No existe valor por defecto: sin clave la API no arranca.
        var secretKey = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                "Falta la clave de firma JWT 'Jwt:Key'. Configúrela fuera del repositorio mediante la variable de " +
                "entorno 'Jwt__Key' o, en desarrollo, con 'dotnet user-secrets set \"Jwt:Key\" <clave>'.");
        }
        if (secretKey.Contains("CAMBIAR_EN_PRODUCCION", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "La clave 'Jwt:Key' contiene el valor de ejemplo de .env.example. Genere una clave aleatoria propia.");
        }
        if (Encoding.UTF8.GetByteCount(secretKey) < 32)
        {
            throw new InvalidOperationException(
                "La clave 'Jwt:Key' debe tener al menos 32 bytes (256 bits) para HMAC-SHA256.");
        }

        var issuer = configuration["Jwt:Issuer"] ?? "AsistenteJuridicoIA";
        var audience = configuration["Jwt:Audience"] ?? "AsistenteJuridicoClients";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false; // permitido en localhost
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero // TOLERANCIA CERO: Expiración estricta
            };
        });

        // ──────────────────────────────────────────────────────────
        // POLÍTICAS DE AUTORIZACIÓN (PBAC / RBAC)
        // ──────────────────────────────────────────────────────────
        services.AddAuthorization(options =>
        {
            // Registrar dinámicamente una política para cada permiso de Permissions
            var permissionFields = typeof(Permissions)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            foreach (var field in permissionFields)
            {
                if (field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
                {
                    var permission = (string)field.GetValue(null)!;
                    options.AddPolicy(permission, policy =>
                        policy.RequireClaim("permission", permission));
                }
            }

            options.AddPolicy("SuperAdminPolicy", policy => policy.RequireRole(Roles.SuperAdmin));
            options.AddPolicy("AdminEstudioPolicy", policy => policy.RequireRole(Roles.AdminEstudio, Roles.SuperAdmin));
        });

        // ──────────────────────────────────────────────────────────
        // SERVICIOS DE SEGURIDAD Y TOKENS
        // ──────────────────────────────────────────────────────────
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IEmailSender, DevEmailSender>();

        // ──────────────────────────────────────────────────────────
        // SERVICIOS FASE 4: GESTIÓN JURÍDICA Y STORAGE
        // ──────────────────────────────────────────────────────────
        services.AddScoped<IExpedienteCodeGenerator, ExpedienteCodeGenerator>();
        services.AddScoped<IFileStorageService, FileStorageService>();
        services.AddScoped<IExpedienteAccessService, ExpedienteAccessService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IClienteService, ClienteService>();
        services.AddScoped<IExpedienteService, ExpedienteService>();
        services.AddScoped<IProcesoJudicialService, ProcesoJudicialService>();
        services.AddScoped<ITareaService, TareaService>();
        services.AddScoped<IAudienciaService, AudienciaService>();
        services.AddScoped<IDocumentoService, DocumentoService>();

        // ──────────────────────────────────────────────────────────
        // SERVICIOS FASE 5: DASHBOARD, AGENDA Y ALERTAS
        // ──────────────────────────────────────────────────────────
        services.AddScoped<IAlertasService, AlertasService>();
        services.AddScoped<IAgendaService, AgendaService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddHostedService<AsistenteJuridico.Infrastructure.BackgroundServices.AlertasBackgroundService>();

        // ──────────────────────────────────────────────────────────
        // SERVICIOS FASE 6: ASISTENTE JURÍDICO IA
        // ──────────────────────────────────────────────────────────
        services.Configure<AsistenteJuridico.Infrastructure.Services.AI.OpenAIOptions>(
            configuration.GetSection(AsistenteJuridico.Infrastructure.Services.AI.OpenAIOptions.SectionName));

        // El tiempo máximo por petición lo gobierna OpenAIOptions.TimeoutSeconds dentro del proveedor
        // (incluye reintentos), por eso se desactiva el timeout propio de HttpClient.
        // Reintentos según v1.1.1 §18: backoff exponencial con jitter, solo ante errores transitorios
        // de red o códigos 429/503; nunca ante 400/401/403/404/409/422.
        services.AddHttpClient<AsistenteJuridico.Infrastructure.Services.AI.OpenAICompatibleProvider>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddResilienceHandler("ai-provider-retry", (builder, context) =>
            {
                var aiOptions = context.ServiceProvider
                    .GetRequiredService<IOptions<AsistenteJuridico.Infrastructure.Services.AI.OpenAIOptions>>().Value;

                if (aiOptions.MaxRetries <= 0)
                {
                    return;
                }

                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = aiOptions.MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(aiOptions.RetryBaseDelaySeconds),
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException
                        || args.Outcome.Result?.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                });
            });

        services.AddScoped<AsistenteJuridico.Infrastructure.Services.AI.MockAIProvider>();

        var aiProviderType = configuration["AI:Provider"] ?? "Mock";
        if (aiProviderType.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<AsistenteJuridico.Application.Common.Interfaces.AI.IAIProvider, AsistenteJuridico.Infrastructure.Services.AI.OpenAICompatibleProvider>();
        }
        else
        {
            services.AddScoped<AsistenteJuridico.Application.Common.Interfaces.AI.IAIProvider, AsistenteJuridico.Infrastructure.Services.AI.MockAIProvider>();
        }

        services.AddScoped<AsistenteJuridico.Application.Features.AI.Interfaces.IAIService, AIService>();

        // ──────────────────────────────────────────────────────────
        // FASE 6.X: EXTRACCIÓN DE TEXTO (H10) Y RECUPERACIÓN DE PROCESANDO (X1)
        // ──────────────────────────────────────────────────────────
        services.Configure<AsistenteJuridico.Infrastructure.Services.AI.DocumentTextExtractionOptions>(
            configuration.GetSection(AsistenteJuridico.Infrastructure.Services.AI.DocumentTextExtractionOptions.SectionName));
        services.AddScoped<AsistenteJuridico.Application.Common.Interfaces.AI.IDocumentTextExtractor,
            AsistenteJuridico.Infrastructure.Services.AI.DocumentTextExtractor>();

        // DA-3: el lease debe superar la suma de los timeouts explícitos del proveedor y de la extracción; si no,
        // la aplicación no arranca.
        services.AddOptions<AsistenteJuridico.Infrastructure.BackgroundServices.ProcesamientoIaRecoveryOptions>()
            .Bind(configuration.GetSection(AsistenteJuridico.Infrastructure.BackgroundServices.ProcesamientoIaRecoveryOptions.SectionName))
            .Validate<IOptions<AsistenteJuridico.Infrastructure.Services.AI.OpenAIOptions>, IOptions<AsistenteJuridico.Infrastructure.Services.AI.DocumentTextExtractionOptions>>(
                (recuperacion, proveedor, extraccion) => AsistenteJuridico.Infrastructure.BackgroundServices.ProcesamientoIaRecoveryOptions.LeaseEsValido(
                    recuperacion.LeaseSeconds, proveedor.Value.TimeoutSeconds, extraccion.Value.TimeoutSeconds),
                "AI:ProcessingRecovery:LeaseSeconds debe ser mayor que AI:OpenAICompatible:TimeoutSeconds + AI:Extraction:TimeoutSeconds.")
            .ValidateOnStart();
        services.AddHostedService<AsistenteJuridico.Infrastructure.BackgroundServices.ProcesamientoIaRecoveryBackgroundService>();

        return services;
    }
}
