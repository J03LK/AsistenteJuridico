using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Features.ProcesosJudiciales.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IProcesoJudicialService
{
    Task<ProcesoJudicialConsultaResult?> ConsultarEnMockAsync(string numeroProceso, CancellationToken cancellationToken = default);
    Task<ProcesoJudicialDto> SincronizarProcesoAsync(SincronizarProcesoDto dto, CancellationToken cancellationToken = default);
    Task<PagedResult<ProcesoJudicialDto>> GetProcesosPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);
    Task<ProcesoJudicialDto> GetProcesoByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
