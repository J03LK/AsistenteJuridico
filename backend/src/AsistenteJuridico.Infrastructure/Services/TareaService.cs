using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Tareas.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using AppValidationException = AsistenteJuridico.Application.Common.Exceptions.ValidationException;

namespace AsistenteJuridico.Infrastructure.Services;

public class TareaService : ITareaService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IExpedienteAccessService _expedienteAccessService;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateTareaDto> _createValidator;
    private readonly IValidator<UpdateTareaDto> _updateValidator;
    private readonly IValidator<CambiarEstadoTareaDto> _cambiarEstadoValidator;

    public TareaService(
        ApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IExpedienteAccessService expedienteAccessService,
        IAuditService auditService,
        IValidator<CreateTareaDto> createValidator,
        IValidator<UpdateTareaDto> updateValidator,
        IValidator<CambiarEstadoTareaDto> cambiarEstadoValidator)
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
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos jurídicos ni tareas.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new ForbiddenException("Contexto de Tenant no especificado.");
        }
    }

    public async Task<PagedResult<TareaDto>> GetTareasPagedAsync(TareaFilterRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var query = _context.Tareas
            .AsNoTracking()
            .Include(t => t.Expediente)
            .Include(t => t.AsignadoA)
            .AsQueryable();

        // Restricción AbogadoJunior: expedientes asignados o tareas asignadas
        if (_currentUserService.Role == Roles.AbogadoJunior)
        {
            var userId = _currentUserService.UserId;
            query = query.Where(t => (t.Expediente != null && t.Expediente.AbogadoResponsableId == userId) || t.AsignadoAUsuarioId == userId);
        }
        else if (request.AsignadoAUsuarioId.HasValue)
        {
            query = query.Where(t => t.AsignadoAUsuarioId == request.AsignadoAUsuarioId.Value);
        }

        if (request.ExpedienteId.HasValue)
        {
            query = query.Where(t => t.ExpedienteId == request.ExpedienteId.Value);
        }

        if (request.Estado.HasValue)
        {
            query = query.Where(t => t.Estado == request.Estado.Value);
        }

        if (request.VencimientoDesde.HasValue)
        {
            query = query.Where(t => t.FechaVencimiento >= request.VencimientoDesde.Value);
        }

        if (request.VencimientoHasta.HasValue)
        {
            query = query.Where(t => t.FechaVencimiento <= request.VencimientoHasta.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var search = request.SearchTerm.Trim().ToLower();
            query = query.Where(t => t.Titulo.ToLower().Contains(search) || (t.Descripcion != null && t.Descripcion.ToLower().Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(t => t.FechaVencimiento)
            .ThenBy(t => t.Id)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(t => new TareaDto(
                t.Id,
                t.TenantId,
                t.ExpedienteId,
                t.Expediente != null ? t.Expediente.NumeroExpediente : null,
                t.Expediente != null ? t.Expediente.Titulo : null,
                t.AsignadoAUsuarioId,
                t.AsignadoA != null ? t.AsignadoA.NombreCompleto : null,
                t.Titulo,
                t.Descripcion,
                t.FechaVencimiento,
                t.Prioridad,
                t.Prioridad.ToString(),
                t.Estado,
                t.Estado.ToString(),
                t.FechaCompletada,
                t.CreatedAt,
                t.UpdatedAt,
                t.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<TareaDto>(items, totalCount, request.PageNumber, request.PageSize);
    }

    public async Task<TareaDto> GetTareaByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tareaValidada = await _expedienteAccessService.EnsureCanAccessTareaAsync(id, requireWriteAccess: false, cancellationToken);

        var tarea = await _context.Tareas
            .AsNoTracking()
            .Include(t => t.Expediente)
            .Include(t => t.AsignadoA)
            .Where(t => t.Id == id)
            .Select(t => new TareaDto(
                t.Id,
                t.TenantId,
                t.ExpedienteId,
                t.Expediente != null ? t.Expediente.NumeroExpediente : null,
                t.Expediente != null ? t.Expediente.Titulo : null,
                t.AsignadoAUsuarioId,
                t.AsignadoA != null ? t.AsignadoA.NombreCompleto : null,
                t.Titulo,
                t.Descripcion,
                t.FechaVencimiento,
                t.Prioridad,
                t.Prioridad.ToString(),
                t.Estado,
                t.Estado.ToString(),
                t.FechaCompletada,
                t.CreatedAt,
                t.UpdatedAt,
                t.Version))
            .FirstOrDefaultAsync(cancellationToken);

        if (tarea == null)
        {
            throw new NotFoundException(nameof(Tarea), id);
        }

        return tarea;
    }

    public async Task<TareaDto> CreateTareaAsync(CreateTareaDto dto, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var validationResult = await _createValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(dto.ExpedienteId, requireWriteAccess: true, cancellationToken);

        var tenantId = _currentTenantService.TenantId!.Value;

        if (dto.AsignadoAUsuarioId.HasValue)
        {
            var exists = await _context.Usuarios
                .AnyAsync(u => u.Id == dto.AsignadoAUsuarioId.Value && u.TenantId == tenantId && u.Activo, cancellationToken);

            if (!exists)
            {
                throw new NotFoundException("El usuario asignado no existe o no pertenece a este estudio.");
            }
        }

        var tarea = new Tarea
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExpedienteId = dto.ExpedienteId,
            AsignadoAUsuarioId = dto.AsignadoAUsuarioId,
            Titulo = dto.Titulo.Trim(),
            Descripcion = dto.Descripcion?.Trim(),
            FechaVencimiento = dto.FechaVencimiento,
            Prioridad = dto.Prioridad,
            Estado = EstadoTarea.Pendiente,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = _currentUserService.Email
        };

        _context.Tareas.Add(tarea);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Tarea", tarea.Id.ToString(), "CREATE", null, new
        {
            tarea.Titulo,
            tarea.ExpedienteId,
            tarea.FechaVencimiento,
            tarea.AsignadoAUsuarioId
        }, cancellationToken);

        var expediente = await _context.Expedientes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == dto.ExpedienteId, cancellationToken);

        return new TareaDto(
            tarea.Id,
            tarea.TenantId,
            tarea.ExpedienteId,
            expediente?.NumeroExpediente,
            expediente?.Titulo,
            tarea.AsignadoAUsuarioId,
            null,
            tarea.Titulo,
            tarea.Descripcion,
            tarea.FechaVencimiento,
            tarea.Prioridad,
            tarea.Prioridad.ToString(),
            tarea.Estado,
            tarea.Estado.ToString(),
            tarea.FechaCompletada,
            tarea.CreatedAt,
            tarea.UpdatedAt,
            tarea.Version);
    }

    public async Task<TareaDto> UpdateTareaAsync(Guid id, UpdateTareaDto dto, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessTareaAsync(id, requireWriteAccess: true, cancellationToken);

        var validationResult = await _updateValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        Tarea tarea = null!;
        object valoresAnteriores = null!;

        // La estrategia de reintentos de Npgsql no admite transacciones abiertas fuera de ella: la carga, la
        // transacción y el guardado forman una sola unidad que, ante un fallo transitorio, se repite entera.
        var primerIntento = true;
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            if (!primerIntento)
            {
                // El intento fallido ya se revirtió en la base de datos; también se descarta su estado en memoria.
                _context.ChangeTracker.Clear();
            }
            primerIntento = false;

            tarea = await _context.Tareas
                .Include(t => t.Expediente)
                .Include(t => t.AsignadoA)
                .FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException(nameof(Tarea), id);

            if (dto.AsignadoAUsuarioId.HasValue && dto.AsignadoAUsuarioId != tarea.AsignadoAUsuarioId)
            {
                var exists = await _context.Usuarios
                    .AnyAsync(u => u.Id == dto.AsignadoAUsuarioId.Value && u.TenantId == tarea.TenantId && u.Activo, ct);

                if (!exists)
                {
                    throw new NotFoundException("El usuario asignado no existe o no pertenece a este estudio.");
                }
            }

            valoresAnteriores = new
            {
                tarea.Titulo,
                tarea.FechaVencimiento,
                tarea.Prioridad,
                tarea.AsignadoAUsuarioId
            };

            bool fechaCambio = tarea.FechaVencimiento != dto.FechaVencimiento;
            var now = DateTime.UtcNow;

            // Si algo falla antes del commit, la transacción se revierte al liberarse.
            await using var tx = await _context.Database.BeginTransactionAsync(ct);

            if (fechaCambio)
            {
                var alertasActivas = await _context.AlertasProcesales
                    .Where(a => a.TenantId == tarea.TenantId
                             && a.TipoOrigen == TipoOrigenAlerta.Tarea
                             && a.OrigenId == tarea.Id
                             && a.EstadoResolucion == EstadoAlertaResolucion.Activa)
                    .ToListAsync(ct);

                foreach (var a in alertasActivas)
                {
                    a.EstadoResolucion = EstadoAlertaResolucion.InvalidaPorReprogramacion;
                    a.ResueltaUtc = now;
                    a.MotivoResolucion = "Reprogramación de fecha de vencimiento de tarea";
                }

                // Si la nueva fecha vence en menos de 48h o ya está vencida, generar alerta inmediata
                if (dto.FechaVencimiento < now)
                {
                    if (dto.AsignadoAUsuarioId.HasValue)
                    {
                        _context.AlertasProcesales.Add(new AlertaProcesal
                        {
                            TenantId = tarea.TenantId,
                            UsuarioId = dto.AsignadoAUsuarioId.Value,
                            TipoOrigen = TipoOrigenAlerta.Tarea,
                            OrigenId = tarea.Id,
                            ReglaAlerta = ReglaAlertaCodigo.TareaVencida,
                            FechaObjetivoUtc = dto.FechaVencimiento,
                            FechaDisparoUtc = now,
                            Titulo = $"TAREA VENCIDA: {dto.Titulo.Trim()}",
                            Mensaje = $"El término de la tarea '{dto.Titulo.Trim()}' feneció el {dto.FechaVencimiento:yyyy-MM-dd HH:mm} UTC.",
                            Severidad = SeveridadAlerta.Critica,
                            ExpedienteId = tarea.ExpedienteId,
                            EstadoResolucion = EstadoAlertaResolucion.Activa
                        });
                    }
                }
                else if (dto.FechaVencimiento <= now.AddHours(48))
                {
                    if (dto.AsignadoAUsuarioId.HasValue)
                    {
                        _context.AlertasProcesales.Add(new AlertaProcesal
                        {
                            TenantId = tarea.TenantId,
                            UsuarioId = dto.AsignadoAUsuarioId.Value,
                            TipoOrigen = TipoOrigenAlerta.Tarea,
                            OrigenId = tarea.Id,
                            ReglaAlerta = ReglaAlertaCodigo.Tarea48Horas,
                            FechaObjetivoUtc = dto.FechaVencimiento,
                            FechaDisparoUtc = now,
                            Titulo = $"Tarea próxima a vencer: {dto.Titulo.Trim()}",
                            Mensaje = $"El término operativo de la tarea '{dto.Titulo.Trim()}' vence el {dto.FechaVencimiento:yyyy-MM-dd HH:mm} UTC.",
                            Severidad = SeveridadAlerta.Media,
                            ExpedienteId = tarea.ExpedienteId,
                            EstadoResolucion = EstadoAlertaResolucion.Activa
                        });
                    }
                }
            }

            _context.Entry(tarea).Property(e => e.Version).OriginalValue = dto.Version;

            tarea.Titulo = dto.Titulo.Trim();
            tarea.Descripcion = dto.Descripcion?.Trim();
            tarea.FechaVencimiento = dto.FechaVencimiento;
            tarea.Prioridad = dto.Prioridad;
            tarea.AsignadoAUsuarioId = dto.AsignadoAUsuarioId;
            tarea.UpdatedAt = now;
            tarea.UpdatedBy = _currentUserService.Email;

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }, cancellationToken);

        await _auditService.LogAsync("Tarea", tarea.Id.ToString(), "UPDATE", valoresAnteriores, new
        {
            tarea.Titulo,
            tarea.FechaVencimiento,
            tarea.Prioridad,
            tarea.AsignadoAUsuarioId
        }, cancellationToken);

        return new TareaDto(
            tarea.Id,
            tarea.TenantId,
            tarea.ExpedienteId,
            tarea.Expediente?.NumeroExpediente,
            tarea.Expediente?.Titulo,
            tarea.AsignadoAUsuarioId,
            tarea.AsignadoA != null ? tarea.AsignadoA.NombreCompleto : null,
            tarea.Titulo,
            tarea.Descripcion,
            tarea.FechaVencimiento,
            tarea.Prioridad,
            tarea.Prioridad.ToString(),
            tarea.Estado,
            tarea.Estado.ToString(),
            tarea.FechaCompletada,
            tarea.CreatedAt,
            tarea.UpdatedAt,
            tarea.Version);
    }

    public async Task<TareaDto> CambiarEstadoAsync(Guid id, CambiarEstadoTareaDto dto, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessTareaAsync(id, requireWriteAccess: true, cancellationToken);

        var validationResult = await _cambiarEstadoValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        Tarea tarea = null!;
        var estadoAnterior = default(EstadoTarea);

        // Misma unidad reintentable que en UpdateTareaAsync: carga, transacción y guardado.
        var primerIntento = true;
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            if (!primerIntento)
            {
                // El intento fallido ya se revirtió en la base de datos; también se descarta su estado en memoria.
                _context.ChangeTracker.Clear();
            }
            primerIntento = false;

            tarea = await _context.Tareas
                .Include(t => t.Expediente)
                .Include(t => t.AsignadoA)
                .FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException(nameof(Tarea), id);

            estadoAnterior = tarea.Estado;
            var now = DateTime.UtcNow;

            // Si algo falla antes del commit, la transacción se revierte al liberarse.
            await using var tx = await _context.Database.BeginTransactionAsync(ct);

            if (dto.NuevoEstado == EstadoTarea.Completada || dto.NuevoEstado == EstadoTarea.Cancelada)
            {
                var alertasActivas = await _context.AlertasProcesales
                    .Where(a => a.TenantId == tarea.TenantId
                             && a.TipoOrigen == TipoOrigenAlerta.Tarea
                             && a.OrigenId == tarea.Id
                             && a.EstadoResolucion == EstadoAlertaResolucion.Activa)
                    .ToListAsync(ct);

                foreach (var a in alertasActivas)
                {
                    a.EstadoResolucion = EstadoAlertaResolucion.ResueltaAutomaticamente;
                    a.ResueltaUtc = now;
                    a.MotivoResolucion = $"Tarea en estado {dto.NuevoEstado}";

                    await _auditService.LogAsync(
                        "AlertaProcesal",
                        a.Id.ToString(),
                        "ALERTA_RESOLUCION_AUTOMATICA",
                        null,
                        new { alertaId = a.Id, motivo = a.MotivoResolucion },
                        ct);
                }
            }

            _context.Entry(tarea).Property(e => e.Version).OriginalValue = dto.Version;

            tarea.Estado = dto.NuevoEstado;
            if (dto.NuevoEstado == EstadoTarea.Completada)
            {
                tarea.FechaCompletada = now;
            }
            else if (estadoAnterior == EstadoTarea.Completada && dto.NuevoEstado != EstadoTarea.Completada)
            {
                tarea.FechaCompletada = null;
            }

            tarea.UpdatedAt = now;
            tarea.UpdatedBy = _currentUserService.Email;

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }, cancellationToken);

        await _auditService.LogAsync("Tarea", tarea.Id.ToString(), "STATE_CHANGE",
            new { estadoAnterior = estadoAnterior.ToString() },
            new { nuevoEstado = dto.NuevoEstado.ToString() },
            cancellationToken);

        if (dto.NuevoEstado == EstadoTarea.Completada)
        {
            await _auditService.LogAsync("Tarea", tarea.Id.ToString(), "COMPLETE",
                new { estadoAnterior = estadoAnterior.ToString() },
                new { estado = EstadoTarea.Completada.ToString(), fechaCompletada = tarea.FechaCompletada },
                cancellationToken);
        }

        return new TareaDto(
            tarea.Id,
            tarea.TenantId,
            tarea.ExpedienteId,
            tarea.Expediente?.NumeroExpediente,
            tarea.Expediente?.Titulo,
            tarea.AsignadoAUsuarioId,
            tarea.AsignadoA != null ? tarea.AsignadoA.NombreCompleto : null,
            tarea.Titulo,
            tarea.Descripcion,
            tarea.FechaVencimiento,
            tarea.Prioridad,
            tarea.Prioridad.ToString(),
            tarea.Estado,
            tarea.Estado.ToString(),
            tarea.FechaCompletada,
            tarea.CreatedAt,
            tarea.UpdatedAt,
            tarea.Version);
    }

    public async Task DeleteTareaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessTareaAsync(id, requireWriteAccess: true, cancellationToken);

        var tarea = await _context.Tareas.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tarea == null)
        {
            throw new NotFoundException(nameof(Tarea), id);
        }

        _context.Tareas.Remove(tarea);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Tarea", tarea.Id.ToString(), "DELETE", new { tarea.Titulo }, null, cancellationToken);
    }
}
