using AsistenteJuridico.Application.Common.DTOs;
using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Audiencias.DTOs;

public record AudienciaDto(
    Guid Id,
    Guid TenantId,
    Guid ExpedienteId,
    string ExpedienteNumero,
    string ExpedienteTitulo,
    Guid? ProcesoJudicialId,
    string? NumeroProceso,
    DateTime FechaHora,
    string SalaOVirtual,
    TipoAudiencia TipoAudiencia,
    string TipoAudienciaDescripcion,
    EstadoAudiencia Estado,
    string EstadoDescripcion,
    string? Notas,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version);

public record CreateAudienciaDto(
    Guid ExpedienteId,
    Guid? ProcesoJudicialId,
    DateTime FechaHora,
    string SalaOVirtual,
    TipoAudiencia TipoAudiencia,
    string? Notas);

public record UpdateAudienciaDto(
    Guid? ProcesoJudicialId,
    DateTime FechaHora,
    string SalaOVirtual,
    TipoAudiencia TipoAudiencia,
    string? Notas,
    uint Version);

public record CambiarEstadoAudienciaDto(
    EstadoAudiencia NuevoEstado,
    uint Version);

public record AudienciaFilterRequest : PagedRequest
{
    public Guid? ExpedienteId { get; init; }
    public EstadoAudiencia? Estado { get; init; }
    public DateTime? FechaDesde { get; init; }
    public DateTime? FechaHasta { get; init; }
}
