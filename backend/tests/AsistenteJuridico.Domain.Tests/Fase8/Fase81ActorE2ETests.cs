using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6X;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Persistence.Interceptors;
using AsistenteJuridico.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.1 (contrato §14.1, criterio 20) — Pipeline HTTP completo: toda operación de IA iniciada por una petición
/// autenticada registra su consumo con Origen = Usuario y el usuario del JWT, nunca con UsuarioId nulo.
/// Se ejecuta sobre una base temporal (<see cref="BaseDatosTemporal"/>): las filas de ai_usage_logs, inmutables,
/// nunca llegan a la base de desarrollo.
/// </summary>
public class Fase81ActorE2ETests : IClassFixture<BaseDatosTemporal>, IDisposable
{
    private readonly BaseDatosTemporal _base;
    private readonly ProveedorFijo _proveedor = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _seniorId = Guid.NewGuid();

    public Fase81ActorE2ETests(BaseDatosTemporal baseTemporal)
    {
        _base = baseTemporal;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(servicios =>
        {
            // El registro original fija la cadena de conexión al registrar los servicios: se sustituye entero.
            servicios.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            servicios.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            servicios.AddDbContext<ApplicationDbContext>((sp, opciones) => opciones
                .UseNpgsql(_base.ConnectionString, npgsql => npgsql.UseVector())
                .AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>()));

            servicios.RemoveAll<IAIProvider>();
            servicios.AddScoped<IAIProvider>(_ => _proveedor);
        }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(string Token, Guid ExpedienteId, Guid DocumentoId)> SembrarAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var slug = "fase81-" + Guid.NewGuid().ToString("N");
        var email = $"{_seniorId:N}@fase81.test";

        db.Tenants.Add(new Tenant { Id = _tenantId, Nombre = "Estudio Fase 8.1", IdentificadorUrl = slug, Activo = true });
        db.Usuarios.Add(new Usuario
        {
            Id = _seniorId, TenantId = _tenantId, UserName = email, Email = email, NombreCompleto = "Senior 8.1",
            Rol = Roles.AbogadoSenior, Activo = true
        });
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, TipoIdentificacion = TipoIdentificacion.Ruc,
            Identificacion = "1790016919001", NombreRazonSocial = "Cliente 8.1", Activo = true
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ClienteId = cliente.Id, NumeroExpediente = "EXP-81-0001",
            Titulo = "Caso 8.1", Materia = "Civil", Estado = EstadoExpediente.Abierto, AbogadoResponsableId = _seniorId
        };
        db.AddRange(cliente, expediente);
        await db.SaveChangesAsync();

        var contenido = Encoding.UTF8.GetBytes("Contrato entre las partes. Cláusula primera: objeto del contrato.");
        var archivo = await scope.ServiceProvider.GetRequiredService<IFileStorageService>()
            .SaveDocumentoAsync(_tenantId, expediente.Id, new MemoryStream(contenido), "contrato.txt", "text/plain", contenido.Length);
        var documento = new Documento
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ExpedienteId = expediente.Id, Titulo = "Contrato", TipoDocumento = "Contrato",
            NombreArchivoOriginal = archivo.NombreArchivoOriginal, RutaAlmacenamiento = archivo.RelativePath,
            ContentType = archivo.ContentType, TamanioBytes = archivo.SizeBytes, HashSha256 = archivo.Sha256Hash
        };
        db.Documentos.Add(documento);
        await db.SaveChangesAsync();

        var tokenService = new TokenService(_factory.Services.GetRequiredService<IConfiguration>());
        var (token, _) = tokenService.GenerateAccessToken(
            new Usuario { Id = _seniorId, Email = email, NombreCompleto = "Senior 8.1", Rol = Roles.AbogadoSenior, TenantId = _tenantId },
            new Tenant { Id = _tenantId, IdentificadorUrl = slug, Nombre = "Estudio Fase 8.1" },
            Roles.AbogadoSenior,
            Permissions.GetPermissionsForRole(Roles.AbogadoSenior).ToList());

        return (token, expediente.Id, documento.Id);
    }

    [Fact]
    public async Task EndpointsDeIa_RegistranOrigenUsuarioConElUsuarioDelJwt_EnBaseTemporal()
    {
        var (token, expedienteId, documentoId) = await SembrarAsync();

        foreach (var (ruta, cuerpo) in new (string, object)[]
        {
            ("chat", new { mensaje = "Consulta general", expedienteId }),
            ("resumir-documento", new { documentoId }),
            ("resumir-expediente", new { expedienteId })
        })
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/ai/{ruta}")
            {
                Content = new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json")
            };
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var respuesta = await _client.SendAsync(peticion);
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{ruta}: {respuesta.StatusCode}");
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(_base.Nombre, db.Database.GetDbConnection().Database);   // la prueba usó la base temporal
        var usos = await db.AIUsageLogs.IgnoreQueryFilters().Where(u => u.TenantId == _tenantId).ToListAsync();

        Assert.Equal(3, usos.Count);
        Assert.All(usos, u =>
        {
            Assert.Equal(OrigenUsoIA.Usuario, u.Origen);
            Assert.Equal(_seniorId, u.UsuarioId);
            Assert.Null(u.ActorSistema);
        });
    }
}
