using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Regla de disponibilidad recurrente del tenant.
/// Ej: "Lunes a Viernes de 9:00 a 18:00", "Sábados de 10:00 a 14:00".
/// </summary>
[Table("availability_rules")]
public class AvailabilityRule
{
    [Key]
    [Column("id_availability_rule")]
    public Guid IdAvailabilityRule { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    /// <summary>
    /// Día de la semana (1=Lunes ... 7=Domingo).
    /// </summary>
    [Column("dia_semana")]
    public int DiaSemana { get; set; }

    /// <summary>
    /// Hora de inicio (HH:mm format).
    /// </summary>
    [Column("hora_inicio")]
    public TimeSpan HoraInicio { get; set; }

    /// <summary>
    /// Hora de fin (HH:mm format).
    /// </summary>
    [Column("hora_fin")]
    public TimeSpan HoraFin { get; set; }

    [Column("activo")]
    public bool Activo { get; set; } = true;

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;
}
