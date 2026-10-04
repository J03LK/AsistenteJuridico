using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;
using Pgvector;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 8.1 — Infraestructura y modelo del índice semántico (docs/FASE_8_CONTRATO.md v1.1):
    /// - extensiones vector (pgvector) y unaccent; función inmutable public.f_unaccent_es para la columna generada;
    /// - documento_indices (unicidades parciales, CHECK de dimensión 1536, hash, estado; xmin) y documento_fragmentos
    ///   (vector(1536), tsvector generado con índice GIN); FK compuestas tenant-aware;
    /// - clave alternativa (TenantId, Id) en documentos;
    /// - ai_usage_logs: UsuarioId nullable, Origen (default 1 = Usuario, solo metadatos: no actualiza filas, compatible
    ///   con los triggers de inmutabilidad), ActorSistema y CHECK de actor;
    /// - ai_messages.FuentesJson (jsonb).
    /// Requiere la imagen PostgreSQL con pgvector (docker/postgres/Dockerfile, procedimiento §3.1.1).
    /// </summary>
    public partial class Fase81IndiceSemantico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            // unaccent() no es IMMUTABLE y una columna generada lo exige: envoltorio inmutable con diccionario explícito.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION public.f_unaccent_es(texto text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
AS $$ SELECT public.unaccent('public.unaccent'::regdictionary, texto) $$;");

            migrationBuilder.AlterColumn<Guid>(
                name: "UsuarioId",
                table: "ai_usage_logs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            // Idempotente: UsuarioId nunca lleva valor por defecto (un NULL de Worker/Sistema no debe convertirse en
            // Guid.Empty). Solo cambia metadatos; no actualiza filas (compatible con los triggers de inmutabilidad).
            migrationBuilder.Sql("ALTER TABLE ai_usage_logs ALTER COLUMN \"UsuarioId\" DROP DEFAULT;");

            migrationBuilder.AddColumn<string>(
                name: "ActorSistema",
                table: "ai_usage_logs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "Origen",
                table: "ai_usage_logs",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<string>(
                name: "FuentesJson",
                table: "ai_messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_documentos_TenantId_Id",
                table: "documentos",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "documento_indices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Perfil = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Dimensiones = table.Column<int>(type: "integer", nullable: false),
                    Estado = table.Column<short>(type: "smallint", nullable: false),
                    HashContenido = table.Column<string>(type: "character(64)", nullable: true),
                    ProcesandoDesde = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcesadoPor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Intentos = table.Column<int>(type: "integer", nullable: false),
                    ProximoIntentoEn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CodigoError = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Fragmentos = table.Column<int>(type: "integer", nullable: false),
                    TokensTotales = table.Column<int>(type: "integer", nullable: false),
                    FragmentosCalculados = table.Column<int>(type: "integer", nullable: true),
                    LimiteAplicado = table.Column<int>(type: "integer", nullable: true),
                    IndexadoEn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documento_indices", x => x.Id);
                    table.UniqueConstraint("AK_documento_indices_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_documento_indices_Contadores", "\"Intentos\" >= 0 AND \"Fragmentos\" >= 0 AND \"TokensTotales\" >= 0");
                    table.CheckConstraint("CK_documento_indices_Dimensiones", "\"Dimensiones\" = 1536");
                    table.CheckConstraint("CK_documento_indices_Estado", "\"Estado\" BETWEEN 0 AND 5");
                    table.CheckConstraint("CK_documento_indices_HashContenido_formato", "\"HashContenido\" IS NULL OR \"HashContenido\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_documento_indices_documentos_TenantId_DocumentoId",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_documento_indices_expedientes_TenantId_ExpedienteId",
                        columns: x => new { x.TenantId, x.ExpedienteId },
                        principalTable: "expedientes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_documento_indices_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documento_fragmentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Orden = table.Column<int>(type: "integer", nullable: false),
                    Texto = table.Column<string>(type: "text", nullable: false),
                    Ubicacion = table.Column<string>(type: "jsonb", nullable: false),
                    RutaSeccion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CaracterInicio = table.Column<int>(type: "integer", nullable: false),
                    CaracterFin = table.Column<int>(type: "integer", nullable: false),
                    TokensEstimados = table.Column<int>(type: "integer", nullable: false),
                    HashFragmento = table.Column<string>(type: "character(64)", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TextoBusqueda = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('spanish'::regconfig, public.f_unaccent_es(\"Texto\"))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documento_fragmentos", x => x.Id);
                    table.CheckConstraint("CK_documento_fragmentos_HashFragmento_formato", "\"HashFragmento\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_documento_fragmentos_Rango", "\"Orden\" >= 0 AND \"CaracterInicio\" >= 0 AND \"CaracterFin\" >= \"CaracterInicio\" AND \"TokensEstimados\" >= 0");
                    table.ForeignKey(
                        name: "FK_documento_fragmentos_documento_indices_TenantId_IndiceId",
                        columns: x => new { x.TenantId, x.IndiceId },
                        principalTable: "documento_indices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_documento_fragmentos_documentos_TenantId_DocumentoId",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_documento_fragmentos_expedientes_TenantId_ExpedienteId",
                        columns: x => new { x.TenantId, x.ExpedienteId },
                        principalTable: "expedientes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_documento_fragmentos_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_usage_logs_Actor",
                table: "ai_usage_logs",
                sql: "(\"Origen\" = 1 AND \"UsuarioId\" IS NOT NULL AND \"ActorSistema\" IS NULL) OR (\"Origen\" IN (2, 3) AND \"UsuarioId\" IS NULL AND \"ActorSistema\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_documento_fragmentos_IndiceId_Orden",
                table: "documento_fragmentos",
                columns: new[] { "IndiceId", "Orden" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_documento_fragmentos_TenantId_DocumentoId",
                table: "documento_fragmentos",
                columns: new[] { "TenantId", "DocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_documento_fragmentos_TenantId_ExpedienteId_IndiceId",
                table: "documento_fragmentos",
                columns: new[] { "TenantId", "ExpedienteId", "IndiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_documento_fragmentos_TenantId_IndiceId",
                table: "documento_fragmentos",
                columns: new[] { "TenantId", "IndiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_documento_fragmentos_TextoBusqueda",
                table: "documento_fragmentos",
                column: "TextoBusqueda")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_documento_indices_Estado_ProximoIntentoEn",
                table: "documento_indices",
                columns: new[] { "Estado", "ProximoIntentoEn" });

            migrationBuilder.CreateIndex(
                name: "IX_documento_indices_Tenant_Expediente_Perfil_Indexado",
                table: "documento_indices",
                columns: new[] { "TenantId", "ExpedienteId", "Perfil" },
                filter: "\"Estado\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_documento_indices_TenantId_DocumentoId",
                table: "documento_indices",
                columns: new[] { "TenantId", "DocumentoId" });

            migrationBuilder.CreateIndex(
                name: "UX_documento_indices_Documento_Perfil_EnCurso",
                table: "documento_indices",
                columns: new[] { "DocumentoId", "Perfil" },
                unique: true,
                filter: "\"Estado\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "UX_documento_indices_Documento_Perfil_Vigente",
                table: "documento_indices",
                columns: new[] { "DocumentoId", "Perfil" },
                unique: true,
                filter: "\"Estado\" = 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // REVERSIBILIDAD CONDICIONADA (documentada; contrato §3.1.1 y §14.1):
            // - Precondición: no existe ninguna fila de ai_usage_logs con UsuarioId NULL. Por el CHECK de actor, solo
            //   las operaciones de Worker o Sistema pueden tenerlas; en la 8.1 ningún código de producción las crea.
            // - Motivo: devolver UsuarioId a NOT NULL exigiría modificar o borrar esas filas, y ai_usage_logs es
            //   inmutable (triggers que rechazan UPDATE y DELETE). Quitar Origen/ActorSistema conservando la
            //   nulabilidad dejaría esas filas sin actor y un esquema que el modelo anterior no puede leer.
            // - Comportamiento: si la precondición falla, la reversión se aborta con FASE81_DOWN_PRECONDICION antes
            //   de cualquier cambio (la migración corre en su transacción): el esquema queda intacto.
            // - Impacto: una vez existan consumos de Worker/Sistema, la 8.1 no se puede revertir por migración; el
            //   rollback de §3.1.1 queda limitado a restaurar el backup validado previo al cambio.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ai_usage_logs WHERE ""UsuarioId"" IS NULL) THEN
        RAISE EXCEPTION 'FASE81_DOWN_PRECONDICION: existen consumos de IA sin usuario (Worker/Sistema); UsuarioId no puede volver a ser obligatorio.';
    END IF;
END $$;");

            migrationBuilder.DropTable(
                name: "documento_fragmentos");

            migrationBuilder.DropTable(
                name: "documento_indices");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.f_unaccent_es(text);");

            // Npgsql no elimina extensiones al revertir una anotación. Se eliminan aquí para que el rollback de §3.1.1
            // (revertir esta migración y volver a la imagen sin pgvector) no deje la extensión vector sin sus binarios.
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS vector;");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS unaccent;");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_documentos_TenantId_Id",
                table: "documentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_usage_logs_Actor",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "ActorSistema",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "Origen",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "FuentesJson",
                table: "ai_messages");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            // Sin defaultValue: la columna original (Fase 6) no tenía valor por defecto. EF genera uno (Guid.Empty) al
            // volver a NOT NULL, lo que dejaría un DEFAULT que nunca existió. La precondición garantiza que no hay NULL.
            migrationBuilder.AlterColumn<Guid>(
                name: "UsuarioId",
                table: "ai_usage_logs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
