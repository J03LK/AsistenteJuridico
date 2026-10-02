using System.Security.Claims;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Interfaces;

namespace AsistenteJuridico.API.Middleware;

/// <summary>
/// Middleware de seguridad multi-tenant.
/// Garantiza que el tenant de una petición autenticada provenga EXCLUSIVAMENTE del JWT verificado
/// y rechaza con HTTP 403 cualquier intento de discrepancia o manipulación de cabeceras.
/// </summary>
public class TenantSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantSecurityMiddleware> _logger;

    public TenantSecurityMiddleware(RequestDelegate next, ILogger<TenantSecurityMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentTenantService currentTenantService)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantClaim = context.User.FindFirst("tenant_id")?.Value;

            if (string.IsNullOrWhiteSpace(tenantClaim) || !Guid.TryParse(tenantClaim, out var jwtTenantId) || jwtTenantId == Guid.Empty)
            {
                _logger.LogWarning("Petición autenticada sin claim 'tenant_id' válido. Rechazando.");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    status = "Fail",
                    message = "El token de autenticación no contiene un identificador de tenant válido."
                }));
                return;
            }

            // Inyectar el tenant verificado del JWT como única fuente de verdad
            currentTenantService.SetTenantId(jwtTenantId);

            // Validar si el cliente envió cabecera X-Tenant-ID y verificar discrepancias
            if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var headerTenantIdStr) &&
                !string.IsNullOrWhiteSpace(headerTenantIdStr))
            {
                if (!Guid.TryParse(headerTenantIdStr, out var headerTenantId) || headerTenantId != jwtTenantId)
                {
                    var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "Desconocido";
                    _logger.LogCritical(
                        "VIOLACIÓN DE SEGURIDAD MULTI-TENANT: Usuario {UserId} con Token para Tenant {JwtTenantId} " +
                        "intentó manipular cabecera X-Tenant-ID: {HeaderTenantId} en {Path}",
                        userId, jwtTenantId, headerTenantIdStr, context.Request.Path);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        status = "Fail",
                        message = "Violación de seguridad: El tenant especificado en la cabecera no coincide con su sesión autenticada."
                    }));
                    return;
                }
            }

            // Validar si el cliente envió cabecera X-Tenant-Slug
            if (context.Request.Headers.TryGetValue("X-Tenant-Slug", out var headerTenantSlugStr) &&
                !string.IsNullOrWhiteSpace(headerTenantSlugStr))
            {
                var jwtTenantSlug = context.User.FindFirst("tenant_slug")?.Value;
                if (!string.Equals(headerTenantSlugStr, jwtTenantSlug, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogCritical(
                        "VIOLACIÓN DE SEGURIDAD MULTI-TENANT: Tenant slug discrepante: JWT={JwtSlug}, Header={HeaderSlug}",
                        jwtTenantSlug, headerTenantSlugStr);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        status = "Fail",
                        message = "Violación de seguridad: El slug del estudio jurídico no coincide con su sesión autenticada."
                    }));
                    return;
                }
            }
        }

        await _next(context);
    }
}
