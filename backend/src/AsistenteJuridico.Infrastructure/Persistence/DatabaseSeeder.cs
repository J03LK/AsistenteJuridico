using System.Security.Claims;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Persistence;

public static class DatabaseSeeder
{
    public static readonly Guid TenantBId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Claves de configuración con las contraseñas de los usuarios semilla de desarrollo. Los valores se
    /// proporcionan fuera del repositorio (dotnet user-secrets o variables de entorno DevSeed__Passwords__*).
    /// </summary>
    public const string PasswordAdminKey = "DevSeed:Passwords:Admin";
    public const string PasswordAbogadoSeniorKey = "DevSeed:Passwords:AbogadoSenior";
    public const string PasswordAbogadoJuniorKey = "DevSeed:Passwords:AbogadoJunior";
    public const string PasswordAbogadoTenantBKey = "DevSeed:Passwords:AbogadoTenantB";
    public const string PasswordSuperAdminKey = "DevSeed:Passwords:SuperAdmin";

    public static readonly IReadOnlyList<string> RequiredPasswordKeys =
    [
        PasswordAdminKey,
        PasswordAbogadoSeniorKey,
        PasswordAbogadoJuniorKey,
        PasswordAbogadoTenantBKey,
        PasswordSuperAdminKey
    ];

    /// <summary>
    /// Valida y devuelve las contraseñas de los usuarios semilla. Falla con un error explícito que nombra las
    /// claves ausentes; no existe ninguna contraseña por defecto.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetRequiredSeedPasswords(IConfiguration configuration)
    {
        var faltantes = RequiredPasswordKeys.Where(k => string.IsNullOrWhiteSpace(configuration[k])).ToList();
        if (faltantes.Count > 0)
        {
            throw new InvalidOperationException(
                "Faltan las contraseñas de los usuarios semilla de desarrollo: " + string.Join(", ", faltantes) +
                ". Configúrelas fuera del repositorio con 'dotnet user-secrets set \"<clave>\" <valor>' en " +
                "backend/src/AsistenteJuridico.API o con variables de entorno (por ejemplo DevSeed__Passwords__Admin).");
        }

        return RequiredPasswordKeys.ToDictionary(k => k, k => configuration[k]!);
    }

    public static async Task SeedDevelopmentDataAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();

        // Antes de sembrar nada: sin contraseñas configuradas el seeder no se ejecuta parcialmente
        var passwords = GetRequiredSeedPasswords(scope.ServiceProvider.GetRequiredService<IConfiguration>());

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

        try
        {
            // ──────────────────────────────────────────────────────────
            // 1. SEED DE ROLES Y PERMISOS
            // ──────────────────────────────────────────────────────────
            foreach (var roleName in Roles.All)
            {
                var role = await roleManager.FindByNameAsync(roleName);
                if (role == null)
                {
                    role = new ApplicationRole(roleName, $"Rol del sistema: {roleName}");
                    await roleManager.CreateAsync(role);
                }

                // Sincronizar claims de permisos al rol (agrega nuevos permisos de Fase 4)
                var existingClaims = await roleManager.GetClaimsAsync(role);
                var existingPerms = existingClaims.Where(c => c.Type == "permission").Select(c => c.Value).ToHashSet();

                var permissions = Permissions.GetPermissionsForRole(roleName);
                foreach (var permission in permissions)
                {
                    if (!existingPerms.Contains(permission))
                    {
                        await roleManager.AddClaimAsync(role, new Claim("permission", permission));
                    }
                }

                logger.LogInformation("Rol {RoleName} sincronizado con {Count} permisos.", roleName, permissions.Count);
            }

            // ──────────────────────────────────────────────────────────
            // 2. SEED DE TENANTS
            // ──────────────────────────────────────────────────────────
            var devTenantA = await context.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == CurrentTenantService.DefaultDevTenantId);

            if (devTenantA == null)
            {
                devTenantA = new Tenant
                {
                    Id = CurrentTenantService.DefaultDevTenantId,
                    Nombre = "Estudio Jurídico Demo Ecuador",
                    Ruc = "1790012345001",
                    IdentificadorUrl = "demo-estudio",
                    Plan = "Enterprise",
                    Activo = true,
                    CreatedAt = DateTime.UtcNow
                };
                context.Tenants.Add(devTenantA);
            }

            var devTenantB = await context.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == TenantBId);

            if (devTenantB == null)
            {
                devTenantB = new Tenant
                {
                    Id = TenantBId,
                    Nombre = "Estudio Jurídico Quito Legal",
                    Ruc = "1790098765001",
                    IdentificadorUrl = "estudio-quito",
                    Plan = "Professional",
                    Activo = true,
                    CreatedAt = DateTime.UtcNow
                };
                context.Tenants.Add(devTenantB);
            }

            await context.SaveChangesAsync();

            // ──────────────────────────────────────────────────────────
            // 3. SEED DE USUARIOS (contraseñas desde configuración externa, nunca en el código)
            // ──────────────────────────────────────────────────────────
            // Admin del Tenant A
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "admin@estudiojuridico.ec",
                "Abogado Administrador Demo",
                Roles.AdminEstudio,
                passwords[PasswordAdminKey],
                logger);

            // Abogado Senior del Tenant A
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "abogado.senior@estudiojuridico.ec",
                "Dr. Carlos Mendoza",
                Roles.AbogadoSenior,
                passwords[PasswordAbogadoSeniorKey],
                logger);

            // Abogado Junior del Tenant A
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "abogado.junior@estudiojuridico.ec",
                "Abg. Valeria Andrade",
                Roles.AbogadoJunior,
                passwords[PasswordAbogadoJuniorKey],
                logger);

            // Abogado del Tenant B (para pruebas de aislamiento multi-tenant)
            await EnsureUserCreatedAsync(
                userManager,
                devTenantB.Id,
                "abogado@quitolegal.ec",
                "Dr. Fernando Suárez",
                Roles.AbogadoSenior,
                passwords[PasswordAbogadoTenantBKey],
                logger);

            // SuperAdmin Global
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "superadmin@asistentejuridico.ec",
                "Super Administrador SaaS",
                Roles.SuperAdmin,
                passwords[PasswordSuperAdminKey],
                logger);

            logger.LogInformation("Seed técnico de autenticación y seguridad completado exitosamente.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al ejecutar el seed técnico de desarrollo.");
        }
    }

    private static async Task EnsureUserCreatedAsync(
        UserManager<Usuario> userManager,
        Guid tenantId,
        string email,
        string nombreCompleto,
        string rol,
        string password,
        ILogger logger)
    {
        var normalizedEmail = email.ToUpperInvariant();
        var existingUser = await userManager.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && (u.NormalizedEmail == normalizedEmail || u.Email == email));

        if (existingUser == null)
        {
            var user = new Usuario
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserName = $"{tenantId}_{email}",
                Email = email,
                NormalizedEmail = normalizedEmail,
                NombreCompleto = nombreCompleto,
                Rol = rol,
                Activo = true,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow
            };

            var result = await userManager.CreateAsync(user, password);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(user, rol);
                logger.LogInformation("Usuario {Email} ({Rol}) creado exitosamente para Tenant {TenantId}.", email, rol, tenantId);
            }
            else
            {
                logger.LogError("Fallo al crear usuario {Email}: {Errors}", email, string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
        else
        {
            existingUser.NormalizedEmail = normalizedEmail;
            existingUser.UserName = $"{tenantId}_{email}";
            existingUser.NormalizedUserName = $"{tenantId}_{email}".ToUpperInvariant();
            existingUser.Rol = rol;
            if (string.IsNullOrEmpty(existingUser.SecurityStamp))
            {
                existingUser.SecurityStamp = Guid.NewGuid().ToString();
            }

            // Si el usuario existía de Fase 2 sin hash de password de Identity, actualizar contraseña
            if (string.IsNullOrEmpty(existingUser.PasswordHash))
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(existingUser);
                await userManager.ResetPasswordAsync(existingUser, token, password);
                logger.LogInformation("Usuario existente {Email} actualizado con contraseña Identity.", email);
            }

            if (!await userManager.IsInRoleAsync(existingUser, rol))
            {
                await userManager.AddToRoleAsync(existingUser, rol);
            }

            await userManager.UpdateAsync(existingUser);
        }
    }
}
