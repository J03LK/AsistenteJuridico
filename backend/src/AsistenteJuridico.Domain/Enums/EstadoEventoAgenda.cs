namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Estado unificado de un evento en la agenda jurídica.
/// </summary>
public enum EstadoEventoAgenda
{
    Programada = 1,
    EnProgreso = 2,
    RealizadaOCompletada = 3,
    Cancelada = 4,
    Suspendida = 5
}
