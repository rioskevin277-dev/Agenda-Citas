using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public async Task<string?> SendTextAsync(string to, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(to))
        {
            _logger.LogWarning("[Messaging] Envío sin destinatario: se omite");
            return null;
        }

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

        // Meta reporta los fallos de entrega REALES (131047 Re-engagement fuera de la ventana de
        // 24h, 131009 número incorrecto, ...) con HTTP 200 y un objeto "error" en el body. Sin
        // esto, el envío fallaría en silencio: el asistente creería que respondió y el usuario
        // no recibiría nada sin rastro en logs.
        if (LogGraphErrorIfPresent(responseBody, "WhatsApp", to))
            return null;

        return ExtractWamId(responseBody);
    }

    public async Task<string?> SendTemplateAsync(string to, string templateName, Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (!_tenantContext.IsSet)
            throw new InvalidOperationException("TenantContext no está configurado");

        var phoneNumberId = _tenantContext.PhoneNumberId;
        var accessToken = _tenantContext.WhatsAppAccessToken;

        var url = $"https://graph.facebook.com/v18.0/{phoneNumberId}/messages";

        // El body de un template recibe los parámetros en UN componente de tipo "body",
        // como un array de parámetros en orden ({{1}}, {{2}}, ...). Antes se generaba un
        // componente por parámetro, que Meta rechaza con >1 body param.
        var bodyParameters = parameters.Values
            .Select(value => (object)new { type = "text", text = value })
            .ToArray();

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
                components = new[]
                {
                    new { type = "body", parameters = bodyParameters }
                }
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

        // Fallo de entrega real reportado como 200 + error (p. ej. template inexistente/no aprobado
        // o fuera de ventana): se registra y no se entrega.
        if (LogGraphErrorIfPresent(responseBody, "WhatsApp template", to))
            return null;

        return ExtractWamId(responseBody);
    }

    /// <summary>
    /// Si un body de respuesta de Meta (aun con HTTP 200) trae un objeto "error", lo registra y
    /// devuelve true (el mensaje NO fue entregado). Sin esto, fallos reales como 131047
    /// (Re-engagement: texto libre fuera de la ventana de 24h) o 131009 (número incorrecto)
    /// quedarían "en silencio": el asistente creería que respondió y el usuario no recibiría nada.
    /// </summary>
    private bool LogGraphErrorIfPresent(string responseBody, string channel, string to)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("error", out var err))
                return false;

            var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            var subCode = err.TryGetProperty("error_subcode", out var sc) ? sc.GetInt32() : 0;
            var title = err.TryGetProperty("title", out var t) ? t.GetString() : "";
            var message = err.TryGetProperty("message", out var m) ? m.GetString() : "";
            var detail = string.IsNullOrWhiteSpace(title) ? "" : $" {title}";

            _logger.LogError(
                "[{Channel}] Mensaje NO entregado a {To}: code {Code} (sub {SubCode}){Detail}. {Message}",
                channel, to, code, subCode, detail, message);

            return true;
        }
        catch (JsonException)
        {
            // Body inesperado: no debe romper el envío.
            return false;
        }
    }

    /// <summary>
    /// Extrae el ID del mensaje (wamid) de la respuesta de Meta: {"messages":[{"id":"..."}]}.
    /// Devuelve null si el payload no lo trae (no debe lanzar).
    /// </summary>
    private static string? ExtractWamId(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch (JsonException)
        {
            // Payload inesperado: no bloquea el envío, solo no hay wamid para correlacionar.
        }
        return null;
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
                    string contactWaId = "";
                    if (value.TryGetProperty("contacts", out var contacts) && contacts.GetArrayLength() > 0)
                    {
                        var contact = contacts[0];
                        if (contact.TryGetProperty("profile", out var profile) && profile.TryGetProperty("name", out var nameProp))
                            nombre = nameProp.GetString() ?? "Usuario";
                        if (contact.TryGetProperty("wa_id", out var waIdProp))
                            contactWaId = waIdProp.GetString() ?? "";
                    }

                    foreach (var message in messages.EnumerateArray())
                    {
                        var dto = ParseSingleMessage(message, phoneNumberId, nombre, contactWaId);
                        if (dto != null)
                        {
                            // Diagnóstico: si un mensaje llega sin remitente identificable, la
                            // respuesta no podrá entregarse. Se registra el JSON crudo del mensaje
                            // para ver qué manda Meta en ese caso.
                            if (string.IsNullOrWhiteSpace(dto.From))
                                _logger.LogWarning("[WhatsAppAdapter] Mensaje sin remitente identificable. message={MsgJson}",
                                    message.ToString());
                            result.Add(dto);
                        }
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

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        // Paso 1: nodo de media → { url, mime_type, ... }. Reutiliza el mismo _httpClient
        // (no "new HttpClient()", que fuga sockets y no respeta la configuración del client).
        var mediaUrl = $"https://graph.facebook.com/v18.0/{mediaId}";
        var mediaResponse = await _httpClient.GetAsync(mediaUrl, ct);
        mediaResponse.EnsureSuccessStatusCode();
        var mediaJson = await mediaResponse.Content.ReadAsStringAsync(ct);
        var mediaInfo = JsonSerializer.Deserialize<MediaInfo>(mediaJson);

        if (string.IsNullOrWhiteSpace(mediaInfo?.Url))
            throw new InvalidOperationException($"No se pudo obtener URL del media {mediaId}");

        // Paso 2: descargar los bytes desde la URL firmada del lookaside.
        return await _httpClient.GetByteArrayAsync(mediaInfo.Url, ct);
    }

    private static IncomingMessage? ParseSingleMessage(JsonElement message, string phoneNumberId, string nombre, string contactWaId = "")
    {
        try
        {
            var externalId = message.GetProperty("id").GetString();
            // "from" es el teléfono E.164 del remitente (WhatsApp). Aunque Meta siempre debería
            // traerlo, se respalda sin restricciones con contacts[0].wa_id (que ES el número real
            // del remitente) y, como último recurso, con message.wa_id. El gate IsE164Phone que
            // había antes podía dejar el remitente vacío (y soltar la respuesta) en mensajes cuyo
            // contactWaId viniera con formato no estrictamente de dígitos.
            var from = message.TryGetProperty("from", out var f)
                ? f.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(from))
            {
                from = !string.IsNullOrWhiteSpace(contactWaId)
                    ? contactWaId
                    : (message.TryGetProperty("wa_id", out var w) ? w.GetString() : null);
            }
            from ??= "";
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

    /// <summary>
    /// La API de metadatos de media de Graph devuelve snake_case. La deserialización
    /// por defecto de System.Text.Json es case-sensitive, así que los rutores deben
    /// declarar el JSON name explícito (url ≠ Url): sin esto, Url quedaría null y la
    /// descarga fallaría con "No se pudo obtener URL del media".
    /// </summary>
    private class MediaInfo
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("mime_type")]
        public string? MimeType { get; set; }

        [JsonPropertyName("file_size")]
        public long? FileSize { get; set; }
    }
}
