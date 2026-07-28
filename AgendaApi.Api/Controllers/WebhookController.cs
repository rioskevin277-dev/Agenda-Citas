using System.Text.Json;
using System.Text.Json.Serialization;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgendaApi.Api.Controllers;

[ApiController]
[Route("api/webhook")]
public class WebhookController : ControllerBase
{
    private readonly IMessagingProvider _messagingProvider;
    private readonly MessageBufferService _messageBuffer;
    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantContext _tenantContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookController> _logger;

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
    /// Endpoint para notificaciones push de calendarios externos.
    /// Sirve tanto para Google Calendar como Microsoft Graph.
    /// - Google: headers X-Goog-Channel-ID, X-Goog-Resource-State, X-Goog-Channel-Token
    /// - MS Graph: query param ?validationToken=xxx (handshake) o JSON body con value[]
    /// </summary>
    [HttpPost("calendar")]
    public async Task<IActionResult> CalendarNotification()
    {
        // 1. MS Graph subscription validation handshake
        var validationToken = Request.Query["validationToken"].FirstOrDefault();
        if (!string.IsNullOrEmpty(validationToken))
        {
            _logger.LogInformation("[Webhook] MS Graph subscription validation");
            return Content(validationToken, "text/plain");
        }

        // 2. Google Calendar notification (header-based)
        var googChannelId = Request.Headers["X-Goog-Channel-ID"].FirstOrDefault();
        if (!string.IsNullOrEmpty(googChannelId))
        {
            var resourceState = Request.Headers["X-Goog-Resource-State"].FirstOrDefault();
            var channelToken = Request.Headers["X-Goog-Channel-Token"].FirstOrDefault();
            var resourceId = Request.Headers["X-Goog-Resource-ID"].FirstOrDefault();

            _logger.LogInformation("[Webhook] Google calendar notification: channel={ChannelId}, state={State}, resource={Resource}",
                googChannelId, resourceState, resourceId);

            // "sync" = initial channel confirmation, skip processing
            if (resourceState is "exists" or "updated")
            {
                Guid? tenantId = null;
                if (Guid.TryParse(channelToken, out var tid))
                    tenantId = tid;
                else
                    // Fallback: look up by channelId in CalendarConnection
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

        // 3. MS Graph notification (JSON body)
        if (Request.ContentLength.GetValueOrDefault() > 0)
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();

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
                _logger.LogWarning(ex, "[Webhook] Body no es notificacion MS Graph valida: {Body}", body[..Math.Min(200, body.Length)]);
            }
        }

        return Ok();
    }

    // ─── Private: background sync + channel resolution ────────────────

    private void TriggerCalendarSync(Guid tenantId)
    {
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
