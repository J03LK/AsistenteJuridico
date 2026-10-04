using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.1 — Reversibilidad de la migración Fase81IndiceSemantico sobre una base temporal (nunca la de desarrollo):
/// sin consumos de Worker/Sistema el Down deja el esquema anterior (sin tablas, función ni extensiones) y el Up lo
/// restaura; con un consumo sin usuario el Down aborta con FASE81_DOWN_PRECONDICION sin cambiar nada, sin tocar los
/// triggers de inmutabilidad de ai_usage_logs.
/// </summary>
public class Fase81MigracionReversibleTests(BaseDatosTemporal baseTemporal) : IClassFixture<BaseDatosTemporal>
{
    private const string MigracionAnterior = "20261003234913_Fase6XIaProcesandoDesde";

    private ApplicationDbContext Contexto() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(baseTemporal.ConnectionString).Options);

    private async Task<T?> EscalarAsync<T>(string sql)
    {
        await using var conexion = new NpgsqlConnection(baseTemporal.ConnectionString);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexion);
        var resultado = await cmd.ExecuteScalarAsync();
        return resultado is null or DBNull ? default : (T)resultado;
    }

    private async Task<(long Tablas, long Extensiones, string UsuarioIdNullable)> EsquemaAsync() => (
        await EscalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_name IN ('documento_indices','documento_fragmentos')"),
        await EscalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname IN ('vector','unaccent')"),
        (await EscalarAsync<string>("SELECT is_nullable FROM information_schema.columns WHERE table_name='ai_usage_logs' AND column_name='UsuarioId'"))!);

    private async Task MigrarAAsync(string? objetivo)
    {
        await using var context = Contexto();
        await context.GetService<IMigrator>().MigrateAsync(objetivo);
    }

    [Fact]
    public async Task Down_SinConsumosDeWorker_Revierte_YConConsumoDeWorker_AbortaSinCambios()
    {
        Assert.Equal((2L, 2L, "YES"), await EsquemaAsync());   // la base temporal está en la 8.1

        // 1. Sin filas con UsuarioId NULL: el Down deja el esquema exactamente como antes de la 8.1.
        await MigrarAAsync(MigracionAnterior);
        Assert.Equal((0L, 0L, "NO"), await EsquemaAsync());
        Assert.Equal(0L, await EscalarAsync<long>("SELECT count(*) FROM pg_proc WHERE proname = 'f_unaccent_es'"));

        // El Down no inventa un valor por defecto para UsuarioId (la columna original no lo tenía).
        Assert.Null(await EscalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_name='ai_usage_logs' AND column_name='UsuarioId'"));

        // 2. El Up lo restaura, también sin valor por defecto en UsuarioId.
        await MigrarAAsync(null);
        Assert.Equal((2L, 2L, "YES"), await EsquemaAsync());
        Assert.Null(await EscalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_name='ai_usage_logs' AND column_name='UsuarioId'"));

        // 3. Un consumo de Worker (UsuarioId NULL) confirmado: el Down debe abortar sin tocar nada.
        await EscalarAsync<object>(
            "INSERT INTO tenants (\"Id\",\"Nombre\",\"IdentificadorUrl\",\"Plan\",\"Activo\",\"ZonaHorariaId\",\"CreatedAt\") " +
            "VALUES ('11111111-1111-1111-1111-111111111111','T','t-81','Starter',true,'America/Guayaquil',now())");
        await EscalarAsync<object>(
            "INSERT INTO ai_usage_logs (\"Id\",\"TenantId\",\"UsuarioId\",\"Origen\",\"ActorSistema\",\"CasoUso\",\"ProviderId\",\"ModelId\"," +
            "\"TokensEntrada\",\"TokensSalida\",\"TotalTokens\",\"DuracionMs\",\"Exitoso\",\"CreatedAt\") " +
            "VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111',NULL,2,'worker:indexacion-semantica',6,'p','m',1,0,1,1,true,now())");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrarAAsync(MigracionAnterior));
        var postgres = error as PostgresException ?? error.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal("P0001", postgres!.SqlState);
        Assert.Contains("FASE81_DOWN_PRECONDICION", postgres.MessageText);

        // Esquema intacto y la fila inmutable sigue ahí (no se borró ni se modificó).
        Assert.Equal((2L, 2L, "YES"), await EsquemaAsync());
        Assert.Equal(1L, await EscalarAsync<long>("SELECT count(*) FROM ai_usage_logs WHERE \"UsuarioId\" IS NULL"));
        Assert.Equal("20261004044624_Fase81IndiceSemantico",
            await EscalarAsync<string>("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1 DESC LIMIT 1"));
    }
}
