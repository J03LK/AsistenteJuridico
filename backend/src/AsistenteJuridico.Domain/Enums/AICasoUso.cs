namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Caso de uso específico de asistencia legal con IA.
/// </summary>
public enum AICasoUso
{
    ChatLibre = 1,
    ResumenExpediente = 2,
    ResumenDocumento = 3,
    ExtraccionMetadatos = 4,
    ExtraccionHechos = 4,
    GeneracionBorrador = 5,
    RedaccionEscrito = 5,

    // Fase 8 (contrato §6 y §14)
    IndexacionSemantica = 6,
    BusquedaSemantica = 7,
    PreguntaRag = 8
}
