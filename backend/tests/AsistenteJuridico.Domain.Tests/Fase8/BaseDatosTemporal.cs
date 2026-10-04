using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Base de datos PostgreSQL temporal y aislada para pruebas que escriben en tablas inmutables (ai_usage_logs): se crea
/// en el mismo servidor de desarrollo, se migra desde cero con todas las migraciones del proyecto y se elimina al
/// terminar. Así ninguna fila de prueba queda en la base de desarrollo, sin tocar los triggers de inmutabilidad.
/// Comprueba además que la cadena completa de migraciones (incluida la 8.1) se aplica sobre una base vacía.
/// </summary>
public sealed class BaseDatosTemporal : IAsyncLifetime
{
    public string Nombre { get; } = "aj_test_f81_" + Guid.NewGuid().ToString("N")[..12];
    public string ConnectionString { get; private set; } = string.Empty;

    private static NpgsqlConnectionStringBuilder Administracion() =>
        new(TestConfiguration.PostgresConnectionString) { Database = "postgres", Pooling = false };

    public async Task InitializeAsync()
    {
        await using (var conexion = new NpgsqlConnection(Administracion().ConnectionString))
        {
            await conexion.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{Nombre}\"", conexion);
            await cmd.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(TestConfiguration.PostgresConnectionString) { Database = Nombre }.ConnectionString;

        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(ConnectionString).Options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conexion = new NpgsqlConnection(Administracion().ConnectionString);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Nombre}\" WITH (FORCE)", conexion);
        await cmd.ExecuteNonQueryAsync();
    }
}
