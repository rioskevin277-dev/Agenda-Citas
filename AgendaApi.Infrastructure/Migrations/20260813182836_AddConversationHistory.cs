using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgendaApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_messages",
                columns: table => new
                {
                    id_conversation_message = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id_tenant = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    phone_cliente = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "user"),
                    content = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    fecha_creacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_messages", x => x.id_conversation_message);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_id_tenant_phone_cliente_fecha_creacion",
                table: "conversation_messages",
                columns: new[] { "id_tenant", "phone_cliente", "fecha_creacion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_messages");
        }
    }
}
