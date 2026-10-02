using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Features.Expedientes.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IExpedienteService
{
    Task<PagedResult<ExpedienteDto>> GetExpedientesPagedAsync(ExpedienteFilterRequest request, CancellationToken cancellationToken = default);
    Task<ExpedienteDetailDto> GetExpedienteByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ExpedienteDto> CreateExpedienteAsync(CreateExpedienteDto dto, CancellationToken cancellationToken = default);
    Task<ExpedienteDto> UpdateExpedienteAsync(Guid id, UpdateExpedienteDto dto, CancellationToken cancellationToken = default);
    Task<ExpedienteDto> CambiarEstadoAsync(Guid id, CambiarEstadoExpedienteDto dto, CancellationToken cancellationToken = default);
    Task<ExpedienteProcesoJudicialDto> VincularProcesoAsync(Guid id, VincularProcesoDto dto, CancellationToken cancellationToken = default);
    Task DesvincularProcesoAsync(Guid id, Guid procesoJudicialId, CancellationToken cancellationToken = default);
    Task DeleteExpedienteAsync(Guid id, CancellationToken cancellationToken = default);
}
