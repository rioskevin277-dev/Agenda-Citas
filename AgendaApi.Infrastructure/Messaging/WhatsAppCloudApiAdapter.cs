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

        // Un destinatario no-E.164 (id tipo "CO.x"/user_id) es un contacto de Instagram en el
        // inbox unificado de Meta: el webhook de esos DMs llega con source_type:"IG" y SIN
        // teléfono. A esos se responde por la Instagram Messaging API (endpoint /messages de IG,
        // messaging_type RESPONSE, token de IG), NO por el endpoint de WhatsApp — que rechaza el
        // "CO.x" como "número de teléfono incorrecto" (#131009) aunque se mande source_type:"IG",
        // porque valida `to` como número siempre. La ruta IG usa el token de IG (global, de env)
        // y NO necesita el contexto de tenant de WhatsApp.
        if (!IsE164Phone(to))
            return await SendInstagramDirectAsync(to, message, ct);

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

    /// <summary>
    /// Responde un mensaje directo (DM) de Instagram por la Instagram Messaging API.
    ///
    /// Contrato de Meta: POST /{ig-scoped-id}/messages con el id con alcance del DESTINATARIO
    /// tanto en el path como en recipient.id, messaging_type "RESPONSE" (respuesta a un DM entrante)
    /// y el token de usuario de Instagram (permiso de mensajería) como Bearer. Es un endpoint
    /// distinto del de WhatsApp; el token IG también lo es.
    ///
    /// Fail-safe: si no hay token de Instagram configurado se registra un warning y se devuelve
    /// null (no rompe el turno, pero el DM de IG queda sin respuesta hasta que el dueño conecte
    /// la cuenta business/creator + permiso de mensajería de IG en Meta).
    /// </summary>
    private async Task<string?> SendInstagramDirectAsync(string igScopedId, string message, CancellationToken ct)
    {
        // Token de IG (usuario del sistema, permiso de mensajería) + id de la cuenta IG business
        // (emisora de los DMs). Ambos son globales, de env.
        var accessToken = Environment.GetEnvironmentVariable("Instagram__AccessToken")
                       ?? Environment.GetEnvironmentVariable("INSTAGRAM_ACCESS_TOKEN");
        var businessAccountId = Environment.GetEnvironmentVariable("Instagram__BusinessAccountId")
                             ?? Environment.GetEnvironmentVariable("INSTAGRAM_BUSINESS_ACCOUNT_ID");

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(businessAccountId))
        {
            _logger.LogWarning(
                "[Instagram] Falta Instagram__AccessToken o Instagram__BusinessAccountId: no se responde al DM de IG {Id} " +
                "(token de usuario del sistema con permiso de mensajería + id de la cuenta IG business requeridos)",
                igScopedId);
            return null;
        }

        // El webhook del inbox unificado identifica al remitente con un id virtualizado "CO.xxxx".
        // El endpoint de mensajería de IG espera SOLO el id numérico real del destinatario (sin el prefijo)
        // como recipient.id, y la cuenta IG business (emisora) como objeto del path. Meta rechaza "CO.x"
        // como objeto (error 100 subcode 33 "does not exist").
        var recipientId = igScopedId.StartsWith("CO.", StringComparison.OrdinalIgnoreCase)
            ? igScopedId.Substring("CO.".Length)
            : igScopedId;

        var apiVersion = Environment.GetEnvironmentVariable("Instagram__ApiVersion") ?? "v18.0";
        var url = $"https://graph.facebook.com/{apiVersion}/{businessAccountId}/messages";

        var payload = new
        {
            recipient = new { id = recipientId },
            message = new { text = message },
            messaging_type = "RESPONSE"
        };

        var jsonContent = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        _logger.LogInformation("[Instagram] Enviando DM de IG a {Id} (POST {Url})", igScopedId, url);

        try
        {
            var response = await _httpClient.PostAsync(url, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Fail-safe: no lanzamos. Un id NO-phone (virtualizado XX. / Instagram / inbox
                // unificado) no es entregable por WhatsApp; que el envío falle aquí NO debe tumbar
                // el turno del cliente ni ensuciar el flush con un [ERR] Error en flush.
                _logger.LogError(
                    "[Instagram] No se respondio el DM a {Id} (destinatario no-telefonico / requiere permiso IG): HTTP {Status}. {Body}",
                    igScopedId, response.StatusCode, responseBody);
                return null;
            }

            // Igual que WhatsApp: Meta devuelve el fallo real (p. ej. code 3 "Application does not
            // have the capability" = app sin App Review / Advanced Access de mensajería de IG) como
            // HTTP 200 con "error". Se registra con marcador propio para el code 3 (bloqueo conocido).
            if (LogGraphErrorIfPresent(responseBody, "Instagram", igScopedId))
                return null;

            return ExtractMessageId(responseBody);
        }
        catch (Exception ex)
        {
            // Fail-safe ante excepciones de red/cancelación: se registra y se devuelve null en
            // lugar de propagar un [ERR] Error en flush que rompería el turno del cliente.
            _logger.LogWarning(ex,
                "[Instagram] No se pudo responder el DM a {Id} (fail-safe): {Message}",
                igScopedId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Extrae el id de mensaje de la respuesta de la Instagram Messaging API: {"message_id":"..."}.
    /// Devuelve null si el payload no lo trae (no debe lanzar).
    /// </summary>
    private static string? ExtractMessageId(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("message_id", out var id))
                return id.GetString();
        }
        catch (JsonException)
        {
            // Payload inesperado: no bloquea el envío.
        }
        return null;
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
    /// (Re-engagement: texto libre fuera de la ventana de 24h), 131009 (número incorrecto) o
    /// code 3 de Instagram (app sin la capacidad de mensajería) quedarían "en silencio": el
    /// asistente creería que respondió y el usuario no recibiría nada.
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

            if (channel == "Instagram" && code == 3)
            {
                _logger.LogError(
                    "[Instagram] DM NO entregado a {To}: la app no tiene la capacidad de mensajeria de Instagram " +
                    "(code 3). Requiere App Review / Advanced Access del permiso " +
                    "instagram_business_manage_messages en Meta. {Message}",
                    to, message);
            }
            else
            {
                _logger.LogError(
                    "[{Channel}] Mensaje NO entregado a {To}: code {Code} (sub {SubCode}){Detail}. {Message}",
                    channel, to, code, subCode, detail, message);
            }

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
            // El DESTINATARIO entregable debe ser un teléfono E.164 real. "from" normalmente
            // coincide con contacts[0].wa_id (E.164). Pero cuando Meta identifica al usuario
            // con un id de negocio ("from_user_id", p. ej. "CO.1053765850856674"), ese valor
            // NO es válido como destinatario de la respuesta: el envío falla con HTTP 400
            // #131009 "formato de número incorrecto". Orden de prioridad:
            //   1) "from" si es E.164 (caso normal),
            //   2) contacts[0].wa_id si es E.164 (número real aunque "from" venga como CO.x),
            //   3) "from_user_id" SOLO como identidad (la entrega dependerá de que sea válido).
            var from =
                (message.TryGetProperty("from", out var f) && IsE164Phone(f.GetString()) ? f.GetString() : null)
                ?? (IsE164Phone(contactWaId) ? contactWaId : null)
                ?? (message.TryGetProperty("from_user_id", out var fuid) ? fuid.GetString() : null)
                ?? "";
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

    /// <summary>¿Es un número de teléfono entregable (E.164, solo dígitos)? </summary>
    private static bool IsE164Phone(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (c < '0' || c > '9')
                return false;
        return s.Length >= 8 && s.Length <= 15;
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
