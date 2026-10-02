using AsistenteJuridico.Application.Features.Agenda.DTOs;

namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IAgendaService
{
    Task<AgendaPaginadaDto> GetEventosAsync(AgendaFilterRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EventoAgendaDto>> GetEventosHoyAsync(CancellationToken cancellationToken = default);
}
