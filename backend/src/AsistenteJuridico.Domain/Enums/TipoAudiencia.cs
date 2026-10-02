namespace AsistenteJuridico.Domain.Enums;

/// <summary>
/// Tipos de audiencias conforme a la normativa procesal ecuatoriana (COGEP / COIP).
/// </summary>
public enum TipoAudiencia
{
    Preliminar = 1,
    Juicio = 2,
    Conciliacion = 3,
    Unica = 4,
    EvaluacionYPreparatoria = 5,
    Apelacion = 6,
    Otra = 7
}
