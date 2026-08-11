using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Registro de recordatorio por etapa para una cita (etapa 1 = antelación larga 24h,
/// etapa 2 = antelación corta 2h). Es la fuente de verdad de dedup, estados de entrega
/// (sent/delivered/failed) y reintentos. Se crea/actualiza de forma perezosa cuando el
/// worker de recordatorios intenta enviar.
/// </summary>
[Table("reminder_logs")]
public class ReminderLog
{
    [Key]
    [Column("id_reminder_log")]
    public Guid IdReminderLog { get; set; } = Guid.NewGuid();

    [Column("id_appointment")]
    public Guid IdAppointment { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    /// <summary>1 = antelación larga (24h), 2 = antelación corta (2h).</summary>
    [Column("etapa")]
    public int Etapa { get; set; }

    /// <summary>Cuándo debería enviarse (FechaInicio - horas de la etapa). Se calcula en el 1er intento.</summary>
    [Column("fecha_programada")]
    public DateTime? FechaProgramada { get; set; }

    [Column("fecha_intento")]
    public DateTime? FechaIntento { get; set; }

    /// <summary>sent | delivered | failed</summary>
    [Column("estado")]
    [StringLength(20)]
    public string Estado { get; set; } = "sent";

    /// <summary>ID del mensaje de Meta (wamid), para correlacionar el callback de entrega.</summary>
    [Column("wamid")]
    [StringLength(100)]
    public string? WamId { get; set; }

    /// <summary>Último motivo de fallo (ej: error del API, "sin template y fuera de ventana").</summary>
    [Column("error")]
    [StringLength(500)]
    public string? Error { get; set; }

    /// <summary>Intentos de envío acumulados (máx por configuración del use case).</summary>
    [Column("reintentos")]
    public int Reintentos { get; set; }

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    // Navigation
    public Appointment? Appointment { get; set; }
}
