using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase7;

/// <summary>
/// Fase 7.1 — Esquema real en PostgreSQL tras la migración Fase71DocumentosAccesoPermisosModelo:
/// ExpedienteId NOT NULL, FK tenant-aware con ON DELETE RESTRICT, columnas nuevas y claims de rol.
/// Las pruebas que fuerzan violaciones se ejecutan en una transacción que siempre se revierte.
/// </summary>
public class Fase71EsquemaPostgreSqlTests
{
    private const string MigracionFase71 = "20261003154927_Fase71DocumentosAccesoPermisosModelo";

    private static async Task<NpgsqlConnection> AbrirAsync()
    {
        var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
        await conexion.OpenAsync();
        return conexion;
    }

    private static async Task<object?> EscalarAsync(string sql)
    {
        await using var conexion = await AbrirAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        return await comando.ExecuteScalarAsync();
    }

    private static async Task<(Guid TenantId, Guid ExpedienteId)> SeedTenantConExpedienteAsync()
    {
        var tenantId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(TestConfiguration.PostgresConnectionString).Options;
        await using var context = new ApplicationDbContext(options, new TestTenantService { TenantId = tenantId });
        var sufijo = Guid.NewGuid().ToString("N")[..10];
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TipoIdentificacion = TipoIdentificacion.Cedula,
            Identificacion = "17" + sufijo[..8],
            NombreRazonSocial = "Cliente esquema " + sufijo,
            Activo = true
        };
        var expediente = new Expediente
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClienteId = cliente.Id,
            NumeroExpediente = "EXP-SCH-" + sufijo,
            Titulo = "Caso esquema",
            Materia = "Civil"
        };
        context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Estudio esquema " + sufijo, IdentificadorUrl = "fase71-sch-" + sufijo, ZonaHorariaId = "America/Guayaquil", Activo = true });
        context.AddRange(cliente, expediente);
        await context.SaveChangesAsync();
        return (tenantId, expediente.Id);
    }

    private const string InsertDocumento = """
        INSERT INTO documentos ("Id","TenantId","ExpedienteId","Titulo","TipoDocumento","RutaAlmacenamiento","ContentType","TamanioBytes","EstadoIa","IsDeleted")
        VALUES (@id, @tenant, @expediente, 'Doc esquema', 'Escrito', 'x/y.pdf', 'application/pdf', 1, 0, false)
        """;

    private static async Task<PostgresException> EjecutarEnTransaccionRevertidaAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task> accion)
    {
        await using var conexion = await AbrirAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        try
        {
            return await Assert.ThrowsAsync<PostgresException>(() => accion(conexion, transaccion));
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    [Fact]
    public async Task MigracionFase71_Aplicada_YSinDocumentosConExpedienteNulo()
    {
        Assert.Equal(1L, await EscalarAsync($"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{MigracionFase71}'"));
        Assert.Equal(0L, await EscalarAsync("SELECT count(*) FROM documentos WHERE \"ExpedienteId\" IS NULL"));
    }

    [Fact]
    public async Task ExpedienteId_EsNotNull()
    {
        Assert.Equal("NO", await EscalarAsync(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'documentos' AND column_name = 'ExpedienteId'"));
    }

    [Fact]
    public async Task FkTenantAware_ConOnDeleteRestrict()
    {
        await using var conexion = await AbrirAsync();
        await using var comando = new NpgsqlCommand("""
            SELECT c.confdeltype::text, pg_get_constraintdef(c.oid)
            FROM pg_constraint c
            WHERE c.conrelid = 'documentos'::regclass AND c.contype = 'f' AND c.confrelid = 'expedientes'::regclass
            """, conexion);
        await using var lector = await comando.ExecuteReaderAsync();

        Assert.True(await lector.ReadAsync());
        Assert.Equal("r", lector.GetString(0)); // r = RESTRICT (c = CASCADE)
        Assert.Equal(
            "FOREIGN KEY (\"TenantId\", \"ExpedienteId\") REFERENCES expedientes(\"TenantId\", \"Id\") ON DELETE RESTRICT",
            lector.GetString(1));
        Assert.False(await lector.ReadAsync()); // una sola FK hacia expedientes
    }

    [Fact]
    public async Task ColumnasNuevas_ConTipoYLongitudDelContrato()
    {
        await using var conexion = await AbrirAsync();
        await using var comando = new NpgsqlCommand("""
            SELECT column_name, data_type, character_maximum_length, is_nullable
            FROM information_schema.columns
            WHERE table_name = 'documentos' AND column_name IN ('Descripcion', 'NombreArchivoOriginal')
            ORDER BY column_name
            """, conexion);
        await using var lector = await comando.ExecuteReaderAsync();

        var columnas = new List<string>();
        while (await lector.ReadAsync())
        {
            columnas.Add($"{lector.GetString(0)}|{lector.GetString(1)}|{lector.GetInt32(2)}|{lector.GetString(3)}");
        }

        Assert.Equal(
            ["Descripcion|character varying|1000|YES", "NombreArchivoOriginal|character varying|255|YES"],
            columnas);
    }

    [Fact]
    public async Task InsertarDocumentoSinExpediente_RechazadoPorNotNull()
    {
        var (tenantId, _) = await SeedTenantConExpedienteAsync();

        var ex = await EjecutarEnTransaccionRevertidaAsync(async (conexion, transaccion) =>
        {
            await using var comando = new NpgsqlCommand(InsertDocumento, conexion, transaccion);
            comando.Parameters.AddWithValue("id", Guid.NewGuid());
            comando.Parameters.AddWithValue("tenant", tenantId);
            comando.Parameters.AddWithValue("expediente", DBNull.Value);
            await comando.ExecuteNonQueryAsync();
        });

        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
        Assert.Equal("ExpedienteId", ex.ColumnName);
    }

    [Fact]
    public async Task BorrarFisicamenteExpedienteConDocumentos_RechazadoPorRestrict()
    {
        var (tenantId, expedienteId) = await SeedTenantConExpedienteAsync();

        var ex = await EjecutarEnTransaccionRevertidaAsync(async (conexion, transaccion) =>
        {
            await using (var insert = new NpgsqlCommand(InsertDocumento, conexion, transaccion))
            {
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("tenant", tenantId);
                insert.Parameters.AddWithValue("expediente", expedienteId);
                await insert.ExecuteNonQueryAsync();
            }

            await using var delete = new NpgsqlCommand("DELETE FROM expedientes WHERE \"Id\" = @id", conexion, transaccion);
            delete.Parameters.AddWithValue("id", expedienteId);
            await delete.ExecuteNonQueryAsync();
        });

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_documentos_expedientes_TenantId_ExpedienteId", ex.ConstraintName);

        // La transacción se revirtió: el expediente sigue existiendo
        Assert.Equal(1L, await EscalarAsync($"SELECT count(*) FROM expedientes WHERE \"Id\" = '{expedienteId}'"));
    }

    [Fact]
    public async Task DocumentoConExpedienteDeOtroTenant_RechazadoPorFkTenantAware()
    {
        var (tenantA, _) = await SeedTenantConExpedienteAsync();
        var (_, expedienteDeB) = await SeedTenantConExpedienteAsync();

        var ex = await EjecutarEnTransaccionRevertidaAsync(async (conexion, transaccion) =>
        {
            await using var comando = new NpgsqlCommand(InsertDocumento, conexion, transaccion);
            comando.Parameters.AddWithValue("id", Guid.NewGuid());
            comando.Parameters.AddWithValue("tenant", tenantA);
            comando.Parameters.AddWithValue("expediente", expedienteDeB);
            await comando.ExecuteNonQueryAsync();
        });

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_documentos_expedientes_TenantId_ExpedienteId", ex.ConstraintName);
    }

    [Fact]
    public async Task RoleClaims_AsistenteLegalSinDocumentosUpload()
    {
        Assert.Equal(0L, await EscalarAsync("""
            SELECT count(*) FROM role_claims rc JOIN roles r ON r."Id" = rc."RoleId"
            WHERE r."Name" = 'AsistenteLegal' AND rc."ClaimType" = 'permission' AND rc."ClaimValue" = 'Documentos.Upload'
            """));
    }
}
