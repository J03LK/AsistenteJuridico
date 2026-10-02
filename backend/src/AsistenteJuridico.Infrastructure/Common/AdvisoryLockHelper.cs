using System;
using System.IO.Hashing;

namespace AsistenteJuridico.Infrastructure.Common;

/// <summary>
/// Helper determinista para generación de Advisory Locks de 64 bits en PostgreSQL a partir del TenantId.
/// Utiliza XxHash64.HashToUInt64(Guid.ToByteArray(), seed: 0) sobre los 16 bytes del GUID, garantizando una clave
/// determinista y estable para pg_try_advisory_xact_lock(bigint).
/// </summary>
public static class AdvisoryLockHelper
{
    /// <summary>
    /// Genera una clave determinista de 64 bits a partir del TenantId mediante XxHash64.
    /// </summary>
    public static long ObtenerTenantLockKey(Guid tenantId)
    {
        byte[] bytes = tenantId.ToByteArray();
        ulong hash = XxHash64.HashToUInt64(bytes, seed: 0);
        return unchecked((long)hash);
    }

    /// <summary>
    /// Intenta adquirir el Advisory Lock transaccional de 64 bits para el tenant indicado.
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> ObtenerLockTenantAsync(
        Npgsql.NpgsqlConnection conn,
        Guid tenantId,
        Npgsql.NpgsqlTransaction? tx = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        long key = ObtenerTenantLockKey(tenantId);
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", conn, tx);
        cmd.Parameters.AddWithValue(key);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is bool b && b;
    }
}
