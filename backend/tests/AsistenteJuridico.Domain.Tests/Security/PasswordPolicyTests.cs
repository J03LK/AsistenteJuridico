using AsistenteJuridico.Application.Features.Auth.DTOs;
using AsistenteJuridico.Application.Features.Auth.Validators;

namespace AsistenteJuridico.Domain.Tests.Security;

public class PasswordPolicyTests
{
    private readonly ResetPasswordRequestValidator _resetPasswordValidator = new();
    private readonly ChangePasswordRequestValidator _changePasswordValidator = new();

    [Theory]
    [InlineData("short")] // Menos de 8 caracteres
    [InlineData("nouppercase123!")] // Sin mayúsculas
    [InlineData("NOLOWERCASE123!")] // Sin minúsculas
    [InlineData("NoDigitsInPassword!")] // Sin números
    [InlineData("NoSpecialChars1234")] // Sin caracteres especiales
    public async Task ResetPasswordValidator_ShouldRejectWeakPasswords(string weakPassword)
    {
        // Arrange
        var request = new ResetPasswordRequest(
            "user@test.ec",
            "demo-estudio",
            "token123",
            weakPassword,
            weakPassword);

        // Act
        var result = await _resetPasswordValidator.ValidateAsync(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ResetPasswordRequest.NewPassword));
    }

    [Fact]
    public async Task ResetPasswordValidator_ShouldRejectWhenPasswordsDoNotMatch()
    {
        // Arrange
        var request = new ResetPasswordRequest(
            "user@test.ec",
            "demo-estudio",
            "token123",
            "StrongP@ssw0rd!",
            "DifferentP@ssw0rd!");

        // Act
        var result = await _resetPasswordValidator.ValidateAsync(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ResetPasswordRequest.ConfirmPassword));
    }

    [Theory]
    [InlineData("Ejemplo2026!Ficticio")]
    [InlineData("LegalTech#2026$")]
    [InlineData("SuperAdmin99!")]
    public async Task ResetPasswordValidator_ShouldAcceptStrongPasswords(string strongPassword)
    {
        // Arrange
        var request = new ResetPasswordRequest(
            "user@test.ec",
            "demo-estudio",
            "token123",
            strongPassword,
            strongPassword);

        // Act
        var result = await _resetPasswordValidator.ValidateAsync(request);

        // Assert
        Assert.True(result.IsValid);
    }
}
