using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfessionals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_availability_rules_id_tenant",
                table: "availability_rules");

            migrationBuilder.DropIndex(
                name: "IX_availability_exceptions_id_tenant_fecha",
                table: "availability_exceptions");

            migrationBuilder.AddColumn<Guid>(
                name: "id_professional",
                table: "availability_rules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_professional",
                table: "availability_exceptions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_professional",
                table: "appointments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "professionals",
                columns: table => new
                {
                    id_professional = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    email = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    telefono = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_professionals", x => x.id_professional);
                    table.ForeignKey(
                        name: "FK_professionals_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "professional_services",
                columns: table => new
                {
                    id_professional = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_service_type = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_professional_services", x => new { x.id_professional, x.id_service_type });
                    table.ForeignKey(
                        name: "FK_professional_services_professionals_id_professional",
                        column: x => x.id_professional,
                        principalTable: "professionals",
                        principalColumn: "id_professional",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_professional_services_service_types_id_service_type",
                        column: x => x.id_service_type,
                        principalTable: "service_types",
                        principalColumn: "id_service_type",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_availability_rules_id_tenant_id_professional",
                table: "availability_rules",
                columns: new[] { "id_tenant", "id_professional" });

            migrationBuilder.CreateIndex(
                name: "IX_availability_exceptions_id_tenant_fecha_id_professional",
                table: "availability_exceptions",
                columns: new[] { "id_tenant", "fecha", "id_professional" });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_id_professional",
                table: "appointments",
                column: "id_professional");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_id_tenant_id_professional_fecha_inicio",
                table: "appointments",
                columns: new[] { "id_tenant", "id_professional", "fecha_inicio" });

            migrationBuilder.CreateIndex(
                name: "IX_professional_services_id_service_type",
                table: "professional_services",
                column: "id_service_type");

            migrationBuilder.CreateIndex(
                name: "IX_professionals_id_tenant_nombre",
                table: "professionals",
                columns: new[] { "id_tenant", "nombre" });

            migrationBuilder.AddForeignKey(
                name: "FK_appointments_professionals_id_professional",
                table: "appointments",
                column: "id_professional",
                principalTable: "professionals",
                principalColumn: "id_professional",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_appointments_professionals_id_professional",
                table: "appointments");

            migrationBuilder.DropTable(
                name: "professional_services");

            migrationBuilder.DropTable(
                name: "professionals");

            migrationBuilder.DropIndex(
                name: "IX_availability_rules_id_tenant_id_professional",
                table: "availability_rules");

            migrationBuilder.DropIndex(
                name: "IX_availability_exceptions_id_tenant_fecha_id_professional",
                table: "availability_exceptions");

            migrationBuilder.DropIndex(
                name: "IX_appointments_id_professional",
                table: "appointments");

            migrationBuilder.DropIndex(
                name: "IX_appointments_id_tenant_id_professional_fecha_inicio",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "id_professional",
                table: "availability_rules");

            migrationBuilder.DropColumn(
                name: "id_professional",
                table: "availability_exceptions");

            migrationBuilder.DropColumn(
                name: "id_professional",
                table: "appointments");

            migrationBuilder.CreateIndex(
                name: "IX_availability_rules_id_tenant",
                table: "availability_rules",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "IX_availability_exceptions_id_tenant_fecha",
                table: "availability_exceptions",
                columns: new[] { "id_tenant", "fecha" });
        }
    }
}
