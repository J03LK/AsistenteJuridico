using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Features.Tareas.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface ITareaService
{
    Task<PagedResult<TareaDto>> GetTareasPagedAsync(TareaFilterRequest request, CancellationToken cancellationToken = default);
    Task<TareaDto> GetTareaByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TareaDto> CreateTareaAsync(CreateTareaDto dto, CancellationToken cancellationToken = default);
    Task<TareaDto> UpdateTareaAsync(Guid id, UpdateTareaDto dto, CancellationToken cancellationToken = default);
    Task<TareaDto> CambiarEstadoAsync(Guid id, CambiarEstadoTareaDto dto, CancellationToken cancellationToken = default);
    Task DeleteTareaAsync(Guid id, CancellationToken cancellationToken = default);
}
