using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Application.Features.Clientes.DTOs;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Expedientes.DTOs;

public record ExpedienteDto(
    Guid Id,
    Guid TenantId,
    string NumeroExpediente,
    string Titulo,
    string? Descripcion,
    string Materia,
    EstadoExpediente Estado,
    string EstadoDescripcion,
    Prioridad Prioridad,
    string PrioridadDescripcion,
    Guid ClienteId,
    string ClienteNombre,
    Guid? AbogadoResponsableId,
    string? AbogadoResponsableNombre,
    DateTime FechaApertura,
    DateTime? FechaCierreEstimada,
    DateTime? FechaCierreReal,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version);

public record ExpedienteDetailDto(
    Guid Id,
    Guid TenantId,
    string NumeroExpediente,
    string Titulo,
    string? Descripcion,
    string Materia,
    EstadoExpediente Estado,
    string EstadoDescripcion,
    Prioridad Prioridad,
    string PrioridadDescripcion,
    Guid ClienteId,
    string ClienteNombre,
    ClienteDto? Cliente,
    Guid? AbogadoResponsableId,
    string? AbogadoResponsableNombre,
    DateTime FechaApertura,
    DateTime? FechaCierreEstimada,
    DateTime? FechaCierreReal,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version,
    IReadOnlyList<ExpedienteProcesoJudicialDto> ProcesosVinculados,
    int TareasTotalCount,
    int TareasPendientesCount,
    int DocumentosCount,
    int AudienciasCount);

public record CreateExpedienteDto(
    Guid ClienteId,
    string Titulo,
    string? Descripcion,
    string Materia,
    Prioridad Prioridad,
    Guid? AbogadoResponsableId,
    DateTime? FechaCierreEstimada);

public record UpdateExpedienteDto(
    string Titulo,
    string? Descripcion,
    string Materia,
    Prioridad Prioridad,
    Guid? AbogadoResponsableId,
    DateTime? FechaCierreEstimada,
    uint Version);

public record CambiarEstadoExpedienteDto(
    EstadoExpediente NuevoEstado,
    bool ConfirmarCierreConTareasPendientes,
    string? MotivoCierreForzado,
    uint Version);

public record VincularProcesoDto(
    Guid ProcesoJudicialId,
    bool EsPrincipal,
    string? Observaciones);

public record ExpedienteProcesoJudicialDto(
    Guid Id,
    Guid ExpedienteId,
    Guid ProcesoJudicialId,
    string NumeroProceso,
    string? Judicatura,
    string? AccionInfraccion,
    bool EsPrincipal,
    DateTime FechaVinculacion,
    string? Observaciones);

public record ExpedienteFilterRequest : PagedRequest
{
    public EstadoExpediente? Estado { get; init; }
    public Prioridad? Prioridad { get; init; }
    public Guid? AbogadoResponsableId { get; init; }
    public Guid? ClienteId { get; init; }
}
