using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCRMFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "estado",
                table: "clients",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "nuevo");

            migrationBuilder.AddColumn<DateTime>(
                name: "fecha_actualizacion",
                table: "clients",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<DateTime>(
                name: "proxima_cita",
                table: "clients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "clients",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "estado",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "fecha_actualizacion",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "proxima_cita",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "clients");
        }
    }
}
