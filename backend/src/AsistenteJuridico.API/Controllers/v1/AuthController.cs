using System.Security.Claims;
using System.Threading.RateLimiting;
using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Features.Auth.DTOs;
using AsistenteJuridico.Application.Features.Auth.Validators;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AsistenteJuridico.API.Controllers.v1;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService _authService,
        IAntiforgery _antiforgery,
        ILogger<AuthController> _logger)
    {
        this._authService = _authService;
        this._antiforgery = _antiforgery;
        this._logger = _logger;
    }

    /// <summary>
    /// Inicia sesión con correo, contraseña y slug del estudio jurídico.
    /// Emite Access Token en el cuerpo y Refresh Token en Cookie HttpOnly + Secure.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("AuthRateLimit")]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status423Locked)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var validator = new LoginRequestValidator();
        var validationResult = await validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var ipAddress = GetClientIpAddress();
        var (response, rawRefreshToken) = await _authService.LoginAsync(request, ipAddress, ct);

        // Adjuntar Refresh Token en Cookie HttpOnly
        SetRefreshTokenCookie(rawRefreshToken);

        // Adjuntar Token CSRF legible para Angular
        SetCsrfCookie();

        return Ok(ApiResponse<LoginResponse>.Ok(response, "Inicio de sesión exitoso."));
    }

    /// <summary>
    /// Rota el Refresh Token recibido en Cookie HttpOnly y emite un nuevo Access Token.
    /// No requiere Access Token expirado. Detección de Replay Attack activa.
    /// </summary>
    [HttpPost("refresh-token")]
    [ProducesResponseType(typeof(ApiResponse<RefreshTokenResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenBodyDto? body, CancellationToken ct)
    {
        // Extraer token prioritariamente de Cookie HttpOnly (o fallback en body para clientes no web)
        var rawRefreshToken = Request.Cookies["refreshToken"] ?? body?.RefreshToken;

        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new UnauthorizedException("No se proporcionó token de refresco en la cookie o cuerpo de la petición.");
        }

        var ipAddress = GetClientIpAddress();
        var (response, newRawRefreshToken) = await _authService.RefreshTokenAsync(rawRefreshToken, ipAddress, ct);

        SetRefreshTokenCookie(newRawRefreshToken);
        SetCsrfCookie();

        return Ok(ApiResponse<RefreshTokenResponse>.Ok(response, "Token renovado exitosamente."));
    }

    /// <summary>
    /// Cierra la sesión activa actual, revoca el Refresh Token en base de datos y expira la cookie.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var rawRefreshToken = Request.Cookies["refreshToken"];
        var ipAddress = GetClientIpAddress();

        await _authService.LogoutAsync(userId, rawRefreshToken, ipAddress, ct);

        ExpireRefreshTokenCookie();

        return Ok(ApiResponse<string>.Ok("Sesión finalizada exitosamente.", "Logout exitoso."));
    }

    /// <summary>
    /// Revoca todas las sesiones activas del usuario en todos los dispositivos.
    /// </summary>
    [HttpPost("revoke-all")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RevokeAll(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var ipAddress = GetClientIpAddress();

        await _authService.RevokeAllSessionsAsync(userId, ipAddress, ct);

        ExpireRefreshTokenCookie();

        return Ok(ApiResponse<string>.Ok("Todas las sesiones activas han sido invalidadas.", "Revocación total exitosa."));
    }

    /// <summary>
    /// Solicita un enlace de restablecimiento de contraseña. Siempre retorna HTTP 200 para mitigar enumeración.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("AuthRateLimit")]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        var validator = new ForgotPasswordRequestValidator();
        var validationResult = await validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        await _authService.ForgotPasswordAsync(request, ct);

        return Ok(ApiResponse<string>.Ok(
            "Si el correo electrónico se encuentra registrado en el estudio jurídico, se enviarán las instrucciones de restablecimiento.",
            "Solicitud procesada."));
    }

    /// <summary>
    /// Restablece la contraseña utilizando el token criptográfico recibido por correo.
    /// </summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var validator = new ResetPasswordRequestValidator();
        var validationResult = await validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var ipAddress = GetClientIpAddress();
        await _authService.ResetPasswordAsync(request, ipAddress, ct);

        return Ok(ApiResponse<string>.Ok(
            "Contraseña actualizada exitosamente. Inicie sesión con su nueva clave.",
            "Contraseña restablecida."));
    }

    /// <summary>
    /// Modifica la contraseña del usuario actualmente autenticado.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var validator = new ChangePasswordRequestValidator();
        var validationResult = await validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var userId = GetCurrentUserId();
        var ipAddress = GetClientIpAddress();

        await _authService.ChangePasswordAsync(userId, request, ipAddress, ct);

        return Ok(ApiResponse<string>.Ok("Contraseña cambiada exitosamente.", "Contraseña actualizada."));
    }

    /// <summary>
    /// Retorna los datos de identidad, roles, permisos y tenant del usuario autenticado.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var user = await _authService.GetCurrentUserProfileAsync(userId, ct);
        return Ok(ApiResponse<UserDto>.Ok(user, "Perfil de usuario obtenido correctamente."));
    }

    /// <summary>
    /// Endpoint para emitir o renovar la cookie XSRF-TOKEN requerida por Angular.
    /// </summary>
    [HttpGet("csrf-token")]
    public IActionResult GetCsrfToken()
    {
        SetCsrfCookie();
        return Ok(ApiResponse<string>.Ok("Token CSRF emitido.", "CSRF OK."));
    }

    // ──────────────────────────────────────────────────────────
    // MÉTODOS AUXILIARES DE SEGURIDAD Y COOKIES
    // ──────────────────────────────────────────────────────────

    private void SetRefreshTokenCookie(string token)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        };

        Response.Cookies.Append("refreshToken", token, cookieOptions);
    }

    private void ExpireRefreshTokenCookie()
    {
        Response.Cookies.Append("refreshToken", "", new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
            Expires = DateTimeOffset.UtcNow.AddDays(-1),
            MaxAge = TimeSpan.Zero
        });
    }

    private void SetCsrfCookie()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        if (tokens.RequestToken != null)
        {
            Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken, new CookieOptions
            {
                HttpOnly = false, // Angular debe leer esta cookie para armar el header X-XSRF-TOKEN
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
        }
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(sub, out var guid))
        {
            return guid;
        }

        throw new UnauthorizedException("Sesión no válida o identificador de usuario ausente.");
    }

    private string? GetClientIpAddress()
    {
        if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) && !string.IsNullOrWhiteSpace(forwardedFor))
        {
            return forwardedFor.ToString().Split(',')[0].Trim();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}

public record RefreshTokenBodyDto(string? RefreshToken);
