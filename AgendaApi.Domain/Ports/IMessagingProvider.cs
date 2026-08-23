namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para el proveedor de mensajería (WhatsApp Cloud API, etc.).
/// </summary>
public interface IMessagingProvider
{
    /// <summary>
    /// Envía un mensaje de texto al destinatario.
    /// Devuelve el ID del mensaje de Meta (wamid) si el API lo aceptó, o null.
    /// </summary>
    Task<string?> SendTextAsync(string to, string message, CancellationToken ct = default);

    /// <summary>
    /// Envía una plantilla de mensaje (para notificaciones / recordatorios).
    /// Devuelve el ID del mensaje de Meta (wamid) si el API lo aceptó, o null.
    /// </summary>
    Task<string?> SendTemplateAsync(string to, string templateName, Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>
    /// Verifica el webhook de Meta (challenge response).
    /// </summary>
    Task<string?> VerifyWebhookAsync(string mode, string token, string challenge);

    /// <summary>
    /// Envía un mensaje interactivo con botón de solicitud de contacto (request_contact_info) para
    /// recuperar el teléfono del usuario cuando el webhook vino sin él (solo BSUID).
    /// </summary>
    Task<string?> SendContactRequestAsync(string recipient, string message, CancellationToken ct = default);

    /// <summary>
    /// Procesa el payload entrante del webhook y devuelve los mensajes parseados.
    /// </summary>
    Task<List<IncomingMessage>> ParseWebhookPayloadAsync(object body);

    /// <summary>
    /// Descarga un media (audio, imagen) desde WhatsApp.
    /// </summary>
    Task<byte[]> DownloadMediaAsync(string mediaId, CancellationToken ct = default);
}

/// <summary>
/// Mensaje entrante parseado desde WhatsApp.
/// </summary>
public class IncomingMessage
{
    /// <summary>Identidad canónica del remitente (BSUID si hay, si no teléfono). Usada como clave de conversación.</summary>
    public string From { get; set; } = string.Empty;
    /// <summary>BSUID (user_id de Meta), si viene. Identificador estable cuando no hay teléfono.</summary>
    public string? UserId { get; set; }
    /// <summary>Teléfono E.164 del remitente, si viene (opcional con global usernames).</summary>
    public string? Phone { get; set; }
    /// <summary>Username global de WhatsApp del perfil, si viene.</summary>
    public string? Username { get; set; }
    /// <summary>Para mensajes system: subtipo (user_changed_number / user_changed_user_id).</summary>
    public string? SystemType { get; set; }
    /// <summary>Para mensajes system: BSUID anterior del usuario.</summary>
    public string? PreviousUserId { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string PhoneNumberId { get; set; } = string.Empty;
    public string ExternalMessageId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? MediaId { get; set; }
    public string? MediaType { get; set; }
    public string? MediaUrl { get; set; }
    public Guid TenantId { get; set; }
}
