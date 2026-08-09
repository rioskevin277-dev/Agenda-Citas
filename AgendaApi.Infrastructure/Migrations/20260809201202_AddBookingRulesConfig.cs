using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingRulesConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "antelacion_maxima_dias",
                table: "tenants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "antelacion_minima_horas",
                table: "tenants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "capacidad_maxima",
                table: "service_types",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "antelacion_maxima_dias",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "antelacion_minima_horas",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "capacidad_maxima",
                table: "service_types");
        }
    }
}
