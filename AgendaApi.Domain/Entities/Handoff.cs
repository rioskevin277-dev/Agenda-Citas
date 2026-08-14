using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>Estado del handoff de una conversación escalada a un humano.</summary>
public enum HandoffState
{
    /// <summary>Flujo normal: el AI atiende la conversación.</summary>
    Normal = 0,

    /// <summary>El cliente fue escalado; el asesor aún no lo tomó.</summary>
    HumanPending = 1,

    /// <summary>El asesor está atendiendo activamente (el AI queda congelado).</summary>
    HumanActive = 2,

    /// <summary>Handoff cerrado: el control volvió al AI.</summary>
    AiResumed = 3
}

/// <summary>
/// Ticket de escalado a asesor humano por conversación (tenant + teléfono del cliente).
/// Es la cola del asesor y el registro de auditoría del handoff. Una conversación puede
/// tener varios tickets a lo largo del tiempo (uno por escalado); un ticket con estado
/// HumanPending/HumanActive indica que el handoff está activo y el AI congelado.
/// Durable en BD (a diferencia del estado en memoria), así la cola y la auditoría
/// sobreviven a un redeploy.
/// </summary>
[Table("handoffs")]
public class Handoff
{
    [Key]
    [Column("id_handoff")]
    public Guid IdHandoff { get; set; } = Guid.NewGuid();

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    /// <summary>Teléfono del cliente (solo dígitos).</summary>
    [Column("phone_cliente")]
    [StringLength(50)]
    public string PhoneCliente { get; set; } = string.Empty;

    /// <summary>Motivo declarado por el AI para escalar.</summary>
    [Column("motivo")]
    [StringLength(1000)]
    public string? Motivo { get; set; }

    /// <summary>Contexto estructurado (acciones del AI en el turno, una por línea).</summary>
    [Column("contexto")]
    public string? Contexto { get; set; }

    /// <summary>Estado del handoff (HumanPending | HumanActive | AiResumed).</summary>
    [Column("estado")]
    public HandoffState Estado { get; set; } = HandoffState.HumanPending;

    /// <summary>Última respuesta del asesor reenviada al cliente (auditoría).</summary>
    [Column("ultimo_mensaje_humano")]
    public string? UltimoMensajeHumano { get; set; }

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    [Column("fecha_actualizacion")]
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;
}
