using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Common.Exceptions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.Clientes.DTOs;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using AppValidationException = AsistenteJuridico.Application.Common.Exceptions.ValidationException;

namespace AsistenteJuridico.Infrastructure.Services;

public class ClienteService : IClienteService
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateClienteDto> _createValidator;
    private readonly IValidator<UpdateClienteDto> _updateValidator;

    public ClienteService(
        ApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IAuditService auditService,
        IValidator<CreateClienteDto> createValidator,
        IValidator<UpdateClienteDto> updateValidator)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _auditService = auditService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    private void EnsureTenantAndNotSuperAdmin()
    {
        if (_currentUserService.Role == Roles.SuperAdmin)
        {
            throw new ForbiddenException("Los administradores globales (SuperAdmin) no tienen acceso a datos jurídicos ni clientes.");
        }

        if (!_currentTenantService.TenantId.HasValue)
        {
            throw new ForbiddenException("Contexto de Tenant no especificado.");
        }
    }

    public async Task<PagedResult<ClienteDto>> GetClientesPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var query = _context.Clientes.AsNoTracking().Where(c => !c.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var search = request.SearchTerm.Trim().ToLower();
            query = query.Where(c => c.NombreRazonSocial.ToLower().Contains(search) || c.Identificacion.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(c => new ClienteDto(
                c.Id,
                c.TenantId,
                c.TipoIdentificacion,
                c.Identificacion,
                c.NombreRazonSocial,
                c.Email,
                c.Telefono,
                c.Direccion,
                c.Notas,
                c.Activo,
                c.CreatedAt,
                c.UpdatedAt,
                c.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<ClienteDto>(items, totalCount, request.PageNumber, request.PageSize);
    }

    public async Task<ClienteDto> GetClienteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var cliente = await _context.Clientes
            .AsNoTracking()
            .Where(c => c.Id == id && !c.IsDeleted)
            .Select(c => new ClienteDto(
                c.Id,
                c.TenantId,
                c.TipoIdentificacion,
                c.Identificacion,
                c.NombreRazonSocial,
                c.Email,
                c.Telefono,
                c.Direccion,
                c.Notas,
                c.Activo,
                c.CreatedAt,
                c.UpdatedAt,
                c.Version))
            .FirstOrDefaultAsync(cancellationToken);

        if (cliente == null)
        {
            throw new NotFoundException(nameof(Cliente), id);
        }

        return cliente;
    }

    public async Task<ClienteDto> CreateClienteAsync(CreateClienteDto dto, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var validationResult = await _createValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var tenantId = _currentTenantService.TenantId!.Value;

        // Comprobar unicidad de identificación dentro del tenant (no eliminados)
        var exists = await _context.Clientes
            .AnyAsync(c => c.TenantId == tenantId && c.Identificacion == dto.Identificacion.Trim() && !c.IsDeleted, cancellationToken);

        if (exists)
        {
            throw new ConflictException($"Ya existe un cliente registrado con la identificación '{dto.Identificacion}'.");
        }

        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TipoIdentificacion = dto.TipoIdentificacion,
            Identificacion = dto.Identificacion.Trim(),
            NombreRazonSocial = dto.NombreRazonSocial.Trim(),
            Email = dto.Email?.Trim(),
            Telefono = dto.Telefono?.Trim(),
            Direccion = dto.Direccion?.Trim(),
            Notas = dto.Notas?.Trim(),
            Activo = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = _currentUserService.Email
        };

        _context.Clientes.Add(cliente);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Cliente", cliente.Id.ToString(), "CREATE", null, new
        {
            cliente.Identificacion,
            cliente.NombreRazonSocial,
            cliente.TipoIdentificacion
        }, cancellationToken);

        return new ClienteDto(
            cliente.Id,
            cliente.TenantId,
            cliente.TipoIdentificacion,
            cliente.Identificacion,
            cliente.NombreRazonSocial,
            cliente.Email,
            cliente.Telefono,
            cliente.Direccion,
            cliente.Notas,
            cliente.Activo,
            cliente.CreatedAt,
            cliente.UpdatedAt,
            cliente.Version);
    }

    public async Task<ClienteDto> UpdateClienteAsync(Guid id, UpdateClienteDto dto, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var validationResult = await _updateValidator.ValidateAsync(dto, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new AppValidationException(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        var cliente = await _context.Clientes
            .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted, cancellationToken);

        if (cliente == null)
        {
            throw new NotFoundException(nameof(Cliente), id);
        }

        // Unicidad si cambió identificación
        if (cliente.Identificacion != dto.Identificacion.Trim())
        {
            var exists = await _context.Clientes
                .AnyAsync(c => c.TenantId == cliente.TenantId && c.Identificacion == dto.Identificacion.Trim() && c.Id != id && !c.IsDeleted, cancellationToken);

            if (exists)
            {
                throw new ConflictException($"Ya existe otro cliente con la identificación '{dto.Identificacion}'.");
            }
        }

        var valoresAnteriores = new
        {
            cliente.Identificacion,
            cliente.NombreRazonSocial,
            cliente.Email,
            cliente.Activo
        };

        // Establecer token de concurrencia optimista original
        _context.Entry(cliente).Property(c => c.Version).OriginalValue = dto.Version;

        cliente.TipoIdentificacion = dto.TipoIdentificacion;
        cliente.Identificacion = dto.Identificacion.Trim();
        cliente.NombreRazonSocial = dto.NombreRazonSocial.Trim();
        cliente.Email = dto.Email?.Trim();
        cliente.Telefono = dto.Telefono?.Trim();
        cliente.Direccion = dto.Direccion?.Trim();
        cliente.Notas = dto.Notas?.Trim();
        cliente.Activo = dto.Activo;
        cliente.UpdatedAt = DateTime.UtcNow;
        cliente.UpdatedBy = _currentUserService.Email;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Cliente", cliente.Id.ToString(), "UPDATE", valoresAnteriores, new
        {
            cliente.Identificacion,
            cliente.NombreRazonSocial,
            cliente.Email,
            cliente.Activo
        }, cancellationToken);

        return new ClienteDto(
            cliente.Id,
            cliente.TenantId,
            cliente.TipoIdentificacion,
            cliente.Identificacion,
            cliente.NombreRazonSocial,
            cliente.Email,
            cliente.Telefono,
            cliente.Direccion,
            cliente.Notas,
            cliente.Activo,
            cliente.CreatedAt,
            cliente.UpdatedAt,
            cliente.Version);
    }

    public async Task DeleteClienteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureTenantAndNotSuperAdmin();

        var cliente = await _context.Clientes
            .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted, cancellationToken);

        if (cliente == null)
        {
            throw new NotFoundException(nameof(Cliente), id);
        }

        // Validar que no tenga expedientes activos
        var hasActiveExpedientes = await _context.Expedientes
            .AnyAsync(e => e.ClienteId == id && !e.IsDeleted && e.Estado != EstadoExpediente.Cerrado && e.Estado != EstadoExpediente.Archivado, cancellationToken);

        if (hasActiveExpedientes)
        {
            throw new BusinessRuleException("No se puede eliminar el cliente porque tiene expedientes jurídicos activos asociados.");
        }

        cliente.IsDeleted = true;
        cliente.DeletedAt = DateTime.UtcNow;
        cliente.DeletedBy = _currentUserService.Email;

        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("Cliente", cliente.Id.ToString(), "DELETE", new { cliente.Identificacion, cliente.NombreRazonSocial }, null, cancellationToken);
    }
}
