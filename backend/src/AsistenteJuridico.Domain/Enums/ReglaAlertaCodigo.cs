namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Código de regla evaluada para la generación de alertas procesales y operativas.
/// </summary>
public enum ReglaAlertaCodigo
{
    Audiencia7Dias = 10,
    Audiencia48Horas = 11,
    Audiencia24Horas = 12,
    Tarea48Horas = 20,
    TareaVencida = 21,
    InactividadOperativa30Dias = 30,
    InactividadOperativa60Dias = 31
}
