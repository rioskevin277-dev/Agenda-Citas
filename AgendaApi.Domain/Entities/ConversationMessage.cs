using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Mensaje persistido de una conversación (turno user/assistant). Durable: sobrevive a
/// redeploys (a diferencia de la memoria volátil de sesión de <c>ConversationMemoryService</c>)
/// y alimenta el pilar "Conversaciones" del CRM.
/// </summary>
[Table("conversation_messages")]
public class ConversationMessage
{
    [Key]
    [Column("id_conversation_message")]
    public Guid IdConversationMessage { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    [Column("phone_cliente")]
    [StringLength(20)]
    public string PhoneCliente { get; set; } = string.Empty;

    /// <summary>Rol del remitente: user (cliente) | assistant (ADAM).</summary>
    [Column("role")]
    [StringLength(20)]
    public string Role { get; set; } = "user";

    [Column("content")]
    [StringLength(4000)]
    public string Content { get; set; } = string.Empty;

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
}