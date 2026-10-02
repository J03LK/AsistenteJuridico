namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Estado de procesamiento del documento para futura indexación vectorial e IA / RAG.
/// </summary>
public enum EstadoProcesamientoIa
{
    Pendiente = 0,
    Procesando = 1,
    Procesado = 2,
    Fallido = 3
}
