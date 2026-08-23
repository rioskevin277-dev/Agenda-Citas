using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Contacto de WhatsApp que agenda citas, asociado a un tenant.
/// </summary>
[Table("clients")]
public class Client
{
    [Key]
    [Column("id_client")]
    public Guid IdClient { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    /// <summary>
    /// Número de WhatsApp (formato internacional, ej: 521234567890). Opcional desde que WhatsApp
    /// introduce BSUID: con global usernames el teléfono puede no venir en el webhook.
    /// </summary>
    [Column("whatsapp")]
    [StringLength(20)]
    public string WhatsApp { get; set; } = string.Empty;

    /// <summary>
    /// BSUID = Business-Scoped User ID (formato CC.&lt;hasta 128 alfanum&gt;), autogenerado y único por
    /// par negocio-usuario. Identificador estable cuando el teléfono no viene en el webhook.
    /// </summary>
    [Column("user_id")]
    [StringLength(200)]
    public string? UserId { get; set; }

    /// <summary>
    /// Username global de WhatsApp (el @username), opcional, presente en el perfil del webhook.
    /// </summary>
    [Column("username")]
    [StringLength(150)]
    public string? Username { get; set; }

    [Column("nombre")]
    [StringLength(150)]
    public string? Nombre { get; set; }

    [Column("email")]
    [StringLength(150)]
    public string? Email { get; set; }

    [Column("notas")]
    [StringLength(500)]
    public string? Notas { get; set; }

    [Column("activo")]
    public bool Activo { get; set; } = true;

    [Column("estado")]
    [StringLength(20)]
    public string Estado { get; set; } = "nuevo"; // nuevo, frecuente, inactivo, no_show

    [Column("tags")]
    [StringLength(500)]
    public string? Tags { get; set; } // JSON o lista separada por comas

    [Column("ultima_interaccion")]
    public DateTime? UltimaInteraccion { get; set; }

    [Column("proxima_cita")]
    public DateTime? ProximaCita { get; set; }

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    [Column("fecha_actualizacion")]
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;
    public ICollection<Appointment> Appointments { get; set; } = new List<Appointment>();

    /// <summary>
    /// Identidad canónica del cliente para claves de conversación y lookups: BSUID si lo hay,
    /// si no el teléfono. El resto del sistema usa esto como identificador estable de la persona.
    /// </summary>
    public string PartnerId => !string.IsNullOrEmpty(UserId) ? UserId : WhatsApp;

    /// <summary>
    /// Destino de envío de WhatsApp: el teléfono si se conoce (priouridad, campo `to`), si no el
    /// username global (también campo `to`). NUNCA el BSUID (user_id): Meta Cloud API rechaza el
    /// `recipient` con BSUID, y el BSUID solo sirve para identificar en los webhooks, no para enviar.
    /// </summary>
    public string PartnerDestination => !string.IsNullOrEmpty(WhatsApp) ? WhatsApp : (Username ?? "");
}
