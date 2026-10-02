namespace AsistenteJuridico.Application.Features.Dashboard.DTOs;

public record KpisGeneralesDto(
    int TotalExpedientesActivos,
    int TotalTareasPendientes,
    int TotalAudienciasProximas7Dias,
    int TotalAlertasAltaCriticasSinResolver
);

public record DistribucionEstadoDto(
    int Abiertos,
    int EnTramite,
    int Suspendidos,
    int Cerrados,
    int Archivados
);

public record TareasPendientesPorPrioridadDto(
    int Baja,
    int Media,
    int Alta,
    int Urgente
);

public record ProximaAudienciaDto(
    Guid Id,
    string Titulo,
    DateTime FechaHora,
    string SalaOVirtual,
    string? ExpedienteNumero,
    string? AbogadoNombre
);

public record MetricasInactividadDto(
    int ExpedientesInactivos30Dias,
    int ExpedientesInactivos60Dias
);

public record MetricasEficienciaDto(
    int TotalExpedientesCerradosEvaluados,
    int ExpedientesCerradosATiempo,
    int ExpedientesConPlazoEstimado,
    double? PorcentajeCumplimiento
);

public record DashboardResumenDto(
    KpisGeneralesDto Kpis,
    DistribucionEstadoDto DistribucionExpedientes,
    TareasPendientesPorPrioridadDto TareasPendientesPorPrioridad,
    IReadOnlyList<ProximaAudienciaDto> ProximasAudiencias,
    MetricasInactividadDto Inactividad,
    MetricasEficienciaDto Eficiencia
);

public class EficienciaRequest
{
    public DateTime? FechaDesdeUtc { get; set; }
    public DateTime? FechaHastaUtc { get; set; }
}
