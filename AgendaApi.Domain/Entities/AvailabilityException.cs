using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Excepción puntual a la disponibilidad recurrente (feriados, días cerrado, horario especial).
/// </summary>
[Table("availability_exceptions")]
public class AvailabilityException
{
    [Key]
    [Column("id_availability_exception")]
    public Guid IdAvailabilityException { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    /// <summary>
    /// Profesional al que aplica la excepción. NULL = del negocio (afecta a todos).
    /// </summary>
    [Column("id_professional")]
    public Guid? IdProfessional { get; set; }

    /// <summary>
    /// Fecha específica de la excepción.
    /// </summary>
    [Column("fecha")]
    public DateTime Fecha { get; set; }

    /// <summary>
    /// true = día completamente cerrado, false = horario especial (usar hora_inicio/hora_fin).
    /// </summary>
    [Column("dia_completo")]
    public bool DiaCompleto { get; set; }

    /// <summary>
    /// Hora de inicio si no es día completo.
    /// </summary>
    [Column("hora_inicio")]
    public TimeSpan? HoraInicio { get; set; }

    /// <summary>
    /// Hora de fin si no es día completo.
    /// </summary>
    [Column("hora_fin")]
    public TimeSpan? HoraFin { get; set; }

    [Column("motivo")]
    [StringLength(200)]
    public string? Motivo { get; set; }

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;
}
