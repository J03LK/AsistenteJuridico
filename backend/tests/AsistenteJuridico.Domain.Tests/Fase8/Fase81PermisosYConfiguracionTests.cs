using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.1 — PBAC (contrato §5), activación por tenant (DA8-13), valores de AICasoUso y artefactos de la imagen
/// PostgreSQL (DA8-1).
/// </summary>
public class Fase81PermisosYConfiguracionTests
{
    [Theory]
    [InlineData(Roles.AdminEstudio, true, true)]
    [InlineData(Roles.AbogadoSenior, true, false)]
    [InlineData(Roles.AbogadoJunior, true, false)]
    [InlineData(Roles.AsistenteLegal, true, false)]
    [InlineData(Roles.SuperAdmin, false, false)]
    public void Permisos_AISearchYAIIndexManage_PorRol(string rol, bool search, bool indexManage)
    {
        var permisos = Permissions.GetPermissionsForRole(rol);
        Assert.Equal(search, permisos.Contains(Permissions.AISearch));
        Assert.Equal(indexManage, permisos.Contains(Permissions.AIIndexManage));
    }

    [Fact]
    public void Permisos_Valores()
    {
        Assert.Equal("AI.Search", Permissions.AISearch);
        Assert.Equal("AI.IndexManage", Permissions.AIIndexManage);
    }

    [Fact]
    public async Task Politicas_RegistradasParaLosPermisosNuevos()
    {
        using var factory = new AiApiFactory();
        var proveedor = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var search = await proveedor.GetPolicyAsync(Permissions.AISearch);
        var indexManage = await proveedor.GetPolicyAsync(Permissions.AIIndexManage);

        Assert.NotNull(search);
        Assert.NotNull(indexManage);
        Assert.Contains(search!.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement>(),
            r => r.ClaimType == "permission" && r.AllowedValues!.Contains(Permissions.AISearch));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("no es json", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    [InlineData("{\"ia\":{}}", false)]
    [InlineData("{\"ia\":{\"indexacionSemantica\":false}}", false)]
    [InlineData("{\"ia\":{\"indexacionSemantica\":\"true\"}}", false)]
    [InlineData("{\"ia\":{\"indexacionSemantica\":1}}", false)]
    [InlineData("{\"ia.indexacionSemantica\":true}", false)]
    [InlineData("{\"ia\":{\"indexacionSemantica\":true}}", true)]
    [InlineData("{\"otra\":1,\"ia\":{\"indexacionSemantica\":true,\"x\":2}}", true)]
    public void ActivacionPorTenant_SoloElBooleanoTrueExplicito(string? configuracion, bool esperado) =>
        Assert.Equal(esperado, ConfiguracionTenantIa.IndexacionSemanticaHabilitada(configuracion));

    [Fact]
    public void CasosDeUso_ValoresNuevosDeLaFase8()
    {
        Assert.Equal(6, (int)AICasoUso.IndexacionSemantica);
        Assert.Equal(7, (int)AICasoUso.BusquedaSemantica);
        Assert.Equal(8, (int)AICasoUso.PreguntaRag);
        Assert.Equal(1, (int)OrigenUsoIA.Usuario);
        Assert.Equal(2, (int)OrigenUsoIA.Worker);
        Assert.Equal(3, (int)OrigenUsoIA.Sistema);
    }

    // ── Artefactos de la imagen PostgreSQL (DA8-1) ───────────────────────

    private static string RaizRepositorio()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Raíz del repositorio");
    }

    [Fact]
    public void Imagen_BaseAlpineYVersionFijaDePgvector()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RaizRepositorio(), "docker", "postgres", "Dockerfile"));

        // Una sola etapa y basada en la imagen Alpine (nunca la imagen Debian pgvector/pgvector).
        var lineasFrom = dockerfile.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("FROM ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["FROM postgres:16-alpine"], lineasFrom);
        Assert.Matches(@"ARG PGVECTOR_VERSION=0\.8\.\d+", dockerfile);        // etiqueta fija 0.8.x
        Assert.Contains("--branch \"v${PGVECTOR_VERSION}\"", dockerfile);
    }

    [Fact]
    public void Compose_UsaLaImagenConPgvector_YConservaElVolumen()
    {
        var compose = File.ReadAllText(Path.Combine(RaizRepositorio(), "docker-compose.yml"));

        Assert.Contains("context: ./docker/postgres", compose);
        Assert.Contains("image: asistente-juridico/postgres:16-alpine-pgvector", compose);
        Assert.Contains("postgres_data:/var/lib/postgresql/data", compose);
        Assert.Contains("PGDATA: /var/lib/postgresql/data/pgdata", compose);
    }
}
