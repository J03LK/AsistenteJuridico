using System.Text.Json;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.BackgroundServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase6X;

/// <summary>
/// Fase 6.X (X1) — Recuperación por lease con la DI real del host (contexto, tenant y AuditService), sobre PostgreSQL.
///
/// Aislamiento: cada prueba usa sus propios tenants y documentos (Escenario6X), y en el proceso de pruebas no hay
/// ningún worker alojado (TestHostDefaults deshabilita el de todos los hosts). Así, el único actor sobre los datos de
/// la prueba es la instancia que la propia prueba crea, y los resultados son exactos.
/// </summary>
public class Fase6XRecuperacionTests : IAsyncLifetime
{
    private readonly Escenario6X _e = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _e.SembrarAsync();
        _factory = new AiApiFactory();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _e.Dispose();
    }

    private ProcesamientoIaRecoveryBackgroundService Worker(ProcesamientoIaRecoveryOptions? opciones = null) => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(opciones ?? new ProcesamientoIaRecoveryOptions()),
        NullLogger<ProcesamientoIaRecoveryBackgroundService>.Instance);

    /// <summary>Documento en Procesando con los instantes indicados (simula una caída a mitad de la extracción).</summary>
    private async Task<Guid> ProcesandoAsync(DateTime? desde, DateTime? actualizado = null, DateTime? creado = null)
    {
        var id = await _e.DocumentoAsync(Archivos.Txt("en proceso"), "p.txt", "text/plain",
            estado: EstadoProcesamientoIa.Procesando, metadatos: "{\"previo\":true}", creado: creado);
        await using var context = _e.Contexto();
        var documento = await context.Documentos.SingleAsync(d => d.Id == id);
        documento.IaProcesandoDesde = desde;
        documento.UpdatedAt = actualizado;
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public void EnElProcesoDePruebas_NingunHostEjecutaElWorkerAlojado()
    {
        // Precondición del aislamiento: el worker alojado está registrado (como en producción) pero deshabilitado.
        Assert.Single(_factory.Services.GetServices<IHostedService>().OfType<ProcesamientoIaRecoveryBackgroundService>());
        Assert.False(_factory.Services.GetRequiredService<IOptions<ProcesamientoIaRecoveryOptions>>().Value.Enabled);
        Assert.True(new ProcesamientoIaRecoveryOptions().Enabled);   // producción: habilitado por defecto
    }

    [Fact]
    public async Task CaidaSimulada_LeaseVencido_PasaAFallido_ConAuditoriaDeSistema()
    {
        var id = await ProcesandoAsync(DateTime.UtcNow.AddMinutes(-10));

        var recuperados = await Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow);

        Assert.Equal(1, recuperados);
        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Fallido, documento.EstadoIa);
        Assert.Null(documento.IaProcesandoDesde);                                       // X1-N2
        Assert.Equal(ProcesamientoIaRecoveryBackgroundService.UsuarioSistema, documento.UpdatedBy);
        Assert.Equal("{\"previo\": true}", documento.MetadatosJson);                    // no se toca (DA-8)

        var evento = Assert.Single(await _e.AuditoriasAsync(id, "AI_PROCESSING_RECOVERED"));
        Assert.Equal(_e.TenantId, evento.TenantId);                                     // SetTenantId del worker
        Assert.Null(evento.UsuarioId);                                                  // sistema
        var valores = JsonDocument.Parse(evento.ValoresNuevosJson!).RootElement;
        Assert.Equal(ProcesamientoIaRecoveryBackgroundService.MotivoLeaseVencido, valores.GetProperty("motivo").GetString());
        Assert.Equal(_e.ExpedienteId, valores.GetProperty("expedienteId").GetGuid());
        Assert.True(valores.TryGetProperty("procesandoDesde", out _));
        Assert.DoesNotContain("en proceso", evento.ValoresNuevosJson);                 // sin contenido
        Assert.DoesNotContain(_e.TenantId.ToString("N"), evento.ValoresNuevosJson);    // sin ruta
    }

    [Fact]
    public async Task DentroDelLease_NoSeRecupera()
    {
        var id = await ProcesandoAsync(DateTime.UtcNow.AddSeconds(-60));

        Assert.Equal(0, await Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow));

        var documento = await _e.LeerDocumentoAsync(id);
        Assert.Equal(EstadoProcesamientoIa.Procesando, documento.EstadoIa);
        Assert.NotNull(documento.IaProcesandoDesde);
        Assert.Empty(await _e.AuditoriasAsync(id, "AI_PROCESSING_RECOVERED"));
    }

    [Fact]
    public async Task Historico_SinIaProcesandoDesde_UsaElFallbackSoloComoHistorico()
    {
        var antiguoPorUpdatedAt = await ProcesandoAsync(null, DateTime.UtcNow.AddHours(-1));
        var recientePorUpdatedAt = await ProcesandoAsync(null, DateTime.UtcNow.AddSeconds(-30));
        var antiguoPorCreatedAt = await ProcesandoAsync(null, null, DateTime.UtcNow.AddHours(-2));

        Assert.Equal(2, await Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow));

        Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(antiguoPorUpdatedAt)).EstadoIa);
        Assert.Equal(EstadoProcesamientoIa.Procesando, (await _e.LeerDocumentoAsync(recientePorUpdatedAt)).EstadoIa);
        Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(antiguoPorCreatedAt)).EstadoIa);
    }

    [Fact]
    public async Task AdvisoryLock_RetenidoPorOtraTransaccion_ElWorkerNoToca_LiberadoRecupera()
    {
        var id = await ProcesandoAsync(DateTime.UtcNow.AddMinutes(-20));

        // La prueba retiene el lock de recuperación del tenant desde otra conexión (simula otra instancia trabajando).
        await using (var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString))
        {
            await conexion.OpenAsync();
            await using var transaccion = await conexion.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@clave)", conexion, transaccion))
            {
                cmd.Parameters.AddWithValue("clave", ProcesamientoIaRecoveryBackgroundService.ObtenerClaveLock(_e.TenantId));
                await cmd.ExecuteNonQueryAsync();
            }

            Assert.Equal(0, await Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow));
            Assert.Equal(EstadoProcesamientoIa.Procesando, (await _e.LeerDocumentoAsync(id)).EstadoIa);
            Assert.Empty(await _e.AuditoriasAsync(id, "AI_PROCESSING_RECOVERED"));

            await transaccion.CommitAsync();   // libera el lock
        }

        Assert.Equal(1, await Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow));
        Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(id)).EstadoIa);
        Assert.Single(await _e.AuditoriasAsync(id, "AI_PROCESSING_RECOVERED"));
    }

    [Fact]
    public async Task DosWorkersSimultaneos_UnoRecuperaTodoYElOtroNada_UnEventoPorDocumento()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await ProcesandoAsync(DateTime.UtcNow.AddMinutes(-20)));
        }

        var resultados = await Task.WhenAll(
            Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow),
            Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow));

        // Determinista: o se solapan (uno tiene el lock y el otro sale con 0) o se ejecutan uno tras otro (el segundo
        // ya no encuentra vencidos). En ambos casos: exactamente {0, N}.
        Assert.Equal([0, ids.Count], resultados.Order().ToArray());
        foreach (var id in ids)
        {
            Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(id)).EstadoIa);
            Assert.Single(await _e.AuditoriasAsync(id, "AI_PROCESSING_RECOVERED"));
        }
    }

    [Fact]
    public async Task CicloCompleto_EncuentraElTenantYRecupera()
    {
        var id = await ProcesandoAsync(DateTime.UtcNow.AddMinutes(-15));

        var recuperados = await Worker().EjecutarCicloAsync(DateTime.UtcNow);

        Assert.True(recuperados >= 1);   // el ciclo es global: puede recuperar también restos vencidos de otras ejecuciones
        Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(id)).EstadoIa);
        Assert.Single(await _e.AuditoriasAsync(id, "AI_PROCESSING_RECOVERED"));
    }

    [Fact]
    public async Task WorkerHabilitado_EjecutaElCicloAlArrancar()
    {
        var id = await ProcesandoAsync(DateTime.UtcNow.AddMinutes(-15));
        var worker = Worker(new ProcesamientoIaRecoveryOptions { Enabled = true, IntervalSeconds = 3600 });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // El primer ciclo se ejecuta al arrancar; se espera a que confirme (sin otros actores sobre este tenant).
            for (var i = 0; i < 100 && (await _e.LeerDocumentoAsync(id)).EstadoIa != EstadoProcesamientoIa.Fallido; i++)
            {
                await Task.Delay(100);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(EstadoProcesamientoIa.Fallido, (await _e.LeerDocumentoAsync(id)).EstadoIa);
        Assert.Single(await _e.AuditoriasAsync(id, "AI_PROCESSING_RECOVERED"));
    }

    [Fact]
    public async Task OperacionYaTerminada_NoSeRecupera()
    {
        var id = await _e.DocumentoAsync(Archivos.Txt("terminado"), "t.txt", "text/plain", estado: EstadoProcesamientoIa.Procesado,
            creado: DateTime.UtcNow.AddHours(-3));

        Assert.Equal(0, await Worker().RecuperarTenantAsync(_e.TenantId, DateTime.UtcNow));
        Assert.Equal(EstadoProcesamientoIa.Procesado, (await _e.LeerDocumentoAsync(id)).EstadoIa);
    }

    // ── Validación del lease al arrancar (DA-3) ──────────────────────────

    [Theory]
    [InlineData(300, 60, 30, true)]
    [InlineData(91, 60, 30, true)]
    [InlineData(90, 60, 30, false)]
    [InlineData(60, 60, 30, false)]
    public void LeaseEsValido_SuperaLaSumaDeTimeoutsExplicitos(int lease, double proveedor, double extraccion, bool esperado) =>
        Assert.Equal(esperado, ProcesamientoIaRecoveryOptions.LeaseEsValido(lease, proveedor, extraccion));

    [Fact]
    public void LeaseMenorQueElMinimo_LaAplicacionNoArranca()
    {
        using var factory = new AiApiFactory().WithWebHostBuilder(b => b.UseSetting("AI:ProcessingRecovery:LeaseSeconds", "60"));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validacion = error as OptionsValidationException
            ?? (error as AggregateException)?.InnerExceptions.OfType<OptionsValidationException>().FirstOrDefault();
        Assert.NotNull(validacion);
        Assert.Contains("AI:ProcessingRecovery:LeaseSeconds", validacion!.Message);
    }

    [Fact]
    public void ValoresPorDefecto_DelContrato()
    {
        var opciones = new ProcesamientoIaRecoveryOptions();
        Assert.Equal(300, opciones.LeaseSeconds);
        Assert.Equal(60, opciones.IntervalSeconds);
        Assert.Equal(30, new Infrastructure.Services.AI.DocumentTextExtractionOptions().TimeoutSeconds);
    }
}
