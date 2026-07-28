using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Tipo de servicio / cita que un tenant ofrece (ej: "Corte de pelo - 30min", "Consulta - 1h").
/// </summary>
[Table("service_types")]
public class ServiceType
{
    [Key]
    [Column("id_service_type")]
    public Guid IdServiceType { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    [Column("nombre")]
    [StringLength(150)]
    public string Nombre { get; set; } = string.Empty;

    [Column("descripcion")]
    [StringLength(500)]
    public string? Descripcion { get; set; }

    /// <summary>
    /// Duración en minutos del servicio.
    /// </summary>
    [Column("duracion_minutos")]
    public int DuracionMinutos { get; set; }

    /// <summary>
    /// Minutos de buffer entre citas (para limpieza, preparación, etc.)
    /// </summary>
    [Column("buffer_minutos")]
    public int BufferMinutos { get; set; } = 0;

    /// <summary>
    /// Precio del servicio (opcional, para referencia).
    /// </summary>
    [Column("precio")]
    public decimal? Precio { get; set; }

    [Column("activo")]
    public bool Activo { get; set; } = true;

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;
}
