using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Profesional/empleado que realiza servicios agendados en un tenant
/// (ej: "Dra. María", "Dr. Carlos"). Puede tener su propio horario
/// (availability_rules con id_professional) y su propia cartera de servicios
/// (professional_services). NULL en availability = reglas del negocio.
/// </summary>
[Table("professionals")]
public class Professional
{
    [Key]
    [Column("id_professional")]
    public Guid IdProfessional { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    [Column("nombre")]
    [StringLength(150)]
    public string Nombre { get; set; } = string.Empty;

    [Column("email")]
    [StringLength(150)]
    public string? Email { get; set; }

    [Column("telefono")]
    [StringLength(30)]
    public string? Telefono { get; set; }

    [Column("activo")]
    public bool Activo { get; set; } = true;

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;

    public ICollection<ProfessionalService> Services { get; set; } = new List<ProfessionalService>();
    public ICollection<Appointment> Appointments { get; set; } = new List<Appointment>();
}