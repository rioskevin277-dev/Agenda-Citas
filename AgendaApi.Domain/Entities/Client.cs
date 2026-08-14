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
    /// Número de WhatsApp (formato internacional, ej: 521234567890).
    /// </summary>
    [Column("whatsapp")]
    [StringLength(20)]
    public string WhatsApp { get; set; } = string.Empty;

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
}
