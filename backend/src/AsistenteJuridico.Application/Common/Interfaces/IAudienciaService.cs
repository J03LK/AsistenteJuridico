using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Features.Audiencias.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IAudienciaService
{
    Task<PagedResult<AudienciaDto>> GetAudienciasPagedAsync(AudienciaFilterRequest request, CancellationToken cancellationToken = default);
    Task<AudienciaDto> GetAudienciaByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AudienciaDto> CreateAudienciaAsync(CreateAudienciaDto dto, CancellationToken cancellationToken = default);
    Task<AudienciaDto> UpdateAudienciaAsync(Guid id, UpdateAudienciaDto dto, CancellationToken cancellationToken = default);
    Task<AudienciaDto> CambiarEstadoAsync(Guid id, CambiarEstadoAudienciaDto dto, CancellationToken cancellationToken = default);
    Task DeleteAudienciaAsync(Guid id, CancellationToken cancellationToken = default);
}
