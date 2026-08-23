using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHandoffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "handoffs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "handoffs",
                columns: table => new
                {
                    id_handoff = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    contexto = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    fecha_actualizacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    motivo = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    phone_cliente = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ultimo_mensaje_humano = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_handoffs", x => x.id_handoff);
                });

            migrationBuilder.CreateIndex(
                name: "IX_handoffs_estado",
                table: "handoffs",
                column: "estado");

            migrationBuilder.CreateIndex(
                name: "IX_handoffs_id_tenant_phone_cliente",
                table: "handoffs",
                columns: new[] { "id_tenant", "phone_cliente" });
        }
    }
}
