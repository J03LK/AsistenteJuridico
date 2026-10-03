using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.4 — Integridad en PostgreSQL (CK_documentos_HashSha256_formato y CK_documentos_TamanioBytes_no_negativo)
/// y migración Fase74DocumentosIntegridad: requisito previo que aborta sin cambios, Up y Down, sobre una base
/// de datos nueva y desechable creada por la prueba.
/// </summary>
public class Fase74IntegridadMigracionTests
{
    private const string MigracionFase71 = "20261003154927_Fase71DocumentosAccesoPermisosModelo";
    private const string MigracionFase74 = "20261003223301_Fase74DocumentosIntegridad";
    private const string HashValido = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // ══ CHECK en la base de desarrollo (migrada) ══════════════════════════

    private static async Task<(Guid TenantId, Guid ExpedienteId)> SeedExpedienteAsync(string connectionString)
    {
        var tenantId = Guid.NewGuid();
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            new TestTenantService { TenantId = tenantId });
        var sufijo = Guid.NewGuid().ToString("N")[..10];
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(), TenantId = tenantId, TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + sufijo[..8], NombreRazonSocial = "Cliente integridad " + sufijo, Activo = true
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClienteId = cliente.Id, NumeroExpediente = "EXP-INT-" + sufijo,
            Titulo = "Caso integridad", Materia = "Civil"
        };
        context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Estudio integridad " + sufijo, IdentificadorUrl = "fase74i-" + sufijo, ZonaHorariaId = "America/Guayaquil", Activo = true });
        context.AddRange(cliente, expediente);
        await context.SaveChangesAsync();
        return (tenantId, expediente.Id);
    }

    private static async Task InsertarDocumentoAsync(NpgsqlConnection conexion, NpgsqlTransaction? transaccion, Guid tenantId, Guid expedienteId, object hash, long tamanio)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO documentos ("Id","TenantId","ExpedienteId","Titulo","TipoDocumento","RutaAlmacenamiento","ContentType","TamanioBytes","HashSha256","EstadoIa","IsDeleted")
            VALUES (@id, @tenant, @expediente, 'Doc integridad', 'Escrito', 'x/y.pdf', 'application/pdf', @tamanio, @hash, 0, false)
            """, conexion, transaccion);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("expediente", expedienteId);
        cmd.Parameters.AddWithValue("tamanio", tamanio);
        cmd.Parameters.Add(new NpgsqlParameter("hash", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = hash });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Inserta en una transacción que siempre se revierte; devuelve la excepción de PostgreSQL o null.</summary>
    private static async Task<PostgresException?> IntentarInsertarAsync(object hash, long tamanio)
    {
        var (tenantId, expedienteId) = await SeedExpedienteAsync(TestConfiguration.PostgresConnectionString);
        await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        try
        {
            await InsertarDocumentoAsync(conexion, transaccion, tenantId, expedienteId, hash, tamanio);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    [Fact]
    public async Task Checks_ExistenConLaDefinicionExacta()
    {
        await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'documentos'::regclass AND contype = 'c' ORDER BY conname",
            conexion);
        await using var lector = await cmd.ExecuteReaderAsync();
        var checks = new List<(string, string)>();
        while (await lector.ReadAsync())
        {
            checks.Add((lector.GetString(0), lector.GetString(1)));
        }

        Assert.Equal(
            [
                ("CK_documentos_HashSha256_formato", "CHECK (((\"HashSha256\" IS NULL) OR ((\"HashSha256\")::text ~ '^[0-9a-f]{64}$'::text)))"),
                ("CK_documentos_TamanioBytes_no_negativo", "CHECK ((\"TamanioBytes\" >= 0))")
            ],
            checks);
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]    // mayúsculas
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]     // 63
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]    // 'g' no es hex
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\n")]   // 63 hex + salto de línea (64)
    [InlineData(" 123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]    // espacio inicial
    [InlineData("")]
    public async Task HashInvalido_RechazadoPorLaCheck(string hash)
    {
        var ex = await IntentarInsertarAsync(hash, 10);
        Assert.NotNull(ex);
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex!.SqlState);
        Assert.Equal("CK_documentos_HashSha256_formato", ex.ConstraintName);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]   // 65
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n")]  // 64 hex + salto de línea (65)
    public async Task HashDeMasDe64_RechazadoPorLaLongitudDeLaColumna(string hash)
    {
        // varchar(64): PostgreSQL lo rechaza por longitud (22001) antes de evaluar la CHECK
        var ex = await IntentarInsertarAsync(hash, 10);
        Assert.NotNull(ex);
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, ex!.SqlState);
    }

    [Fact]
    public async Task TamanioNegativo_RechazadoPorLaBase()
    {
        var ex = await IntentarInsertarAsync(HashValido, -1);
        Assert.NotNull(ex);
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex!.SqlState);
        Assert.Equal("CK_documentos_TamanioBytes_no_negativo", ex.ConstraintName);
    }

    [Fact]
    public async Task Historico_HashNuloYTamanio0_Permitidos_YHashValidoAceptado()
    {
        Assert.Null(await IntentarInsertarAsync(DBNull.Value, 0));     // documento histórico
        Assert.Null(await IntentarInsertarAsync(HashValido, 1));
    }

    // ══ Migración sobre una base nueva y desechable ═══════════════════════

    [Fact]
    public async Task Migracion_AbortaConDatosIncompatibles_YLuegoUpYDownFuncionan()
    {
        var baseDatos = "aj_test_fase74_" + Guid.NewGuid().ToString("N")[..12];
        var builder = new NpgsqlConnectionStringBuilder(TestConfiguration.PostgresConnectionString);
        var mantenimiento = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var conexionPrueba = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = baseDatos, Pooling = false }.ConnectionString;

        await EjecutarAsync(mantenimiento, $"CREATE DATABASE \"{baseDatos}\"");
        try
        {
            ApplicationDbContext NuevoContexto() => new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conexionPrueba).Options,
                new TestTenantService());

            // 1. Base limpia hasta la Fase 7.1 (sin las CHECK) e inserción de datos incompatibles
            await using (var context = NuevoContexto())
            {
                await context.GetService<IMigrator>().MigrateAsync(MigracionFase71);
            }

            var (tenantId, expedienteId) = await SeedExpedienteAsync(conexionPrueba);
            await using (var conexion = new NpgsqlConnection(conexionPrueba))
            {
                await conexion.OpenAsync();
                await InsertarDocumentoAsync(conexion, null, tenantId, expedienteId, HashValido.ToUpperInvariant(), 10);
                await InsertarDocumentoAsync(conexion, null, tenantId, expedienteId, DBNull.Value, -5);
                await InsertarDocumentoAsync(conexion, null, tenantId, expedienteId, DBNull.Value, 0);   // histórico válido
            }

            // 2. La migración aborta con el error explícito y no cambia nada
            await using (var context = NuevoContexto())
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => context.GetService<IMigrator>().MigrateAsync());
                Assert.Equal("P0001", ex.SqlState);
                Assert.Contains("FASE74_PRECONDICION_DOCUMENTOS_INCOMPATIBLES: 1 documento(s) con HashSha256 de formato invalido y 1 con TamanioBytes negativo", ex.MessageText);
            }
            Assert.Equal(0L, await EscalarAsync(conexionPrueba, "SELECT count(*) FROM pg_constraint WHERE conrelid = 'documentos'::regclass AND contype = 'c'"));
            Assert.Equal(0L, await EscalarAsync(conexionPrueba, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{MigracionFase74}'"));
            Assert.Equal(2L, await EscalarAsync(conexionPrueba,
                "SELECT count(*) FROM documentos WHERE (\"HashSha256\" IS NOT NULL AND \"HashSha256\" !~ '^[0-9a-f]{64}$') OR \"TamanioBytes\" < 0"));   // no corrige datos

            // 3. Corregidos los datos, Up crea las CHECK y no hay cambios de modelo pendientes
            await EjecutarAsync(conexionPrueba, "UPDATE documentos SET \"HashSha256\" = lower(\"HashSha256\") WHERE \"HashSha256\" IS NOT NULL");
            await EjecutarAsync(conexionPrueba, "UPDATE documentos SET \"TamanioBytes\" = 0 WHERE \"TamanioBytes\" < 0");
            await using (var context = NuevoContexto())
            {
                await context.GetService<IMigrator>().MigrateAsync();
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                Assert.False(context.Database.HasPendingModelChanges());
            }
            Assert.Equal(2L, await EscalarAsync(conexionPrueba, "SELECT count(*) FROM pg_constraint WHERE conrelid = 'documentos'::regclass AND contype = 'c'"));

            // 4. Down elimina las CHECK
            await using (var context = NuevoContexto())
            {
                await context.GetService<IMigrator>().MigrateAsync(MigracionFase71);
            }
            Assert.Equal(0L, await EscalarAsync(conexionPrueba, "SELECT count(*) FROM pg_constraint WHERE conrelid = 'documentos'::regclass AND contype = 'c'"));
            Assert.Equal(0L, await EscalarAsync(conexionPrueba, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{MigracionFase74}'"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await EjecutarAsync(mantenimiento, $"DROP DATABASE IF EXISTS \"{baseDatos}\" WITH (FORCE)");
        }
    }

    private static async Task EjecutarAsync(string connectionString, string sql)
    {
        await using var conexion = new NpgsqlConnection(connectionString);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexion);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> EscalarAsync(string connectionString, string sql)
    {
        await using var conexion = new NpgsqlConnection(connectionString);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexion);
        return await cmd.ExecuteScalarAsync();
    }
}
