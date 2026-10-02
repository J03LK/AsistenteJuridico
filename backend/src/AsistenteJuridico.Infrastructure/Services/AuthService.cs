using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Auth.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly UserManager<Usuario> _userManager;
    private readonly SignInManager<Usuario> _signInManager;
    private readonly ApplicationDbContext _context;
    private readonly ITokenService _tokenService;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<Usuario> userManager,
        SignInManager<Usuario> signInManager,
        ApplicationDbContext context,
        ITokenService tokenService,
        IEmailSender emailSender,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _context = context;
        _tokenService = tokenService;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<(LoginResponse Response, string RawRefreshToken)> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken ct = default)
    {
        // 1. Validar Tenant
        var tenant = await _context.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IdentificadorUrl == request.TenantSlug, ct);

        if (tenant == null || !tenant.Activo)
        {
            _logger.LogWarning("Intento de login en estudio jurídico inexistente o inactivo: {Slug}", request.TenantSlug);
            throw new ForbiddenException("El estudio jurídico especificado no existe o se encuentra inactivo.");
        }

        // 2. Buscar Usuario dentro del Tenant
        var normalizedEmail = request.Email.ToUpperInvariant();
        var user = await _userManager.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == normalizedEmail && !u.IsDeleted, ct);

        if (user == null || !user.Activo)
        {
            _logger.LogWarning("Intento de login con usuario inexistente o inactivo: {Email} en Tenant {TenantId}", request.Email, tenant.Id);
            throw new UnauthorizedException("Credenciales incorrectas.");
        }

        // 3. Verificar si el usuario se encuentra bloqueado
        if (await _userManager.IsLockedOutAsync(user))
        {
            _logger.LogWarning("Usuario {UserId} bloqueado temporalmente por intentos fallidos", user.Id);
            await RegistrarAuditoriaAsync(tenant.Id, user.Id, user.Email, "LockoutAttempt", ipAddress, ct);
            throw new UserLockedException();
        }

        // 4. Verificar contraseña con SignInManager (gestiona lockout automáticamente)
        var signInResult = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (signInResult.IsLockedOut)
        {
            _logger.LogWarning("Usuario {UserId} alcanzó el límite de intentos fallidos. Bloqueado.", user.Id);
            await RegistrarAuditoriaAsync(tenant.Id, user.Id, user.Email, "LockoutTriggered", ipAddress, ct);
            throw new UserLockedException();
        }

        if (!signInResult.Succeeded)
        {
            _logger.LogWarning("Contraseña incorrecta para usuario {UserId}", user.Id);
            await RegistrarAuditoriaAsync(tenant.Id, user.Id, user.Email, "LoginFailed", ipAddress, ct);
            throw new UnauthorizedException("Credenciales incorrectas.");
        }

        // 5. Resetear contador de fallos
        await _userManager.ResetAccessFailedCountAsync(user);

        // 6. Generar Tokens
        var role = user.Rol;
        var permissions = Permissions.GetPermissionsForRole(role);

        var (accessToken, expiresIn) = _tokenService.GenerateAccessToken(user, tenant, role, permissions);
        var (rawRefreshToken, refreshTokenEntity) = _tokenService.GenerateRefreshToken(
            user.Id,
            tenant.Id,
            user.SecurityStamp ?? string.Empty,
            ipAddress);

        _context.RefreshTokens.Add(refreshTokenEntity);
        await RegistrarAuditoriaAsync(tenant.Id, user.Id, user.Email, "LoginSuccess", ipAddress, ct);
        await _context.SaveChangesAsync(ct);

        var userDto = new UserDto(
            user.Id,
            user.Email ?? string.Empty,
            user.NombreCompleto,
            user.Rol,
            tenant.Id,
            tenant.IdentificadorUrl,
            tenant.Nombre,
            permissions
        );

        var response = new LoginResponse(accessToken, expiresIn, "Bearer", userDto);
        return (response, rawRefreshToken);
    }

    public async Task<(RefreshTokenResponse Response, string RawRefreshToken)> RefreshTokenAsync(string rawRefreshToken, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new UnauthorizedException("Token de refresco requerido.");
        }

        var tokenHash = _tokenService.HashToken(rawRefreshToken);

        var refreshToken = await _context.RefreshTokens
            .IgnoreQueryFilters()
            .Include(rt => rt.User)
            .Include(rt => rt.Tenant)
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, ct);

        if (refreshToken == null)
        {
            _logger.LogWarning("Token de refresco no encontrado");
            throw new UnauthorizedException("Sesión no válida o expirada.");
        }

        // 1. Validar Expiración
        if (refreshToken.IsExpired)
        {
            _logger.LogInformation("Token de refresco expirado para usuario {UserId}", refreshToken.UserId);
            throw new UnauthorizedException("La sesión ha expirado. Por favor, inicie sesión nuevamente.");
        }

        // 2. Validar Estado de Revocación y DETECCIÓN DE REPLAY ATTACK
        if (refreshToken.IsRevoked)
        {
            _logger.LogCritical("ALERTA DE SEGURIDAD: Replay attack detectado para RefreshToken FamilyId {FamilyId}, Usuario {UserId}",
                refreshToken.FamilyId, refreshToken.UserId);

            // Revocación masiva de toda la familia de tokens
            var activeFamilyTokens = await _context.RefreshTokens
                .IgnoreQueryFilters()
                .Where(rt => rt.FamilyId == refreshToken.FamilyId && !rt.IsRevoked)
                .ToListAsync(ct);

            foreach (var token in activeFamilyTokens)
            {
                token.IsRevoked = true;
                token.RevokedAt = DateTime.UtcNow;
                token.RevokedByIp = ipAddress;
                token.ReasonRevoked = "Violación de seguridad: detección de reutilización (Replay Attack)";
            }

            await RegistrarAuditoriaAsync(
                refreshToken.TenantId,
                refreshToken.UserId,
                refreshToken.User?.Email,
                "RefreshTokenReplayDetected",
                ipAddress,
                ct,
                $"FamilyId: {refreshToken.FamilyId}");

            await _context.SaveChangesAsync(ct);
            throw new UnauthorizedException("Violación de seguridad detectada: reuso de token. Todas las sesiones activas han sido revocadas.");
        }

        // 3. Validar Usuario
        var user = refreshToken.User;
        if (user == null || !user.Activo || user.IsDeleted || (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow))
        {
            _logger.LogWarning("Usuario {UserId} asociado al token no existe, está inactivo o bloqueado", refreshToken.UserId);
            throw new UnauthorizedException("Usuario no válido o bloqueado.");
        }

        // 4. Validar Tenant
        var tenant = refreshToken.Tenant;
        if (tenant == null || !tenant.Activo || tenant.Id != user.TenantId)
        {
            _logger.LogWarning("Tenant {TenantId} del token inactivo o discrepante con usuario {UserId}", refreshToken.TenantId, user.Id);
            throw new ForbiddenException("El estudio jurídico se encuentra inactivo.");
        }

        // 5. Validar SecurityStamp (si cambió de contraseña o cerró sesiones globales)
        if (!string.Equals(user.SecurityStamp, refreshToken.SecurityStamp, StringComparison.Ordinal))
        {
            _logger.LogWarning("SecurityStamp desincronizado para usuario {UserId}. Sesión revocada.", user.Id);
            refreshToken.IsRevoked = true;
            refreshToken.RevokedAt = DateTime.UtcNow;
            refreshToken.ReasonRevoked = "SecurityStamp modificado (cambio de clave o revocación remota)";
            await _context.SaveChangesAsync(ct);
            throw new UnauthorizedException("La sesión ha sido invalidada. Inicie sesión nuevamente.");
        }

        // 6. Rotación del Token: Invalida el actual
        refreshToken.IsRevoked = true;
        refreshToken.RevokedAt = DateTime.UtcNow;
        refreshToken.RevokedByIp = ipAddress;
        refreshToken.ReasonRevoked = "Rotación de sesión exitosa";

        // 7. Generar Nuevo Par (hereda el FamilyId)
        var (newRawRefreshToken, newRefreshTokenEntity) = _tokenService.GenerateRefreshToken(
            user.Id,
            tenant.Id,
            user.SecurityStamp ?? string.Empty,
            ipAddress,
            refreshToken.FamilyId);

        refreshToken.ReplacedByTokenHash = newRefreshTokenEntity.TokenHash;
        _context.RefreshTokens.Add(newRefreshTokenEntity);

        // 8. Generar Nuevo Access Token
        var role = user.Rol;
        var permissions = Permissions.GetPermissionsForRole(role);
        var (newAccessToken, expiresIn) = _tokenService.GenerateAccessToken(user, tenant, role, permissions);

        await RegistrarAuditoriaAsync(tenant.Id, user.Id, user.Email, "RefreshTokenRotated", ipAddress, ct);
        await _context.SaveChangesAsync(ct);

        return (new RefreshTokenResponse(newAccessToken, expiresIn, "Bearer"), newRawRefreshToken);
    }

    public async Task LogoutAsync(Guid userId, string? rawRefreshToken, string? ipAddress, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            var tokenHash = _tokenService.HashToken(rawRefreshToken);
            var token = await _context.RefreshTokens
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash && rt.UserId == userId, ct);

            if (token != null && !token.IsRevoked)
            {
                token.IsRevoked = true;
                token.RevokedAt = DateTime.UtcNow;
                token.RevokedByIp = ipAddress;
                token.ReasonRevoked = "Logout voluntario de usuario";
            }
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user != null)
        {
            await RegistrarAuditoriaAsync(user.TenantId, user.Id, user.Email, "Logout", ipAddress, ct);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task RevokeAllSessionsAsync(Guid userId, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            throw new NotFoundException("Usuario", userId);
        }

        // Actualizar SecurityStamp para invalidar tokens previos
        await _userManager.UpdateSecurityStampAsync(user);

        // Revocar todos los refresh tokens activos
        var activeTokens = await _context.RefreshTokens
            .IgnoreQueryFilters()
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
        {
            token.IsRevoked = true;
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedByIp = ipAddress;
            token.ReasonRevoked = "Revocación total de sesiones";
        }

        await RegistrarAuditoriaAsync(user.TenantId, user.Id, user.Email, "RevokeAllSessions", ipAddress, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var tenant = await _context.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IdentificadorUrl == request.TenantSlug, ct);

        if (tenant == null || !tenant.Activo)
        {
            // Retornar en silencio para evitar enumeración de tenants
            return;
        }

        var normalizedEmail = request.Email.ToUpperInvariant();
        var user = await _userManager.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == normalizedEmail && !u.IsDeleted, ct);

        if (user == null || !user.Activo)
        {
            // Retornar en silencio para evitar enumeración de usuarios
            return;
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        await _emailSender.SendPasswordResetEmailAsync(user.Email ?? request.Email, user.NombreCompleto, token, request.TenantSlug, ct);

        await RegistrarAuditoriaAsync(tenant.Id, user.Id, user.Email, "PasswordResetRequested", null, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var tenant = await _context.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.IdentificadorUrl == request.TenantSlug, ct);

        if (tenant == null || !tenant.Activo)
        {
            throw new ValidationException(["El enlace de restablecimiento es inválido o ha expirado."]);
        }

        var normalizedEmail = request.Email.ToUpperInvariant();
        var user = await _userManager.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == normalizedEmail && !u.IsDeleted, ct);

        if (user == null || !user.Activo)
        {
            throw new ValidationException(["El enlace de restablecimiento es inválido o ha expirado."]);
        }

        var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description);
            throw new ValidationException(errors);
        }

        // Rota security stamp y revoca todos los refresh tokens
        await _userManager.UpdateSecurityStampAsync(user);

        var activeTokens = await _context.RefreshTokens
            .IgnoreQueryFilters()
            .Where(rt => rt.UserId == user.Id && !rt.IsRevoked)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
        {
            token.IsRevoked = true;
            token.RevokedAt = DateTime.UtcNow;
            token.ReasonRevoked = "Contraseña restablecida exitosamente";
        }

        await RegistrarAuditoriaAsync(tenant.Id, user.Id, user.Email, "PasswordResetSuccess", ipAddress, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            throw new NotFoundException("Usuario", userId);
        }

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description);
            throw new ValidationException(errors);
        }

        // Actualizar security stamp
        await _userManager.UpdateSecurityStampAsync(user);

        await RegistrarAuditoriaAsync(user.TenantId, user.Id, user.Email, "PasswordChanged", ipAddress, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<UserDto> GetCurrentUserProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userManager.Users
            .IgnoreQueryFilters()
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);

        if (user == null)
        {
            throw new NotFoundException("Usuario", userId);
        }

        var role = user.Rol;
        var permissions = Permissions.GetPermissionsForRole(role);

        return new UserDto(
            user.Id,
            user.Email ?? string.Empty,
            user.NombreCompleto,
            user.Rol,
            user.TenantId,
            user.Tenant.IdentificadorUrl,
            user.Tenant.Nombre,
            permissions
        );
    }

    private async Task RegistrarAuditoriaAsync(Guid tenantId, Guid? userId, string? email, string accion, string? ipAddress, CancellationToken ct, string? detalles = null)
    {
        string? jsonDetalles = null;
        if (!string.IsNullOrWhiteSpace(detalles))
        {
            jsonDetalles = detalles.Trim().StartsWith('{')
                ? detalles
                : System.Text.Json.JsonSerializer.Serialize(new { detalle = detalles });
        }

        var auditoria = new HistorialAuditoria
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Entidad = "Auth",
            EntidadId = userId.HasValue ? userId.Value.ToString() : "N/A",
            Accion = accion,
            UsuarioId = userId,
            UsuarioEmail = email,
            IpAddress = ipAddress,
            Fecha = DateTime.UtcNow,
            ValoresNuevosJson = jsonDetalles
        };

        _context.HistorialAuditorias.Add(auditoria);
        await Task.CompletedTask;
    }
}
