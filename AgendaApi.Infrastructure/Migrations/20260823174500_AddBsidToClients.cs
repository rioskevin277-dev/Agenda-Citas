using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBsidToClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // BSUID: identificador canónico del usuario de WhatsApp Cloud API con global usernames
            // (user_id, formato "CC.<alfanumérico>"). Estable y único por par negocio-usuario.
            migrationBuilder.AddColumn<string>(
                name: "user_id",
                table: "clients",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // Username global de WhatsApp del perfil del usuario (opcional).
            migrationBuilder.AddColumn<string>(
                name: "username",
                table: "clients",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            // La clave de conversación / phone_cliente ahora guarda la identidad CANÓNICA del usuario,
            // que con global usernames puede ser un BSUID de hasta ~128 caracteres (antes era solo
            // teléfono E.164, 20 chars). Se ensancha la columna para que un BSUID no desborde.
            migrationBuilder.AlterColumn<string>(
                name: "phone_cliente",
                table: "conversation_messages",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            // El índice único (id_tenant, whatsapp) pasa a filtrado: excluye el "whatsapp" vacío de
            // los clientes BSUID-only (sin teléfono). Sin el filtro, el segundo cliente sin número
            // chocaría con (id_tenant, whatsapp = '').
            migrationBuilder.DropIndex(
                name: "IX_clients_id_tenant_whatsapp",
                table: "clients");

            migrationBuilder.CreateIndex(
                name: "IX_clients_id_tenant_whatsapp",
                table: "clients",
                columns: new[] { "id_tenant", "whatsapp" },
                unique: true,
                filter: "[whatsapp] <> ''");

            // Índice único filtrado del BSUID: permite varias filas sin user_id (clientes legacy que
            // solo tienen teléfono), pero garantiza un único BSUID por tenant cuando existe.
            migrationBuilder.CreateIndex(
                name: "IX_clients_id_tenant_user_id",
                table: "clients",
                columns: new[] { "id_tenant", "user_id" },
                unique: true,
                filter: "[user_id] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_clients_id_tenant_user_id",
                table: "clients");

            migrationBuilder.DropIndex(
                name: "IX_clients_id_tenant_whatsapp",
                table: "clients");

            migrationBuilder.CreateIndex(
                name: "IX_clients_id_tenant_whatsapp",
                table: "clients",
                columns: new[] { "id_tenant", "whatsapp" },
                unique: true);

            migrationBuilder.AlterColumn<string>(
                name: "phone_cliente",
                table: "conversation_messages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.DropColumn(
                name: "username",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "clients");
        }
    }
}