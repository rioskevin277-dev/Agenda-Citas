using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id_waitlist_entry = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_client = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_service_type = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_professional = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    fecha_desde = table.Column<DateTime>(type: "datetime2", nullable: true),
                    fecha_hasta = table.Column<DateTime>(type: "datetime2", nullable: true),
                    estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    fecha_actualizacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waitlist_entries", x => x.id_waitlist_entry);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_clients_id_client",
                        column: x => x.id_client,
                        principalTable: "clients",
                        principalColumn: "id_client",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_professionals_id_professional",
                        column: x => x.id_professional,
                        principalTable: "professionals",
                        principalColumn: "id_professional",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_service_types_id_service_type",
                        column: x => x.id_service_type,
                        principalTable: "service_types",
                        principalColumn: "id_service_type",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_id_client",
                table: "waitlist_entries",
                column: "id_client");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_id_professional",
                table: "waitlist_entries",
                column: "id_professional");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_id_service_type",
                table: "waitlist_entries",
                column: "id_service_type");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_id_tenant_estado",
                table: "waitlist_entries",
                columns: new[] { "id_tenant", "estado" });

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_id_tenant_id_service_type_id_professional_estado",
                table: "waitlist_entries",
                columns: new[] { "id_tenant", "id_service_type", "id_professional", "estado" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "waitlist_entries");
        }
    }
}
