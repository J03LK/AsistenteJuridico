namespace AsistenteJuridico.Application.Common.Interfaces;

/// <summary>
/// Generador concurrente y seguro de códigos correlativos de expedientes (EXP-{YYYY}-{NNNN}).
/// Garantiza unicidad absoluta por tenant y año, y progresión monotónica para operaciones confirmadas.
/// </summary>
public interface IExpedienteCodeGenerator
{
    Task<string> GenerateNextCodeAsync(Guid tenantId, int? anio = null, CancellationToken cancellationToken = default);
}
