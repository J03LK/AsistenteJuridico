using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Agenda.DTOs;

public record EventoAgendaDto(
    Guid Id,
    TipoEventoAgenda TipoEvento,
    string Titulo,
    string? Descripcion,
    DateTime FechaHoraInicioUtc,
    DateTime? FechaHoraFinUtc,
    string? UbicacionOSala,
    EstadoEventoAgenda Estado,
    string? Prioridad,
    Guid? ExpedienteId,
    string? ExpedienteNumero,
    string? ExpedienteTitulo,
    string? Materia,
    Guid? ResponsableId,
    string? ResponsableNombre
);

public class AgendaFilterRequest
{
    public DateTime FechaDesdeUtc { get; set; }
    public DateTime FechaHastaUtc { get; set; }
    public Guid? AbogadoId { get; set; }
    public string? Materia { get; set; }
    public TipoEventoAgenda? TipoEvento { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 200;
}

public record AgendaPaginadaDto(
    IReadOnlyList<EventoAgendaDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize
);
