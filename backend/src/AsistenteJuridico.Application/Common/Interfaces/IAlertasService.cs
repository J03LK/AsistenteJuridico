using AsistenteJuridico.Application.Features.Alertas.DTOs;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IAlertasService
{
    Task<IReadOnlyList<AlertaProcesalDto>> GetAlertasAsync(AlertasFilterRequest request, CancellationToken cancellationToken = default);
    Task<ConteoNoLeidasDto> GetConteoNoLeidasAsync(CancellationToken cancellationToken = default);
    Task<AlertaProcesalDto> MarcarLeidaAsync(Guid id, uint? version = null, CancellationToken cancellationToken = default);
    Task<AlertaProcesalDto> DescartarAlertaAsync(Guid id, string? motivo, uint? version = null, CancellationToken cancellationToken = default);
    Task<MarcarTodasLeidasResponseDto> MarcarTodasLeidasAsync(MarcarTodasLeidasRequest request, CancellationToken cancellationToken = default);
    Task InvalidarAlertasPorReprogramacionAsync(Guid tenantId, TipoOrigenAlerta tipoOrigen, Guid origenId, string motivo, CancellationToken cancellationToken = default);
    Task ProcesarReglasAlertasTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
