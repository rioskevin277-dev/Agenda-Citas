using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Cita agendada por un cliente en el calendario de un tenant.
/// </summary>
[Table("appointments")]
public class Appointment
{
    [Key]
    [Column("id_appointment")]
    public Guid IdAppointment { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    [Column("id_client")]
    public Guid IdClient { get; set; }

    [Column("id_service_type")]
    public Guid IdServiceType { get; set; }

    /// <summary>
    /// Fecha y hora de inicio de la cita.
    /// </summary>
    [Column("fecha_inicio")]
    public DateTime FechaInicio { get; set; }

    /// <summary>
    /// Fecha y hora de fin de la cita.
    /// </summary>
    [Column("fecha_fin")]
    public DateTime FechaFin { get; set; }

    /// <summary>
    /// Estado de la cita: pending, confirmed, cancelled, completed, no_show.
    /// </summary>
    [Column("estado")]
    [StringLength(20)]
    public string Estado { get; set; } = "pending";

    /// <summary>
    /// ID del evento en el calendario externo (Google Calendar event ID o MS Graph event ID).
    /// </summary>
    [Column("external_event_id")]
    [StringLength(500)]
    public string? ExternalEventId { get; set; }

    /// <summary>
    /// Fecha de confirmación por parte del cliente.
    /// </summary>
    [Column("confirmado_en")]
    public DateTime? ConfirmadoEn { get; set; }

    /// <summary>
    /// Motivo de cancelación (opcional).
    /// </summary>
    [Column("motivo_cancelacion")]
    [StringLength(500)]
    public string? MotivoCancelacion { get; set; }

    [Column("notas")]
    [StringLength(1000)]
    public string? Notas { get; set; }

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    [Column("fecha_actualizacion")]
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Fecha en que se envió el recordatorio.
    /// </summary>
    [Column("recordatorio_enviado_en")]
    public DateTime? RecordatorioEnviadoEn { get; set; }

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;

    [ForeignKey(nameof(IdClient))]
    public Client Client { get; set; } = null!;

    [ForeignKey(nameof(IdServiceType))]
    public ServiceType ServiceType { get; set; } = null!;
}
