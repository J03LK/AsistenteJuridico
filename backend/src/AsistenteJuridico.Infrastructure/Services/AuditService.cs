using System.Text.Json;
using System.Text.Json.Serialization;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

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
    private readonly ILogger<AuditService> _logger;

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
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditService>? logger = null)
    {
        _context = context;
        _currentUserService = currentUserService;
        _currentTenantService = currentTenantService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger ?? NullLogger<AuditService>.Instance;
    }

    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordhash", "hash", "secret", "secreto",
        "token", "jwt", "refreshtoken", "refreshtokens",
        "securitystamp", "concurrencystamp", "binario", "base64",
        "key", "privatekey", "clientsecret"
    };

    /// <summary>
    /// Agrega la auditoría a la unidad de trabajo actual sin guardarla: el SaveChanges del servicio que controla la
    /// transacción la persiste junto con la operación, de modo que ambas se confirman o se revierten a la vez.
    /// </summary>
    public Task LogInTransactionAsync(
        string entidad,
        string entidadId,
        string accion,
        object? valoresAnteriores = null,
        object? valoresNuevos = null,
        CancellationToken cancellationToken = default)
    {
        var auditoria = CrearAuditoria(entidad, entidadId, accion, valoresAnteriores, valoresNuevos);
        if (auditoria != null)
        {
            _context.HistorialAuditorias.Add(auditoria);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Guarda la auditoría de una operación ya confirmada en un contexto propio, para no guardar ni dejar pendiente
    /// nada en el contexto compartido. Si falla, la operación no se revierte y queda constancia en el log técnico,
    /// sin los valores auditados.
    /// </summary>
    public async Task LogAsync(
        string entidad,
        string entidadId,
        string accion,
        object? valoresAnteriores = null,
        object? valoresNuevos = null,
        CancellationToken cancellationToken = default)
    {
        var auditoria = CrearAuditoria(entidad, entidadId, accion, valoresAnteriores, valoresNuevos);
        if (auditoria == null)
        {
            return;
        }

        try
        {
            // Mismas opciones que el contexto compartido (conexión, reintentos e interceptores).
            var opciones = (DbContextOptions<ApplicationDbContext>)_context.GetService<IDbContextOptions>();
            await using var contextoAuditoria = new ApplicationDbContext(opciones, _currentTenantService);
            contextoAuditoria.HistorialAuditorias.Add(auditoria);
            await contextoAuditoria.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "[AUDITORIA_NO_REGISTRADA] No se pudo guardar la auditoría {Accion} de {Entidad} {EntidadId} (tenant {TenantId}). Error: {TipoError}, SQLSTATE: {SqlState}.",
                accion, entidad, entidadId, auditoria.TenantId, DescribirTipoError(ex), ObtenerSqlState(ex) ?? "n/a");
        }
    }

    private HistorialAuditoria? CrearAuditoria(
        string entidad,
        string entidadId,
        string accion,
        object? valoresAnteriores,
        object? valoresNuevos)
    {
        var tenantId = _currentTenantService.TenantId ?? _currentUserService.TenantId;
        if (!tenantId.HasValue)
        {
            return null;
        }

        var ip = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

        var sanitizedAnteriores = SanitizeAuditData(valoresAnteriores);
        var sanitizedNuevos = SanitizeAuditData(valoresNuevos);

        return new HistorialAuditoria
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
    }

    /// <summary>Tipo de la excepción y de su causa más interna; nunca sus mensajes, que pueden incluir datos.</summary>
    private static string DescribirTipoError(Exception ex)
    {
        var interna = ex;
        while (interna.InnerException != null)
        {
            interna = interna.InnerException;
        }

        return interna == ex ? ex.GetType().Name : $"{ex.GetType().Name} -> {interna.GetType().Name}";
    }

    private static string? ObtenerSqlState(Exception ex)
    {
        for (var actual = ex; actual != null; actual = actual.InnerException)
        {
            if (actual is PostgresException pg)
            {
                return pg.SqlState;
            }
        }

        return null;
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
