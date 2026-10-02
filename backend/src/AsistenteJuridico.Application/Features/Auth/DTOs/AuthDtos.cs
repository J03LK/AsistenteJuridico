namespace AsistenteJuridico.Application.Features.Auth.DTOs;

public record LoginRequest(
    string Email,
    string Password,
    string TenantSlug
);

public record UserDto(
    Guid Id,
    string Email,
    string NombreCompleto,
    string Rol,
    Guid TenantId,
    string TenantSlug,
    string TenantNombre,
    IReadOnlyList<string> Permissions
);

public record LoginResponse(
    string AccessToken,
    int ExpiresIn,
    string TokenType,
    UserDto User
);

public record RefreshTokenResponse(
    string AccessToken,
    int ExpiresIn,
    string TokenType
);

public record ForgotPasswordRequest(
    string Email,
    string TenantSlug
);

public record ResetPasswordRequest(
    string Email,
    string TenantSlug,
    string Token,
    string NewPassword,
    string ConfirmPassword
);

public record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword,
    string ConfirmPassword
);

public record RevokeTokenRequest(
    bool RevokeAllSessions = false
);
