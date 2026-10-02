using System.Text.Json;
using System.Text.Json.Serialization;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Implementación de IAuditService.
/// Registra trazas de auditoría persistidas con omisión estricta de datos sensibles
/// (contraseñas, tokens, secretos y payloads binarios).
/// </summary>
public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public AuditService(
        ApplicationDbContext context,
        ICurrentUserService currentUserService,
        ICurrentTenantService currentTenantService,
        IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _currentUserService = currentUserService;
        _currentTenantService = currentTenantService;
        _httpContextAccessor = httpContextAccessor;
    }

    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordhash", "hash", "secret", "secreto",
        "token", "jwt", "refreshtoken", "refreshtokens",
        "securitystamp", "concurrencystamp", "binario", "base64",
        "key", "privatekey", "clientsecret"
    };

    public async Task LogAsync(
        string entidad,
        string entidadId,
        string accion,
        object? valoresAnteriores = null,
        object? valoresNuevos = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _currentTenantService.TenantId ?? _currentUserService.TenantId;
        if (!tenantId.HasValue)
        {
            return;
        }

        var ip = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

        var sanitizedAnteriores = SanitizeAuditData(valoresAnteriores);
        var sanitizedNuevos = SanitizeAuditData(valoresNuevos);

        var auditoria = new HistorialAuditoria
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId.Value,
            Entidad = entidad,
            EntidadId = entidadId,
            Accion = accion,
            UsuarioId = _currentUserService.UserId,
            UsuarioEmail = _currentUserService.Email,
            IpAddress = ip,
            Fecha = DateTime.UtcNow,
            ValoresAnterioresJson = sanitizedAnteriores != null ? JsonSerializer.Serialize(sanitizedAnteriores, JsonOptions) : null,
            ValoresNuevosJson = sanitizedNuevos != null ? JsonSerializer.Serialize(sanitizedNuevos, JsonOptions) : null
        };

        _context.HistorialAuditorias.Add(auditoria);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // La auditoría no debe bloquear el flujo de la aplicación si ocurre un error transitorio
        }
    }

    private static object? SanitizeAuditData(object? obj)
    {
        if (obj == null) return null;
        try
        {
            var jsonString = JsonSerializer.Serialize(obj, JsonOptions);
            using var doc = JsonDocument.Parse(jsonString);
            return SanitizeElement(doc.RootElement);
        }
        catch
        {
            return "[UNSERIALIZABLE]";
        }
    }

    private static object? SanitizeElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsSensitiveKey(prop.Name))
                    {
                        dict[prop.Name] = "[REDACTED]";
                    }
                    else
                    {
                        dict[prop.Name] = SanitizeElement(prop.Value);
                    }
                }
                return dict;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(SanitizeElement(item));
                }
                return list;

            case JsonValueKind.String:
                var str = element.GetString();
                if (str != null && (str.StartsWith("eyJ") || str.Length > 2000))
                {
                    return "[REDACTED_PAYLOAD]";
                }
                return str;

            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDouble();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
            default:
                return null;
        }
    }

    private static bool IsSensitiveKey(string name)
    {
        foreach (var keyword in SensitiveKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
