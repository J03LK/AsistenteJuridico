using AsistenteJuridico.Domain.Entities;

namespace AsistenteJuridico.Domain.Tests.Security;

public class RefreshTokenSecurityTests
{
    [Fact]
    public void ActiveRefreshToken_ShouldBeActiveWhenNotRevokedAndNotExpired()
    {
        // Arrange
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = "abc123hash",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IsRevoked = false
        };

        // Assert
        Assert.True(token.IsActive);
        Assert.False(token.IsExpired);
        Assert.False(token.IsRevoked);
    }

    [Fact]
    public void ExpiredRefreshToken_ShouldBeInactive()
    {
        // Arrange
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = "abc123hash",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            IsRevoked = false
        };

        // Assert
        Assert.False(token.IsActive);
        Assert.True(token.IsExpired);
    }

    [Fact]
    public void RevokedRefreshToken_ShouldBeInactiveEvenIfExpiresInFuture()
    {
        // Arrange
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = "abc123hash",
            ExpiresAt = DateTime.UtcNow.AddDays(5),
            IsRevoked = true,
            RevokedAt = DateTime.UtcNow,
            ReasonRevoked = "Logout"
        };

        // Assert
        Assert.False(token.IsActive);
        Assert.False(token.IsExpired);
        Assert.True(token.IsRevoked);
    }
}
