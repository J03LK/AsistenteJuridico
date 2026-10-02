namespace AsistenteJuridico.Application.Features.ProcesosJudiciales.DTOs;

public record ProcesoJudicialDto(
    Guid Id,
    Guid TenantId,
    string NumeroProceso,
    string Judicatura,
    string? JuezPonente,
    string? AccionInfraccion,
    string Materia,
    string EstadoJudicial,
    DateTime? FechaInicio,
    DateTime? UltimaSincronizacion,
    string? DetallesJson,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version);

public record SincronizarProcesoDto(
    string NumeroProceso,
    string? Observaciones);
