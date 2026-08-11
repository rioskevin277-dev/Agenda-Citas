using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "recordatorio_1_horas",
                table: "tenants",
                type: "int",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "recordatorio_2_horas",
                table: "tenants",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<bool>(
                name: "recordatorio_habilitado",
                table: "tenants",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "reminder_logs",
                columns: table => new
                {
                    id_reminder_log = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_appointment = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    etapa = table.Column<int>(type: "int", nullable: false),
                    fecha_programada = table.Column<DateTime>(type: "datetime2", nullable: true),
                    fecha_intento = table.Column<DateTime>(type: "datetime2", nullable: true),
                    estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "sent"),
                    wamid = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    reintentos = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_logs", x => x.id_reminder_log);
                    table.ForeignKey(
                        name: "FK_reminder_logs_appointments_id_appointment",
                        column: x => x.id_appointment,
                        principalTable: "appointments",
                        principalColumn: "id_appointment",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_logs_id_appointment_etapa",
                table: "reminder_logs",
                columns: new[] { "id_appointment", "etapa" });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_logs_id_tenant",
                table: "reminder_logs",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_logs_wamid",
                table: "reminder_logs",
                column: "wamid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminder_logs");

            migrationBuilder.DropColumn(
                name: "recordatorio_1_horas",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "recordatorio_2_horas",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "recordatorio_habilitado",
                table: "tenants");
        }
    }
}
