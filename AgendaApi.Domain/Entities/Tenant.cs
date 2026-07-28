using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Cliente/negocio que usa la plataforma de agendamiento (multi-tenant).
/// </summary>
[Table("tenants")]
public class Tenant
{
    [Key]
    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    [Column("nombre")]
    [StringLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [Column("nombre_comercial")]
    [StringLength(200)]
    public string? NombreComercial { get; set; }

    [Column("correo")]
    [StringLength(150)]
    public string? Correo { get; set; }

    [Column("telefono")]
    [StringLength(30)]
    public string? Telefono { get; set; }

    /// <summary>
    /// ID numérico del número de teléfono de WhatsApp Business asignado a este tenant.
    /// Meta envía este ID en el webhook como metadata.phone_number_id para identificar el negocio.
    /// </summary>
    [Column("whatsapp_phone_number_id")]
    [StringLength(50)]
    public string? WhatsAppPhoneNumberId { get; set; }

    [Column("direccion")]
    [StringLength(250)]
    public string? Direccion { get; set; }

    /// <summary>
    /// Proveedor de calendario que este tenant usa: "google" o "microsoft".
    /// Se define en onboarding y no cambia frecuentemente.
    /// </summary>
    [Column("calendar_provider")]
    [StringLength(20)]
    public string CalendarProvider { get; set; } = "google";

    [Column("activo")]
    public bool Activo { get; set; } = true;

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    [Column("fecha_actualizacion")]
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public CalendarConnection? CalendarConnection { get; set; }
    public ICollection<ServiceType> ServiceTypes { get; set; } = new List<ServiceType>();
    public ICollection<AvailabilityRule> AvailabilityRules { get; set; } = new List<AvailabilityRule>();
    public ICollection<Client> Clients { get; set; } = new List<Client>();
    public ICollection<Appointment> Appointments { get; set; } = new List<Appointment>();
}
