using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TurnFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "turn_failures",
                columns: table => new
                {
                    id_turn_failure = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    phone_cliente = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    motivo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    detalle = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turn_failures", x => x.id_turn_failure);
                });

            migrationBuilder.CreateIndex(
                name: "IX_turn_failures_fecha_creacion",
                table: "turn_failures",
                column: "fecha_creacion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "turn_failures");
        }
    }
}
