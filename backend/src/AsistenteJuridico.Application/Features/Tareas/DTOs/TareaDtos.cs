using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Tareas.DTOs;

public record TareaDto(
    Guid Id,
    Guid TenantId,
    Guid? ExpedienteId,
    string? ExpedienteNumero,
    string? ExpedienteTitulo,
    Guid? AsignadoAUsuarioId,
    string? AsignadoAUsuarioNombre,
    string Titulo,
    string? Descripcion,
    DateTime FechaVencimiento,
    Prioridad Prioridad,
    string PrioridadDescripcion,
    EstadoTarea Estado,
    string EstadoDescripcion,
    DateTime? FechaCompletada,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version);

public record CreateTareaDto(
    Guid ExpedienteId,
    string Titulo,
    string? Descripcion,
    DateTime FechaVencimiento,
    Prioridad Prioridad,
    Guid? AsignadoAUsuarioId);

public record UpdateTareaDto(
    string Titulo,
    string? Descripcion,
    DateTime FechaVencimiento,
    Prioridad Prioridad,
    Guid? AsignadoAUsuarioId,
    uint Version);

public record CambiarEstadoTareaDto(
    EstadoTarea NuevoEstado,
    uint Version);

public record TareaFilterRequest : PagedRequest
{
    public Guid? ExpedienteId { get; init; }
    public EstadoTarea? Estado { get; init; }
    public Guid? AsignadoAUsuarioId { get; init; }
    public DateTime? VencimientoDesde { get; init; }
    public DateTime? VencimientoHasta { get; init; }
}
