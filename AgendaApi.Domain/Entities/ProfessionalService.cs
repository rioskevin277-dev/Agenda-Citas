using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Cartera de servicios que un profesional puede realizar (relación muchos-a-muchos
/// Professional ↔ ServiceType). Un profesional NO puede agendar un servicio que
/// no figure aquí.
/// </summary>
[Table("professional_services")]
public class ProfessionalService
{
    [Column("id_professional")]
    public Guid IdProfessional { get; set; }

    [Column("id_service_type")]
    public Guid IdServiceType { get; set; }

    [Column("activo")]
    public bool Activo { get; set; } = true;

    // Navigation
    [ForeignKey(nameof(IdProfessional))]
    public Professional Professional { get; set; } = null!;

    [ForeignKey(nameof(IdServiceType))]
    public ServiceType ServiceType { get; set; } = null!;
}