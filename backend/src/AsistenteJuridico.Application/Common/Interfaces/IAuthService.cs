using AsistenteJuridico.Application.Features.Auth.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IAuthService
{
    Task<(LoginResponse Response, string RawRefreshToken)> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken ct = default);
    Task<(RefreshTokenResponse Response, string RawRefreshToken)> RefreshTokenAsync(string rawRefreshToken, string? ipAddress, CancellationToken ct = default);
    Task LogoutAsync(Guid userId, string? rawRefreshToken, string? ipAddress, CancellationToken ct = default);
    Task RevokeAllSessionsAsync(Guid userId, string? ipAddress, CancellationToken ct = default);
    Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? ipAddress, CancellationToken ct = default);
    Task<UserDto> GetCurrentUserProfileAsync(Guid userId, CancellationToken ct = default);
}
