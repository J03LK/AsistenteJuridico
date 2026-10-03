using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Alertas.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AsistenteJuridico.Infrastructure.Persistence;

namespace AsistenteJuridico.Infrastructure.Services;

public class AlertasService : IAlertasService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly IAuditService _auditService;
    private readonly ILogger<AlertasService> _logger;

    public AlertasService(
        ApplicationDbContext context,
        ICurrentUserService currentUserService,
        ICurrentTenantService currentTenantService,
        IAuditService auditService,
        ILogger<AlertasService> logger)
    {
        _context = context;
        _currentUserService = currentUserService;
        _currentTenantService = currentTenantService;
        _auditService = auditService;
        _logger = logger;
    }

    private void EnsureTenantAndNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("El SuperAdministrador no tiene acceso a las alertas jurídicas de los estudios.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new UnauthorizedException("No se ha identificado el estudio jurídico.");
        }
    }

    public async Task<IReadOnlyList<AlertaProcesalDto>> GetAlertasAsync(AlertasFilterRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        var query = _context.AlertasProcesales
            .AsNoTracking()
            .Include(a => a.Usuario)
            .Include(a => a.Expediente)
            .Where(a => a.TenantId == currentTenantId);

        // PBAC: Alcance de datos por rol
        if (currentRole == Roles.AdminEstudio || currentRole == Roles.AbogadoSenior)
        {
            // Ven sus alertas directas y las institucionales del estudio
            query = query.Where(a => a.UsuarioId == currentUserId || a.UsuarioId == null);
        }
        else
        {
            // Junior y Asistente: ESTRICTAMENTE alertas dirigidas a ellos mismos
            query = query.Where(a => a.UsuarioId == currentUserId);
        }

        if (request.SoloNoLeidas.HasValue && request.SoloNoLeidas.Value)
        {
            query = query.Where(a => !a.Leida);
        }

        if (!request.IncluirResueltas)
        {
            query = query.Where(a => a.EstadoResolucion == EstadoAlertaResolucion.Activa);
        }

        int pageSize = Math.Clamp(request.PageSize, 1, 100);
        int pageNumber = Math.Max(request.PageNumber, 1);

        var items = await query
            .OrderByDescending(a => a.FechaDisparoUtc)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AlertaProcesalDto(
                a.Id,
                a.TenantId,
                a.UsuarioId,
                a.Usuario != null ? a.Usuario.NombreCompleto : (a.UsuarioId == null ? "Supervisión Estudio" : null),
                a.TipoOrigen,
                a.OrigenId,
                a.ReglaAlerta,
                a.FechaObjetivoUtc,
                a.FechaDisparoUtc,
                a.Titulo,
                a.Mensaje,
                a.Severidad,
                a.ExpedienteId,
                a.Expediente != null ? a.Expediente.NumeroExpediente : null,
                a.EstadoResolucion,
                a.ResueltaUtc,
                a.MotivoResolucion,
                a.Leida,
                a.FechaLeidaUtc,
                a.Version
            ))
            .ToListAsync(cancellationToken);

        return items;
    }

    public async Task<ConteoNoLeidasDto> GetConteoNoLeidasAsync(CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        var query = _context.AlertasProcesales
            .AsNoTracking()
            .Where(a => a.TenantId == currentTenantId
                     && a.EstadoResolucion == EstadoAlertaResolucion.Activa
                     && !a.Leida);

        if (currentRole == Roles.AdminEstudio || currentRole == Roles.AbogadoSenior)
        {
            query = query.Where(a => a.UsuarioId == currentUserId || a.UsuarioId == null);
        }
        else
        {
            query = query.Where(a => a.UsuarioId == currentUserId);
        }

        int total = await query.CountAsync(cancellationToken);
        return new ConteoNoLeidasDto(total);
    }

    public async Task<AlertaProcesalDto> MarcarLeidaAsync(Guid id, uint? version = null, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        var alerta = await _context.AlertasProcesales
            .Include(a => a.Usuario)
            .Include(a => a.Expediente)
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == currentTenantId, cancellationToken);

        if (alerta == null)
        {
            throw new NotFoundException("Alerta", id);
        }

        // Validación PBAC
        if (currentRole == Roles.AbogadoJunior || currentRole == Roles.AsistenteLegal)
        {
            if (alerta.UsuarioId != currentUserId)
            {
                throw new ForbiddenException("No tiene permisos para modificar esta alerta.");
            }
        }
        else if (currentRole == Roles.AdminEstudio || currentRole == Roles.AbogadoSenior)
        {
            if (alerta.UsuarioId.HasValue && alerta.UsuarioId != currentUserId)
            {
                throw new ForbiddenException("No tiene permisos para marcar como leída una alerta asignada a otro usuario.");
            }
        }

        if (version.HasValue)
        {
            _context.Entry(alerta).Property(e => e.Version).OriginalValue = version.Value;
        }

        if (!alerta.Leida)
        {
            alerta.Leida = true;
            alerta.FechaLeidaUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(
                "AlertaProcesal",
                alerta.Id.ToString(),
                "MARCAR_ALERTA_LEIDA",
                null,
                new { alertaId = alerta.Id, fechaLeidaUtc = alerta.FechaLeidaUtc },
                cancellationToken);
        }

        return MapToDto(alerta);
    }

    public async Task<AlertaProcesalDto> DescartarAlertaAsync(Guid id, string? motivo, uint? version = null, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        var alerta = await _context.AlertasProcesales
            .Include(a => a.Usuario)
            .Include(a => a.Expediente)
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == currentTenantId, cancellationToken);

        if (alerta == null)
        {
            throw new NotFoundException("Alerta", id);
        }

        // Validación PBAC
        if (currentRole == Roles.AbogadoJunior || currentRole == Roles.AsistenteLegal)
        {
            if (alerta.UsuarioId != currentUserId)
            {
                throw new ForbiddenException("No tiene permisos para descartar esta alerta.");
            }
        }
        else if (currentRole == Roles.AdminEstudio || currentRole == Roles.AbogadoSenior)
        {
            if (alerta.UsuarioId.HasValue && alerta.UsuarioId != currentUserId)
            {
                throw new ForbiddenException("No tiene permisos para descartar una alerta asignada a otro usuario.");
            }
        }

        if (version.HasValue)
        {
            _context.Entry(alerta).Property(e => e.Version).OriginalValue = version.Value;
        }

        alerta.EstadoResolucion = EstadoAlertaResolucion.DescartadaManualmente;
        alerta.ResueltaUtc = DateTime.UtcNow;
        alerta.MotivoResolucion = string.IsNullOrWhiteSpace(motivo) ? "Descartada manualmente por el usuario" : motivo.Trim();

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            "AlertaProcesal",
            alerta.Id.ToString(),
            "DESCARTAR_ALERTA",
            null,
            new { alertaId = alerta.Id, motivo = alerta.MotivoResolucion },
            cancellationToken);

        return MapToDto(alerta);
    }

    public async Task<MarcarTodasLeidasResponseDto> MarcarTodasLeidasAsync(MarcarTodasLeidasRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        var query = _context.AlertasProcesales
            .Where(a => a.TenantId == currentTenantId
                     && a.EstadoResolucion == EstadoAlertaResolucion.Activa
                     && !a.Leida);

        if (currentRole == Roles.AdminEstudio || currentRole == Roles.AbogadoSenior)
        {
            query = request.IncluirInstitucionales
                ? query.Where(a => a.UsuarioId == currentUserId || a.UsuarioId == null)
                : query.Where(a => a.UsuarioId == currentUserId);
        }
        else
        {
            query = query.Where(a => a.UsuarioId == currentUserId);
        }

        int afectadas = await query.ExecuteUpdateAsync(s => s
            .SetProperty(a => a.Leida, true)
            .SetProperty(a => a.FechaLeidaUtc, DateTime.UtcNow), cancellationToken);

        if (afectadas > 0)
        {
            await _auditService.LogAsync(
                "AlertaProcesal",
                "ALL",
                "MARCAR_TODAS_LEIDAS",
                null,
                new { totalAfectadas = afectadas, incluirInstitucionales = request.IncluirInstitucionales },
                cancellationToken);
        }

        return new MarcarTodasLeidasResponseDto(afectadas);
    }

    public async Task InvalidarAlertasPorReprogramacionAsync(
        Guid tenantId,
        TipoOrigenAlerta tipoOrigen,
        Guid origenId,
        string motivo,
        CancellationToken cancellationToken = default)
    {
        var alertasActivas = await _context.AlertasProcesales
            .Where(a => a.TenantId == tenantId
                     && a.TipoOrigen == tipoOrigen
                     && a.OrigenId == origenId
                     && a.EstadoResolucion == EstadoAlertaResolucion.Activa)
            .ToListAsync(cancellationToken);

        if (alertasActivas.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var alerta in alertasActivas)
            {
                alerta.EstadoResolucion = EstadoAlertaResolucion.InvalidaPorReprogramacion;
                alerta.ResueltaUtc = now;
                alerta.MotivoResolucion = motivo;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ProcesarReglasAlertasTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var nuevasAlertas = new List<AlertaProcesal>();

        // -------------------------------------------------------------
        // 1. AUDIENCIAS
        // -------------------------------------------------------------
        var audiencias = await _context.Audiencias
            .AsNoTracking()
            .Include(a => a.Expediente)
            .Where(a => a.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        foreach (var aud in audiencias)
        {
            // Auto-resolución si la audiencia fue cancelada o realizada
            if (aud.Estado == EstadoAudiencia.Cancelada || aud.Estado == EstadoAudiencia.Realizada)
            {
                await ResolverAlertasActivasOrigenAsync(tenantId, TipoOrigenAlerta.Audiencia, aud.Id,
                    $"Audiencia en estado {aud.Estado}", cancellationToken);
                continue;
            }

            if (aud.Estado != EstadoAudiencia.Programada) continue;

            // Regla: Audiencia7Dias (now <= FechaHora <= now + 7d)
            if (aud.FechaHora >= nowUtc && aud.FechaHora <= nowUtc.AddDays(7))
            {
                await EvaluarEInsertarAlertaAudienciaAsync(tenantId, aud, ReglaAlertaCodigo.Audiencia7Dias, SeveridadAlerta.Baja,
                    $"Audiencia próxima en 7 días ({aud.TipoAudiencia})",
                    $"La audiencia '{aud.TipoAudiencia}' en {aud.SalaOVirtual} está programada para el {aud.FechaHora:yyyy-MM-dd HH:mm} UTC.",
                    nuevasAlertas, cancellationToken);
            }

            // Regla: Audiencia48Horas (now <= FechaHora <= now + 48h)
            if (aud.FechaHora >= nowUtc && aud.FechaHora <= nowUtc.AddHours(48))
            {
                await EvaluarEInsertarAlertaAudienciaAsync(tenantId, aud, ReglaAlertaCodigo.Audiencia48Horas, SeveridadAlerta.Media,
                    $"Audiencia urgente en 48 horas ({aud.TipoAudiencia})",
                    $"Preparación requerida: Audiencia '{aud.TipoAudiencia}' programada para el {aud.FechaHora:yyyy-MM-dd HH:mm} UTC.",
                    nuevasAlertas, cancellationToken);
            }

            // Regla: Audiencia24Horas (now <= FechaHora <= now + 24h)
            if (aud.FechaHora >= nowUtc && aud.FechaHora <= nowUtc.AddHours(24))
            {
                await EvaluarEInsertarAlertaAudienciaAsync(tenantId, aud, ReglaAlertaCodigo.Audiencia24Horas, SeveridadAlerta.Alta,
                    $"Audiencia inminente en 24 horas ({aud.TipoAudiencia})",
                    $"Comparecencia inminente: Audiencia '{aud.TipoAudiencia}' el {aud.FechaHora:yyyy-MM-dd HH:mm} UTC en {aud.SalaOVirtual}.",
                    nuevasAlertas, cancellationToken);
            }
        }

        // -------------------------------------------------------------
        // 2. TAREAS
        // -------------------------------------------------------------
        var tareas = await _context.Tareas
            .AsNoTracking()
            .Include(t => t.Expediente)
            .Where(t => t.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        foreach (var tar in tareas)
        {
            // Auto-resolución si la tarea fue completada o cancelada
            if (tar.Estado == EstadoTarea.Completada || tar.Estado == EstadoTarea.Cancelada)
            {
                await ResolverAlertasActivasOrigenAsync(tenantId, TipoOrigenAlerta.Tarea, tar.Id,
                    $"Tarea en estado {tar.Estado}", cancellationToken);
                continue;
            }

            // Regla: Tarea48Horas (now <= FechaVencimiento <= now + 48h)
            if (tar.FechaVencimiento >= nowUtc && tar.FechaVencimiento <= nowUtc.AddHours(48))
            {
                if (tar.AsignadoAUsuarioId.HasValue)
                {
                    await EvaluarEInsertarAlertaTareaAsync(tenantId, tar, tar.AsignadoAUsuarioId.Value,
                        ReglaAlertaCodigo.Tarea48Horas, SeveridadAlerta.Media,
                        $"Tarea próxima a vencer: {tar.Titulo}",
                        $"El término operativo de la tarea '{tar.Titulo}' vence el {tar.FechaVencimiento:yyyy-MM-dd HH:mm} UTC.",
                        nuevasAlertas, cancellationToken);
                }
            }

            // Regla: TareaVencida (FechaVencimiento < now)
            if (tar.FechaVencimiento < nowUtc)
            {
                if (tar.AsignadoAUsuarioId.HasValue)
                {
                    await EvaluarEInsertarAlertaTareaAsync(tenantId, tar, tar.AsignadoAUsuarioId.Value,
                        ReglaAlertaCodigo.TareaVencida, SeveridadAlerta.Critica,
                        $"TAREA VENCIDA: {tar.Titulo}",
                        $"El término de la tarea '{tar.Titulo}' feneció el {tar.FechaVencimiento:yyyy-MM-dd HH:mm} UTC y permanece sin completar.",
                        nuevasAlertas, cancellationToken);
                }

                // Notificación al abogado responsable si es distinto al asignado
                if (tar.Expediente != null && tar.Expediente.AbogadoResponsableId.HasValue &&
                    tar.Expediente.AbogadoResponsableId != tar.AsignadoAUsuarioId)
                {
                    await EvaluarEInsertarAlertaTareaAsync(tenantId, tar, tar.Expediente.AbogadoResponsableId.Value,
                        ReglaAlertaCodigo.TareaVencida, SeveridadAlerta.Critica,
                        $"TAREA VENCIDA EN EXPEDIENTE: {tar.Titulo}",
                        $"En el expediente '{tar.Expediente.NumeroExpediente}', la tarea '{tar.Titulo}' feneció el {tar.FechaVencimiento:yyyy-MM-dd HH:mm} UTC.",
                        nuevasAlertas, cancellationToken);
                }
            }
        }

        // -------------------------------------------------------------
        // 3. INACTIVIDAD OPERATIVA DE EXPEDIENTES
        // -------------------------------------------------------------
        var expedientes = await _context.Expedientes
            .AsNoTracking()
            .Include(e => e.Tareas)
            .Include(e => e.Audiencias)
            .Include(e => e.Documentos)
            .Include(e => e.ProcesosVinculados)
            .Where(e => e.TenantId == tenantId && (e.Estado == EstadoExpediente.Abierto || e.Estado == EstadoExpediente.EnTramite))
            .ToListAsync(cancellationToken);

        foreach (var exp in expedientes)
        {
            var ultimaActividad = CalcularUltimaActividadOperativa(exp);
            var diasInactivo = (nowUtc - ultimaActividad).TotalDays;

            if (diasInactivo >= 30)
            {
                var fechaObjetivo = ultimaActividad.AddDays(30);
                await EvaluarEInsertarAlertaInactividadAsync(tenantId, exp, ReglaAlertaCodigo.InactividadOperativa30Dias,
                    SeveridadAlerta.Alta, fechaObjetivo,
                    $"Expediente con 30 días de inactividad: {exp.NumeroExpediente}",
                    $"El expediente '{exp.NumeroExpediente}' ({exp.Titulo}) no registra actividad operativa interna desde hace más de 30 días.",
                    nuevasAlertas, cancellationToken);
            }

            if (diasInactivo >= 60)
            {
                var fechaObjetivo = ultimaActividad.AddDays(60);
                await EvaluarEInsertarAlertaInactividadAsync(tenantId, exp, ReglaAlertaCodigo.InactividadOperativa60Dias,
                    SeveridadAlerta.Critica, fechaObjetivo,
                    $"EXPEDIENTE CON 60 DÍAS DE INACTIVIDAD: {exp.NumeroExpediente}",
                    $"El expediente '{exp.NumeroExpediente}' ({exp.Titulo}) no registra actividad operativa interna desde hace más de 60 días (Última actividad: {ultimaActividad:yyyy-MM-dd}).",
                    nuevasAlertas, cancellationToken);
            }

            if (diasInactivo < 30)
            {
                // Auto-resolución si el expediente volvió a registrar actividad
                await ResolverAlertasActivasOrigenAsync(tenantId, TipoOrigenAlerta.Expediente, exp.Id,
                    "Nueva actividad operativa registrada en expediente", cancellationToken);
            }
        }

        if (nuevasAlertas.Count > 0)
        {
            _context.AlertasProcesales.AddRange(nuevasAlertas);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("[WORKER_ALERTAS] Tenant {TenantId}: Se insertaron {TotalNuevas} nuevas alertas procesales.",
                tenantId, nuevasAlertas.Count);
        }
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

    private async Task EvaluarEInsertarAlertaAudienciaAsync(
        Guid tenantId,
        Audiencia aud,
        ReglaAlertaCodigo regla,
        SeveridadAlerta severidad,
        string titulo,
        string mensaje,
        List<AlertaProcesal> bufferNuevas,
        CancellationToken cancellationToken)
    {
        // 1. Destinatario: Abogado Responsable (si existe)
        if (aud.Expediente != null && aud.Expediente.AbogadoResponsableId.HasValue)
        {
            var abogadoId = aud.Expediente.AbogadoResponsableId.Value;
            bool yaExiste = await ExisteAlertaHistoricaAsync(tenantId, TipoOrigenAlerta.Audiencia, aud.Id, regla, abogadoId, aud.FechaHora, cancellationToken)
                || bufferNuevas.Any(b => b.TenantId == tenantId && b.TipoOrigen == TipoOrigenAlerta.Audiencia && b.OrigenId == aud.Id && b.ReglaAlerta == regla && b.UsuarioId == abogadoId && b.FechaObjetivoUtc == aud.FechaHora);

            if (!yaExiste)
            {
                bufferNuevas.Add(new AlertaProcesal
                {
                    TenantId = tenantId,
                    UsuarioId = abogadoId,
                    TipoOrigen = TipoOrigenAlerta.Audiencia,
                    OrigenId = aud.Id,
                    ReglaAlerta = regla,
                    FechaObjetivoUtc = aud.FechaHora,
                    FechaDisparoUtc = DateTime.UtcNow,
                    Titulo = titulo,
                    Mensaje = mensaje,
                    Severidad = severidad,
                    ExpedienteId = aud.ExpedienteId,
                    EstadoResolucion = EstadoAlertaResolucion.Activa
                });
            }
        }

        // 2. Destinatario: Alerta Institucional de Supervisión (UsuarioId = null)
        bool existeGeneral = await ExisteAlertaHistoricaAsync(tenantId, TipoOrigenAlerta.Audiencia, aud.Id, regla, null, aud.FechaHora, cancellationToken)
            || bufferNuevas.Any(b => b.TenantId == tenantId && b.TipoOrigen == TipoOrigenAlerta.Audiencia && b.OrigenId == aud.Id && b.ReglaAlerta == regla && b.UsuarioId == null && b.FechaObjetivoUtc == aud.FechaHora);

        if (!existeGeneral)
        {
            bufferNuevas.Add(new AlertaProcesal
            {
                TenantId = tenantId,
                UsuarioId = null,
                TipoOrigen = TipoOrigenAlerta.Audiencia,
                OrigenId = aud.Id,
                ReglaAlerta = regla,
                FechaObjetivoUtc = aud.FechaHora,
                FechaDisparoUtc = DateTime.UtcNow,
                Titulo = $"[ESTUDIO] {titulo}",
                Mensaje = mensaje,
                Severidad = severidad,
                ExpedienteId = aud.ExpedienteId,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
        }
    }

    private async Task EvaluarEInsertarAlertaTareaAsync(
        Guid tenantId,
        Tarea tar,
        Guid usuarioDestinatarioId,
        ReglaAlertaCodigo regla,
        SeveridadAlerta severidad,
        string titulo,
        string mensaje,
        List<AlertaProcesal> bufferNuevas,
        CancellationToken cancellationToken)
    {
        bool yaExiste = await ExisteAlertaHistoricaAsync(tenantId, TipoOrigenAlerta.Tarea, tar.Id, regla, usuarioDestinatarioId, tar.FechaVencimiento, cancellationToken)
            || bufferNuevas.Any(b => b.TenantId == tenantId && b.TipoOrigen == TipoOrigenAlerta.Tarea && b.OrigenId == tar.Id && b.ReglaAlerta == regla && b.UsuarioId == usuarioDestinatarioId && b.FechaObjetivoUtc == tar.FechaVencimiento);

        if (!yaExiste)
        {
            bufferNuevas.Add(new AlertaProcesal
            {
                TenantId = tenantId,
                UsuarioId = usuarioDestinatarioId,
                TipoOrigen = TipoOrigenAlerta.Tarea,
                OrigenId = tar.Id,
                ReglaAlerta = regla,
                FechaObjetivoUtc = tar.FechaVencimiento,
                FechaDisparoUtc = DateTime.UtcNow,
                Titulo = titulo,
                Mensaje = mensaje,
                Severidad = severidad,
                ExpedienteId = tar.ExpedienteId,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
        }
    }

    private async Task EvaluarEInsertarAlertaInactividadAsync(
        Guid tenantId,
        Expediente exp,
        ReglaAlertaCodigo regla,
        SeveridadAlerta severidad,
        DateTime fechaObjetivoUtc,
        string titulo,
        string mensaje,
        List<AlertaProcesal> bufferNuevas,
        CancellationToken cancellationToken)
    {
        // 1. Destinatario directo si tiene abogado
        if (exp.AbogadoResponsableId.HasValue)
        {
            var abogadoId = exp.AbogadoResponsableId.Value;
            bool yaExiste = await ExisteAlertaHistoricaAsync(tenantId, TipoOrigenAlerta.Expediente, exp.Id, regla, abogadoId, fechaObjetivoUtc, cancellationToken)
                || bufferNuevas.Any(b => b.TenantId == tenantId && b.TipoOrigen == TipoOrigenAlerta.Expediente && b.OrigenId == exp.Id && b.ReglaAlerta == regla && b.UsuarioId == abogadoId && b.FechaObjetivoUtc == fechaObjetivoUtc);

            if (!yaExiste)
            {
                bufferNuevas.Add(new AlertaProcesal
                {
                    TenantId = tenantId,
                    UsuarioId = abogadoId,
                    TipoOrigen = TipoOrigenAlerta.Expediente,
                    OrigenId = exp.Id,
                    ReglaAlerta = regla,
                    FechaObjetivoUtc = fechaObjetivoUtc,
                    FechaDisparoUtc = DateTime.UtcNow,
                    Titulo = titulo,
                    Mensaje = mensaje,
                    Severidad = severidad,
                    ExpedienteId = exp.Id,
                    EstadoResolucion = EstadoAlertaResolucion.Activa
                });
            }
        }

        // 2. Destinatario institucional (Supervisión)
        bool existeGeneral = await ExisteAlertaHistoricaAsync(tenantId, TipoOrigenAlerta.Expediente, exp.Id, regla, null, fechaObjetivoUtc, cancellationToken)
            || bufferNuevas.Any(b => b.TenantId == tenantId && b.TipoOrigen == TipoOrigenAlerta.Expediente && b.OrigenId == exp.Id && b.ReglaAlerta == regla && b.UsuarioId == null && b.FechaObjetivoUtc == fechaObjetivoUtc);

        if (!existeGeneral)
        {
            bufferNuevas.Add(new AlertaProcesal
            {
                TenantId = tenantId,
                UsuarioId = null,
                TipoOrigen = TipoOrigenAlerta.Expediente,
                OrigenId = exp.Id,
                ReglaAlerta = regla,
                FechaObjetivoUtc = fechaObjetivoUtc,
                FechaDisparoUtc = DateTime.UtcNow,
                Titulo = $"[ESTUDIO] {titulo}",
                Mensaje = mensaje,
                Severidad = severidad,
                ExpedienteId = exp.Id,
                EstadoResolucion = EstadoAlertaResolucion.Activa
            });
        }
    }

    private async Task<bool> ExisteAlertaHistoricaAsync(
        Guid tenantId,
        TipoOrigenAlerta tipoOrigen,
        Guid origenId,
        ReglaAlertaCodigo regla,
        Guid? usuarioId,
        DateTime fechaObjetivoUtc,
        CancellationToken cancellationToken)
    {
        // Unicidad histórica: No debe estar en InvalidaPorReprogramacion (Estado 3)
        return await _context.AlertasProcesales
            .AnyAsync(a => a.TenantId == tenantId
                        && a.TipoOrigen == tipoOrigen
                        && a.OrigenId == origenId
                        && a.ReglaAlerta == regla
                        && a.UsuarioId == usuarioId
                        && a.FechaObjetivoUtc == fechaObjetivoUtc
                        && a.EstadoResolucion != EstadoAlertaResolucion.InvalidaPorReprogramacion, cancellationToken);
    }

    private async Task ResolverAlertasActivasOrigenAsync(
        Guid tenantId,
        TipoOrigenAlerta tipoOrigen,
        Guid origenId,
        string motivo,
        CancellationToken cancellationToken)
    {
        var activas = await _context.AlertasProcesales
            .Where(a => a.TenantId == tenantId
                     && a.TipoOrigen == tipoOrigen
                     && a.OrigenId == origenId
                     && a.EstadoResolucion == EstadoAlertaResolucion.Activa)
            .ToListAsync(cancellationToken);

        if (activas.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var a in activas)
            {
                a.EstadoResolucion = EstadoAlertaResolucion.ResueltaAutomaticamente;
                a.ResueltaUtc = now;
                a.MotivoResolucion = motivo;

                await _auditService.LogInTransactionAsync(
                    "AlertaProcesal",
                    a.Id.ToString(),
                    "ALERTA_RESOLUCION_AUTOMATICA",
                    null,
                    new { alertaId = a.Id, motivo = a.MotivoResolucion },
                    cancellationToken);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private static AlertaProcesalDto MapToDto(AlertaProcesal a)
    {
        return new AlertaProcesalDto(
            a.Id,
            a.TenantId,
            a.UsuarioId,
            a.Usuario != null ? a.Usuario.NombreCompleto : (a.UsuarioId == null ? "Supervisión Estudio" : null),
            a.TipoOrigen,
            a.OrigenId,
            a.ReglaAlerta,
            a.FechaObjetivoUtc,
            a.FechaDisparoUtc,
            a.Titulo,
            a.Mensaje,
            a.Severidad,
            a.ExpedienteId,
            a.Expediente != null ? a.Expediente.NumeroExpediente : null,
            a.EstadoResolucion,
            a.ResueltaUtc,
            a.MotivoResolucion,
            a.Leida,
            a.FechaLeidaUtc,
            a.Version
        );
    }
}
