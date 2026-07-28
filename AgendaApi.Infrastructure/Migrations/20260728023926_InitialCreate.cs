using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    nombre_comercial = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    correo = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    telefono = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    direccion = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    calendar_provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "google"),
                    activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    fecha_actualizacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id_tenant);
                });

            migrationBuilder.CreateTable(
                name: "availability_exceptions",
                columns: table => new
                {
                    id_availability_exception = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    fecha = table.Column<DateTime>(type: "datetime2", nullable: false),
                    dia_completo = table.Column<bool>(type: "bit", nullable: false),
                    hora_inicio = table.Column<TimeSpan>(type: "time", nullable: true),
                    hora_fin = table.Column<TimeSpan>(type: "time", nullable: true),
                    motivo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_exceptions", x => x.id_availability_exception);
                    table.ForeignKey(
                        name: "FK_availability_exceptions_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "availability_rules",
                columns: table => new
                {
                    id_availability_rule = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    dia_semana = table.Column<int>(type: "int", nullable: false),
                    hora_inicio = table.Column<TimeSpan>(type: "time", nullable: false),
                    hora_fin = table.Column<TimeSpan>(type: "time", nullable: false),
                    activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_rules", x => x.id_availability_rule);
                    table.ForeignKey(
                        name: "FK_availability_rules_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_connections",
                columns: table => new
                {
                    id_calendar_connection = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    account_email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    access_token_encrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    refresh_token_encrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    token_expires_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    calendar_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    sync_channel_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    sync_channel_expires_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    fecha_actualizacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_connections", x => x.id_calendar_connection);
                    table.ForeignKey(
                        name: "FK_calendar_connections_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    id_client = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    whatsapp = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    email = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    notas = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    ultima_interaccion = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clients", x => x.id_client);
                    table.ForeignKey(
                        name: "FK_clients_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_types",
                columns: table => new
                {
                    id_service_type = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    descripcion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    duracion_minutos = table.Column<int>(type: "int", nullable: false),
                    buffer_minutos = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    precio = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    activo = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_types", x => x.id_service_type);
                    table.ForeignKey(
                        name: "FK_service_types_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "appointments",
                columns: table => new
                {
                    id_appointment = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_client = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_service_type = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    fecha_inicio = table.Column<DateTime>(type: "datetime2", nullable: false),
                    fecha_fin = table.Column<DateTime>(type: "datetime2", nullable: false),
                    estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    external_event_id = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    confirmado_en = table.Column<DateTime>(type: "datetime2", nullable: true),
                    motivo_cancelacion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    notas = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    fecha_actualizacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    recordatorio_enviado_en = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointments", x => x.id_appointment);
                    table.ForeignKey(
                        name: "FK_appointments_clients_id_client",
                        column: x => x.id_client,
                        principalTable: "clients",
                        principalColumn: "id_client",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_service_types_id_service_type",
                        column: x => x.id_service_type,
                        principalTable: "service_types",
                        principalColumn: "id_service_type",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_tenants_id_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_estado",
                table: "appointments",
                column: "estado");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_external_event_id",
                table: "appointments",
                column: "external_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_id_client",
                table: "appointments",
                column: "id_client");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_id_service_type",
                table: "appointments",
                column: "id_service_type");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_id_tenant_fecha_inicio",
                table: "appointments",
                columns: new[] { "id_tenant", "fecha_inicio" });

            migrationBuilder.CreateIndex(
                name: "IX_availability_exceptions_id_tenant_fecha",
                table: "availability_exceptions",
                columns: new[] { "id_tenant", "fecha" });

            migrationBuilder.CreateIndex(
                name: "IX_availability_rules_id_tenant",
                table: "availability_rules",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_connections_id_tenant",
                table: "calendar_connections",
                column: "id_tenant",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_clients_id_tenant_whatsapp",
                table: "clients",
                columns: new[] { "id_tenant", "whatsapp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_types_id_tenant",
                table: "service_types",
                column: "id_tenant");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointments");

            migrationBuilder.DropTable(
                name: "availability_exceptions");

            migrationBuilder.DropTable(
                name: "availability_rules");

            migrationBuilder.DropTable(
                name: "calendar_connections");

            migrationBuilder.DropTable(
                name: "clients");

            migrationBuilder.DropTable(
                name: "service_types");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
