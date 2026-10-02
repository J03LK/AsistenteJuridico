using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Auth.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Domain.Tests.Security;

public class AuthServiceSecurityTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Guid _tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public AuthServiceSecurityTests()
    {
        var services = new ServiceCollection();

        var inMemorySettings = new Dictionary<string, string?>
        {
            { "Jwt:Key", "ClaveSecretaDePruebasUnitariasMinimo256BitsParaHmacSha256!12345" },
            { "Jwt:Issuer", "AsistenteJuridicoTest" },
            { "Jwt:Audience", "AsistenteJuridicoClientsTest" },
            { "Jwt:ExpiresInMinutes", "15" }
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddDebug());
        services.AddHttpContextAccessor();

        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: dbName));

        services.AddIdentity<Usuario, ApplicationRole>(options =>
        {
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;
            options.User.RequireUniqueEmail = false;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        services.AddAuthentication();

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IEmailSender, DevEmailSender>();
        services.AddScoped<IAuthService, AuthService>();

        _serviceProvider = services.BuildServiceProvider();
    }

    private async Task<(Tenant tenant, Usuario user)> SeedTenantAndUserAsync(string email, string password, bool tenantActivo = true, bool userActivo = true)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            IdentificadorUrl = "estudio-" + Guid.NewGuid().ToString("N")[..8],
            Nombre = "Estudio Jurídico Test",
            Activo = tenantActivo
        };

        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var user = new Usuario
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserName = $"{tenant.Id}_{email}",
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NombreCompleto = "Abogado Test",
            Rol = Roles.AbogadoSenior,
            Activo = userActivo,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded);

        return (tenant, user);
    }

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ReturnsSuccessfulLoginResponseAndRefreshToken()
    {
        // Arrange
        var (tenant, _) = await SeedTenantAndUserAsync("abogado.valido@estudio.ec", "Password123!*");
        using var scope = _serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var request = new LoginRequest("abogado.valido@estudio.ec", "Password123!*", tenant.IdentificadorUrl);

        // Act
        var (response, rawRefreshToken) = await authService.LoginAsync(request, "127.0.0.1");

        // Assert
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(rawRefreshToken));
        Assert.Equal("abogado.valido@estudio.ec", response.User.Email);
        Assert.Equal(tenant.Id, response.User.TenantId);
        Assert.Equal(Roles.AbogadoSenior, response.User.Rol);
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ThrowsUnauthorizedException()
    {
        // Arrange
        var (tenant, _) = await SeedTenantAndUserAsync("abogado.fallo@estudio.ec", "PasswordCorrecta123!*");
        using var scope = _serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var request = new LoginRequest("abogado.fallo@estudio.ec", "PasswordErronea999!*", tenant.IdentificadorUrl);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            authService.LoginAsync(request, "127.0.0.1"));
    }

    [Fact]
    public async Task LoginAsync_WithInactiveTenant_ThrowsForbiddenException()
    {
        // Arrange: Tenant inactivo
        var (tenant, _) = await SeedTenantAndUserAsync("abogado.inactivo@estudio.ec", "Password123!*", tenantActivo: false);
        using var scope = _serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var request = new LoginRequest("abogado.inactivo@estudio.ec", "Password123!*", tenant.IdentificadorUrl);

        // Act & Assert
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            authService.LoginAsync(request, "127.0.0.1"));
    }

    [Fact]
    public async Task RefreshTokenAsync_RotatesTokenAndIssuesNewAccessToken()
    {
        // Arrange
        var (tenant, _) = await SeedTenantAndUserAsync("abogado.refresh@estudio.ec", "Password123!*");
        using var scope = _serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var loginRequest = new LoginRequest("abogado.refresh@estudio.ec", "Password123!*", tenant.IdentificadorUrl);
        var (_, rawRefreshToken) = await authService.LoginAsync(loginRequest, "127.0.0.1");

        // Act: Renovar token
        var (refreshResponse, newRawRefreshToken) = await authService.RefreshTokenAsync(rawRefreshToken, "127.0.0.1");

        // Assert
        Assert.NotNull(refreshResponse);
        Assert.False(string.IsNullOrWhiteSpace(refreshResponse.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(newRawRefreshToken));
        Assert.NotEqual(rawRefreshToken, newRawRefreshToken); // Debe ser rotado

        // El token anterior debe figurar como revocado en la base de datos
        var oldHash = scope.ServiceProvider.GetRequiredService<ITokenService>().HashToken(rawRefreshToken);
        var oldTokenEntity = await context.RefreshTokens.IgnoreQueryFilters().FirstAsync(rt => rt.TokenHash == oldHash);
        Assert.True(oldTokenEntity.IsRevoked);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenTokenIsReused_DetectsReplayAttackAndRevokesEntireFamily()
    {
        // Arrange
        var (tenant, user) = await SeedTenantAndUserAsync("abogado.replay@estudio.ec", "Password123!*");
        using var scope = _serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var loginRequest = new LoginRequest("abogado.replay@estudio.ec", "Password123!*", tenant.IdentificadorUrl);
        var (_, rawRefreshToken) = await authService.LoginAsync(loginRequest, "127.0.0.1");

        // 1ra renovación exitosa (el token original queda revocado)
        var (_, secondRawRefreshToken) = await authService.RefreshTokenAsync(rawRefreshToken, "127.0.0.1");

        // Act: Intento malicioso de reutilizar el token anterior (Replay Attack)
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            authService.RefreshTokenAsync(rawRefreshToken, "192.168.1.50"));

        Assert.Contains("Violación de seguridad", ex.Message);

        // Assert: Toda la familia debe haber sido revocada
        var activeTokens = await context.RefreshTokens
            .IgnoreQueryFilters()
            .Where(rt => rt.UserId == user.Id && !rt.IsRevoked)
            .ToListAsync();

        Assert.Empty(activeTokens); // NINGÚN token de la familia puede quedar activo
    }

    [Fact]
    public async Task LogoutAsync_RevokesSpecifiedRefreshToken()
    {
        // Arrange
        var (tenant, user) = await SeedTenantAndUserAsync("abogado.logout@estudio.ec", "Password123!*");
        using var scope = _serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var loginRequest = new LoginRequest("abogado.logout@estudio.ec", "Password123!*", tenant.IdentificadorUrl);
        var (_, rawRefreshToken) = await authService.LoginAsync(loginRequest, "127.0.0.1");

        // Act
        await authService.LogoutAsync(user.Id, rawRefreshToken, "127.0.0.1");

        // Assert
        var hash = scope.ServiceProvider.GetRequiredService<ITokenService>().HashToken(rawRefreshToken);
        var tokenEntity = await context.RefreshTokens.IgnoreQueryFilters().FirstAsync(rt => rt.TokenHash == hash);
        Assert.True(tokenEntity.IsRevoked);
    }
}
