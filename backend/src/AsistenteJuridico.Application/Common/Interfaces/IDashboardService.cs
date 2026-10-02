using AsistenteJuridico.Application.Features.Dashboard.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IDashboardService
{
    Task<DashboardResumenDto> GetResumenAsync(CancellationToken cancellationToken = default);
    Task<MetricasEficienciaDto> GetEficienciaAsync(EficienciaRequest request, CancellationToken cancellationToken = default);
}
