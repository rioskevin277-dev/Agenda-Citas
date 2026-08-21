using System.Collections.Concurrent;
using System.Threading.Channels;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Buffer de mensajes entrantes con Channel. Mismo patrón que AdamApi.
///
/// - Cada usuario tiene un buffer individual de 30s
/// - Deduplicación por ExternalMessageId
/// - Rate-limit: máx 5 mensajes cada 15s por usuario
/// - Timers de limpieza para buffers expirados
/// - Los mensajes se encolan y procesan en lotes por usuario
/// </summary>
public class MessageBufferService : BackgroundService
{
    private readonly Channel<IncomingMessageEvent> _channel;
    private readonly ConcurrentDictionary<string, UserMessageBuffer> _userBuffers;
    private readonly ConcurrentDictionary<string, List<DateTime>> _userRateLimits;
    private readonly MessageRetryService _retryService;
    private readonly ILogger<MessageBufferService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private CancellationToken _shutdownToken;

    // Tiempo de espera antes de procesar el lote de mensajes de un usuario. 8s (no 30s) para
    // acercar el tiempo de respuesta total del bot a ~15-20s: el cliente percibe una atención
    // ágil sin disparar una llamada de IA por cada mensaje suelto (las ráfagas cortas se agrupan).
    private const int FlushDelayMs = 8_000; // 8s por buffer de usuario
    private const int MaxMessagesPerWindow = 5;
    private const int RateLimitWindowMs = 15_000; // 15s
    private const int CleanupIntervalMs = 60_000; // limpiar buffers viejos cada 60s
    private const int RetryCheckIntervalMs = 15_000; // revisar reintentos vencidos cada 15s

    public MessageBufferService(
        ILogger<MessageBufferService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _retryService = new MessageRetryService();
        _channel = Channel.CreateBounded<IncomingMessageEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _userBuffers = new ConcurrentDictionary<string, UserMessageBuffer>();
        _userRateLimits = new ConcurrentDictionary<string, List<DateTime>>();
    }

    /// <summary>
    /// Encola un mensaje entrante en el Channel.
    /// </summary>
    public async Task EnqueueMessageAsync(
        string externalMessageId,
        string from,
        string? fromName,
        string content,
        string? mediaId,
        string? mediaType,
        Guid tenantId,
        CancellationToken ct = default)
    {
        var evt = new IncomingMessageEvent
        {
            ExternalMessageId = externalMessageId,
            From = from,
            FromName = fromName,
            Content = content,
            MediaId = mediaId,
            MediaType = mediaType,
            TenantId = tenantId,
            ReceivedAt = DateTime.UtcNow
        };

        await _channel.Writer.WriteAsync(evt, ct);
        _logger.LogDebug("[Buffer] Encolado mensaje {Id} de {From}", externalMessageId, from);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Buffer] MessageBufferService iniciado");

        // Guardar el token de apagado: los reintentos (fire-and-forget desde el timer) lo usan
        // para cancelar el procesamiento cuando el host se detiene.
        _shutdownToken = stoppingToken;

        // Timer de limpieza periódica
        using var cleanupTimer = new Timer(
            CleanupBuffers,
            null,
            CleanupIntervalMs,
            CleanupIntervalMs);

        // Timer de reintentos (backoff 30s/2m/8m de MessageRetryService): reintenta mensajes
        // que fallaron al procesarse para que un fallo transitorio no deje un cliente sin respuesta.
        using var retryTimer = new Timer(
            ProcessRetries,
            null,
            RetryCheckIntervalMs,
            RetryCheckIntervalMs);

        // Reader loop
        var readerTask = ProcessMessagesAsync(stoppingToken);

        try
        {
            await readerTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Buffer] MessageBufferService detenido");
        }
    }

    private async Task ProcessMessagesAsync(CancellationToken ct)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                // 1. Deduplicación
                if (IsDuplicate(evt.ExternalMessageId))
                {
                    _logger.LogWarning("[Buffer] Mensaje duplicado ignorado: {Id}", evt.ExternalMessageId);
                    continue;
                }

                // 2. Rate-limit por usuario
                if (IsRateLimited(evt.From))
                {
                    _logger.LogWarning("[Buffer] Rate-limit alcanzado para {From}", evt.From);
                    continue;
                }

                // 3. Acumular en buffer del usuario (o crear uno nuevo)
                var buffer = _userBuffers.GetOrAdd(evt.From, _ =>
                {
                    var b = new UserMessageBuffer(evt.From);
                    b.FlushTimer = new Timer(
                        async _ => await FlushUserBufferAsync(evt.From, ct),
                        null,
                        FlushDelayMs,
                        Timeout.Infinite);
                    return b;
                });

                buffer.Messages.Add(evt);
                buffer.LastActivityAt = DateTime.UtcNow;

                _logger.LogDebug("[Buffer] Buffer de {From}: {Count} mensajes acumulados",
                    evt.From, buffer.Messages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Buffer] Error procesando mensaje {Id}", evt.ExternalMessageId);
            }
        }
    }

    /// <summary>
    /// Procesa el buffer de un usuario: envía todos los mensajes acumulados al ChatOrchestrator.
    /// Si el procesamiento falla (error transitorio de BD, proveedor de IA o entrega), el mensaje
    /// NO se pierde: queda programado en MessageRetryService con backoff (30s/2m/8m).
    /// </summary>
    private async Task FlushUserBufferAsync(string from, CancellationToken ct)
    {
        try
        {
            if (!_userBuffers.TryRemove(from, out var buffer))
                return;

            buffer.FlushTimer?.Dispose();

            if (buffer.Messages.Count == 0)
                return;

            _logger.LogInformation("[Buffer] Flushing {Count} mensajes para {From}",
                buffer.Messages.Count, from);

            var lastMsg = buffer.Messages.Last();
            var tenantId = lastMsg.TenantId;
            var clientName = lastMsg.FromName;
            var receivedAt = lastMsg.ReceivedAt;

            // Transcripción de audios: si el cliente envió uno o más audios, se descargan,
            // se transcriben (Groq Whisper) y el texto reemplaza el placeholder "[audio]"
            // para que el flujo los trate como mensajes escritos.
            var ordered = buffer.Messages.OrderBy(m => m.ReceivedAt).ToList();
            if (ordered.Any(IsAudio))
            {
                using var scope = _scopeFactory.CreateScope();
                foreach (var m in ordered.Where(IsAudio))
                {
                    var transcript = await TranscribeAudioAsync(scope, tenantId, m.MediaId, m.MediaType, ct);
                    if (!string.IsNullOrWhiteSpace(transcript))
                        m.Content = transcript;
                }
            }

            // Concatenar mensajes del buffer en orden
            var fullContent = string.Join("\n", ordered.Select(m => m.Content));

            // Intento inicial (failedAttempts=0 ⇒ aun no ha fallado ninguno).
            await ProcessAndMaybeRetryAsync(from, fullContent, tenantId, clientName, receivedAt, failedAttempts: 0, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Buffer] Error en flush para {From}", from);
        }
    }

    /// <summary>
    /// Procesa un lote (flush inicial o reintento) y, si falla, programa un reintento con backoff.
    /// Comparte el canal del asesor humano: el mensaje del dueño SOLO se consume cuando hubo una
    /// acción real de asesor (Forwarded: respuesta reenviada al cliente; ChatClosed: FIN para
    /// cerrar); en cualquier otro caso cae al flujo normal de la IA y recibe respuesta.
    /// </summary>
    private async Task ProcessAndMaybeRetryAsync(
        string from,
        string fullContent,
        Guid tenantId,
        string? clientName,
        DateTime receivedAt,
        int failedAttempts,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var handoffService = scope.ServiceProvider.GetRequiredService<HandoffService>();
            var ownerResult = await handoffService.HandleOwnerReplyAsync(tenantId, from, fullContent, ct);
            if (ownerResult is HandoffService.OwnerReplyResult.Forwarded
                or HandoffService.OwnerReplyResult.ChatClosed)
            {
                _logger.LogInformation("[Buffer] Mensaje del asesor consumido para {Tenant}: {Result}",
                    tenantId, ownerResult);
                return;
            }

            var orchestrator = scope.ServiceProvider.GetRequiredService<ChatOrchestratorService>();

            // Límite de tiempo de respuesta: el cliente recibe una respuesta a más tardar 15 s
            // después del flush. Si la cadena de proveedores de IA o las herramientas tardan más,
            // el token se cancela y ProcessMessageAsync corta el turno sin bloquear al cliente.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await orchestrator.ProcessMessageAsync(from, fullContent, tenantId, responseCts.Token, clientName);
        }
        catch (Exception ex)
        {
            var failures = failedAttempts + 1;
            _logger.LogError(ex, "[Buffer] Error procesando mensaje de {From} (intento {Failures})", from, failures);

            if (_retryService.Schedule(from, fullContent, tenantId, clientName, receivedAt, DateTime.UtcNow, failures))
            {
                _logger.LogWarning("[Buffer] Reintento de {From} programado (fallo {Failures}/{Max})",
                    from, failures, _retryService.MaxRetries);
            }
            else
            {
                _logger.LogError("[Buffer] Se agotaron los reintentos ({Max}) para {From}: no se pudo atender el mensaje",
                    _retryService.MaxRetries, from);
            }
        }
    }

    /// <summary>
    /// Timer de reintentos: entrega los candidatos cuyo backoff venció y los ejecuta. Los que
    /// excedieron la ventana total de 30 min se descartan (con log) para no atender contenido obsoleto.
    /// </summary>
    private void ProcessRetries(object? state)
    {
        foreach (var item in _retryService.CollectDue(DateTime.UtcNow))
        {
            if (item.Expired)
            {
                _logger.LogError("[Buffer] Reintento de {From} expirado (>30 min): no se pudo atender", item.Key);
                continue;
            }

            _logger.LogInformation("[Buffer] Reintento #{Attempt}/{Max} de {From}",
                item.Attempt, _retryService.MaxRetries + 1, item.Key);
            _ = ProcessAndMaybeRetryAsync(item.Key, item.Content, item.TenantId, item.ClientName, item.ReceivedAt, item.Attempt - 1, _shutdownToken);
        }
    }

    private bool IsDuplicate(string externalMessageId)
    {
        // Evitar re-procesar mensajes ya vistos
        return _userBuffers.Values.Any(b =>
            b.Messages.Any(m => m.ExternalMessageId == externalMessageId));
    }

    private bool IsRateLimited(string from)
    {
        var now = DateTime.UtcNow;
        var timestamps = _userRateLimits.GetOrAdd(from, _ => new List<DateTime>());

        lock (timestamps)
        {
            // Limpiar timestamps fuera de la ventana
            timestamps.RemoveAll(t => (now - t).TotalMilliseconds >= RateLimitWindowMs);

            if (timestamps.Count >= MaxMessagesPerWindow)
                return true;

            timestamps.Add(now);
        }

        return false;
    }

    private void CleanupBuffers(object? state)
    {
        var threshold = DateTime.UtcNow.AddMilliseconds(-FlushDelayMs * 2);
        foreach (var kvp in _userBuffers)
        {
            if (kvp.Value.LastActivityAt < threshold)
            {
                _logger.LogInformation("[Buffer] Limpieza: buffer huérfano de {User}", kvp.Key);
                if (_userBuffers.TryRemove(kvp.Key, out var buffer))
                {
                    buffer.FlushTimer?.Dispose();
                }
            }
        }
    }

    /// <summary>¿Es un mensaje de audio (voz) descargable? Requiere MediaId (id del media en WhatsApp).</summary>
    private static bool IsAudio(IncomingMessageEvent m)
        => !string.IsNullOrWhiteSpace(m.MediaId)
           && (m.MediaType?.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Descarga y transcribe un audio de WhatsApp. Fail-safe: si algo falla devuelve null
    /// (el placeholder "[audio]" se conserva y el flujo responde igual sin romper el turno).
    /// </summary>
    private async Task<string?> TranscribeAudioAsync(
        IServiceScope scope,
        Guid tenantId,
        string? mediaId,
        string? mediaType,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
                return null;

            // La descarga exige el contexto de tenant (access token + phone id), igual que HandoffService.
            var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var tenant = await tenantRepo.GetByIdAsync(tenantId, ct);
            if (tenant == null)
            {
                _logger.LogInformation("[Buffer] Transcripción sin tenant {Tenant}", tenantId);
                return null;
            }
            tenantContext.SetTenant(
                tenantId,
                calendarProvider: tenant.CalendarProvider ?? "google",
                whatsAppAccessToken: Environment.GetEnvironmentVariable("WhatsApp__AccessToken")
                                   ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
                                   ?? "",
                phoneNumberId: tenant.WhatsAppPhoneNumberId ?? "");

            var messaging = scope.ServiceProvider.GetRequiredService<IMessagingProvider>();
            var stt = scope.ServiceProvider.GetRequiredService<ISpeechToTextProvider>();

            var audioBytes = await messaging.DownloadMediaAsync(mediaId, ct);
            if (audioBytes is not { Length: > 0 })
                return null;

            var text = await stt.TranscribeAsync(audioBytes, mediaType ?? "audio/ogg", ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("[Buffer] Audio sin transcripción utilizable para tenant {Tenant}", tenantId);
                return null;
            }
            _logger.LogInformation("[Buffer] Audio transcrito ({Chars} chars) para tenant {Tenant}", text.Length, tenantId);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Buffer] No se pudo transcribir el audio de {Tenant} (fallback a [audio])", tenantId);
            return null;
        }
    }

    private class IncomingMessageEvent
    {
        public string ExternalMessageId { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string? FromName { get; set; }
        public string Content { get; set; } = string.Empty;
        public string? MediaId { get; set; }
        public string? MediaType { get; set; }
        public Guid TenantId { get; set; }
        public DateTime ReceivedAt { get; set; }
    }

    private class UserMessageBuffer
    {
        public string UserId { get; }
        public List<IncomingMessageEvent> Messages { get; } = new();
        public Timer? FlushTimer { get; set; }
        public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

        public UserMessageBuffer(string userId)
        {
            UserId = userId;
        }
    }
}
