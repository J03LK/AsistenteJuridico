namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Servicio centralizado de auditoría para operaciones críticas del sistema.
/// Garantiza la omisión estricta de credenciales, secretos, tokens y contenido binario.
/// </summary>
public interface IAuditService
{
    Task LogAsync(
        string entidad,
        string entidadId,
        string accion,
        object? valoresAnteriores = null,
        object? valoresNuevos = null,
        CancellationToken cancellationToken = default);
}
