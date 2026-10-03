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
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AsistenteJuridico.Domain.Tests.Fase5;

/// <summary>
/// Infraestructura compartida por las pruebas que ejercitan el código con la configuración real de producción
/// (AddInfrastructureServices, que activa EnableRetryOnFailure / NpgsqlRetryingExecutionStrategy) contra PostgreSQL.
/// </summary>
internal static class ExecutionStrategyTestSupport
{
    internal const string ErrorEstrategiaConTransaccion = "does not support user-initiated transactions";

    internal const string MarcaFalloSimulado = "simulado";

    // ─────────────────────────────────────────────────────────────
    // Logs
    // ─────────────────────────────────────────────────────────────

    internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    internal sealed class CapturingLoggerProvider : ILoggerProvider
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

    internal static bool EsErrorDeComandoEF(LogEntry e) => e.EventId.Id == RelationalEventId.CommandError.Id;

    internal static bool EsErrorDeSaveChangesEF(LogEntry e) => e.EventId.Id == CoreEventId.SaveChangesFailed.Id;

    internal static void AssertSinMensajeDeEstrategia(CapturingLoggerProvider logs)
    {
        Assert.DoesNotContain(logs.Entries, e =>
            e.Message.Contains(ErrorEstrategiaConTransaccion) || (e.Exception?.ToString().Contains(ErrorEstrategiaConTransaccion) ?? false));
    }

    // ─────────────────────────────────────────────────────────────
    // Configuración de producción
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Proveedor de servicios igual al de producción: misma cadena de conexión, EnableRetryOnFailure, servicios reales
    /// de alertas, auditoría y tenant, y sin HttpContext.
    /// </summary>
    internal static ServiceProvider CrearServiciosProduccion(CapturingLoggerProvider logs, params IInterceptor[] interceptoresAdicionales)
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
        if (interceptoresAdicionales.Length > 0)
        {
            services.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(interceptoresAdicionales));
        }

        return services.BuildServiceProvider();
    }

    // ─────────────────────────────────────────────────────────────
    // Interceptores de prueba
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Hace fallar una sola vez el primer comando cuyo SQL contiene el fragmento indicado. Con
    /// <paramref name="despuesDeEjecutar"/> el comando llega a ejecutarse (sus cambios quedan aplicados dentro de la
    /// transacción) y la excepción se lanza después, para comprobar que esos cambios parciales se revierten.
    /// </summary>
    internal sealed class FalloComandoInterceptor(string fragmentoSql, Func<Exception> crearExcepcion, bool despuesDeEjecutar) : DbCommandInterceptor
    {
        private int _armado = 1;
        public int FallosProvocados;
        public int Ejecuciones;

        private bool Coincide(DbCommand command) =>
            command.CommandText.Contains(fragmentoSql, StringComparison.OrdinalIgnoreCase);

        private bool Disparar()
        {
            if (Interlocked.Exchange(ref _armado, 0) != 1) return false;
            Interlocked.Increment(ref FallosProvocados);
            return true;
        }

        private void AntesDeEjecutar(DbCommand command)
        {
            if (!Coincide(command)) return;
            Interlocked.Increment(ref Ejecuciones);
            if (!despuesDeEjecutar && Disparar()) throw crearExcepcion();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            AntesDeEjecutar(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (despuesDeEjecutar && Coincide(command) && Disparar())
            {
                // Consumir el resultado garantiza que todas las sentencias del lote se ejecutaron en el servidor.
                await result.DisposeAsync();
                throw crearExcepcion();
            }
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            AntesDeEjecutar(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (despuesDeEjecutar && Coincide(command) && Disparar()) throw crearExcepcion();
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Hace que PostgreSQL rechace, una sola vez, el primer comando cuyo SQL contiene el fragmento indicado: lo
    /// reemplaza por un RAISE EXCEPTION con el SQLSTATE dado, de modo que el servidor no aplica nada de ese comando.
    /// </summary>
    internal sealed class ErrorServidorInterceptor(string fragmentoSql, string sqlState) : DbCommandInterceptor
    {
        private int _armado = 1;
        public int FallosProvocados;
        public int Ejecuciones;

        private void Reescribir(DbCommand command)
        {
            if (!command.CommandText.Contains(fragmentoSql, StringComparison.OrdinalIgnoreCase)) return;
            Interlocked.Increment(ref Ejecuciones);
            if (Interlocked.Exchange(ref _armado, 0) != 1) return;

            Interlocked.Increment(ref FallosProvocados);
            command.Parameters.Clear();
            command.CommandText = $"DO $$ BEGIN RAISE EXCEPTION 'Fallo de servidor simulado' USING ERRCODE = '{sqlState}'; END $$;";
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reescribir(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Reescribir(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Cuenta las transacciones iniciadas y confirmadas: cada intento de la estrategia abre una nueva.</summary>
    internal sealed class ContadorTransaccionesInterceptor : DbTransactionInterceptor
    {
        public int Iniciadas;
        public int Confirmadas;

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Iniciadas);
            return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Confirmadas);
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Contexto de tenant y usuario
    // ─────────────────────────────────────────────────────────────

    internal sealed class TenantFijo(Guid tenantId) : ICurrentTenantService
    {
        public Guid? TenantId { get; private set; } = tenantId;
        public string? TenantSlug => "test";
        public bool IsMultiTenantContext => true;
        public void SetTenantId(Guid id) => TenantId = id;
    }

    internal sealed class UsuarioFijo(Guid userId, Guid tenantId, string rol) : ICurrentUserService
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public string? Email => "abogado@estudio.com";
        public string? Role => rol;
        public bool IsAuthenticated => true;
        public IEnumerable<string> Permissions =>
        [
            Application.Common.Security.Permissions.AudienciasManage,
            Application.Common.Security.Permissions.TareasManage
        ];
        public bool HasPermission(string permission) => true;
    }

    internal sealed class AuditoriaNula : IAuditService
    {
        public Task LogAsync(string entidad, string entidadId, string accion, object? valoresAnteriores = null, object? valoresNuevos = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────
    // Datos de prueba (contextos sin reintentos, solo para preparar y verificar)
    // ─────────────────────────────────────────────────────────────

    internal static ApplicationDbContext CrearContextoDatos(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(TestConfiguration.PostgresConnectionString)
            .Options;
        return new ApplicationDbContext(options, tenantId.HasValue ? new TenantFijo(tenantId.Value) : null);
    }

    internal sealed record Escenario(Guid TenantId, Guid AbogadoId, Guid ExpedienteId);

    internal static async Task<Escenario> SeedEscenarioAsync(DateTime? expedienteCreadoUtc = null)
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

    internal static async Task<Guid> SeedExpedienteAsync(Guid tenantId, Guid? abogadoId, DateTime creadoUtc)
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

    internal static async Task<Guid> SeedAudienciaAsync(Escenario e, DateTime fechaHora, EstadoAudiencia estado = EstadoAudiencia.Programada)
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

    internal static async Task<Guid> SeedTareaAsync(Escenario e, Guid? asignadoA, DateTime vencimiento, EstadoTarea estado = EstadoTarea.Pendiente)
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

    internal static async Task<Guid> SeedUsuarioAsync(Guid tenantId, string rol)
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

    /// <summary>Inserta una alerta con los datos indicados y devuelve su Id.</summary>
    internal static async Task<Guid> SeedAlertaAsync(
        Escenario e, TipoOrigenAlerta tipoOrigen, Guid origenId, ReglaAlertaCodigo regla, Guid? usuarioId,
        DateTime fechaObjetivoUtc, EstadoAlertaResolucion estado = EstadoAlertaResolucion.Activa)
    {
        await using var context = CrearContextoDatos(e.TenantId);
        var alerta = new AlertaProcesal
        {
            Id = Guid.NewGuid(),
            TenantId = e.TenantId,
            UsuarioId = usuarioId,
            ExpedienteId = e.ExpedienteId,
            TipoOrigen = tipoOrigen,
            OrigenId = origenId,
            ReglaAlerta = regla,
            Severidad = SeveridadAlerta.Media,
            Titulo = "Alerta previa",
            Mensaje = "Alerta previa",
            FechaObjetivoUtc = fechaObjetivoUtc,
            FechaDisparoUtc = DateTime.UtcNow,
            EstadoResolucion = estado,
            ResueltaUtc = estado == EstadoAlertaResolucion.Activa ? null : DateTime.UtcNow,
            MotivoResolucion = estado == EstadoAlertaResolucion.Activa ? null : "Histórica"
        };
        context.AlertasProcesales.Add(alerta);
        await context.SaveChangesAsync();
        return alerta.Id;
    }

    internal static async Task<List<AlertaProcesal>> AlertasDelTenantAsync(Guid tenantId)
    {
        await using var context = CrearContextoDatos(tenantId);
        return await context.AlertasProcesales.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync();
    }

    internal static async Task<int> AuditoriasAsync(Guid tenantId, string entidadId, string accion)
    {
        await using var context = CrearContextoDatos(tenantId);
        return await context.HistorialAuditorias.AsNoTracking()
            .CountAsync(h => h.TenantId == tenantId && h.EntidadId == entidadId && h.Accion == accion);
    }

    /// <summary>Lee la fecha guardada en la propiedad indicada de ValoresAnterioresJson de la única auditoría que coincide.</summary>
    internal static async Task<DateTime> FechaAnteriorAuditadaAsync(Guid tenantId, string entidadId, string accion, string propiedad)
    {
        await using var context = CrearContextoDatos(tenantId);
        var auditoria = await context.HistorialAuditorias.AsNoTracking()
            .SingleAsync(h => h.TenantId == tenantId && h.EntidadId == entidadId && h.Accion == accion);
        using var json = System.Text.Json.JsonDocument.Parse(auditoria.ValoresAnterioresJson!);
        return json.RootElement.GetProperty(propiedad).GetDateTime().ToUniversalTime();
    }

    internal static void AssertSinDuplicados(IEnumerable<AlertaProcesal> alertas)
    {
        var duplicadas = alertas
            .Where(a => a.EstadoResolucion != EstadoAlertaResolucion.InvalidaPorReprogramacion)
            .GroupBy(a => (a.TipoOrigen, a.OrigenId, a.ReglaAlerta, a.UsuarioId, a.FechaObjetivoUtc))
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.True(duplicadas.Count == 0, $"Hay {duplicadas.Count} alertas duplicadas.");
    }

    internal static bool MismoInstante(DateTime a, DateTime b) => Math.Abs((a - b).TotalMilliseconds) < 1;
}
