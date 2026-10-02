using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFase4BusinessEntitiesAndRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_audiencias_expedientes_ExpedienteId",
                table: "audiencias");

            migrationBuilder.DropForeignKey(
                name: "FK_audiencias_procesos_judiciales_ProcesoJudicialId",
                table: "audiencias");

            migrationBuilder.DropForeignKey(
                name: "FK_documentos_expedientes_ExpedienteId",
                table: "documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_expedientes_clientes_ClienteId",
                table: "expedientes");

            migrationBuilder.DropForeignKey(
                name: "FK_expedientes_procesos_judiciales_expedientes_ExpedienteId",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropForeignKey(
                name: "FK_expedientes_procesos_judiciales_procesos_judiciales_Proceso~",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropForeignKey(
                name: "FK_tareas_expedientes_ExpedienteId",
                table: "tareas");

            migrationBuilder.DropIndex(
                name: "IX_tareas_ExpedienteId",
                table: "tareas");

            migrationBuilder.DropIndex(
                name: "IX_tareas_TenantId_AsignadoAUsuarioId",
                table: "tareas");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_procesos_judiciales_ExpedienteId",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_procesos_judiciales_ProcesoJudicialId",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_ClienteId",
                table: "expedientes");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_TenantId_NumeroExpediente",
                table: "expedientes");

            migrationBuilder.DropIndex(
                name: "IX_documentos_ExpedienteId",
                table: "documentos");

            migrationBuilder.DropIndex(
                name: "IX_documentos_TenantId_ExpedienteId",
                table: "documentos");

            migrationBuilder.DropIndex(
                name: "IX_clientes_TenantId_Identificacion",
                table: "clientes");

            migrationBuilder.DropIndex(
                name: "IX_audiencias_ExpedienteId",
                table: "audiencias");

            migrationBuilder.DropIndex(
                name: "IX_audiencias_ProcesoJudicialId",
                table: "audiencias");

            migrationBuilder.DropIndex(
                name: "IX_audiencias_TenantId_Estado",
                table: "audiencias");

            migrationBuilder.DropIndex(
                name: "IX_audiencias_TenantId_FechaHora",
                table: "audiencias");

            migrationBuilder.AlterColumn<string>(
                name: "Descripcion",
                table: "tareas",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "tareas",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "procesos_judiciales",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "expedientes",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Descripcion",
                table: "expedientes",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "expedientes",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "documentos",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "documentos",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "clientes",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AlterColumn<string>(
                name: "SalaOVirtual",
                table: "audiencias",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "audiencias",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_procesos_judiciales_TenantId_Id",
                table: "procesos_judiciales",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_expedientes_TenantId_Id",
                table: "expedientes",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_clientes_TenantId_Id",
                table: "clientes",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "tenant_secuencias",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoSecuencia = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Anio = table.Column<int>(type: "integer", nullable: false),
                    UltimoValor = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_secuencias", x => new { x.TenantId, x.TipoSecuencia, x.Anio });
                    table.ForeignKey(
                        name: "FK_tenant_secuencias_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tareas_TenantId_AsignadoAUsuarioId_FechaVencimiento_Estado",
                table: "tareas",
                columns: new[] { "TenantId", "AsignadoAUsuarioId", "FechaVencimiento", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_procesos_judiciales_TenantId_Id",
                table: "procesos_judiciales",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_procesos_judiciales_TenantId_ExpedienteId",
                table: "expedientes_procesos_judiciales",
                columns: new[] { "TenantId", "ExpedienteId" },
                unique: true,
                filter: "\"EsPrincipal\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_procesos_judiciales_TenantId_ProcesoJudicialId",
                table: "expedientes_procesos_judiciales",
                columns: new[] { "TenantId", "ProcesoJudicialId" });

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_TenantId_Id",
                table: "expedientes",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_TenantId_NumeroExpediente",
                table: "expedientes",
                columns: new[] { "TenantId", "NumeroExpediente" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_documentos_TenantId_ExpedienteId",
                table: "documentos",
                columns: new[] { "TenantId", "ExpedienteId" },
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_clientes_TenantId_Id",
                table: "clientes",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_clientes_TenantId_Identificacion",
                table: "clientes",
                columns: new[] { "TenantId", "Identificacion" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_audiencias_TenantId_FechaHora_Estado",
                table: "audiencias",
                columns: new[] { "TenantId", "FechaHora", "Estado" });

            migrationBuilder.AddForeignKey(
                name: "FK_audiencias_expedientes_TenantId_ExpedienteId",
                table: "audiencias",
                columns: new[] { "TenantId", "ExpedienteId" },
                principalTable: "expedientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_audiencias_procesos_judiciales_TenantId_ProcesoJudicialId",
                table: "audiencias",
                columns: new[] { "TenantId", "ProcesoJudicialId" },
                principalTable: "procesos_judiciales",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_documentos_expedientes_TenantId_ExpedienteId",
                table: "documentos",
                columns: new[] { "TenantId", "ExpedienteId" },
                principalTable: "expedientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_expedientes_clientes_TenantId_ClienteId",
                table: "expedientes",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "clientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expedientes_procesos_judiciales_expedientes_TenantId_Expedi~",
                table: "expedientes_procesos_judiciales",
                columns: new[] { "TenantId", "ExpedienteId" },
                principalTable: "expedientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_expedientes_procesos_judiciales_procesos_judiciales_TenantI~",
                table: "expedientes_procesos_judiciales",
                columns: new[] { "TenantId", "ProcesoJudicialId" },
                principalTable: "procesos_judiciales",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tareas_expedientes_TenantId_ExpedienteId",
                table: "tareas",
                columns: new[] { "TenantId", "ExpedienteId" },
                principalTable: "expedientes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_audiencias_expedientes_TenantId_ExpedienteId",
                table: "audiencias");

            migrationBuilder.DropForeignKey(
                name: "FK_audiencias_procesos_judiciales_TenantId_ProcesoJudicialId",
                table: "audiencias");

            migrationBuilder.DropForeignKey(
                name: "FK_documentos_expedientes_TenantId_ExpedienteId",
                table: "documentos");

            migrationBuilder.DropForeignKey(
                name: "FK_expedientes_clientes_TenantId_ClienteId",
                table: "expedientes");

            migrationBuilder.DropForeignKey(
                name: "FK_expedientes_procesos_judiciales_expedientes_TenantId_Expedi~",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropForeignKey(
                name: "FK_expedientes_procesos_judiciales_procesos_judiciales_TenantI~",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropForeignKey(
                name: "FK_tareas_expedientes_TenantId_ExpedienteId",
                table: "tareas");

            migrationBuilder.DropTable(
                name: "tenant_secuencias");

            migrationBuilder.DropIndex(
                name: "IX_tareas_TenantId_AsignadoAUsuarioId_FechaVencimiento_Estado",
                table: "tareas");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_procesos_judiciales_TenantId_Id",
                table: "procesos_judiciales");

            migrationBuilder.DropIndex(
                name: "IX_procesos_judiciales_TenantId_Id",
                table: "procesos_judiciales");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_procesos_judiciales_TenantId_ExpedienteId",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_procesos_judiciales_TenantId_ProcesoJudicialId",
                table: "expedientes_procesos_judiciales");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_expedientes_TenantId_Id",
                table: "expedientes");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_TenantId_Id",
                table: "expedientes");

            migrationBuilder.DropIndex(
                name: "IX_expedientes_TenantId_NumeroExpediente",
                table: "expedientes");

            migrationBuilder.DropIndex(
                name: "IX_documentos_TenantId_ExpedienteId",
                table: "documentos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_clientes_TenantId_Id",
                table: "clientes");

            migrationBuilder.DropIndex(
                name: "IX_clientes_TenantId_Id",
                table: "clientes");

            migrationBuilder.DropIndex(
                name: "IX_clientes_TenantId_Identificacion",
                table: "clientes");

            migrationBuilder.DropIndex(
                name: "IX_audiencias_TenantId_FechaHora_Estado",
                table: "audiencias");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "tareas");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "procesos_judiciales");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "expedientes");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "documentos");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "clientes");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "audiencias");

            migrationBuilder.AlterColumn<string>(
                name: "Descripcion",
                table: "tareas",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "expedientes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(250)",
                oldMaxLength: 250);

            migrationBuilder.AlterColumn<string>(
                name: "Descripcion",
                table: "expedientes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "documentos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(250)",
                oldMaxLength: 250);

            migrationBuilder.AlterColumn<string>(
                name: "SalaOVirtual",
                table: "audiencias",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(250)",
                oldMaxLength: 250);

            migrationBuilder.CreateIndex(
                name: "IX_tareas_ExpedienteId",
                table: "tareas",
                column: "ExpedienteId");

            migrationBuilder.CreateIndex(
                name: "IX_tareas_TenantId_AsignadoAUsuarioId",
                table: "tareas",
                columns: new[] { "TenantId", "AsignadoAUsuarioId" });

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_procesos_judiciales_ExpedienteId",
                table: "expedientes_procesos_judiciales",
                column: "ExpedienteId");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_procesos_judiciales_ProcesoJudicialId",
                table: "expedientes_procesos_judiciales",
                column: "ProcesoJudicialId");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_ClienteId",
                table: "expedientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_TenantId_NumeroExpediente",
                table: "expedientes",
                columns: new[] { "TenantId", "NumeroExpediente" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_documentos_ExpedienteId",
                table: "documentos",
                column: "ExpedienteId");

            migrationBuilder.CreateIndex(
                name: "IX_documentos_TenantId_ExpedienteId",
                table: "documentos",
                columns: new[] { "TenantId", "ExpedienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_clientes_TenantId_Identificacion",
                table: "clientes",
                columns: new[] { "TenantId", "Identificacion" },
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_audiencias_ExpedienteId",
                table: "audiencias",
                column: "ExpedienteId");

            migrationBuilder.CreateIndex(
                name: "IX_audiencias_ProcesoJudicialId",
                table: "audiencias",
                column: "ProcesoJudicialId");

            migrationBuilder.CreateIndex(
                name: "IX_audiencias_TenantId_Estado",
                table: "audiencias",
                columns: new[] { "TenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_audiencias_TenantId_FechaHora",
                table: "audiencias",
                columns: new[] { "TenantId", "FechaHora" });

            migrationBuilder.AddForeignKey(
                name: "FK_audiencias_expedientes_ExpedienteId",
                table: "audiencias",
                column: "ExpedienteId",
                principalTable: "expedientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_audiencias_procesos_judiciales_ProcesoJudicialId",
                table: "audiencias",
                column: "ProcesoJudicialId",
                principalTable: "procesos_judiciales",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_documentos_expedientes_ExpedienteId",
                table: "documentos",
                column: "ExpedienteId",
                principalTable: "expedientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_expedientes_clientes_ClienteId",
                table: "expedientes",
                column: "ClienteId",
                principalTable: "clientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_expedientes_procesos_judiciales_expedientes_ExpedienteId",
                table: "expedientes_procesos_judiciales",
                column: "ExpedienteId",
                principalTable: "expedientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_expedientes_procesos_judiciales_procesos_judiciales_Proceso~",
                table: "expedientes_procesos_judiciales",
                column: "ProcesoJudicialId",
                principalTable: "procesos_judiciales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tareas_expedientes_ExpedienteId",
                table: "tareas",
                column: "ExpedienteId",
                principalTable: "expedientes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
