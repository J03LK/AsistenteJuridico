using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure;
using AsistenteJuridico.Infrastructure.BackgroundServices;
using AsistenteJuridico.Infrastructure.Common;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase5;

/// <summary>
/// El worker de alertas se ejecuta con la configuración real de producción (AddInfrastructureServices, que activa
/// EnableRetryOnFailure / NpgsqlRetryingExecutionStrategy) contra PostgreSQL. Cada prueba procesa solo sus propios
/// tenants mediante ProcesarTenantConLockAsync, para no tocar datos de otras pruebas que corren en paralelo.
/// </summary>
public class AlertasWorkerExecutionStrategyTests
{
    private const string ErrorEstrategiaConTransaccion = "does not support user-initiated transactions";

    // ─────────────────────────────────────────────────────────────
    // Infraestructura de prueba
    // ─────────────────────────────────────────────────────────────

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }

    /// <summary>
    /// Interceptor que hace fallar, una sola vez, el primer comando que inserta alertas procesales.
    /// </summary>
    private sealed class FalloInsercionAlertasInterceptor(Func<Exception> crearExcepcion) : DbCommandInterceptor
    {
        private int _armado = 1;
        public int FallosProvocados;
        public int InsercionesIntentadas;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Evaluar(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Evaluar(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Evaluar(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO alertas_procesales", StringComparison.OrdinalIgnoreCase)) return;

            Interlocked.Increment(ref InsercionesIntentadas);
            if (Interlocked.Exchange(ref _armado, 0) == 1)
            {
                Interlocked.Increment(ref FallosProvocados);
                throw crearExcepcion();
            }
        }
    }

    /// <summary>
    /// Proveedor de servicios igual al de producción: misma cadena de conexión, EnableRetryOnFailure, servicios reales
    /// de alertas, auditoría y tenant, y sin HttpContext.
    /// </summary>
    private static ServiceProvider CrearServiciosProduccion(CapturingLoggerProvider logs, IInterceptor? interceptorAdicional = null)
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = TestConfiguration.PostgresConnectionString,
                ["Jwt:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(configuracion);
        services.AddInfrastructureServices(configuracion);
        if (interceptorAdicional != null)
        {
            services.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(interceptorAdicional));
        }

        return services.BuildServiceProvider();
    }

    private static AlertasBackgroundService CrearWorker(ServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<AlertasBackgroundService>>());

    private static bool EsErrorDeComandoEF(LogEntry e) => e.EventId.Id == RelationalEventId.CommandError.Id;

    private static bool EsErrorDeSaveChangesEF(LogEntry e) => e.EventId.Id == CoreEventId.SaveChangesFailed.Id;

    private static bool EsErrorDelWorker(LogEntry e) => e.Message.Contains("[ALERTAS_WORKER_ERROR]");

    /// <summary>
    /// El worker no registra errores propios ni aparece la excepción de la estrategia. Los únicos errores admitidos
    /// son los que EF Core registra (CommandError y SaveChangesFailed, uno de cada) por cada fallo de comando que la
    /// propia prueba provocó.
    /// </summary>
    private static void AssertSinErrores(CapturingLoggerProvider logs, int fallosDeComandoProvocados = 0, int erroresDelWorkerEsperados = 0)
    {
        Assert.DoesNotContain(logs.Entries, e =>
            e.Message.Contains(ErrorEstrategiaConTransaccion) || (e.Exception?.ToString().Contains(ErrorEstrategiaConTransaccion) ?? false));

        var errores = logs.Entries.Where(e => e.Level >= LogLevel.Error).ToList();
        var inesperados = errores.Where(e => !EsErrorDeComandoEF(e) && !EsErrorDeSaveChangesEF(e) && !EsErrorDelWorker(e)).ToList();
        Assert.True(inesperados.Count == 0,
            "Errores inesperados: " + string.Join(" | ", inesperados.Select(e => $"[{e.EventId.Id}/{e.EventId.Name}] " + e.Message[..Math.Min(80, e.Message.Length)])));
        Assert.Equal(fallosDeComandoProvocados, errores.Count(EsErrorDeComandoEF));
        Assert.Equal(fallosDeComandoProvocados, errores.Count(EsErrorDeSaveChangesEF));
        Assert.Equal(erroresDelWorkerEsperados, errores.Count(EsErrorDelWorker));
    }

    private static int Procesados(CapturingLoggerProvider logs, Guid tenantId) =>
        logs.Entries.Count(e => e.Message.Contains($"Procesamiento completado para Tenant {tenantId}"));

    private static int OmitidosPorLock(CapturingLoggerProvider logs, Guid tenantId) =>
        logs.Entries.Count(e => e.Message.Contains($"[ADVISORY_LOCK] Tenant {tenantId} ocupado"));

    private static ApplicationDbContext CrearContextoDatos(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(TestConfiguration.PostgresConnectionString)
            .Options;
        return new ApplicationDbContext(options, tenantId.HasValue ? new TenantFijo(tenantId.Value) : null);
    }

    private sealed class TenantFijo(Guid tenantId) : ICurrentTenantService
    {
        public Guid? TenantId { get; private set; } = tenantId;
        public string? TenantSlug => "test";
        public bool IsMultiTenantContext => true;
        public void SetTenantId(Guid id) => TenantId = id;
    }

    private sealed class UsuarioFijo(Guid userId, Guid tenantId, string rol) : ICurrentUserService
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public string? Email => "abogado@estudio.com";
        public string? Role => rol;
        public bool IsAuthenticated => true;
        public IEnumerable<string> Permissions => [Application.Common.Security.Permissions.AudienciasManage];
        public bool HasPermission(string permission) => true;
    }

    private sealed class AuditoriaNula : IAuditService
    {
        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed record Escenario(Guid TenantId, Guid AbogadoId, Guid ExpedienteId);

    private static async Task<Escenario> SeedEscenarioAsync(DateTime? expedienteCreadoUtc = null)
    {
        var tenantId = Guid.NewGuid();
        var abogadoId = Guid.NewGuid();
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        await using (var context = CrearContextoDatos())
        {
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Estudio Worker " + sufijo,
                IdentificadorUrl = "worker-" + sufijo,
                ZonaHorariaId = "America/Guayaquil",
                Activo = true,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using (var context = CrearContextoDatos(tenantId))
        {
            var email = $"{Guid.NewGuid():N}_abogado@estudio.com";
            context.Usuarios.Add(new Usuario
            {
                Id = abogadoId,
                TenantId = tenantId,
                UserName = email,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(),
                NombreCompleto = "Abogado Responsable",
                Rol = Roles.AbogadoSenior,
                Activo = true,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var expedienteId = await SeedExpedienteAsync(tenantId, abogadoId, expedienteCreadoUtc ?? DateTime.UtcNow);
        return new Escenario(tenantId, abogadoId, expedienteId);
    }

    private static async Task<Guid> SeedExpedienteAsync(Guid tenantId, Guid? abogadoId, DateTime creadoUtc)
    {
        await using var context = CrearContextoDatos(tenantId);
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + Guid.NewGuid().ToString("N")[..8],
            NombreRazonSocial = "Cliente Worker " + Guid.NewGuid().ToString("N")[..8],
            Activo = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Clientes.Add(cliente);

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClienteId = cliente.Id,
            NumeroExpediente = "EXP-" + Guid.NewGuid().ToString("N")[..8],
            Titulo = "Caso Worker " + Guid.NewGuid().ToString("N")[..8],
            Materia = "Civil",
            AbogadoResponsableId = abogadoId,
            Estado = EstadoExpediente.Abierto,
            CreatedAt = creadoUtc
        };
        context.Expedientes.Add(expediente);
        await context.SaveChangesAsync();
        return expediente.Id;
    }

    private static async Task<Guid> SeedAudienciaAsync(Escenario e, DateTime fechaHora, EstadoAudiencia estado = EstadoAudiencia.Programada)
    {
        await using var context = CrearContextoDatos(e.TenantId);
        var audiencia = new Audiencia
        {
            Id = Guid.NewGuid(),
            TenantId = e.TenantId,
            ExpedienteId = e.ExpedienteId,
            FechaHora = fechaHora,
            SalaOVirtual = "Sala Worker",
            TipoAudiencia = TipoAudiencia.Juicio,
            Estado = estado,
            CreatedAt = DateTime.UtcNow
        };
        context.Audiencias.Add(audiencia);
        await context.SaveChangesAsync();
        return audiencia.Id;
    }

    private static async Task<Guid> SeedTareaAsync(Escenario e, Guid? asignadoA, DateTime vencimiento, EstadoTarea estado = EstadoTarea.Pendiente)
    {
        await using var context = CrearContextoDatos(e.TenantId);
        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = e.TenantId,
            ExpedienteId = e.ExpedienteId,
            AsignadoAUsuarioId = asignadoA,
            Titulo = "Tarea Worker " + Guid.NewGuid().ToString("N")[..6],
            FechaVencimiento = vencimiento,
            Prioridad = Prioridad.Alta,
            Estado = estado,
            CreatedAt = DateTime.UtcNow
        };
        context.Tareas.Add(tarea);
        await context.SaveChangesAsync();
        return tarea.Id;
    }

    private static async Task<Guid> SeedUsuarioAsync(Guid tenantId, string rol)
    {
        await using var context = CrearContextoDatos(tenantId);
        var email = $"{Guid.NewGuid():N}_usuario@estudio.com";
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserName = email,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            NombreCompleto = "Usuario " + rol,
            Rol = rol,
            Activo = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Usuarios.Add(usuario);
        await context.SaveChangesAsync();
        return usuario.Id;
    }

    private static async Task<List<AlertaProcesal>> AlertasDelTenantAsync(Guid tenantId)
    {
        await using var context = CrearContextoDatos(tenantId);
        return await context.AlertasProcesales.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync();
    }

    private static void AssertSinDuplicados(IEnumerable<AlertaProcesal> alertas)
    {
        var duplicadas = alertas
            .Where(a => a.EstadoResolucion != EstadoAlertaResolucion.InvalidaPorReprogramacion)
            .GroupBy(a => (a.TipoOrigen, a.OrigenId, a.ReglaAlerta, a.UsuarioId, a.FechaObjetivoUtc))
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.True(duplicadas.Count == 0, $"Hay {duplicadas.Count} alertas duplicadas.");
    }

    /// <summary>Alertas de audiencia a menos de 24 h: 7d, 48h y 24h, cada una para el responsable y para supervisión.</summary>
    private static readonly ReglaAlertaCodigo[] ReglasAudiencia24h =
        [ReglaAlertaCodigo.Audiencia7Dias, ReglaAlertaCodigo.Audiencia48Horas, ReglaAlertaCodigo.Audiencia24Horas];

    // ─────────────────────────────────────────────────────────────
    // 1. Sin la excepción de la estrategia de reintentos
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_ConEstrategiaDeReintentosDeProduccion_NoLanzaExcepcionDeTransaccion()
    {
        var e = await SeedEscenarioAsync();
        await SeedAudienciaAsync(e, DateTime.UtcNow.AddHours(20));

        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs);

        // La configuración bajo prueba es la de producción: reintentos activos.
        using (var scope = sp.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(context.Database.CreateExecutionStrategy().RetriesOnFailure);
        }

        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);

        AssertSinErrores(logs);
        Assert.Equal(1, Procesados(logs, e.TenantId));
        Assert.NotEmpty(await AlertasDelTenantAsync(e.TenantId));
    }

    // ─────────────────────────────────────────────────────────────
    // 2 y 8. Ejecución normal: reglas y destinatarios del contrato de Fase 5
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_EjecucionNormal_GeneraLasAlertasDeCadaReglaYDestinatario()
    {
        var e = await SeedEscenarioAsync();
        var asistenteId = await SeedUsuarioAsync(e.TenantId, Roles.AsistenteLegal);

        var audiencia24h = await SeedAudienciaAsync(e, DateTime.UtcNow.AddHours(20));
        var audiencia5d = await SeedAudienciaAsync(e, DateTime.UtcNow.AddDays(5));
        var tarea48h = await SeedTareaAsync(e, asistenteId, DateTime.UtcNow.AddHours(30));
        var tareaVencida = await SeedTareaAsync(e, asistenteId, DateTime.UtcNow.AddHours(-2));
        var tareaCompletada = await SeedTareaAsync(e, asistenteId, DateTime.UtcNow.AddHours(-3), EstadoTarea.Completada);

        // Expediente sin actividad desde hace 65 días: reglas de inactividad 30 y 60 días.
        var creadoInactivo = DateTime.UtcNow.AddDays(-65);
        var expedienteInactivo = await SeedExpedienteAsync(e.TenantId, e.AbogadoId, creadoInactivo);

        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs);
        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);

        AssertSinErrores(logs);
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);
        Assert.All(alertas, a => Assert.Equal(EstadoAlertaResolucion.Activa, a.EstadoResolucion));

        // Audiencia a 20 h: 7d, 48h y 24h para responsable y supervisión (UsuarioId = null).
        foreach (var regla in ReglasAudiencia24h)
        {
            var deRegla = alertas.Where(a => a.OrigenId == audiencia24h && a.ReglaAlerta == regla).ToList();
            Assert.Equal(2, deRegla.Count);
            Assert.Contains(deRegla, a => a.UsuarioId == e.AbogadoId);
            Assert.Contains(deRegla, a => a.UsuarioId == null);
        }
        Assert.Equal(6, alertas.Count(a => a.OrigenId == audiencia24h));

        // Audiencia a 5 días: solo 7d.
        var de5d = alertas.Where(a => a.OrigenId == audiencia5d).ToList();
        Assert.Equal(2, de5d.Count);
        Assert.All(de5d, a => Assert.Equal(ReglaAlertaCodigo.Audiencia7Dias, a.ReglaAlerta));

        // Tarea a 30 h: Tarea48Horas solo para el asignado.
        var de48h = Assert.Single(alertas, a => a.OrigenId == tarea48h);
        Assert.Equal(ReglaAlertaCodigo.Tarea48Horas, de48h.ReglaAlerta);
        Assert.Equal(asistenteId, de48h.UsuarioId);

        // Tarea vencida: asignado y abogado responsable (distinto del asignado).
        var vencidas = alertas.Where(a => a.OrigenId == tareaVencida).ToList();
        Assert.Equal(2, vencidas.Count);
        Assert.All(vencidas, a => Assert.Equal(ReglaAlertaCodigo.TareaVencida, a.ReglaAlerta));
        Assert.Contains(vencidas, a => a.UsuarioId == asistenteId);
        Assert.Contains(vencidas, a => a.UsuarioId == e.AbogadoId);

        // Tarea completada: ninguna alerta.
        Assert.DoesNotContain(alertas, a => a.OrigenId == tareaCompletada);

        // Inactividad: 30 y 60 días, responsable y supervisión, con fecha objetivo última actividad + N días.
        var inactividad = alertas.Where(a => a.OrigenId == expedienteInactivo).ToList();
        Assert.Equal(4, inactividad.Count);
        foreach (var (regla, dias) in new[] { (ReglaAlertaCodigo.InactividadOperativa30Dias, 30), (ReglaAlertaCodigo.InactividadOperativa60Dias, 60) })
        {
            var deRegla = inactividad.Where(a => a.ReglaAlerta == regla).ToList();
            Assert.Equal(2, deRegla.Count);
            Assert.Contains(deRegla, a => a.UsuarioId == e.AbogadoId);
            Assert.Contains(deRegla, a => a.UsuarioId == null);
            Assert.All(deRegla, a => Assert.True(Math.Abs((a.FechaObjetivoUtc - creadoInactivo.AddDays(dias)).TotalSeconds) < 1));
        }

        // El expediente con actividad reciente no recibe alertas de inactividad.
        Assert.DoesNotContain(alertas, a => a.OrigenId == e.ExpedienteId);

        // Una segunda ejecución es idempotente.
        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        AssertSinErrores(logs);
        Assert.Equal(alertas.Count, (await AlertasDelTenantAsync(e.TenantId)).Count);
    }

    // ─────────────────────────────────────────────────────────────
    // 3. Ejecuciones concurrentes del mismo tenant
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_EjecucionesConcurrentesDelMismoTenant_NoDuplicanAlertas()
    {
        var e = await SeedEscenarioAsync();
        var audienciaId = await SeedAudienciaAsync(e, DateTime.UtcNow.AddHours(20));

        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs);

        // Cuatro instancias del worker (cada una con su scope y su DbContext) arrancan a la vez.
        using var inicio = new ManualResetEventSlim(false);
        var tareas = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                inicio.Wait();
                await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
            }))
            .ToList();
        inicio.Set();
        await Task.WhenAll(tareas);

        AssertSinErrores(logs);
        var procesados = Procesados(logs, e.TenantId);
        var omitidos = OmitidosPorLock(logs, e.TenantId);
        Assert.Equal(4, procesados + omitidos);
        Assert.True(procesados >= 1);

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);
        Assert.Equal(6, alertas.Count(a => a.OrigenId == audienciaId));
    }

    // ─────────────────────────────────────────────────────────────
    // 4. El advisory lock sigue protegiendo al tenant
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_ConAdvisoryLockTomadoPorOtraInstancia_OmiteElTenantHastaQueSeLibera()
    {
        var e = await SeedEscenarioAsync();
        await SeedAudienciaAsync(e, DateTime.UtcNow.AddHours(20));

        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs);

        // Otra instancia retiene el lock del tenant (misma clave XxHash64 y pg_try_advisory_xact_lock).
        await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
        await conexion.OpenAsync();
        await using (var txExterna = await conexion.BeginTransactionAsync())
        {
            Assert.True(await AdvisoryLockHelper.ObtenerLockTenantAsync(conexion, e.TenantId, txExterna));

            await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);

            AssertSinErrores(logs);
            Assert.Equal(1, OmitidosPorLock(logs, e.TenantId));
            Assert.Equal(0, Procesados(logs, e.TenantId));
            Assert.Empty(await AlertasDelTenantAsync(e.TenantId));

            await txExterna.CommitAsync();
        }

        // Liberado el lock (fin de la transacción), el siguiente ciclo procesa el tenant.
        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        AssertSinErrores(logs);
        Assert.Equal(1, Procesados(logs, e.TenantId));
        Assert.Equal(6, (await AlertasDelTenantAsync(e.TenantId)).Count);

        // El lock del worker es transaccional: tras su ejecución queda libre.
        await using var txPosterior = await conexion.BeginTransactionAsync();
        Assert.True(await AdvisoryLockHelper.ObtenerLockTenantAsync(conexion, e.TenantId, txPosterior));
        await txPosterior.RollbackAsync();
    }

    // ─────────────────────────────────────────────────────────────
    // 5 y 6. Tenants independientes y sin cruce de datos
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_DosTenants_SeProcesanIndependientementeSinTocarDatosDelOtro()
    {
        var a = await SeedEscenarioAsync();
        var b = await SeedEscenarioAsync();
        var audienciaA = await SeedAudienciaAsync(a, DateTime.UtcNow.AddHours(20));
        var audienciaB = await SeedAudienciaAsync(b, DateTime.UtcNow.AddHours(20));

        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs);

        // Procesar solo A: B no recibe nada.
        await CrearWorker(sp).ProcesarTenantConLockAsync(a.TenantId, CancellationToken.None);
        AssertSinErrores(logs);
        var alertasA = await AlertasDelTenantAsync(a.TenantId);
        Assert.Equal(6, alertasA.Count);
        Assert.All(alertasA, x => Assert.Equal(audienciaA, x.OrigenId));
        Assert.Empty(await AlertasDelTenantAsync(b.TenantId));

        // El lock de B retenido por otra instancia no bloquea el procesamiento de A.
        await using (var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString))
        {
            await conexion.OpenAsync();
            await using var txB = await conexion.BeginTransactionAsync();
            Assert.True(await AdvisoryLockHelper.ObtenerLockTenantAsync(conexion, b.TenantId, txB));

            await CrearWorker(sp).ProcesarTenantConLockAsync(a.TenantId, CancellationToken.None);
            Assert.Equal(2, Procesados(logs, a.TenantId));
            await txB.RollbackAsync();
        }

        // A y B en paralelo: ambos se procesan.
        await Task.WhenAll(
            CrearWorker(sp).ProcesarTenantConLockAsync(a.TenantId, CancellationToken.None),
            CrearWorker(sp).ProcesarTenantConLockAsync(b.TenantId, CancellationToken.None));
        AssertSinErrores(logs);
        Assert.Equal(1, Procesados(logs, b.TenantId));

        var alertasB = await AlertasDelTenantAsync(b.TenantId);
        Assert.Equal(6, alertasB.Count);
        Assert.All(alertasB, x => Assert.Equal(audienciaB, x.OrigenId));
        Assert.Equal(6, (await AlertasDelTenantAsync(a.TenantId)).Count);

        // Procesar A de nuevo no modifica ninguna fila de B (xmin intacto).
        var versionesB = alertasB.ToDictionary(x => x.Id, x => x.Version);
        await CrearWorker(sp).ProcesarTenantConLockAsync(a.TenantId, CancellationToken.None);
        AssertSinErrores(logs);
        var alertasBDespues = await AlertasDelTenantAsync(b.TenantId);
        Assert.Equal(versionesB.Count, alertasBDespues.Count);
        Assert.All(alertasBDespues, x => Assert.Equal(versionesB[x.Id], x.Version));

        // Ninguna alerta de las audiencias de A o B quedó registrada en otro tenant.
        await using var global = CrearContextoDatos();
        var porOrigen = await global.AlertasProcesales.AsNoTracking()
            .Where(x => x.OrigenId == audienciaA || x.OrigenId == audienciaB)
            .ToListAsync();
        Assert.All(porOrigen, x => Assert.Equal(x.OrigenId == audienciaA ? a.TenantId : b.TenantId, x.TenantId));
    }

    // ─────────────────────────────────────────────────────────────
    // 7a. Fallo transitorio: la estrategia reintenta la unidad completa
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_FalloTransitorioAlInsertar_ReintentaLaUnidadCompletaSinDuplicar()
    {
        var e = await SeedEscenarioAsync();
        var audienciaId = await SeedAudienciaAsync(e, DateTime.UtcNow.AddHours(20));

        // Alerta activa de una tarea ya completada: el primer intento la resuelve (y audita) antes de fallar.
        var tareaCompletada = await SeedTareaAsync(e, e.AbogadoId, DateTime.UtcNow.AddHours(10), EstadoTarea.Completada);
        var alertaPreviaId = Guid.NewGuid();
        await using (var context = CrearContextoDatos(e.TenantId))
        {
            context.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = alertaPreviaId,
                TenantId = e.TenantId,
                UsuarioId = e.AbogadoId,
                ExpedienteId = e.ExpedienteId,
                TipoOrigen = TipoOrigenAlerta.Tarea,
                OrigenId = tareaCompletada,
                ReglaAlerta = ReglaAlertaCodigo.Tarea48Horas,
                Severidad = SeveridadAlerta.Media,
                Titulo = "Tarea por vencer",
                Mensaje = "Vence pronto",
                FechaObjetivoUtc = DateTime.UtcNow.AddHours(10),
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
            await context.SaveChangesAsync();
        }

        var fallo = new FalloInsercionAlertasInterceptor(
            () => new NpgsqlException("Fallo transitorio simulado", new TimeoutException()));
        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs, fallo);

        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);

        // El fallo ocurrió y la estrategia volvió a ejecutar la unidad completa en el mismo ciclo.
        Assert.Equal(1, fallo.FallosProvocados);
        Assert.Equal(2, fallo.InsercionesIntentadas);
        AssertSinErrores(logs, fallosDeComandoProvocados: 1);
        Assert.Equal(1, Procesados(logs, e.TenantId));

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);
        Assert.Equal(6, alertas.Count(a => a.OrigenId == audienciaId));
        var previa = Assert.Single(alertas, a => a.Id == alertaPreviaId);
        Assert.Equal(EstadoAlertaResolucion.ResueltaAutomaticamente, previa.EstadoResolucion);

        // Lo escrito por el intento fallido se revirtió: una sola auditoría de la resolución.
        await using var context2 = CrearContextoDatos(e.TenantId);
        var auditorias = await context2.HistorialAuditorias.AsNoTracking()
            .CountAsync(h => h.TenantId == e.TenantId && h.EntidadId == alertaPreviaId.ToString() && h.Accion == "ALERTA_RESOLUCION_AUTOMATICA");
        Assert.Equal(1, auditorias);
    }

    // ─────────────────────────────────────────────────────────────
    // 7b. Fallo no transitorio: se revierte y el siguiente ciclo lo reintenta
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_FalloNoTransitorio_RevierteTodoYElSiguienteCicloNoDuplica()
    {
        var e = await SeedEscenarioAsync();
        var audienciaId = await SeedAudienciaAsync(e, DateTime.UtcNow.AddHours(20));
        var audienciaCancelada = await SeedAudienciaAsync(e, DateTime.UtcNow.AddDays(3), EstadoAudiencia.Cancelada);
        var alertaPreviaId = Guid.NewGuid();
        await using (var context = CrearContextoDatos(e.TenantId))
        {
            context.AlertasProcesales.Add(new AlertaProcesal
            {
                Id = alertaPreviaId,
                TenantId = e.TenantId,
                UsuarioId = null,
                ExpedienteId = e.ExpedienteId,
                TipoOrigen = TipoOrigenAlerta.Audiencia,
                OrigenId = audienciaCancelada,
                ReglaAlerta = ReglaAlertaCodigo.Audiencia7Dias,
                Severidad = SeveridadAlerta.Baja,
                Titulo = "Audiencia en 7 días",
                Mensaje = "Preparar",
                FechaObjetivoUtc = DateTime.UtcNow.AddDays(3),
                FechaDisparoUtc = DateTime.UtcNow,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
            await context.SaveChangesAsync();
        }

        var fallo = new FalloInsercionAlertasInterceptor(() => new InvalidOperationException("Fallo no transitorio simulado"));
        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs, fallo);

        // Primer ciclo: falla, se registra el error del tenant y nada queda escrito.
        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        Assert.Equal(1, fallo.FallosProvocados);
        // El error del tenant es el fallo simulado, no el de la estrategia de reintentos.
        AssertSinErrores(logs, fallosDeComandoProvocados: 1, erroresDelWorkerEsperados: 1);
        var error = Assert.Single(logs.Entries, EsErrorDelWorker);
        Assert.Contains("Fallo no transitorio simulado", error.Exception?.ToString());
        var tras1 = await AlertasDelTenantAsync(e.TenantId);
        var previa = Assert.Single(tras1);
        Assert.Equal(EstadoAlertaResolucion.Activa, previa.EstadoResolucion);

        // Siguiente ciclo: procesa todo una sola vez.
        var logs2 = new CapturingLoggerProvider();
        await using var sp2 = CrearServiciosProduccion(logs2);
        await CrearWorker(sp2).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        await CrearWorker(sp2).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        AssertSinErrores(logs2);
        Assert.Equal(2, Procesados(logs2, e.TenantId));

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);
        Assert.Equal(6, alertas.Count(a => a.OrigenId == audienciaId));
        Assert.Equal(EstadoAlertaResolucion.ResueltaAutomaticamente, Assert.Single(alertas, a => a.Id == alertaPreviaId).EstadoResolucion);
    }

    // ─────────────────────────────────────────────────────────────
    // 9. Reprogramación: las alertas invalidadas no reviven ni se duplican
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Worker_TrasReprogramacion_MantieneInvalidadasYGeneraSoloLasDeLaNuevaFecha()
    {
        var e = await SeedEscenarioAsync();
        var fechaOriginal = DateTime.UtcNow.AddHours(20);
        var audienciaId = await SeedAudienciaAsync(e, fechaOriginal);

        var logs = new CapturingLoggerProvider();
        await using var sp = CrearServiciosProduccion(logs);
        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        AssertSinErrores(logs);
        Assert.Equal(6, (await AlertasDelTenantAsync(e.TenantId)).Count);

        // Reprogramación por el flujo de Fase 5 (AudienciaService.UpdateAudienciaAsync).
        var nuevaFecha = DateTime.UtcNow.AddDays(5);
        await using (var context = CrearContextoDatos(e.TenantId))
        {
            var tenant = new TenantFijo(e.TenantId);
            var usuario = new UsuarioFijo(e.AbogadoId, e.TenantId, Roles.AbogadoSenior);
            var audienciaService = new AudienciaService(
                context, tenant, usuario,
                new ExpedienteAccessService(context, usuario, tenant),
                new AuditoriaNula(),
                new InlineValidator<CreateAudienciaDto>(),
                new InlineValidator<UpdateAudienciaDto>(),
                new InlineValidator<CambiarEstadoAudienciaDto>());

            var actual = await context.Audiencias.AsNoTracking().FirstAsync(a => a.Id == audienciaId);
            await audienciaService.UpdateAudienciaAsync(audienciaId, new UpdateAudienciaDto(
                null, nuevaFecha, "Sala Worker 2", TipoAudiencia.Juicio, "Reprogramación", actual.Version));
        }

        // El worker se ejecuta dos veces tras la reprogramación.
        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        await CrearWorker(sp).ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        AssertSinErrores(logs);

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);

        // Las 6 alertas de la fecha original siguen invalidadas por reprogramación.
        var originales = alertas.Where(a => Math.Abs((a.FechaObjetivoUtc - fechaOriginal).TotalSeconds) < 1).ToList();
        Assert.Equal(6, originales.Count);
        Assert.All(originales, a => Assert.Equal(EstadoAlertaResolucion.InvalidaPorReprogramacion, a.EstadoResolucion));

        // Para la nueva fecha (a 5 días) solo hay Audiencia7Dias activa, para responsable y supervisión.
        var nuevas = alertas.Where(a => a.EstadoResolucion != EstadoAlertaResolucion.InvalidaPorReprogramacion).ToList();
        Assert.Equal(2, nuevas.Count);
        Assert.All(nuevas, a =>
        {
            Assert.Equal(EstadoAlertaResolucion.Activa, a.EstadoResolucion);
            Assert.Equal(ReglaAlertaCodigo.Audiencia7Dias, a.ReglaAlerta);
            Assert.True(Math.Abs((a.FechaObjetivoUtc - nuevaFecha).TotalSeconds) < 1);
        });
        Assert.Contains(nuevas, a => a.UsuarioId == e.AbogadoId);
        Assert.Contains(nuevas, a => a.UsuarioId == null);
    }
}
