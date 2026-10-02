namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Razón de finalización reportada por el proveedor de IA.
/// </summary>
public enum AIFinishReason
{
    Stop = 1,
    Length = 2,
    ContentFilter = 3,
    Error = 4,
    Cancelled = 5
}
