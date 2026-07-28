using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AgendaApi.Domain.Entities;

/// <summary>
/// Credenciales OAuth cifradas del tenant para su proveedor de calendario.
/// Cada tenant tiene solo una conexión activa a su proveedor elegido.
/// </summary>
[Table("calendar_connections")]
public class CalendarConnection
{
    [Key]
    [Column("id_calendar_connection")]
    public Guid IdCalendarConnection { get; set; }

    [Column("id_tenant")]
    public Guid IdTenant { get; set; }

    /// <summary>
    /// Email de la cuenta de calendario asociada.
    /// </summary>
    [Column("account_email")]
    [StringLength(200)]
    public string? AccountEmail { get; set; }

    /// <summary>
    /// Token de acceso OAuth (cifrado en reposo).
    /// </summary>
    [Column("access_token_encrypted")]
    public string AccessTokenEncrypted { get; set; } = string.Empty;

    /// <summary>
    /// Token de refresco OAuth (cifrado en reposo).
    /// </summary>
    [Column("refresh_token_encrypted")]
    public string RefreshTokenEncrypted { get; set; } = string.Empty;

    /// <summary>
    /// Fecha de expiración del access token.
    /// </summary>
    [Column("token_expires_at")]
    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>
    /// Identificador del calendario por defecto (para Google: "primary" o un ID; para MS: calendario ID).
    /// </summary>
    [Column("calendar_id")]
    [StringLength(200)]
    public string? CalendarId { get; set; }

    /// <summary>
    /// Google resource ID o identificador de recurso para el webhook.
    /// </summary>
    [Column("sync_resource_id")]
    [StringLength(500)]
    public string? SyncResourceId { get; set; }

    /// <summary>
    /// Webhook/sync channel ID para recibir notificaciones de cambios externos.
    /// </summary>
    [Column("sync_channel_id")]
    [StringLength(200)]
    public string? SyncChannelId { get; set; }

    /// <summary>
    /// Fecha de expiración del canal de sync.
    /// </summary>
    [Column("sync_channel_expires_at")]
    public DateTime? SyncChannelExpiresAt { get; set; }

    /// <summary>
    /// Token de sincronización incremental (Google syncToken o MS deltaToken).
    /// Se actualiza después de cada polling para traer solo cambios desde la última vez.
    /// </summary>
    [Column("sync_token")]
    [StringLength(2000)]
    public string? SyncToken { get; set; }

    [Column("activo")]
    public bool Activo { get; set; } = true;

    [Column("fecha_creacion")]
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    [Column("fecha_actualizacion")]
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;

    // Navigation
    [ForeignKey(nameof(IdTenant))]
    public Tenant Tenant { get; set; } = null!;
}
