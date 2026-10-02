using System.Security.Claims;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Persistence;

public static class DatabaseSeeder
{
    public static readonly Guid TenantBId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static async Task SeedDevelopmentDataAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
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
            // 3. SEED DE USUARIOS CON PASSWORDS SEGURAS
            // ──────────────────────────────────────────────────────────
            // Admin del Tenant A
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "admin@estudiojuridico.ec",
                "Abogado Administrador Demo",
                Roles.AdminEstudio,
                "REMOVED_SECRET",
                logger);

            // Abogado Senior del Tenant A
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "abogado.senior@estudiojuridico.ec",
                "Dr. Carlos Mendoza",
                Roles.AbogadoSenior,
                "REMOVED_SECRET",
                logger);

            // Abogado Junior del Tenant A
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "abogado.junior@estudiojuridico.ec",
                "Abg. Valeria Andrade",
                Roles.AbogadoJunior,
                "REMOVED_SECRET",
                logger);

            // Abogado del Tenant B (para pruebas de aislamiento multi-tenant)
            await EnsureUserCreatedAsync(
                userManager,
                devTenantB.Id,
                "abogado@quitolegal.ec",
                "Dr. Fernando Suárez",
                Roles.AbogadoSenior,
                "REMOVED_SECRET",
                logger);

            // SuperAdmin Global
            await EnsureUserCreatedAsync(
                userManager,
                devTenantA.Id,
                "superadmin@asistentejuridico.ec",
                "Super Administrador SaaS",
                Roles.SuperAdmin,
                "REMOVED_SECRET",
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
