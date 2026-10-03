using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Servicio de autorización contextual y jerárquica para expedientes y recursos hijos.
/// Valida pertenencia al Tenant, restricción de SuperAdmin y condiciones de asignación de AbogadoJunior.
/// </summary>
public class ExpedienteAccessService : IExpedienteAccessService
{
    /// <summary>
    /// Estados de tarea que habilitan a un AsistenteLegal a acceder a los documentos del expediente
    /// de la tarea. Lista cerrada: una tarea Completada o Cancelada NO concede acceso.
    /// </summary>
    public static readonly IReadOnlyList<EstadoTarea> EstadosTareaQueHabilitanAccesoDocumental =
    [
        EstadoTarea.Pendiente,
        EstadoTarea.EnProgreso
    ];

    private readonly ApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentTenantService _currentTenantService;

    public ExpedienteAccessService(
        ApplicationDbContext context,
        ICurrentUserService currentUserService,
        ICurrentTenantService currentTenantService)
    {
        _context = context;
        _currentUserService = currentUserService;
        _currentTenantService = currentTenantService;
    }

    public async Task EnsureCanAccessExpedienteAsync(Guid expedienteId, bool requireWriteAccess = false, CancellationToken cancellationToken = default)
    {
        // 1. SuperAdmin tiene prohibido el acceso a datos jurídicos de clientes
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos jurídicos ni expedientes de los clientes.");
        }

        var currentTenantId = _currentTenantService.TenantId;
        if (!currentTenantId.HasValue)
        {
            throw new ForbiddenException("No se ha establecido el contexto de Tenant para la operación jurídica.");
        }

        var expediente = await _context.Expedientes
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == expedienteId && !e.IsDeleted, cancellationToken);

        if (expediente == null)
        {
            throw new NotFoundException(nameof(Expediente), expedienteId);
        }

        // 2. Comprobación estricta de aislamiento Multi-Tenant (evitar cross-tenant)
        if (expediente.TenantId != currentTenantId.Value)
        {
            throw new ForbiddenException("Acceso denegado: El expediente pertenece a otro estudio jurídico.");
        }

        // 3. Reglas de roles dentro del tenant
        await EnsureCanAccessExpedienteAsync(expediente, requireWriteAccess, cancellationToken);
    }

    public Task EnsureCanAccessExpedienteAsync(Expediente expediente, bool requireWriteAccess = false, CancellationToken cancellationToken = default)
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos jurídicos ni expedientes de los clientes.");
        }

        var currentTenantId = _currentTenantService.TenantId;
        if (!currentTenantId.HasValue || expediente.TenantId != currentTenantId.Value)
        {
            throw new ForbiddenException("Acceso denegado: El expediente pertenece a otro estudio jurídico.");
        }

        var role = _currentUserService.Role;
        var currentUserId = _currentUserService.UserId;

        if (role == Roles.AbogadoJunior)
        {
            if (expediente.AbogadoResponsableId != currentUserId)
            {
                if (requireWriteAccess)
                {
                    throw new ForbiddenException("Un Abogado Junior solo puede modificar expedientes a los cuales se encuentra asignado como responsable.");
                }
                else
                {
                    throw new ForbiddenException("Un Abogado Junior solo puede acceder a expedientes a los cuales se encuentra asignado como responsable.");
                }
            }
        }
        else if (role == Roles.AsistenteLegal)
        {
            if (requireWriteAccess)
            {
                throw new ForbiddenException("El rol Asistente Legal no tiene permisos de modificación sobre expedientes.");
            }
        }

        return Task.CompletedTask;
    }

    public async Task<bool> CanAccessExpedienteAsync(Guid expedienteId, bool requireWriteAccess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureCanAccessExpedienteAsync(expedienteId, requireWriteAccess, cancellationToken);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    public async Task<Tarea> EnsureCanAccessTareaAsync(Guid tareaId, bool requireWriteAccess = false, CancellationToken cancellationToken = default)
    {
        var currentTenantId = _currentTenantService.TenantId;
        if (!currentTenantId.HasValue)
        {
            throw new ForbiddenException("No se ha establecido el contexto de Tenant.");
        }

        var tarea = await _context.Tareas
            .IgnoreQueryFilters()
            .Include(t => t.Expediente)
            .FirstOrDefaultAsync(t => t.Id == tareaId, cancellationToken);

        if (tarea == null)
        {
            throw new NotFoundException(nameof(Tarea), tareaId);
        }

        if (tarea.TenantId != currentTenantId.Value)
        {
            throw new ForbiddenException("Acceso denegado: La tarea pertenece a otro estudio jurídico.");
        }

        if (tarea.Expediente != null)
        {
            await EnsureCanAccessExpedienteAsync(tarea.Expediente, requireWriteAccess, cancellationToken);
        }

        return tarea;
    }

    public async Task<Audiencia> EnsureCanAccessAudienciaAsync(Guid audienciaId, bool requireWriteAccess = false, CancellationToken cancellationToken = default)
    {
        var currentTenantId = _currentTenantService.TenantId;
        if (!currentTenantId.HasValue)
        {
            throw new ForbiddenException("No se ha establecido el contexto de Tenant.");
        }

        var audiencia = await _context.Audiencias
            .IgnoreQueryFilters()
            .Include(a => a.Expediente)
            .FirstOrDefaultAsync(a => a.Id == audienciaId, cancellationToken);

        if (audiencia == null)
        {
            throw new NotFoundException(nameof(Audiencia), audienciaId);
        }

        if (audiencia.TenantId != currentTenantId.Value)
        {
            throw new ForbiddenException("Acceso denegado: La audiencia pertenece a otro estudio jurídico.");
        }

        if (audiencia.Expediente != null)
        {
            await EnsureCanAccessExpedienteAsync(audiencia.Expediente, requireWriteAccess, cancellationToken);
        }

        return audiencia;
    }

    public async Task<Documento> EnsureCanAccessDocumentoAsync(Guid documentoId, bool requireWriteAccess = false, CancellationToken cancellationToken = default)
    {
        // Fase 7: el bloqueo de SuperAdmin es explícito y no depende de que el documento tenga expediente.
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a documentos jurídicos.");
        }

        var currentTenantId = _currentTenantService.TenantId;
        if (!currentTenantId.HasValue)
        {
            throw new ForbiddenException("No se ha establecido el contexto de Tenant.");
        }

        var documento = await _context.Documentos
            .IgnoreQueryFilters()
            .Include(d => d.Expediente)
            .FirstOrDefaultAsync(d => d.Id == documentoId && !d.IsDeleted, cancellationToken);

        if (documento == null)
        {
            throw new NotFoundException(nameof(Documento), documentoId);
        }

        if (documento.TenantId != currentTenantId.Value)
        {
            throw new ForbiddenException("Acceso denegado: El documento pertenece a otro estudio jurídico.");
        }

        // Fase 7 (D-3): un documento cuyo expediente está eliminado se trata como inexistente (404). La consulta usa
        // IgnoreQueryFilters, que también carga expedientes eliminados, así que se comprueba aquí explícitamente.
        // Con ExpedienteId NOT NULL (D-4) las reglas de rol se aplican siempre: ya no hay documentos sin expediente.
        if (documento.Expediente == null || documento.Expediente.IsDeleted)
        {
            throw new NotFoundException(nameof(Documento), documentoId);
        }

        await EnsureCanAccessExpedienteAsync(documento.Expediente, requireWriteAccess, cancellationToken);
        await EnsureAsistenteLegalConTareaVigenteAsync(documento.ExpedienteId, currentTenantId.Value, cancellationToken);

        return documento;
    }

    public async Task EnsureCanAccessDocumentosDeExpedienteAsync(Guid expedienteId, CancellationToken cancellationToken = default)
    {
        // Fase 7 (D-1): el listado aplica la misma regla documental que el detalle.
        await EnsureCanAccessExpedienteAsync(expedienteId, requireWriteAccess: false, cancellationToken);
        await EnsureAsistenteLegalConTareaVigenteAsync(expedienteId, _currentTenantService.TenantId!.Value, cancellationToken);
    }

    /// <summary>
    /// Regla documental de AsistenteLegal (Fase 6 v1.1.1, precisada en Fase 6.2):
    /// con al menos una tarea VIGENTE (ver EstadosTareaQueHabilitanAccesoDocumental) asignada al usuario
    /// en el expediente y dentro del mismo tenant -> permitido.
    /// En cualquier otro caso (sin tarea, tarea completada o cancelada, otro expediente, otro tenant)
    /// -> HTTP 403 Forbidden. Para el resto de roles no hace nada.
    /// </summary>
    private async Task EnsureAsistenteLegalConTareaVigenteAsync(Guid expedienteId, Guid tenantId, CancellationToken cancellationToken)
    {
        if (_currentUserService.Role != Roles.AsistenteLegal)
        {
            return;
        }

        var currentUserId = _currentUserService.UserId;
        var estadosPermitidos = EstadosTareaQueHabilitanAccesoDocumental.ToArray();
        var hasTask = await _context.Tareas
            .AnyAsync(t => t.TenantId == tenantId
                && t.ExpedienteId == expedienteId
                && t.AsignadoAUsuarioId == currentUserId
                && estadosPermitidos.Contains(t.Estado), cancellationToken);

        if (!hasTask)
        {
            throw new ForbiddenException("Un Asistente Legal solo puede acceder a documentos de expedientes en los cuales tiene una tarea asignada.");
        }
    }

    public async Task EnsureCanAccessProcesoAsync(Guid procesoId, CancellationToken cancellationToken = default)
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos jurídicos ni procesos judiciales.");
        }

        var currentTenantId = _currentTenantService.TenantId;
        if (!currentTenantId.HasValue)
        {
            throw new ForbiddenException("No se ha establecido el contexto de Tenant.");
        }

        var proceso = await _context.ProcesosJudiciales
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == procesoId, cancellationToken);

        if (proceso == null)
        {
            throw new NotFoundException(nameof(ProcesoJudicial), procesoId);
        }

        if (proceso.TenantId != currentTenantId.Value)
        {
            throw new ForbiddenException("Acceso denegado: El proceso judicial pertenece a otro estudio jurídico.");
        }
    }
}
