using AsistenteJuridico.Domain.Enums;

namespace AsistenteJuridico.Application.Features.Alertas.DTOs;

public record AlertaProcesalDto(
    Guid Id,
    Guid TenantId,
    Guid? UsuarioId,
    string? UsuarioNombre,
    TipoOrigenAlerta TipoOrigen,
    Guid OrigenId,
    ReglaAlertaCodigo ReglaAlerta,
    DateTime FechaObjetivoUtc,
    DateTime FechaDisparoUtc,
    string Titulo,
    string Mensaje,
    SeveridadAlerta Severidad,
    Guid? ExpedienteId,
    string? ExpedienteNumero,
    EstadoAlertaResolucion EstadoResolucion,
    DateTime? ResueltaUtc,
    string? MotivoResolucion,
    bool Leida,
    DateTime? FechaLeidaUtc,
    uint Version
);

public class AlertasFilterRequest
{
    public bool? SoloNoLeidas { get; set; }
    public bool IncluirResueltas { get; set; } = false;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class MarcarTodasLeidasRequest
{
    public bool IncluirInstitucionales { get; set; } = false;
}

public class MarcarLeidaRequest
{
    public uint? Version { get; set; }
}

public class DescartarAlertaRequest
{
    public string? Motivo { get; set; }
    public uint? Version { get; set; }
}

public record ConteoNoLeidasDto(int TotalNoLeidas);

public record MarcarTodasLeidasResponseDto(int TotalAfectadas);
