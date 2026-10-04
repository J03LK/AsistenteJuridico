namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Fase 8 — Estado de indexación semántica de un documento en un perfil (contrato §7). Es un eje independiente de
/// <see cref="EstadoProcesamientoIa"/>, que pertenece a la extracción de hechos de la Fase 6.X.
/// </summary>
public enum EstadoIndexacion
{
    Pendiente = 0,
    Procesando = 1,
    Indexado = 2,
    Fallido = 3,
    Obsoleto = 4,
    PurgaPendiente = 5
}
