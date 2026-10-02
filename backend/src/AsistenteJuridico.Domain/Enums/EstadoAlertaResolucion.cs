namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Estado determinista del ciclo de vida y resolución de una alerta procesal.
/// Única fuente de verdad de estado para AlertaProcesal.
/// </summary>
public enum EstadoAlertaResolucion
{
    Activa = 1,
    ResueltaAutomaticamente = 2,
    InvalidaPorReprogramacion = 3,
    DescartadaManualmente = 4
}
