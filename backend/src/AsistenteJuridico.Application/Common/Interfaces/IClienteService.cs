using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Features.Clientes.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IClienteService
{
    Task<PagedResult<ClienteDto>> GetClientesPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);
    Task<ClienteDto> GetClienteByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ClienteDto> CreateClienteAsync(CreateClienteDto dto, CancellationToken cancellationToken = default);
    Task<ClienteDto> UpdateClienteAsync(Guid id, UpdateClienteDto dto, CancellationToken cancellationToken = default);
    Task DeleteClienteAsync(Guid id, CancellationToken cancellationToken = default);
}
