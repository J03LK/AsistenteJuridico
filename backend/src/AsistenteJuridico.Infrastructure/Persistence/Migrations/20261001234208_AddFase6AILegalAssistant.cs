using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFase6AILegalAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CasoUso = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_conversations", x => x.Id);
                    table.UniqueConstraint("AK_ai_conversations_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ai_conversations_expedientes_TenantId_ExpedienteId",
                        columns: x => new { x.TenantId, x.ExpedienteId },
                        principalTable: "expedientes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ai_conversations_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_conversations_usuarios_TenantId_UsuarioId",
                        columns: x => new { x.TenantId, x.UsuarioId },
                        principalTable: "usuarios",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rol = table.Column<int>(type: "integer", nullable: false),
                    Contenido = table.Column<string>(type: "text", nullable: false),
                    SystemPromptVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TokensEntrada = table.Column<int>(type: "integer", nullable: true),
                    TokensSalida = table.Column<int>(type: "integer", nullable: true),
                    DuracionMs = table.Column<int>(type: "integer", nullable: true),
                    ModelId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProviderId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FinishReason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ContextoAutorizado = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Disclaimer = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_messages_ai_conversations_TenantId_ConversationId",
                        columns: x => new { x.TenantId, x.ConversationId },
                        principalTable: "ai_conversations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_messages_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CasoUso = table.Column<int>(type: "integer", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TokensEntrada = table.Column<int>(type: "integer", nullable: false),
                    TokensSalida = table.Column<int>(type: "integer", nullable: false),
                    TotalTokens = table.Column<int>(type: "integer", nullable: false),
                    DuracionMs = table.Column<int>(type: "integer", nullable: false),
                    CostoEstimadoUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Exitoso = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CodigoError = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_usage_logs_ai_conversations_TenantId_ConversationId",
                        columns: x => new { x.TenantId, x.ConversationId },
                        principalTable: "ai_conversations",
                        principalColumns: new[] { "TenantId", "Id" });
                    table.ForeignKey(
                        name: "FK_ai_usage_logs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_usage_logs_usuarios_TenantId_UsuarioId",
                        columns: x => new { x.TenantId, x.UsuarioId },
                        principalTable: "usuarios",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_TenantId_CreatedAt",
                table: "ai_conversations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_TenantId_ExpedienteId",
                table: "ai_conversations",
                columns: new[] { "TenantId", "ExpedienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_TenantId_Id",
                table: "ai_conversations",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_TenantId_IsDeleted",
                table: "ai_conversations",
                columns: new[] { "TenantId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_TenantId_UsuarioId",
                table: "ai_conversations",
                columns: new[] { "TenantId", "UsuarioId" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_messages_TenantId_ConversationId_CreatedAt",
                table: "ai_messages",
                columns: new[] { "TenantId", "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_logs_TenantId_CasoUso",
                table: "ai_usage_logs",
                columns: new[] { "TenantId", "CasoUso" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_logs_TenantId_ConversationId",
                table: "ai_usage_logs",
                columns: new[] { "TenantId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_logs_TenantId_CreatedAt",
                table: "ai_usage_logs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_logs_TenantId_UsuarioId_CreatedAt",
                table: "ai_usage_logs",
                columns: new[] { "TenantId", "UsuarioId", "CreatedAt" });

            // Triggers PostgreSQL para garantizar la inmutabilidad de ai_usage_logs (auditoría deontológica)
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION trg_ai_usage_logs_immutable()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'AIUsageLog es un registro inmutable. Operaciones de UPDATE y DELETE están prohibidas por auditoría.'
        USING ERRCODE = '55000';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_ai_usage_logs_prevent_update
BEFORE UPDATE ON ai_usage_logs
FOR EACH ROW
EXECUTE FUNCTION trg_ai_usage_logs_immutable();

CREATE TRIGGER trg_ai_usage_logs_prevent_delete
BEFORE DELETE ON ai_usage_logs
FOR EACH ROW
EXECUTE FUNCTION trg_ai_usage_logs_immutable();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_ai_usage_logs_prevent_update ON ai_usage_logs;
DROP TRIGGER IF EXISTS trg_ai_usage_logs_prevent_delete ON ai_usage_logs;
DROP FUNCTION IF EXISTS trg_ai_usage_logs_immutable();
");

            migrationBuilder.DropTable(
                name: "ai_messages");

            migrationBuilder.DropTable(
                name: "ai_usage_logs");

            migrationBuilder.DropTable(
                name: "ai_conversations");
        }
    }
}
