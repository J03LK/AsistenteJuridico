using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Generador concurrente y determinista de códigos correlativos para expedientes (EXP-{YYYY}-{NNNN}).
/// Emplea INSERT ... ON CONFLICT DO NOTHING seguido de SELECT ... FOR UPDATE en PostgreSQL
/// para garantizar atomicidad estricta y secuencia monotónicamente creciente para operaciones confirmadas.
/// </summary>
public class ExpedienteCodeGenerator : IExpedienteCodeGenerator
{
    private readonly ApplicationDbContext _context;

    public ExpedienteCodeGenerator(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<string> GenerateNextCodeAsync(Guid tenantId, int? anio = null, CancellationToken cancellationToken = default)
    {
        var year = anio ?? DateTime.UtcNow.Year;
        const string tipo = "EXPEDIENTE";

        var executionStrategy = _context.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            // 1. Inicialización idempotente
            await _context.Database.ExecuteSqlRawAsync(
                @"INSERT INTO tenant_secuencias (""TenantId"", ""TipoSecuencia"", ""Anio"", ""UltimoValor"", ""UpdatedAt"")
                  VALUES ({0}, {1}, {2}, 0, NOW())
                  ON CONFLICT (""TenantId"", ""TipoSecuencia"", ""Anio"") DO NOTHING;",
                [tenantId, tipo, year],
                cancellationToken);

            // 2. Bloqueo pesimista exclusivo (FOR UPDATE)
            var secuencia = await _context.TenantSecuencias
                .FromSqlRaw(
                    @"SELECT * FROM tenant_secuencias 
                      WHERE ""TenantId"" = {0} AND ""TipoSecuencia"" = {1} AND ""Anio"" = {2} 
                      FOR UPDATE",
                    tenantId, tipo, year)
                .SingleAsync(cancellationToken);

            secuencia.UltimoValor += 1;
            secuencia.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return $"EXP-{year}-{secuencia.UltimoValor:D4}";
        });
    }
}
