using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers;

[ApiController]
[Route("api/v1/webhook")]
[AllowAnonymous]
public class WebhookController : ControllerBase
{
    private readonly IMessagingProvider _messagingProvider;
    private readonly MessageBufferService _messageBuffer;
    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantContext _tenantContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookController> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _syncLocks = new();

    public WebhookController(
        IMessagingProvider messagingProvider,
        MessageBufferService messageBuffer,
        ITenantRepository tenantRepo,
        ITenantContext tenantContext,
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookController> logger)
    {
        _messagingProvider = messagingProvider;
        _messageBuffer = messageBuffer;
        _tenantRepo = tenantRepo;
        _tenantContext = tenantContext;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Verify(
        [FromQuery(Name = "hub.mode")] string mode,
        [FromQuery(Name = "hub.verify_token")] string token,
        [FromQuery(Name = "hub.challenge")] string challenge)
    {
        var result = await _messagingProvider.VerifyWebhookAsync(mode, token, challenge);
        if (result != null)
            return Content(result, "text/plain");

        return Forbid();
    }

    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] object body)
    {
        try
        {
            _logger.LogInformation("[Webhook] Recibido payload de WhatsApp");

            LogDeliveryStatus(body);

            var messages = await _messagingProvider.ParseWebhookPayloadAsync(body);

            foreach (var msg in messages)
            {
                // Resolver tenant por phone_number_id desde el payload de Meta
                var tenant = await _tenantRepo.GetByPhoneNumberIdAsync(msg.PhoneNumberId);
                if (tenant == null || !tenant.Activo)
                {
                    _logger.LogWarning("[Webhook] Tenant no encontrado para phoneNumberId: {PhoneId}", msg.PhoneNumberId);
                    continue;
                }

                msg.TenantId = tenant.IdTenant;

                _logger.LogInformation("[Webhook] Mensaje de {From}: {Content} (tenant: {Tenant})",
                    msg.From, msg.Content, tenant.IdTenant);

                await _messageBuffer.EnqueueMessageAsync(
                    msg.ExternalMessageId,
                    msg.From,
                    msg.Content,
                    msg.MediaType,
                    msg.MediaUrl,
                    msg.TenantId);
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] Error procesando payload");
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Registra los callbacks de estado de entrega de Meta (sent/delivered/failed).
    /// Solo loguea el estado y código de error, nunca el contenido del mensaje.
    /// </summary>
    private void LogDeliveryStatus(object body)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
            var root = doc.RootElement;
            if (!root.TryGetProperty("entry", out var entries))
                return;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes))
                    continue;
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value))
                        continue;
                    if (!value.TryGetProperty("statuses", out var statuses))
                        continue;

                    foreach (var st in statuses.EnumerateArray())
                    {
                        var stStatus = st.TryGetProperty("status", out var s) ? s.GetString() : "?";
                        var recipient = st.TryGetProperty("recipient_id", out var rId) ? rId.GetString() : "?";
                        string detail = "OK";
                        if (st.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0)
                        {
                            var e0 = errs[0];
                            var code = e0.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                            var title = e0.TryGetProperty("title", out var t) ? t.GetString() : "";
                            detail = $"{code} {title}";
                        }
                        _logger.LogInformation("[Webhook] Entrega Msg: status={Status} recip={Recipient} {Detail}",
                            stStatus, recipient, detail);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Webhook] No fue un payload de estado");
        }
    }

    /// <summary>
    /// Endpoint para notificaciones push de calendarios externos.
    /// Sirve tanto para Google Calendar como Microsoft Graph.
    /// </summary>
    [HttpPost("calendar")]
    public async Task<IActionResult> CalendarNotification()
    {
        // 1. MS Graph subscription validation handshake (query param)
        if (TryHandleMsGraphValidation(out var validationResult))
            return validationResult!;

        // 2. Google Calendar notification (header-based)
        var googleResult = await TryHandleGoogleNotificationAsync();
        if (googleResult != null)
            return googleResult;

        // 3. MS Graph notification (JSON body)
        if (Request.ContentLength.GetValueOrDefault() > 0)
            return await HandleMsGraphNotificationBodyAsync();

        _logger.LogWarning("[Webhook] Notificación de calendario no reconocida (ni Google ni MS Graph)");
        return Ok();
    }

    /// <summary>
    /// MS Graph envía un ?validationToken=xxx en el handshake de subscription.
    /// </summary>
    private bool TryHandleMsGraphValidation(out IActionResult? result)
    {
        var validationToken = Request.Query["validationToken"].FirstOrDefault();
        if (!string.IsNullOrEmpty(validationToken))
        {
            result = Content(validationToken, "text/plain");
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Google Calendar envía notificaciones vía headers X-Goog-*.
    /// Retorna un IActionResult si se identificó como notificación Google, null si no.
    /// </summary>
    private async Task<IActionResult?> TryHandleGoogleNotificationAsync()
    {
        var googChannelId = Request.Headers["X-Goog-Channel-ID"].FirstOrDefault();
        if (string.IsNullOrEmpty(googChannelId))
            return null;

        var resourceState = Request.Headers["X-Goog-Resource-State"].FirstOrDefault();
        var channelToken = Request.Headers["X-Goog-Channel-Token"].FirstOrDefault();
        var resourceId = Request.Headers["X-Goog-Resource-ID"].FirstOrDefault();

        _logger.LogInformation("[Webhook] Google calendar notification: channel={ChannelId}, state={State}, resource={Resource}",
            googChannelId, resourceState, resourceId);

        if (resourceState is "exists" or "updated")
        {
            Guid? tenantId = null;
            if (Guid.TryParse(channelToken, out var tid))
                tenantId = tid;
            else
                tenantId = await ResolveTenantByChannelIdAsync(googChannelId);

            if (tenantId.HasValue)
            {
                _logger.LogInformation("[Webhook] Programando sync por notificacion Google para tenant {TenantId}", tenantId);
                TriggerCalendarSync(tenantId.Value);
            }
            else
            {
                _logger.LogWarning("[Webhook] No se pudo resolver tenant para Google channel {ChannelId}", googChannelId);
            }
        }

        return Ok();
    }

    /// <summary>
    /// MS Graph envía notificaciones como JSON en el body con una lista de cambios.
    /// </summary>
    private async Task<IActionResult> HandleMsGraphNotificationBodyAsync()
    {
        Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(Request.Body, leaveOpen: false))
        {
            body = await reader.ReadToEndAsync();
        }

        try
        {
            var msNotification = JsonSerializer.Deserialize<MsGraphNotificationPayload>(body);
            if (msNotification?.Value?.Count > 0)
            {
                foreach (var item in msNotification.Value)
                {
                    Guid? tenantId = null;
                    if (!string.IsNullOrEmpty(item.ClientState) && Guid.TryParse(item.ClientState, out var cid))
                        tenantId = cid;
                    else
                        tenantId = await ResolveTenantByChannelIdAsync(item.SubscriptionId);

                    if (tenantId.HasValue)
                    {
                        _logger.LogInformation("[Webhook] MS Graph notification for tenant {TenantId}, resource={Resource}",
                            tenantId, item.Resource);
                        TriggerCalendarSync(tenantId.Value);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Webhook] Body no es notificacion MS Graph valida: {Body}",
                body[..Math.Min(200, body.Length)]);
        }

        return Ok();
    }

    // ─── Private: background sync + channel resolution ────────────────

    private void TriggerCalendarSync(Guid tenantId)
    {
        var semaphore = _syncLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));

        // Intentar adquirir el semáforo sin bloquearte (Try = 0 timeout)
        if (!semaphore.Wait(0))
        {
            _logger.LogInformation("[Webhook] Sync ya en progreso para {TenantId}, saltando", tenantId);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var useCase = scope.ServiceProvider.GetRequiredService<SyncExternalChangesUseCase>();
                var count = await useCase.ExecuteAsync(tenantId);
                _logger.LogInformation("[Webhook] Sync completado para {TenantId}: {Count} cambios", tenantId, count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Webhook] Error en sync para {TenantId}", tenantId);
            }
            finally
            {
                semaphore.Release();
            }
        });
    }

    private async Task<Guid?> ResolveTenantByChannelIdAsync(string channelId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var connectionRepo = scope.ServiceProvider.GetRequiredService<ICalendarConnectionRepository>();
            var connection = await connectionRepo.GetByChannelIdAsync(channelId);
            return connection?.IdTenant;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Webhook] Error resolviendo tenant por channel {ChannelId}", channelId);
            return null;
        }
    }
}

// ─── DTOs para notificaciones entrantes ───────────────────────────────

/// <summary>
/// Payload de notificación push de Microsoft Graph.
/// </summary>
public class MsGraphNotificationPayload
{
    [JsonPropertyName("value")]
    public List<MsGraphNotificationItem>? Value { get; set; }
}

public class MsGraphNotificationItem
{
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("clientState")]
    public string? ClientState { get; set; }

    [JsonPropertyName("resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("lifecycleEvent")]
    public string? LifecycleEvent { get; set; }
}
