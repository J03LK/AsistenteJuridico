using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AsistenteJuridico.API.Middleware;

/// <summary>
/// Middleware de manejo global de excepciones.
/// Captura todas las excepciones no controladas y las convierte
/// en respuestas HTTP estructuradas con el envelope ApiResponse.
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, response) = exception switch
        {
            NotFoundException nfe => (
                StatusCodes.Status404NotFound,
                ApiResponse<object>.Fail(nfe.Message, ErroresConCodigo(nfe))
            ),
            UnauthorizedException ue => (
                StatusCodes.Status401Unauthorized,
                ApiResponse<object>.Fail(ue.Message)
            ),
            UserLockedException ule => (
                StatusCodes.Status423Locked,
                ApiResponse<object>.Fail(ule.Message)
            ),
            TenantMismatchException tme => (
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail(tme.Message)
            ),
            ForbiddenException fe => (
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail(fe.Message, ErroresConCodigo(fe))
            ),
            ConflictException ce => (
                StatusCodes.Status409Conflict,
                ApiResponse<object>.Fail(ce.Message, ErroresConCodigo(ce))
            ),
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                ApiResponse<object>.Fail("Conflicto de concurrencia optimista: El recurso fue modificado por otra transacción. Por favor recargue y vuelva a intentar.")
            ),
            Microsoft.EntityFrameworkCore.DbUpdateException dbEx when dbEx.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505" => (
                StatusCodes.Status409Conflict,
                ApiResponse<object>.Fail("Conflicto de unicidad: Ya existe un registro con los mismos datos clave o restricción única violada.")
            ),
            // Fase 6.X (DA-7): aditivo; sin ErrorCode la respuesta es la de siempre (errors: []).
            BusinessRuleException bre => (
                StatusCodes.Status422UnprocessableEntity,
                ApiResponse<object>.Fail(bre.Message, ErroresConCodigo(bre))
            ),
            TenantTimeZoneInvalidException itze => (
                StatusCodes.Status422UnprocessableEntity,
                ApiResponse<object>.Fail(itze.Message)
            ),
            ValidationException ve => (
                StatusCodes.Status400BadRequest,
                ApiResponse<object>.Fail(ve.Message, ErroresDeValidacion(ve))
            ),
            PayloadTooLargeException ptle => (
                StatusCodes.Status413PayloadTooLarge,
                ApiResponse<object>.Fail(ptle.Message, ErroresConCodigo(ptle))
            ),
            UnsupportedMediaTypeException umte => (
                StatusCodes.Status415UnsupportedMediaType,
                ApiResponse<object>.Fail(umte.Message, ErroresConCodigo(umte))
            ),
            // Fase 7.2: exceso del límite de petición de Kestrel/formulario -> 413 con envelope (antes caía en 500).
            BadHttpRequestException bhre when bhre.StatusCode == StatusCodes.Status413PayloadTooLarge => (
                StatusCodes.Status413PayloadTooLarge,
                ApiResponse<object>.Fail(
                    "La solicitud excede el tamaño máximo permitido.",
                    EsSubidaDeDocumento(context) ? [DocumentoErrorCodes.SizeExceeded] : null)
            ),
            AIProviderException ape => (
                StatusCodes.Status502BadGateway,
                ApiResponse<object>.Fail(ape.Message, [ape.ErrorCode])
            ),
            TooManyRequestsException tmre => (
                StatusCodes.Status429TooManyRequests,
                ApiResponse<object>.Fail(tmre.Message)
            ),
            UnauthorizedAccessException uae => (
                StatusCodes.Status401Unauthorized,
                ApiResponse<object>.Fail(uae.Message)
            ),
            _ => (
                StatusCodes.Status500InternalServerError,
                ApiResponse<object>.Fail("Ocurrió un error interno del servidor.")
            )
        };

        // Loguear excepciones no controladas con nivel Error
        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception,
                "Error no controlado en {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
        }
        else
        {
            _logger.LogWarning(exception,
                "Error de dominio [{StatusCode}] en {Method} {Path}: {Message}",
                statusCode,
                context.Request.Method,
                context.Request.Path,
                exception.Message);
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Fase 7: añade el ErrorCode a <c>errors</c> solo si la excepción lo trae; sin código, la respuesta es la de
    /// siempre (<c>errors: []</c>).
    /// </summary>
    private static IEnumerable<string>? ErroresConCodigo(DomainException exception) =>
        string.IsNullOrEmpty(exception.ErrorCode) ? null : [exception.ErrorCode];

    /// <summary>
    /// Fase 7.2: con código, <c>errors: [código, ...mensajes]</c>; sin código, exactamente los mensajes de siempre.
    /// </summary>
    private static IEnumerable<string> ErroresDeValidacion(ValidationException exception) =>
        string.IsNullOrEmpty(exception.ErrorCode)
            ? exception.ValidationErrors
            : [exception.ErrorCode, .. exception.ValidationErrors];

    private static bool EsSubidaDeDocumento(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api/v1/documentos/upload", StringComparison.OrdinalIgnoreCase);
}
