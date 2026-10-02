using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using AppValidationException = AsistenteJuridico.Application.Common.Exceptions.ValidationException;

namespace AsistenteJuridico.Infrastructure.Services;

public class AudienciaService : IAudienciaService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IExpedienteAccessService _expedienteAccessService;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateAudienciaDto> _createValidator;
    private readonly IValidator<UpdateAudienciaDto> _updateValidator;
    private readonly IValidator<CambiarEstadoAudienciaDto> _cambiarEstadoValidator;

    public AudienciaService(
        ApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IExpedienteAccessService expedienteAccessService,
        IAuditService auditService,
        IValidator<CreateAudienciaDto> createValidator,
        IValidator<UpdateAudienciaDto> updateValidator,
        IValidator<CambiarEstadoAudienciaDto> cambiarEstadoValidator)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _expedienteAccessService = expedienteAccessService;
        _auditService = auditService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _cambiarEstadoValidator = cambiarEstadoValidator;
    }

    private void EnsureTenantAndNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos jurídicos ni audiencias.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new ForbiddenException("Contexto de Tenant no especificado.");
        }
    }

    public async Task<PagedResult<AudienciaDto>> GetAudienciasPagedAsync(AudienciaFilterRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var query = _context.Audiencias
            .AsNoTracking()
            .Include(a => a.Expediente)
            .Include(a => a.ProcesoJudicial)
            .AsQueryable();

        // Restricción AbogadoJunior
        if (_currentUserService.Role == Roles.AbogadoJunior)
        {
            var userId = _currentUserService.UserId;
            query = query.Where(a => a.Expediente.AbogadoResponsableId == userId);
        }

        if (request.ExpedienteId.HasValue)
        {
            query = query.Where(a => a.ExpedienteId == request.ExpedienteId.Value);
        }

        if (request.Estado.HasValue)
        {
            query = query.Where(a => a.Estado == request.Estado.Value);
        }

        if (request.FechaDesde.HasValue)
        {
            query = query.Where(a => a.FechaHora >= request.FechaDesde.Value);
        }

        if (request.FechaHasta.HasValue)
        {
            query = query.Where(a => a.FechaHora <= request.FechaHasta.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var search = request.SearchTerm.Trim().ToLower();
            query = query.Where(a =>
                a.SalaOVirtual.ToLower().Contains(search) ||
                (a.Notas != null && a.Notas.ToLower().Contains(search)) ||
                a.Expediente.NumeroExpediente.ToLower().Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(a => a.FechaHora)
            .ThenBy(a => a.Id)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(a => new AudienciaDto(
                a.Id,
                a.TenantId,
                a.ExpedienteId,
                a.Expediente.NumeroExpediente,
                a.Expediente.Titulo,
                a.ProcesoJudicialId,
                a.ProcesoJudicial != null ? a.ProcesoJudicial.NumeroProceso : null,
                a.FechaHora,
                a.SalaOVirtual,
                a.TipoAudiencia,
                a.TipoAudiencia.ToString(),
                a.Estado,
                a.Estado.ToString(),
                a.Notas,
                a.CreatedAt,
                a.UpdatedAt,
                a.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<AudienciaDto>(items, totalCount, request.PageNumber, request.PageSize);
    }

    public async Task<AudienciaDto> GetAudienciaByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var audienciaValidada = await _expedienteAccessService.EnsureCanAccessAudienciaAsync(id, requireWriteAccess: false, cancellationToken);

        var audiencia = await _context.Audiencias
            .AsNoTracking()
            .Include(a => a.Expediente)
            .Include(a => a.ProcesoJudicial)
            .Where(a => a.Id == id)
            .Select(a => new AudienciaDto(
                a.Id,
                a.TenantId,
                a.ExpedienteId,
                a.Expediente.NumeroExpediente,
                a.Expediente.Titulo,
                a.ProcesoJudicialId,
                a.ProcesoJudicial != null ? a.ProcesoJudicial.NumeroProceso : null,
                a.FechaHora,
                a.SalaOVirtual,
                a.TipoAudiencia,
                a.TipoAudiencia.ToString(),
                a.Estado,
                a.Estado.ToString(),
                a.Notas,
                a.CreatedAt,
                a.UpdatedAt,
                a.Version))
            .FirstOrDefaultAsync(cancellationToken);

        if (audiencia == null)
        {
            throw new NotFoundException(nameof(Audiencia), id);
        }

        return audiencia;
    }

    public async Task<AudienciaDto> CreateAudienciaAsync(CreateAudienciaDto dto, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var validationResult = await _createValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId, requireWriteAccess: true, cancellationToken);

        if (dto.ProcesoJudicialId.HasValue)
        {
            await _expedienteAccessService.EnsureCanAccessProcesoAsync(dto.ProcesoJudicialId.Value, cancellationToken);
        }

        var tenantId = _currentTenantService.TenantId!.Value;

        var audiencia = new Audiencia
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = dto.ExpedienteId,
            ProcesoJudicialId = dto.ProcesoJudicialId,
            FechaHora = dto.FechaHora,
            SalaOVirtual = dto.SalaOVirtual.Trim(),
            TipoAudiencia = dto.TipoAudiencia,
            Estado = EstadoAudiencia.Programada,
            Notas = dto.Notas?.Trim(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = _currentUserService.Email
        };

        _context.Audiencias.Add(audiencia);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Audiencia", audiencia.Id.ToString(), "CREATE", null, new
        {
            audiencia.ExpedienteId,
            audiencia.FechaHora,
            audiencia.TipoAudiencia
        }, cancellationToken);

        var expediente = await _context.Expedientes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == dto.ExpedienteId, cancellationToken);
        var proceso = dto.ProcesoJudicialId.HasValue
            ? await _context.ProcesosJudiciales.AsNoTracking().FirstOrDefaultAsync(p => p.Id == dto.ProcesoJudicialId.Value, cancellationToken)
            : null;

        return new AudienciaDto(
            audiencia.Id,
            audiencia.TenantId,
            audiencia.ExpedienteId,
            expediente?.NumeroExpediente ?? string.Empty,
            expediente?.Titulo ?? string.Empty,
            audiencia.ProcesoJudicialId,
            proceso?.NumeroProceso,
            audiencia.FechaHora,
            audiencia.SalaOVirtual,
            audiencia.TipoAudiencia,
            audiencia.TipoAudiencia.ToString(),
            audiencia.Estado,
            audiencia.Estado.ToString(),
            audiencia.Notas,
            audiencia.CreatedAt,
            audiencia.UpdatedAt,
            audiencia.Version);
    }

    public async Task<AudienciaDto> UpdateAudienciaAsync(Guid id, UpdateAudienciaDto dto, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessAudienciaAsync(id, requireWriteAccess: true, cancellationToken);

        var validationResult = await _updateValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        if (dto.ProcesoJudicialId.HasValue)
        {
            await _expedienteAccessService.EnsureCanAccessProcesoAsync(dto.ProcesoJudicialId.Value, cancellationToken);
        }

        var audiencia = await _context.Audiencias
            .Include(a => a.Expediente)
            .Include(a => a.ProcesoJudicial)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (audiencia == null)
        {
            throw new NotFoundException(nameof(Audiencia), id);
        }

        var valoresAnteriores = new
        {
            audiencia.FechaHora,
            audiencia.SalaOVirtual,
            audiencia.TipoAudiencia
        };

        bool fechaCambio = audiencia.FechaHora != dto.FechaHora;
        var now = DateTime.UtcNow;

        await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (fechaCambio)
            {
                var alertasActivas = await _context.AlertasProcesales
                    .Where(a => a.TenantId == audiencia.TenantId
                             && a.TipoOrigen == TipoOrigenAlerta.Audiencia
                             && a.OrigenId == audiencia.Id
                             && a.EstadoResolucion == EstadoAlertaResolucion.Activa)
                    .ToListAsync(cancellationToken);

                foreach (var a in alertasActivas)
                {
                    a.EstadoResolucion = EstadoAlertaResolucion.InvalidaPorReprogramacion;
                    a.ResueltaUtc = now;
                    a.MotivoResolucion = "Reprogramación de fecha de audiencia";
                }

                // Si la nueva fecha cae en una ventana inmediata de 24h, 48h o 7d, generar alerta inmediata
                if (dto.FechaHora >= now && dto.FechaHora <= now.AddDays(7))
                {
                    var regla = dto.FechaHora <= now.AddHours(24) ? ReglaAlertaCodigo.Audiencia24Horas
                              : dto.FechaHora <= now.AddHours(48) ? ReglaAlertaCodigo.Audiencia48Horas
                              : ReglaAlertaCodigo.Audiencia7Dias;
                    var severidad = regla == ReglaAlertaCodigo.Audiencia24Horas ? SeveridadAlerta.Alta
                                  : regla == ReglaAlertaCodigo.Audiencia48Horas ? SeveridadAlerta.Media
                                  : SeveridadAlerta.Baja;

                    if (audiencia.Expediente != null && audiencia.Expediente.AbogadoResponsableId.HasValue)
                    {
                        _context.AlertasProcesales.Add(new AlertaProcesal
                        {
                            TenantId = audiencia.TenantId,
                            UsuarioId = audiencia.Expediente.AbogadoResponsableId.Value,
                            TipoOrigen = TipoOrigenAlerta.Audiencia,
                            OrigenId = audiencia.Id,
                            ReglaAlerta = regla,
                            FechaObjetivoUtc = dto.FechaHora,
                            FechaDisparoUtc = now,
                            Titulo = $"Audiencia reprogramada ({dto.TipoAudiencia})",
                            Mensaje = $"La audiencia '{dto.TipoAudiencia}' fue reprogramada para el {dto.FechaHora:yyyy-MM-dd HH:mm} UTC.",
                            Severidad = severidad,
                            ExpedienteId = audiencia.ExpedienteId,
                            EstadoResolucion = EstadoAlertaResolucion.Activa
                        });
                    }

                    _context.AlertasProcesales.Add(new AlertaProcesal
                    {
                        TenantId = audiencia.TenantId,
                        UsuarioId = null,
                        TipoOrigen = TipoOrigenAlerta.Audiencia,
                        OrigenId = audiencia.Id,
                        ReglaAlerta = regla,
                        FechaObjetivoUtc = dto.FechaHora,
                        FechaDisparoUtc = now,
                        Titulo = $"[ESTUDIO] Audiencia reprogramada ({dto.TipoAudiencia})",
                        Mensaje = $"La audiencia '{dto.TipoAudiencia}' en {dto.SalaOVirtual} fue reprogramada para el {dto.FechaHora:yyyy-MM-dd HH:mm} UTC.",
                        Severidad = severidad,
                        ExpedienteId = audiencia.ExpedienteId,
                        EstadoResolucion = EstadoAlertaResolucion.Activa
                    });
                }
            }

            _context.Entry(audiencia).Property(e => e.Version).OriginalValue = dto.Version;

            audiencia.ProcesoJudicialId = dto.ProcesoJudicialId;
            audiencia.FechaHora = dto.FechaHora;
            audiencia.SalaOVirtual = dto.SalaOVirtual.Trim();
            audiencia.TipoAudiencia = dto.TipoAudiencia;
            audiencia.Notas = dto.Notas?.Trim();
            audiencia.UpdatedAt = now;
            audiencia.UpdatedBy = _currentUserService.Email;

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        await _auditService.LogAsync("Audiencia", audiencia.Id.ToString(), "UPDATE", valoresAnteriores, new
        {
            audiencia.FechaHora,
            audiencia.SalaOVirtual,
            audiencia.TipoAudiencia
        }, cancellationToken);

        return new AudienciaDto(
            audiencia.Id,
            audiencia.TenantId,
            audiencia.ExpedienteId,
            audiencia.Expediente?.NumeroExpediente ?? string.Empty,
            audiencia.Expediente?.Titulo ?? string.Empty,
            audiencia.ProcesoJudicialId,
            audiencia.ProcesoJudicial?.NumeroProceso,
            audiencia.FechaHora,
            audiencia.SalaOVirtual,
            audiencia.TipoAudiencia,
            audiencia.TipoAudiencia.ToString(),
            audiencia.Estado,
            audiencia.Estado.ToString(),
            audiencia.Notas,
            audiencia.CreatedAt,
            audiencia.UpdatedAt,
            audiencia.Version);
    }

    public async Task<AudienciaDto> CambiarEstadoAsync(Guid id, CambiarEstadoAudienciaDto dto, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessAudienciaAsync(id, requireWriteAccess: true, cancellationToken);

        var validationResult = await _cambiarEstadoValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var audiencia = await _context.Audiencias
            .Include(a => a.Expediente)
            .Include(a => a.ProcesoJudicial)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (audiencia == null)
        {
            throw new NotFoundException(nameof(Audiencia), id);
        }

        var estadoAnterior = audiencia.Estado;
        var now = DateTime.UtcNow;

        await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (dto.NuevoEstado == EstadoAudiencia.Cancelada || dto.NuevoEstado == EstadoAudiencia.Realizada)
            {
                var alertasActivas = await _context.AlertasProcesales
                    .Where(a => a.TenantId == audiencia.TenantId
                             && a.TipoOrigen == TipoOrigenAlerta.Audiencia
                             && a.OrigenId == audiencia.Id
                             && a.EstadoResolucion == EstadoAlertaResolucion.Activa)
                    .ToListAsync(cancellationToken);

                foreach (var a in alertasActivas)
                {
                    a.EstadoResolucion = EstadoAlertaResolucion.ResueltaAutomaticamente;
                    a.ResueltaUtc = now;
                    a.MotivoResolucion = $"Audiencia en estado {dto.NuevoEstado}";

                    await _auditService.LogAsync(
                        "AlertaProcesal",
                        a.Id.ToString(),
                        "ALERTA_RESOLUCION_AUTOMATICA",
                        null,
                        new { alertaId = a.Id, motivo = a.MotivoResolucion },
                        cancellationToken);
                }
            }

            _context.Entry(audiencia).Property(e => e.Version).OriginalValue = dto.Version;

            audiencia.Estado = dto.NuevoEstado;
            audiencia.UpdatedAt = now;
            audiencia.UpdatedBy = _currentUserService.Email;

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        await _auditService.LogAsync("Audiencia", audiencia.Id.ToString(), "STATE_CHANGE",
            new { estadoAnterior = estadoAnterior.ToString() },
            new { nuevoEstado = dto.NuevoEstado.ToString() },
            cancellationToken);

        if (dto.NuevoEstado == EstadoAudiencia.Realizada)
        {
            await _auditService.LogAsync("Audiencia", audiencia.Id.ToString(), "COMPLETE",
                new { estadoAnterior = estadoAnterior.ToString() },
                new { estado = EstadoAudiencia.Realizada.ToString() },
                cancellationToken);
        }

        return new AudienciaDto(
            audiencia.Id,
            audiencia.TenantId,
            audiencia.ExpedienteId,
            audiencia.Expediente.NumeroExpediente,
            audiencia.Expediente.Titulo,
            audiencia.ProcesoJudicialId,
            audiencia.ProcesoJudicial?.NumeroProceso,
            audiencia.FechaHora,
            audiencia.SalaOVirtual,
            audiencia.TipoAudiencia,
            audiencia.TipoAudiencia.ToString(),
            audiencia.Estado,
            audiencia.Estado.ToString(),
            audiencia.Notas,
            audiencia.CreatedAt,
            audiencia.UpdatedAt,
            audiencia.Version);
    }

    public async Task DeleteAudienciaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessAudienciaAsync(id, requireWriteAccess: true, cancellationToken);

        var audiencia = await _context.Audiencias.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (audiencia == null)
        {
            throw new NotFoundException(nameof(Audiencia), id);
        }

        _context.Audiencias.Remove(audiencia);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Audiencia", audiencia.Id.ToString(), "DELETE", new { audiencia.FechaHora, audiencia.SalaOVirtual }, null, cancellationToken);
    }
}
