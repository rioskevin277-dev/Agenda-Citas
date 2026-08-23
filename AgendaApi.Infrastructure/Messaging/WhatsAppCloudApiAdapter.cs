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

        // Sin teléfono ni username (solo BSUID) no hay destino válido para Meta: se omite el envío
        // en lugar de mandar el BSUID y recibir 400. El flujo normal pasa `phone ?? username`.
        if (string.IsNullOrWhiteSpace(to))
        {
            _logger.LogWarning("[Messaging] Envío sin destinatario atendible (teléfono ni username): se omite");
            return null;
        }

        var url = $"https://graph.facebook.com/v18.0/{phoneNumberId}/messages";

        // Con BSUID y global usernames el destinatario llega resuelto a teléfono E.164 o username
        // global, y Meta solo acepta el campo "to" para ambos. El BSUID (user_id CC.xxx) NO es un
        // destino de envío válido (Meta responde 400), por eso nunca va aquí.
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["type"] = "text",
            ["text"] = new { preview_url = false, body = message }
        };
        AddRecipientField(payload, to);

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

        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["type"] = "template",
            ["template"] = new
            {
                name = templateName,
                language = new { code = "es" },
                components = new[]
                {
                    new { type = "body", parameters = bodyParameters }
                }
            }
        };
        AddRecipientField(payload, to);

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

    /// <summary>
    /// Añade el campo de destinatario "to" al payload. Meta Cloud API solo lo acepta con un
    /// teléfono E.164 o un username global; el BSUID (user_id CC.xxx) NO es un destino válido
    /// (HTTP 400). Los call-sites envían `phone ?? username`, nunca el user_id.
    /// </summary>
    private static void AddRecipientField(Dictionary<string, object?> payload, string recipient)
    {
        // Meta Cloud API solo acepta "to" (teléfono E.164 o username global). El BSUID (CC.xxx, con
        // punto) NO es un destino válido: se rechaza con HTTP 400 ("Invalid parameter" / "text.body is
        // required"). Por eso el destinatario debe llegar SIEMPRE resuelto a teléfono o username,
        // nunca como user_id. Los call-sites envían `phone ?? username`.
        if (string.IsNullOrWhiteSpace(recipient))
            return;
        payload["to"] = recipient;
    }

    public async Task<string?> SendContactRequestAsync(string recipient, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(recipient))
        {
            _logger.LogWarning("[Messaging] Solicitud de contacto sin destinatario: se omite");
            return null;
        }

        if (!_tenantContext.IsSet)
            throw new InvalidOperationException("TenantContext no está configurado");

        var phoneNumberId = _tenantContext.PhoneNumberId;
        var accessToken = _tenantContext.WhatsAppAccessToken;

        if (string.IsNullOrEmpty(phoneNumberId) || string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("Configuración de WhatsApp incompleta");

        // Sin teléfono ni username (solo BSUID) no hay destino válido para Meta: se omite el envío
        // en lugar de mandar el BSUID y recibir 400. El flujo normal pasa `phone ?? username`.
        if (string.IsNullOrWhiteSpace(recipient))
        {
            _logger.LogWarning("[Messaging] Envío sin destinatario atendible (teléfono ni username): se omite");
            return null;
        }

        var url = $"https://graph.facebook.com/v18.0/{phoneNumberId}/messages";

        // Botón de solicitud de contacto (request_contact_info): pide el teléfono al usuario cuando
        // el webhook vino solo con BSUID. Meta manda la respuesta como type=="contacts".
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["type"] = "interactive",
            ["interactive"] = new
            {
                type = "button",
                header = new { type = "text", text = message },
                body = new { text = "Para enviarte recordatorios por WhatsApp, comparte tu número." },
                action = new
                {
                    buttons = new Dictionary<string, object?>[]
                    {
                        new()
                        {
                            ["type"] = "reply",
                            ["reply"] = new { id = "REQUEST_CONTACT", title = "Comparte tu teléfono" },
                            ["request_contact_info"] = true
                        }
                    }
                }
            }
        };
        AddRecipientField(payload, recipient);

        var jsonContent = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        var response = await _httpClient.PostAsync(url, content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Error WhatsApp contacto: {response.StatusCode} - {responseBody}");

        if (LogGraphErrorIfPresent(responseBody, "WhatsApp contacto", recipient))
            return null;

        return ExtractWamId(responseBody);
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
                    string contactUserId = "";
                    string contactUsername = "";
                    if (value.TryGetProperty("contacts", out var contacts) && contacts.GetArrayLength() > 0)
                    {
                        var contact = contacts[0];
                        if (contact.TryGetProperty("profile", out var profile))
                        {
                            if (profile.TryGetProperty("name", out var nameProp))
                                nombre = nameProp.GetString() ?? "Usuario";
                            if (profile.TryGetProperty("username", out var userProp))
                                contactUsername = userProp.GetString() ?? "";
                        }
                        // BSUID: user_id del contacto (identificador estable, único por negocio-usuario).
                        if (contact.TryGetProperty("user_id", out var uidProp))
                            contactUserId = uidProp.GetString() ?? "";
                        // Teléfono legacy (puede faltar con global usernames).
                        if (contact.TryGetProperty("wa_id", out var waIdProp))
                            contactWaId = waIdProp.GetString() ?? "";
                    }

                    foreach (var message in messages.EnumerateArray())
                    {
                        var dto = ParseSingleMessage(message, phoneNumberId, nombre, contactWaId, contactUserId, contactUsername);
                        if (dto != null)
                        {
                            // Diagnóstico: si un mensaje llega sin remitente identificable (ni BSUID ni
                            // teléfono), la respuesta no podrá entregarse. Se registra el JSON crudo.
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

    private static IncomingMessage? ParseSingleMessage(
        JsonElement message, string phoneNumberId, string nombre, string contactWaId = "",
        string contactUserId = "", string contactUsername = "")
    {
        try
        {
            var externalId = message.TryGetProperty("id", out var eId) ? eId.GetString() : null;

            var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;

            // ── System messages (cambio de número / de user_id de un usuario) ──
            if (type == "system")
            {
                string? previousUserId = null;
                string? newUserId = null;
                string? systemType = null;
                if (message.TryGetProperty("system", out var sys))
                {
                    if (sys.TryGetProperty("type", out var st)) systemType = st.GetString();
                    if (sys.TryGetProperty("user_id", out var nuid)) newUserId = nuid.GetString();
                    if (sys.TryGetProperty("previous_user_id", out var puid)) previousUserId = puid.GetString();
                }
                return new IncomingMessage
                {
                    ExternalMessageId = externalId ?? "",
                    From = newUserId ?? previousUserId ?? contactUserId ?? contactWaId ?? "",
                    UserId = newUserId,
                    PreviousUserId = previousUserId,
                    SystemType = systemType,
                    PhoneNumberId = phoneNumberId,
                    Type = "system",
                    Content = "",
                    FromName = nombre
                };
            }

            // ── Mensaje de contacto compartido (respuesta al botón request_contact_info) ──
            if (type == "contacts")
            {
                string? sharedPhone = null;
                bool fromContactRequest = false;
                // El teléfono compartido llega en messages[].contacts[].phones[].phone.
                if (message.TryGetProperty("contacts", out var mContacts) && mContacts.GetArrayLength() > 0)
                {
                    var first = mContacts[0];
                    if (first.TryGetProperty("phones", out var phones) && phones.GetArrayLength() > 0
                        && phones[0].TryGetProperty("phone", out var phoneProp))
                        sharedPhone = phoneProp.GetString();
                }
                // Solo se persiste si vino de un botón request_contact_info (no un contacto reenviado).
                if (message.TryGetProperty("origin", out var origin) && origin.GetString() == "contact_request")
                    fromContactRequest = true;

                return new IncomingMessage
                {
                    ExternalMessageId = externalId ?? "",
                    From = contactUserId ?? contactWaId ?? "",
                    UserId = contactUserId,
                    Phone = fromContactRequest ? sharedPhone : null,
                    PhoneNumberId = phoneNumberId,
                    Type = fromContactRequest ? "contacts" : "unknown",
                    Content = sharedPhone ?? "",
                    FromName = nombre
                };
            }

            // ── Mensajes normales (text/imagen/audio/...) ──
            // "from"/"wa_id" es el teléfono E.164 del remitente. Puede faltar con global usernames:
            // se respalda con contacts[0].wa_id; el identificador CANÓNICO (From) es el BSUID si vino.
            var phone = message.TryGetProperty("from", out var f) ? f.GetString() : null;
            if (string.IsNullOrWhiteSpace(phone))
            {
                phone = !string.IsNullOrWhiteSpace(contactWaId)
                    ? contactWaId
                    : (message.TryGetProperty("wa_id", out var w) ? w.GetString() : null);
            }
            phone ??= "";
            // user_id del propio mensaje (fallback de Meta): precedence => from_user_id > contacts[].user_id.
            var messageUserId = message.TryGetProperty("from_user_id", out var fuid) && !string.IsNullOrWhiteSpace(fuid.GetString())
                ? fuid.GetString()
                : contactUserId;
            string? mediaId = null;
            string? mediaType = null;

            string content = type switch
            {
                "text" => message.TryGetProperty("text", out var txt) && txt.TryGetProperty("body", out var b)
                            ? b.GetString() ?? ""
                            : "",
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

            var canonicalFrom = !string.IsNullOrWhiteSpace(messageUserId) ? messageUserId! : phone!;
            return new IncomingMessage
            {
                ExternalMessageId = externalId ?? "",
                From = canonicalFrom,
                UserId = string.IsNullOrWhiteSpace(messageUserId) ? null : messageUserId,
                Phone = string.IsNullOrWhiteSpace(phone) ? null : phone!,
                Username = string.IsNullOrWhiteSpace(contactUsername) ? null : contactUsername,
                PhoneNumberId = phoneNumberId,
                Type = type ?? "",
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
