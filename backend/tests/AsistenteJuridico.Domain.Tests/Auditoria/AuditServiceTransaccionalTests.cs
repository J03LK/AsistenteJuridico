using System;
using System.Linq;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using AsistenteJuridico.Application.Features.Expedientes.DTOs;
using AsistenteJuridico.Application.Features.Expedientes.Validators;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using AsistenteJuridico.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static AsistenteJuridico.Domain.Tests.Fase5.ExecutionStrategyTestSupport;

namespace AsistenteJuridico.Domain.Tests.Auditoria;

/// <summary>
/// Auditoría dentro y fuera de la unidad de trabajo, con la configuración real de producción
/// (EnableRetryOnFailure) contra PostgreSQL y el AuditService registrado en DI.
/// </summary>
public class AuditServiceTransaccionalTests
{
    private const string MarcaAuditoriaNoRegistrada = "[AUDITORIA_NO_REGISTRADA]";

    private sealed class Entorno : IAsyncDisposable
    {
        public required ServiceProvider Proveedor { get; init; }
        public required IServiceScope Scope { get; init; }
        public required ApplicationDbContext Contexto { get; init; }
        public required ExpedienteService Expedientes { get; init; }
        public required AudienciaService Audiencias { get; init; }
        public required CapturingLoggerProvider Logs { get; init; }

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            await Proveedor.DisposeAsync();
        }
    }

    private static Entorno CrearEntorno(Escenario e, params IInterceptor[] interceptores)
    {
        var logs = new CapturingLoggerProvider();
        var proveedor = CrearServiciosProduccion(logs, interceptores);
        var scope = proveedor.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantService>();
        tenant.SetTenantId(e.TenantId);
        var contexto = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auditoria = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var usuario = new UsuarioFijo(e.AbogadoId, e.TenantId, Roles.AbogadoSenior);
        var acceso = new ExpedienteAccessService(contexto, usuario, tenant);

        return new Entorno
        {
            Proveedor = proveedor,
            Scope = scope,
            Contexto = contexto,
            Logs = logs,
            Expedientes = new ExpedienteService(contexto, tenant, usuario, acceso, new ExpedienteCodeGenerator(contexto), auditoria,
                new InlineValidator<CreateExpedienteDto>(), new InlineValidator<UpdateExpedienteDto>(),
                new CambiarEstadoExpedienteValidator(), new VincularProcesoValidator()),
            Audiencias = new AudienciaService(contexto, tenant, usuario, acceso, auditoria,
                new InlineValidator<CreateAudienciaDto>(), new InlineValidator<UpdateAudienciaDto>(), new InlineValidator<CambiarEstadoAudienciaDto>())
        };
    }

    private static FalloComandoInterceptor FalloNoTransitorio(string fragmentoSql) =>
        new(fragmentoSql, () => new InvalidOperationException("Fallo no transitorio simulado"), despuesDeEjecutar: true);

    private static FalloComandoInterceptor FalloTransitorio(string fragmentoSql) =>
        new(fragmentoSql, () => new NpgsqlException("Fallo transitorio simulado", new TimeoutException()), despuesDeEjecutar: true);

    /// <summary>Expediente EnTramite con dos tareas pendientes y una completada.</summary>
    private static async Task<(Escenario E, Guid[] Pendientes, Guid Completada)> SeedExpedienteConTareasAsync()
    {
        var e = await SeedEscenarioAsync();
        await using (var c = CrearContextoDatos(e.TenantId))
        {
            var exp = await c.Expedientes.SingleAsync(x => x.Id == e.ExpedienteId);
            exp.Estado = EstadoExpediente.EnTramite;
            await c.SaveChangesAsync();
        }
        var pendientes = new[]
        {
            await SeedTareaAsync(e, e.AbogadoId, DateTime.UtcNow.AddDays(3)),
            await SeedTareaAsync(e, e.AbogadoId, DateTime.UtcNow.AddDays(4), EstadoTarea.EnProgreso)
        };
        var completada = await SeedTareaAsync(e, e.AbogadoId, DateTime.UtcNow.AddDays(-1), EstadoTarea.Completada);
        return (e, pendientes, completada);
    }

    private static async Task<Expediente> LeerExpedienteAsync(Escenario e)
    {
        await using var c = CrearContextoDatos(e.TenantId);
        return await c.Expedientes.AsNoTracking().SingleAsync(x => x.Id == e.ExpedienteId);
    }

    private static async Task<EstadoTarea[]> EstadosTareasAsync(Escenario e, Guid[] ids)
    {
        await using var c = CrearContextoDatos(e.TenantId);
        var tareas = await c.Tareas.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync();
        return ids.Select(id => tareas.Single(t => t.Id == id).Estado).ToArray();
    }

    private static CambiarEstadoExpedienteDto CierreForzado(uint version) =>
        new(EstadoExpediente.Cerrado, ConfirmarCierreConTareasPendientes: true, MotivoCierreForzado: "Acuerdo transaccional entre las partes", version);

    // ─────────────────────────────────────────────────────────────
    // 1. Cierre forzado exitoso
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task CierreForzado_Exitoso_CierraCancelaTareasYAuditaUnaVez()
    {
        var (e, pendientes, completada) = await SeedExpedienteConTareasAsync();
        var antes = await LeerExpedienteAsync(e);

        await using var entorno = CrearEntorno(e);
        await entorno.Expedientes.CambiarEstadoAsync(e.ExpedienteId, CierreForzado(antes.Version));

        var despues = await LeerExpedienteAsync(e);
        Assert.Equal(EstadoExpediente.Cerrado, despues.Estado);
        Assert.NotNull(despues.FechaCierreReal);
        Assert.All(await EstadosTareasAsync(e, pendientes), s => Assert.Equal(EstadoTarea.Cancelada, s));
        Assert.Equal(EstadoTarea.Completada, (await EstadosTareasAsync(e, [completada]))[0]);
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, e.ExpedienteId.ToString(), "FORCE_CLOSE"));
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, e.ExpedienteId.ToString(), "STATE_CHANGE"));
        Assert.DoesNotContain(entorno.Logs.Entries, l => l.Message.Contains(MarcaAuditoriaNoRegistrada));
    }

    // ─────────────────────────────────────────────────────────────
    // 2. Cierre forzado con xmin obsoleto: 409 y nada aplicado
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task CierreForzado_ConVersionObsoleta_LanzaConflictoYNoAplicaNada()
    {
        var (e, pendientes, _) = await SeedExpedienteConTareasAsync();
        var obsoleta = (await LeerExpedienteAsync(e)).Version;
        await using (var c = CrearContextoDatos(e.TenantId))
        {
            var exp = await c.Expedientes.SingleAsync(x => x.Id == e.ExpedienteId);
            exp.Descripcion = "Modificado por otro usuario";
            await c.SaveChangesAsync();
        }

        await using var entorno = CrearEntorno(e);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            entorno.Expedientes.CambiarEstadoAsync(e.ExpedienteId, CierreForzado(obsoleta)));

        var despues = await LeerExpedienteAsync(e);
        Assert.Equal(EstadoExpediente.EnTramite, despues.Estado);
        Assert.Null(despues.FechaCierreReal);
        Assert.Equal(new[] { EstadoTarea.Pendiente, EstadoTarea.EnProgreso }, await EstadosTareasAsync(e, pendientes));
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, e.ExpedienteId.ToString(), "FORCE_CLOSE"));
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, e.ExpedienteId.ToString(), "STATE_CHANGE"));
    }

    // ─────────────────────────────────────────────────────────────
    // 3. Fallo al guardar la auditoría dentro de la operación: rollback completo
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task CierreForzado_FalloAlGuardarAuditoria_RevierteTodaLaOperacion()
    {
        var (e, pendientes, _) = await SeedExpedienteConTareasAsync();
        var antes = await LeerExpedienteAsync(e);

        var fallo = FalloNoTransitorio("INSERT INTO historial_auditorias");
        await using var entorno = CrearEntorno(e, fallo);
        var ex = await Record.ExceptionAsync(() => entorno.Expedientes.CambiarEstadoAsync(e.ExpedienteId, CierreForzado(antes.Version)));

        Assert.Equal(1, fallo.FallosProvocados);
        Assert.NotNull(ex);
        Assert.Contains(MarcaFalloSimulado, ex!.ToString());
        var despues = await LeerExpedienteAsync(e);
        Assert.Equal(EstadoExpediente.EnTramite, despues.Estado);
        Assert.Equal(antes.Version, despues.Version);
        Assert.Equal(new[] { EstadoTarea.Pendiente, EstadoTarea.EnProgreso }, await EstadosTareasAsync(e, pendientes));
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, e.ExpedienteId.ToString(), "FORCE_CLOSE"));
    }

    [Fact]
    public async Task AudienciaCambiarEstado_FalloAlGuardarAuditoria_RevierteTodaLaOperacion()
    {
        var e = await SeedEscenarioAsync();
        var audienciaId = await SeedAudienciaAsync(e, DateTime.UtcNow.AddDays(3));
        var audiencia = await CrearContextoDatos(e.TenantId).Audiencias.AsNoTracking().SingleAsync(a => a.Id == audienciaId);
        var alertas = new[]
        {
            await SeedAlertaAsync(e, TipoOrigenAlerta.Audiencia, audienciaId, ReglaAlertaCodigo.Audiencia7Dias, e.AbogadoId, audiencia.FechaHora),
            await SeedAlertaAsync(e, TipoOrigenAlerta.Audiencia, audienciaId, ReglaAlertaCodigo.Audiencia7Dias, null, audiencia.FechaHora)
        };

        var fallo = FalloNoTransitorio("INSERT INTO historial_auditorias");
        await using var entorno = CrearEntorno(e, fallo);
        var ex = await Record.ExceptionAsync(() =>
            entorno.Audiencias.CambiarEstadoAsync(audienciaId, new CambiarEstadoAudienciaDto(EstadoAudiencia.Cancelada, audiencia.Version)));

        Assert.Equal(1, fallo.FallosProvocados);
        Assert.NotNull(ex);
        Assert.Contains(MarcaFalloSimulado, ex!.ToString());
        Assert.Equal(EstadoAudiencia.Programada, (await CrearContextoDatos(e.TenantId).Audiencias.AsNoTracking().SingleAsync(a => a.Id == audienciaId)).Estado);
        var actuales = await AlertasDelTenantAsync(e.TenantId);
        foreach (var id in alertas)
        {
            Assert.Equal(EstadoAlertaResolucion.Activa, actuales.Single(a => a.Id == id).EstadoResolucion);
            Assert.Equal(0, await AuditoriasAsync(e.TenantId, id.ToString(), "ALERTA_RESOLUCION_AUTOMATICA"));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4. Auditoría posterior al commit que falla
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task AuditoriaPostCommit_Fallida_NoRevierteNiQuedaPendienteYSeRegistraEnLog()
    {
        var e = await SeedEscenarioAsync();
        var audienciaId = await SeedAudienciaAsync(e, DateTime.UtcNow.AddDays(3));
        var antes = await CrearContextoDatos(e.TenantId).Audiencias.AsNoTracking().SingleAsync(a => a.Id == audienciaId);
        var nuevaFecha = DateTime.UtcNow.AddDays(10);
        const string salaConfidencial = "Sala reservada 7B";

        // PostgreSQL rechaza la auditoría (22001, no transitorio): no se inserta nada.
        var fallo = new ErrorServidorInterceptor("INSERT INTO historial_auditorias", "22001");
        await using var entorno = CrearEntorno(e, fallo);
        await entorno.Audiencias.UpdateAudienciaAsync(audienciaId, new UpdateAudienciaDto(
            null, nuevaFecha, salaConfidencial, antes.TipoAudiencia, null, antes.Version));

        // La operación principal quedó confirmada.
        Assert.Equal(1, fallo.FallosProvocados);
        var despues = await CrearContextoDatos(e.TenantId).Audiencias.AsNoTracking().SingleAsync(a => a.Id == audienciaId);
        Assert.True(MismoInstante(nuevaFecha, despues.FechaHora));
        Assert.Equal(salaConfidencial, despues.SalaOVirtual);

        // La auditoría fallida no queda pendiente ni la persiste un SaveChanges posterior del mismo contexto.
        Assert.DoesNotContain(entorno.Contexto.ChangeTracker.Entries<HistorialAuditoria>(), x => x.State == EntityState.Added);
        await entorno.Contexto.SaveChangesAsync();
        Assert.Equal(0, await AuditoriasAsync(e.TenantId, audienciaId.ToString(), "UPDATE"));

        // Evidencia técnica segura: entidad, acción y tipo de error; sin los valores auditados.
        var registro = Assert.Single(entorno.Logs.Entries, l => l.Message.Contains(MarcaAuditoriaNoRegistrada));
        Assert.Equal(LogLevel.Error, registro.Level);
        Assert.Contains("Audiencia", registro.Message);
        Assert.Contains(audienciaId.ToString(), registro.Message);
        Assert.Contains("UPDATE", registro.Message);
        Assert.Contains("22001", registro.Message);
        Assert.DoesNotContain(salaConfidencial, registro.Message);
        Assert.Null(registro.Exception);
    }

    [Fact]
    public async Task AuditoriaPostCommit_FalloTransitorio_SeReintentaYSeGuardaUnaVez()
    {
        var e = await SeedEscenarioAsync();
        var audienciaId = await SeedAudienciaAsync(e, DateTime.UtcNow.AddDays(3));
        var antes = await CrearContextoDatos(e.TenantId).Audiencias.AsNoTracking().SingleAsync(a => a.Id == audienciaId);

        // PostgreSQL rechaza la auditoría con un error transitorio (40001): la estrategia reintenta el guardado.
        var fallo = new ErrorServidorInterceptor("INSERT INTO historial_auditorias", "40001");
        await using var entorno = CrearEntorno(e, fallo);
        await entorno.Audiencias.UpdateAudienciaAsync(audienciaId, new UpdateAudienciaDto(
            null, DateTime.UtcNow.AddDays(10), antes.SalaOVirtual, antes.TipoAudiencia, null, antes.Version));

        Assert.Equal(1, fallo.FallosProvocados);
        Assert.Equal(2, fallo.Ejecuciones);
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, audienciaId.ToString(), "UPDATE"));
        Assert.DoesNotContain(entorno.Logs.Entries, l => l.Message.Contains(MarcaAuditoriaNoRegistrada));
    }

    // ─────────────────────────────────────────────────────────────
    // 5. Reintento con EnableRetryOnFailure: una sola operación y una sola auditoría efectivas
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task CierreForzado_FalloTransitorio_ReintentaYAplicaUnaSolaVez()
    {
        var (e, pendientes, _) = await SeedExpedienteConTareasAsync();
        var antes = await LeerExpedienteAsync(e);

        var fallo = FalloTransitorio("UPDATE expedientes");
        await using var entorno = CrearEntorno(e, fallo);
        await entorno.Expedientes.CambiarEstadoAsync(e.ExpedienteId, CierreForzado(antes.Version));

        // El primer intento llegó a ejecutarse en la base de datos y falló; el segundo completó la operación.
        Assert.Equal(1, fallo.FallosProvocados);
        Assert.Equal(2, fallo.Ejecuciones);
        Assert.Equal(EstadoExpediente.Cerrado, (await LeerExpedienteAsync(e)).Estado);
        Assert.All(await EstadosTareasAsync(e, pendientes), s => Assert.Equal(EstadoTarea.Cancelada, s));
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, e.ExpedienteId.ToString(), "FORCE_CLOSE"));
        Assert.Equal(1, await AuditoriasAsync(e.TenantId, e.ExpedienteId.ToString(), "STATE_CHANGE"));
    }
}
