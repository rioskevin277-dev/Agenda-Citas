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
    private readonly ConversationStateService _conversationState;
    private readonly ILogger<WebhookController> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _syncLocks = new();

    public WebhookController(
        IMessagingProvider messagingProvider,
        MessageBufferService messageBuffer,
        ITenantRepository tenantRepo,
        ITenantContext tenantContext,
        IServiceScopeFactory scopeFactory,
        ConversationStateService conversationState,
        ILogger<WebhookController> logger)
    {
        _messagingProvider = messagingProvider;
        _messageBuffer = messageBuffer;
        _tenantRepo = tenantRepo;
        _tenantContext = tenantContext;
        _scopeFactory = scopeFactory;
        _conversationState = conversationState;
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

            await LogDeliveryStatusAsync(body);

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

                // System messages (user_changed_number / user_changed_user_id): Meta avisa que el
                // usuario cambió su BSUID. Se reasigna el client para no perder historial (no es un
                // turno de chat, así que no se encola al bot).
                if (msg.Type == "system" && msg.SystemType is "user_changed_number" or "user_changed_user_id")
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var crm = scope.ServiceProvider.GetRequiredService<ClientContextService>();
                        await crm.HandleUserChangedIdAsync(tenant.IdTenant, msg.UserId ?? "", msg.PreviousUserId ?? "");
                    }
                    _logger.LogInformation("[Webhook] System {SysType}: user {Prev}→{New} reasignado (tenant {Tenant})",
                        msg.SystemType, msg.PreviousUserId, msg.UserId, tenant.IdTenant);
                    continue;
                }

                // Contacto compartido (respuesta al botón request_contact_info): guardar el teléfono.
                if (msg.Type == "contacts" && !string.IsNullOrWhiteSpace(msg.Phone))
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var crm = scope.ServiceProvider.GetRequiredService<ClientContextService>();
                        await crm.StoreSharedPhoneAsync(tenant.IdTenant, msg.UserId ?? "", msg.Phone!);
                    }
                    _logger.LogInformation("[Webhook] Teléfono {Phone} compartido por user {UserId} (tenant {Tenant})",
                        msg.Phone, msg.UserId, tenant.IdTenant);
                    continue;
                }

                _logger.LogInformation("[Webhook] Mensaje de {From}: {Content} (tenant: {Tenant})",
                    msg.From, msg.Content, tenant.IdTenant);

                await _messageBuffer.EnqueueMessageAsync(
                    msg.ExternalMessageId,
                    msg.From,
                    msg.FromName,
                    msg.Content,
                    msg.MediaId,
                    msg.MediaType,
                    msg.TenantId,
                    msg.UserId,
                    msg.Phone,
                    msg.Username);
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
    /// Loguea el estado y código de error (nunca el contenido), y si el wamid corresponde a
    /// un recordatorio (reminder_logs), actualiza su estado de entrega (delivered/failed+retry).
    /// </summary>
    private async Task LogDeliveryStatusAsync(object body)
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

                        // El wamid de un status llega en el campo "id" del objeto statuses
                        // (id = "wamid.HBg..."). Algunas versiones lo envían también como "wamid"
                        // explícito; se lee "id" con fallback a "wamid".
                        var wamId = st.TryGetProperty("id", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString())
                            ? idProp.GetString()
                            : st.TryGetProperty("wamid", out var wamProp) ? wamProp.GetString() : null;
                        if (!string.IsNullOrEmpty(wamId) && stStatus is "delivered" or "read" or "failed")
                            await UpdateReminderLogDeliveryAsync(wamId, stStatus, detail);
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
    /// Correlaciona el wamid del callback de entrega con un recordatorio y actualiza su estado.
    /// No-op si el mensaje no es un recordatorio (no hay fila con ese wamid).
    /// </summary>
    private async Task UpdateReminderLogDeliveryAsync(string wamId, string status, string detail)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var reminderRepo = scope.ServiceProvider.GetRequiredService<IReminderLogRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var log = await reminderRepo.GetByWamIdAsync(wamId);
            if (log == null)
                return; // no es un recordatorio (mensaje de conversación normal)

            if (status is "delivered" or "read")
            {
                log.Estado = "delivered";
                log.Error = null;
            }
            else if (status == "failed")
            {
                log.Estado = "failed";
                log.Reintentos++;
                log.Error = detail;
            }
            await reminderRepo.UpdateAsync(log);
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Webhook] No se pudo actualizar estado de entrega del recordatorio {WamId}", wamId);
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
                // RF3: marcar tenant como sucio después de la sync de cambios externos para que el
                // orquestador fuerce un re-check de disponibilidad en el próximo turno (incluso si
                // no hay PendingBooking ni pedido de fecha/hora explícito).
                _conversationState.MarkTenantDirty(tenantId);
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
