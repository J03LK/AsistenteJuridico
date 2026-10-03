using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 7.1 — Documentos: acceso, permisos y modelo.
    /// - Requisito previo: 0 documentos con ExpedienteId NULL; si hay alguno, la migración aborta sin cambiar nada
    ///   (se ejecuta dentro de la transacción de la migración).
    /// - ExpedienteId pasa a NOT NULL, sin valor por defecto (nunca se rellenan nulos).
    /// - FK tenant-aware (TenantId, ExpedienteId) -> expedientes(TenantId, Id) pasa de CASCADE a RESTRICT.
    /// - Nuevas columnas NombreArchivoOriginal (varchar 255) y Descripcion (varchar 1000), ambas nullable.
    /// - D2: retira el claim "Documentos.Upload" del rol AsistenteLegal en role_claims, que el seeder solo agrega
    ///   y nunca quita (los JWT se emiten desde Permissions.GetPermissionsForRole, que ya no lo incluye).
    /// </summary>
    public partial class Fase71DocumentosAccesoPermisosModelo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    nulos bigint;
                BEGIN
                    SELECT count(*) INTO nulos FROM documentos WHERE "ExpedienteId" IS NULL;
                    IF nulos > 0 THEN
                        RAISE EXCEPTION 'FASE71_PRECONDICION_EXPEDIENTE_NULL: % documento(s) sin ExpedienteId. La migracion se aborta sin cambios; asignelos a un expediente antes de migrar.', nulos;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_documentos_expedientes_TenantId_ExpedienteId",
                table: "documentos");

            migrationBuilder.AlterColumn<Guid>(
                name: "ExpedienteId",
                table: "documentos",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Descripcion",
                table: "documentos",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NombreArchivoOriginal",
                table: "documentos",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_documentos_expedientes_TenantId_ExpedienteId",
                table: "documentos",
                columns: new[] { "TenantId", "ExpedienteId" },
                principalTable: "expedientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                DELETE FROM role_claims
                WHERE "ClaimType" = 'permission'
                  AND "ClaimValue" = 'Documentos.Upload'
                  AND "RoleId" IN (SELECT "Id" FROM roles WHERE "Name" = 'AsistenteLegal');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO role_claims ("RoleId", "ClaimType", "ClaimValue")
                SELECT r."Id", 'permission', 'Documentos.Upload'
                FROM roles r
                WHERE r."Name" = 'AsistenteLegal'
                  AND NOT EXISTS (
                      SELECT 1 FROM role_claims rc
                      WHERE rc."RoleId" = r."Id" AND rc."ClaimType" = 'permission' AND rc."ClaimValue" = 'Documentos.Upload');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_documentos_expedientes_TenantId_ExpedienteId",
                table: "documentos");

            migrationBuilder.DropColumn(
                name: "Descripcion",
                table: "documentos");

            migrationBuilder.DropColumn(
                name: "NombreArchivoOriginal",
                table: "documentos");

            migrationBuilder.AlterColumn<Guid>(
                name: "ExpedienteId",
                table: "documentos",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_documentos_expedientes_TenantId_ExpedienteId",
                table: "documentos",
                columns: new[] { "TenantId", "ExpedienteId" },
                principalTable: "expedientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
