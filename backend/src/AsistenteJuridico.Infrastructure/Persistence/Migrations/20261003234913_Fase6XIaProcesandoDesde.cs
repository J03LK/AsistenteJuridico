using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 6.X (X1, DA-2 = A1) — Columna documentos."IaProcesandoDesde" (timestamptz, nullable): inicio del lease de
    /// Procesando. Sin relleno de datos: los documentos históricos en Procesando con NULL los recupera el worker con
    /// el fallback COALESCE(IaProcesandoDesde, UpdatedAt, CreatedAt), exclusivo para ese histórico.
    /// </summary>
    public partial class Fase6XIaProcesandoDesde : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IaProcesandoDesde",
                table: "documentos",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IaProcesandoDesde",
                table: "documentos");
        }
    }
}
