using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFase5DashboardAgendaAndAlertas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ZonaHorariaId",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "America/Guayaquil");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_usuarios_TenantId_Id",
                table: "usuarios",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "alertas_procesales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipoOrigen = table.Column<int>(type: "integer", nullable: false),
                    OrigenId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReglaAlerta = table.Column<int>(type: "integer", nullable: false),
                    FechaObjetivoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FechaDisparoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Mensaje = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Severidad = table.Column<int>(type: "integer", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstadoResolucion = table.Column<int>(type: "integer", nullable: false),
                    ResueltaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoResolucion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Leida = table.Column<bool>(type: "boolean", nullable: false),
                    FechaLeidaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alertas_procesales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alertas_procesales_expedientes_TenantId_ExpedienteId",
                        columns: x => new { x.TenantId, x.ExpedienteId },
                        principalTable: "expedientes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alertas_procesales_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alertas_procesales_usuarios_TenantId_UsuarioId",
                        columns: x => new { x.TenantId, x.UsuarioId },
                        principalTable: "usuarios",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usuarios_TenantId_Id",
                table: "usuarios",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alertas_idempotencia_historica_general",
                table: "alertas_procesales",
                columns: new[] { "TenantId", "TipoOrigen", "OrigenId", "ReglaAlerta", "FechaObjetivoUtc" },
                unique: true,
                filter: "\"EstadoResolucion\" != 3 AND \"UsuarioId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_alertas_idempotencia_historica_usuario",
                table: "alertas_procesales",
                columns: new[] { "TenantId", "TipoOrigen", "OrigenId", "ReglaAlerta", "UsuarioId", "FechaObjetivoUtc" },
                unique: true,
                filter: "\"EstadoResolucion\" != 3 AND \"UsuarioId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_alertas_procesales_TenantId_ExpedienteId",
                table: "alertas_procesales",
                columns: new[] { "TenantId", "ExpedienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_alertas_procesales_TenantId_OrigenId_TipoOrigen",
                table: "alertas_procesales",
                columns: new[] { "TenantId", "OrigenId", "TipoOrigen" });

            migrationBuilder.CreateIndex(
                name: "IX_alertas_tenant_usuario_activas",
                table: "alertas_procesales",
                columns: new[] { "TenantId", "UsuarioId", "EstadoResolucion", "Leida" },
                filter: "\"EstadoResolucion\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alertas_procesales");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_usuarios_TenantId_Id",
                table: "usuarios");

            migrationBuilder.DropIndex(
                name: "IX_usuarios_TenantId_Id",
                table: "usuarios");

            migrationBuilder.DropColumn(
                name: "ZonaHorariaId",
                table: "tenants");
        }
    }
}
