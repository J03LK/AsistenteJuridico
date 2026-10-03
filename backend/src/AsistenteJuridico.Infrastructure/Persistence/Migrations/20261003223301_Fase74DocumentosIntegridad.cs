using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 7.4 — Integridad de documentos compatible con el histórico.
    /// - Requisito previo: 0 documentos con HashSha256 de formato inválido y 0 con TamanioBytes negativo. Si hay
    ///   alguno, la migración aborta con un error explícito y, al ejecutarse dentro de su transacción, no cambia nada.
    ///   Nunca se corrigen datos de forma silenciosa.
    /// - CK_documentos_HashSha256_formato: NULL (documentos antiguos) o SHA-256 en minúsculas. Sin NOT NULL.
    /// - CK_documentos_TamanioBytes_no_negativo: >= 0 (el histórico tiene filas con 0). Sin límite de 25 MiB.
    /// - No toca historial_auditorias: el histórico de auditoría queda intacto.
    /// </summary>
    public partial class Fase74DocumentosIntegridad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    hash_invalidos bigint;
                    tamanio_negativos bigint;
                BEGIN
                    SELECT count(*) INTO hash_invalidos
                    FROM documentos
                    WHERE "HashSha256" IS NOT NULL AND "HashSha256" !~ '^[0-9a-f]{64}$';

                    SELECT count(*) INTO tamanio_negativos
                    FROM documentos
                    WHERE "TamanioBytes" < 0;

                    IF hash_invalidos > 0 OR tamanio_negativos > 0 THEN
                        RAISE EXCEPTION 'FASE74_PRECONDICION_DOCUMENTOS_INCOMPATIBLES: % documento(s) con HashSha256 de formato invalido y % con TamanioBytes negativo. La migracion se aborta sin cambios; corrija esos datos antes de migrar.', hash_invalidos, tamanio_negativos;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_documentos_HashSha256_formato",
                table: "documentos",
                sql: "\"HashSha256\" IS NULL OR \"HashSha256\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_documentos_TamanioBytes_no_negativo",
                table: "documentos",
                sql: "\"TamanioBytes\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_documentos_HashSha256_formato",
                table: "documentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_documentos_TamanioBytes_no_negativo",
                table: "documentos");
        }
    }
}
