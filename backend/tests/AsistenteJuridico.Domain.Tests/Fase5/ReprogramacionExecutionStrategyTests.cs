using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using AsistenteJuridico.Application.Features.Tareas.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.BackgroundServices;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static AsistenteJuridico.Domain.Tests.Fase5.ExecutionStrategyTestSupport;

namespace AsistenteJuridico.Domain.Tests.Fase5;

/// <summary>
/// Reprogramación y cambio de estado de audiencias y tareas con la configuración real de producción
/// (EnableRetryOnFailure / NpgsqlRetryingExecutionStrategy) contra PostgreSQL, usando el AuditService real.
/// </summary>
public class ReprogramacionExecutionStrategyTests
{
    // ─────────────────────────────────────────────────────────────
    // Infraestructura
    // ─────────────────────────────────────────────────────────────

    /// <summary>Servicios de un tenant construidos sobre el DbContext de producción (con reintentos).</summary>
    private sealed class Entorno : IAsyncDisposable
    {
        public required ServiceProvider Proveedor { get; init; }
        public required IServiceScope Scope { get; init; }
        public required ApplicationDbContext Contexto { get; init; }
        public required AudienciaService Audiencias { get; init; }
        public required TareaService Tareas { get; init; }
        public required CapturingLoggerProvider Logs { get; init; }
        public required ContadorTransaccionesInterceptor Transacciones { get; init; }

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            await Proveedor.DisposeAsync();
        }
    }

    private static Entorno CrearEntorno(Escenario e, FalloComandoInterceptor? fallo = null)
    {
        var logs = new CapturingLoggerProvider();
        var transacciones = new ContadorTransaccionesInterceptor();
        var interceptores = fallo == null
            ? new Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] { transacciones }
            : [transacciones, fallo];
        var proveedor = CrearServiciosProduccion(logs, interceptores);

        var scope = proveedor.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantService>();
        tenant.SetTenantId(e.TenantId);
        var contexto = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var usuario = new UsuarioFijo(e.AbogadoId, e.TenantId, Roles.AbogadoSenior);
        var auditoria = new AuditService(contexto, usuario, tenant, scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>());
        var acceso = new ExpedienteAccessService(contexto, usuario, tenant);

        return new Entorno
        {
            Proveedor = proveedor,
            Scope = scope,
            Contexto = contexto,
            Logs = logs,
            Transacciones = transacciones,
            Audiencias = new AudienciaService(contexto, tenant, usuario, acceso, auditoria,
                new InlineValidator<CreateAudienciaDto>(), new InlineValidator<UpdateAudienciaDto>(), new InlineValidator<CambiarEstadoAudienciaDto>()),
            Tareas = new TareaService(contexto, tenant, usuario, acceso, auditoria,
                new InlineValidator<CreateTareaDto>(), new InlineValidator<UpdateTareaDto>(), new InlineValidator<CambiarEstadoTareaDto>())
        };
    }

    private static FalloComandoInterceptor FalloTransitorio(string fragmentoSql) =>
        new(fragmentoSql, () => new NpgsqlException("Fallo transitorio simulado", new TimeoutException()), despuesDeEjecutar: true);

    private static FalloComandoInterceptor FalloNoTransitorio(string fragmentoSql) =>
        new(fragmentoSql, () => new InvalidOperationException("Fallo no transitorio simulado"), despuesDeEjecutar: true);

    private static async Task<Audiencia> LeerAudienciaAsync(Escenario e, Guid id)
    {
        await using var context = CrearContextoDatos(e.TenantId);
        return await context.Audiencias.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private static async Task<Tarea> LeerTareaAsync(Escenario e, Guid id)
    {
        await using var context = CrearContextoDatos(e.TenantId);
        return await context.Tareas.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private static Task ReprogramarAudienciaAsync(Entorno entorno, Audiencia actual, DateTime nuevaFecha) =>
        entorno.Audiencias.UpdateAudienciaAsync(actual.Id, new UpdateAudienciaDto(
            null, nuevaFecha, actual.SalaOVirtual, actual.TipoAudiencia, "Reprogramación", actual.Version));

    private static Task ReprogramarTareaAsync(Entorno entorno, Tarea actual, DateTime nuevoVencimiento) =>
        entorno.Tareas.UpdateTareaAsync(actual.Id, new UpdateTareaDto(
            actual.Titulo, actual.Descripcion, nuevoVencimiento, actual.Prioridad, actual.AsignadoAUsuarioId, actual.Version));

    /// <summary>
    /// Audiencia a 5 días con sus alertas Audiencia7Dias activas (responsable y supervisión) y una alerta histórica
    /// ya invalidada de una fecha anterior.
    /// </summary>
    private static async Task<(Escenario E, Guid AudienciaId, DateTime FechaOriginal, Guid[] Activas, Guid Historica)> SeedAudienciaConAlertasAsync()
    {
        var e = await SeedEscenarioAsync();
        var fechaOriginal = DateTime.UtcNow.AddDays(5);
        var audienciaId = await SeedAudienciaAsync(e, fechaOriginal);
        var fechaOriginalBd = (await LeerAudienciaAsync(e, audienciaId)).FechaHora;

        var activas = new[]
        {
            await SeedAlertaAsync(e, TipoOrigenAlerta.Audiencia, audienciaId, ReglaAlertaCodigo.Audiencia7Dias, e.AbogadoId, fechaOriginalBd),
            await SeedAlertaAsync(e, TipoOrigenAlerta.Audiencia, audienciaId, ReglaAlertaCodigo.Audiencia7Dias, null, fechaOriginalBd)
        };
        var historica = await SeedAlertaAsync(e, TipoOrigenAlerta.Audiencia, audienciaId, ReglaAlertaCodigo.Audiencia7Dias, e.AbogadoId,
            DateTime.UtcNow.AddDays(6), EstadoAlertaResolucion.InvalidaPorReprogramacion);

        return (e, audienciaId, fechaOriginalBd, activas, historica);
    }

    /// <summary>
    /// Tarea asignada que vence en 30 h con su alerta Tarea48Horas activa y una alerta histórica ya invalidada.
    /// </summary>
    private static async Task<(Escenario E, Guid TareaId, Guid AsignadoId, Guid Activa, Guid Historica)> SeedTareaConAlertasAsync()
    {
        var e = await SeedEscenarioAsync();
        var asignadoId = await SeedUsuarioAsync(e.TenantId, Roles.AsistenteLegal);
        var tareaId = await SeedTareaAsync(e, asignadoId, DateTime.UtcNow.AddHours(30));
        var vencimientoBd = (await LeerTareaAsync(e, tareaId)).FechaVencimiento;

        var activa = await SeedAlertaAsync(e, TipoOrigenAlerta.Tarea, tareaId, ReglaAlertaCodigo.Tarea48Horas, asignadoId, vencimientoBd);
        var historica = await SeedAlertaAsync(e, TipoOrigenAlerta.Tarea, tareaId, ReglaAlertaCodigo.Tarea48Horas, asignadoId,
            DateTime.UtcNow.AddHours(40), EstadoAlertaResolucion.InvalidaPorReprogramacion);

        return (e, tareaId, asignadoId, activa, historica);
    }

    // ═════════════════════════════════════════════════════════════
    // AUDIENCIA
    // ═════════════════════════════════════════════════════════════

    // 1–5. Reprogramación con la estrategia de producción
    [Fact]
    public async Task Audiencia_Reprogramar_ConEstrategiaDeProduccion_ActualizaInvalidaYGeneraAlertas()
    {
        var (e, audienciaId, fechaOriginal, activas, historica) = await SeedAudienciaConAlertasAsync();
        var antes = await LeerAudienciaAsync(e, audienciaId);
        var nuevaFecha = DateTime.UtcNow.AddHours(20);

        await using var entorno = CrearEntorno(e);
        Assert.True(entorno.Contexto.Database.CreateExecutionStrategy().RetriesOnFailure);

        await ReprogramarAudienciaAsync(entorno, antes, nuevaFecha);

        AssertSinMensajeDeEstrategia(entorno.Logs);
        Assert.Equal(1, entorno.Transacciones.Iniciadas);
        Assert.Equal(1, entorno.Transacciones.Confirmadas);

        // 2. Fecha actualizada (y xmin nuevo).
        var despues = await LeerAudienciaAsync(e, audienciaId);
        Assert.True(MismoInstante(nuevaFecha, despues.FechaHora));
        Assert.NotEqual(antes.Version, despues.Version);

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);

        // 3. Las activas de la fecha anterior quedan invalidadas.
        Assert.All(alertas.Where(a => activas.Contains(a.Id)), a =>
        {
            Assert.Equal(EstadoAlertaResolucion.InvalidaPorReprogramacion, a.EstadoResolucion);
            Assert.NotNull(a.ResueltaUtc);
            Assert.Contains("Reprogramación", a.MotivoResolucion);
        });
        Assert.DoesNotContain(alertas, a => a.EstadoResolucion == EstadoAlertaResolucion.Activa && MismoInstante(a.FechaObjetivoUtc, fechaOriginal));

        // 4. La histórica sigue ahí, sin cambios.
        var hist = Assert.Single(alertas, a => a.Id == historica);
        Assert.Equal(EstadoAlertaResolucion.InvalidaPorReprogramacion, hist.EstadoResolucion);
        Assert.Equal("Histórica", hist.MotivoResolucion);

        // 5. Nueva fecha a 20 h: alerta inmediata Audiencia24Horas para responsable y supervisión.
        var nuevas = alertas.Where(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa).ToList();
        Assert.Equal(2, nuevas.Count);
        Assert.All(nuevas, a =>
        {
            Assert.Equal(ReglaAlertaCodigo.Audiencia24Horas, a.ReglaAlerta);
            Assert.True(MismoInstante(despues.FechaHora, a.FechaObjetivoUtc));
        });
        Assert.Contains(nuevas, a => a.UsuarioId == e.AbogadoId);
        Assert.Contains(nuevas, a => a.UsuarioId == null);

        // Auditoría posterior al commit: una sola.
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, audienciaId.ToString(), "UPDATE"));
    }

    // 6. Operación equivalente repetida y worker posterior: sin duplicados
    [Fact]
    public async Task Audiencia_ReprogramacionRepetidaYWorker_NoDuplicanAlertas()
    {
        var (e, audienciaId, _, _, _) = await SeedAudienciaConAlertasAsync();
        var nuevaFecha = DateTime.UtcNow.AddHours(20);

        await using (var entorno = CrearEntorno(e))
        {
            await ReprogramarAudienciaAsync(entorno, await LeerAudienciaAsync(e, audienciaId), nuevaFecha);
        }
        var tras1 = await AlertasDelTenantAsync(e.TenantId);

        // Misma fecha otra vez (con el xmin vigente): no cambia la fecha, no se generan alertas.
        var actual = await LeerAudienciaAsync(e, audienciaId);
        await using (var entorno = CrearEntorno(e))
        {
            await ReprogramarAudienciaAsync(entorno, actual, actual.FechaHora);
            AssertSinMensajeDeEstrategia(entorno.Logs);
        }
        var tras2 = await AlertasDelTenantAsync(e.TenantId);
        Assert.Equal(tras1.Count, tras2.Count);
        AssertSinDuplicados(tras2);

        // El worker completa las reglas restantes de la nueva fecha sin duplicar la alerta inmediata.
        var logs = new CapturingLoggerProvider();
        await using (var sp = CrearServiciosProduccion(logs))
        {
            var worker = new AlertasBackgroundService(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<AlertasBackgroundService>>());
            await worker.ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
            await worker.ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        }
        AssertSinMensajeDeEstrategia(logs);
        Assert.DoesNotContain(logs.Entries, x => x.Level >= LogLevel.Error);

        var finales = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(finales);
        var activas = finales.Where(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa).ToList();
        Assert.Equal(6, activas.Count); // 7d, 48h y 24h para responsable y supervisión
        Assert.All(activas, a => Assert.True(MismoInstante(actual.FechaHora, a.FechaObjetivoUtc)));
    }

    // 7. Fallo transitorio tras aplicar cambios: la estrategia repite la unidad completa
    [Fact]
    public async Task Audiencia_Reprogramar_FalloTransitorio_ReintentaLaUnidadCompletaSinDuplicar()
    {
        var (e, audienciaId, _, activas, historica) = await SeedAudienciaConAlertasAsync();
        var antes = await LeerAudienciaAsync(e, audienciaId);
        var nuevaFecha = DateTime.UtcNow.AddHours(20);

        var fallo = FalloTransitorio("UPDATE audiencias");
        await using var entorno = CrearEntorno(e, fallo);

        await ReprogramarAudienciaAsync(entorno, antes, nuevaFecha);

        // Primer intento: el lote (invalidaciones, alertas nuevas y la audiencia) se ejecutó y luego falló.
        Assert.Equal(1, fallo.FallosProvocados);
        Assert.Equal(2, fallo.Ejecuciones);
        Assert.Equal(2, entorno.Transacciones.Iniciadas);
        Assert.Equal(1, entorno.Transacciones.Confirmadas);
        AssertSinMensajeDeEstrategia(entorno.Logs);

        // Resultado final idéntico al de una ejecución sin fallo.
        Assert.True(MismoInstante(nuevaFecha, (await LeerAudienciaAsync(e, audienciaId)).FechaHora));
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);
        Assert.Equal(5, alertas.Count); // 2 invalidadas + 1 histórica + 2 nuevas
        Assert.All(alertas.Where(a => activas.Contains(a.Id) || a.Id == historica),
            a => Assert.Equal(EstadoAlertaResolucion.InvalidaPorReprogramacion, a.EstadoResolucion));
        Assert.Equal(2, alertas.Count(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa && a.ReglaAlerta == ReglaAlertaCodigo.Audiencia24Horas));

        // El reintento parte del estado real de la base de datos: la auditoría registra la fecha original como anterior.
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, audienciaId.ToString(), "UPDATE"));
        Assert.True(MismoInstante(antes.FechaHora, await FechaAnteriorAuditadaAsync(e.TenantId, audienciaId.ToString(), "UPDATE", "fechaHora")));
    }

    // 8. Fallo no transitorio: nada queda aplicado
    [Fact]
    public async Task Audiencia_Reprogramar_FalloNoTransitorio_RevierteLaOperacionCompleta()
    {
        var (e, audienciaId, fechaOriginal, activas, _) = await SeedAudienciaConAlertasAsync();
        var antes = await LeerAudienciaAsync(e, audienciaId);
        var alertasAntes = await AlertasDelTenantAsync(e.TenantId);

        var fallo = FalloNoTransitorio("UPDATE audiencias");
        await using var entorno = CrearEntorno(e, fallo);

        var ex = await Record.ExceptionAsync(() => ReprogramarAudienciaAsync(entorno, antes, DateTime.UtcNow.AddHours(20)));

        Assert.NotNull(ex);
        Assert.Contains(MarcaFalloSimulado, ex!.ToString());
        Assert.DoesNotContain(ErrorEstrategiaConTransaccion, ex.ToString());
        Assert.Equal(1, entorno.Transacciones.Iniciadas); // no se reintenta
        Assert.Equal(0, entorno.Transacciones.Confirmadas);

        var despues = await LeerAudienciaAsync(e, audienciaId);
        Assert.True(MismoInstante(fechaOriginal, despues.FechaHora));
        Assert.Equal(antes.Version, despues.Version);

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        Assert.Equal(alertasAntes.Count, alertas.Count);
        Assert.All(alertas.Where(a => activas.Contains(a.Id)), a => Assert.Equal(EstadoAlertaResolucion.Activa, a.EstadoResolucion));
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, audienciaId.ToString(), "UPDATE"));
    }

    // CambiarEstado (mismo patrón): reintento con resolución y auditoría dentro de la transacción
    [Fact]
    public async Task Audiencia_CambiarEstado_FalloTransitorio_ResuelveYAuditaUnaSolaVez()
    {
        var (e, audienciaId, _, activas, _) = await SeedAudienciaConAlertasAsync();
        var antes = await LeerAudienciaAsync(e, audienciaId);

        var fallo = FalloTransitorio("UPDATE audiencias");
        await using var entorno = CrearEntorno(e, fallo);

        await entorno.Audiencias.CambiarEstadoAsync(audienciaId, new CambiarEstadoAudienciaDto(EstadoAudiencia.Cancelada, antes.Version));

        Assert.Equal(1, fallo.FallosProvocados);
        Assert.Equal(2, entorno.Transacciones.Iniciadas);
        Assert.Equal(1, entorno.Transacciones.Confirmadas);
        AssertSinMensajeDeEstrategia(entorno.Logs);

        Assert.Equal(EstadoAudiencia.Cancelada, (await LeerAudienciaAsync(e, audienciaId)).Estado);
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        foreach (var id in activas)
        {
            Assert.Equal(EstadoAlertaResolucion.ResueltaAutomaticamente, Assert.Single(alertas, a => a.Id == id).EstadoResolucion);
            Assert.Equal(1, await AuditoriasAsync(e.TenantId, id.ToString(), "ALERTA_RESOLUCION_AUTOMATICA"));
        }
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, audienciaId.ToString(), "STATE_CHANGE"));
    }

    [Fact]
    public async Task Audiencia_CambiarEstado_FalloNoTransitorio_RevierteResolucionesYAuditoria()
    {
        var (e, audienciaId, _, activas, _) = await SeedAudienciaConAlertasAsync();
        var antes = await LeerAudienciaAsync(e, audienciaId);

        var fallo = FalloNoTransitorio("UPDATE audiencias");
        await using var entorno = CrearEntorno(e, fallo);

        var ex = await Record.ExceptionAsync(() =>
            entorno.Audiencias.CambiarEstadoAsync(audienciaId, new CambiarEstadoAudienciaDto(EstadoAudiencia.Cancelada, antes.Version)));

        Assert.NotNull(ex);
        Assert.Contains(MarcaFalloSimulado, ex!.ToString());
        Assert.Equal(1, entorno.Transacciones.Iniciadas);

        Assert.Equal(EstadoAudiencia.Programada, (await LeerAudienciaAsync(e, audienciaId)).Estado);
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        foreach (var id in activas)
        {
            // La resolución y su auditoría se escribieron en la transacción y se revirtieron con ella.
            Assert.Equal(EstadoAlertaResolucion.Activa, Assert.Single(alertas, a => a.Id == id).EstadoResolucion);
            Assert.Equal(0, await AuditoriasAsync(e.TenantId, id.ToString(), "ALERTA_RESOLUCION_AUTOMATICA"));
        }
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, audienciaId.ToString(), "STATE_CHANGE"));
    }

    // Concurrencia xmin: sigue siendo DbUpdateConcurrencyException (409), sin reintentos ni cambios parciales
    [Fact]
    public async Task Audiencia_Reprogramar_ConVersionObsoleta_LanzaConflictoDeConcurrenciaYNoAplicaNada()
    {
        var (e, audienciaId, fechaOriginal, activas, _) = await SeedAudienciaConAlertasAsync();
        var obsoleta = await LeerAudienciaAsync(e, audienciaId);

        await using (var context = CrearContextoDatos(e.TenantId))
        {
            var aud = await context.Audiencias.SingleAsync(a => a.Id == audienciaId);
            aud.SalaOVirtual = "Sala cambiada por otro usuario";
            await context.SaveChangesAsync();
        }

        await using var entorno = CrearEntorno(e);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            ReprogramarAudienciaAsync(entorno, obsoleta, DateTime.UtcNow.AddHours(20)));
        Assert.Equal(1, entorno.Transacciones.Iniciadas);

        Assert.True(MismoInstante(fechaOriginal, (await LeerAudienciaAsync(e, audienciaId)).FechaHora));
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        Assert.All(alertas.Where(a => activas.Contains(a.Id)), a => Assert.Equal(EstadoAlertaResolucion.Activa, a.EstadoResolucion));
        Assert.DoesNotContain(alertas, a => a.ReglaAlerta == ReglaAlertaCodigo.Audiencia24Horas);
    }

    // ═════════════════════════════════════════════════════════════
    // TAREA
    // ═════════════════════════════════════════════════════════════

    // 9–12. Reprogramación con la estrategia de producción
    [Fact]
    public async Task Tarea_Reprogramar_ConEstrategiaDeProduccion_ActualizaInvalidaYGeneraAlerta()
    {
        var (e, tareaId, asignadoId, activa, historica) = await SeedTareaConAlertasAsync();
        var antes = await LeerTareaAsync(e, tareaId);
        var nuevoVencimiento = DateTime.UtcNow.AddHours(-2);

        await using var entorno = CrearEntorno(e);
        Assert.True(entorno.Contexto.Database.CreateExecutionStrategy().RetriesOnFailure);

        await ReprogramarTareaAsync(entorno, antes, nuevoVencimiento);

        AssertSinMensajeDeEstrategia(entorno.Logs);
        Assert.Equal(1, entorno.Transacciones.Iniciadas);
        Assert.Equal(1, entorno.Transacciones.Confirmadas);

        // 10. Vencimiento actualizado.
        var despues = await LeerTareaAsync(e, tareaId);
        Assert.True(MismoInstante(nuevoVencimiento, despues.FechaVencimiento));
        Assert.NotEqual(antes.Version, despues.Version);

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);

        // 11. La alerta del vencimiento anterior queda invalidada; la histórica se conserva.
        var previa = Assert.Single(alertas, a => a.Id == activa);
        Assert.Equal(EstadoAlertaResolucion.InvalidaPorReprogramacion, previa.EstadoResolucion);
        Assert.Contains("Reprogramación", previa.MotivoResolucion);
        Assert.Equal("Histórica", Assert.Single(alertas, a => a.Id == historica).MotivoResolucion);

        // 12. Nuevo vencimiento ya pasado: alerta inmediata TareaVencida para el asignado.
        var nueva = Assert.Single(alertas, a => a.EstadoResolucion == EstadoAlertaResolucion.Activa);
        Assert.Equal(ReglaAlertaCodigo.TareaVencida, nueva.ReglaAlerta);
        Assert.Equal(asignadoId, nueva.UsuarioId);
        Assert.True(MismoInstante(despues.FechaVencimiento, nueva.FechaObjetivoUtc));

        Assert.Equal(1, await AuditoriasAsync(e.TenantId, tareaId.ToString(), "UPDATE"));
    }

    // 13. Operación equivalente repetida y worker posterior: sin duplicados
    [Fact]
    public async Task Tarea_ReprogramacionRepetidaYWorker_NoDuplicanAlertas()
    {
        var (e, tareaId, asignadoId, _, _) = await SeedTareaConAlertasAsync();

        await using (var entorno = CrearEntorno(e))
        {
            await ReprogramarTareaAsync(entorno, await LeerTareaAsync(e, tareaId), DateTime.UtcNow.AddHours(-2));
        }
        var tras1 = await AlertasDelTenantAsync(e.TenantId);

        var actual = await LeerTareaAsync(e, tareaId);
        await using (var entorno = CrearEntorno(e))
        {
            await ReprogramarTareaAsync(entorno, actual, actual.FechaVencimiento);
            AssertSinMensajeDeEstrategia(entorno.Logs);
        }
        var tras2 = await AlertasDelTenantAsync(e.TenantId);
        Assert.Equal(tras1.Count, tras2.Count);

        var logs = new CapturingLoggerProvider();
        await using (var sp = CrearServiciosProduccion(logs))
        {
            var worker = new AlertasBackgroundService(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<AlertasBackgroundService>>());
            await worker.ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
            await worker.ProcesarTenantConLockAsync(e.TenantId, CancellationToken.None);
        }
        AssertSinMensajeDeEstrategia(logs);
        Assert.DoesNotContain(logs.Entries, x => x.Level >= LogLevel.Error);

        // TareaVencida para el asignado (inmediata, no se duplica) y para el responsable (la añade el worker).
        var activas = (await AlertasDelTenantAsync(e.TenantId)).Where(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa).ToList();
        AssertSinDuplicados(activas);
        Assert.Equal(2, activas.Count);
        Assert.All(activas, a => Assert.Equal(ReglaAlertaCodigo.TareaVencida, a.ReglaAlerta));
        Assert.Contains(activas, a => a.UsuarioId == asignadoId);
        Assert.Contains(activas, a => a.UsuarioId == e.AbogadoId);
    }

    // 14. Fallo transitorio tras aplicar cambios
    [Fact]
    public async Task Tarea_Reprogramar_FalloTransitorio_ReintentaLaUnidadCompletaSinDuplicar()
    {
        var (e, tareaId, asignadoId, activa, _) = await SeedTareaConAlertasAsync();
        var antes = await LeerTareaAsync(e, tareaId);
        var nuevoVencimiento = DateTime.UtcNow.AddHours(-2);

        var fallo = FalloTransitorio("UPDATE tareas");
        await using var entorno = CrearEntorno(e, fallo);

        await ReprogramarTareaAsync(entorno, antes, nuevoVencimiento);

        Assert.Equal(1, fallo.FallosProvocados);
        Assert.Equal(2, fallo.Ejecuciones);
        Assert.Equal(2, entorno.Transacciones.Iniciadas);
        Assert.Equal(1, entorno.Transacciones.Confirmadas);
        AssertSinMensajeDeEstrategia(entorno.Logs);

        Assert.True(MismoInstante(nuevoVencimiento, (await LeerTareaAsync(e, tareaId)).FechaVencimiento));
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        AssertSinDuplicados(alertas);
        Assert.Equal(3, alertas.Count); // invalidada + histórica + nueva
        Assert.Equal(EstadoAlertaResolucion.InvalidaPorReprogramacion, Assert.Single(alertas, a => a.Id == activa).EstadoResolucion);
        var nueva = Assert.Single(alertas, a => a.EstadoResolucion == EstadoAlertaResolucion.Activa);
        Assert.Equal(asignadoId, nueva.UsuarioId);

        // El reintento parte del estado real de la base de datos: la auditoría registra el vencimiento original como anterior.
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, tareaId.ToString(), "UPDATE"));
        Assert.True(MismoInstante(antes.FechaVencimiento, await FechaAnteriorAuditadaAsync(e.TenantId, tareaId.ToString(), "UPDATE", "fechaVencimiento")));
    }

    // 15. Fallo no transitorio: rollback completo
    [Fact]
    public async Task Tarea_Reprogramar_FalloNoTransitorio_RevierteLaOperacionCompleta()
    {
        var (e, tareaId, _, activa, _) = await SeedTareaConAlertasAsync();
        var antes = await LeerTareaAsync(e, tareaId);
        var alertasAntes = await AlertasDelTenantAsync(e.TenantId);

        var fallo = FalloNoTransitorio("UPDATE tareas");
        await using var entorno = CrearEntorno(e, fallo);

        var ex = await Record.ExceptionAsync(() => ReprogramarTareaAsync(entorno, antes, DateTime.UtcNow.AddHours(-2)));

        Assert.NotNull(ex);
        Assert.Contains(MarcaFalloSimulado, ex!.ToString());
        Assert.DoesNotContain(ErrorEstrategiaConTransaccion, ex.ToString());
        Assert.Equal(1, entorno.Transacciones.Iniciadas);
        Assert.Equal(0, entorno.Transacciones.Confirmadas);

        var despues = await LeerTareaAsync(e, tareaId);
        Assert.True(MismoInstante(antes.FechaVencimiento, despues.FechaVencimiento));
        Assert.Equal(antes.Version, despues.Version);

        var alertas = await AlertasDelTenantAsync(e.TenantId);
        Assert.Equal(alertasAntes.Count, alertas.Count);
        Assert.Equal(EstadoAlertaResolucion.Activa, Assert.Single(alertas, a => a.Id == activa).EstadoResolucion);
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, tareaId.ToString(), "UPDATE"));
    }

    [Fact]
    public async Task Tarea_CambiarEstado_FalloTransitorio_ResuelveYAuditaUnaSolaVez()
    {
        var (e, tareaId, _, activa, _) = await SeedTareaConAlertasAsync();
        var antes = await LeerTareaAsync(e, tareaId);

        var fallo = FalloTransitorio("UPDATE tareas");
        await using var entorno = CrearEntorno(e, fallo);

        await entorno.Tareas.CambiarEstadoAsync(tareaId, new CambiarEstadoTareaDto(EstadoTarea.Completada, antes.Version));

        Assert.Equal(1, fallo.FallosProvocados);
        Assert.Equal(2, entorno.Transacciones.Iniciadas);
        Assert.Equal(1, entorno.Transacciones.Confirmadas);
        AssertSinMensajeDeEstrategia(entorno.Logs);

        var despues = await LeerTareaAsync(e, tareaId);
        Assert.Equal(EstadoTarea.Completada, despues.Estado);
        Assert.NotNull(despues.FechaCompletada);
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        Assert.Equal(EstadoAlertaResolucion.ResueltaAutomaticamente, Assert.Single(alertas, a => a.Id == activa).EstadoResolucion);
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, activa.ToString(), "ALERTA_RESOLUCION_AUTOMATICA"));
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, tareaId.ToString(), "COMPLETE"));
    }

    [Fact]
    public async Task Tarea_CambiarEstado_FalloNoTransitorio_RevierteResolucionYAuditoria()
    {
        var (e, tareaId, _, activa, _) = await SeedTareaConAlertasAsync();
        var antes = await LeerTareaAsync(e, tareaId);

        var fallo = FalloNoTransitorio("UPDATE tareas");
        await using var entorno = CrearEntorno(e, fallo);

        var ex = await Record.ExceptionAsync(() =>
            entorno.Tareas.CambiarEstadoAsync(tareaId, new CambiarEstadoTareaDto(EstadoTarea.Completada, antes.Version)));

        Assert.NotNull(ex);
        Assert.Contains(MarcaFalloSimulado, ex!.ToString());
        Assert.Equal(1, entorno.Transacciones.Iniciadas);

        var despues = await LeerTareaAsync(e, tareaId);
        Assert.Equal(EstadoTarea.Pendiente, despues.Estado);
        Assert.Null(despues.FechaCompletada);
        Assert.Equal(EstadoAlertaResolucion.Activa, Assert.Single(await AlertasDelTenantAsync(e.TenantId), a => a.Id == activa).EstadoResolucion);
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, activa.ToString(), "ALERTA_RESOLUCION_AUTOMATICA"));
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, tareaId.ToString(), "COMPLETE"));
    }

    [Fact]
    public async Task Tarea_Reprogramar_ConVersionObsoleta_LanzaConflictoDeConcurrenciaYNoAplicaNada()
    {
        var (e, tareaId, _, activa, _) = await SeedTareaConAlertasAsync();
        var obsoleta = await LeerTareaAsync(e, tareaId);

        await using (var context = CrearContextoDatos(e.TenantId))
        {
            var tarea = await context.Tareas.SingleAsync(t => t.Id == tareaId);
            tarea.Descripcion = "Cambiada por otro usuario";
            await context.SaveChangesAsync();
        }

        await using var entorno = CrearEntorno(e);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            ReprogramarTareaAsync(entorno, obsoleta, DateTime.UtcNow.AddHours(-2)));
        Assert.Equal(1, entorno.Transacciones.Iniciadas);

        Assert.True(MismoInstante(obsoleta.FechaVencimiento, (await LeerTareaAsync(e, tareaId)).FechaVencimiento));
        var alertas = await AlertasDelTenantAsync(e.TenantId);
        Assert.Equal(EstadoAlertaResolucion.Activa, Assert.Single(alertas, a => a.Id == activa).EstadoResolucion);
        Assert.DoesNotContain(alertas, a => a.ReglaAlerta == ReglaAlertaCodigo.TareaVencida);
    }

    // ═════════════════════════════════════════════════════════════
    // MULTI-TENANT
    // ═════════════════════════════════════════════════════════════

    // 16. Reprogramar una audiencia de A no toca las alertas de B; A no puede reprogramar una audiencia de B
    [Fact]
    public async Task Audiencia_ReprogramarEnTenantA_NoModificaAlertasDeTenantB()
    {
        var (a, audienciaA, _, _, _) = await SeedAudienciaConAlertasAsync();
        var (b, audienciaB, fechaB, _, _) = await SeedAudienciaConAlertasAsync();
        var alertasB = (await AlertasDelTenantAsync(b.TenantId)).ToDictionary(x => x.Id, x => (x.Version, x.EstadoResolucion));

        await using (var entorno = CrearEntorno(a))
        {
            await ReprogramarAudienciaAsync(entorno, await LeerAudienciaAsync(a, audienciaA), DateTime.UtcNow.AddHours(20));

            // Con el contexto de A, la audiencia de B no es accesible y no cambia.
            var audB = await LeerAudienciaAsync(b, audienciaB);
            var ex = await Record.ExceptionAsync(() => ReprogramarAudienciaAsync(entorno, audB, DateTime.UtcNow.AddHours(20)));
            Assert.NotNull(ex);
            Assert.DoesNotContain(ErrorEstrategiaConTransaccion, ex!.ToString());
        }

        Assert.True(MismoInstante(fechaB, (await LeerAudienciaAsync(b, audienciaB)).FechaHora));
        var alertasBDespues = await AlertasDelTenantAsync(b.TenantId);
        Assert.Equal(alertasB.Count, alertasBDespues.Count);
        Assert.All(alertasBDespues, x => Assert.Equal(alertasB[x.Id], (x.Version, x.EstadoResolucion)));

        // Todas las alertas creadas por la reprogramación de A pertenecen a A.
        // Vista global (solo en la prueba): sin filtro de tenant, acotada por OrigenId.
        await using var global = CrearContextoDatos();
        var deA = await global.AlertasProcesales.IgnoreQueryFilters().AsNoTracking().Where(x => x.OrigenId == audienciaA).ToListAsync();
        Assert.Equal(5, deA.Count); // 2 invalidadas + 1 histórica + 2 nuevas
        Assert.All(deA, x => Assert.Equal(a.TenantId, x.TenantId));
        Assert.Equal(2, deA.Count(x => x.EstadoResolucion == EstadoAlertaResolucion.Activa));
    }

    // 17. Ídem para tareas
    [Fact]
    public async Task Tarea_ReprogramarEnTenantA_NoModificaAlertasDeTenantB()
    {
        var (a, tareaA, _, _, _) = await SeedTareaConAlertasAsync();
        var (b, tareaB, _, _, _) = await SeedTareaConAlertasAsync();
        var vencimientoB = (await LeerTareaAsync(b, tareaB)).FechaVencimiento;
        var alertasB = (await AlertasDelTenantAsync(b.TenantId)).ToDictionary(x => x.Id, x => (x.Version, x.EstadoResolucion));

        await using (var entorno = CrearEntorno(a))
        {
            await ReprogramarTareaAsync(entorno, await LeerTareaAsync(a, tareaA), DateTime.UtcNow.AddHours(-2));

            var tarB = await LeerTareaAsync(b, tareaB);
            var ex = await Record.ExceptionAsync(() => ReprogramarTareaAsync(entorno, tarB, DateTime.UtcNow.AddHours(-2)));
            Assert.NotNull(ex);
            Assert.DoesNotContain(ErrorEstrategiaConTransaccion, ex!.ToString());
        }

        Assert.True(MismoInstante(vencimientoB, (await LeerTareaAsync(b, tareaB)).FechaVencimiento));
        var alertasBDespues = await AlertasDelTenantAsync(b.TenantId);
        Assert.Equal(alertasB.Count, alertasBDespues.Count);
        Assert.All(alertasBDespues, x => Assert.Equal(alertasB[x.Id], (x.Version, x.EstadoResolucion)));

        // Vista global (solo en la prueba): sin filtro de tenant, acotada por OrigenId.
        await using var global = CrearContextoDatos();
        var deA = await global.AlertasProcesales.IgnoreQueryFilters().AsNoTracking().Where(x => x.OrigenId == tareaA).ToListAsync();
        Assert.Equal(3, deA.Count); // invalidada + histórica + nueva
        Assert.All(deA, x => Assert.Equal(a.TenantId, x.TenantId));
        Assert.Single(deA, x => x.EstadoResolucion == EstadoAlertaResolucion.Activa);
    }
}
