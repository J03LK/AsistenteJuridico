using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.ProcesosJudiciales.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Infrastructure.Services;

public class ProcesoJudicialService : IProcesoJudicialService
{
    private readonly ApplicationDbContext _context;
    private readonly IProcesoJudicialProvider _provider;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IExpedienteAccessService _expedienteAccessService;
    private readonly IAuditService _auditService;

    public ProcesoJudicialService(
        ApplicationDbContext context,
        IProcesoJudicialProvider provider,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IExpedienteAccessService expedienteAccessService,
        IAuditService auditService)
    {
        _context = context;
        _provider = provider;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _expedienteAccessService = expedienteAccessService;
        _auditService = auditService;
    }

    private void EnsureTenantAndNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos judiciales.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new ForbiddenException("Contexto de Tenant no especificado.");
        }
    }

    public async Task<ProcesoJudicialConsultaResult?> ConsultarEnMockAsync(string numeroProceso, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        return await _provider.ConsultarPorNumeroAsync(numeroProceso, cancellationToken);
    }

    public async Task<ProcesoJudicialDto> SincronizarProcesoAsync(SincronizarProcesoDto dto, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        if (string.IsNullOrWhiteSpace(dto.NumeroProceso))
        {
            throw new ValidationException(["El número de proceso judicial es obligatorio."]);
        }

        var consulta = await _provider.ConsultarPorNumeroAsync(dto.NumeroProceso, cancellationToken);
        if (consulta == null)
        {
            throw new NotFoundException($"No se encontró información judicial en el sistema para el proceso '{dto.NumeroProceso}'.");
        }

        var tenantId = _currentTenantService.TenantId!.Value;

        var cleanNumero = consulta.NumeroProceso.Trim();
        var proceso = await _context.ProcesosJudiciales
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.NumeroProceso == cleanNumero, cancellationToken);

        if (proceso == null)
        {
            proceso = new ProcesoJudicial
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                NumeroProceso = consulta.NumeroProceso,
                Judicatura = consulta.Judicatura,
                JuezPonente = consulta.JuezPonente,
                AccionInfraccion = consulta.AccionInfraccion,
                Materia = consulta.Materia,
                EstadoJudicial = consulta.EstadoJudicial,
                FechaInicio = consulta.FechaInicio,
                UltimaSincronizacion = DateTime.UtcNow,
                DetallesJson = consulta.ActuacionesJson,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = _currentUserService.Email
            };

            _context.ProcesosJudiciales.Add(proceso);
        }
        else
        {
            proceso.Judicatura = consulta.Judicatura;
            proceso.JuezPonente = consulta.JuezPonente;
            proceso.AccionInfraccion = consulta.AccionInfraccion;
            proceso.Materia = consulta.Materia;
            proceso.EstadoJudicial = consulta.EstadoJudicial;
            proceso.FechaInicio = consulta.FechaInicio;
            proceso.UltimaSincronizacion = DateTime.UtcNow;
            proceso.DetallesJson = consulta.ActuacionesJson;
            proceso.UpdatedAt = DateTime.UtcNow;
            proceso.UpdatedBy = _currentUserService.Email;
        }

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("ProcesoJudicial", proceso.Id.ToString(), "SYNC", null, new
        {
            proceso.NumeroProceso,
            proceso.Judicatura,
            proceso.EstadoJudicial
        }, cancellationToken);

        return new ProcesoJudicialDto(
            proceso.Id,
            proceso.TenantId,
            proceso.NumeroProceso,
            proceso.Judicatura,
            proceso.JuezPonente,
            proceso.AccionInfraccion,
            proceso.Materia,
            proceso.EstadoJudicial,
            proceso.FechaInicio,
            proceso.UltimaSincronizacion,
            proceso.DetallesJson,
            proceso.CreatedAt,
            proceso.UpdatedAt,
            proceso.Version);
    }

    public async Task<PagedResult<ProcesoJudicialDto>> GetProcesosPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var query = _context.ProcesosJudiciales.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var search = request.SearchTerm.Trim().ToLower();
            query = query.Where(p =>
                p.NumeroProceso.ToLower().Contains(search) ||
                p.Judicatura.ToLower().Contains(search) ||
                (p.AccionInfraccion != null && p.AccionInfraccion.ToLower().Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(p => new ProcesoJudicialDto(
                p.Id,
                p.TenantId,
                p.NumeroProceso,
                p.Judicatura,
                p.JuezPonente,
                p.AccionInfraccion,
                p.Materia,
                p.EstadoJudicial,
                p.FechaInicio,
                p.UltimaSincronizacion,
                p.DetallesJson,
                p.CreatedAt,
                p.UpdatedAt,
                p.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<ProcesoJudicialDto>(items, totalCount, request.PageNumber, request.PageSize);
    }

    public async Task<ProcesoJudicialDto> GetProcesoByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _expedienteAccessService.EnsureCanAccessProcesoAsync(id, cancellationToken);

        var proceso = await _context.ProcesosJudiciales
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProcesoJudicialDto(
                p.Id,
                p.TenantId,
                p.NumeroProceso,
                p.Judicatura,
                p.JuezPonente,
                p.AccionInfraccion,
                p.Materia,
                p.EstadoJudicial,
                p.FechaInicio,
                p.UltimaSincronizacion,
                p.DetallesJson,
                p.CreatedAt,
                p.UpdatedAt,
                p.Version))
            .FirstOrDefaultAsync(cancellationToken);

        if (proceso == null)
        {
            throw new NotFoundException(nameof(ProcesoJudicial), id);
        }

        return proceso;
    }
}
