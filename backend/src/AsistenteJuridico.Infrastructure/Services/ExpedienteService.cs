using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Clientes.DTOs;
using AsistenteJuridico.Application.Features.Expedientes.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using AppValidationException = AsistenteJuridico.Application.Common.Exceptions.ValidationException;

namespace AsistenteJuridico.Infrastructure.Services;

public class ExpedienteService : IExpedienteService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IExpedienteAccessService _expedienteAccessService;
    private readonly IExpedienteCodeGenerator _codeGenerator;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateExpedienteDto> _createValidator;
    private readonly IValidator<UpdateExpedienteDto> _updateValidator;
    private readonly IValidator<CambiarEstadoExpedienteDto> _cambiarEstadoValidator;
    private readonly IValidator<VincularProcesoDto> _vincularProcesoValidator;

    public ExpedienteService(
        ApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IExpedienteAccessService expedienteAccessService,
        IExpedienteCodeGenerator codeGenerator,
        IAuditService auditService,
        IValidator<CreateExpedienteDto> createValidator,
        IValidator<UpdateExpedienteDto> updateValidator,
        IValidator<CambiarEstadoExpedienteDto> cambiarEstadoValidator,
        IValidator<VincularProcesoDto> vincularProcesoValidator)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _expedienteAccessService = expedienteAccessService;
        _codeGenerator = codeGenerator;
        _auditService = auditService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _cambiarEstadoValidator = cambiarEstadoValidator;
        _vincularProcesoValidator = vincularProcesoValidator;
    }

    private void EnsureTenantAndNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos jurídicos ni expedientes.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new ForbiddenException("Contexto de Tenant no especificado.");
        }
    }

    public async Task<PagedResult<ExpedienteDto>> GetExpedientesPagedAsync(ExpedienteFilterRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var query = _context.Expedientes
            .AsNoTracking()
            .Include(e => e.Cliente)
            .Include(e => e.AbogadoResponsable)
            .Where(e => !e.IsDeleted);

        // Si es AbogadoJunior, restringir automáticamente a sus propios expedientes asignados
        if (_currentUserService.Role == Roles.AbogadoJunior)
        {
            var userId = _currentUserService.UserId;
            query = query.Where(e => e.AbogadoResponsableId == userId);
        }
        else if (request.AbogadoResponsableId.HasValue)
        {
            query = query.Where(e => e.AbogadoResponsableId == request.AbogadoResponsableId.Value);
        }

        if (request.Estado.HasValue)
        {
            query = query.Where(e => e.Estado == request.Estado.Value);
        }

        if (request.Prioridad.HasValue)
        {
            query = query.Where(e => e.Prioridad == request.Prioridad.Value);
        }

        if (request.ClienteId.HasValue)
        {
            query = query.Where(e => e.ClienteId == request.ClienteId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var search = request.SearchTerm.Trim().ToLower();
            query = query.Where(e =>
                e.NumeroExpediente.ToLower().Contains(search) ||
                e.Titulo.ToLower().Contains(search) ||
                e.Cliente.NombreRazonSocial.ToLower().Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(e => new ExpedienteDto(
                e.Id,
                e.TenantId,
                e.NumeroExpediente,
                e.Titulo,
                e.Descripcion,
                e.Materia,
                e.Estado,
                e.Estado.ToString(),
                e.Prioridad,
                e.Prioridad.ToString(),
                e.ClienteId,
                e.Cliente.NombreRazonSocial,
                e.AbogadoResponsableId,
                e.AbogadoResponsable != null ? e.AbogadoResponsable.NombreCompleto : null,
                e.FechaApertura,
                e.FechaCierreEstimada,
                e.FechaCierreReal,
                e.CreatedAt,
                e.UpdatedAt,
                e.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<ExpedienteDto>(items, totalCount, request.PageNumber, request.PageSize);
    }

    public async Task<ExpedienteDetailDto> GetExpedienteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(id, requireWriteAccess: false, cancellationToken);

        var expediente = await _context.Expedientes
            .AsNoTracking()
            .Include(e => e.Cliente)
            .Include(e => e.AbogadoResponsable)
            .Include(e => e.ProcesosVinculados)
                .ThenInclude(ep => ep.ProcesoJudicial)
            .Where(e => e.Id == id && !e.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken);

        if (expediente == null)
        {
            throw new NotFoundException(nameof(Expediente), id);
        }

        var tareasQuery = _context.Tareas.AsNoTracking().Where(t => t.ExpedienteId == id);
        var totalTareas = await tareasQuery.CountAsync(cancellationToken);
        var pendingTareas = await tareasQuery.CountAsync(t => t.Estado == EstadoTarea.Pendiente || t.Estado == EstadoTarea.EnProgreso, cancellationToken);
        var totalDocs = await _context.Documentos.AsNoTracking().CountAsync(d => d.ExpedienteId == id && !d.IsDeleted, cancellationToken);
        var totalAudiencias = await _context.Audiencias.AsNoTracking().CountAsync(a => a.ExpedienteId == id, cancellationToken);

        var clienteDto = new ClienteDto(
            expediente.Cliente.Id,
            expediente.Cliente.TenantId,
            expediente.Cliente.TipoIdentificacion,
            expediente.Cliente.Identificacion,
            expediente.Cliente.NombreRazonSocial,
            expediente.Cliente.Email,
            expediente.Cliente.Telefono,
            expediente.Cliente.Direccion,
            expediente.Cliente.Notas,
            expediente.Cliente.Activo,
            expediente.Cliente.CreatedAt,
            expediente.Cliente.UpdatedAt,
            expediente.Cliente.Version);

        var procesosDtos = expediente.ProcesosVinculados.Select(ep => new ExpedienteProcesoJudicialDto(
            ep.Id,
            ep.ExpedienteId,
            ep.ProcesoJudicialId,
            ep.ProcesoJudicial.NumeroProceso,
            ep.ProcesoJudicial.Judicatura,
            ep.ProcesoJudicial.AccionInfraccion,
            ep.EsPrincipal,
            ep.FechaVinculacion,
            ep.Observaciones)).ToList();

        return new ExpedienteDetailDto(
            expediente.Id,
            expediente.TenantId,
            expediente.NumeroExpediente,
            expediente.Titulo,
            expediente.Descripcion,
            expediente.Materia,
            expediente.Estado,
            expediente.Estado.ToString(),
            expediente.Prioridad,
            expediente.Prioridad.ToString(),
            expediente.ClienteId,
            expediente.Cliente.NombreRazonSocial,
            clienteDto,
            expediente.AbogadoResponsableId,
            expediente.AbogadoResponsable != null ? expediente.AbogadoResponsable.NombreCompleto : null,
            expediente.FechaApertura,
            expediente.FechaCierreEstimada,
            expediente.FechaCierreReal,
            expediente.CreatedAt,
            expediente.UpdatedAt,
            expediente.Version,
            procesosDtos,
            totalTareas,
            pendingTareas,
            totalDocs,
            totalAudiencias);
    }

    public async Task<ExpedienteDto> CreateExpedienteAsync(CreateExpedienteDto dto, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var validationResult = await _createValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var tenantId = _currentTenantService.TenantId!.Value;

        // Validar que el cliente exista en el tenant
        var cliente = await _context.Clientes
            .FirstOrDefaultAsync(c => c.Id == dto.ClienteId && c.TenantId == tenantId && !c.IsDeleted, cancellationToken);

        if (cliente == null)
        {
            throw new NotFoundException("El cliente especificado no existe o pertenece a otro estudio jurídico.");
        }

        // Si se asigna abogado, validar que pertenezca al tenant
        Guid? abogadoId = dto.AbogadoResponsableId;
        if (_currentUserService.Role == Roles.AbogadoJunior)
        {
            // El abogado junior solo puede crearse a sí mismo como responsable
            abogadoId = _currentUserService.UserId;
        }

        if (abogadoId.HasValue)
        {
            var existsAbogado = await _context.Usuarios
                .AnyAsync(u => u.Id == abogadoId.Value && u.TenantId == tenantId && u.Activo, cancellationToken);

            if (!existsAbogado)
            {
                throw new NotFoundException("El abogado responsable seleccionado no existe o no está activo en el estudio.");
            }
        }

        // Generar correlativo atómico EXP-{YYYY}-{NNNN}
        var numeroExpediente = await _codeGenerator.GenerateNextCodeAsync(tenantId, DateTime.UtcNow.Year, cancellationToken);

        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NumeroExpediente = numeroExpediente,
            Titulo = dto.Titulo.Trim(),
            Descripcion = dto.Descripcion?.Trim(),
            Materia = dto.Materia.Trim(),
            Estado = EstadoExpediente.Abierto,
            Prioridad = dto.Prioridad,
            ClienteId = dto.ClienteId,
            AbogadoResponsableId = abogadoId,
            FechaApertura = DateTime.UtcNow,
            FechaCierreEstimada = dto.FechaCierreEstimada,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = _currentUserService.Email
        };

        _context.Expedientes.Add(expediente);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Expediente", expediente.Id.ToString(), "CREATE", null, new
        {
            expediente.NumeroExpediente,
            expediente.Titulo,
            expediente.ClienteId,
            expediente.AbogadoResponsableId
        }, cancellationToken);

        if (expediente.AbogadoResponsableId.HasValue)
        {
            await _auditService.LogAsync("Expediente", expediente.Id.ToString(), "ASSIGN_LAWYER", null, new
            {
                abogadoResponsableId = expediente.AbogadoResponsableId.Value
            }, cancellationToken);
        }

        return new ExpedienteDto(
            expediente.Id,
            expediente.TenantId,
            expediente.NumeroExpediente,
            expediente.Titulo,
            expediente.Descripcion,
            expediente.Materia,
            expediente.Estado,
            expediente.Estado.ToString(),
            expediente.Prioridad,
            expediente.Prioridad.ToString(),
            expediente.ClienteId,
            cliente.NombreRazonSocial,
            expediente.AbogadoResponsableId,
            null,
            expediente.FechaApertura,
            expediente.FechaCierreEstimada,
            expediente.FechaCierreReal,
            expediente.CreatedAt,
            expediente.UpdatedAt,
            expediente.Version);
    }

    public async Task<ExpedienteDto> UpdateExpedienteAsync(Guid id, UpdateExpedienteDto dto, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(id, requireWriteAccess: true, cancellationToken);

        var validationResult = await _updateValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var expediente = await _context.Expedientes
            .Include(e => e.Cliente)
            .FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, cancellationToken);

        if (expediente == null)
        {
            throw new NotFoundException(nameof(Expediente), id);
        }

        // Si es AbogadoJunior, no puede reasignar el expediente a otro
        if (_currentUserService.Role == Roles.AbogadoJunior && dto.AbogadoResponsableId != expediente.AbogadoResponsableId)
        {
            throw new ForbiddenException("Un Abogado Junior no tiene autorización para reasignar el responsable del expediente.");
        }

        if (dto.AbogadoResponsableId.HasValue && dto.AbogadoResponsableId != expediente.AbogadoResponsableId)
        {
            var exists = await _context.Usuarios
                .AnyAsync(u => u.Id == dto.AbogadoResponsableId.Value && u.TenantId == expediente.TenantId && u.Activo, cancellationToken);

            if (!exists)
            {
                throw new NotFoundException("El abogado responsable seleccionado no existe o no está activo en el estudio.");
            }
        }

        var valoresAnteriores = new
        {
            expediente.Titulo,
            expediente.Materia,
            expediente.Prioridad,
            expediente.AbogadoResponsableId,
            expediente.FechaCierreEstimada
        };

        // Asignar versión de concurrencia optimista original
        _context.Entry(expediente).Property(e => e.Version).OriginalValue = dto.Version;

        expediente.Titulo = dto.Titulo.Trim();
        expediente.Descripcion = dto.Descripcion?.Trim();
        expediente.Materia = dto.Materia.Trim();
        expediente.Prioridad = dto.Prioridad;
        expediente.AbogadoResponsableId = dto.AbogadoResponsableId;
        expediente.FechaCierreEstimada = dto.FechaCierreEstimada;
        expediente.UpdatedAt = DateTime.UtcNow;
        expediente.UpdatedBy = _currentUserService.Email;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Expediente", expediente.Id.ToString(), "UPDATE", valoresAnteriores, new
        {
            expediente.Titulo,
            expediente.Materia,
            expediente.Prioridad,
            expediente.AbogadoResponsableId,
            expediente.FechaCierreEstimada
        }, cancellationToken);

        if (dto.AbogadoResponsableId != valoresAnteriores.AbogadoResponsableId)
        {
            await _auditService.LogAsync("Expediente", expediente.Id.ToString(), "ASSIGN_LAWYER",
                new { abogadoResponsableIdAnterior = valoresAnteriores.AbogadoResponsableId },
                new { abogadoResponsableIdNuevo = dto.AbogadoResponsableId },
                cancellationToken);
        }

        return new ExpedienteDto(
            expediente.Id,
            expediente.TenantId,
            expediente.NumeroExpediente,
            expediente.Titulo,
            expediente.Descripcion,
            expediente.Materia,
            expediente.Estado,
            expediente.Estado.ToString(),
            expediente.Prioridad,
            expediente.Prioridad.ToString(),
            expediente.ClienteId,
            expediente.Cliente.NombreRazonSocial,
            expediente.AbogadoResponsableId,
            null,
            expediente.FechaApertura,
            expediente.FechaCierreEstimada,
            expediente.FechaCierreReal,
            expediente.CreatedAt,
            expediente.UpdatedAt,
            expediente.Version);
    }

    public async Task<ExpedienteDto> CambiarEstadoAsync(Guid id, CambiarEstadoExpedienteDto dto, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(id, requireWriteAccess: true, cancellationToken);

        var validationResult = await _cambiarEstadoValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var expediente = await _context.Expedientes
            .Include(e => e.Cliente)
            .FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, cancellationToken);

        if (expediente == null)
        {
            throw new NotFoundException(nameof(Expediente), id);
        }

        if (!expediente.PuedeTransicionarA(dto.NuevoEstado))
        {
            throw new BusinessRuleException($"Transición de estado no permitida de '{expediente.Estado}' a '{dto.NuevoEstado}'.");
        }

        // Si se intenta cerrar el expediente
        if (dto.NuevoEstado == EstadoExpediente.Cerrado)
        {
            var tareasActivas = await _context.Tareas
                .Where(t => t.ExpedienteId == id && (t.Estado == EstadoTarea.Pendiente || t.Estado == EstadoTarea.EnProgreso))
                .ToListAsync(cancellationToken);

            if (tareasActivas.Count > 0)
            {
                if (!dto.ConfirmarCierreConTareasPendientes)
                {
                    throw new BusinessRuleException(
                        $"No se puede cerrar el expediente: existen {tareasActivas.Count} tarea(s) pendiente(s) o en progreso. Para forzar el cierre debe proporcionar confirmación explícita y motivo justificado.");
                }

                // Cierre forzado: Validar permiso especial
                if (!_currentUserService.HasPermission(Permissions.ExpedientesCloseForce))
                {
                    throw new ForbiddenException("No posee el permiso necesario (Expedientes.CloseForce) para realizar el cierre forzado de un expediente con tareas pendientes.");
                }

                // Cancelar transaccionalmente todas las tareas pendientes o en progreso
                foreach (var tarea in tareasActivas)
                {
                    tarea.Estado = EstadoTarea.Cancelada;
                    tarea.UpdatedAt = DateTime.UtcNow;
                    tarea.UpdatedBy = _currentUserService.Email;
                }

                // La cancelación de tareas, esta auditoría y el cierre (con su control xmin) se guardan en el mismo
                // SaveChanges: una sola transacción que se confirma o se revierte entera.
                await _auditService.LogInTransactionAsync("Expediente", expediente.Id.ToString(), "FORCE_CLOSE",
                    new { tareasCanceladas = tareasActivas.Select(t => t.Id).ToList() },
                    new { motivo = dto.MotivoCierreForzado?.Trim() },
                    cancellationToken);
            }

            expediente.FechaCierreReal = DateTime.UtcNow;
        }
        else if (expediente.Estado == EstadoExpediente.Cerrado && dto.NuevoEstado == EstadoExpediente.EnTramite)
        {
            // Reapertura
            expediente.FechaCierreReal = null;
        }

        var estadoAnterior = expediente.Estado;

        _context.Entry(expediente).Property(e => e.Version).OriginalValue = dto.Version;
        expediente.Estado = dto.NuevoEstado;
        expediente.UpdatedAt = DateTime.UtcNow;
        expediente.UpdatedBy = _currentUserService.Email;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Expediente", expediente.Id.ToString(), "STATE_CHANGE",
            new { estadoAnterior = estadoAnterior.ToString() },
            new { nuevoEstado = dto.NuevoEstado.ToString() },
            cancellationToken);

        return new ExpedienteDto(
            expediente.Id,
            expediente.TenantId,
            expediente.NumeroExpediente,
            expediente.Titulo,
            expediente.Descripcion,
            expediente.Materia,
            expediente.Estado,
            expediente.Estado.ToString(),
            expediente.Prioridad,
            expediente.Prioridad.ToString(),
            expediente.ClienteId,
            expediente.Cliente.NombreRazonSocial,
            expediente.AbogadoResponsableId,
            null,
            expediente.FechaApertura,
            expediente.FechaCierreEstimada,
            expediente.FechaCierreReal,
            expediente.CreatedAt,
            expediente.UpdatedAt,
            expediente.Version);
    }

    public Task<ExpedienteProcesoJudicialDto> VincularProcesoAsync(Guid id, VincularProcesoDto dto, CancellationToken cancellationToken = default)
        => VincularProcesoAsync(id, dto, cancellationToken, onBeforeCommit: null);

    public async Task<ExpedienteProcesoJudicialDto> VincularProcesoAsync(
        Guid id,
        VincularProcesoDto dto,
        CancellationToken cancellationToken,
        Func<Task>? onBeforeCommit)
    {
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(id, requireWriteAccess: true, cancellationToken);
        await _expedienteAccessService.EnsureCanAccessProcesoAsync(dto.ProcesoJudicialId, cancellationToken);

        var validationResult = await _vincularProcesoValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var tenantId = _currentTenantService.TenantId!.Value;

        var existing = await _context.ExpedienteProcesosJudiciales
            .FirstOrDefaultAsync(ep => ep.ExpedienteId == id && ep.ProcesoJudicialId == dto.ProcesoJudicialId, cancellationToken);

        if (existing != null)
        {
            throw new ConflictException("La causa judicial ya se encuentra vinculada a este expediente.");
        }

        var proceso = await _context.ProcesosJudiciales
            .FirstOrDefaultAsync(p => p.Id == dto.ProcesoJudicialId, cancellationToken);

        if (proceso == null)
        {
            throw new NotFoundException(nameof(ProcesoJudicial), dto.ProcesoJudicialId);
        }

        if (_context.Database.IsRelational())
        {
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            return await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);

                try
                {
                    if (dto.EsPrincipal)
                    {
                        // Serializar concurrentemente sobre el expediente padre (Aggregate Root)
                        await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE expedientes 
                              SET ""UpdatedAt"" = CURRENT_TIMESTAMP 
                              WHERE ""Id"" = {0}",
                            [id],
                            cancellationToken);

                        // Desmarcar atómicamente el principal existente para este expediente
                        await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE expedientes_procesos_judiciales 
                              SET ""EsPrincipal"" = FALSE 
                              WHERE ""ExpedienteId"" = {0} AND ""EsPrincipal"" = TRUE",
                            [id],
                            cancellationToken);
                    }

                    var vinculo = new ExpedienteProcesoJudicial
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        ExpedienteId = id,
                        ProcesoJudicialId = dto.ProcesoJudicialId,
                        EsPrincipal = dto.EsPrincipal,
                        FechaVinculacion = DateTime.UtcNow,
                        Observaciones = dto.Observaciones?.Trim()
                    };

                    _context.ExpedienteProcesosJudiciales.Add(vinculo);
                    await _context.SaveChangesAsync(cancellationToken);

                    if (onBeforeCommit != null)
                    {
                        await onBeforeCommit();
                    }

                    await transaction.CommitAsync(cancellationToken);

                    await _auditService.LogAsync("ExpedienteProcesoJudicial", vinculo.Id.ToString(), "LINK", null, new
                    {
                        vinculo.ExpedienteId,
                        vinculo.ProcesoJudicialId,
                        vinculo.EsPrincipal
                    }, cancellationToken);

                    return new ExpedienteProcesoJudicialDto(
                        vinculo.Id,
                        vinculo.ExpedienteId,
                        vinculo.ProcesoJudicialId,
                        proceso.NumeroProceso,
                        proceso.Judicatura,
                        proceso.AccionInfraccion,
                        vinculo.EsPrincipal,
                        vinculo.FechaVinculacion,
                        vinculo.Observaciones);
                }
                catch (Exception ex) when (
                    (ex is DbUpdateException dbEx && dbEx.InnerException is Npgsql.PostgresException pgEx && (pgEx.SqlState == "23505" || pgEx.SqlState == "40001"))
                    || (ex is Npgsql.PostgresException pEx && (pEx.SqlState == "40001" || pEx.SqlState == "23505"))
                    || (ex.InnerException is Npgsql.PostgresException innerPg && (innerPg.SqlState == "40001" || innerPg.SqlState == "23505")))
                {
                    try { await transaction.RollbackAsync(cancellationToken); } catch { }
                    throw new ConflictException("Conflicto al establecer el proceso judicial principal: otro proceso fue asignado simultáneamente como principal.");
                }
            });
        }
        else
        {
            if (dto.EsPrincipal)
            {
                var existentes = await _context.ExpedienteProcesosJudiciales
                    .Where(ep => ep.ExpedienteId == id && ep.EsPrincipal)
                    .ToListAsync(cancellationToken);
                foreach (var ep in existentes)
                {
                    ep.EsPrincipal = false;
                }
            }

            var vinculo = new ExpedienteProcesoJudicial
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ExpedienteId = id,
                ProcesoJudicialId = dto.ProcesoJudicialId,
                EsPrincipal = dto.EsPrincipal,
                FechaVinculacion = DateTime.UtcNow,
                Observaciones = dto.Observaciones?.Trim()
            };

            _context.ExpedienteProcesosJudiciales.Add(vinculo);
            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync("ExpedienteProcesoJudicial", vinculo.Id.ToString(), "LINK", null, new
            {
                vinculo.ExpedienteId,
                vinculo.ProcesoJudicialId,
                vinculo.EsPrincipal
            }, cancellationToken);

            return new ExpedienteProcesoJudicialDto(
                vinculo.Id,
                vinculo.ExpedienteId,
                vinculo.ProcesoJudicialId,
                proceso.NumeroProceso,
                proceso.Judicatura,
                proceso.AccionInfraccion,
                vinculo.EsPrincipal,
                vinculo.FechaVinculacion,
                vinculo.Observaciones);
        }
    }

    public async Task DesvincularProcesoAsync(Guid id, Guid procesoJudicialId, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(id, requireWriteAccess: true, cancellationToken);

        var vinculo = await _context.ExpedienteProcesosJudiciales
            .FirstOrDefaultAsync(ep => ep.ExpedienteId == id && ep.ProcesoJudicialId == procesoJudicialId, cancellationToken);

        if (vinculo == null)
        {
            throw new NotFoundException("La vinculación entre el expediente y el proceso judicial no fue encontrada.");
        }

        _context.ExpedienteProcesosJudiciales.Remove(vinculo);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("ExpedienteProcesoJudicial", vinculo.Id.ToString(), "UNLINK", new
        {
            vinculo.ExpedienteId,
            vinculo.ProcesoJudicialId
        }, null, cancellationToken);
    }

    public async Task DeleteExpedienteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessExpedienteAsync(id, requireWriteAccess: true, cancellationToken);

        var expediente = await _context.Expedientes
            .FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, cancellationToken);

        if (expediente == null)
        {
            throw new NotFoundException(nameof(Expediente), id);
        }

        var hasActiveTasks = await _context.Tareas
            .AnyAsync(t => t.ExpedienteId == id && (t.Estado == EstadoTarea.Pendiente || t.Estado == EstadoTarea.EnProgreso), cancellationToken);

        if (hasActiveTasks)
        {
            throw new BusinessRuleException("No se puede eliminar un expediente que contiene tareas pendientes o en ejecución.");
        }

        expediente.IsDeleted = true;
        expediente.DeletedAt = DateTime.UtcNow;
        expediente.DeletedBy = _currentUserService.Email;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Expediente", expediente.Id.ToString(), "DELETE", new { expediente.NumeroExpediente, expediente.Titulo }, null, cancellationToken);
    }
}
