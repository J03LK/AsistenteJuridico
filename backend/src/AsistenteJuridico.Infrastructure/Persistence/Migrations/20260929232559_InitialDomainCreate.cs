using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDomainCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "historial_auditorias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Entidad = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntidadId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Accion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ValoresAnterioresJson = table.Column<string>(type: "jsonb", nullable: true),
                    ValoresNuevosJson = table.Column<string>(type: "jsonb", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    UsuarioEmail = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Fecha = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historial_auditorias", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Ruc = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    IdentificadorUrl = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    ConfiguracionJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "clientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoIdentificacion = table.Column<int>(type: "integer", nullable: false),
                    Identificacion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NombreRazonSocial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Telefono = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Direccion = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clientes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_clientes_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "procesos_judiciales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    NumeroProceso = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Judicatura = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    JuezPonente = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    AccionInfraccion = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Materia = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EstadoJudicial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FechaInicio = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UltimaSincronizacion = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DetallesJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procesos_judiciales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_procesos_judiciales_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usuarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    NombreCompleto = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Rol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usuarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usuarios_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expedientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    NumeroExpediente = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Materia = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    Prioridad = table.Column<int>(type: "integer", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    AbogadoResponsableId = table.Column<Guid>(type: "uuid", nullable: true),
                    FechaApertura = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    FechaCierreEstimada = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FechaCierreReal = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expedientes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_expedientes_clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expedientes_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expedientes_usuarios_AbogadoResponsableId",
                        column: x => x.AbogadoResponsableId,
                        principalTable: "usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "audiencias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcesoJudicialId = table.Column<Guid>(type: "uuid", nullable: true),
                    FechaHora = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SalaOVirtual = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TipoAudiencia = table.Column<int>(type: "integer", nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    Notas = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audiencias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audiencias_expedientes_ExpedienteId",
                        column: x => x.ExpedienteId,
                        principalTable: "expedientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_audiencias_procesos_judiciales_ProcesoJudicialId",
                        column: x => x.ProcesoJudicialId,
                        principalTable: "procesos_judiciales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_audiencias_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TipoDocumento = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RutaAlmacenamiento = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TamanioBytes = table.Column<long>(type: "bigint", nullable: false),
                    HashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EstadoIa = table.Column<int>(type: "integer", nullable: false),
                    MetadatosJson = table.Column<string>(type: "jsonb", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documentos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_documentos_expedientes_ExpedienteId",
                        column: x => x.ExpedienteId,
                        principalTable: "expedientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_documentos_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expedientes_procesos_judiciales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcesoJudicialId = table.Column<Guid>(type: "uuid", nullable: false),
                    EsPrincipal = table.Column<bool>(type: "boolean", nullable: false),
                    FechaVinculacion = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    Observaciones = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expedientes_procesos_judiciales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_expedientes_procesos_judiciales_expedientes_ExpedienteId",
                        column: x => x.ExpedienteId,
                        principalTable: "expedientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_expedientes_procesos_judiciales_procesos_judiciales_Proceso~",
                        column: x => x.ProcesoJudicialId,
                        principalTable: "procesos_judiciales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expedientes_procesos_judiciales_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tareas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpedienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    AsignadoAUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FechaVencimiento = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Prioridad = table.Column<int>(type: "integer", nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    FechaCompletada = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tareas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tareas_expedientes_ExpedienteId",
                        column: x => x.ExpedienteId,
                        principalTable: "expedientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tareas_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tareas_usuarios_AsignadoAUsuarioId",
                        column: x => x.AsignadoAUsuarioId,
                        principalTable: "usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

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
                name: "IX_audiencias_TenantId_ExpedienteId",
                table: "audiencias",
                columns: new[] { "TenantId", "ExpedienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_audiencias_TenantId_FechaHora",
                table: "audiencias",
                columns: new[] { "TenantId", "FechaHora" });

            migrationBuilder.CreateIndex(
                name: "IX_audiencias_TenantId_ProcesoJudicialId",
                table: "audiencias",
                columns: new[] { "TenantId", "ProcesoJudicialId" });

            migrationBuilder.CreateIndex(
                name: "IX_clientes_TenantId_Identificacion",
                table: "clientes",
                columns: new[] { "TenantId", "Identificacion" },
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_clientes_TenantId_NombreRazonSocial",
                table: "clientes",
                columns: new[] { "TenantId", "NombreRazonSocial" });

            migrationBuilder.CreateIndex(
                name: "IX_documentos_ExpedienteId",
                table: "documentos",
                column: "ExpedienteId");

            migrationBuilder.CreateIndex(
                name: "IX_documentos_TenantId_EstadoIa",
                table: "documentos",
                columns: new[] { "TenantId", "EstadoIa" });

            migrationBuilder.CreateIndex(
                name: "IX_documentos_TenantId_ExpedienteId",
                table: "documentos",
                columns: new[] { "TenantId", "ExpedienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_documentos_TenantId_TipoDocumento",
                table: "documentos",
                columns: new[] { "TenantId", "TipoDocumento" });

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_AbogadoResponsableId",
                table: "expedientes",
                column: "AbogadoResponsableId");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_ClienteId",
                table: "expedientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_TenantId_AbogadoResponsableId",
                table: "expedientes",
                columns: new[] { "TenantId", "AbogadoResponsableId" });

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_TenantId_ClienteId",
                table: "expedientes",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_TenantId_Estado",
                table: "expedientes",
                columns: new[] { "TenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_TenantId_NumeroExpediente",
                table: "expedientes",
                columns: new[] { "TenantId", "NumeroExpediente" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_procesos_judiciales_ExpedienteId",
                table: "expedientes_procesos_judiciales",
                column: "ExpedienteId");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_procesos_judiciales_ProcesoJudicialId",
                table: "expedientes_procesos_judiciales",
                column: "ProcesoJudicialId");

            migrationBuilder.CreateIndex(
                name: "IX_expedientes_procesos_judiciales_TenantId_ExpedienteId_Proce~",
                table: "expedientes_procesos_judiciales",
                columns: new[] { "TenantId", "ExpedienteId", "ProcesoJudicialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_historial_auditorias_TenantId_Entidad_EntidadId",
                table: "historial_auditorias",
                columns: new[] { "TenantId", "Entidad", "EntidadId" });

            migrationBuilder.CreateIndex(
                name: "IX_historial_auditorias_TenantId_Fecha",
                table: "historial_auditorias",
                columns: new[] { "TenantId", "Fecha" });

            migrationBuilder.CreateIndex(
                name: "IX_procesos_judiciales_TenantId_EstadoJudicial",
                table: "procesos_judiciales",
                columns: new[] { "TenantId", "EstadoJudicial" });

            migrationBuilder.CreateIndex(
                name: "IX_procesos_judiciales_TenantId_Judicatura",
                table: "procesos_judiciales",
                columns: new[] { "TenantId", "Judicatura" });

            migrationBuilder.CreateIndex(
                name: "IX_procesos_judiciales_TenantId_NumeroProceso",
                table: "procesos_judiciales",
                columns: new[] { "TenantId", "NumeroProceso" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tareas_AsignadoAUsuarioId",
                table: "tareas",
                column: "AsignadoAUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_tareas_ExpedienteId",
                table: "tareas",
                column: "ExpedienteId");

            migrationBuilder.CreateIndex(
                name: "IX_tareas_TenantId_AsignadoAUsuarioId",
                table: "tareas",
                columns: new[] { "TenantId", "AsignadoAUsuarioId" });

            migrationBuilder.CreateIndex(
                name: "IX_tareas_TenantId_Estado",
                table: "tareas",
                columns: new[] { "TenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_tareas_TenantId_ExpedienteId",
                table: "tareas",
                columns: new[] { "TenantId", "ExpedienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_tareas_TenantId_FechaVencimiento",
                table: "tareas",
                columns: new[] { "TenantId", "FechaVencimiento" });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_IdentificadorUrl",
                table: "tenants",
                column: "IdentificadorUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usuarios_TenantId_Email",
                table: "usuarios",
                columns: new[] { "TenantId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audiencias");

            migrationBuilder.DropTable(
                name: "documentos");

            migrationBuilder.DropTable(
                name: "expedientes_procesos_judiciales");

            migrationBuilder.DropTable(
                name: "historial_auditorias");

            migrationBuilder.DropTable(
                name: "tareas");

            migrationBuilder.DropTable(
                name: "procesos_judiciales");

            migrationBuilder.DropTable(
                name: "expedientes");

            migrationBuilder.DropTable(
                name: "clientes");

            migrationBuilder.DropTable(
                name: "usuarios");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
