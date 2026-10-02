namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Proveedor de integración externa con sistemas judiciales (SATJE / Función Judicial del Ecuador).
/// Permite desacoplar el origen de datos (Mock de desarrollo vs integración real futura en Fase 8).
/// </summary>
public interface IProcesoJudicialProvider
{
    Task<ProcesoJudicialConsultaResult?> ConsultarPorNumeroAsync(string numeroProceso, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProcesoJudicialConsultaResult>> ConsultarPorIdentificacionAsync(string tipoIdentificacion, string identificacion, CancellationToken cancellationToken = default);
}

public record ProcesoJudicialConsultaResult
{
    public string NumeroProceso { get; init; } = string.Empty;
    public string Judicatura { get; init; } = string.Empty;
    public string? JuezPonente { get; init; }
    public string? AccionInfraccion { get; init; }
    public string Materia { get; init; } = "Civil";
    public string EstadoJudicial { get; init; } = "En Trámite";
    public DateTime? FechaInicio { get; init; }
    public string? ActuacionesJson { get; init; }
    public IReadOnlyList<ParteProcesalDto> PartesProcesales { get; init; } = Array.Empty<ParteProcesalDto>();
}

public record ParteProcesalDto(string Tipo, string Nombres, string? Identificacion);
