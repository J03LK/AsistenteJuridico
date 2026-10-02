using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Dashboard.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(
        ApplicationDbContext context,
        ICurrentUserService currentUserService,
        ICurrentTenantService currentTenantService,
        ILogger<DashboardService> logger)
    {
        _context = context;
        _currentUserService = currentUserService;
        _currentTenantService = currentTenantService;
        _logger = logger;
    }

    private void EnsureTenantAndNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("El SuperAdministrador no tiene acceso a las métricas jurídicas de los estudios.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new UnauthorizedException("No se ha identificado el estudio jurídico.");
        }
    }

    public async Task<DashboardResumenDto> GetResumenAsync(CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;
        var nowUtc = DateTime.UtcNow;

        // -------------------------------------------------------------
        // 1. UNIVERSO EXPEDIENTES (PBAC PREVIO A AGREGACIONES)
        // -------------------------------------------------------------
        var expBaseQuery = _context.Expedientes
            .AsNoTracking()
            .Where(e => e.TenantId == currentTenantId);

        if (currentRole == Roles.AbogadoJunior)
        {
            expBaseQuery = expBaseQuery.Where(e => e.AbogadoResponsableId == currentUserId);
        }
        else if (currentRole == Roles.AsistenteLegal)
        {
            // AsistenteLegal: métricas de expediente no le pertenecen
            expBaseQuery = expBaseQuery.Where(e => false);
        }

        // -------------------------------------------------------------
        // 2. UNIVERSO TAREAS (PBAC PREVIO A AGREGACIONES)
        // -------------------------------------------------------------
        var tarBaseQuery = _context.Tareas
            .AsNoTracking()
            .Where(t => t.TenantId == currentTenantId);

        if (currentRole == Roles.AbogadoJunior)
        {
            tarBaseQuery = tarBaseQuery.Where(t => (t.Expediente != null && t.Expediente.AbogadoResponsableId == currentUserId) || t.AsignadoAUsuarioId == currentUserId);
        }
        else if (currentRole == Roles.AsistenteLegal)
        {
            tarBaseQuery = tarBaseQuery.Where(t => t.AsignadoAUsuarioId == currentUserId);
        }

        // -------------------------------------------------------------
        // 3. UNIVERSO AUDIENCIAS (PBAC PREVIO A AGREGACIONES)
        // -------------------------------------------------------------
        var audBaseQuery = _context.Audiencias
            .AsNoTracking()
            .Include(a => a.Expediente)
            .ThenInclude(e => e.AbogadoResponsable)
            .Where(a => a.TenantId == currentTenantId);

        if (currentRole == Roles.AbogadoJunior)
        {
            audBaseQuery = audBaseQuery.Where(a => a.Expediente.AbogadoResponsableId == currentUserId);
        }
        else if (currentRole == Roles.AsistenteLegal)
        {
            audBaseQuery = audBaseQuery.Where(a => false);
        }

        // -------------------------------------------------------------
        // 4. UNIVERSO ALERTAS (PBAC PREVIO A AGREGACIONES)
        // -------------------------------------------------------------
        var aleBaseQuery = _context.AlertasProcesales
            .AsNoTracking()
            .Where(a => a.TenantId == currentTenantId);

        if (currentRole == Roles.AdminEstudio || currentRole == Roles.AbogadoSenior)
        {
            aleBaseQuery = aleBaseQuery.Where(a => a.UsuarioId == currentUserId || a.UsuarioId == null);
        }
        else
        {
            aleBaseQuery = aleBaseQuery.Where(a => a.UsuarioId == currentUserId);
        }

        // -------------------------------------------------------------
        // KPIS GENERALES
        // -------------------------------------------------------------
        int totalExpedientesActivos = await expBaseQuery
            .CountAsync(e => e.Estado == EstadoExpediente.Abierto || e.Estado == EstadoExpediente.EnTramite, cancellationToken);

        int totalTareasPendientes = await tarBaseQuery
            .CountAsync(t => t.Estado == EstadoTarea.Pendiente || t.Estado == EstadoTarea.EnProgreso, cancellationToken);

        int totalAudiencias7Dias = await audBaseQuery
            .CountAsync(a => a.Estado == EstadoAudiencia.Programada && a.FechaHora >= nowUtc && a.FechaHora <= nowUtc.AddDays(7), cancellationToken);

        int totalAlertasAltaCriticas = await aleBaseQuery
            .CountAsync(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa 
                          && !a.Leida 
                          && (a.Severidad == SeveridadAlerta.Alta || a.Severidad == SeveridadAlerta.Critica), cancellationToken);

        var kpis = new KpisGeneralesDto(
            totalExpedientesActivos,
            totalTareasPendientes,
            totalAudiencias7Dias,
            totalAlertasAltaCriticas
        );

        // -------------------------------------------------------------
        // DISTRIBUCIÓN POR ESTADO DE EXPEDIENTES
        // -------------------------------------------------------------
        int abiertos = await expBaseQuery.CountAsync(e => e.Estado == EstadoExpediente.Abierto, cancellationToken);
        int enTramite = await expBaseQuery.CountAsync(e => e.Estado == EstadoExpediente.EnTramite, cancellationToken);
        int suspendidos = await expBaseQuery.CountAsync(e => e.Estado == EstadoExpediente.Suspendido, cancellationToken);
        int cerrados = await expBaseQuery.CountAsync(e => e.Estado == EstadoExpediente.Cerrado, cancellationToken);
        int archivados = await expBaseQuery.CountAsync(e => e.Estado == EstadoExpediente.Archivado, cancellationToken);

        var distribucion = new DistribucionEstadoDto(abiertos, enTramite, suspendidos, cerrados, archivados);

        // -------------------------------------------------------------
        // TAREAS PENDIENTES POR PRIORIDAD
        // -------------------------------------------------------------
        var tareasPendientesQuery = tarBaseQuery
            .Where(t => t.Estado == EstadoTarea.Pendiente || t.Estado == EstadoTarea.EnProgreso);

        int baja = await tareasPendientesQuery.CountAsync(t => t.Prioridad == Prioridad.Baja, cancellationToken);
        int media = await tareasPendientesQuery.CountAsync(t => t.Prioridad == Prioridad.Media, cancellationToken);
        int alta = await tareasPendientesQuery.CountAsync(t => t.Prioridad == Prioridad.Alta, cancellationToken);
        int urgente = await tareasPendientesQuery.CountAsync(t => t.Prioridad == Prioridad.Urgente, cancellationToken);

        var prioridades = new TareasPendientesPorPrioridadDto(baja, media, alta, urgente);

        // -------------------------------------------------------------
        // PRÓXIMAS AUDIENCIAS (TOP 5 DE LOS PRÓXIMOS 7 DÍAS)
        // -------------------------------------------------------------
        var proximasAudienciasList = await audBaseQuery
            .Where(a => a.Estado == EstadoAudiencia.Programada && a.FechaHora >= nowUtc && a.FechaHora <= nowUtc.AddDays(7))
            .OrderBy(a => a.FechaHora)
            .Take(5)
            .Select(a => new ProximaAudienciaDto(
                a.Id,
                $"Audiencia: {a.TipoAudiencia}",
                a.FechaHora,
                a.SalaOVirtual,
                a.Expediente != null ? a.Expediente.NumeroExpediente : null,
                a.Expediente != null && a.Expediente.AbogadoResponsable != null ? a.Expediente.AbogadoResponsable.NombreCompleto : null
            ))
            .ToListAsync(cancellationToken);

        // -------------------------------------------------------------
        // MÉTRICAS DE INACTIVIDAD OPERATIVA (30 Y 60 DÍAS)
        // -------------------------------------------------------------
        var expedientesActivosParaInactividad = await expBaseQuery
            .Include(e => e.Tareas)
            .Include(e => e.Audiencias)
            .Include(e => e.Documentos)
            .Include(e => e.ProcesosVinculados)
            .Where(e => e.Estado == EstadoExpediente.Abierto || e.Estado == EstadoExpediente.EnTramite)
            .ToListAsync(cancellationToken);

        int inactivos30 = 0;
        int inactivos60 = 0;

        foreach (var exp in expedientesActivosParaInactividad)
        {
            var ultimaActividad = CalcularUltimaActividadOperativa(exp);
            var dias = (nowUtc - ultimaActividad).TotalDays;
            if (dias >= 30)
            {
                inactivos30++;
            }
            if (dias >= 60)
            {
                inactivos60++;
            }
        }

        var inactividad = new MetricasInactividadDto(inactivos30, inactivos60);

        // -------------------------------------------------------------
        // MÉTRICAS DE EFICIENCIA OPERATIVA (ÚLTIMOS 90 DÍAS POR DEFECTO)
        // -------------------------------------------------------------
        var eficiencia = await CalcularEficienciaInternaAsync(expBaseQuery, null, null, cancellationToken);

        return new DashboardResumenDto(
            kpis,
            distribucion,
            prioridades,
            proximasAudienciasList,
            inactividad,
            eficiencia
        );
    }

    public async Task<MetricasEficienciaDto> GetEficienciaAsync(EficienciaRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        var expBaseQuery = _context.Expedientes
            .AsNoTracking()
            .Where(e => e.TenantId == currentTenantId);

        if (currentRole == Roles.AbogadoJunior)
        {
            expBaseQuery = expBaseQuery.Where(e => e.AbogadoResponsableId == currentUserId);
        }
        else if (currentRole == Roles.AsistenteLegal)
        {
            expBaseQuery = expBaseQuery.Where(e => false);
        }

        // Validaciones de rango
        if (request.FechaDesdeUtc.HasValue && request.FechaHastaUtc.HasValue)
        {
            if (request.FechaHastaUtc.Value < request.FechaDesdeUtc.Value)
            {
                throw new ValidationException(new[] { "La fecha final no puede ser menor a la fecha inicial." });
            }

            if ((request.FechaHastaUtc.Value - request.FechaDesdeUtc.Value).TotalDays > 365)
            {
                throw new ValidationException(new[] { "El período de cálculo de eficiencia no puede superar los 365 días." });
            }
        }

        return await CalcularEficienciaInternaAsync(expBaseQuery, request.FechaDesdeUtc, request.FechaHastaUtc, cancellationToken);
    }

    private static async Task<MetricasEficienciaDto> CalcularEficienciaInternaAsync(
        IQueryable<Expediente> expQuery,
        DateTime? desdeUtc,
        DateTime? hastaUtc,
        CancellationToken cancellationToken)
    {
        var fin = hastaUtc ?? DateTime.UtcNow;
        var inicio = desdeUtc ?? fin.AddDays(-90);

        var cerradosEvaluados = await expQuery
            .Where(e => e.Estado == EstadoExpediente.Cerrado
                     && e.FechaCierreReal.HasValue
                     && e.FechaCierreReal.Value >= inicio
                     && e.FechaCierreReal.Value <= fin)
            .ToListAsync(cancellationToken);

        int totalEvaluados = cerradosEvaluados.Count;
        var conPlazo = cerradosEvaluados.Where(e => e.FechaCierreEstimada.HasValue).ToList();
        int totalConPlazo = conPlazo.Count;

        // Regla: Si el denominador es 0, PorcentajeCumplimiento = null (NO 0%)
        if (totalConPlazo == 0)
        {
            return new MetricasEficienciaDto(
                totalEvaluados,
                0,
                0,
                null
            );
        }

        int cumplidos = conPlazo.Count(e => e.FechaCierreReal!.Value <= e.FechaCierreEstimada!.Value);
        double porcentaje = Math.Round((double)cumplidos / totalConPlazo * 100.0, 2);

        return new MetricasEficienciaDto(
            totalEvaluados,
            cumplidos,
            totalConPlazo,
            porcentaje
        );
    }

    private static DateTime CalcularUltimaActividadOperativa(Expediente exp)
    {
        var fechas = new List<DateTime>
        {
            exp.UpdatedAt ?? exp.CreatedAt
        };

        if (exp.Tareas != null && exp.Tareas.Count > 0)
        {
            fechas.Add(exp.Tareas.Max(t => t.UpdatedAt ?? t.CreatedAt));
        }

        if (exp.Audiencias != null && exp.Audiencias.Count > 0)
        {
            fechas.Add(exp.Audiencias.Max(a => a.FechaHora));
        }

        if (exp.Documentos != null && exp.Documentos.Count > 0)
        {
            fechas.Add(exp.Documentos.Max(d => d.CreatedAt));
        }

        if (exp.ProcesosVinculados != null && exp.ProcesosVinculados.Count > 0)
        {
            fechas.Add(exp.ProcesosVinculados.Max(v => v.FechaVinculacion));
        }

        return fechas.Max();
    }
}
