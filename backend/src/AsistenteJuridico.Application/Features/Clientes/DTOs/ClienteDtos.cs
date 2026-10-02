using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Clientes.DTOs;

public record ClienteDto(
    Guid Id,
    Guid TenantId,
    TipoIdentificacion TipoIdentificacion,
    string Identificacion,
    string NombreRazonSocial,
    string? Email,
    string? Telefono,
    string? Direccion,
    string? Notas,
    bool Activo,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version);

public record CreateClienteDto(
    TipoIdentificacion TipoIdentificacion,
    string Identificacion,
    string NombreRazonSocial,
    string? Email,
    string? Telefono,
    string? Direccion,
    string? Notas);

public record UpdateClienteDto(
    TipoIdentificacion TipoIdentificacion,
    string Identificacion,
    string NombreRazonSocial,
    string? Email,
    string? Telefono,
    string? Direccion,
    string? Notas,
    bool Activo,
    uint Version);
