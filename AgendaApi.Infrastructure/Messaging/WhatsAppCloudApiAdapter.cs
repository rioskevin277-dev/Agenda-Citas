using System.Net.Http;
using System.Text.Json;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Messaging;

/// <summary>
/// Adaptador de WhatsApp Cloud API.
/// Reutiliza el mismo patrón de AdamApi: parseo de webhooks de Meta, envío de mensajes, descarga de media.
/// Pendiente de implementar: MessageBufferService con buffer de 30s y tool-calling loop.
/// </summary>
public class WhatsAppCloudApiAdapter : IMessagingProvider
{
    private readonly HttpClient _httpClient;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<WhatsAppCloudApiAdapter> _logger;

    public WhatsAppCloudApiAdapter(
        IHttpClientFactory httpClientFactory,
        ITenantContext tenantContext,
        ILogger<WhatsAppCloudApiAdapter> logger)
    {
        _httpClient = httpClientFactory.CreateClient("whatsapp-api");
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task SendTextAsync(string to, string message, CancellationToken ct = default)
    {
        if (!_tenantContext.IsSet)
            throw new InvalidOperationException("TenantContext no está configurado");

        var phoneNumberId = _tenantContext.PhoneNumberId;
        var accessToken = _tenantContext.WhatsAppAccessToken;

        if (string.IsNullOrEmpty(phoneNumberId) || string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("Configuración de WhatsApp incompleta");

        var url = $"https://graph.facebook.com/v18.0/{phoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = to,
            type = "text",
            text = new { preview_url = false, body = message }
        };

        var jsonContent = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        var response = await _httpClient.PostAsync(url, content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Error WhatsApp: {response.StatusCode} - {responseBody}");
    }

    public async Task SendTemplateAsync(string to, string templateName, Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (!_tenantContext.IsSet)
            throw new InvalidOperationException("TenantContext no está configurado");

        var phoneNumberId = _tenantContext.PhoneNumberId;
        var accessToken = _tenantContext.WhatsAppAccessToken;

        var url = $"https://graph.facebook.com/v18.0/{phoneNumberId}/messages";

        var components = parameters.Select((kvp, i) => new
        {
            type = "body",
            parameters = new[]
            {
                new { type = "text", text = kvp.Value }
            }
        }).ToArray();

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = to,
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = "es" },
                components = components
            }
        };

        var jsonContent = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        var response = await _httpClient.PostAsync(url, content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Error WhatsApp template: {response.StatusCode} - {responseBody}");
    }

    public Task<string?> VerifyWebhookAsync(string mode, string token, string challenge)
    {
        var verifyToken = Environment.GetEnvironmentVariable("WhatsApp__VerifyToken")
                       ?? Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN")
                       ?? "agenda_api_prod_2024";

        if (mode == "subscribe" && token == verifyToken)
            return Task.FromResult<string?>(challenge);

        return Task.FromResult<string?>(null);
    }

    public Task<List<IncomingMessage>> ParseWebhookPayloadAsync(object body)
    {
        var result = new List<IncomingMessage>();

        try
        {
            var json = JsonSerializer.Serialize(body);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("entry", out var entryArray))
                return Task.FromResult(result);

            foreach (var entry in entryArray.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changesArray))
                    continue;

                foreach (var change in changesArray.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value))
                        continue;
                    if (!value.TryGetProperty("metadata", out var metadata))
                        continue;
                    if (!value.TryGetProperty("messages", out var messages))
                        continue;

                    var phoneNumberId = metadata.GetProperty("phone_number_id").GetString() ?? "";

                    string nombre = "Usuario";
                    if (value.TryGetProperty("contacts", out var contacts))
                    {
                        nombre = contacts[0].GetProperty("profile").GetProperty("name").GetString() ?? "Usuario";
                    }

                    foreach (var message in messages.EnumerateArray())
                    {
                        var dto = ParseSingleMessage(message, phoneNumberId, nombre);
                        if (dto != null)
                            result.Add(dto);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WhatsAppAdapter] Error parseando webhook: {Message}", ex.Message);
        }

        return Task.FromResult(result);
    }

    public async Task<byte[]> DownloadMediaAsync(string mediaId, CancellationToken ct = default)
    {
        if (!_tenantContext.IsSet)
            throw new InvalidOperationException("TenantContext no está configurado");

        var accessToken = _tenantContext.WhatsAppAccessToken;

        // Get media URL
        var mediaUrl = $"https://graph.facebook.com/v18.0/{mediaId}";
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        var mediaResponse = await _httpClient.GetAsync(mediaUrl, ct);
        mediaResponse.EnsureSuccessStatusCode();
        var mediaJson = await mediaResponse.Content.ReadAsStringAsync(ct);
        var mediaInfo = JsonSerializer.Deserialize<MediaInfo>(mediaJson);

        if (string.IsNullOrWhiteSpace(mediaInfo?.Url))
            throw new InvalidOperationException($"No se pudo obtener URL del media {mediaId}");

        // Download bytes
        using var downloadClient = new HttpClient();
        downloadClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        return await downloadClient.GetByteArrayAsync(mediaInfo.Url, ct);
    }

    private static IncomingMessage? ParseSingleMessage(JsonElement message, string phoneNumberId, string nombre)
    {
        try
        {
            var externalId = message.GetProperty("id").GetString();
            // Preferimos "from" (teléfono E.164 real) cuando Meta lo envía,
            // porque es el destinatario entregable para la respuesta.
            // "from_user_id" (identificador a nivel negocio) solo como fallback,
            // ya que Meta puede no incluir el teléfono en algunos casos.
            var from = message.TryGetProperty("from", out var f) ? f.GetString()
                     : message.TryGetProperty("from_user_id", out var fuid) ? fuid.GetString()
                     : null;
            var type = message.GetProperty("type").GetString();
            string? mediaId = null;
            string? mediaType = null;

            string content = type switch
            {
                "text" => message.GetProperty("text").GetProperty("body").GetString() ?? "",
                "image" => message.TryGetProperty("image", out var img) && img.TryGetProperty("caption", out var cap)
                            ? cap.GetString() ?? "[imagen]"
                            : "[imagen]",
                "audio" => "[audio]",
                "video" => "[video]",
                "document" => "[documento]",
                _ => "[no soportado]"
            };

            // Extraer info de media según tipo (propertyName no puede ser null)
            if (type != null && message.TryGetProperty(type, out var mediaProp))
            {
                if (mediaProp.TryGetProperty("id", out var mid))
                    mediaId = mid.GetString();
                if (mediaProp.TryGetProperty("mime_type", out var mt))
                    mediaType = mt.GetString();
                // Media URL se obtiene por separado vía DownloadMediaAsync
            }

            return new IncomingMessage
            {
                ExternalMessageId = externalId!,
                From = from!,
                PhoneNumberId = phoneNumberId,
                Type = type!,
                Content = content,
                FromName = nombre,
                MediaId = mediaId,
                MediaType = mediaType
            };
        }
        catch
        {
            return null;
        }
    }

    private class MediaInfo
    {
        public string? Url { get; set; }
        public string? MimeType { get; set; }
        public long? FileSize { get; set; }
    }
}
