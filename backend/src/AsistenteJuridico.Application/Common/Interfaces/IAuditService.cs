namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Servicio centralizado de auditoría para operaciones críticas del sistema.
/// Garantiza la omisión estricta de credenciales, secretos, tokens y contenido binario.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Audita una operación ya confirmada. La auditoría se guarda en una unidad propia, sin tocar la unidad de
    /// trabajo del llamador; si falla, la operación confirmada no se revierte y el fallo queda en el log técnico.
    /// </summary>
    Task LogAsync(
        string entidad,
        string entidadId,
        string accion,
        object? valoresAnteriores = null,
        object? valoresNuevos = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Audita una operación que aún no se ha guardado: la auditoría se agrega a la unidad de trabajo actual y se
    /// persiste, de forma atómica con la operación, en el guardado del servicio que controla la transacción.
    /// Las implementaciones que no distinguen ambos casos (por ejemplo, dobles de prueba) delegan en LogAsync.
    /// </summary>
    Task LogInTransactionAsync(
        string entidad,
        string entidadId,
        string accion,
        object? valoresAnteriores = null,
        object? valoresNuevos = null,
        CancellationToken cancellationToken = default)
        => LogAsync(entidad, entidadId, accion, valoresAnteriores, valoresNuevos, cancellationToken);
}
