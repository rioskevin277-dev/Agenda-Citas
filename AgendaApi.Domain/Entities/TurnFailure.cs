using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Registro durable del fallo de un turno del bot: la causa por la que el cliente recibió el
/// genérico "Lo siento, tuve un problema" (timeout del turno o todos los proveedores de IA
/// fallaron). Permite diagnosticar la causa en producción vía dashboard sin revisar logs.
/// </summary>
[Table("turn_failures")]
public class TurnFailure
{
    [Key]
    [Column("id_turn_failure")]
    public Guid IdTurnFailure { get; set; }

    /// <summary>Tenant dueño del turno (siempre conocido en el punto de emisión).</summary>
    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    /// <summary>Identidad del cliente remitente (misma convención que conversation_messages.phone_cliente).</summary>
    [Column("phone_cliente")]
    [StringLength(200)]
    public string PhoneCliente { get; set; } = string.Empty;

    /// <summary>
    /// Clasificación corta del fallo: "timeout" | "all_providers_failed" | "stale_availability".
    /// </summary>
    /// <remarks>
    /// - <c>timeout</c>: el turno venció antes de obtener respuesta de la IA.
    /// - <c>all_providers_failed</c>: toda la cadena de proveedores de IA falló.
    /// - <c>stale_availability</c>: el cupo prometido se liberó y fue re-ocupado (o quedó
    ///   indisponible) entre el re-check de disponibilidad y la materialización de la cita;
    ///   el cliente derivó a lista de espera.
    /// </remarks>
    [Column("motivo")]
    [StringLength(50)]
    public string Motivo { get; set; } = string.Empty;

    /// <summary>
    /// Detalle diagnóstico: proveedores probados, intentos, ms transcurridos y último error de cada uno.
    /// </summary>
    [Column("detalle")]
    [StringLength(2000)]
    public string Detalle { get; set; } = string.Empty;

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
}
