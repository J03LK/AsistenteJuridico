using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Agenda.DTOs;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Common;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services;

public class AgendaService : IAgendaService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ILogger<AgendaService> _logger;

    public AgendaService(
        ApplicationDbContext context,
        ICurrentUserService currentUserService,
        ICurrentTenantService currentTenantService,
        ILogger<AgendaService> logger)
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
            throw new ForbiddenException("El SuperAdministrador no tiene acceso a la agenda jurídica de los estudios.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new UnauthorizedException("No se ha identificado el estudio jurídico.");
        }
    }

    public async Task<AgendaPaginadaDto> GetEventosAsync(AgendaFilterRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        // Límite duro máximo de 500 registros, default 200
        int pageSize = request.PageSize <= 0 ? 200 : Math.Min(request.PageSize, 500);
        int pageNumber = Math.Max(request.PageNumber, 1);

        // Control de Scope para AbogadoJunior: no puede ampliar scope a otros abogados
        Guid? filtroAbogadoId = request.AbogadoId;
        if (currentRole == Roles.AbogadoJunior)
        {
            filtroAbogadoId = currentUserId;
        }

        var listaEventos = new List<EventoAgendaDto>();

        // 1. CONSULTA DE AUDIENCIAS
        if (!request.TipoEvento.HasValue || request.TipoEvento.Value == TipoEventoAgenda.Audiencia)
        {
            var audQuery = _context.Audiencias
                .AsNoTracking()
                .Include(a => a.Expediente)
                .ThenInclude(e => e.AbogadoResponsable)
                .Where(a => a.TenantId == currentTenantId
                         && a.FechaHora >= request.FechaDesdeUtc
                         && a.FechaHora <= request.FechaHastaUtc);

            if (currentRole == Roles.AbogadoJunior)
            {
                audQuery = audQuery.Where(a => a.Expediente.AbogadoResponsableId == currentUserId);
            }
            else if (filtroAbogadoId.HasValue)
            {
                audQuery = audQuery.Where(a => a.Expediente.AbogadoResponsableId == filtroAbogadoId.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Materia))
            {
                audQuery = audQuery.Where(a => a.Expediente.Materia == request.Materia.Trim());
            }

            var audiencias = await audQuery.ToListAsync(cancellationToken);
            foreach (var a in audiencias)
            {
                listaEventos.Add(new EventoAgendaDto(
                    a.Id,
                    TipoEventoAgenda.Audiencia,
                    $"Audiencia: {a.TipoAudiencia}",
                    a.Notas,
                    a.FechaHora,
                    a.FechaHora.AddHours(1),
                    a.SalaOVirtual,
                    MapearEstadoAudiencia(a.Estado),
                    null,
                    a.ExpedienteId,
                    a.Expediente?.NumeroExpediente,
                    a.Expediente?.Titulo,
                    a.Expediente?.Materia,
                    a.Expediente?.AbogadoResponsableId,
                    a.Expediente?.AbogadoResponsable?.NombreCompleto
                ));
            }
        }

        // 2. CONSULTA DE TAREAS
        if (!request.TipoEvento.HasValue || request.TipoEvento.Value == TipoEventoAgenda.Tarea)
        {
            var tarQuery = _context.Tareas
                .AsNoTracking()
                .Include(t => t.Expediente)
                .Include(t => t.AsignadoA)
                .Where(t => t.TenantId == currentTenantId
                         && t.FechaVencimiento >= request.FechaDesdeUtc
                         && t.FechaVencimiento <= request.FechaHastaUtc);

            if (currentRole == Roles.AbogadoJunior)
            {
                tarQuery = tarQuery.Where(t => (t.Expediente != null && t.Expediente.AbogadoResponsableId == currentUserId) || t.AsignadoAUsuarioId == currentUserId);
            }
            else if (currentRole == Roles.AsistenteLegal)
            {
                // AsistenteLegal: tareas directamente asignadas
                tarQuery = tarQuery.Where(t => t.AsignadoAUsuarioId == currentUserId);
            }
            else if (filtroAbogadoId.HasValue)
            {
                tarQuery = tarQuery.Where(t => (t.Expediente != null && t.Expediente.AbogadoResponsableId == filtroAbogadoId.Value) || t.AsignadoAUsuarioId == filtroAbogadoId.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Materia))
            {
                tarQuery = tarQuery.Where(t => t.Expediente != null && t.Expediente.Materia == request.Materia.Trim());
            }

            var tareas = await tarQuery.ToListAsync(cancellationToken);
            foreach (var t in tareas)
            {
                listaEventos.Add(new EventoAgendaDto(
                    t.Id,
                    TipoEventoAgenda.Tarea,
                    $"Tarea: {t.Titulo}",
                    t.Descripcion,
                    t.FechaVencimiento,
                    null,
                    null,
                    MapearEstadoTarea(t.Estado),
                    t.Prioridad.ToString(),
                    t.ExpedienteId,
                    t.Expediente?.NumeroExpediente,
                    t.Expediente?.Titulo,
                    t.Expediente?.Materia,
                    t.AsignadoAUsuarioId,
                    t.AsignadoA?.NombreCompleto
                ));
            }
        }

        int totalCount = listaEventos.Count;

        var itemsPaginados = listaEventos
            .OrderBy(e => e.FechaHoraInicioUtc)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new AgendaPaginadaDto(itemsPaginados, totalCount, pageNumber, pageSize);
    }

    public async Task<IReadOnlyList<EventoAgendaDto>> GetEventosHoyAsync(CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();
        var currentTenantId = _currentTenantService.TenantId!.Value;
        var currentUserId = _currentUserService.UserId;
        var currentRole = _currentUserService.Role;

        var tenant = await _context.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == currentTenantId, cancellationToken);

        if (tenant == null)
        {
            throw new NotFoundException("Tenant", currentTenantId);
        }

        // Resolución estricta de ZonaHorariaId sin fallback silencioso
        if (!TimeZoneHelper.TryGetTimeZone(tenant.ZonaHorariaId, out var tz))
        {
            _logger.LogError("[TENANT_CONFIG_ERROR] Tenant {TenantId} posee ZonaHorariaId inválida '{ZonaHorariaId}'. Solicitud abortada.",
                currentTenantId, tenant.ZonaHorariaId);

            throw new TenantTimeZoneInvalidException(
                $"La zona horaria configurada para el estudio jurídico ('{tenant.ZonaHorariaId}') no es un identificador IANA válido. Contacte al administrador.");
        }

        // Cálculo exacto del intervalo semiabierto [desdeUtc, siguienteDiaUtc)
        var ahoraUtc = DateTime.UtcNow;
        var ahoraLocal = TimeZoneInfo.ConvertTimeFromUtc(ahoraUtc, tz);
        var inicioDiaLocal = ahoraLocal.Date;
        var siguienteDiaLocal = inicioDiaLocal.AddDays(1);

        var desdeUtc = TimeZoneInfo.ConvertTimeToUtc(inicioDiaLocal, tz);
        var siguienteDiaUtc = TimeZoneInfo.ConvertTimeToUtc(siguienteDiaLocal, tz);

        var listaEventos = new List<EventoAgendaDto>();

        // 1. Audiencias de hoy (semiabierto, sin canceladas)
        var audQuery = _context.Audiencias
            .AsNoTracking()
            .Include(a => a.Expediente)
            .ThenInclude(e => e.AbogadoResponsable)
            .Where(a => a.TenantId == currentTenantId
                     && a.FechaHora >= desdeUtc
                     && a.FechaHora < siguienteDiaUtc
                     && a.Estado != EstadoAudiencia.Cancelada);

        if (currentRole == Roles.AbogadoJunior)
        {
            audQuery = audQuery.Where(a => a.Expediente.AbogadoResponsableId == currentUserId);
        }

        var audiencias = await audQuery.ToListAsync(cancellationToken);
        foreach (var a in audiencias)
        {
            listaEventos.Add(new EventoAgendaDto(
                a.Id,
                TipoEventoAgenda.Audiencia,
                $"Audiencia: {a.TipoAudiencia}",
                a.Notas,
                a.FechaHora,
                a.FechaHora.AddHours(1),
                a.SalaOVirtual,
                MapearEstadoAudiencia(a.Estado),
                null,
                a.ExpedienteId,
                a.Expediente?.NumeroExpediente,
                a.Expediente?.Titulo,
                a.Expediente?.Materia,
                a.Expediente?.AbogadoResponsableId,
                a.Expediente?.AbogadoResponsable?.NombreCompleto
            ));
        }

        // 2. Tareas de hoy (semiabierto, sin canceladas)
        var tarQuery = _context.Tareas
            .AsNoTracking()
            .Include(t => t.Expediente)
            .Include(t => t.AsignadoA)
            .Where(t => t.TenantId == currentTenantId
                     && t.FechaVencimiento >= desdeUtc
                     && t.FechaVencimiento < siguienteDiaUtc
                     && t.Estado != EstadoTarea.Cancelada);

        if (currentRole == Roles.AbogadoJunior)
        {
            tarQuery = tarQuery.Where(t => (t.Expediente != null && t.Expediente.AbogadoResponsableId == currentUserId) || t.AsignadoAUsuarioId == currentUserId);
        }
        else if (currentRole == Roles.AsistenteLegal)
        {
            tarQuery = tarQuery.Where(t => t.AsignadoAUsuarioId == currentUserId);
        }

        var tareas = await tarQuery.ToListAsync(cancellationToken);
        foreach (var t in tareas)
        {
            listaEventos.Add(new EventoAgendaDto(
                t.Id,
                TipoEventoAgenda.Tarea,
                $"Tarea: {t.Titulo}",
                t.Descripcion,
                t.FechaVencimiento,
                null,
                null,
                MapearEstadoTarea(t.Estado),
                t.Prioridad.ToString(),
                t.ExpedienteId,
                t.Expediente?.NumeroExpediente,
                t.Expediente?.Titulo,
                t.Expediente?.Materia,
                t.AsignadoAUsuarioId,
                t.AsignadoA?.NombreCompleto
            ));
        }

        return listaEventos.OrderBy(e => e.FechaHoraInicioUtc).ToList();
    }

    public static EstadoEventoAgenda MapearEstadoAudiencia(EstadoAudiencia estado) => estado switch
    {
        EstadoAudiencia.Programada => EstadoEventoAgenda.Programada,
        EstadoAudiencia.EnCurso => EstadoEventoAgenda.EnProgreso,
        EstadoAudiencia.Realizada => EstadoEventoAgenda.RealizadaOCompletada,
        EstadoAudiencia.Cancelada => EstadoEventoAgenda.Cancelada,
        EstadoAudiencia.Suspendida => EstadoEventoAgenda.Suspendida,
        EstadoAudiencia.Diferida => EstadoEventoAgenda.Suspendida,
        _ => throw new ArgumentOutOfRangeException(nameof(estado), estado, $"Estado de audiencia no soportado: {estado}")
    };

    public static EstadoEventoAgenda MapearEstadoTarea(EstadoTarea estado) => estado switch
    {
        EstadoTarea.Pendiente => EstadoEventoAgenda.Programada,
        EstadoTarea.EnProgreso => EstadoEventoAgenda.EnProgreso,
        EstadoTarea.Completada => EstadoEventoAgenda.RealizadaOCompletada,
        EstadoTarea.Cancelada => EstadoEventoAgenda.Cancelada,
        _ => throw new ArgumentOutOfRangeException(nameof(estado), estado, $"Estado de tarea no soportado: {estado}")
    };
}
