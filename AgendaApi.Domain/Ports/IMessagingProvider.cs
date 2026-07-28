namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para el proveedor de mensajería (WhatsApp Cloud API, etc.).
/// </summary>
public interface IMessagingProvider
{
    /// <summary>
    /// Envía un mensaje de texto al destinatario.
    /// </summary>
    Task SendTextAsync(string to, string message, CancellationToken ct = default);

    /// <summary>
    /// Envía una plantilla de mensaje (para notificaciones / recordatorios).
    /// </summary>
    Task SendTemplateAsync(string to, string templateName, Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>
    /// Verifica el webhook de Meta (challenge response).
    /// </summary>
    Task<string?> VerifyWebhookAsync(string mode, string token, string challenge);

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
    public string From { get; set; } = string.Empty;
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
