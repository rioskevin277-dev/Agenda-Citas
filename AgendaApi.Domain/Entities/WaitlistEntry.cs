using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Entrada de lista de espera: cliente apuntado a un servicio (opcionalmente por profesional)
/// para ser notificado cuando se libere un cupo. Reutiliza la semántica de disponibilidad del
/// Motor de Reglas para detectar cuándo el cupo vuelve a estar reservable.
/// </summary>
[Table("waitlist_entries")]
public class WaitlistEntry
{
    [Key]
    [Column("id_waitlist_entry")]
    public Guid IdWaitlistEntry { get; set; } = Guid.NewGuid();

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    [Column("id_client")]
    public Guid IdClient { get; set; }

    [Column("id_service_type")]
    public Guid IdServiceType { get; set; }

    /// <summary>Canal específico al que espera el cliente (null = cualquier profesional del servicio).</summary>
    [Column("id_professional")]
    public Guid? IdProfessional { get; set; }

    /// <summary>Preferencia de ventana de fechas del cliente (opcional; null = cualquier cupo futuro).</summary>
    [Column("fecha_desde")]
    public DateTime? FechaDesde { get; set; }

    [Column("fecha_hasta")]
    public DateTime? FechaHasta { get; set; }

    /// <summary>Estado: active, notified (ya se le avisó y espera/sí reserva), expired, fulfilled, removed.</summary>
    [Column("estado")]
    [StringLength(20)]
    public string Estado { get; set; } = "active";

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    [Column("fecha_actualizacion")]
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;

    [ForeignKey(nameof(IdClient))]
    public Client Client { get; set; } = null!;

    [ForeignKey(nameof(IdServiceType))]
    public ServiceType ServiceType { get; set; } = null!;

    [ForeignKey(nameof(IdProfessional))]
    public Professional? Professional { get; set; }
}